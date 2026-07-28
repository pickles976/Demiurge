# Developer Commands

The developer terminal opens with backtick/tilde. `F3` toggles the free camera. Opening the terminal
captures gameplay input and releases the mouse; Escape or backtick closes it. Shift+tilde enters `~`
for relative coordinates.

Single-player enables world-changing commands. A standalone server rejects them unless started with:

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
