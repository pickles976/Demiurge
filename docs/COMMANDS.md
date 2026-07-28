# Developer And Editor Commands

The in-game terminal opens with backtick/tilde. Opening it captures gameplay input and releases the
mouse; Escape or backtick closes it. Shift+tilde enters `~` for relative coordinates. In a runtime
session, `F3` toggles the free camera.

Session and map commands are handled locally by the persistent client coordinator. Runtime `spawn`
and `equip` commands are sent to the authoritative server. Editor commands mutate only the local
source document and never travel over the network.

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
```

Examples:

```text
spawn mob
spawn pickup demiurge:glock ~3 ~
equip @s demiurge:body_armor
equip @60002 demiurge:ak47
```

Without coordinates, spawn commands place the entity three metres in front of the issuer. Coordinate
components prefixed by `~` are relative to the issuer. Y always comes from the server's authoritative
terrain surface. Mob and player IDs share one actor-ID space; a successful mob spawn prints the ID to
use with `equip`.

Canonical item IDs:

```text
demiurge:ak47
demiurge:awp
demiurge:glock
demiurge:body_armor
```

Short aliases are accepted as input, but results always print canonical IDs. Add new canonical names
and aliases in `Common/ItemCatalog.cs`; never use `ItemType.ToString()` as external identity.

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
and connects the local client.

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
editor terrain shape <sphere|box>
editor terrain size <size>
editor terrain size <x> <y> <z>
editor terrain strength <0..1>
editor terrain material <block>

editor block <block>

editor object pickup <item>
editor object mob
editor object spawn <spawn-id>
editor object clear

editor rotate <degrees>
editor undo
editor redo
```

In editor mode, press `1`, `2`, or `3` to switch directly to terrain, block, or object mode. The
active mode and these bindings are shown in the top-right editor status panel.

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
Mouse wheel           adjust terrain brush
Delete                delete selected object
Escape                cancel selection or placement
Ctrl+S                save source
Ctrl+Shift+B           save and bake
Ctrl+Z / Ctrl+Y       undo / redo
```

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
spawn pickup ak47 0 0
players
equip @60000 glock
map load trench-test
```

Dedicated commands require absolute X/Z coordinates. The server derives Y from authoritative
terrain. The console has no body, so relative `~` coordinates and `@s` are invalid. `map load`
validates the new runtime package before disconnecting clients and rotating the world. EOF on stdin
does not stop the server.
