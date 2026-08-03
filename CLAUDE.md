# DemiurgeSharp

Code-only Stride 4.3.0.2507 multiplayer game. net10.0, Linux, Vulkan backend.
There is no Game Studio project: `Client/Program.cs` starts `ClientApplication`, which owns the
Stride process, persistent terminal, and `ClientSessionCoordinator`. Runtime and editor scene state
live in separate disposable sessions.

## Build & run

```bash
dotnet build DemiurgeSharp.slnx
dotnet run --launch-profile singleplayer        # client + in-process server — USE THIS
dotnet run                                      # client only; connects to a server you started
dotnet run -- --editor trench-test              # in-engine map editor
dotnet run --project Server/DemiurgeServer.csproj   # standalone server
dotnet test DemiurgeSharp.slnx                  # xUnit suite (Common.Tests), headless
dotnet test --filter "Category!=Benchmark&Category!=Integration"  # ordinary fast suite
dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "Category=Integration"  # feature integration
```

**Singleplayer is the normal way to test anything server-side** — one launch instead of two.
Profiles live in `Properties/launchSettings.json`; `client` is first so a bare `dotnet run` keeps
its old behaviour. The underlying flag is `--singleplayer`, so `dotnet run -- --singleplayer` also
works (the bare `--` matters; dotnet eats unknown flags first). It refuses to start rather than
falling back if the port is taken — silently attaching to an already-running server would mean
testing a stale build.

The current singleplayer scenario loads `conquest`, reserves a team-1 spawn for the player, and
creates 16 NPCs per team around authored team spawn clusters. Use `npc-test` through the editor or
an explicit hosted map for smaller focused tests.

`Common` has no Stride dependency — it's plain `System.Numerics` — so anything in it is
testable without booting the engine. That's why the voxel coordinate maths has real tests and
the rest of the codebase doesn't; keep new pure logic in `Common` and it stays that way. Note
that `DemiurgeSharp.csproj` lives at the repo root and globs `**/*.cs`, so every sibling project
needs a `<Compile Remove="Dir/**/*.cs" />` line or the root build breaks on duplicate
assembly attributes.

If the build goes weird after dependency changes: `dotnet clean && dotnet restore --no-cache && dotnet build --no-incremental`.

Projects: `DemiurgeSharp.csproj` (client), `Server/DemiurgeServer.csproj`,
`Common/DemiurgeCommon.csproj`, `Editor.Core/DemiurgeEditor.Core.csproj`,
`Common.Tests/DemiurgeCommon.Tests.csproj`, `Editor.Core.Tests/DemiurgeEditor.Core.Tests.csproj`,
`Server.Tests/DemiurgeServer.Tests.csproj`, `tools/GltfAssetGenerator`.

## Developer terminal

Backtick/tilde opens the in-game terminal; `F3` toggles the runtime free camera. The terminal is
process-owned and survives transitions among editor, local host, and remote runtime sessions.
World-changing runtime commands are parsed into typed values in Common and executed authoritatively
on the server. Single-player enables client-issued commands; a standalone server requires
`--allow-cheats` for clients but its local stdin console is always trusted. Canonical item names live
in `ItemCatalog` and are namespaced (`demiurge:ak47`), while `ItemType` remains the append-only
numeric wire identity. Runtime mob IDs are actor selectors (`@60000`); replicated object IDs use
`#1`. Runtime spawn/equip commands mutate only the current server session and never modify or save
the editor source map. Editor placements use stable GUIDs displayed as unique eight-character
prefixes; `editor object equip` persists a mob weapon in that source placement. `ai stats` reports the
last one-second AI window; `ai track` is a client-side NPC debug overlay and is answered locally
without reaching the server. See `docs/COMMANDS.md`.

## Performance targets

**60+ FPS client and 30 TPS server, 99% of the time, in singleplayer, on a Beelink SER5.** These are
requirements, not aspirations — a design that only holds on a good machine does not hold.

**The "99% of the time" is the load-bearing part, and nothing currently measures it.** `frame:` and
`server tick:` report one-second averages plus a single worst case, which cannot distinguish "60 fps
throughout" from "115 fps for half a second and 20 fps for the other half" — and the second is what
the player feels. A mean that meets the target while the p99 misses it is the normal way this goes
wrong, not an edge case. Percentile instrumentation is a prerequisite for claiming either target is
met; until it exists, treat every "we hit 30 TPS" claim, including the ones already written down
here, as unverified against the real requirement.

The worst case is what to design against anyway. Combat is when the budget is tightest and also when
stutter is least acceptable.

The SER5 is the reference machine and its shape matters more than its speed: a Ryzen 5 5500U/5560U
class part, **6 cores / 12 threads**, a **Radeon Vega 6–7 iGPU**, and dual-channel DDR4 whose
bandwidth the CPU and GPU **share**, at 15–25 W sustained. Three consequences:

- **Do not infer the bottleneck from resolution alone.** The measured 1920x1080 regression was an
  SDL fullscreen resize/device-reset loop, not GPU load; forcing the RTX 4060 did not improve it.
  Terrain rendering/streaming remains a likely steady-state cost, but profile before choosing a
  CPU, GPU, or allocation fix.
- **There are cores to spare.** Pushing work onto a worker is the preferred fix over micro-optimizing
  it on the main thread — but only *computation* moves. Anything that creates a Riptide `Message` or
  mutates world state stays on the main thread and hands results back through a queue, exactly as
  `TerrainState` does. See the Riptide pooling note under Netcode; that constraint is not negotiable.
- **Cache-hostile access costs twice**, because scattered reads compete with the iGPU for the same
  DDR4. This is why the `TerrainCollision` fetch pattern below matters more here than it would on a
  discrete-GPU box.

**Singleplayer's server runs on its own thread** (`ServerHost.StartOnOwnThread`), so the server tick
no longer shares the client's 16.6 ms frame budget. It used to, and the cost was not subtle: a
measured frame spent **214 ms of 216 ms** blocked inside `ServerHost.Step()` — up to
`MaxCatchUpTicks` ticks inline — while the client's own net, drain, and terrain work came to 2 ms.
That is 5 fps caused entirely by who owned the thread.

Both targets are currently met on the conquest scenario: **53–117 fps and 30–31 TPS**, with the tick
at 12–25 ms inside its 33 ms budget.

Threading also made the tick itself cheaper — `actors` 50 ms to 10–21 ms on identical code — because
the main thread had been running four catch-up ticks inline while eight path workers and the renderer
fought for the same cores and the same DDR4 the iGPU uses. **A measurement taken while the machine is
thrashing attributes cost to whoever holds the thread, not to whatever is expensive.** Fix the
contention before believing a profile.

This means **singleplayer is no longer the combined-budget stress case** it used to be, and that makes
the targets *harder* to monitor rather than easier. The two budgets are now independent: the client
owes 16.6 ms per frame, the server owes 33 ms per tick. A server tick over budget no longer announces
itself as a frame drop — it shows up as `[ServerTick] total` exceeding 33 and the tick count settling
below 30, which is quieter and easy to miss. Read `frame:` and `server tick:` together; neither alone
tells you whether the machine is keeping up.

The consequence that actually shapes code: **nothing expensive runs every tick for every entity.**
Per-entity per-tick work is scheduled, amortized across ticks, shared between entities, or made
event-driven. This is the rule behind AI think-rate LOD, the terrain meshing queue, and the LOS ray
budget — it is not an AI-specific concern.

One measured hot path worth knowing before you profile: `TerrainCollision.TrySample` reads 56 voxels
(8 corners for the cell, 48 more for the smoothed gradient stencil) and `TryDeepestContact` triples
that.

**The repeated-lookup half of this is already fixed and the batching half was tried and is a
regression.** `VoxelCursor` memoizes the owning chunk, so those 56 reads cost one `ChunkAt` plus a
`ConcurrentDictionary` probe and 55 memo hits, not 56 probes. This file used to claim the whole
sample "fits in one 3×3×3 block fetched once — an unclaimed order of magnitude". It does fit, and it
is not a win: fetching 27 voxels and then indexing into the block costs **more** than 56 warm
`TryGet` calls, because it trades cheap memoized reads for 56 block-index computations. Measured at
41.5 µs → 79.8 µs per `PlayerMovement.Step`, i.e. nearly 2× worse, and reverted. Do not re-derive it.

What did help there was smaller: `TrySampleRaw` was calling `TrySampleCell`, which computes a
9-lerp analytic gradient and discards it — six times per sample, once for each central-difference
stencil point. A value-only path took `Step` from 41.5 µs to 37.6 µs with bit-identical output.

The order-of-magnitude win on this class of cost turned out to be in `TerrainRaycast` rather than
collision: the march computed a smoothed surface normal at every step and used none of it, and
dropping to the per-cell gradient took a 60 m line-of-sight ray from 249 µs to 56 µs.

Measure before optimizing, and measure again after. Per-system tick timing belongs in the developer
terminal, not in a one-off harness.

## Design method: complete systems, not special cases

**Prefer a system whose completeness produces the behavior as a side effect, over a branch that
produces the behavior directly.** The navigation rewrite is the worked example and the reason this
section exists. A run of NPC movement bugs was each fixed by another terrain-shape classifier —
highest ground within N metres, a fixed escape direction, a pre-follower ramp branch — and every fix
satisfied one scenario while breaking a neighbouring one, because no two of those classifiers could
be compared against each other. Adopting Baritone's contract deleted all of them at once, and the
scenarios they had been patching passed without anyone writing code aimed at them.

**The part that generalizes is the common currency, not the search.** A* was the cheap half. What
made it work is that walking, jumping, falling, and excavation are all priced in *estimated execution
seconds*, so "route through the exit", "cross the bridge", and "cut a staircase" stop being behaviors
anybody implements and become whichever number was smaller. A complete search over incommensurable
costs generalizes nothing. When reaching for this method, the design work is finding the unit the
alternatives can be honestly priced in — the algorithm that then compares them is usually off the
shelf.

It shapes tests too: assert a **property of the model** ("crossed without excavation when the bridge
is cheaper", "stone never produces a dig route"), not a trace through an implementation. A test
written against a heuristic encodes that heuristic's special cases and then obstructs the general
system that would have replaced it. The stage-4 list in `docs/BARITONE.md` is written this way
deliberately.

Two limits, because the method has its own failure mode:

- **Generality cannot be bought with compute here.** Sutton's bitter lesson bets on search and
  learning scaling as compute gets cheaper; the SER5 and the 16.6 ms singleplayer frame mean we do
  not get that bet. Generality has to come from the model being right, not from being allowed to
  think longer — which is why `docs/BARITONE.md` is full of node ceilings and p95 gates. A general
  system that wins by starving the tick is not a win.
- **A complete search over the wrong state space is worse than a pile of heuristics**, because it
  generalizes confidently in the wrong direction and leaves no single branch to point at. Baritone
  had already done the modelling for us; the next problem will not arrive with that done. Derive the
  state space and the cost unit *before* writing the search.

And it was not free. That execution record ends in roughly eight corrections, and the goal-rise gate
in particular is a special case bolted to the side of the search. That is the expected shape — the
method moves the residue from "more branches" to "annealing a cost model" — but filing it as a clean
sweep would set up the next adoption to be a surprise.

Unclaimed today: per-unit AI arbitration still selects behavior from role and weapon type rather than
pricing every available action in one currency. See the closing note under AI layers in
`docs/ARCHITECTURE.md`.

## Architecture

**Read `docs/RECIPES.md` before changing gameplay code.** It is the map, not a tutorial:
Common is the wire, Server is truth, Client is Netcode → Sim → View with a strict
one-way flow, and Program.cs wires it all. It also carries step-by-step recipes for
adding a replicated component, an equippable item, a weapon, or a new trait — each one
a fixed list of append-only edits. Follow the recipe instead of re-deriving it; the
steps that are easy to forget are exactly the ones that shipped bugs before.

Wire rule worth repeating here: enum values and the `ComponentBundle` if-chain order
ARE the protocol. Append, never reorder, never delete — clients desync silently.

`docs/ARCHITECTURE.md` is the current subsystem map and AI overview;
`docs/NAVIGATION.md` documents the live navigation lifecycle, worker scheduling, recovery, metrics,
and tests.

`ClientApplication` is the process composition root. It owns Stride, global rendering and lighting,
the persistent terminal, and `ClientSessionCoordinator`. `RuntimeClientSession` owns networking,
registries, terrain streaming, views, gameplay scripts, and an optional in-process `ServerHost`.
`EditorClientSession` owns the source document preview, editor camera, tools, and placement views.
Both sessions must release entities, services, event handlers, sockets, and GPU terrain resources in
`Dispose()`. In runtime updates, terrain `Drain()` must still run before `RebuildDirty()`: Drain is
the only writer of chunk voxels, and dispatch only hands out chunks Drain has finished. More detail
is in `docs/stride/code-only-runtime-and-assets.md` and `docs/EDITOR.md`.

Editor placements store a stable GUID and an integer anchor cell, not a raw world transform.
`EditorPlacementPosition` is the one conversion to world space: X/Z come from the cell center and Y
comes from the nearest upward SDF crossing around that cell. Preview views, validation, and runtime
baking must all call its placement-kind-aware overload; player spawns additionally resolve the
capsule clearance required on slopes. Using `Cell.Y` directly buries objects and using the highest
column surface breaks trenches and caves. Plain block brushes are axis-aligned dimensions expanded
into ordinary sample-centered block placements and have no rotation. Rotation remains meaningful
for named structures and selected objects.

`session playtest` / `F4` is the fast embedded playtest. It still starts the normal authoritative
server and runtime networking, actor, input, and view systems, but borrows the editor camera,
`ClientTerrain`, and preloaded terrain map. The server receives `ChunkMap.DeepClone()`, so runtime
edits cannot mutate editor source through shared references. Client edits are tracked and
`EditorSession.RestoreTerrain()` replays only affected chunks on return without touching history or
dirty state. Runtime camera scripts must be removed before editor controls resume, and editor
placement proxies stay hidden while runtime actor/object views are active.

**A playtest spawns you at the editor fly camera's exact position**, not at the map's player spawns.
`EditorClientSession.PlaytestSpawn` reads the camera transform and rides to the server as
`ServerOptions.SpawnOverride`; `GameWorld.SpawnPlayerMove` prefers it over `RuntimePlacementKind.PlayerSpawn`
and spawns ungrounded, since the camera is usually in the air. It is the whole session's spawn point, so
dying in a playtest returns you to the same spot instead of to a map spawn. Both `session playtest` and
`session playtest-networked` set it (the latter through `SessionRequest.HostMap`); a normal `session host`
or dedicated server leaves it null and the map's spawns apply as before.

`session playtest-networked` retains the full save, bake, runtime load, TCP terrain stream, and fresh
client remesh path. Use it to validate persistence and transport. The fast path deliberately skips
those expensive presentation and delivery steps; it is not a replacement for that release-path
check.

Runtime terrain has two separate load-bearing subscriptions:
`NetworkManager.Welcomed -> ChunkTcpClient.Connect` opens the authenticated TCP stream, while
`ChunkTcpClient.ChunkReceived -> TerrainState.Receive` actually queues each chunk for the main
thread. A connected stream without the second subscription leaves the terrain map empty and causes
continuous movement reconciliation because the client predicts against unloaded terrain.

AI is decision-only. `SquadFormation` re-groups NPCs into squads from live proximity, `CommanderAi`
assigns team objectives, `SquadBlackboard` shares delayed contacts/claims/permits and carries the
roster and tactical orders, `SquadTactics` decides who is base of fire and who bounds, `MobSystem`
arbitrates per-unit behavior, and every NPC ultimately emits the same movement flags and
weapon/dig/grenade requests used by players. Do not add a second movement, damage, reload, or
terrain-edit path for mobs.

Squad membership is **not** fixed at spawn, and `SquadTactics` is where "spread out and leapfrog"
lives — one man per flank bounds while the rest suppress, and the leapfrog is emergent from "the man
farthest from the threat bounds next" rather than any hand-off state. Both are pure and tested in
`Server.Tests`. Two invariants there have bitten already: `MobBrain.AtCover` must not be treated as
terminal (an arrived NPC that can never move again is why nothing bounded), and a base of fire must be
allowed to run a cover query even though it does not want to advance (otherwise it stands in the open,
never goes set, and nobody in the squad is ever cleared to move). `ai track` draws the NPC debug
overlay; roles are server-only and deliberately not on the wire.

An actor's hittable volume is the capsule `PlayerMovement.Body` describes, via
`GunMath.PlayerHitDistance`. It was a single sphere at 0.5 m, which left a standing player's head and
shoulders unhittable by anyone and made peeking over cover invulnerable. Every entry in
`GunConfig.AimHeights` must stay inside that capsule — an AI must never aim at a point it can see and
cannot damage. `docs/networking/Shooting.md` carries the detail.

Navigation search is the exception to main-thread computation: `NavigationSystem` uses a prioritized
pool capped at eight workers and returns plain paths. Riptide messages, actor mutation, terrain edits,
and result installation stay on the main thread. Paths carry chunk-corridor revisions rather than
depending on the map-wide edit generation; reconstruction must tolerate terrain changing during an
optimistic worker read.

Client display configuration is also a composition-root invariant. Set the 1920x1080 back buffer
before `game.Run`, leave `Game.AutoLoadDefaultSettings` disabled, and use borderless desktop
fullscreen on SDL. Calling `ApplyChanges` from the post-device start callback can recreate the
resize/device-reset loop described under Performance targets above.

Design specs live in `docs/superpowers/specs/`, plans in `docs/superpowers/plans/`,
loose notes in `docs/scratchpad/`. `docs/networking/` explains the object replication,
movement and shooting paths end to end.

## Netcode at a glance

`Common/NetworkProtocol.cs` is the tuning surface. Port 7777 for Riptide, 7778 for the terrain
stream (`ChunkTransport.Port`, derived so there is one number to change); `TickRate` is 30 Hz and
**everything tick-related must derive from it** or client and server drift;
`InterpolationDelayTicks` is 3; `MaxRewindTicks` equals `TickRate`, i.e. the one-second accepted
age window for fire/throw requests and snapshot retention. Projectile collision runs forward and
does not currently rewind targets.

`SimulatedLatencySeconds` / `SimulatedJitterSeconds` fake inbound lag on the client only —
set them non-zero to reproduce lag bugs with both ends on this machine.

The `ushort` values in `ServerToClientId` / `ClientToServerId` are the wire protocol. Renumber
one and the matching handler silently stops firing — no error, just nothing happening.

Transport is Riptide. The server is authoritative: it re-steps a starved move queue with the
player's last intent forever (`GameWorld.Tick`), so a client that stops sending input leaves
its character running rather than standing still.

Two Riptide facts that cost real debugging time:

- **`MessageSendMode.Reliable` guarantees delivery but NOT order.** Every message must be
  independently applicable. Don't design anything that assumes arrival order. (Terrain used to be
  the example here; it now has its own ordered TCP stream — see below.)
- **`Message` and `PendingMessage` pool into unsynchronised static `List<>`s** —
  `if (pool.Count > 0) { pool[0]; pool.RemoveAt(0); }` with no lock. Safe for one peer on one
  thread; corrupts instantly with a server and a client creating messages concurrently. Symptoms were
  a truncated read on the far end ("N unread bits") and `ArgumentOutOfRangeException` inside
  `RetrieveFromPool`. **This is why singleplayer does not use Riptide at all.** It runs on
  `Common/Net`'s in-process transport, whose queues are locked and whose `Message` pool is
  `[ThreadStatic]`, which is what let `ServerHost` move onto its own thread. A remote client still
  uses Riptide, but then the two peers are in separate processes and share no pool. Never put a
  Riptide server and a Riptide client on separate threads of one process.
- `NetworkManager.Dispatch` runs handlers **on the network thread** when
  `SimulatedLatencySeconds` is 0. Anything it writes that the main thread also reads needs
  marshalling — `TerrainState` queues and drains in `Update()` for this reason.

## Terrain / chunks (in progress)

The current work is tracked in `docs/TODO.md`. Code sits in `Common/Voxel/`, with no Stride dependency,
so both ends share it.

**The server owns terrain and streams it; the client never generates any.** `WorldGen.Generate`
is server-side only (`GameWorld`), `ChunkTcpServer` sends it over a **dedicated TCP connection**, and
`Client/Simulation/TerrainState` holds what arrived.

**Terrain is NOT on Riptide.** `ChunkTransport` carries the reasoning: Riptide's reliable channel has
no congestion control, so the only throttle was a messages-per-tick constant, and raising it to load
terrain faster killed a *localhost* connection. A blocking TCP write is backpressure; there is no rate
constant in the path any more. A frame is a whole chunk column, since the 1225-byte datagram limit is
what forced slabs in the first place.

There is deliberately no client-side generator to fall back on, which is what
keeps the client from rendering a world the server hasn't sent — and what will keep it honest once
player edits mean terrain is no longer a pure function of a seed. Minecraft's model, for the same
reason: mutability, not secrecy.

**Meshing runs on worker threads** (`Client/Rendering/SectionMeshQueue.cs`). Two things make that safe
and both are load-bearing: `ChunkMap`'s lookup is a `ConcurrentDictionary`, and the dispatcher only
submits sections whose **whole 3×3 chunk neighbourhood has finished arriving**, because a chunk is
inserted into the map on its *first* slab and keeps being written until its last. `ChunkMesher` is
stateless apart from the caller's scratch buffer. `ChunkMeshFactory` stays on the main thread — it
creates GPU buffers, and off-thread resource creation is not worth gambling on this platform.

**Uploads are batched and buffers are reference counted**, and both are load-bearing rather than tidy:
one `Buffer.New` per section meant ~3,468 Vulkan allocations whose cost climbed to 11 ms each, and
nothing freed them because `Scene = null` doesn't release GPU memory. That was the real reason terrain
took 32 s to appear — see `docs/stride/code-only-runtime-and-assets.md` for the full measurement, and
note the residual growth is still unexplained.

The Bevy/Rust project at `/home/sebas/Projects/Demiurge` is the working reference this was
ported from — `src/chunks/{utils,mod,tilemap}.rs`. When the terrain math looks wrong, diff
against it before theorising, and note that `utils.rs` carries unit tests that double as the
spec for the coordinate transforms.

- A chunk is **16 × 16 × 128 voxels**, stored as 128 **lazily allocated slabs** — a null slab means
  "all of it is this one voxel". Most of a column is uniform air or uniform bedrock, so a typical chunk
  allocates ~8 of 128 and costs ~5 KB instead of 64 KB; at 1 km that is 21 MB per map instead of 248.
  Index it with `chunk[flatIndex]`, the same flat layout `ChunkTransforms.WorldVoxelIndex` produces.
  Generation and `ChunkWire.Decode` both write slab-at-a-time and call `FillSlab` for uniform ones —
  writing voxel by voxel materialises everything and then frees it, which measured at 300 MB of
  transient garbage. `Voxel` is **2 bytes**:
  `sbyte` quantized signed distance + `BlockType`. `ChunkIndex` is 2D, so a chunk spans the
  world's full height.
- **Meshing and rendering happen per 16³ SECTION** (`SectionIndex`), not per column. Storage is
  still one flat array per chunk — a section is a view into it — so indexing, edits and
  `ChunkIndex` are unaffected. An edit re-meshes 16³ voxels instead of 16×16×128, and each
  section frustum-culls on its own box.
- **The coordinate conventions live in the header comment of `Common/Voxel/ChunkTransforms.cs`.**
  Read that before touching anything positional; it is the only place they're written down.
- Heights come from `NoiseGen.GenerateHeightsForChunk` (`NoiseDotNet`), seed 100. It returns world
  heights, **padded one column on every side** so slope can be central-differenced at a chunk edge —
  index it through `ChunkTransforms.PaddedColumnIndexOf`, never by hand. Three noise fields (erosion,
  fbm detail, folded ridge) go through `TerrainShape`'s splines; see `docs/voxel/GENERATION.md`.
- **Terrain material communicates slope.** `DensityToMaterial` takes a slope. Grass extends through
  `PlayerMovement.MaxSlopeDegrees`; stone begins above the coupled 55-degree walkability threshold.
  Additive editor terrain uses grass as an automatic fill and derives this classification from the
  CSG shape gradient.
- **The bottom voxel plane is permanently solid** (`ChunkConstants.BedrockThickness`), enforced at
  every write. Two reasons in one invariant: you can't dig out of the world, and the lowest grid
  point any section *owns* is `WorldMinY`, so carving it away leaves a sign change on an edge
  nobody emits a quad for — a hole you see through.
- Edits are CSG on the field, not voxel assignment: `TerrainEdits` uses `min` for add and
  `max(d, -shape)` for subtract, and writes `Margin` past the shape because a voxel just outside a
  cut is now measured from the cut, not from the old surface. After spherical subtraction it scans
  only the 7³ affected neighborhood and removes fully enclosed components of at most four shallow
  solid samples. Boundary-connected, deeply solid, and larger components are protected; see
  DATA_MODEL.md.
- Textures come from `BlockTextures` (a `BlockType` → files manifest) through a triplanar shader
  with per-cell variant selection. A type with no entry draws the purple prototype texture, i.e.
  obviously-missing rather than a plausible wrong material.
- **`ChunkWire` encodes density and material as separate PLANES**, not interleaved — they have
  nothing in common statistically and interleaving defeats both schemes. Material goes as a palette
  plus run lengths or packed indices, whichever is smaller per slab; density goes as a 256-bit mask of
  the voxels that are NOT saturated, since `Voxel` only resolves ±2.54 voxels and the rest reconstruct
  from the material plane's own sign. Both fall back to raw, so a badly-compressing slab loses 0.4%
  rather than 100%. Cost is about `59·mixedSlabs + 1664` bytes, so **roughly independent of terrain
  roughness** — which is what stops mountains costing more to stream than plains.
- Terrain **collision** exists and is shared (`Common/Voxel/TerrainCollision.cs` +
  `PlayerMovement`); see `docs/voxel/COLLISION.md`. Still missing: LOD, per-player chunk tracking,
  view-distance meshing, and collision against anything but terrain. Collision carries a smoothed
  pushout normal and an exact cell-local surface normal; contacts above 45 degrees use the latter
  for the 55-degree standability check so saturated neighboring samples cannot make cliffs walkable.
- Human terrain docs are in `docs/voxel/`; keep them terse and put implementation-heavy notes here
  or in `docs/stride/`.

`docs/voxel/` has four docs, one per layer: **DATA_MODEL** (what a voxel is, and the wire format
derived from it), **GENERATION** (seed to height), **MESHING** (field to triangles), **COLLISION**
(field to contact). The two below are the ones with load-bearing surprises in them.

**`docs/voxel/DATA_MODEL.md` is the design for where this is heading** — a quantized
signed-distance field plus a material byte per voxel, why a dual method forces that rather than
block-type enums, and the chunk dimension/indexing/padding decisions. Read it before touching the
storage layer.

**`docs/voxel/MESHING.md` is the other half** — how the field becomes triangles. The load-bearing
fact: **surface nets and dual contouring are one algorithm** differing only in where the cell's
vertex goes (average of the edge crossings vs. a QEF solve). Both exist —
`ChunkMesher.GenerateMeshFromSurfaceNet` and `GenerateMeshDualContouring` are two entry points onto
one skeleton. `GenerateMesh` currently selects surface nets because its rounded placement suits dug
terrain better; DC is retained for comparison. Editor blocks must enclose an integer SDF sample
instead of relying on DC to rescue a boundary-only field. DC sharpens geometry but **not** shading,
which needs vertex splitting by crease angle. That doc also records the limitations the article's
author hit
afterwards — chunk LOD being the genuinely hard part, not the meshing.

### The rule that keeps the terrain math honest

**Never compute a block's world position twice.** `ConvertChunkCoordinatesAndBlockIndexToGlobalBlockCoordinates`
is the single index→world function; noise generation and rendering both go through it, so an
array slot cannot mean different places to the two of them. The porting bugs fixed in July 2026
were all a second, hand-rolled walk of the chunk drifting out of sync with it — a transpose
(x-major generation vs z-major decode) and a half-chunk offset at once.

Related invariants worth preserving:

- Blocks index z-major: `index = z * ChunkWidth + x`, the y = 0 slice of `y*256 + z*16 + x`.
- Negative coordinates use real floor division. This **diverges from the Rust**, which shifts
  and truncates — an approximation that misplaces exact negative multiples of the width, sending
  every negative chunk's first row and column into its neighbour. All of the reference's own test
  vectors still pass under real flooring.

## Assets

`docs/ASSET_LOADING.md` has the detail. Two stages: at build time the `SyncStrideGltfAssets`
MSBuild target runs `tools/GltfAssetGenerator` over `assets/**/*.gltf` to emit Stride asset
descriptors; at runtime you just `Content.Load<Model>(path)`. No SharpGLTF or image decoding
happens at runtime.

SDSL shaders live in `assets/shaders/` and are referenced by class name, not path — e.g.
`new ComputeShaderClassColor { MixinReference = "TestShader" }` resolves
`assets/shaders/TestShader.sdsl`.

## Two subsystems that are ours, not Stride's

**Debug drawing** — `Client/Rendering/LineRenderer.cs`, immediate mode: call `DrawLine`,
`DrawPolyline`, `DrawPoint`, `Circle2D` (and the `*2D` screen-space variants) every frame from
any script and they're re-issued each frame. 3D coordinates project through the static
`LineRenderer.Camera`; 2D coordinates are pixels centred on the screen. This is the tool for
things like the "debug draw chunk borders" TODO — reach for it before inventing anything.

**Audio** — `Client/Audio/SoundManager.cs` talks to OpenAL directly through Silk.NET,
deliberately bypassing Stride's audio, whose native layer deadlocks on this platform
(`docs/scratchpad/AUDIO.md`). Don't reintroduce `Stride.Audio`. The camera entity is the 3D
listener, resolved from services.

## Stride engine reference — check `docs/stride/` first

`docs/stride/` is our own Stride reference, written from the decompiled 4.3.0.2507
assemblies and cross-checked against this repo. **Look there before decompiling the
engine or searching the web** — it exists specifically to kill that cold-start cost.

- `scripts-and-lifecycle.md` — ScriptComponent/SyncScript/AsyncScript, update order, priorities
- `input.md` — keyboard/mouse API, edge vs level triggers, `Keys`, mouse lock and delta
- `entities-transforms-cameras.md` — entities, transforms, world matrices, cameras, projection
- `rendering-and-compositor.md` — graphics compositor, custom scene renderers, materials, lights, UI
- `code-only-runtime-and-assets.md` — this repo's composition root, terrain streaming bridge,
  generated runtime meshes, and asset-pipeline wiring
- `community-toolkit.md` — which helpers are CommunityToolkit vs core Stride, and what they do
- `physics-bepu.md` — Stride.BepuPhysics bodies, colliders, raycasts, impulses

If a fact is missing, decompile it rather than guessing, then **add it back to the
relevant doc**:

```bash
ilspycmd -t Stride.Engine.Processors.ScriptSystem \
  ~/.nuget/packages/stride.engine/4.3.0.2507/lib/net10.0/Stride.Engine.dll
ilspycmd -l class <dll>          # list types
# backtick generics need single quotes: -t 'Stride.Engine.EntityProcessor`2'
```

Assemblies: `~/.nuget/packages/<package>/4.3.0.2507/lib/net10.0/*.dll`, with XML doc
comments in `Stride.*.xml` beside them. Use the plain `net10.0` variants — this is Linux.

## Engine gotchas that have already cost real time

- **There is no `Enabled` switch on a script.** `ScriptComponent` derives from
  `EntityComponent`, not `ActivableEntityComponent`, so `script.Enabled = false` doesn't
  compile — and nothing would honour it anyway: `ScriptSystem.Update` schedules every
  registered sync script unconditionally, and `ScriptProcessor` only reacts to components
  being added/removed. To actually stop per-frame work, early-return on your own flag or
  remove the component.
- **Vulkan/Linux landmines** (all documented in `Client/Program.cs` comments):
  `FastTextRenderer` crashes, so `AddProfiler()` and `DebugTextSystem.Print` are
  unusable — use the UI/`HUD` path for on-screen text; particle rendering crashes
  (stride3d/stride#2496) and is disabled; SSR/`LocalReflections` needs a pixel format
  Vulkan lacks and is turned off; setting `IsFullScreen` before `Run()` throws in
  `InitDefaultRenderTarget` — use borderless windowed inside `Start()`.
- `Texture.Load` pulls in Windows-only `System.Drawing.Common`; decode with
  StbImageSharp and build textures via `Texture.New2D`.
- **Quaternion multiply is reversed** from Unity/GLM: Stride's `a * b` means "apply `a`, then
  `b`". Get it backwards and rotations pick up roll. See `DebugFlyCamera.cs` for a
  yaw/pitch camera written the correct way round.
- **`Input.IsKeyPressed` re-fires on OS key auto-repeat** — it is not a reliable one-shot for
  a key that gets held. Edge-detect `IsKeyDown` yourself for toggles.
- **`Input.MouseDelta` is anisotropic** (X over window width, Y over window height,
  separately). For free-look use `AbsoluteMouseDelta`.

## Working with Sebastian

- **He directs, you implement.** As of 2026-07-26. Write the code, build it, run the tests,
  report what happened. This replaced an earlier plans-only default, which was gated on him
  learning a given subsystem rather than being a blanket preference — so expect it back for
  the next thing he wants to build himself, and take him at his word when he says so.
- **Never `git commit`.** Leave changes unstaged for him to review and commit himself.
- **Verify visual changes by asking him to look**, not by screenshotting the game.
- Two-client local testing: an unfocused Stride window is throttled by the engine.
  Background-window stutter is not a netcode bug.

### Concepts freely, systems iteratively

This is a codebase Sebastian is learning in, so the *altitude* of help matters as much as its
correctness.

**Concepts** are single graspable ideas — "density is separate from material", "signed distance
encodes sub-voxel position", "a chunk is a fixed-size region of the world". They transfer by
explanation, and once held, the implementation usually follows intuitively. Explain these in
full, with code where it helps.

**Systems** are assemblages whose difficulty emerges from parts interacting — 3D chunking *plus*
palette compression *plus* filesystem streaming *plus* per-player server-side chunk tracking.
Explanation does not transfer a system; only building one does, and it gets refined by annealing
rather than foresight. Build them a step at a time rather than delivering one assembled, and
expect the shape to change between steps. This is not about settling for a worse design — the
destination should still be correct — it's that a system gets arrived at by living inside
successive versions of it. When he wants to build one himself he'll say so.

It isn't a hard binary, and detail at either altitude is welcome **when he asks for it**. The two
failure modes to actively avoid are **cognitive overload** and **premature optimization**:

- Don't answer a concept question with a system design. ("What chunk size and data structure?"
  wants a concept, not sections + cache analysis + a meshing strategy.)
- Don't optimize a step he has scoped as scaffolding or throwaway.
- When design must run ahead of code, mark plainly which parts belong to a later step. `docs/TODO.md`
  does this by ending each step with what is *deliberately not* in it; that marking is what lets
  a blueprint run ahead without reading as a to-do list.
