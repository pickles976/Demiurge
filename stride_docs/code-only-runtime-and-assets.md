# Code-only runtime and asset path

Project-specific Stride notes for DemiurgeSharp. These are not general engine docs; they are the
wiring facts that are easy to miss when starting from a code-only project instead of Game Studio.

Verified against repo source and Stride/CommunityToolkit 4.3.0.2507 / 1.0.0-preview.62.

---

## 1. Composition root

`Client/Program.cs` is the only composition root. There is no `.sdscene`, no compositor asset, and
no editor-authored service graph.

The top-level setup before `game.Run(...)` creates long-lived pure services:

- `NetworkManager`
- `PlayerRegistry`
- `ObjectRegistry`
- `TerrainState`
- optional in-process `ServerHost` for `--singleplayer`

`Start(Scene rootScene)` builds anything that needs Stride services or a live scene:

- `GraphicsCompositor` via `game.AddGraphicsCompositor()`
- UI stage via `compositor.AddCleanUIStage()`
- debug line renderer via `compositor.AddSceneRenderer(new LineSceneRenderer())`
- `ClientTerrain`, cameras, lights, HUD, view factories, OpenAL `SoundManager`
- per-entity `SyncScript`s for player camera/input/FX

`Update(Scene, GameTime)` is the per-frame root loop:

1. `localServer?.Step()` before client networking, so in-process server/client never touch
   Riptide's unsynchronised static message pools concurrently.
2. `network.Update()` pumps Riptide and delayed fake-latency deliveries.
3. `terrainState.Drain()` moves chunk messages from the network thread into the main-thread map.
4. `terrainView?.RebuildDirty()` meshes a time-budgeted slice of dirty sections.
5. local debug picking/physics code runs last.

Do not move scene construction above `Run`; Stride's scene system and graphics services are not in
the state this code expects until the CommunityToolkit root script calls `Start`.

---

## 2. Boundary map

The repo keeps three client layers:

| Layer | Files | Rule |
|---|---|---|
| Netcode | `Client/Netcode/NetworkManager.cs` | Socket -> decoded events only |
| Sim | `Client/Simulation/*` | Pure client state; no entities, no model loading |
| View | `Client/View/*`, `Client/Rendering/*` | Read sim, render entities, send no authoritative decisions |

The composition root is allowed to glue sim objects together. Example: `LinkOwned` in
`Program.cs` observes `ObjectRegistry` and `PlayerRegistry` events so the local player equips the
Hand-slot weapon and links its `PlayerStatus` object. That would be wrong inside a view script
because it is sim-to-sim wiring, not rendering.

Server-side gameplay stays behind `ServerHost`. `ServerHost` is deliberately the only public type
in `Server/DemiurgeServer.csproj`; singleplayer can host the real server without the client gaining
access to `GameWorld`, `ItemSystem`, `WeaponSystem`, or `ObjectReplication` internals.

---

## 3. Terrain streaming path

**Terrain does not travel over Riptide.** It has its own TCP connection on `ChunkTransport.Port`
(7778, one past the Riptide port). Riptide keeps gameplay; see "Why terrain left Riptide" below.

The server owns terrain:

`GameWorld` -> `WorldGen.Generate(ChunkMap)` -> `ChunkTcpServer` (accept thread + one writer thread
per client) -> `ChunkWire.Encode` -> framed TCP write

The client receives terrain:

`ChunkTcpClient` (reader thread) -> `TerrainState.Receive()` -> `TerrainState.Drain()` (main thread)
-> `ClientTerrain.MarkChunkDirty()` -> `ClientTerrain.RebuildDirty()`, which splits into
`Dispatch()` -> `SectionMeshQueue` (worker threads) -> `Collect()` -> `ChunkMeshFactory.Build()`

### Why terrain left Riptide

Gameplay and bulk transfer want opposite things, and the old path had no flow control anywhere.
Riptide's reliable channel provides delivery but no congestion control, so the only throttle was a
hand-picked `MessagesPerTick` constant — and a constant is not flow control. Raising it from 8 to 48
to speed up terrain **killed a localhost connection**: the server emitted its whole allowance unpaced
inside one frame, the client could not drain its socket while meshing, datagrams dropped, and
retransmits lengthened the frames that caused the drops. Riptide gave up after 15 failed reliable
attempts and reported "Poor connection".

A blocking TCP write *is* backpressure, so there is no rate constant in the new path at all.

### Important details

- A frame carries a **whole chunk column**, not a slab run. Riptide's 1225-byte datagram limit is
  what forced slabs; without it the slab cursor and its resume logic are gone.
- Framing is the new failure mode. Over UDP a corrupt datagram was one bad chunk; over a stream a
  wrong length desynchronises everything after it. `ChunkTransport.TryReadHeader` validates the
  length rather than trusting it, and the reader closes the connection on a bad one.
- The client presents a `Guid` from `WelcomeData.ChunkToken` as the first 16 bytes, because a TCP
  connection otherwise has no way to say which player it belongs to.
- `TerrainState.Receive()` is thread safe because it only queues. `ChunkMap` is mutated only by
  `Drain()` on the main thread.
- **Meshing runs on worker threads** (`SectionMeshQueue`), which is safe because of two things
  together: `ChunkMap`'s lookup is a `ConcurrentDictionary`, and `Dispatch()` only submits sections
  whose whole 3x3 chunk neighbourhood is complete, so no voxel a worker reads is still being written.
  `ChunkMesher` itself is stateless apart from the caller's scratch buffer.
- `ChunkMeshFactory` is **main thread only** and no longer meshes anything — it creates GPU buffers.
- `ClientTerrain` marks dependent neighbouring sections too, because a section mesh reads apron
  samples across section/chunk boundaries.
- Nothing in the TCP path touches a Riptide `Message`, which is the only reason it may use threads
  at all — see the pooling note on `ServerHost`.

The outer loaded ring often produces no mesh until its neighbour ring arrives. That is expected,
not a meshing failure.

---

## 4. Runtime-generated meshes

`Common/Voxel/MeshGeneration.cs` produces engine-agnostic `MeshData` using `System.Numerics`.
`Client/Rendering/ChunkMeshFactory.cs` is the conversion boundary into Stride buffers.

Current terrain mesh path:

1. Fill a reusable `Sample[]` scratch buffer for one `SectionIndex`.
2. Generate dual-contouring mesh data.
3. Split creases at `CreaseAngleDegrees = 50`.
4. Upload one vertex buffer and one index buffer.
5. Add one Stride `Mesh` per material submesh, all sharing those buffers and differing by
   `MeshDraw.StartLocation`, `DrawCount`, and `MaterialIndex`.
6. Set `Mesh.BoundingBox`. If bounds are omitted, Stride can silently cull the mesh away.

Mesh positions are section-local. The returned entity's transform carries the chunk X/Z origin and
section base Y.

Triplanar terrain materials do not use mesh UVs. The vertex format still includes `TEXCOORD0`
because the shader stream expects it, but `TerrainMaterials` binds a `Texture2DArray` directly to
`TriplanarTexture.VariantTextures` and the shader derives UVs from world position.

---

## 5. Content pipeline in this repo

GLTF model assets are generated at build time, not loaded dynamically at runtime.

- `DemiurgeSharp.csproj` target `SyncStrideGltfAssets` runs before `StrideCompileAsset`.
- The target executes `tools/GltfAssetGenerator`.
- The generator scans `assets/**/*.gltf`, emits `.sdtex`, `.sdmat`, `.sdm3d`, optional `.sdskel`
  and `.sdanim`, and rewrites `DemiurgeSharp.sdpkg`.
- Runtime code calls `Content.Load<Model>(contentPath)` through `GLTFLoader.LoadModel`.

Linux-specific build plumbing in `DemiurgeSharp.csproj` is load-bearing:

- `SetStrideNativeLibPathForLinux` sets `LD_LIBRARY_PATH` so the asset compiler can load Stride's
  native `.so` dependencies.
- `DeployGlslangValidator` copies `linux-x64/glslangValidator.bin` into the project root because
  Vulkan runtime shader compilation looks for it relative to the process working directory.

Audio is the exception. `assets/**/*.wav` files are copied as content and loaded directly by
`Client/Audio/SoundManager.cs` through OpenAL/Silk.NET, bypassing Stride's audio layer.

---

## 6. Code-only traps

- `game.Run(start:, update:)` blocks. Code after it runs at shutdown.
- `Create3DPrimitive(...)` returns a detached entity. `Add3DGround`, `Add3DCamera`, and
  `AddDirectionalLight` attach to the root scene themselves.
- `AddCleanUIStage()` replaces the post-processing object installed by `AddGraphicsCompositor()`.
  Any post-effect toggles must happen after it.
- The default compositor has no particle or instancing render feature. Add those explicitly and
  expect Linux/Vulkan particle rendering to crash in current Stride.
- `LineSceneRenderer` is appended as a bare scene renderer, not inside a `SceneCameraRenderer`, so
  it relies on the manually assigned static `LineRenderer.Camera`.
- `Texture.Load` is avoided for terrain textures because it pulls in Windows-only
  `System.Drawing.Common`; use `StbImageSharp` plus `Texture.New2D`.
