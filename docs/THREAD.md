# Running the server on its own thread

> **Implemented, 2026-08-02.** Steps 1–4 are done; step 5 (re-measure) is what remains.
>
> Steps 1–3 are specified in
> [2026-08-02-transport-parity-design.md](superpowers/specs/2026-08-02-transport-parity-design.md),
> which also corrects one thing this document assumes: Riptide's **unreliable** channel is never
> sequenced, so it can *duplicate* as well as drop and reorder.
>
> Step 4 landed as `ServerHost.StartOnOwnThread`. The measurement that motivated it: one frame spent
> **214 ms of 216 ms** inside `ServerHost.Step()`, running up to `MaxCatchUpTicks` ticks inline, while
> the client's own work totalled 2 ms — 5 fps decided entirely by thread ownership.
>
> Two notes this document predicted and which held. `MaxCatchUpTicks` did need re-reading: its
> rationale was about a slow *caller*, and it now bounds the server against its own over-budget tick
> instead. And singleplayer has stopped being the combined-budget stress case, so the CLAUDE.md
> performance-targets section was revised rather than left to quietly become false.
>
> **Step 5, measured on the same conquest scenario:**
>
> | | Before | After |
> | --- | --- | --- |
> | Frame rate | 4–7 fps | 53–117 fps |
> | `server` slot in the frame log | 214 ms | 0.00 ms |
> | Client session total | 216 ms | 0.22–1.46 ms |
> | Worst frame | 312 ms | 4.5–16 ms |
> | Server tick rate | 16–20 TPS | 30–31 TPS |
> | Server tick total | 50–60 ms | 12–25 ms |
>
> The `server` collapse to zero is what this document predicted. The tick getting **cheaper** is not,
> and it is the more interesting result: `actors` fell from 50 ms to 10–21 ms and `follow` from 37 ms
> to 10–20 ms on identical code. The work did not get cheaper, the contention did. The main thread had
> been running four catch-up ticks inline while eight path workers and the renderer competed for the
> same six cores and the same DDR4 the iGPU uses. Pacing the server removed the thrash, so the 30 TPS
> target is now met rather than merely unblocked.
>
> Read that as a caution as much as a win: a measurement taken while the machine is thrashing
> attributes cost to whatever happens to be holding the thread, not to whatever is actually expensive.
> The pre-threading numbers overstated `follow` and `cover` by roughly 2x for that reason.
>
> **What remains.** Navigation is now the binding constraint, and it is queue-bound rather than
> frame-bound: `queue p50/p95` climbed to 362/488 ms per path against BARITONE.md's 500 ms ceiling,
> with `0 full / 39 partial` routes and `shared routes 0`. Every NPC gets a partial answer, re-requests,
> and keeps eight workers saturated at ~34 ms each per tick. `search p50/p95` of 153/342 ms also sits
> well past the 25 ms prefix and 100 ms failure budgets those searches are supposed to respect — a
> wall-clock budget checked every 64 expansions does not hold when the thread is descheduled.

**Goal:** in singleplayer, stop the server tick from blocking the client frame.

**Multiplayer is the canonical implementation.** A dedicated server and a remote client keep using
Riptide over UDP exactly as they do today, and nothing in this document changes that path. In-process
singleplayer is a *trick*: it borrows the same client and server code and runs them in one process,
and this is about making that trick thread-safe.

---

## Why a thread is not currently possible

`ServerHost` already has both shapes — `Run()` for the standalone exe, `Step()` for singleplayer —
and `RuntimeClientSession` touches it in exactly four places (construct, `Start`, `Step`, `Dispose`).
Our own client/server split is genuinely clean. The blocker is entirely third-party.

Decompiled from the shipped RiptideNetworking 2.2.1 assembly:

```csharp
private static readonly List<Message> pool;   // process-wide, no lock, not [ThreadStatic]
internal static byte[] ByteBuffer;            // one shared serialisation scratch buffer

private static Message RetrieveFromPool() {
    if (pool.Count > 0) { result = pool[0]; pool.RemoveAt(0); }        // unguarded
}
public void Release() {
    if (pool.Count < pool.Capacity && !pool.Contains(this)) pool.Add(this);  // check-then-act
}
```

`Release` is the worse half: two threads can both pass `Contains` and add the same `Message` twice,
after which two threads are later handed the **same object**. `ByteBuffer` being a single static
means concurrent serialisation stomps bytes. In one process our client and server share all of it.

This is invisible in real multiplayer, where each process has exactly one peer — which is precisely
why it only bites the singleplayer trick.

Symptoms when it does bite: truncated reads on the far end ("N unread bits") and
`ArgumentOutOfRangeException` inside `RetrieveFromPool`. Both intermittent, neither a clean crash.

---

## The approach: swap the transport, not the library

Define our own `Message` / `Server` / `Client` types with the same shape, and choose the
implementation **at runtime** where the session is constructed:

- **Riptide-backed** — real UDP, used by dedicated servers and by clients joining a remote host.
  Unchanged behaviour.
- **In-process** — a thread-safe queue between two peers in one process, used by singleplayer.

Runtime rather than compile-time, because one binary has to both host singleplayer and
`session join` a remote server. A compile-time switch would mean two builds and a shipped game that
can only do one.

### Why this is smaller than it sounds

For in-process singleplayer there is **no transport to write**. No UDP, no reliability, no
retransmission, no congestion control, no fragmentation, no connection negotiation. Two peers in one
address space need a queue. That is strictly less than Riptide does, not a reimplementation of it.

### Measured surface

Everything the codebase actually uses, across 29 files and 139 `Message` call sites:

| Type | Members used |
| --- | --- |
| `Message` | `Create`, `Release`, and 9 Add/Get pairs: UShort, UInt, Float, Byte, Int, String, Bool, Bytes, Serializable |
| `Server` | `Start`, `Stop`, `Update`, `Send`, `MessageReceived`, `ClientConnected`, `ClientDisconnected` |
| `Client` | `Connect`, `Disconnect`, `Update`, `Send`, `Connected`, `MessageReceived`, `Id` |
| Supporting | `MessageSendMode`, `MessageReceivedEventArgs`, `ServerConnectedEventArgs`, `ServerDisconnectedEventArgs`, `RiptideLogger`, `IMessageSerializable` |

About 25 members. **And we already opted out of the hard part:** both ends pass
`useMessageHandlers: false`, so we do not use Riptide's attribute/reflection dispatch at all — just
`MessageReceived` and a manual `switch` on message id in `NetworkManager` and `GameServer`. There is
no reflection machinery to reproduce.

The terrain stream is unaffected: `ChunkTcpClient`/`ChunkTcpServer` is already ours and already a
separate TCP connection.

---

## The load-bearing design decision

This is what determines whether the change is a win or a slow-acting trap.

Today `--singleplayer` connects over a **real socket** at 127.0.0.1:7777 with **real serialisation**.
So singleplayer currently exercises:

- every message's serialise/deserialise round trip;
- the `ComponentBundle` if-chain order, which `CLAUDE.md` says **is** the protocol;
- the 1225-byte datagram limit;
- **reliable-but-unordered** delivery — "`MessageSendMode.Reliable` guarantees delivery but NOT
  order. Every message must be independently applicable."

A shim that hands objects across a queue delivers **in order**, **unserialised**, **unbounded**. A
message that fails to round-trip, or code that quietly assumes arrival order, would then work
perfectly in singleplayer and fail only against a real server — silently, later, and in the hardest
place to debug.

**So the in-process transport must not shortcut the protocol.** It must:

1. **Serialise to a byte buffer and read back.** It does not need to match Riptide's wire format —
   both ends are ours — it only needs to round-trip faithfully.
2. **Enforce the datagram size limit**, so an oversized message fails here rather than in
   production.
3. **Deliberately reorder within the reliable channel**, seeded and reproducible.

Point 3 turns the change from a fidelity risk into a **fidelity improvement**: a localhost socket
essentially never reorders, so today's singleplayer cannot catch an order assumption at all. A shim
that reorders on purpose catches them on the developer's machine. That is the strongest argument for
this over forking Riptide, and it should not be dropped for convenience.

---

## Why not fork Riptide

It was the alternative and it is smaller in raw diff — make the two pools and `ByteBuffer`
`[ThreadStatic]`, three fields. But it means vendoring a networking library to change three lines,
owning its future bugfixes by hand, and it buys none of the conformance-harness value above.

---

## Scope

**In:** the transport abstraction, an in-process implementation, runtime selection, and pointing
singleplayer's `ServerHost` at `Run()` on its own thread instead of `Step()` from `Update()`.

**Out, and stated because they are easy to assume:**

- **This does not reduce the AI tick cost.** It stops the client *blocking* on it. A dedicated
  server pays exactly the same per-tick cost today and will after. The 30 TPS budget is a separate
  problem governed by the [performance targets](../CLAUDE.md#performance-targets).
- **It removes singleplayer's value as a combined-budget stress case.** `CLAUDE.md` currently calls
  singleplayer the binding case *because* the server tick shares the client's 16.6 ms frame. After
  this, it no longer does, and that note needs revising rather than silently becoming false.
- `MaxCatchUpTicks` and the accumulator in `ServerHost.Step` exist to stop a slow *caller* becoming
  a burst amplifier. On its own thread the server paces itself, so that logic needs re-reading in
  its new context rather than being carried over unexamined.

## Steps

1. Introduce the types and the interface, with the Riptide-backed implementation only. Mechanical
   `using` change across 29 files; behaviour identical; full suite green. This step is the risky one
   for review, and it is deliberately behaviour-free.
2. Add the in-process implementation with serialisation round-trip and the size limit. Still stepped
   from `Update()`, so any failure is a transport bug and not a threading bug.
3. Add deterministic reordering, and fix whatever it exposes. Expect it to expose something.
4. Give singleplayer's `ServerHost` its own thread. Re-read the catch-up logic.
5. Re-measure. The `frame:` line should show `server` collapse toward zero while `ServerTick`'s own
   `total` stays where it is.
