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

The server owns terrain:

`GameWorld` -> `WorldGen.Generate(ChunkMap)` -> `ChunkStreamer.Tick()` -> `ChunkSlabsData`

The client receives terrain:

`NetworkManager.ChunkSlabsReceived` -> `TerrainState.Receive()` -> `TerrainState.Drain()` ->
`ClientTerrain.MarkChunkDirty()` -> `ClientTerrain.RebuildDirty()` -> `ChunkMeshFactory.TryBuild()`

Important details:

- `ChunkWire` sends horizontal slabs, not whole chunks or 16^3 sections. One raw 16x16 slab is
  513 bytes including the kind byte, so it fits inside Riptide's payload budget.
- `ChunkStreamer` sends only `MessagesPerTick` messages per client. This is intentional: Riptide's
  reliable channel does not provide congestion control.
- Reliable messages are not ordered. Every `ChunkSlabsData` carries its chunk index, first slab,
  slab count, completion flag, and payload so it can be applied immediately.
- `TerrainState.Receive()` is network-thread safe because it only queues. The `ChunkMap` is mutated
  only by `Drain()` on the main thread.
- A chunk is not meshed until the server marks it `ChunkComplete`. Missing slabs default to air in
  the allocated array, but no view should observe partial chunks.
- `ClientTerrain` marks dependent neighbouring sections too, because a section mesh reads apron
  samples across section/chunk boundaries.
- `ChunkMeshFactory.TryBuild()` returns `false` when a needed neighbour is missing; callers must
  leave the section dirty and retry later.

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
