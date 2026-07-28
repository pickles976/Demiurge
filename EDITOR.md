# In-Game Map Editor Implementation Plan

## Goal

Build a solo, in-engine map editor with the feel of Halo Forge without copying Forge's controls.
The author flies through the live Stride scene, selects a tool and palette entry through the
existing terminal, sees a world-space preview under the crosshair, and clicks to edit.

The first version has three tools:

1. **Terrain brush** for smooth additive and subtractive CSG.
2. **Block placement** for grid-snapped, Minecraft-style construction using the block palette.
3. **Object placement** for pickups, mobs, and player spawn points.

The editor saves a non-destructive source map and bakes a runtime map. The runtime map is ordinary
authoritative voxel chunks plus spawn metadata. Runtime digging, meshing, collision, terrain
streaming, and edit replication continue to use the existing systems.

## Product Decisions

- Editing is initially local and single-user. There is no collaborative editor protocol.
- The editor and runtime are separate disposable sessions inside the same Stride client process.
  `--editor` selects the initial session, and terminal commands can transition later.
- Editor mode does not connect to a game server and does not start the in-process server.
- The existing free-fly camera movement is reused, but editor mode owns it directly instead of
  toggling it with `F3`.
- The terminal is the editor's menu for the first version. There is no object browser or properties
  panel yet.
- World-space previews and selection outlines are still required. The terminal selects a tool;
  the viewport shows what clicking will do.
- Editor undo and redo apply only to authoring actions. Player digging in a runtime match has no
  undo history.
- Source maps retain terrain operations and placed objects. Runtime maps contain baked chunks and
  spawn metadata, with no editor history.
- Version 1 maps use the current `WorldGen.Min`, `WorldGen.Max`, meshable bounds, world height, and
  apron. Supporting arbitrary dimensions would also require sending bounds to clients and removing
  static `WorldGen` bounds from chunk streaming and LOD. That is a separate runtime feature.
- Block mode means Minecraft-style interaction and grid snapping. It initially bakes unit box CSG
  stamps into the existing SDF. It does not introduce a second cube renderer.
- Exact axis-aligned cube silhouettes are a later decision. Surface nets will round or relax some
  corners. Adding a dedicated block geometry layer would change the runtime renderer and is outside
  the first implementation.
- Switching to play starts the normal in-process multiplayer server with the baked map and connects
  the local client over the existing sockets. Other clients can join that hosted session normally.
- The standalone dedicated server has its own stdin terminal. It executes the same typed world
  commands as players, plus server lifecycle and map commands, without requiring a connected player.

## Non-Goals For The First Version

- Multiplayer or collaborative editing
- Runtime undo for digging or destruction
- A Forge-style radial menu, object browser, or property inspector
- Controller support
- Visual scripting
- Terrain smoothing, erosion, paint-only brushes, or arbitrary mesh import
- Rotation and scaling gizmos
- Exact per-triangle cube rendering
- Editing a running authoritative multiplayer match
- Persisting the undo stack across editor restarts

## User Workflow

### Launch

```bash
dotnet run -- --editor trench-test
```

If the source file exists, the editor loads it. If it does not exist, the editor creates a new
document using the current terrain generator, seed, bounds, and apron.

Editor mode starts in free flight:

```text
WASD                 horizontal movement
Space / Left Ctrl    up / down
Left Shift           speed boost
Mouse                look
Tilde                open or close terminal
Ctrl+S               save source map
Ctrl+Shift+B         bake runtime map
Ctrl+Z / Ctrl+Y      undo / redo
Escape               cancel active placement or clear selection
Delete               delete selected editor object
```

The runtime meaning of `F3` remains unchanged. Editor mode is already a fly camera, so `F3` has no
editor action in the first version.

### Terminal Commands

Session and map commands are always local:

```text
session status
session editor <map-name>
session host <map-name>
session host <map-name> --build
session join <host>

map list
map new <map-name>
map status
map save
map save-as <map-name>
map load <map-name>
map load <map-name> --discard
map validate
map bake
```

`session editor trench-test` leaves the current game session, loads
`maps/trench-test/source.json`, and enters the editor. `session host trench-test` leaves the editor
or current local session, starts a real in-process server from
`maps/trench-test/runtime.dmap`, and connects the local client to it. The server binds the normal
ports, runs the normal authority and replication paths, and accepts additional clients.

`session host <map> --build` is the fast playtest path from the editor. It validates, saves, and
bakes the current document first, then transitions only if all three steps succeed. Without
`--build`, hosting rejects a missing or stale runtime bake instead of silently playing old data.

`session join <host>` joins an existing server. A joining client cannot select that server's map;
the remote server remains authoritative.

`map load` means source-map loading and is available only in an editor session. It refuses to replace
a dirty document. `--discard` makes data loss explicit. Loading a baked `runtime.dmap` into the
editor is not supported because the baked file intentionally has no brush or structure history.
A later `map import-runtime` command may create a new source baseline from baked chunks.

Use one local `editor` command tree for tool settings. These commands never travel through Riptide:

```text
editor status
editor mode terrain
editor mode block
editor mode object

editor terrain operation add
editor terrain operation subtract
editor terrain shape sphere
editor terrain shape box
editor terrain size 3
editor terrain size 3 2 5
editor terrain strength 1
editor terrain material demiurge:dirt

editor block demiurge:stone

editor object pickup demiurge:ak47
editor object pickup demiurge:body_armor
editor object mob
editor object spawn default
editor object clear

editor rotate 90
editor undo
editor redo
```

Short aliases can be added after the command tree is stable. Terminal output always prints canonical
names, matching the current `ItemCatalog` behavior.

`editor status` prints the current mode, selected palette entry, brush settings, and selection.
`map status` prints source path, dirty state, source hash, last bake hash, and last bake path.

The terminal remains available in runtime mode. Its existing `spawn` and `equip` commands continue
to be server-authoritative runtime commands. Session and map commands are local coordinator
commands. Editor tool commands use a separate parser and executor so authoring placements cannot
accidentally become temporary runtime entities.

### Dedicated Server Terminal

The standalone server reads commands from stdin:

```text
help
status
players

spawn mob <x> <z>
spawn pickup <item> <x> <z>
equip <@actor-id> <item>

map status
map load <map-name>
stop
```

World commands reuse `GameCommandParser`, `ServerCommandService`, `GameWorld`, `MobSystem`, and
`ItemSystem`. The console must not grow a second implementation of spawn or equip behavior.

The dedicated console is an administrator command source:

- It is trusted regardless of `--allow-cheats`.
- `--allow-cheats` continues to control world-changing commands received from network clients.
- Console commands are logged with `source=console`.
- Client commands remain rate-limited; local console commands are not.

The console has no player body, position, look direction, or actor identity. Therefore:

- `@s` is invalid from the console.
- Relative `~` coordinates are invalid from the console.
- Spawn commands require absolute X and Z coordinates.
- `equip` requires an explicit actor ID.
- Y still comes from authoritative terrain through `SurfaceQuery`.

This keeps the shared grammar while making context-dependent behavior explicit. A future
`execute as` command can provide actor-relative context without inventing an invisible console
player.

`map load <map-name>` is a dedicated-server map rotation:

1. Resolve and fully validate `maps/<map-name>/runtime.dmap` before touching the active world.
2. Announce the shutdown when a client notification mechanism exists.
3. Disconnect current clients.
4. Stop and dispose the current `GameWorld`, chunk server, and Riptide server.
5. Construct a fresh `GameServer` from the selected runtime map.
6. Bind the normal ports and resume ticking.

If validation fails, the current session keeps running. Map rotation does not attempt to transfer
players, runtime digging, items, or mobs between maps. Clients reconnect and receive the new
authoritative world normally.

Console input must not mutate the world from an input thread. A background stdin reader only places
complete lines into a `ConcurrentQueue<string>`. `ServerHost.Run` drains that queue before simulation
ticks and executes commands on the same thread as `GameWorld.Tick`. End-of-file on stdin does not
stop a server running under a service manager; `stop`, Ctrl+C, or process supervision handles
shutdown.

## Viewport Interaction

### Shared Targeting

All tools cast from the editor camera through the center reticle. The ray has no player reach limit.
It is bounded by the editable map bounds.

Create a pure `EditorTargeting` helper that converts a `TerrainHit` into:

- The solid cell on the inside of the hit surface.
- The adjacent air cell on the outside of the hit surface.
- The world-space center and bounds of either cell.

The conversion must use `floor(hit +/- normal * epsilon)`, not rounding. Unit tests must cover
negative coordinates, exact integer boundaries, steep surfaces, and hits on chunk seams.

The target preview uses `LineRenderer`, which already provides immediate-mode world-space lines:

- White: valid target
- Red: invalid target
- Cyan: selected object
- Yellow: pending move destination

### Terrain Brush Mode

The crosshair ray finds the terrain surface. The viewport draws the brush footprint and a simple
wireframe sphere or box.

```text
Left mouse            apply selected operation
Left mouse drag       continue one brush stroke
Right mouse           temporarily invert add/subtract
Mouse wheel           adjust uniform brush size
Shift + mouse wheel   adjust brush strength
```

A press-to-release gesture is one undo transaction. Dragging must not add one stamp every rendered
frame. Sample the stroke by world distance:

- Add the first dab immediately.
- Add another dab after the cursor has moved at least 25 percent of the smallest brush extent.
- Interpolate missed dabs when the camera moves far between frames.
- Cap work per frame and carry remaining interpolated dabs forward.

This makes brush density independent of framerate and avoids flooding the mesher during a stutter.
All dabs in one stroke share operation, shape, size, strength, and material.

### Block Placement Mode

Block mode uses the adjacent air cell as the placement target and the solid-side cell as the removal
target.

```text
Left mouse       place the selected block
Left mouse drag  place across newly entered cells
Right mouse      remove the targeted placed block
```

The preview is a unit wireframe cube at the exact grid cell that will change. Dragging remembers
cells already visited during the current gesture, so a stationary cursor cannot repeatedly place
the same block.

Each block is represented in the source map as an editor block placement. Evaluation turns it into
an additive axis-aligned SDF box:

```text
cell minimum:  (x, y, z)
CSG center:    (x + 0.5, y + 0.5, z + 0.5)
half extent:   (0.5, 0.5, 0.5)
material:      selected BlockType
```

Removing a block removes that source placement and reevaluates affected chunks. It does not apply a
subtractive inverse brush. This preserves terrain that existed before the block was placed and
handles overlaps correctly.

Block placement is legal only inside editable bounds and above the permanent bedrock plane. The
first version may place blocks intersecting other blocks in the same cell only by replacing that
cell's placement in one undoable transaction.

### Object Placement Mode

The selected object archetype comes from the terminal. The viewport highlights the adjacent air cell
where the object anchor will be placed.

```text
Left mouse on terrain       place selected archetype
Left mouse on editor object select it
Left mouse after selection  move it to the highlighted cell
Right mouse or Escape       cancel move / clear selection
Delete                      delete selection
R                           rotate selection or pending object by 90 degrees
Shift+R                     rotate by 15 degrees
```

Selection raycasts editor placement bounds and terrain, taking the nearest valid result. The first
version uses a stable cell-sized selection proxy instead of depending on model mesh raycasts.

Object anchors are explicit per placement kind:

- Pickup: bottom-center of the target cell, with the item model's existing cosmetic offset.
- Mob: feet at the support surface inside the target cell.
- Player spawn: feet position plus yaw. It renders only as an editor marker.

The preview must show the selected model when cheap to do so and always show the target cell. A
wireframe proxy is an acceptable first milestone before translucent ghost materials exist.

Clicking an existing object selects it without creating a new object. Once selected, the object
remains in its old position while a ghost or outline follows the target. The next valid left click
commits one move command. Escape leaves the original placement unchanged.

## Input Ownership

`ClientInputState` currently carries only `TerminalOpen`. Replace the growing set of independent
booleans with explicit ownership:

```csharp
public enum ClientInputOwner
{
    Gameplay,
    Terminal,
    RuntimeFreeCamera,
    Editor,
}
```

The input state exposes the current owner and an editor sub-mode. Only the owner polls gameplay
mouse buttons and movement keys.

Required behavior:

- Opening the terminal from editor mode releases the mouse and remembers that the editor owned it.
- Closing the terminal restores editor mouse lock.
- Runtime terminal behavior stays the same.
- Gameplay scripts early-return unless gameplay owns input.
- `DebugFlyCameraScript` remains the runtime `F3` implementation.
- `EditorCameraScript` reuses or extracts the movement/look math but does not pretend to be the
  runtime debug camera.

Extract the shared camera math into a small helper instead of making one script inherit from the
other. The scripts have different mode transitions even though their movement is identical.

## Architecture

### Session Boundary

There are two session types in one long-lived client executable:

```text
Runtime:
  Network -> TerrainState -> ClientTerrain
  Player position drives terrain LOD

Editor:
  EditorDocument -> EditorTerrainSession -> ClientTerrain
  Editor camera position drives terrain LOD
```

`Client/Program.cs` creates rendering that truly belongs to the process, a persistent terminal, and a
`ClientSessionCoordinator`. The coordinator owns exactly one `IClientSession`:

```csharp
public interface IClientSession : IDisposable
{
    ClientSessionKind Kind { get; }
    void Start(Scene scene);
    void Update(GameTime time);
}
```

Concrete sessions:

```text
RuntimeClientSession
  owns NetworkManager, ChunkTcpClient, optional ServerHost, registries,
  gameplay views, runtime terrain state, and ClientTerrain

EditorClientSession
  owns EditorSession, source document, editor terrain state, ClientTerrain,
  editor camera, previews, and placement views
```

Editor sessions skip:

- `ServerHost`
- `NetworkManager.Connect`
- `ChunkTcpClient.Connect`
- player and object replication factories
- gameplay HUD, weapon input, digging, and shooting scripts

An editor session still creates:

- Stride rendering and lighting
- `TerrainState` or a narrowed shared terrain presentation state
- `ClientTerrain`
- `LineRenderer`
- terminal UI
- editor camera, controller, previews, and local placement views

Do not keep growing top-level conditionals in `Client/Program.cs`. Split composition into:

```text
Client/Sessions/ClientSessionCoordinator.cs
Client/Runtime/RuntimeClientSession.cs
Client/Editor/EditorClientSession.cs
Client/Program.cs                    process setup and initial-session arguments only
```

Both session classes receive the `Game` and own their setup, update, event subscriptions, scene
entities, services, and disposal.

### Session Transitions

Terminal commands request a transition; they do not tear down the current session from inside the
terminal script's input callback. The coordinator executes the request at a safe point in the main
update loop.

Transition states are explicit:

```text
Active -> Stopping -> Loading -> Starting -> Active
                                  |
                                  +-> IdleWithError
```

The persistent terminal remains available in `IdleWithError`, so a failed map load or port bind does
not require restarting the game.

Runtime-session disposal order:

1. Stop accepting gameplay input.
2. Unsubscribe registry, network, and UI events.
3. Disconnect and dispose `ChunkTcpClient`.
4. Disconnect the local `NetworkManager`.
5. Dispose `ClientTerrain` and join mesher workers.
6. Remove and dispose session-owned scene entities and GPU resources.
7. Remove or replace session-owned services.
8. Stop the owned `ServerHost`, if this client hosted it.

Editor-session disposal order:

1. Cancel pending brush or placement gestures.
2. Refuse the transition if the document is dirty unless the command saved it or explicitly
   discarded it.
3. Stop editor input.
4. Dispose `ClientTerrain` and placement preview resources.
5. Remove editor scene entities and services.
6. Release the source document and undo history.

Do not reuse `NetworkManager`, registries, terrain states, or view factories across runtime
sessions. Constructing fresh session objects is less error-prone than trying to reset every event,
sequence number, queue, and network token.

Leaving a locally hosted game disconnects all other players because the authoritative server is
stopping. Switching to the editor is not collaborative and never carries runtime terrain digging
back into the source document.

### Project Boundary

Authoring logic should remain headless and testable:

```text
Editor.Core/
  DemiurgeEditor.Core.csproj
  EditorDocument.cs
  EditorCommands.cs
  EditorCommandParser.cs
  EditorHistory.cs
  EditorTargeting.cs
  EditorTerrainEvaluator.cs
  EditorSpatialIndex.cs
  SourceMapSerializer.cs
  StructureDocument.cs

Editor.Core.Tests/
  DemiurgeEditor.Core.Tests.csproj

Client/Editor/
  EditorClientSession.cs
  EditorCameraScript.cs
  EditorControllerScript.cs
  EditorTerminalDispatcher.cs
  EditorPreviewRenderer.cs
  EditorPlacementViewFactory.cs
```

`Editor.Core` references `Common` and has no Stride or Riptide dependency. Add both sibling project
folders to the root client project's `<Compile Remove>` list, as required by the repository globbing
rule in `CLAUDE.md`.

Runtime map serialization belongs in `Common/Maps` because the editor writes it and the server reads
it:

```text
Common/Maps/RuntimeMap.cs
Common/Maps/RuntimeMapSerializer.cs
Common/Maps/RuntimeMapValidation.cs
Common/Maps/MapPathResolver.cs
```

### Terminal Dispatch

`DeveloperTerminalScript` currently owns a hard-coded local command dictionary and otherwise sends
text to the server. Introduce:

```csharp
public interface ITerminalCommandDispatcher
{
    TerminalCommandResult Execute(string commandLine);
    IReadOnlyList<string> Help();
}
```

Runtime uses a dispatcher that handles `clear`, `echo`, and `help` locally, then sends game commands
through `NetworkManager`. Editor mode uses a local dispatcher backed by a pure typed
`EditorCommandParser` and `EditorSession`.

The persistent dispatcher is a chain:

```text
Process commands: clear, echo, help
Session commands: session status/editor/host/join
Map commands: map list/new/status/save/save-as/load/validate/bake
Active session:
  Runtime -> NetworkManager for spawn/equip and future server commands
  Editor  -> EditorCommandParser for tool commands
```

Add `MapPathResolver` in `Common/Maps` as the only component allowed to turn safe map slugs into
paths under `maps/`. Both the editor and dedicated server use it, so the server does not reference
`Editor.Core`.

Add an editor `MapRepository` over that resolver. It:

- Lists maps with source/bake availability and source/bake hashes.
- Opens source, autosave, backup, and runtime paths.
- Determines whether a runtime bake is stale relative to its source.
- Provides atomic save and publish destinations.

The dedicated server uses `MapPathResolver.RuntimePath(name)` and
`RuntimeMapSerializer`; it never reads `source.json`.

The editor parser follows the existing command architecture:

```text
text -> typed editor command -> validation -> EditorSession mutation -> result text
```

Keep tokenization shared if useful, but do not add editor cases to `GameCommandParser`. Runtime
commands require server authority and actor context; editor commands mutate a local source document.

### Server Command Sources

Refactor `ServerCommandService` to execute against an explicit source:

```csharp
public enum ServerCommandSourceKind
{
    Player,
    Console,
}

public readonly record struct ServerCommandSource(
    ServerCommandSourceKind Kind,
    ushort? ActorId);
```

Network requests create a player source and retain permission and rate-limit checks. The dedicated
terminal creates a console source and writes the returned result directly to stdout.

Execution resolves context after parsing:

```text
Player source:
  may use @s, omitted spawn positions, and relative coordinates

Console source:
  requires explicit actor selectors and absolute coordinates
```

Add the process-level console dispatcher separately:

```text
Server/DedicatedServerConsole.cs
  stdin reader and main-thread queue
  help/status/players/stop/map commands
  delegates spawn/equip to ServerCommandService
```

`ServerHost.Run` owns this dispatcher because `ServerHost.Step` is also used by singleplayer and must
not consume the client process's stdin. Expose world mutations through `GameServer` methods that are
called only while the server thread is draining console commands.

### Terrain Presentation

`ClientTerrain` should continue rendering a `ChunkMap` and receiving dirty-region notifications.
Avoid teaching it about editor operations.

Introduce a narrow terrain presentation contract:

```csharp
public interface IClientTerrainSource
{
    ChunkMap Map { get; }
    event Action<ChunkIndex>? ChunkCompleted;
    event Action<Vector3, Vector3>? RegionEdited;
    bool FootprintComplete(LodSection section);
}
```

Runtime `TerrainState` implements it with its existing network queues. Editor terrain state starts
with complete chunks and raises the same events after evaluator swaps. `ClientTerrain` keeps its
current meshing, batching, dirty-section, and LOD behavior.

Rename the `RebuildDirty` parameter from `playerPosition` to `lodFocus`:

- Runtime passes the local player position, preserving current behavior.
- Editor passes the editor camera position, because the camera is the author's inspection point.

### Editor Document

The source document stores current authoring intent, not the session undo log:

```csharp
public sealed class EditorDocument
{
    public int SchemaVersion;
    public Guid MapId;
    public string Name;
    public BaseTerrainDefinition BaseTerrain;
    public List<TerrainStroke> TerrainStrokes;
    public Dictionary<Int3, BlockPlacement> Blocks;
    public List<EditorPlacement> Placements;
}
```

Use explicit DTO vector types rather than relying on reflection-based serialization of
`System.Numerics.Vector3`.

`BaseTerrainDefinition` records:

- Generator ID, initially `demiurge:terrain-v1`
- Seed, initially `100`
- Inclusive chunk bounds
- Meshable bounds and apron requirements
- World height and chunk-format compatibility values

`TerrainStroke` records:

- Stable `Guid`
- Monotonic sequence number
- Add or subtract
- Sphere or box
- Extents
- Strength
- Fill material
- Ordered world-space dabs

`BlockPlacement` records:

- Stable `Guid`
- Integer cell coordinate
- Canonical block ID
- Sequence number for deterministic material precedence
- Optional group ID for a structure placement

`EditorPlacement` records:

- Stable `Guid`
- Kind: pickup, mob, or player spawn
- Canonical archetype ID
- Integer anchor cell
- Yaw
- Optional group ID
- Kind-specific properties through versioned typed fields, not an unbounded string dictionary

Use canonical source IDs:

```text
demiurge:grass
demiurge:dirt
demiurge:stone

demiurge:ak47
demiurge:awp
demiurge:glock
demiurge:body_armor

demiurge:mob
demiurge:spawn/default
```

Add a `BlockCatalog` modeled after `ItemCatalog`. Source files store canonical strings so enum
renames do not corrupt maps. Runtime terrain continues storing the append-only `BlockType` byte.

### Terrain Evaluation

The editor owns one authoritative preview `ChunkMap`.

To build or rebuild a chunk:

1. Generate its base chunk from `BaseTerrainDefinition`.
2. Query all terrain strokes whose affected bounds overlap the chunk plus required CSG margin.
3. Replay them in sequence order through `TerrainEdits`.
4. Query block placements overlapping the chunk and replay unit box additions in sequence order.
5. Collapse uniform slabs.
6. Atomically replace the chunk reference in the preview `ChunkMap`.
7. Raise a dirty-region notification for the replaced bounds.

Maintain spatial indexes:

```text
ChunkIndex -> ordered terrain stroke IDs
ChunkIndex -> block placement IDs
spatial cell -> editor placement IDs
```

An authoring edit reevaluates only chunks touched by its old or new bounds. Removing or moving an
operation uses the union of both sets.

Build replacement chunks away from the map observed by mesher workers, then swap references. A
worker already reading an old chunk may finish a stale job; the existing dirty/in-flight behavior
must schedule the replacement mesh afterward. Add a per-section generation number if testing shows
that stale completions can temporarily overwrite a newer mesh.

Do not reevaluate the whole world after each click. Do not apply inverse CSG for undo.

### Undo And Redo

Use the command pattern in `Editor.Core`:

```csharp
public interface IEditorCommand
{
    string Description { get; }
    EditorChange Apply(EditorDocument document);
    EditorChange Revert(EditorDocument document);
}
```

`EditorChange` reports changed terrain bounds, block cells, placements, and validation warnings.
`EditorSession` reevaluates or refreshes only those results.

Initial command types:

- `AddTerrainStrokeCommand`
- `PlaceBlocksCommand`
- `RemoveBlocksCommand`
- `AddPlacementCommand`
- `MovePlacementCommand`
- `DeletePlacementCommand`
- `PlaceStructureCommand`

One mouse gesture creates one command. A block drag stores all changed cells and their previous
placements. A terrain stroke owns all of its dabs. Moving an object records old and new placement
values.

Rules:

- Applying a new command clears redo.
- History is bounded by estimated memory, with a default such as 128 MiB.
- Save does not clear history.
- Load clears history.
- Bake does not enter history because it does not change the source document.
- Autosave does not enter history.
- Runtime digging never instantiates `EditorHistory`.

### Source Map Persistence

Use versioned JSON for source maps. It is easy to inspect, diff, migrate, and recover while authoring
data remains relatively sparse.

```text
maps/<map-name>/source.json
maps/<map-name>/autosave.json
maps/<map-name>/runtime.dmap
maps/<map-name>/structures/
```

Save rules:

- Serialize to a temporary file in the same directory.
- Flush and atomically rename over the destination.
- Keep one previous successful save as `source.json.bak`.
- Mark the document clean only after the rename succeeds.
- Reject duplicate IDs, non-finite values, invalid extents, unknown catalogs, out-of-bounds cells,
  and unsupported schema versions.
- Sort operations and placements deterministically before serialization.
- Never serialize internal dictionaries or runtime-only caches.
- Compute a canonical source-content hash after serialization and store it in the runtime map when
  baking.

Autosave after a quiet period and no more often than once per minute. The terminal reports when a
newer autosave exists; it does not silently replace the explicit source file.

Load rules:

- `map load` runs validation before replacing the active document.
- A failed load leaves the current document and history untouched.
- A successful load clears undo/redo and rebuilds editor terrain from the new source.
- Dirty documents require `map save`, `session host --build`, or explicit `--discard`.
- A source map and runtime map share `MapId`; a name alone is never accepted as proof that a bake
  belongs to a source.

### Runtime Map Format

The baked file is a compact binary package:

```text
Header
  magic
  format version
  map ID
  map name
  chunk/world compatibility values
  editable and meshable bounds
  chunk count
  placement count
  source content hash

Chunk records, sorted by (x, z)
  chunk x
  chunk z
  payload length
  ChunkWire payload for all 128 slabs

Placement records
  kind
  canonical or numeric runtime identity
  position
  yaw
  typed properties

Footer
  content hash
```

Reuse `ChunkWire` for each full chunk. Add deterministic chunk enumeration or snapshot APIs to
`ChunkMap`; do not expose its internal `ConcurrentDictionary`.

The bake pipeline:

1. Validate the source document.
2. Evaluate all chunks, including the required apron.
3. Validate bedrock and material/density invariants.
4. Convert editor placements into runtime spawn records.
5. Write a temporary runtime package.
6. Read the package back and verify its hash and record counts.
7. Atomically replace `runtime.dmap`.

The runtime package contains no generator dependency and no editor operation list.

Version 1 writes bounds and chunk compatibility values into the header for validation, but requires
them to equal the current runtime constants. This lets `ChunkTcpServer` and `TerrainLod` keep using
their existing fixed bounds. A later variable-size-map feature would thread loaded bounds through
the welcome message, chunk streamer, client terrain completeness checks, and LOD collector.

### Minimal Runtime Integration

The server gets one new initialization seam:

```text
No --map argument:
  WorldGen.Generate(terrain)
  existing hard-coded spawn setup remains as fallback

--map maps/trench-test/runtime.dmap:
  RuntimeMapSerializer.Load(...)
  use baked chunks
  instantiate baked pickups, mobs, and spawn points
```

Thread `ServerOptions` from `Server/Program.cs` and `ServerHost` into `GameWorld`. Construct the
terrain before `MobSystem`, `WeaponSystem`, `TerrainSystem`, or `ChunkTcpServer` starts using it.

After initialization:

- `ChunkTcpServer` streams the same `ChunkMap`.
- `TerrainSystem` applies runtime digging exactly as it does now.
- Clients receive the same `ChunkWire` data.
- `TerrainState`, `ClientTerrain`, meshing, collision, and raycasts are unchanged.
- Runtime edits remain `TerrainEditData` broadcasts.

Map metadata replaces the hard-coded spawn content only when a runtime map is supplied:

- Player spawn points replace `(0, 0)`.
- Pickup placements replace constructor pickup calls.
- Mob placements replace the two constructor mob calls.
- Missing required player spawn points fail map validation before the server binds its port.

For the first PVP test, choose player spawns round-robin from valid map spawn points. Spawn selection
policy can later become team-aware without changing the file's terrain representation.

`session host <map>` constructs `ServerHost` with the same runtime map path and then constructs a
fresh local runtime client. It is multiplayer, not an editor preview:

- The server binds ports `7777` and `7778`.
- The local client connects through Riptide and the TCP chunk stream.
- Additional normal clients can connect.
- Server authority, movement, weapons, digging, object replication, and late-join catch-up all run.
- Returning to `session editor <map>` tears this server down and reloads the source map from disk.

The standalone server keeps the equivalent command-line path:

```bash
dotnet run --project Server/DemiurgeServer.csproj -- --map maps/trench-test/runtime.dmap
```

Dedicated-server map rotation is initiated from the dedicated process's stdin terminal, never from
an ordinary client terminal. Remote administration can later add authentication and delegate to the
same queued rotation operation.

## Structure Library

Structures build on block mode after basic editing and baking work.

```text
structures/<canonical-name>.json
```

A structure document contains:

- Schema version
- Canonical name
- Integer pivot cell
- Sparse block cells relative to the pivot
- Canonical block IDs
- Optional embedded editor placements
- Bounds and content hash

Initial terminal workflow:

```text
editor structure corner 1
editor structure corner 2
editor structure save demiurge:bunker
editor structure select demiurge:bunker
editor structure rotate 90
editor structure mirror x
editor structure clear
```

`corner 1` and `corner 2` capture the currently highlighted cells. Saving copies block placements in
the inclusive box relative to the chosen pivot.

Placing a structure creates one `PlaceStructureCommand`. It expands the structure into normal source
block placements with one shared group ID. The map does not retain a live dependency on the library
file, so changing or deleting the library entry cannot silently change existing maps. Undo removes
the entire group as one action.

Runtime bake sees only the expanded block placements and remains unaware of structures.

## Validation

`editor validate` reports errors and warnings.

Errors block baking:

- Source schema or generator version is unsupported.
- Map bounds or chunk constants do not match the version 1 runtime.
- A catalog ID is unknown.
- Terrain or block data lies outside writable bounds.
- Bedrock is not solid.
- No player spawn point exists.
- A spawn capsule intersects solid terrain.
- A chunk required by the apron is absent.
- Density says air while a solid material is assigned, or vice versa.
- A runtime record exceeds configured count or size limits.

Warnings permit baking:

- Spawn points are very close together.
- A pickup or mob has no support surface.
- Multiple placements share one anchor cell.
- Terrain operation count or per-chunk overlap exceeds an editor budget.
- A structure contains no blocks.
- A map has no pickups or mobs.

Validation output includes stable placement or operation IDs so the terminal can select the offending
entry in a later improvement.

## Performance Rules

- Reevaluate affected chunks only.
- Spatially index strokes and blocks by chunk.
- Group a drag into one command and one dirty-region notification per affected region.
- Coalesce repeated dirty sections before submitting meshing work.
- Build replacement chunks off to the side and swap them into the map.
- Keep GPU creation on the main thread through the existing `ChunkMeshFactory`.
- Keep source serialization and runtime bake off the render-critical path.
- Show bake progress through terminal output or a small status line.
- Use the editor camera as LOD focus so distant terrain does not remain detailed while the author
  flies elsewhere.
- Put soft warnings on operation count and maximum operations overlapping one chunk.

Long authoring sessions can accumulate many terrain strokes. The first response is indexing and
chunk-local replay. If profiling shows source evaluation becoming slow, add an explicit
`editor compact` operation:

- Bake existing terrain intent into a new source baseline.
- Clear old terrain strokes and block placements only after confirmation.
- Preserve current visible output exactly.
- Start a new undo history.

Compaction is destructive to old authoring intent and must never happen automatically.

## Testing Strategy

### Headless Tests

Add tests in `Editor.Core.Tests` for:

- Editor command parsing and canonical catalog output
- Target cell selection at positive, negative, integer, and chunk-boundary coordinates
- Terrain stroke apply, undo, and redo
- Overlapping add/subtract strokes with undo
- Block placement over generated terrain followed by removal
- Overlapping block materials and deterministic replay order
- One drag becoming one undo command
- Moving and deleting placements
- Source JSON round trip and stable ordering
- Source schema rejection and migration
- Structure capture, rotation, placement, and grouped undo
- Runtime bake/load round trip
- Chunk payload equality before and after runtime package round trip
- Deterministic bake hash from identical source documents
- Validation of bedrock, catalogs, bounds, and spawn clearance

Continue running existing `Common.Tests` and `Server.Tests` to catch runtime terrain and command
regressions.

Add `Server.Tests` coverage for:

- Player and console command-source context
- Console rejection of `@s`, relative coordinates, and omitted spawn coordinates
- Console spawn and equip using the same `ICommandWorld` operations as network commands
- Console commands bypassing `--allow-cheats` without changing client permissions
- Stdin reader queueing without mutating `GameWorld` off-thread
- Invalid map rotation preserving the active world
- Valid map rotation disposing the old world and starting the requested map
- EOF on stdin leaving the dedicated server running

### In-Engine Verification

For each vertical slice:

1. Launch a new editor map.
2. Fly across positive and negative chunk coordinates.
3. Open and close the terminal repeatedly and verify mouse ownership.
4. Place across a chunk seam and inspect the rebuilt geometry.
5. Undo and redo while a mesh job is in flight.
6. Save and load another source map without restarting the process.
7. Bake and run `session host <map>`.
8. Connect a second client and a late joiner.
9. Dig the baked terrain in runtime and verify all clients agree.
10. Return to `session editor <map>` and verify runtime digging did not alter the source.
11. Repeat editor/host transitions and verify ports, threads, entities, and event subscriptions do
    not accumulate.
12. Verify the editor source and undo system are absent from runtime logs and allocations.
13. Start the standalone server and issue `spawn`, `equip`, and `map load` through stdin.
14. Reconnect clients after a dedicated-server map rotation and verify late-join state.

## Implementation Phases

Each phase ends with a build, automated tests, and a runnable vertical slice.

### Phase 1: Runtime Map Package

- [ ] Add `Common/Maps` runtime DTOs, validation, and binary serializer.
- [ ] Add safe deterministic chunk enumeration.
- [ ] Reuse `ChunkWire` for complete chunk records.
- [ ] Add runtime package round-trip and corruption tests.
- [ ] Add `ServerOptions.MapPath`.
- [ ] Load a runtime map before server systems start.
- [ ] Preserve `WorldGen.Generate` and current hard-coded content when no map is supplied.
- [ ] Verify two clients and a late joiner receive a loaded map.

Deliberately absent: editor mode, source maps, undo, and interactive tools.

### Phase 2: Editor Shell

- [ ] Split `Program.cs` into process setup, coordinator, runtime session, and editor session.
- [ ] Define `IClientSession` ownership and disposal contracts.
- [ ] Make the terminal process-owned so it survives session transitions.
- [ ] Add transition state and `IdleWithError` recovery.
- [ ] Prove a runtime session can be disposed and recreated without stale events or services.
- [ ] Add `--editor <source-path>` argument handling.
- [ ] Add `Editor.Core` and `Editor.Core.Tests`.
- [ ] Introduce the client terrain presentation interface.
- [ ] Load generated terrain directly into an editor terrain state.
- [ ] Add always-active editor fly camera.
- [ ] Use editor camera position as terrain LOD focus.
- [ ] Refactor terminal dispatch and add `editor status`.
- [ ] Add the small mode/status text and center reticle.

Deliberately absent: map mutations. This phase proves editor startup, rendering, camera, terminal,
input ownership, and clean shutdown.

### Phase 3: Source Document, Save, And Undo Foundation

- [ ] Define versioned source DTOs and canonical block IDs.
- [ ] Add `MapRepository` with safe map-name resolution.
- [ ] Implement atomic source save, load, backup, and autosave.
- [ ] Add `map list`, `new`, `status`, `save`, `save-as`, and guarded `load`.
- [ ] Add canonical source hashing and stale-bake detection.
- [ ] Implement typed editor command parser and executor.
- [ ] Implement bounded undo and redo history.
- [ ] Build terrain stroke and block spatial indexes.
- [ ] Add deterministic source round-trip tests.

Deliberately absent: object placement and structures.

### Phase 4: Terrain Brush Vertical Slice

- [ ] Add terrain brush settings and terminal commands.
- [ ] Add unlimited-range editor terrain raycast.
- [ ] Draw sphere and box brush previews.
- [ ] Record distance-sampled strokes.
- [ ] Evaluate and swap only affected chunks.
- [ ] Group each drag into one undo command.
- [ ] Verify overlapping add/subtract undo without inverse brushes.
- [ ] Verify edits at chunk and section seams.

Deliberately absent: smoothing and material-only painting.

### Phase 5: Block Placement Vertical Slice

- [ ] Add tested solid-side and air-side cell targeting.
- [ ] Add block palette terminal command.
- [ ] Draw valid and invalid cell previews.
- [ ] Place, drag-place, replace, and remove source blocks.
- [ ] Replay blocks as unit SDF box stamps.
- [ ] Group drag operations for undo.
- [ ] Verify all current block materials bake and render.

Deliberately absent: a dedicated cube renderer and structure capture.

### Phase 6: Object Placement And Spawn Metadata

- [ ] Define typed pickup, mob, and player-spawn source placements.
- [ ] Add object palette commands.
- [ ] Add local placement preview views.
- [ ] Raycast editor selection proxies.
- [ ] Place, select, move, rotate, and delete.
- [ ] Validate support and spawn capsule clearance.
- [ ] Bake placements into runtime records.
- [ ] Replace hard-coded runtime content only for loaded maps.
- [ ] Verify PVP spawning, pickups, and mobs with two clients.

Deliberately absent: arbitrary runtime object properties and transform gizmos.

### Phase 7: Bake From Editor

- [ ] Evaluate all source chunks deterministically.
- [ ] Validate the complete map.
- [ ] Write, verify, and atomically publish `runtime.dmap`.
- [ ] Add `map validate` and `map bake`.
- [ ] Print progress and final content hash.
- [ ] Store the source map ID and source content hash in the runtime package.
- [ ] Make stale and mismatched runtime packages impossible to host accidentally.
- [ ] Compare editor preview chunks byte-for-byte with loaded runtime chunks.

Deliberately absent: in-process transition into the baked map.

### Phase 8: Session Switching And Multiplayer Playtest

- [ ] Add `session status`.
- [ ] Add `session editor <map>`.
- [ ] Add `session host <map>`.
- [ ] Add `session host <map> --build`.
- [ ] Add `session join <host>`.
- [ ] Queue transitions and execute them at a safe main-loop point.
- [ ] Refuse dirty editor transitions unless saved, built, or explicitly discarded.
- [ ] Tear down and recreate all session-owned network, terrain, scene, and service state.
- [ ] Keep terminal history and transition output across sessions.
- [ ] Recover to `IdleWithError` after a failed load, bind, or connect.
- [ ] Verify another client can join the hosted map.
- [ ] Verify editor -> host -> editor -> host repeatedly without leaked ports, threads, or entities.

Deliberately absent: dedicated-server stdin commands and collaborative editing.

### Phase 9: Dedicated Server Console

- [ ] Add a background stdin line reader and main-thread command queue.
- [ ] Add explicit player and console command-source contexts.
- [ ] Reuse `GameCommandParser` and `ServerCommandService` for spawn and equip.
- [ ] Require explicit absolute positions and actor IDs from console.
- [ ] Add `help`, `status`, `players`, and `stop`.
- [ ] Add `map status` and transactional `map load`.
- [ ] Prevalidate a runtime package before stopping the active world.
- [ ] Recreate `GameServer` and `GameWorld` without restarting the process.
- [ ] Preserve client `--allow-cheats` behavior while treating local console as administrator.
- [ ] Verify EOF, Ctrl+C, graceful stop, failed rotation, and repeated valid rotations.
- [ ] Verify clients reconnect and receive the newly loaded map.

Deliberately absent: authenticated remote administration and state transfer between maps.

### Phase 10: Named Structures

- [ ] Define the versioned structure format and catalog.
- [ ] Add two-corner volume capture.
- [ ] Add pivot selection.
- [ ] Add structure select, rotate, and mirror commands.
- [ ] Draw a structure bounds preview.
- [ ] Place structures as grouped block transactions.
- [ ] Undo a whole placement in one action.
- [ ] Verify maps remain valid after the source structure file is removed.

Deliberately absent: live-linked prefab updates.

### Phase 11: Authoring Safety And Playtest Polish

- [ ] Add autosave recovery prompt through the terminal.
- [ ] Add dirty-document warning on exit.
- [ ] Add operation-count and per-chunk overlap diagnostics.
- [ ] Add cancellable background bake with progress.
- [ ] Add `editor compact` only after profiling justifies it.
- [ ] Profile rapid brush movement, block dragging, saving, and baking.

## Acceptance Criteria

The editor is ready for initial map production when:

- A user can launch directly into a source map and fly anywhere in it.
- The terminal selects terrain, block, and object tools without a dedicated editor menu.
- Every mode shows the exact target before a click changes the document.
- Terrain strokes, block drags, object placement, movement, and deletion undo as one gesture each.
- Saving and reloading a source map preserves visible output and placement identities.
- `map load` can switch source maps without restarting and protects dirty work.
- Baking the same source twice produces the same runtime content hash.
- `session host <map> --build` saves, bakes, and starts a real multiplayer host.
- `session editor <map>` returns from a game session to the source editor without importing runtime
  digging.
- A standalone server loads the baked map with the current chunk streaming path.
- The dedicated server accepts spawn and equip commands through stdin without a connected command
  issuer.
- A valid dedicated `map load` rotates to another baked map, while an invalid load leaves the
  current world running.
- Two clients and a late joiner see the same terrain, pickups, mobs, and spawn points.
- Players can dig the baked terrain with the existing runtime system.
- Runtime mode does not allocate editor history, parse source maps, or evaluate editor CSG.

## Prior Art

Halo Forge is useful here as product inspiration rather than as a control specification: it is an
in-game map creation environment with placeable objects, prefabs, bots, object naming, and solo
undo/redo. Demiurge's first editor intentionally takes only the direct in-world authoring feel and
keeps its first interface terminal-driven.

Reference: [Halo Infinite Forge Overview](https://support.halowaypoint.com/hc/en-us/articles/10581874119828-Halo-Infinite-Forge-Overview)
