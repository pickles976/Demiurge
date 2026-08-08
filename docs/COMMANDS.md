# Developer And Editor Commands

The in-game terminal opens with backtick/tilde. Opening it captures gameplay input and releases the
mouse; Escape or backtick closes it. Shift+tilde enters `~` for relative coordinates. In a runtime
session, `F3` toggles the free camera.

Press `Tab` to complete command tokens. Block and item arguments complete to canonical IDs such as
`demiurge:stone` and `demiurge:sks`; when several candidates remain, press `Tab` again to list them.

Session and map commands are handled locally by the persistent client coordinator. Runtime `spawn`
and `equip` commands are sent to the authoritative server. Editor commands mutate only the local
source document and never travel over the network. `ai track` is a client-side view toggle and is
also answered locally, so it never reaches the server.

## Runtime Commands

Single-player enables world-changing client commands. A standalone server rejects commands received
from clients unless started with:

```bash
dotnet run --project Server/DemiurgeServer.csproj -- --allow-cheats
```

Commands accept an optional leading `/`.

```text
spawn mob [x z]
spawn pickup <item> [x z]
equip <@s|@actor-id> <item>
ai stats
ai track [off|on|beacons|facing|clustering]
net <seed|log>
```

Examples:

```text
spawn mob
spawn pickup demiurge:ppsh ~3 ~
equip @s demiurge:body_armor
equip @60002 demiurge:sks
ai track on
ai track beacons clustering
```

Without coordinates, spawn commands place the entity three metres in front of the issuer. Coordinate
components prefixed by `~` are relative to the issuer. Y always comes from the server's authoritative
terrain surface. Mob and player IDs share one actor-ID space; a successful mob spawn prints the ID to
use with `equip`, for example `actor ID @60000`. A pickup spawn prints a distinct replicated object
ID such as `object ID #1`; `equip` accepts actor IDs, not object IDs.

### `net` — in-process transport diagnostics

Answered on the client like `ai track`, and only meaningful when this session hosts its own server
(singleplayer, `session host`, or a playtest). Against a remote server the delivery decisions are
Riptide's, not ours, and the command says so rather than inventing an answer.

```text
net seed    the delivery seed for this session
net log     the last 256 delivery decisions, newest first
```

Singleplayer runs a deliberately hostile transport: the unreliable channel drops, reorders and
duplicates; the reliable channel reorders but never drops. This is not a bug to be tuned out. A
localhost socket essentially never misbehaves, so without it singleplayer cannot catch an ordering or
duplication assumption at all — it would simply pass, and the failure would surface later against a
real server. See `docs/superpowers/specs/2026-08-02-transport-parity-design.md`.

Quote **both** values when reporting a glitch. The seed fixes the delivery *policy*, not the traffic:
the message sequence depends on frame-to-frame input timing, so the seed alone will not replay a
session.

### `ai track` — NPC debug overlay

Draws every AI actor the client knows about. Layers combine, `off` clears them, and no argument
reports the current state. Actors are identified as NPCs by `ActorIds.IsMob` (id >= 60000) rather
than by a replicated flag, so nothing was added to the wire for it.

| Layer | Draws |
| --- | --- |
| `beacons` | A vertical beam per NPC in team colour, without depth testing, so it reads through terrain |
| `facing` | A ground ring and heading spoke, depth tested, for reading stance and bearing up close |
| `clustering` | A line between every pair of NPCs within 4 m — roughly what one burst or grenade covers, which is what bunching costs them |

`on` and `all` enable every layer. The overlay is drawn by `NpcTrackerScript` from the replicated
player registry and touches no simulation or network state; the toggle is process-wide, so it
survives session transitions and can be set before a session exists.

Runtime commands mutate the current server session only. They do not modify `source.json`, so
`map save` does not preserve a runtime-spawned mob or a weapon assigned with runtime `equip`.
Editor mob placements have a separate persistent `WeaponId`; existing placements without one
default to the active datapack's `npcPrimary` item.

Canonical item IDs:

```text
demiurge:sks
demiurge:ppsh
demiurge:mosin
demiurge:dp27
demiurge:shovel
demiurge:body_armor
demiurge:grenade
demiurge:mortar
```

Short aliases are accepted as input, but results always print canonical IDs. Add new canonical names
and aliases in a datapack item JSON file; never use `ItemType.ToString()` as external identity.

## Architecture

`GameCommandParser` in Common turns command text into typed command records. It validates item IDs,
actor selectors, finite coordinates, grammar, and the 256-character limit without depending on
Stride or server state.

The terminal sends the original text in a reliable `CommandRequest`. The server reparses it, applies
permission and rate limits, resolves the issuing actor and terrain position, and calls the same
`GameWorld`, `MobSystem`, and `ItemSystem` operations used by normal gameplay. A reliable
`CommandResult` returns output only to the issuer. The client queues results before touching UI state.

`ICommandWorld` is the test boundary around server mutations. Production uses `GameWorld`; server
tests use an in-memory implementation and verify command behavior without starting terrain generation,
network listeners, or a client.

Administrative equip replacement despawns the previous slot occupant. Normal E-to-equip still drops
the swapped item. This prevents repeated terminal commands from leaving unwanted pickups.

## Sessions And Maps

```text
session status
session editor <map-name>
session host <map-name>
session host <map-name> --build
session join <host>
session playtest
session playtest-networked

map list
map new <map-name>
map status
map save
map save-as <map-name>
map load <map-name>
map load <map-name> --discard
map recover
map discard-autosave
map validate
map bake
```

Maps live under `maps/<map-name>/`. `source.json` is the non-destructive editor document and
`runtime.dmap` is the complete, validated runtime package. `session host` rejects a missing or stale
bake. `session host <map> --build` validates, saves, bakes, starts the in-process multiplayer server,
and connects the local client. `session playtest` is the fast F4 toggle for the current editor
document. `session playtest-networked` runs the full save/load/stream/remesh path.

The editor autosaves after a quiet period. When `autosave.json` is newer than `source.json`, the
terminal reports it. `map recover` promotes the autosave to the explicit source while retaining the
old source backup; `map discard-autosave` removes it.

Launching directly into the editor creates the map when no source exists:

```bash
dotnet run -- --editor trench-test
```

## Editor Commands

```text
editor status
editor mode <terrain|block|object>

editor terrain operation <add|subtract>
editor terrain shape <sphere|box|organic>
editor terrain size <size>
editor terrain size <x> <y> <z>
editor terrain strength <0..1>
editor terrain material <block>

editor block <block>
editor block size <size>
editor block size <x> <y> <z>

editor object pickup <item>
editor object crate <item>
editor object mob
editor object spawn <spawn-id>
editor object clear
editor object list
editor object select <placement-id>
editor object equip <placement-id|selected> <weapon>

editor rotate <degrees>
editor undo
editor redo
```

In editor mode, press `1`, `2`, or `3` to switch directly to terrain, block, or object mode. The
active mode and these bindings are shown in the top-right editor status panel. `U` undoes, `Y`
redoes, and `R` rotates a selected object or named structure. Plain block brushes remain
axis-aligned. Mouse wheel changes terrain and block brush size.

Use `help <command>` for contextual terminal help. In particular, `help object` lists the object
selection commands and available item IDs. Placing an object reports its stable eight-character
placement ID. `editor object list` prints every placement, and Tab completes IDs for `select` and
`equip`. These are editor IDs, not runtime actor IDs prefixed with `@`.

`editor object crate <item>` places the same pickup as `editor object pickup`, drawn as a supply
crate instead of as the weapon and resting still on the ground rather than hovering and spinning.
Taking one gives the item inside, and from that moment it looks like an ordinary weapon — carried or
dropped.

Canonical blocks are `demiurge:grass`, `demiurge:dirt`, and `demiurge:stone`.
The default terrain fill is `demiurge:grass`, which enables automatic surface classification:
slopes through the 55-degree movement threshold are grass, and steeper slopes are stone. Selecting
another terrain material is an explicit override.

Structure capture and placement use highlighted cells:

```text
editor structure corner 1
editor structure corner 2
editor structure pivot
editor structure save <name>
editor structure select <name>
editor structure rotate <multiple-of-90>
editor structure mirror x
editor structure clear
```

Selected structures place as one grouped block transaction and undo in one operation.

Editor controls:

```text
WASD / mouse          fly and look
Space / Left Ctrl     fly up / down
Left Shift            speed boost
Left mouse            apply or place
Right mouse           remove a block
Mouse wheel           adjust terrain or block brush
R                     rotate selected object or named structure
F4                    toggle authoritative playtest
U / Y                 undo / redo
Delete                delete selected object
Escape                cancel selection or placement
Ctrl+S                save source
Ctrl+Shift+B           save and bake
```

`session playtest` and `F4` host the current in-memory map through the authoritative runtime stack
while reusing the editor camera and terrain renderer. Both playtest commands spawn the player at the
editor fly camera's exact position instead of at the map's player spawns, and that stays the spawn
point for the rest of the playtest. Runtime changes are temporary and affected
terrain chunks are restored from editor source on return. `session playtest-networked` saves and
bakes, creates a fresh runtime client, streams the terrain, and remeshes it; use that command to test
the complete persistence and network-loading path.

## Dedicated Server Console

The standalone server always trusts commands entered through its local stdin terminal. Client
permissions remain controlled by `--allow-cheats`.

```bash
dotnet run --project Server/DemiurgeServer.csproj
```

Type `help` for the runnable forms or `help <command>` for examples. `help spawn mob` and
`help spawn pickup` provide subcommand-specific help. `items` lists every canonical item ID and its
accepted aliases.

```text
help [command]
status
players
items
spawn mob <x> <z>
spawn pickup <item> <x> <z>
equip <@actor-id> <item>
map status
map load <map-name>
stop
```

Examples:

```text
spawn mob 10 -15
spawn pickup sks 0 0
players
equip @60000 ppsh
map load trench-test
```

Dedicated commands require absolute X/Z coordinates. The server derives Y from authoritative
terrain. The console has no body, so relative `~` coordinates and `@s` are invalid. `map load`
validates the new runtime package before disconnecting clients and rotating the world. EOF on stdin
does not stop the server. Successful spawn output labels the returned actor or object ID explicitly.
Runtime `spawn` and `equip` changes are discarded when the server stops or rotates maps.
