# NEXT_STEPS.md — consolidating player & object netcode, splitting GameWorld

Goal: after these four parts, players and objects share one replication machine, the
server is three small classes instead of one god class, and Part 5 gives you the
recipes to add components, weapons, objects, and messages **without help**. Every part
ends in a state where both projects build and the two-client smoke test passes — do
them in order, verify between parts, commit between parts.

Why this order: Parts 1–2 are behavior-neutral moves (safe, verifiable by "nothing
changed"), Part 3 removes the last duplicated machinery on the client, and Part 4 —
player health — is the payoff feature, built on the cleaned base so it lands in small
focused classes instead of feeding the god class.

## Part 2 — Server: extract WeaponSystem

All combat and pickup logic moves out. GameWorld keeps player lookup (its job) and
hands the resolved `ServerPlayer` to the system.

New file `Server/WeaponSystem.cs`:

```csharp
using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>Pickups, equipping, and server-authoritative fire/reload. Owns no
    /// state of its own: weapons live in ObjectReplication, timing gates live on
    /// ServerPlayer. GameWorld resolves clientId -> ServerPlayer and delegates.</summary>
    public class WeaponSystem
    {
        private readonly Server server;
        private readonly ObjectReplication objects;

        private const float PickupRadiusSq = 0.75f * 0.75f;

        public WeaponSystem(Server server, ObjectReplication objects)
        {
            this.server = server;
            this.objects = objects;
        }

        public ServerObject SpawnPickup(WeaponType type, Vector3 position)
        {
            return objects.Spawn(ObjectType.WeaponPickup, NetComponents.Transform | NetComponents.Weapon, position,
                obj => obj.Weapon = new WeaponState { Type = type, CurrentAmmo = WeaponConfig.Get(type).MagazineCapacity });
        }

        /// <summary>Call once per player per tick, after movement.</summary>
        public void TryPickup(ServerPlayer player)
        {
            if (player.WeaponId != 0) return;   // armed players ignore pickups

            // Find first, act after: Despawn/Spawn mutate the object dictionary
            // and must not run inside its enumeration.
            ServerObject? pickup = null;
            foreach (var obj in objects.All)
            {
                if (obj.Type != ObjectType.WeaponPickup) continue;
                if (Vector3.DistanceSquared(obj.Transform.Position, player.Position) > PickupRadiusSq) continue;
                pickup = obj;
                break;
            }
            if (pickup == null) return;

            var carried = pickup.Weapon;        // ammo carries over from the pickup
            objects.Despawn(pickup.NetworkId);

            var weapon = objects.Spawn(ObjectType.EquippedWeapon, NetComponents.Weapon | NetComponents.Owner, player.Position,
                obj =>
                {
                    obj.Weapon = carried;
                    obj.Owner = new OwnerState { PlayerId = player.Id };
                });
            player.WeaponId = weapon.NetworkId;
        }

        public void ApplyFire(ServerPlayer player, PlayerFireData fire, uint tick)
        {
            if (!IsFinite(fire.Origin) || !IsFinite(fire.Direction)) return;
            if (fire.Direction == Vector3.Zero) return;

            // Unarmed players can't fire; the equipped weapon object is the source
            // of truth for ammo, and its type keys the config both ends enforce.
            if (player.WeaponId == 0 || !objects.TryGet(player.WeaponId, out var weapon)) return;
            var stats = WeaponConfig.Get(weapon.Weapon.Type);

            // Enforce the same WeaponConfig numbers the client predicted with.
            if (tick < player.NextFireTick) return;    // faster than the gun can cycle
            if (tick < player.ReloadDoneTick) return;  // mid-reload
            if (weapon.Weapon.CurrentAmmo <= 0) return;

            // The client supplies the aim, but the shot must leave from roughly where
            // the server has the player. 2m tolerance covers prediction drift.
            if (Vector3.DistanceSquared(fire.Origin, player.Position) > 2f * 2f) return;

            player.NextFireTick = tick + (uint)stats.TicksPerShot;
            weapon.Weapon.CurrentAmmo--;
            weapon.Dirty |= NetComponents.Weapon;       // ammo replicates like any component

            var direction = Vector3.Normalize(fire.Direction);

            // A healthless object (a pickup) still blocks the shot; it just takes no damage.
            if (Raycast(fire.Origin, direction, stats.MaxRange) is { } hit
                && hit.Has.HasFlag(NetComponents.Health))
            {
                hit.Health.Current = hit.Health.Current > stats.Damage
                    ? (ushort)(hit.Health.Current - stats.Damage)
                    : (ushort)0;
                hit.Dirty |= NetComponents.Health;   // the object pipeline replicates the rest
            }

            // Cosmetic rebroadcast for remote tracers/audio. Unreliable: a lost
            // tracer is nothing. Only ACCEPTED shots get here, so rejected fire
            // never flashes on anyone's screen.
            Message fired = Message.Create(MessageSendMode.Unreliable, ServerToClientId.PlayerFired);
            fired.AddSerializable(new PlayerFiredData
            {
                PlayerId = player.Id,
                Weapon = weapon.Weapon.Type,
                Origin = fire.Origin,
                Direction = direction,
            });
            server.SendToAll(fired);
        }

        public void ApplyReload(ServerPlayer player, uint tick)
        {
            if (player.WeaponId == 0 || !objects.TryGet(player.WeaponId, out var weapon)) return;

            var stats = WeaponConfig.Get(weapon.Weapon.Type);
            if (tick < player.ReloadDoneTick) return;   // already reloading
            if (weapon.Weapon.CurrentAmmo == stats.MagazineCapacity) return;

            // Refill now, block firing until the window passes — observably identical
            // to refilling at the end, with no completion bookkeeping. (The client
            // refills at the end instead so its HUD reads 0 during the reload.)
            weapon.Weapon.CurrentAmmo = stats.MagazineCapacity;
            weapon.Dirty |= NetComponents.Weapon;
            player.ReloadDoneTick = tick + (uint)stats.ReloadTicks;
        }

        /// <summary>The weapon leaves with its owner. Call from RemovePlayer.</summary>
        public void DespawnFor(ServerPlayer player)
        {
            if (player.WeaponId == 0) return;
            objects.Despawn(player.WeaponId);
            player.WeaponId = 0;
        }

        private ServerObject? Raycast(Vector3 origin, Vector3 direction, float maxRange)
        {
            ServerObject? nearest = null;
            float nearestT = float.MaxValue;

            foreach (var obj in objects.All)
            {
                // No Transform component = not in the world (equipped weapons keep a
                // stale spawn position) — never hittable.
                if (!obj.Has.HasFlag(NetComponents.Transform)) continue;
                if (GunMath.HitDistance(origin, direction, obj.Transform.Position, maxRange) is not { } t) continue;
                if (t >= nearestT) continue;
                nearest = obj;
                nearestT = t;
            }
            return nearest;
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
```

`Server/GameWorld.cs` after both extractions — the whole file, now a thin
coordinator that owns players and the tick order (note `Tick` reads as a script:
moves → pickups → broadcast objects → broadcast positions — keep it that way, tick
order is gameplay-visible behavior):

```csharp
using Riptide;
using System.Numerics;

namespace Demiurge.GameServer
{
    internal class GameWorld
    {
        private readonly Dictionary<ushort, ServerPlayer> players = new();
        private readonly ObjectReplication objects;
        private readonly WeaponSystem weapons;

        // TEMP OBJECTS
        private ServerObject? testDummy;

        private readonly Server server;

        private uint _Tick = 0;

        private const int MaxQueuedMoves = 3;

        public GameWorld(Server server)
        {
            this.server = server;
            objects = new ObjectReplication(server);
            weapons = new WeaponSystem(server, objects);

            // TEMPORARY demo world content. SendToAll to zero clients is a no-op;
            // connecting clients get these via the AddPlayer catch-up.
            testDummy = objects.Spawn(ObjectType.TrainingDummy, NetComponents.Transform | NetComponents.Health, new Vector3(-2f, 0f, 2f),
                obj => obj.Health = new HealthState { Current = 100, Max = 100 });

            weapons.SpawnPickup(WeaponType.Ak47, new Vector3(3f, 0f, 0f));
            weapons.SpawnPickup(WeaponType.Ak47, new Vector3(-3f, 0f, -3f));
        }

        public void AddPlayer(ushort clientId)
        {
            foreach (var other in players.Values)              // catch the newcomer up
                server.Send(CreateSpawnMessage(other), clientId);

            objects.SendCatchUp(clientId);                     // catch the newcomer up on objects

            var player = new ServerPlayer { Id = clientId };
            players[clientId] = player;
            server.SendToAll(CreateSpawnMessage(player));      // announce the newcomer
        }

        public void RemovePlayer(ushort clientId)
        {
            if (players.Remove(clientId, out var player))
                weapons.DespawnFor(player);

            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerDespawn);
            message.AddSerializable(new PlayerDespawnData { PlayerId = clientId, Tick = _Tick });
            server.SendToAll(message);
        }

        public void ApplyInput(ushort clientId, PlayerInputData input)
        {
            if (!players.TryGetValue(clientId, out var player)) return;
            if (input.Sequence <= player.LastReceivedSequence) return; // dupe or out of order

            if (!IsFinite(input.Intent) || !float.IsFinite(input.Yaw)) return;

            player.LastReceivedSequence = input.Sequence;
            player.PendingMoves.Enqueue(input);
        }

        public void ApplyFire(ushort clientId, PlayerFireData fire)
        {
            if (players.TryGetValue(clientId, out var player))
                weapons.ApplyFire(player, fire, _Tick);
        }

        public void ApplyReload(ushort clientId)
        {
            if (players.TryGetValue(clientId, out var player))
                weapons.ApplyReload(player, _Tick);
        }

        private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

        public void Tick(float dt)
        {
            _Tick++;

            foreach (var player in players.Values)
            {
                // If the queue starts overflowing, consume at a faster rate
                int toProcess = player.PendingMoves.Count > MaxQueuedMoves ? 2 : 1;
                bool processedAny = false;

                for (int i = 0; i < toProcess && player.PendingMoves.TryDequeue(out var move); i++)
                {
                    player.Position = PlayerMovement.Step(player.Position, move.Intent, move.State, dt);
                    player.State = move.State;
                    player.Yaw = move.Yaw;
                    player.LastIntent = move.Intent;
                    player.LastProcessedSequence = move.Sequence;
                    processedAny = true;
                }

                // Queue starved, just reuse last player input
                if (!processedAny)
                    player.Position = PlayerMovement.Step(player.Position, player.LastIntent, player.State, dt);

                weapons.TryPickup(player);
            }

            // TEMPORARY demo behavior: the dummy comes back to life a moment after
            // dying, so there is always something to shoot.
            if (testDummy != null && testDummy.Health.Current == 0 && _Tick % (NetworkConfig.TickRate * 3) == 0)
            {
                testDummy.Health.Current = testDummy.Health.Max;
                testDummy.Dirty |= NetComponents.Health;
            }

            objects.BroadcastDirtyStates(_Tick);
            BroadcastPositions();
        }

        private Message CreateSpawnMessage(ServerPlayer player)
        {
            Message message = Message.Create(MessageSendMode.Reliable, ServerToClientId.PlayerSpawn);
            message.AddSerializable(new PlayerSpawnData { PlayerId = player.Id, Position = player.Position });
            return message;
        }

        private void BroadcastPositions()
        {
            foreach (var player in players.Values)
            {
                Message message = Message.Create(MessageSendMode.Unreliable, ServerToClientId.PlayerPosition);
                message.AddSerializable(
                    new PlayerPositionData
                    {
                        PlayerId = player.Id,
                        Tick = _Tick,
                        Position = player.Position,
                        Yaw = player.Yaw,
                        State = player.State,
                        LastProcessedSequence = player.LastProcessedSequence
                    });
                server.SendToAll(message);
            }
        }
    }
}
```

`GameServer.cs` needs no changes — the GameWorld facade keeps its public signatures.

**Verify Part 2:** identical smoke test to Part 1 — no behavior change allowed. Commit.

---

## Part 3 — Client: one component-copy path, one snapshot buffer

Two consolidations. First, the bug that stranded the AK at the origin lived in
`OnSpawn` having its own hand-written component copies separate from `Apply` — merge
them so forgetting a component is impossible in one place and fatal nowhere.

`Client/Simulation/ObjectRegistry.cs`, replace `OnSpawn` and `Apply` with:

```csharp
private void OnSpawn(ObjectSpawnData data)
{
    if (objects.ContainsKey(data.NetworkId)) return;

    var obj = new NetObject { NetworkId = data.NetworkId, Type = data.Type, Has = data.State.Mask };
    CopyComponents(obj, data.State, tick: 0);   // tick 0: spawn state predates any update
    objects[data.NetworkId] = obj;

    if (pendingUpdates.Remove(data.NetworkId, out var queued))
        foreach (var update in queued)
            Apply(obj, update);

    ObjectSpawned?.Invoke(obj);   // after pending applies, so the view builds from newest state
}

private static void Apply(NetObject obj, ObjectStateData data)
    => CopyComponents(obj, data.State, data.Tick);

// THE one place bundle components land on a NetObject — spawn and update both.
// New component = one new line here.
private static void CopyComponents(NetObject obj, in ComponentBundle state, uint tick)
{
    if (state.Mask.HasFlag(NetComponents.Transform))
    {
        obj.Transform = state.Transform;
        obj.Snapshots.Store(tick, state.Transform.Position);
    }
    if (state.Mask.HasFlag(NetComponents.Health)) obj.Health = state.Health;
    if (state.Mask.HasFlag(NetComponents.Weapon)) obj.Weapon = state.Weapon;
    if (state.Mask.HasFlag(NetComponents.Owner)) obj.Owner = state.Owner;
}
```

Second, `RemotePlayer` still carries its own inline copy of the snapshot machinery
that `SnapshotBuffer` extracted for objects. Retire it. In
`Client/Simulation/Player.cs`, the whole class becomes:

```csharp
// netcode writes, view reads
public class RemotePlayer : Player
{
    public SnapshotBuffer Snapshots { get; } = new();
}
```

Two call sites update. `Client/Simulation/PlayerRegistry.cs`, in `OnPlayerPosition`:

```csharp
case RemotePlayer remote:
    remote.Snapshots.Store(data.Tick, data.Position);
    remote.Position = data.Position;
    remote.Yaw = data.Yaw;
    remote.State = data.State;
    break;
```

`Client/View/PlayerViewScript.cs`, the remote branch of `Update`:

```csharp
case RemotePlayer remote:
    double renderTick = remote.Snapshots.NewestTick
        + (remote.Snapshots.SecondsSinceNewest * NetworkConfig.TickRate) - 3.0;
    Entity.Transform.Position = remote.Snapshots.GetInterpolated(renderTick, remote.Position).ToStride();
    break;
```

**Verify Part 3:** two clients, walk around — remote players must move exactly as
smoothly as before (same interpolation, now on the shared class). Pickups/shooting
unchanged. Commit.

---

## Part 4 — Player health via the PlayerStatus object

The consolidation payoff: players get replicated Health WITHOUT new message types,
new registries, or touching the movement/prediction channel. Each player gets a
companion object carrying their non-movement components — the same owner-linked
pattern the equipped weapon already proved. When players later need more replicated
state (armor, effects), it's an appended component bit on this same object.

### 4a. Common

Append to `ObjectType` in `Common/Component.cs` (append-only, as always):

```csharp
public enum ObjectType : ushort {
    Crate = 1,
    TrainingDummy,
    WeaponPickup,
    EquippedWeapon,
    PlayerStatus
}
```

Add one global to `Common/GunConfig.cs` (the player hit-sphere center; a flat ray at
MuzzleHeight 0.4 comfortably intersects a 0.6-radius sphere centered at 0.5):

```csharp
public const float PlayerCenterHeight = 0.5f;
```

### 4b. Server

`Server/ServerPlayer.cs`, add next to WeaponId:

```csharp
// The player's replicated component record (Health today; append bits later).
public ServerObject? Status { get; set; }
```

`Server/GameWorld.cs`, in `AddPlayer`, replace the two player lines with:

```csharp
var player = new ServerPlayer { Id = clientId };
player.Status = objects.Spawn(ObjectType.PlayerStatus, NetComponents.Owner | NetComponents.Health, player.Position,
    obj =>
    {
        obj.Owner = new OwnerState { PlayerId = clientId };
        obj.Health = new HealthState { Current = 100, Max = 100 };
    });
players[clientId] = player;
```

In `RemovePlayer`, despawn it with the weapon:

```csharp
if (players.Remove(clientId, out var player))
{
    weapons.DespawnFor(player);
    if (player.Status != null) objects.Despawn(player.Status.NetworkId);
}
```

In `Tick`, after the player loop and before the dummy block — death and respawn:

```csharp
// Death + respawn: dead players teleport home with full health. The client's
// reconciliation sees the teleport as a large correction and snaps to it.
foreach (var player in players.Values)
{
    if (player.Status is not { } status || status.Health.Current > 0) continue;
    player.Position = Vector3.Zero;
    status.Health.Current = status.Health.Max;
    status.Dirty |= NetComponents.Health;
}
```

`Server/WeaponSystem.cs` — the raycast learns about players and returns the victim's
STATUS object, so the existing damage code works unchanged (it just flips Health on a
ServerObject, same as the dummy). Replace `Raycast` and update its call in
`ApplyFire`:

```csharp
private ServerObject? Raycast(Vector3 origin, Vector3 direction, float maxRange,
                              ServerPlayer shooter, IEnumerable<ServerPlayer> players)
{
    ServerObject? nearest = null;
    float nearestT = float.MaxValue;

    foreach (var obj in objects.All)
    {
        if (!obj.Has.HasFlag(NetComponents.Transform)) continue;
        if (GunMath.HitDistance(origin, direction, obj.Transform.Position, maxRange) is not { } t) continue;
        if (t >= nearestT) continue;
        nearest = obj;
        nearestT = t;
    }

    foreach (var player in players)
    {
        // Skip the shooter: the ray starts inside their own sphere and would
        // register a self-hit at t ~ 0 on every shot.
        if (player == shooter || player.Status == null) continue;

        var center = player.Position + new Vector3(0f, GunConfig.PlayerCenterHeight, 0f);
        if (GunMath.HitDistance(origin, direction, center, maxRange) is not { } t) continue;
        if (t >= nearestT) continue;

        nearest = player.Status;   // the hittable "body" IS the status object
        nearestT = t;
    }

    return nearest;
}
```

`ApplyFire` gains a `players` parameter and passes it through — change the signature
and the raycast call:

```csharp
public void ApplyFire(ServerPlayer player, PlayerFireData fire, uint tick, IEnumerable<ServerPlayer> players)
```

```csharp
if (Raycast(fire.Origin, direction, stats.MaxRange, player, players) is { } hit
    && hit.Has.HasFlag(NetComponents.Health))
```

and `GameWorld.ApplyFire` passes them:

```csharp
weapons.ApplyFire(player, fire, _Tick, players.Values);
```

### 4c. Client

`Client/Simulation/Player.cs`, add to `LocalPlayer` next to the Weapon block:

```csharp
// Our replicated component record (Health etc). Set by the composition-root
// bridge when the server's PlayerStatus object for us spawns. Netcode writes
// (via ObjectRegistry), view reads — HUD included.
public NetObject? Status { get; set; }
```

`Client/Program.cs` — extend the existing registry bridge to route both owned object
types (this REPLACES the current two subscriptions):

```csharp
// Bridge the two registries: objects owned by our client id attach to the local
// player. Sim-to-sim glue lives here in the composition root.
objectRegistry.ObjectSpawned += obj =>
{
    if (registry.LocalPlayer is not { } local || obj.Owner.PlayerId != network.ClientId) return;
    if (obj.Type == Demiurge.ObjectType.EquippedWeapon) local.Equip(obj);
    if (obj.Type == Demiurge.ObjectType.PlayerStatus) local.Status = obj;
};
objectRegistry.ObjectDespawned += obj =>
{
    if (registry.LocalPlayer is not { } local) return;
    if (obj.Type == Demiurge.ObjectType.EquippedWeapon) local.Unequip(obj);   // no-ops unless ours
    if (ReferenceEquals(local.Status, obj)) local.Status = null;
};
```

`Client/View/HUD.cs` — a health line joins the panel. In `CreateUI`, after the
`ammoText` block:

```csharp
var healthText = new TextBlock
{
    Text = "HP —",
    TextColor = Color.White,
    Font = font,
    TextSize = 24,
    VerticalAlignment = VerticalAlignment.Center,
    Margin = new Thickness(6, 0, 12, 0),
};
```

add it to the panel FIRST (health left of the bullet icon):

```csharp
var ammoPanel = new StackPanel { Orientation = Orientation.Horizontal };
ammoPanel.Children.Add(healthText);
ammoPanel.Children.Add(bulletImage);
ammoPanel.Children.Add(ammoText);
```

hand it to the script:

```csharp
new HudScript {
    canvas = canvas,
    AmmoText = ammoText,
    HealthText = healthText },
```

and `HudScript` becomes (panel now shows from spawn — health is always relevant;
ammo reads `--` until armed):

```csharp
public class HudScript : SyncScript
{
    public TextBlock AmmoText { get; set; } = null!;
    public TextBlock HealthText { get; set; } = null!;
    public Canvas canvas {get; set; } = null!;

    private PlayerRegistry _registry = null!;

    private int _lastAmmo = int.MinValue;
    private bool _lastReloading;
    private int _lastHealth = int.MinValue;
    private bool _lastVisible;

    public override void Start()
    {
        _registry = Services.GetSafeServiceAs<PlayerRegistry>();
        canvas.Visibility = Visibility.Collapsed;   // until spawn
    }

    public override void Update()
    {
        var local = _registry.LocalPlayer;

        bool visible = local != null;
        if (visible != _lastVisible)
        {
            _lastVisible = visible;
            canvas.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        if (local == null) return;

        int health = local.Status?.Health.Current ?? 0;
        if (health != _lastHealth)
        {
            _lastHealth = health;
            HealthText.Text = $"HP {health}";
        }

        int ammo = local.IsArmed ? local.Ammo : -1;
        if (ammo != _lastAmmo || local.IsReloading != _lastReloading)
        {
            _lastAmmo = ammo;
            _lastReloading = local.IsReloading;
            AmmoText.Text = !local.IsArmed ? "--"
                : local.IsReloading ? "RELOADING"
                : $"{local.Ammo}/{local.Stats.MagazineCapacity}";
        }
    }
}
```

**Verify Part 4:** two clients. (1) Both HUDs show `HP 100` from spawn, ammo `--`
until a pickup. (2) Client A shoots client B: B's HUD health drops in steps of 10 —
that damage traveled A → PlayerFire → server raycast (hitting B's sphere) → status
object Health dirty → ObjectState → B's registry → B's HUD. (3) At 0, B teleports to
the origin with `HP 100` (the view snaps — that's the >2m correction path working).
(4) B's death does not affect B's weapon or A's anything. (5) A late-joining third
client sees correct current health for everyone (status objects ride the same
catch-up as everything else). Note the shooter gets no hit feedback beyond watching
the victim — a `HitConfirm` message is the natural follow-up, and the recipe below
covers adding it.

---

## Part 5 — Recipes: adding things yourself

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
