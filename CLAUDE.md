# DemiurgeSharp

Code-only Stride 4.3.0.2507 multiplayer game. net10.0, Linux, Vulkan backend.
There is no Game Studio project: the scene is assembled in code in `Client/Program.cs`,
which is also the composition root for all wiring.

## Build & run

```bash
dotnet build DemiurgeSharp.slnx
dotnet run --launch-profile singleplayer        # client + in-process server — USE THIS
dotnet run                                      # client only; connects to a server you started
dotnet run --project Server/DemiurgeServer.csproj   # standalone server
dotnet test DemiurgeSharp.slnx                  # xUnit suite (Common.Tests), headless
dotnet test --filter "Category!=Benchmark"      # ~1s; skips the pipeline benchmarks, which
                                                # generate the whole world and cost a few seconds
```

**Singleplayer is the normal way to test anything server-side** — one launch instead of two.
Profiles live in `Properties/launchSettings.json`; `client` is first so a bare `dotnet run` keeps
its old behaviour. The underlying flag is `--singleplayer`, so `dotnet run -- --singleplayer` also
works (the bare `--` matters; dotnet eats unknown flags first). It refuses to start rather than
falling back if the port is taken — silently attaching to an already-running server would mean
testing a stale build.

`Common` has no Stride dependency — it's plain `System.Numerics` — so anything in it is
testable without booting the engine. That's why the voxel coordinate maths has real tests and
the rest of the codebase doesn't; keep new pure logic in `Common` and it stays that way. Note
that `DemiurgeSharp.csproj` lives at the repo root and globs `**/*.cs`, so every sibling project
needs a `<Compile Remove="Dir/**/*.cs" />` line or the root build breaks on duplicate
assembly attributes.

If the build goes weird after dependency changes: `dotnet clean && dotnet restore --no-cache && dotnet build --no-incremental`.

Projects: `DemiurgeSharp.csproj` (client), `Server/DemiurgeServer.csproj`,
`Common/DemiurgeCommon.csproj`, `tools/GltfAssetGenerator`.

## Architecture

**Read `RECIPES.md` before changing gameplay code.** It is the map, not a tutorial:
Common is the wire, Server is truth, Client is Netcode → Sim → View with a strict
one-way flow, and Program.cs wires it all. It also carries step-by-step recipes for
adding a replicated component, an equippable item, a weapon, or a new trait — each one
a fixed list of append-only edits. Follow the recipe instead of re-deriving it; the
steps that are easy to forget are exactly the ones that shipped bugs before.

Wire rule worth repeating here: enum values and the `ComponentBundle` if-chain order
ARE the protocol. Append, never reorder, never delete — clients desync silently.

`Client/Program.cs` is the code-only composition root. Before `game.Run(...)` it creates
long-lived pure services (`NetworkManager`, registries, `TerrainState`, optional `ServerHost`);
`Start(Scene)` builds anything that needs Stride services or a live scene; `Update(Scene, GameTime)`
steps singleplayer, pumps networking, drains terrain, then dispatches dirty sections to the mesher
threads and uploads whatever they finished. Drain must run before RebuildDirty: Drain is the only
writer of chunk voxels, and dispatch only hands out chunks Drain has finished. More
detail in `stride_docs/code-only-runtime-and-assets.md`.

Design specs live in `docs/superpowers/specs/`, plans in `docs/superpowers/plans/`,
loose notes in `docs/scratchpad/`. `docs/networking/` explains the object replication,
movement and shooting paths end to end.

## Netcode at a glance

`Common/NetworkProtocol.cs` is the tuning surface. Port 7777 for Riptide, 7778 for the terrain
stream (`ChunkTransport.Port`, derived so there is one number to change); `TickRate` is 30 Hz and
**everything tick-related must derive from it** or client and server drift;
`InterpolationDelayTicks` is 3; `MaxRewindTicks` equals `TickRate`, i.e. one second of
lag-compensation rewind, matching the snapshot buffer's retention.

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
  thread; corrupts instantly with a server and a client creating messages concurrently. This is
  why `ServerHost` is **stepped from the client's `Update()`** in singleplayer rather than given a
  background thread. Symptoms were a truncated read on the far end ("N unread bits") and
  `ArgumentOutOfRangeException` inside `RetrieveFromPool`. Do not "optimize" that back onto a
  thread without patching Riptide.
- `NetworkManager.Dispatch` runs handlers **on the network thread** when
  `SimulatedLatencySeconds` is 0. Anything it writes that the main thread also reads needs
  marshalling — `TerrainState` queues and drains in `Update()` for this reason.

## Terrain / chunks (in progress)

The current work, tracked in `TODO.md`. Code sits in `Common/Voxel/`, with no Stride dependency,
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
took 32 s to appear — see `stride_docs/code-only-runtime-and-assets.md` for the full measurement, and
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
- **Steep columns are bare stone.** `DensityToMaterial` takes a slope, and the threshold is 25
  degrees — chosen against the measured slope distribution, not derived from the movement limit. See
  GENERATION.md for why that derivation had to be abandoned.
- **The bottom voxel plane is permanently solid** (`ChunkConstants.BedrockThickness`), enforced at
  every write. Two reasons in one invariant: you can't dig out of the world, and the lowest grid
  point any section *owns* is `WorldMinY`, so carving it away leaves a sign change on an edge
  nobody emits a quad for — a hole you see through.
- Edits are CSG on the field, not voxel assignment: `TerrainEdits` uses `min` for add and
  `max(d, -shape)` for subtract, and writes `Margin` past the shape because a voxel just outside a
  cut is now measured from the cut, not from the old surface.
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
  view-distance meshing, and collision against anything but terrain.
- Human terrain docs are in `docs/voxel/`; keep them terse and put implementation-heavy notes here
  or in `stride_docs/`.

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
one skeleton. DC sharpens geometry but **not** shading, which needs vertex splitting by crease
angle. That doc also records the limitations the article's author hit afterwards — chunk LOD being
the genuinely hard part, not the meshing.

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

## Stride engine reference — check `stride_docs/` first

`stride_docs/` is our own Stride reference, written from the decompiled 4.3.0.2507
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
- When design must run ahead of code, mark plainly which parts belong to a later step. `TODO.md`
  does this by ending each step with what is *deliberately not* in it; that marking is what lets
  a blueprint run ahead without reading as a to-do list.
