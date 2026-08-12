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
1 as a rifleman, and creates 16 PPSh/SKS/Mosin NPCs per team around the authored team spawns.
While waiting for a respawn wave you can pick a class — Rifleman, Marksman or Assault — with the
number keys or by clicking; every kit also carries a shovel and two grenades. `kill` in the terminal
is the quick way to get to that screen.
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
editor mode structure
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
1 / 2 / 3 / 4         terrain / block / object / structure mode
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
```

## Structure Editor

Structures are reusable clusters of blocks — a bunker, a bridge, a revetment — authored once and
placed into any map. They live in a shared library beside the maps, not inside one:

```text
structures/<name>.json
```

Launch the structure editor with its own flag, or `session structure` from the terminal:

```bash
dotnet run -- --structure-editor
```

It is the same editor against a different world: a flat 100x100 m debug pad with a gridded floor,
and nothing else. The pad is generated rather than built, so it never ends up inside a structure you
capture. Nothing about the world is saved — it is not a map, has no player spawn, and is never
baked. Relaunching gives you a fresh pad, and the only thing that leaves it is a saved structure.

Build something on the pad in block mode, then name it:

```text
editor structure save bunker
```

That is the whole capture step. There is no region to mark out: everything on the pad is the
structure, so its bounds are the bounding box of the blocks you placed, and its anchor is the base
of that box at its horizontal centre — which is what makes rotation turn it in place under the
cursor. The debug floor is generated terrain rather than blocks, so it is never part of what you
capture.

Place it from any map editor session, in structure mode (press `4`):

```text
editor structure list
editor structure select bunker   also switches you to structure mode
editor structure rotate 90       or press R
editor structure mirror x
```

The structure is drawn as a ghost at the cursor — rotation and mirroring included — and turns red
where it will not fit. Left mouse places it as one grouped transaction, and `U` undoes the whole
group in one step. `editor structure clear` deselects it and puts you back to capturing.

Press `Tab` in the terminal to complete structure names from the library.

## Developer Commands

The in-game terminal opens with backtick/tilde. In runtime mode, `F3` toggles the free camera.

Runtime commands:

```text
spawn mob [x z]
spawn pickup <item> [x z]
equip <@s|@actor-id> <item>
team <@s|@actor-id> <team>
kill [<@s|@actor-id>]
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
session structure [name]
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
- [AI_TODO.md](AI_TODO.md): authoritative remaining AI and navigation work
- [docs/NAVIGATION.md](docs/NAVIGATION.md): navigation model, workers, recovery, diagnostics, and tests
- [docs/RECIPES.md](docs/RECIPES.md): checklists for adding replicated gameplay features
- [docs/EDITOR.md](docs/EDITOR.md): editor design, formats, lifecycle, and acceptance criteria
- [docs/COMMANDS.md](docs/COMMANDS.md): terminal command reference
- [docs/DATAPACKS.md](docs/DATAPACKS.md): JSON item, weapon, ballistics, and compatibility format
- [docs/voxel/](docs/voxel/): voxel data, generation, meshing, and collision
- [docs/networking/](docs/networking/): replication and gameplay message flows
- [docs/stride/](docs/stride/): Stride-specific runtime and rendering findings

The code-only Stride setup follows the
[Stride Community Toolkit guide](https://stride3d.github.io/stride-community-toolkit/manual/code-only/create-project.html#example-code).
