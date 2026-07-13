# Recipes: adding things yourself

The map, first. **Common** is the wire: enums and structs both ends compile.
**Server** is truth: three classes — GameWorld (players, tick order), ObjectReplication
(objects on the wire), WeaponSystem (combat). **Client** is three layers with strict
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

### Add a weapon (e.g. a shotgun)

1. `Common/Component.cs`: append to `WeaponType`.
2. `Common/WeaponConfig.cs`: one table row (capacity, cadence, reload, damage, range).
3. `Client/View/WeaponCosmetics.cs`: one row (model, sound, tracer color) + assets.
4. Spawn it somewhere: `weapons.SpawnPickup(WeaponType.Shotgun, pos)`.

Nothing else — pickup, equip, prediction, validation, FX all key off the type.

### Add an object type (e.g. a barrel)

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

### Parent an item to the player model (helmet, back slot, off-hand)

The pattern already exists once — EquippedWeapon — and it generalizes by turning
its one hardcoded case into a socket table. The split that keeps it general: the
wire says WHOSE body (the `Owner` component); the client decides WHERE on the
body. Bone name and seat offset are cosmetic, so they stay off the wire — same
reason WeaponConfig's damage numbers do.

1. `Client/View/WeaponScripts.cs`: generalize `WeaponAttachScript` into an
   `AttachToOwnerScript` with a `Socket` init property, plus a socket table:
   `Socket.RightHand / Head / Back → (bone NodeName, seat position, seat rotation)`.
   The current right_hand values become the first row. Bone names come from the
   rig — dump the gltf's node names if unsure.
2. `Client/View/ObjectViewFactory.cs`: the builder picks the socket:
   `new AttachToOwnerScript { Object = obj, Socket = Socket.Head }`.
3. Server: spawn with `NetComponents.Owner` in the mask and set
   `obj.Owner = new OwnerState { PlayerId = ... }` inside `init`
   (`WeaponSystem.TryPickup` is the model).
4. Despawn it when the owner leaves, like `WeaponSystem.DespawnFor`. Miss this
   and every client keeps an orphan view retrying its attach every frame, forever.

Rules that make it work:

- **No Transform in the mask.** The bone link owns the entity's transform; a
  NetTransformScript would fight it (same conflict the pickup builder suppresses
  for PickupBobScript). EquippedWeapon spawns with `Weapon | Owner` only — copy that.
- **Keep the entity at the scene root.** ModelNodeLinkComponent drives the world
  transform from the bone regardless of hierarchy, and DestroyView finds views
  by name at the root.
- **Keep the retry loop.** Reliable messages aren't ordered across each other, so
  the item can arrive before its owner's view exists. Attaching in Update until
  the owner appears is the fix, not a hack.
- The `attached` flag latches once. If an item can change owners (drop, then
  someone else grabs it), don't latch: watch `Object.Owner.PlayerId` and on
  change remove the ModelNodeLinkComponent and re-attach.
- If one object must move between sockets at runtime (rifle in hands vs. slung
  on back), the socket stops being cosmetic — append an `Attachment` component
  carrying a socket id, via the replicated-component recipe above.

### Give the player a stat (health, armor)

Player stats already have a home: the server spawns each player an
`ObjectType.PlayerStatus` object (`GameWorld.AddPlayer`, mask `Owner | Health`),
and the client wires it to `LocalPlayer.Status` (Program.cs). A new stat is a
component on that object — no new message type.

1. Follow the replicated-component recipe (its ArmorState example is literally this).
2. Server: add the bit to the PlayerStatus spawn mask, set the value in `init`;
   on change, write the field and set `status.Dirty |= NetComponents.Armor`
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
  `TryPickup`).
- Verify with two clients + a late joiner. The late joiner is the test that finds
  catch-up bugs; the second client finds every "works on my screen" bug.
