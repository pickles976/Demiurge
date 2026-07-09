# Networking: Patterns & Roadmap

Companion to [NETWORKING.md](NETWORKING.md) (which has the architecture diagrams).
This doc explains *why* the code is shaped the way it is, and what to build next.

Audience note: this assumes you're a competent engineer who is newer to C# —
each pattern calls out the C# feature that implements it, and there's a short
[C# idioms glossary](#c-idioms-used-in-this-codebase) at the bottom.

---

## Design patterns in this codebase

### Layered architecture (one-way dependencies)

The client is three layers; the server is two. Dependencies point strictly
downward — the view knows the sim, the sim knows the netcode, never the reverse:

| Layer | Client | Server | Allowed dependencies |
|---|---|---|---|
| View | `PlayerViewFactory`, `PlayerViewScript`, `PlayerVisualScript`, `LocalPlayerController` | — | Stride, sim layer |
| Simulation | `PlayerRegistry`, `LocalPlayer`, `RemotePlayer` | `GameWorld`, `ServerPlayer` | `Common`, netcode events |
| Netcode | `NetworkManager` | `GameServer` | Riptide, `Common` |

`Common` (the `DemiurgeCommon` project) sits under everything: message structs,
`PlayerStateFlags`, `PlayerMovement`. It references Riptide but **never Stride** —
and because it's a separate `.csproj`, the compiler enforces that.

This is the game-dev flavor of hexagonal/ports-and-adapters architecture: the
simulation is the core; Riptide and Stride are both replaceable adapters.

### Observer pattern (C# `event`)

Each layer boundary is crossed by events, not direct calls upward:

- `NetworkManager.PlayerSpawned` / `PlayerPositionReceived` — netcode → sim
- `PlayerRegistry.PlayerJoined` — sim → view

The publisher has no idea who's listening. That's why adding the HUD's client-id
display, or later a "Player joined" toast, requires zero netcode changes.
C# builds observer into the language: `public event Action<PlayerSpawnData>? PlayerSpawned;`
declares it, subscribers attach with `+=`, and the owner fires it with
`PlayerSpawned?.Invoke(data)` (the `?.` handles the zero-subscribers case, which
is `null`).

### Composition root

`Client/Program.cs` and `Server/Program.cs` are the *only* places that construct
and wire the long-lived objects (`NetworkManager` → `PlayerRegistry` →
`PlayerViewFactory`; `GameServer` → `GameWorld`). Everything else receives its
dependencies through constructors or properties. This replaced the old static
singletons (`Program.Server`, `PlayerHandle.List`, static `Game` references),
which made every class secretly depend on every other class.

### Registry pattern

`PlayerRegistry` (client) and `GameWorld` (server) each own an id → object
dictionary *and* the lifecycle rules around it (spawn, despawn, catch-up for
late joiners). Nobody else touches the collection.

### Factory pattern

`PlayerViewFactory` isolates the messy construction of a player entity (GLTF
model, five animation clips, two scripts) in one place, triggered by the
`PlayerJoined` event.

### Typed messages (DTOs)

Every wire message is a struct in `Common/Messages/` implementing Riptide's
`IMessageSerializable`. The field order — which *is* the wire protocol — exists
in exactly one place per message. Before this, the server's `AddUShort/AddVector3`
and the client's `GetUShort/GetVector3` had to match by convention across files,
and a mismatch failed silently.

### Humble object

`PlayerVisualScript` (animation blending) makes no decisions: it renders
whatever `State` it is handed. Input reading was removed from it — that's how we
fixed the bug where *remote* players aimed when *you* held right-mouse. Keep
engine-facing scripts dumb; put logic in plain classes you could unit-test.

### Functional core: `PlayerMovement.Step`

One pure function turns (position, intent, flags, dt) into a new position.
The client calls it to predict; the server calls it as the authority.
Prediction, and later reconciliation, only work because both sides run
*literally the same code* — this is the single most important seam in the repo.

### Fixed-timestep loop

`Server/Program.cs` accumulates real elapsed time and steps the simulation in
fixed 1/30s increments. Deterministic tick length is a prerequisite for
reconciliation and lag compensation.

## Prior art

- **Glazer & Madhav, *Multiplayer Game Programming*** — the primary source. The
  layering, typed serialization (ch. 4), replication (ch. 5), and the
  input/tick/broadcast flow (ch. 6–8) are this book mapped onto Riptide.
- **Riptide sample projects** (Tom Weiland) — the message-id enums and
  Welcome/Spawn handshake idiom. We swapped its reflection-based
  `[MessageHandler]` for explicit dispatch switches.
- **Glenn Fiedler, gafferongames.com** — ["Fix Your Timestep!"](https://gafferongames.com/post/fix_your_timestep/)
  (our server loop), and "Snapshot Interpolation" for what's next.
- **Gabriel Gambetta, ["Fast-Paced Multiplayer"](https://www.gabrielgambetta.com/client-server-game-architecture.html)** —
  the clearest short read on prediction, reconciliation, and entity interpolation.
- **Valve, ["Source Multiplayer Networking"](https://developer.valvesoftware.com/wiki/Source_Multiplayer_Networking)** —
  how a shipped engine combines all of the above (100ms interpolation delay, etc.).

---

## Next steps

### 2. Object registry (synced non-player objects)

Copy the player pipeline; solve the two problems players got for free:

1. **Identity.** Players ride on Riptide's client id. Objects need a
   server-assigned `NetworkId` — an incrementing `uint` owned by `GameWorld`.
   Decide now that client ids and network ids are different things.
2. **Type dispatch.** A spawn message must say *what* to build. Add an
   `ObjectType : ushort` enum to `Common`.

Then mirror the existing structure:

- **Messages** (`Common/Messages/`): `ObjectSpawnData { NetworkId, ObjectType, Position, ... }`,
  `ObjectDespawnData { NetworkId }`, `ObjectStateData { NetworkId, ... }`.
- **Server:** `GameWorld.SpawnObject(type, pos)` assigns the id and broadcasts.
  `AddPlayer` must also catch newcomers up on *existing objects*, exactly like it
  does for existing players.
- **Client sim:** `ObjectRegistry` — same shape as `PlayerRegistry`: dictionary,
  subscribes to `NetworkManager` events, raises `ObjectSpawned` / `ObjectDespawned`.
- **Client view:** `ObjectViewFactory` with a
  `Dictionary<ObjectType, Func<NetworkObject, Entity>>` spawn table mapping each
  type to its entity construction.

Read book **ch. 5 (Object Replication)** before building — it covers this
create/update/destroy trio, plus partial updates with dirty flags, which you'll
want as soon as objects have more state than a position.

### 3. State flags: inputs vs outcomes (who computes `PlayerStateFlags`?)

Today the client computes all flags in `LocalPlayerController`, sends them in
`PlayerInputData`, and `GameWorld.ApplyInput` trusts them verbatim. Before
wiring up shooting/reloading/jumping, decide flag by flag who the authority is.
The useful distinction is not client vs server — it's **inputs vs outcomes**:

- **Input-like flags** (`Sprinting`, `Crouching`, `Aiming`) are just "is a
  button held." The client *is* the authority on its own buttons, so sending
  these is fine — claiming `Sprinting` without holding shift isn't even a
  detectable lie, since it's indistinguishable from a player who always holds
  shift. This holds **only while the flag has no cost or precondition**: add a
  stamina bar and `Sprinting` becomes an outcome the server must own.
- **Outcome-like flags** (`Shooting`, `Reloading`, `Jumping`, and even
  `Moving`) are *results of game rules* — fire-rate cooldowns, ammo, reload
  timers, being grounded. Letting the client assert an outcome means letting it
  assert a rule was satisfied. The server should derive these from discrete
  *actions* ("fire pressed", "jump pressed") plus its own timers/resources.
  `Moving` is the degenerate case: it's derivable server-side as
  `intent != Vector3.Zero`, so it shouldn't be on the wire at all — that's a
  bit the client can only use to lie.

**Why contradiction-checking isn't enough.** It's tempting to think the server
can just reject nonsense like `Crouching | Sprinting`. It can — but cheaters
don't send contradictions, they send plausible packets, and mostly they cheat
by **omission**. `PlayerMovement.SlowingStates` makes `Aiming`/`Shooting`/
`Reloading` drop you to `SlowSpeed`; a hacked client that aims and fires
normally but *never sets those bits* strafes at `SprintSpeed` while shooting
accurately, and its packet stream is indistinguishable from a legit hip-firing
player. Contradiction checks are a **blacklist** (enumerate every invalid
combo, re-audit whenever mechanics change). Server-side derivation is a
**whitelist**: invalid states can't exist because the server only produces
states its own rules generated — there's nothing to detect.

**The real cost: prediction.** Flags feed `PlayerMovement.Step`, so they affect
predicted position. Whatever the server derives, the client must derive
*identically* during prediction and replay, or every reload causes a speed
misprediction and a visible reconciliation snap. "Server-side flags" therefore
means the derivation (cooldowns, reload duration, grounded checks) becomes
another pure shared function in `Common`, run by both sides — the same seam as
`PlayerMovement.Step`. That's real machinery, which is why the plan is a
hybrid:

1. **Now, cheaply:** clamp `Intent` to unit length in `GameWorld.ApplyInput`
   (the first-order hole — `Intent = (100, 0, 0)` is a 30× speed hack, far
   worse than any flag game), drop `Moving` from the wire and derive it
   server-side, and normalize contradictory combos while you're there.
2. **Keep `Sprinting`/`Crouching`/`Aiming` client-sent.** They're inputs today.
   The trigger to migrate one server-side is the moment it gains a cost or
   precondition (stamina, etc.).
3. **When wiring up shooting/reloading/jumping, never send them as claimed
   state.** Send the *action* (edge-triggered "fire/reload/jump pressed" bits
   in the input message); shared pure code in `Common` derives the resulting
   flags from action + timers, identically on both sides. The `Gun.cs` comment
   about routing shooting through the server already points this way.

Book **ch. 10 (Security)** covers the general principle: validate inputs,
derive outcomes.

### 4. Roadmap from the book

In order, each mapped to where it lands in this repo:

1. **Ch. 8 — Entity interpolation** → item 1 above (`RemotePlayer`, `PlayerViewScript`).
2. **Ch. 8 — Prediction reconciliation** → `LocalPlayer`. Keep a buffer of sent
   inputs (the `Sequence` field already exists for this). Server echoes back
   `(lastProcessedSequence, position)`; client discards acked inputs, snaps to
   the server position, and replays the unacked inputs through
   `PlayerMovement.Step`. This is why `Step` must stay pure. Then remove the
   "ignore own position" guard in `PlayerRegistry`.
3. **Ch. 5 — Replication** → item 2 above (object registry).
4. **Ch. 7 — Latency & reliability** → mostly covered by Riptide (it provides
   reliable/unreliable channels), but read it to understand what Riptide does
   under the hood; add an RTT display to the HUD (`Client.RTT` in Riptide).
5. **Ch. 10 — Security** → item 3 above (clamp `Intent`, derive outcome flags
   server-side instead of trusting claimed state).
6. **Ch. 9 — Scalability / interest management** → only relevant once worlds are
   big: send updates only for nearby players/objects.

---

## C# idioms used in this codebase

Quick reference if you're coming from another language:

- **`event Action<T>? Foo;`** — built-in observer. `Action<T>` is the delegate
  type "function taking a `T`, returning nothing" (like `Consumer<T>` in Java or
  a `(T) -> Unit` lambda type). Subscribe with `+=`, fire with `Foo?.Invoke(x)`.
  An event with no subscribers is `null`, hence the `?.`.
- **Extension methods** — `static` method whose first parameter has `this`:
  `public static Vector3 ToStride(this System.Numerics.Vector3 v)` lets you write
  `pos.ToStride()`. Pure syntax sugar; requires the defining class's namespace to
  be `using`-imported. Used for `message.AddVector3(...)`, `flags.With(...)`.
- **`struct` vs `class`** — structs are value types (copied on assignment, no
  heap allocation). Message DTOs are structs because they're small, short-lived,
  and copied onto the wire.
- **Nullable reference types (`?`)** — with `<Nullable>enable</Nullable>` in the
  csproj, `PlayerRegistry?` means "may be null, compiler makes you check";
  `PlayerRegistry` means "never null, compiler enforces it".
- **`{ get; init; }`** — property settable *only* inside an object initializer:
  `new Foo { Bar = x }` works, `foo.Bar = x` later doesn't. Used for wire-up
  properties that shouldn't change after construction.
- **`required`** — the compiler forces the property to be set in the object
  initializer (see `PlayerViewScript.Player`).
- **`=>` bodies** — `public void Update() => client.Update();` is just a
  single-expression method/property, nothing more.
- **`[Flags]` enums** — bitmask enums (`PlayerStateFlags`); combine with `|`,
  test with `HasFlag` or `(flags & mask) != 0`.
