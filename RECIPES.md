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
