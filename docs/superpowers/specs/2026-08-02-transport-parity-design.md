# Transport Abstraction and Riptide Parity — Design

Covers steps 1–3 of [THREAD.md](../../THREAD.md): the transport seam, an in-process implementation,
and the mechanism that keeps it honest. Singleplayer's `ServerHost` is still stepped from the client's
`Update()` when this is done, so any failure it produces is provably a transport bug and never a
threading bug. Giving the server its own thread (THREAD.md steps 4–5) is a separate spec written after
this one is trusted.

## Problem

In-process singleplayer shares Riptide's process-wide `Message`/`PendingMessage` pools and its single
static `ByteBuffer` between two peers on one thread. THREAD.md decompiles the unguarded
check-then-act in `Release` and the unlocked `RetrieveFromPool`. That is what forces `ServerHost.Step`
onto the client's thread today.

The obvious fix — swap the transport for an in-process queue — introduces a subtler problem, and this
spec exists for that problem rather than for the queue. A queue that hands objects between peers
delivers **in order**, **unserialised**, and **unbounded**. Singleplayer would then pass while the
real network path fails, silently and later. Today singleplayer at least exercises real
serialisation, the `ComponentBundle` if-chain, the 1225-byte datagram limit, and reliable-but-unordered
delivery. None of that may be lost.

## Requirements

1. Singleplayer must not share mutable static state between the two peers.
2. A message that fails to round-trip must fail in singleplayer, not only against a real server.
3. Code that assumes arrival order must fail in singleplayer, not only against a real server.
4. One binary must both host singleplayer and `session join` a remote server, so implementation
   selection is at runtime, not compile time.
5. The in-process path must not cost more per tick than the loopback socket path it replaces. The
   Riptide path accepts exactly one additional buffer copy and a length prefix, which is noise against
   a UDP send.
6. The terrain stream (`ChunkTcpClient`/`ChunkTcpServer`) is out of scope and unchanged.

## Verified behavior of the thing we are matching

Decompiled from `Riptide.Connection` in RiptideNetworking 2.2.1. These are facts, not assumptions, and
the delivery model below is derived from them:

- `ReliableSequencer.ShouldHandle` filters duplicates and passes out-of-order messages through.
  Reliable is therefore **delivered exactly once, in arbitrary order**.
- Deduplication is **windowed** — a `Bitfield` of received sequence ids, with a warning logged once the
  sequence gap exceeds 64.
- **Unreliable messages are never sequenced.** The send path handles `MessageSendMode.Unreliable`
  without assigning a sequence id, and `Connection.ShouldHandle` delegates only to the reliable
  sequencer. No sequence id means no deduplication and no ordering, so a duplicate reaches the handler.

Both channels are in live use. Unreliable: `PlayerInput`, `PlayerPosition`, `PlayerFired`,
`HitConfirm`, and `ObjectState` (variable mode). Reliable: `Welcome`, `TerrainEdit`, the spawn/despawn
pairs, `CommandRequest`/`CommandResult`, `PlayerFire`, `PlayerDig`, `PlayerReload`, `PlayerInteract`.

## Design

### 1. The parity target is domination, not equality

Exact behavioral equality would require reproducing Riptide's reliability windows, acking, and
fragmentation — a UDP reimplementation, which is the thing THREAD.md exists to avoid. The property we
actually need is directional:

> **Green in singleplayer implies green in multiplayer.**

That is satisfied by the fake being *strictly more hostile* than the real transport, not identical to
it. Divergences that make the in-process path stricter are features. Divergences that make it more
permissive are bugs.

The property, stated so it can be checked rather than hoped for:

> For every channel, the set of delivery orderings the in-process transport can produce is a
> **superset** of Riptide's, and a **subset** of what is physically realizable.

The second half is load-bearing and is the one place "more hostile is better" is wrong. A fake that
reorders the reliable channel further than Riptide's own deduplication window tolerates manufactures
scenarios no real network produces, and debugging those is pure waste. Hostility is capped at the real
ceiling.

### 2. One serializer, two carriers

`Common/Net/` gains our own `Message`, `INetServer`, and `INetClient`. **`Common.Net.Message` is the
only code in the project that turns a value into bytes.** Both transports receive an already-serialized
`(ushort id, byte[] payload, MessageSendMode mode)` and differ only in how they carry it: the Riptide
implementation stuffs the payload into a `Riptide.Message` as a byte blob, the in-process
implementation queues it.

This is the core move — it minimizes what *can* diverge instead of testing that it hasn't:

- `ComponentBundle`'s if-chain order stops being a parity risk. There is one implementation, exercised
  identically on both paths, along with all 23 `Message.Create` sites and every `Messages/*.cs` type.
- The 1225-byte limit is enforced in one place, so an oversized message fails the same way in
  singleplayer as in production.
- Conformance can assert **bytes**, which is stronger and cheaper than asserting behavior.

The surface to reproduce is nine Add/Get pairs — `Bool`, `Byte`, `Bytes`, `Float`, `Int`,
`Serializable`, `String`, `UInt`, `UShort` — plus `Create` and `Release`. `AddVector3`/`GetVector3` in
`Common/NetworkProtocol.cs` are already our own extension methods composed from those primitives and
need no interface changes; they are the precedent for how composites are built. Both ends already pass
`useMessageHandlers: false` and switch manually on message id, so none of Riptide's attribute dispatch
is in play.

Consequence worth stating: under this design we stop using Riptide's `Add*`/`Get*` entirely and Riptide
degrades to a datagram pipe with connection management.

### 3. Delivery model

| Channel | Riptide (verified) | In-process transport must |
| --- | --- | --- |
| Reliable | Eventual delivery, duplicates filtered, arbitrary order | Deliver exactly once; reorder within a bounded window; never drop |
| Unreliable | No sequence id: may drop, reorder, and duplicate | Drop, reorder, and duplicate |

The hostility knobs live in `Common/NetworkProtocol.cs` beside the existing tuning constants, since
that file is already the tuning surface. Starting values, chosen to be aggressive but inside what a
plausible connection produces:

| Knob | Value | Rationale |
| --- | --- | --- |
| Reorder window | 64 messages | Riptide's own deduplication warning threshold; beyond it we manufacture impossible scenarios |
| Unreliable drop rate | 2% | High for a LAN, ordinary for a bad connection |
| Unreliable duplicate rate | 1% | Rare in practice, and rare is exactly why it stays unfound without injection |
| Reliable drop rate | 0 | Riptide guarantees delivery; dropping would be more permissive than real, not stricter |

Duplication is the row THREAD.md did not anticipate and the most likely to find a real bug.
`PlayerInput` is unreliable, so a duplicate double-applies movement if the server's input queue applies
each arrival; a duplicated `HitConfirm` is a double hitmarker and a duplicated `PlayerFired` a double
muzzle flash. No existing local test covers any of this, because a loopback socket does not duplicate.

**Hostility is always on in singleplayer.** Gating it behind a test-only flag would mean the
configuration played daily proves nothing about multiplayer. The cost is accepted: a latent order,
loss, or duplication assumption becomes a visible singleplayer glitch during unrelated work, which is
the mechanism working rather than failing.

### 4. Latency simulation moves into the transport

`SimulatedLatencySeconds` / `SimulatedJitterSeconds` are currently applied in
`NetworkManager.Dispatch` — client-side and inbound only — so server-side handling of late client
input has never been exercised locally. Since the in-process transport owns both directions, it applies
delay symmetrically.

This matters because the in-process path is *faster* than the socket it replaces, and zero-latency
delivery under-exercises everything that exists because latency does: the 3-tick
`InterpolationDelayTicks` buffer, client prediction and reconciliation, and the one-second
`MaxRewindTicks` acceptance window. Speed is a fidelity liability here, not only a win.

### 5. Conformance suite

Four layers, cheapest and most complete first.

**Layer 1 — byte identity (`Common.Tests`, no Stride).** Every wire type round-trips: serialize,
deserialize, assert equality, and assert the payload bytes are identical regardless of carrier. The
suite **enumerates `IMessageSerializable` implementations by reflection** rather than listing them, so
a wire type added through the RECIPES.md checklist gets coverage without anyone remembering to write a
test. The completeness of the enumeration buys the coverage; diligence does not have to.

**Layer 2 — transport conformance (one test body, both implementations).** An xUnit `[Theory]`
parameterized over both transports asserting the shared contract: reliable arrives exactly once,
everything eventually arrives, oversized payloads are rejected, connect/disconnect ordering. Anything
green here is green for both by construction. The Riptide leg uses loopback sockets and carries
`Category=Integration`; the in-process leg stays in the fast suite.

**Layer 3 — always-on singleplayer fuzz.** Seeded per session. This is the layer that finds the
duplicate-`PlayerInput` class, because it runs during ordinary development rather than when someone
thinks to write the scenario.

**Layer 4 — release path, unchanged.** `session playtest-networked` and real multiplayer.

### 6. Failure surfacing

- **Oversized payloads throw at serialization time**, in one place, naming the message id and byte
  count. The existing failure mode is silent truncation surfacing as "N unread bits" on the far end,
  recorded in CLAUDE.md as having cost real debugging time. Converting that into an immediate throw at
  the authoring site is most of the value of owning serialization.
- **The session seed is printed at startup** and readable from the developer terminal.
- **A ring buffer of the last 256 delivery decisions** (id, mode, verdict) is dumpable from the
  developer terminal.

The ring buffer is required, not optional, because of an honest limit: a seed alone does not reproduce
a live session. The seed makes the delivery *policy* deterministic; it does not make the traffic
deterministic, since the message sequence depends on frame-to-frame input timing. Seed plus decision
log is what actually makes a glitch diagnosable. Building only the seed would mean discovering this
after the first glitch we cannot reproduce.

## Verification

- Full solution builds; ordinary fast suite green.
- Layer 1 covers every `IMessageSerializable` implementation, verified by asserting the reflected count
  against the known type count so an empty enumeration cannot pass silently.
- Layer 2 passes identically for both transports.
- A deliberately oversized message throws at the authoring site in both transports.
- A deliberately order-dependent handler is added temporarily, observed to fail under the in-process
  transport, and removed. This proves the fuzz can actually catch what it exists to catch; without it
  the suite's green is unfalsifiable.
- Singleplayer runs a conquest session to completion with hostility on.
- `session playtest-networked` still completes the save, bake, stream, and remesh path.

## Deliberately not in this spec

- **Threading.** `ServerHost` is still stepped from `Update()`. THREAD.md steps 4–5, including
  re-reading `MaxCatchUpTicks` and revising the CLAUDE.md "singleplayer is the binding perf case"
  note, are the next spec.
- **Terrain transport.** `ChunkTcpClient`/`ChunkTcpServer` is already ours and already separate.
- **Matching Riptide's wire encoding.** Both ends are ours; the payload format only needs to
  round-trip faithfully.
- **Removing Riptide.** It remains the real-network transport. This spec only narrows what we depend
  on it for.
