# Gameplay Datapacks

Items, weapons, armor, ballistics, presentation settings, aliases, and default loadouts are loaded
from JSON at startup. The built-in definitions live in `datapacks/base`; adding an ordinary firearm
does not require editing or recompiling C#.

## Pack layout

```text
datapacks/
  my_pack/
    pack.json
    data/
      my_namespace/
        defaults.json                 # optional override
        items/
          field_rifle.json
        ballistics/
          light_rifle.json
```

`pack.json` declares a namespaced ID, schema version, and priority:

```json
{
  "schemaVersion": 1,
  "id": "example:field_weapons",
  "priority": 100
}
```

Packs are applied by ascending priority and then pack ID. A later file with the same canonical ID
replaces the whole earlier definition. Overrides therefore repeat every required field. JSON is
strict: unknown properties, invalid values, duplicate aliases/handles, and missing references stop
startup with a combined validation error.

`defaults.json` selects `playerPrimary`, `npcPrimary`, `unidentifiedThreatWeapon`, `assaultWeapon`,
and `marksmanWeapon` by canonical item ID. It is also a whole-file override.

## Adding a weapon

Ballistics are reusable definitions:

```json
{
  "schemaVersion": 1,
  "id": "example:light_rifle",
  "projectileSpeed": 760,
  "benchMoa": 3,
  "recoilPerShotMoa": 30,
  "recoilDecayMoaPerSecond": 28,
  "recoilCapMoa": 180,
  "sightingMoa": 150
}
```

An item opts into an engine behavior and references those ballistics:

```json
{
  "schemaVersion": 1,
  "id": "example:field_rifle",
  "displayName": "Field Rifle",
  "aliases": ["field-rifle"],
  "category": "equippable",
  "slot": "hand",
  "hotbar": "primary",
  "behavior": "firearm",
  "weapon": {
    "magazineCapacity": 12,
    "roundsPerMinute": 300,
    "reloadSeconds": 2.25,
    "damage": 42,
    "ballistics": "example:light_rifle",
    "fireMode": "semiAutomatic"
  },
  "presentation": {
    "model": "assets/models/sks.gltf",
    "worldScale": 1,
    "aimMagnification": 1,
    "aimSpeedScale": 1,
    "shotSounds": ["assets/sfx/sks_shot_1.wav"],
    "reloadSound": "assets/sfx/sks_reload.wav",
    "tracerColor": "#FFFF00"
  }
}
```

`networkId` is optional for extension packs. The registry deterministically assigns extension items
handles at 1024 and above. The base pack pins its legacy-compatible handles; values 1–3 stay retired
and are never reassigned.

## Loading and compatibility

The game always loads the built-in `datapacks` root. Set `DEMIURGE_DATAPACK_ROOTS` to an additional
platform-separated list of roots to load more packs. Client and server must resolve identical data:
the welcome handshake compares a SHA-256 gameplay hash and disconnects on a mismatch.

Canonical names such as `example:field_rifle` are used by commands, editor source maps, and runtime
map format v3. Compact numeric handles exist only inside a running, hash-matched session. New
mechanics still require a C# `ItemBehavior`; new items using an existing behavior are data-only.
