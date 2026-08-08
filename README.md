# DemiurgeSharp

DemiurgeSharp is a code-only Stride 4.3 multiplayer game built on .NET 10. It uses an
authoritative dedicated server, streamed smooth-voxel terrain, first-person combat, and an in-engine
map editor. The project currently targets Linux and Vulkan; there is no Stride Game Studio project.

## Prerequisites

- .NET 10 SDK
- A Vulkan-capable GPU and driver
- Linux is the actively tested platform

Build and run the ordinary fast test suite:

```bash
dotnet restore
dotnet build DemiurgeSharp.slnx
dotnet test DemiurgeSharp.slnx --filter "Category!=Benchmark&Category!=Integration"
```

Run the full-system pathfinding scenarios only when working on that feature:

```bash
dotnet test Server.Tests/DemiurgeServer.Tests.csproj --filter "Category=Integration"
```

When dependency state needs a clean rebuild:

```bash
dotnet clean
dotnet restore --no-cache
dotnet build DemiurgeSharp.slnx --no-incremental
```

## Run The Game

### Single Player

Single player starts a real authoritative server inside the client process and connects through the
normal network paths. It currently loads the authored `conquest` source map, puts the player on team
1 with the datapack's default Mosin, and creates 16 PPSh/SKS/Mosin NPCs per team around the authored
team spawns.
This is the fastest integrated AI, gameplay, and server test:

```bash
dotnet run --launch-profile singleplayer
```

The equivalent explicit form is:

```bash
dotnet run -- --singleplayer
```

### Dedicated Server And Client

Start the server in one terminal:

```bash
dotnet run --project Server/DemiurgeServer.csproj
```

Start a client in another terminal:

```bash
dotnet run
```

The client connects to `127.0.0.1:7777`; terrain streams over port `7778`. To permit connected
clients to issue world-changing developer commands:

```bash
dotnet run --project Server/DemiurgeServer.csproj -- --allow-cheats
```

Load a baked map at server startup:

```bash
dotnet run --project Server/DemiurgeServer.csproj -- \
  --map maps/trench-test/runtime.dmap
```

The dedicated server has an stdin console. Type `help` for all commands or `help <command>` for
command-specific grammar and examples. Useful commands include `status`, `players`, `items`,
`spawn`, `equip`, `map load`, and `stop`.

## Map Editor

Launch directly into a source map:

```bash
dotnet run -- --editor trench-test
```

The editor loads `maps/trench-test/source.json`, or creates a new document when it does not exist.
Open the terminal with backtick/tilde and choose an editing mode:

```text
editor mode terrain
editor mode block
editor mode object
```

Typical workflow:

```text
map status
map save
map validate
map bake
session playtest
```

`session playtest`, or `F4`, starts an authoritative in-process server directly from the current
in-memory editor terrain. It reuses the existing camera and terrain renderer, so entering play is
fast. You spawn at the fly camera's exact position — including after dying — rather than at the
map's player spawns, so play starts wherever you were looking. Runtime digging, spawns, and
equipment are temporary; edited chunks are restored from the source document when you return.

Run `session playtest-networked` when validating the complete shipping path. It saves and bakes,
loads `runtime.dmap`, streams terrain to a fresh runtime client, and remeshes it. Run the same command
again to return to the editor.

Editor controls:

```text
1 / 2 / 3             terrain / block / object mode
WASD / mouse          fly and look
Space / Left Ctrl     move up / down
Left Shift            speed boost
Left mouse            terraform or place
Right mouse           inverse terrain operation or remove block
Mouse wheel           change terrain or block brush size
R                     rotate selected object or named structure
F4                    toggle authoritative playtest
U / Y                 undo / redo
Delete                delete selected object
Escape                cancel selection
Ctrl+S                save source
Ctrl+Shift+B           save and bake
```

Source maps are editable JSON. Runtime maps are complete binary packages:

```text
maps/<name>/source.json
maps/<name>/autosave.json
maps/<name>/runtime.dmap
maps/<name>/structures/
```

## Developer Commands

The in-game terminal opens with backtick/tilde. In runtime mode, `F3` toggles the free camera.

Runtime commands:

```text
spawn mob [x z]
spawn pickup <item> [x z]
equip <@s|@actor-id> <item>
```

Successful mob spawns print an actor ID such as `@60000`; pass that value to `equip`. Pickup
spawns print a network object ID such as `#1`. Runtime spawns and equipment changes are temporary
session state and are not written by `map save`. Editor placements instead use stable eight-character
IDs: `editor object list` prints them, and `editor object equip <placement-id> <weapon>` persists a
mob weapon in the source and runtime bake. Existing mobs use the active datapack's NPC default.

Session commands:

```text
session status
session editor <map-name>
session host <map-name> [--build]
session join <host>
session playtest
session playtest-networked
```

Map and editor commands are documented in [docs/COMMANDS.md](docs/COMMANDS.md), including terrain
brush settings, block and object palettes, structures, autosave recovery, item aliases, and the
dedicated-server console.

## Project Layout

```text
Common/             shared protocol, voxel math, movement, commands, runtime map format
Common/Navigation/  deterministic traversal, A*, goals, paths, and terrain corridor stamps
Common/Ai/          testable contact, cover, hearing, and strategic planning logic
Server/             authoritative simulation, replication, terrain streaming, server console
Server/Ai/          commander, squads, tactical behaviors, navigation workers and followers
Client/             Stride composition, networking, simulation mirrors, rendering, input
Editor.Core/        engine-independent source documents, undo/redo, validation, baking
Common.Tests/       shared logic and voxel tests
Server.Tests/       authoritative command and gameplay tests
Editor.Core.Tests/  editor persistence, history, structures, validation, bake parity
assets/             models, textures, shaders, sounds
```

The main dependency direction is:

```text
Common <- Server
Common <- Editor.Core <- Client
Common <- Client
```

The server is authoritative. The client follows `Netcode -> Simulation -> View`; do not mutate view
state directly from network callbacks. `Common` and `Editor.Core` avoid Stride dependencies so their
logic remains headless and testable.

## Further Reading

- [docs/README.md](docs/README.md): complete documentation index
- [CLAUDE.md](CLAUDE.md): architecture invariants and implementation guidance
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): current process, authority, AI, and data-flow boundaries
- [docs/NAVIGATION.md](docs/NAVIGATION.md): navigation model, workers, recovery, diagnostics, and tests
- [docs/BARITONE.md](docs/BARITONE.md): time-costed pathfinding plan and performance constraints
- [docs/RECIPES.md](docs/RECIPES.md): checklists for adding replicated gameplay features
- [docs/EDITOR.md](docs/EDITOR.md): editor design, formats, lifecycle, and acceptance criteria
- [docs/COMMANDS.md](docs/COMMANDS.md): terminal command reference
- [docs/DATAPACKS.md](docs/DATAPACKS.md): JSON item, weapon, ballistics, and compatibility format
- [docs/voxel/](docs/voxel/): voxel data, generation, meshing, and collision
- [docs/networking/](docs/networking/): replication and gameplay message flows
- [docs/stride/](docs/stride/): Stride-specific runtime and rendering findings

The code-only Stride setup follows the
[Stride Community Toolkit guide](https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code).
