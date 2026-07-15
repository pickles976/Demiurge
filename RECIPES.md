# Recipes: adding things yourself

The map, first. **Common** is the wire: enums and structs both ends compile.
**Server** is truth: four classes — GameWorld (players, tick order), ObjectReplication
(objects on the wire), ItemSystem (pickups, equip, swap-drop), WeaponSystem
(fire/reload validation). **Client** is three layers with strict
flow: Netcode (NetworkManager: socket → events) writes **Sim** (registries, Player,
NetObject: pure state, no Stride types beyond math) which is read by **View** (scripts
and factories: render what the sim says, decide nothing). All wiring happens in
Program.cs — the composition root. If you're about to make a view script send a
message or the sim touch an Entity, stop: that's the boundary talking.

**Wire rules (break these and clients silently desync):** enum values and the
ComponentBundle if-chain order ARE the protocol — append, never reorder, never
delete. Streamed state (transforms) goes unreliable; evented state (everything else)
goes reliable. Events that must not be lost (fire, reload) are their own reliable
messages; cosmetic events (PlayerFired) go unreliable.

### Add a replicated component (e.g. ArmorState)

1. `Common/Component.cs`: append a bit to `NetComponents` (`Armor = 1 << 4`).
2. `Common/Component.cs`: add the `ArmorState : IMessageSerializable` struct.
3. `Common/Component.cs`: add the field to `ComponentBundle` and one masked line at
   the END of `Serialize` AND `Deserialize`.
4. `Server/ServerObject.cs`: add the field (a FIELD, not a property — mutable struct).
5. `Server/ObjectReplication.cs`: one line in `Bundle(...)`.
6. `Client/Simulation/NetObject.cs`: add the field.
7. `Client/Simulation/ObjectRegistry.cs`: one masked line in `CopyComponents`.
8. Optional view: a script in `ObjectViewFactory.CreateView`'s mask block.

Seven mechanical edits, all append-only. Steps 5 and 7 exist in exactly one place
each because of Parts 1 and 3 — before, they were the two spots this checklist got
forgotten (once each, both shipped bugs).

### How items work (read this before the item recipes)

*(These recipes describe the generalized item system —
`docs/superpowers/specs/2026-07-15-generalized-item-system-design.md`.)*

An item is identity + data, not a type hierarchy. `ItemType` names it,
`ItemConfig` (Common) says what it does — category, equip slot, weapon stats if
any — and `ItemCosmetics` (Client) says what it looks like and where it sits on
the body. There is no per-item class, component, or ObjectType.

Behavior comes from the component mask — the mask IS the trait system:

- `ItemState + Transform` → a pickup sitting in the world (bobs, E-interactable)
- `ItemState + Owner` → equipped, attached to that player's model
- `+ WeaponState` → it shoots (fire/reload validation applies)
- `+ ArmorState` → it's armor (replicated now, consumed by nothing yet)

`ItemSystem.SpawnPickup` derives the spawn mask from the config, so a designer
adding an item never touches masks. An item has a trait if its mask carries the
bit; giving items a NEW trait is the last recipe below.

### Add an equippable item (hat, helmet, armor — no new code)

If the item just gets worn somewhere, data rows are the whole job:

1. `Common/Component.cs`: append to `ItemType`.
2. `Common/ItemConfig.cs`: one row — `Category = Equippable`, its `EquipSlot`.
3. `Client/View/ItemCosmetics.cs`: one row — model path, attach node (a bone
   like `"torso"` for body armor or `"head"` for a helmet; `null` parents to
   the player entity root instead), seat offset/rotation — plus the assets.
4. Spawn it somewhere: `items.SpawnPickup(ItemType.TopHat, pos)`.

Pickup bob, E-to-equip, swap-drop, attach, despawn-on-disconnect, late-join
catch-up: all free. They key off `ItemState` and the config, not the item.

### Add a weapon (e.g. a shotgun)

A weapon is an equippable whose config row has a weapon section — same recipe,
one extra row of numbers:

1. `Common/Component.cs`: append to `ItemType`.
2. `Common/ItemConfig.cs`: one row — Hand slot + weapon stats (capacity,
   cadence, reload, damage, range).
3. `Client/View/ItemCosmetics.cs`: one row (model, `"right_hand"` attach node,
   seat, shot sound, tracer color) + assets.
4. Spawn: `items.SpawnPickup(ItemType.Shotgun, pos)`.

Prediction, validation, ammo replication, FX all key off the type; the weapon
stats section is what puts `WeaponState` in the spawn mask, which is what makes
fire/reload apply.

### Add an equip slot (e.g. Feet)

1. `Common/ItemConfig.cs`: append to `EquipSlot`.

Done. Occupancy is a per-player dictionary keyed by slot and nothing enumerates
slots, so no other code changes. Point items at it via their config row; where
that slot sits on the body is each item's cosmetics entry, not the slot's.

### Add an object type (e.g. a barrel) — scenery only

Items never add ObjectTypes (they're all `ObjectType.Item`; `ItemState` and the
mask carry the meaning). This recipe is for scenery and logic objects — crates,
barrels, the PlayerStatus object.

1. `Common/Component.cs`: append to `ObjectType`.
2. `Client/View/ObjectViewFactory.cs`: one builder entry (model + any custom script).
3. Server: `objects.Spawn(ObjectType.Barrel, NetComponents.Transform | ..., pos, init)`
   — put full component state in `init`; it must be set before the broadcast.

### Add a client→server message (e.g. UseAction)

1. `Common/NetworkProtocol.cs`: append to `ClientToServerId`.
2. `Common/Messages/`: the `IMessageSerializable` struct (skip if no payload — see
   PlayerReload).
3. `Client/Netcode/NetworkManager.cs`: a `SendX` method (reliable if losing it would
   jam gameplay).
4. `Server/GameServer.cs`: a dispatch case → `world.ApplyX(e.FromConnection.Id, ...)`.
5. `Server/GameWorld.cs`: the handler — resolve the player, validate (finite floats,
   sequence/tick gates, positions near the server's own belief), then act.

Server→client is the mirror: `ServerToClientId` append, struct, a
`NetworkManager` event + dispatch case, and a subscriber in the sim (registry) or
composition root — never directly in a view script.

### How attach-to-player works (the rules behind ItemAttachScript)

Designers get attachment for free from the cosmetics row; this is for anyone
touching the machinery. The split that keeps it general: the wire says WHOSE
body (the `Owner` component); the client decides WHERE on the body.
`ItemAttachScript` reads the item's `ItemCosmetics` entry — a bone name attaches
via ModelNodeLinkComponent, `null` parents to the `Player_{id}` entity root.
Bone names and seat offsets are cosmetic, so they stay off the wire — same
reason WeaponConfig's damage numbers do. Bone names come from the rig — dump
the gltf's node names if unsure.

Rules that make it work:

- **No Transform in the mask.** The bone link owns the entity's transform; a
  NetTransformScript would fight it (same conflict the pickup path suppresses
  for PickupBobScript). Equipped items spawn with `ItemState | Owner` (+ live
  state), never Transform — which also keeps them unhittable by the raycast.
- **Keep the entity at the scene root.** ModelNodeLinkComponent drives the world
  transform from the bone regardless of hierarchy, and DestroyView finds views
  by name at the root.
- **Keep the retry loop.** Reliable messages aren't ordered across each other, so
  the item can arrive before its owner's view exists. Attaching in Update until
  the owner appears is the fix, not a hack.
- The `attached` flag latches once. That's safe because ownership transitions are
  despawn → respawn (a swap-dropped gun is a NEW pickup object), so no view ever
  sees `Owner.PlayerId` change. If that ever changes, stop latching: watch the
  id and re-attach on change.
- If one object must move between sockets at runtime (rifle in hands vs. slung
  on back), the socket stops being cosmetic — append an `Attachment` component
  carrying a socket id, via the replicated-component recipe above.
- Despawn equipped items when the owner leaves (`ItemSystem.DespawnFor`). Miss
  this and every client keeps an orphan view retrying its attach forever.

### Give items a new trait (new component)

When an item needs new live, replicated state — fuel, charges, durability, a
container's inventory — that's a new component bit, not a new item kind or a
new pickup/equipped class:

1. Follow the replicated-component recipe above (e.g. `FuelState`).
2. `Common/ItemConfig.cs`: declare it in the config rows that have the trait
   (e.g. a max-fuel field), so the data says which items carry it.
3. `Server/ItemSystem.cs`: `SpawnPickup`'s mask derivation adds the bit when the
   config declares it, and `init` sets the starting state. Carry the live struct
   across the pickup↔equipped despawn/respawn transitions, like ammo.
4. A server system consumes/mutates it and sets `obj.Dirty |= ...` — the object
   pipeline replicates the rest.
5. Client view: attach a script in `ObjectViewFactory`'s mask block if the trait
   is visible.

After this, giving any OTHER item the trait is a config row — that's the payoff
of keeping traits in the mask.

### Give the player a stat (health, stamina)

Player stats already have a home: the server spawns each player an
`ObjectType.PlayerStatus` object (`GameWorld.AddPlayer`, mask `Owner | Health`),
and the client wires it to `LocalPlayer.Status` (Program.cs). A new stat is a
component on that object — no new message type. (Stats that belong to a WORN
thing — armor value, backpack contents — go on the item object instead, via the
new-trait recipe; ArmorState lives on the armor item, not on PlayerStatus.)

1. Follow the replicated-component recipe.
2. Server: add the bit to the PlayerStatus spawn mask, set the value in `init`;
   on change, write the field and set `status.Dirty |= NetComponents.Stamina`
   (how damage flows today — GameWorld dirties Health, the object pipeline does
   the rest).
3. Client: read it off `LocalPlayer.Status`. For the HUD, keep the A+B pattern
   from the `IPlayerStatus` doc comment: write the shared state, broadcast the
   `GameEvents` signal, HUD re-reads — never polls.
4. Visible-to-everyone on/off state (helmet on the model) may not need a stat at
   all: if the helmet IS an owned object (recipe above), its existence is the
   state, and you get the visual for free.

### The habits that keep it working

- Server validates everything a client sends; the client predicts with the same
  shared numbers (`WeaponConfig`, `PlayerMovement`, `GunMath`) so honest clients
  never trip a gate.
- New spawn state goes through `Spawn`'s `init` callback — state set after spawn
  needs a `Dirty` flag or it never leaves the server.
- Never mutate the object dictionary while enumerating it (find-then-act, like
  the interact handler's nearest-pickup scan).
- Verify with two clients + a late joiner. The late joiner is the test that finds
  catch-up bugs; the second client finds every "works on my screen" bug.
