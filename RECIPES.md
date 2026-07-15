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
4. `Server/ServerObject.cs`: add the field (a FIELD, not a property — mutable struct),
   and one masked line in `ServerObject.CopyComponents` (carries live state across
   ItemSystem's equip/drop transitions — forget it and the trait silently zeroes on swap).
5. `Server/ObjectReplication.cs`: one line in `Bundle(...)`.
6. `Client/Simulation/NetObject.cs`: add the field.
7. `Client/Simulation/ObjectRegistry.cs`: one masked line in `CopyComponents`.
8. Optional view: a script in `ObjectViewFactory.CreateView`'s mask block.

Eight mechanical edits, all append-only. Steps 4 (CopyComponents), 5, and 7 exist
in exactly one place each — before they were centralized, those were the spots
this checklist got forgotten (both shipped bugs).

### How items work (read this before the item recipes)

*(These recipes describe the generalized item system —
`docs/superpowers/specs/2026-07-15-generalized-item-system-design.md`.)*

An item is identity + data, not a type hierarchy. `ItemType` names it;
`ItemConfig` (Common) holds the identity-level facts every item has (category,
equip slot); each TRAIT has its own table keyed by `ItemType` — `WeaponConfig`
(Common) for guns, `ArmorConfig` (Common) for armor — where having a row IS
having the trait. On the client, `ItemCosmetics` maps type → model and slot →
socket, and `WeaponFx` maps type → shot sound/tracer. There is no per-item
class, component, or ObjectType.

Behavior comes from the component mask — the mask IS the trait system:

- `ItemState + Transform` → a pickup sitting in the world (bobs, E-interactable)
- `ItemState + Owner + Attachment` → equipped; `Attachment.Slot` says where
- `+ WeaponState` → it shoots (fire/reload validation applies)
- `+ ArmorState` → it's armor (replicated now, consumed by nothing yet)

`ItemSystem.SpawnPickup` derives the spawn mask from the trait tables, so a
designer adding an item never touches masks. An item has a trait if its mask
carries the bit; giving items a NEW trait is the last recipe below.

### Add an equippable item (hat, helmet, armor — no new code)

If the item just gets worn somewhere, data rows are the whole job:

1. `Common/Component.cs`: append to `ItemType`.
2. `Common/ItemConfig.cs`: one row — `Category = Equippable`, its `EquipSlot`.
3. `Client/View/ItemCosmetics.cs`: one row in `Model(...)` — plus the assets.
   WHERE it sits comes from its slot's socket row, not the item.
4. Spawn it somewhere: `items.SpawnPickup(ItemType.TopHat, pos)`.

Pickup bob, E-to-equip, swap-drop, attach, despawn-on-disconnect, late-join
catch-up: all free. They key off `ItemState` and the config, not the item.

### Add a weapon (e.g. a shotgun)

A weapon is an equippable with a row in the weapon trait table — same recipe,
two extra rows of numbers:

1. `Common/Component.cs`: append to `ItemType`.
2. `Common/ItemConfig.cs`: one row — `Category = Equippable`, Hand slot.
3. `Common/WeaponConfig.cs`: one row — capacity, cadence, reload, damage, range.
4. `Client/View/ItemCosmetics.cs`: one row in `Model(...)`;
   `Client/View/WeaponFx.cs`: one row (shot sound, tracer color) + assets.
5. Spawn: `items.SpawnPickup(ItemType.Shotgun, pos)`.

Prediction, validation, ammo replication, FX all key off the type; the
WeaponConfig row is what puts `WeaponState` in the spawn mask, which is what
makes fire/reload apply. (Armor is the same shape: an `ArmorConfig` row drives
`ArmorState`.)

### Add an equip slot (e.g. Feet)

1. `Common/ItemConfig.cs`: append to `EquipSlot` (it rides the wire inside
   `AttachmentState` — append-only).
2. `Client/View/ItemCosmetics.cs`: one socket row in `SlotSockets` — which bone
   the slot lives on, plus its seat offset/rotation.

Occupancy is a per-player dictionary keyed by slot and nothing enumerates
slots, so nothing else changes. Point items at the slot via their `ItemConfig`
row; every item in that slot shares the socket.

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

Designers get attachment for free from the slot; this is for anyone touching
the machinery. The split that keeps it general: the wire says WHOSE body (the
`Owner` component) and WHICH slot (the `Attachment` component); the client
decides what that slot looks like. `ItemAttachScript` keys `SlotSockets` by
`Attachment.Slot` — a socket's bone name attaches via ModelNodeLinkComponent,
`null` parents to the `Player_{id}` entity root. Bone names and seat offsets
are cosmetic, so they stay off the wire — same reason WeaponConfig's damage
numbers do. Bone names come from the rig — dump the gltf's node names if
unsure.

Rules that make it work:

- **No Transform in the mask.** The bone link owns the entity's transform; a
  NetTransformScript would fight it (same conflict the pickup path suppresses
  for PickupBobScript). Equipped items spawn with `ItemState | Owner |
  Attachment` (+ live state), never Transform — which also keeps them
  unhittable by the raycast.
- **Keep the entity at the scene root.** ModelNodeLinkComponent drives the world
  transform from the bone regardless of hierarchy, and DestroyView finds views
  by name at the root.
- **Keep the retry loop.** Reliable messages aren't ordered across each other, so
  the item can arrive before its owner's view exists. Attaching in Update until
  the owner appears is the fix, not a hack.
- The `boneLinked` flag latches once. That's safe because owner AND slot
  transitions are despawn → respawn (a swap-dropped gun is a NEW pickup object),
  so no view ever sees `Owner.PlayerId` or `Attachment.Slot` change. Moving a
  rifle from Hand to Back is therefore a server-side `Equipped` shuffle that
  respawns the object — no view changes needed.
- **The active gun is the Hand slot**, nothing else. `LinkOwned` (Program.cs)
  equips only `Weapon`-masked objects whose `Attachment.Slot == Hand` — a stowed
  weapon on another slot must never drive the HUD or prediction.
- Despawn equipped items when the owner leaves (`ItemSystem.DespawnFor`). Miss
  this and every client keeps an orphan view retrying its attach forever.

### Give items a new trait (new component)

When an item needs new live, replicated state — fuel, charges, durability, a
container's inventory — that's a new component bit, not a new item kind or a
new pickup/equipped class:

1. Follow the replicated-component recipe above (e.g. `FuelState`) — its
   CopyComponents step is what carries the live struct across pickup↔equipped
   transitions, automatically, like ammo.
2. Create the trait's table: `Common/FuelConfig.cs`, `Get(ItemType) → FuelStats?`
   — a row means "this item has the trait" (`WeaponConfig`/`ArmorConfig` are the
   models).
3. `Server/ItemSystem.cs`: two lines in `SpawnPickup` — add the mask bit when
   the table has a row, set the starting state in `init`.
4. A server system consumes/mutates it and sets `obj.Dirty |= ...` — the object
   pipeline replicates the rest.
5. Client view: attach a script in `ObjectViewFactory`'s mask block if the trait
   is visible.

After this, giving any OTHER item the trait is one row in the trait's table —
that's the payoff of keeping traits in the mask.

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

### Where this design sits (and its three scaling seams)

Two schools of game networking. **Per-feature packets** (Minecraft): ~150
hand-written packet types over TCP, one per game feature — total control, but
every feature grows new packets and handlers. **Generic state replication**
(the Quake → Source → Unreal lineage): the server owns a set of replicated
objects and the engine ships state diffs. `ObjectReplication` is a small member
of the second school — generic spawn/despawn/catch-up plus dirty-mask deltas —
and the streamed/evented split mirrors Source's unreliable snapshot stream +
reliable event channel. The convergent details are convergent because everyone
hits the same walls: Minecraft's `SynchedEntityData` is our dirty mask, its
`SetEquipment` packet is our `AttachmentState`, its dropped-item entities are
our `ItemState + Transform` pickups.

What the big engines have that we deliberately don't (yet) — each has a clean
seam when its day comes:

- **Interest management.** We broadcast everything to everyone; Minecraft
  replicates only within view distance, Unreal culls by relevancy. Correct at
  our scale. The seam: the `SendToAll` calls inside `ObjectReplication` become
  a per-client relevancy filter — nothing outside that class changes.
- **Ack-based delta compression.** We use dirty flags + a reliable channel;
  Quake/Source delta-compress against the last snapshot the client ACKED, so
  loss never stalls anything. Simpler wins until packet loss on the reliable
  channel causes hitches under load. The seam: also entirely inside
  `ObjectReplication` (`BroadcastDirtyStatess` grows per-client baselines).
- **Prediction scope.** We predict movement and firing (the Counter-Strike
  feel); Minecraft predicts almost nothing, which is why laggy Minecraft feels
  like molasses instead of rubber-banding. Widening prediction (e.g. predicted
  pickup on E) means more reconcile paths — add per action, deliberately, the
  way fire/reload already work.
