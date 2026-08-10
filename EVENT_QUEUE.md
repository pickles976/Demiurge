# Event Queue, Journaling, and Theater Replay

## Status

This document defines the planned event architecture for authoritative simulation, replayable tests,
debugging, and a Halo-style theater mode. It is a design and migration plan, not a description of an
implemented system.

The target is deliberately more than an in-process event bus. Gameplay commands and discrete domain
events become a durable, versioned history, while periodic authoritative state frames preserve
high-frequency simulation state for reliable theater playback and seeking.

## Goals

- Apply all authoritative commands at a deterministic server-tick boundary.
- Make discrete gameplay state auditable through an append-only event journal.
- Reproduce bugs from recorded command streams and report the first divergent tick.
- Let tests use the same command path as networked clients.
- Keep a bounded, always-on flight recorder that can be dumped after failures.
- Record full matches for theater playback with pause, seek, speed, and independent cameras.
- Keep recordings safe to inspect after crashes and explicit about build/content compatibility.
- Preserve the 30 TPS server target without adding unbounded work or allocation to every tick.

## Non-goals

- Do not turn presentation, logging, or profiling messages into authoritative gameplay events.
- Do not record every transform change as a semantic domain event.
- Do not promise that arbitrary old command journals will resimulate under arbitrary future builds.
- Do not use CLR type names, object layouts, or raw network messages as the durable replay format.
- Do not let subscribers mutate authoritative state through an unconstrained event bus.
- Do not require theater playback to run AI, physics, or navigation again.

## Lessons from Quake 3

Quake 3 has two mechanisms that should remain conceptually separate here.

Its common event journal records timestamped platform, input, console, and network events. In replay
mode, recorded events replace live events at the same ingestion point and then pass through the
normal dispatcher. Its pushed-event queue is a power-of-two ring with monotonically increasing head
and tail sequence counters. See
[`code/qcommon/common.c`](https://github.com/id-Software/Quake-III-Arena/blob/master/code/qcommon/common.c)
and the `sysEvent_t` contract in
[`code/qcommon/qcommon.h`](https://github.com/id-Software/Quake-III-Arena/blob/master/code/qcommon/qcommon.h).

Quake 3 demos are separate. They store an initial game state and subsequent server messages, then
feed those messages through the normal client parser. See
[`code/client/cl_main.c`](https://github.com/id-Software/Quake-III-Arena/blob/master/code/client/cl_main.c).
Predictable player events also use sequence counters and bounded ring indexing; see
[`code/game/g_active.c`](https://github.com/id-Software/Quake-III-Arena/blob/master/code/game/g_active.c).

We should borrow:

- One substitution point for live commands, test commands, and replayed commands.
- Tick/sequence ordering rather than incidental handler order.
- A compact bounded ring for recent diagnostic history.
- Playback through the same application path used during live play.
- Clear payload ownership and explicit serialization.

We should not copy:

- Native structure or pointer dumps as a persistent format.
- Silent oldest-event loss for authoritative gameplay.
- Wall-clock timestamps as simulation authority.
- A replay format without explicit schema and content versions.
- The assumption that an input journal alone is sufficient for durable theater playback.

## Model

The system has four streams with different contracts.

### Commands

Commands are requests to change the authoritative world. Examples include player input, fire,
reload, interact, use, dig, mortar fire, connection lifecycle, and trusted console commands.

A command is not proof that an action occurred. A `FireRequested` command may be rejected because
the weapon is reloading, the mortar aim is outside its fire sector, or the player is dead.

Network callbacks, tests, consoles, and replay verification all submit commands to the same ingress
queue. Only the simulation thread applies them.

### Domain events

Domain events are immutable facts accepted or produced by authoritative simulation. Examples include
`WeaponFired`, `DamageApplied`, `ActorKilled`, `ItemEquipped`, `TerrainEdited`, `FlagCaptured`, and
`MortarImpacted`.

Discrete gameplay state should be event-sourced: inventory, equipment, health changes, deaths,
spawns, objectives, terrain edits, and other durable decisions are represented by domain events.
Their projectors update current state, replication, activity feeds, journals, and test observers.

This rule does not mean every system publishes arbitrary events and later mutates the world in an
uncontrolled subscriber. Command handlers and domain aggregates decide outcomes in a defined tick
phase. Projectors apply the resulting facts in a deterministic order.

### State frames

State frames capture authoritative high-frequency state that is a poor semantic event stream:
positions, rotations, velocities, animation state, projectile state, and similar continuously
changing values. Frames may be deltas from a keyframe.

Full keyframes allow seeking and bound recovery time. The theater player consumes state frames and
domain-event markers without rerunning historical AI, navigation, or physics. This is the durable
visual record of what occurred.

### Diagnostics

Diagnostic events include AI choices, path-query details, profiling spans, rejection explanations,
and other developer-facing information. They may be enabled selectively and written beside the
authoritative streams, but they never drive simulation and are excluded from state hashes.

## Data flow

```text
Network ---------+
Console ---------+
Tests -----------+--> command ingress --> begin-tick drain --> GameWorld tick
Replay verifier -+                                           |
                                                             +--> domain events
                                                             |      +--> state projectors
                                                             |      +--> network projectors
                                                             |      +--> replay journal
                                                             |      +--> debug ring / JSONL
                                                             |
                                                             +--> state frame sampler
                                                                    +--> replay journal
```

`GameServer.OnMessageReceived` currently applies gameplay requests directly. This is the first
migration seam: callbacks will normalize and enqueue commands instead. `GameWorld.Tick` is the
single deterministic drain boundary.

Object replication, terrain replication, activity feeds, and other direct sends will become event
projections incrementally. This avoids a big-bang rewrite while moving authoritative mutation onto
one path.

## Event envelope

Every durable command and domain event uses a versioned envelope:

```text
FormatVersion    replay container/envelope version
Tick             authoritative simulation tick
Sequence         monotonically increasing session sequence
Phase            command, domain, state, or diagnostic stream
TypeId           stable append-only numeric event identifier
PayloadVersion   schema version for this event type
SourceObjectId   optional authoritative actor/object identity
Payload          length-delimited serialized data
```

Rules:

- `(Tick, Sequence)` defines total order within a session.
- Type IDs are never reused, even after an event type is retired.
- Payloads use canonical datapack IDs where identity must survive a session. Runtime item handles are
  valid only inside the matching resolved registry.
- Unknown optional event types can be skipped because payloads are length-delimited. Unknown required
  authoritative types make verification or playback fail with a compatibility error.
- Wall-clock time may appear in recording metadata but never determines authoritative ordering.
- Event schemas use explicit fields and codecs, not default reflection serialization of CLR objects.

The network `Message` serializer can share primitives with the event codec, but network packets are
not the durable event schema. Wire protocol evolution and replay evolution have different lifetimes.

## Queue semantics

### Ingress

Producers submit normalized commands to a bounded multi-producer ingress queue. Receipt order is
assigned with a monotonic sequence. The simulation thread is the only consumer and the only thread
allowed to mutate `GameWorld`.

At the beginning of each tick, the simulation thread atomically takes commands eligible for that
tick and applies them in sequence order. A command submitted while the current drain is executing is
not recursively processed; it becomes eligible at the next defined drain boundary.

Connection lifecycle and trusted console commands follow the same rule when they affect world state.
Transport housekeeping that does not affect simulation may remain outside the queue.

### Overflow

Authoritative commands and domain events must never be silently overwritten.

- Reliable commands use bounded backpressure or reject/disconnect the faulty producer explicitly.
- High-rate movement input may use a documented latest-input coalescing policy before it enters the
  authoritative queue. Coalescing is itself observable through counters.
- A journal writer that cannot keep up reports a recording failure; it does not stall the server
  indefinitely or pretend the recording is complete.
- Overflow counters and maximum queue depth are exposed in server diagnostics.

The flight recorder is different: it is intentionally a ring and overwrites old diagnostic history.
Its retained tick range must always be visible in metadata.

### Phases and reentrancy

Each tick has explicit phases:

1. Drain and validate commands.
2. Apply accepted commands and emit domain events.
3. Advance simulation systems in a fixed order.
4. Apply/publish resulting domain events in sequence order.
5. Build replication output and sample state frames.
6. Commit the tick journal and state hash.

Handlers cannot synchronously enqueue a command back into an earlier phase. Cross-system work is
represented as an event for a later defined phase or as a command for a later tick. This prevents
subscriber registration order and recursion from becoming hidden gameplay rules.

## Recording format

The canonical recording is a chunked binary file. JSONL is an export for people and log tooling.

The header contains:

- Magic and replay format version.
- Game build and protocol version.
- Tick rate.
- Map ID and content hash.
- Resolved gameplay datapack hash.
- Event registry/schema version.
- Simulation seed or named subsystem seeds.
- Initial tick and canonical state hash.

Chunk types include:

- Command batches.
- Domain-event batches.
- State deltas.
- Full world keyframes.
- Terrain checkpoints and edits.
- Optional diagnostics.
- Tick-to-file-offset index blocks.

Chunks are length-prefixed and checksummed. A reader must recover every complete chunk before a
truncated or corrupt tail. The final index accelerates seeking, but periodic partial indexes make a
crash recording useful without a clean close.

The first format supports exact build, protocol, map, and datapack compatibility. A mismatch fails
clearly rather than attempting a plausible but incorrect resimulation. State-based theater playback
can later support a wider compatibility window through explicit format migrations.

## Replay modes

### Verification replay

The headless verifier loads the recorded map, datapacks, seeds, and initial checkpoint. It submits
recorded commands on their original ticks through the normal command ingress, compares emitted
domain events, and checks canonical state hashes at fixed intervals.

On divergence it reports the first differing tick and sequence, then narrows the difference to an
object and component where possible. Verification is intended for regression tests, bug reports,
and determinism audits.

Deterministic verification requires:

- Centralized, seedable simulation randomness.
- No wall-clock decisions inside authoritative systems.
- Stable iteration where collection order can change results.
- Deterministic ordering for asynchronous navigation and worker results.
- Canonical float and component hashing rules.
- Exclusion of profiling values and transport-only IDs from authoritative hashes.

### Theater replay

The theater client loads authoritative keyframes and applies state deltas through the same
simulation-to-view presentation path used by live replication. It does not rerun gameplay systems.
Domain events provide timeline markers, effects, UI context, and camera targets.

Initial theater controls are pause/resume, playback speed, single-tick stepping, timeline seeking,
free camera, follow camera, and bookmarks for kills, explosions, and objectives.

Start with a full keyframe approximately every five seconds and state frames at the normal network
snapshot cadence. Measure file size and seek latency before changing either rate.

## Debugging workflow

The server keeps an always-on bounded flight recorder, initially targeting the latest 60 seconds.
It can be dumped on a command, assertion, crash path, or test failure. Full-match recording remains
explicit.

Developer tooling should provide equivalents of:

```text
events status
events tail [filter]
events start <path>
events stop
events dump <path>
events export-jsonl <input> <output>
replay verify <path>
```

Filters include tick range, sequence range, actor/object, stream, and event type. Sensitive values
such as authentication material or raw connection tokens must never enter payloads or JSONL output.

## Testing strategy

The queue and codecs live in `Common` so their core behavior is testable without Stride or sockets.

Required unit tests:

- Total ordering across multiple producers.
- Tick eligibility and next-tick reentrancy.
- Explicit overflow behavior and movement-input coalescing.
- Event registry uniqueness and retired-ID protection.
- Payload round trips and version upgrades.
- Unknown optional versus required event handling.
- Truncated and checksum-failed chunk recovery.
- Canonical state hashing independent of dictionary insertion order.

Required server tests:

- Network messages and scripted tests reach the same command handler.
- Network callbacks do not mutate `GameWorld` directly.
- Two runs of the same journal emit identical ordered domain events and state hashes.
- Invalid commands produce identical rejection results live and during replay.
- A recorded checkpoint plus remaining commands reaches the same final state as a full run.
- Flight-recorder dumps accurately declare their retained tick range.

Golden journals should be small, intentional scenarios rather than large opaque binary fixtures.
Property/fuzz tests can generate longer command streams and preserve the seed plus minimal failing
journal when they find a divergence.

## Performance requirements

- Recording disabled adds no per-event heap allocation on the tick hot path after warmup.
- Queue work is bounded per tick and exposes backlog depth.
- Disk compression and writes occur off the simulation thread.
- Worker threads receive immutable recording batches; they never own pooled network `Message`
  instances and never mutate world state.
- Writer backpressure is bounded and produces an explicit incomplete-recording result.
- Benchmarks cover combat-heavy ticks, terrain edits, and the maximum expected player input rate on
  the Beelink SER5 target.

Exact budgets should be set from a baseline measurement. The non-negotiable gate is that recording
cannot cause the server to miss its 30 TPS p99 target.

## Migration plan

### Phase 1: Event kernel

Add the envelope, type registry, codecs, deterministic queue, in-memory sink, flight-recorder ring,
and JSONL formatter in `Common`. Establish append-only ID and schema review rules.

### Phase 2: Command ingress

Change `GameServer` gameplay callbacks to enqueue typed commands. Drain them at the start of
`GameWorld.Tick`. Include world-affecting connection lifecycle and console commands. Preserve the
existing player-input sequence and rate-control semantics.

### Phase 3: Authoritative domain events

Introduce events for player lifecycle, weapons, damage/death, equipment, explosives, mortar,
terrain, flags, and tickets. Initially mirror existing behavior, then move state changes and direct
network/activity output behind deterministic projectors one subsystem at a time.

### Phase 4: Journal and flight recorder

Implement the binary reader/writer, partial indexes, compatibility checks, JSONL export, filtering,
and server commands. Verify crash/truncation recovery and bounded writer behavior.

### Phase 5: Deterministic verification

Centralize gameplay randomness, audit time and iteration order, order asynchronous results, add
canonical state hashing, and build the headless replay verifier. Add representative golden and
generated replay tests.

### Phase 6: Theater state track

Record keyframes, state deltas, terrain checkpoints, and a seek index. Establish measured file-size,
recording-cost, and seek-latency budgets.

### Phase 7: Theater client

Add a replay client session that reuses the live presentation pipeline, followed by cameras,
timeline controls, event markers, speed control, and bookmarks.

## First milestone

The first implementation milestone ends before theater UI and delivers:

- A versioned event envelope, registry, queue, and codecs.
- Tick-boundary ingestion for all network gameplay requests.
- An in-memory flight recorder and JSONL dump.
- Domain events for player lifecycle, firing, damage/death, and terrain edits.
- A scripted headless runner using the same command path as live play.
- Canonical end-state hashing and first-divergence reporting.

It is complete when:

- Identical scripted journals produce identical event order and final state hashes.
- Tests and network traffic use the same command application path.
- No network callback directly mutates authoritative world state.
- Queue overflow cannot silently discard a reliable gameplay command or domain event.
- A truncated journal loads through its last complete chunk.
- Recording-disabled overhead is negligible and enabled recording stays within an established tick
  budget.
- A replay mismatch identifies the first divergent tick and affected object or subsystem.

## Architectural commitment

Discrete authoritative gameplay is event-sourced. Current materialized state remains available for
fast queries and replication, and full snapshots bound recovery time. High-frequency physical state
is recorded as versioned frames rather than mislabeled as millions of semantic events. Theater uses
those authoritative frames for fidelity; verification uses commands and domain events to test the
simulation.

This gives the project one auditable gameplay history without making long-term replay fidelity
depend on every future build reproducing every historical physics, AI, and navigation decision.
