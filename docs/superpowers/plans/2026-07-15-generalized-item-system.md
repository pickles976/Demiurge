# Generalized Item System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **For this project:** the owner implements tasks by hand; this document is the deliverable. Spec: `docs/superpowers/specs/2026-07-15-generalized-item-system-design.md`.

**Goal:** One component-driven item system — pickup, E-to-equip, slot-based swap-drop, attach-to-player — shared by weapons, armor, and every future wearable, replacing the per-kind ObjectTypes and the pickup logic trapped in WeaponSystem.

**Architecture:** Item identity moves into a replicated `ItemState` component; semantics live in the component mask (`Item+Transform` = pickup, `Item+Owner` = equipped, `+WeaponState` = gun). Static data lives in `ItemConfig` (Common) and `ItemCosmetics` (Client), keyed by one `ItemType` enum. A new server `ItemSystem` owns pickup/equip/swap; `WeaponSystem` keeps only fire/reload validation. Transitions stay despawn→respawn, so the replication pipeline is untouched.

**Tech Stack:** C# / .NET 10, Stride (client), Riptide (netcode). No test project exists; each task is verified by building and running the real game.

## Global Constraints

- **Layering:** Netcode writes Sim; View reads Sim and decides nothing; the sim never touches Stride types beyond math; all client sends go through `NetworkManager`; cross-registry wiring lives in `Client/Program.cs` only.
- **Wire:** this migration deliberately breaks the wire format ONCE (Task 2 deletes `WeaponType` and reshapes `WeaponState`; Task 5 renumbers `ObjectType`). Client and server always run from the same build, so this is safe — but never mix a rebuilt server with a stale client while testing. After Task 5, append-only discipline resumes: enum values and the `ComponentBundle` if-chain order are protocol.
- **Mutable struct components are FIELDS, not properties** (`ServerObject`, `NetObject`, `ComponentBundle`) — a property getter returns a copy.
- **Spawn state goes in `Spawn`'s `init` callback** (runs before the broadcast); state changed later needs `obj.Dirty |= <bit>` or it never leaves the server.
- **Never mutate the object dictionary while enumerating it** — find first, act after.
- **Verification protocol (every task):** `dotnet build DemiurgeSharp.slnx` must succeed with 0 errors, then run the real thing:
  - Server: `dotnet run --project Server/DemiurgeServer.csproj`
  - Client(s): `dotnet run` (from repo root), one or two instances as the task specifies.
  - An unfocused Stride window throttles its update loop — judge smoothness only on the focused window; background stutter is not a netcode bug.
- **Commit at the end of every task.** The game must build AND play at every commit.

## File Structure

| File | Role after this plan |
|---|---|
| `Common/Component.cs` | Wire enums (`ObjectType` scenery-only, `ItemType`, `NetComponents` + `Item` bit) and component structs (`ItemState` new, `WeaponState` slimmed) |
| `Common/ItemConfig.cs` (new) | `ItemCategory`, `EquipSlot`, `ItemStats`, `WeaponStats`, `ItemConfig` registry — all static item data |
| `Common/WeaponConfig.cs` | **deleted** (absorbed into ItemConfig.cs) |
| `Common/NetworkProtocol.cs` | + `ClientToServerId.PlayerInteract` |
| `Common/Messages/PlayerFiredData.cs` | carries `ItemType` instead of `WeaponType` |
| `Server/ItemSystem.cs` (new) | spawn-pickup, interact, equip, swap-drop, despawn-on-leave — all items |
| `Server/WeaponSystem.cs` | fire/reload validation only |
| `Server/ServerPlayer.cs` | `WeaponId` → `Dictionary<EquipSlot, uint> Equipped` |
| `Server/GameWorld.cs` | wires ItemSystem, `ApplyInteract`, spawns via `items.SpawnPickup` |
| `Server/GameServer.cs` | + `PlayerInteract` dispatch case |
| `Server/ServerObject.cs`, `Client/Simulation/NetObject.cs` | + `ItemState Item` field |
| `Server/ObjectReplication.cs`, `Client/Simulation/ObjectRegistry.cs` | + one `Item` line each (`Bundle` / `CopyComponents`) |
| `Client/Netcode/NetworkManager.cs` | + `SendInteract()` |
| `Client/Simulation/Player.cs` | + `TryInteract()`; `Equip` reads `ItemConfig` |
| `Client/View/ItemCosmetics.cs` (new) | model, attach node, seat, sounds, tracer per `ItemType` |
| `Client/View/WeaponCosmetics.cs` | **deleted** (absorbed) |
| `Client/View/ItemScripts.cs` (renamed from `WeaponScripts.cs`) | `PickupBobScript` (unchanged) + `ItemAttachScript` (replaces `WeaponAttachScript`) |
| `Client/View/ObjectViewFactory.cs` | mask-driven composition; builder table = scenery only |
| `Client/View/LocalPlayerController.cs` | + E key |
| `Client/View/PlayerCamera.cs`, `Client/View/ShotEffectsScript.cs` | read `local.Stats` / `ItemType` |
| `Client/Program.cs` | `LinkOwned` goes mask-based |

Task order is chosen so **every task ends with a game that builds and plays**: Task 1 is purely additive, Task 2 migrates weapon identity (game unchanged), Task 3 lands E-to-interact (still single-weapon), Task 4 makes the client view mask-driven, Task 5 lands ItemSystem + slots + swap and collapses ObjectType, Task 6 is the full verification pass.

---

### Task 1: ItemState on the wire, ItemConfig registry (purely additive)

**Files:**
- Modify: `Common/Component.cs`
- Create: `Common/ItemConfig.cs`
- Modify: `Server/ServerObject.cs`
- Modify: `Server/ObjectReplication.cs` (`Bundle`, ~line 112)
- Modify: `Client/Simulation/NetObject.cs`
- Modify: `Client/Simulation/ObjectRegistry.cs` (`CopyComponents`, ~line 70)

**Interfaces:**
- Consumes: existing `WeaponStats` record (still in `Common/WeaponConfig.cs` until Task 2).
- Produces: `enum ItemType : ushort { Ak47=1, AWP=2, Glock=3, BodyArmor=4 }`; `struct ItemState { ItemType Type }`; `NetComponents.Item = 1 << 5`; `enum ItemCategory : byte { Equippable, Item }`; `enum EquipSlot : byte { Hand, Chest, Head, Back }`; `record struct ItemStats(ItemCategory Category, EquipSlot Slot, WeaponStats? Weapon = null, float? Armor = null)`; `ItemConfig.Get(ItemType) → ItemStats`; `ItemConfig.GetWeapon(ItemType) → WeaponStats`. Field `ItemState Item` on `ServerObject`, `NetObject`, `ComponentBundle`.

- [ ] **Step 1: Add `ItemType`, `ItemState`, and the `Item` bit to `Common/Component.cs`**

After the `WeaponType` enum (leave `WeaponType` alone for now), add:

```csharp
    /// <summary>Which item an ItemState describes — every pickup/wearable/weapon
    /// in the game, one enum. Wire protocol (rides inside ItemState) and the key
    /// into ItemConfig + ItemCosmetics — append-only.</summary>
    public enum ItemType : ushort
    {
        Ak47 = 1,
        AWP = 2,
        Glock = 3,
        BodyArmor = 4,
    }
```

Append to `NetComponents`:

```csharp
        Armor = 1 << 4,
        Item = 1 << 5
```

After the `ArmorState` struct, add:

```csharp
    /// <summary>What an object IS, when it's an item. The mask around it is the
    /// semantics: Item+Transform = pickup in the world, Item+Owner = equipped.
    /// Static data (slot, stats, model) is looked up by Type in ItemConfig /
    /// ItemCosmetics — never on the wire.</summary>
    public struct ItemState : IMessageSerializable
    {
        public ItemType Type;
        public void Serialize(Message m) => m.AddUShort((ushort)Type);
        public void Deserialize(Message m) => Type = (ItemType)m.GetUShort();
    }
```

In `ComponentBundle`: add the field `public ItemState Item;` after `Armor`, and append one masked line at the END of `Serialize` AND `Deserialize` (the if-chain order is the wire format):

```csharp
            if (Mask.HasFlag(NetComponents.Item)) m.AddSerializable(Item);
```
```csharp
            if (Mask.HasFlag(NetComponents.Item)) Item = m.GetSerializable<ItemState>();
```

- [ ] **Step 2: Create `Common/ItemConfig.cs`**

The weapon numbers are duplicated from `WeaponConfig` for exactly one task — Task 2 deletes `WeaponConfig` and this becomes the single source.

```csharp
namespace Demiurge
{
    public enum ItemCategory : byte
    {
        /// <summary>Worn or held in an EquipSlot; picked up with E, swapping
        /// drops the current occupant back into the world.</summary>
        Equippable,
        /// <summary>Walk-over loot that goes into an inventory. Reserved —
        /// nothing spawns these yet; ApplyInteract ignores them.</summary>
        Item,
    }

    /// <summary>Where an equippable sits. Occupancy is a per-player dictionary
    /// keyed by this enum and nothing enumerates it — a new slot is one line.
    /// WHERE a slot sits on the body is each item's ItemCosmetics entry.</summary>
    public enum EquipSlot : byte
    {
        Hand,
        Chest,
        Head,
        Back,
    }

    /// <summary>Static per-item data. Never on the wire: both ends key into it
    /// by ItemState.Type — the same client-predicts/server-enforces contract as
    /// PlayerMovement. The Weapon/Armor sections double as the mask recipe:
    /// ItemSystem.SpawnPickup adds the WeaponState/ArmorState bits iff the
    /// section is present.</summary>
    public readonly record struct ItemStats(
        ItemCategory Category,
        EquipSlot Slot,
        WeaponStats? Weapon = null,
        float? Armor = null);

    public static class ItemConfig
    {
        public static ItemStats Get(ItemType type) => type switch
        {
            ItemType.Ak47 => new(ItemCategory.Equippable, EquipSlot.Hand,
                new WeaponStats(MagazineCapacity: 30, TicksPerShot: 3, ReloadTicks: 45, Damage: 10, MaxRange: 100f, shiftNear: 2.5f, shiftFar: 6.0f)),
            ItemType.AWP => new(ItemCategory.Equippable, EquipSlot.Hand,
                new WeaponStats(MagazineCapacity: 5, TicksPerShot: 60, ReloadTicks: 45, Damage: 75, MaxRange: 200f, shiftNear: 2.5f, shiftFar: 12.5f)),
            ItemType.Glock => new(ItemCategory.Equippable, EquipSlot.Hand,
                new WeaponStats(MagazineCapacity: 15, TicksPerShot: 7, ReloadTicks: 20, Damage: 5, MaxRange: 50f, shiftNear: 1.5f, shiftFar: 2.5f)),
            ItemType.BodyArmor => new(ItemCategory.Equippable, EquipSlot.Chest, Armor: 50f),

            // Unknown type off the wire: AK stand-in rather than a crash.
            _ => new(ItemCategory.Equippable, EquipSlot.Hand, new WeaponStats(30, 3, 45, 10, 100f, 1.5f, 2.5f)),
        };

        /// <summary>The weapon section, or the AK fallback for call sites that
        /// know they hold a gun (fire/reload gates on the WeaponState bit first).</summary>
        public static WeaponStats GetWeapon(ItemType type) =>
            Get(type).Weapon ?? new WeaponStats(30, 3, 45, 10, 100f, 1.5f, 2.5f);
    }
}
```

- [ ] **Step 3: Mirror the field into the four plumbing spots**

`Server/ServerObject.cs` — add after `public ArmorState Armor;`:
```csharp
        public ItemState Item;
```

`Server/ObjectReplication.cs` — in `Bundle(...)`, add after `Armor = obj.Armor`:
```csharp
            Item = obj.Item
```
(mind the comma on the previous line).

`Client/Simulation/NetObject.cs` — add after `public ArmorState Armor;`:
```csharp
    public ItemState Item;
```

`Client/Simulation/ObjectRegistry.cs` — in `CopyComponents`, add at the end:
```csharp
        if (state.Mask.HasFlag(NetComponents.Item)) obj.Item = state.Item;
```

- [ ] **Step 4: Build**

Run: `dotnet build DemiurgeSharp.slnx`
Expected: 0 errors. Nothing spawns with the `Item` bit yet, so behavior is unchanged — no run needed.

- [ ] **Step 5: Commit**

```bash
git add Common/Component.cs Common/ItemConfig.cs Server/ServerObject.cs Server/ObjectReplication.cs Client/Simulation/NetObject.cs Client/Simulation/ObjectRegistry.cs
git commit -m "Add ItemState component, ItemType, and ItemConfig registry (additive)"
```

---

### Task 2: Migrate weapon identity to ItemState (game plays identically)

**Files:**
- Modify: `Common/Component.cs` (delete `WeaponType`, slim `WeaponState`)
- Delete: `Common/WeaponConfig.cs` (move the `WeaponStats` record into `Common/ItemConfig.cs`)
- Modify: `Common/GunConfig.cs` (one stale comment)
- Modify: `Common/Messages/PlayerFiredData.cs`
- Modify: `Server/WeaponSystem.cs`
- Modify: `Server/GameWorld.cs` (spawn calls)
- Create: `Client/View/ItemCosmetics.cs`; Delete: `Client/View/WeaponCosmetics.cs`
- Modify: `Client/View/ObjectViewFactory.cs` (builders read `ItemState`)
- Modify: `Client/Simulation/Player.cs` (`Equip`)
- Modify: `Client/View/PlayerCamera.cs` (~line 77)
- Modify: `Client/View/ShotEffectsScript.cs`

**Interfaces:**
- Consumes: `ItemType`, `ItemState`, `ItemConfig.Get/GetWeapon`, `NetComponents.Item` (Task 1).
- Produces: `WeaponState { int CurrentAmmo }` (no Type); `PlayerFiredData.Weapon : ItemType`; `ItemCosmetics.Get(ItemType) → Entry(ModelPath, AttachNode, Seat, SeatRotation, ShotSoundPath, TracerColor)`; `WeaponSystem.SpawnPickup(ItemType, Vector3)`. Every weapon/armor object now spawns with `NetComponents.Item` in its mask and `obj.Item` set.

- [ ] **Step 1: Reshape the wire structs in `Common/Component.cs`**

Delete the `WeaponType` enum entirely. Replace `WeaponState` with:

```csharp
    /// <summary>Live weapon state only — WHICH gun lives in ItemState; static
    /// numbers live in ItemConfig, keyed by ItemState.Type, so they can't
    /// drift mid-match. Present only on items whose config has a weapon section.</summary>
    public struct WeaponState : IMessageSerializable
    {
        public int CurrentAmmo;
        public void Serialize(Message m) => m.AddInt(CurrentAmmo);
        public void Deserialize(Message m) => CurrentAmmo = m.GetInt();
    }
```

- [ ] **Step 2: Move `WeaponStats` into `Common/ItemConfig.cs`, delete `Common/WeaponConfig.cs`**

Cut this record from `WeaponConfig.cs` and paste it into `ItemConfig.cs` (above `ItemStats`), then delete the file:

```csharp
    /// <summary>Static per-weapon numbers, the weapon section of ItemStats.
    /// Cadence is in server ticks so both ends count the same clock.</summary>
    public readonly record struct WeaponStats(
        int MagazineCapacity,
        int TicksPerShot,
        int ReloadTicks,
        ushort Damage,
        float MaxRange,
        float shiftNear,
        float shiftFar);
```

```bash
git rm Common/WeaponConfig.cs
```

In `Common/GunConfig.cs`, fix the stale comment: `Per-weapon numbers live in WeaponConfig.` → `Per-weapon numbers live in ItemConfig.` Same in `Common/GunMath.cs`: `(per-weapon, see WeaponConfig)` → `(per-weapon, see ItemConfig)`.

- [ ] **Step 3: `Common/Messages/PlayerFiredData.cs` carries `ItemType`**

Change the field and both casts (wire shape is unchanged — still a ushort):

```csharp
        public ItemType Weapon;
```
```csharp
            message.AddUShort((ushort)Weapon);
```
```csharp
            Weapon = (ItemType)message.GetUShort();
```

- [ ] **Step 4: `Server/WeaponSystem.cs` — identity via ItemState**

Replace `SpawnPickup`:

```csharp
        public ServerObject SpawnPickup(ItemType type, Vector3 position)
        {
            return objects.Spawn(ObjectType.WeaponPickup, NetComponents.Transform | NetComponents.Item | NetComponents.Weapon, position,
            obj =>
            {
                obj.Item = new ItemState { Type = type };
                obj.Weapon = new WeaponState { CurrentAmmo = ItemConfig.GetWeapon(type).MagazineCapacity };
            });
        }
```

In `TryPickup`, replace the despawn/spawn block (ammo AND identity carry over):

```csharp
            var carriedItem = pickup.Item;
            var carriedWeapon = pickup.Weapon;      // ammo carries over from the pickup
            objects.Despawn(pickup.NetworkId);

            var weapon = objects.Spawn(ObjectType.EquippedWeapon, NetComponents.Item | NetComponents.Weapon | NetComponents.Owner, player.Position,
                obj =>
                {
                    obj.Item = carriedItem;
                    obj.Weapon = carriedWeapon;
                    obj.Owner = new OwnerState { PlayerId = player.Id };
                });
            player.WeaponId = weapon.NetworkId;
```

In `ApplyFire`: `var stats = WeaponConfig.Get(weapon.Weapon.Type);` → `var stats = ItemConfig.GetWeapon(weapon.Item.Type);` and in the `PlayerFiredData` literal: `Weapon = weapon.Weapon.Type,` → `Weapon = weapon.Item.Type,`.

In `ApplyReload`: `var stats = WeaponConfig.Get(weapon.Weapon.Type);` → `var stats = ItemConfig.GetWeapon(weapon.Item.Type);`.

- [ ] **Step 5: `Server/GameWorld.cs` — spawns carry ItemState**

Replace the constructor's spawn block:

```csharp
            // Transitional: inline armor init until ItemSystem.SpawnPickup exists (Task 5).
            var armor = ItemConfig.Get(ItemType.BodyArmor);
            objects.Spawn(ObjectType.ArmorPickup, NetComponents.Transform | NetComponents.Item | NetComponents.Armor, new Vector3(3f, 0f, 3f),
            obj =>
            {
                obj.Item = new ItemState { Type = ItemType.BodyArmor };
                obj.Armor = new ArmorState { MaxValue = armor.Armor!.Value, Current = armor.Armor.Value };
            });

            weapons.SpawnPickup(ItemType.AWP, new Vector3(3f, 0f, 0f));
            weapons.SpawnPickup(ItemType.Ak47, new Vector3(-3f, 0f, -3f));
            weapons.SpawnPickup(ItemType.Glock, new Vector3(-5f, 0f, -5f));
```

- [ ] **Step 6: Create `Client/View/ItemCosmetics.cs`, delete `Client/View/WeaponCosmetics.cs`**

```csharp
using Demiurge;
using Stride.Core.Mathematics;

// Client-only item cosmetics, keyed by the same ItemType as ItemConfig.
// Kept out of Common so the wire protocol and the server stay view-free.
// AttachNode is WHERE on the owner's body an equipped item sits: a bone name
// (from the rig — dump the gltf's node names if unsure) links via
// ModelNodeLinkComponent; null follows the Player_{id} entity root instead.
public static class ItemCosmetics
{
    public readonly record struct Entry(
        string ModelPath,
        string? AttachNode,
        Vector3 Seat,
        Quaternion SeatRotation,
        string ShotSoundPath,
        Color TracerColor);

    // Bone-relative gun seat, carried over from the old WeaponAttachScript.
    private static readonly Vector3 HandSeat = new(0.0f, -3.75f / 16.0f, 0.425f / 16.0f);
    private static readonly Quaternion HandRotation = Quaternion.RotationX(MathF.PI / 2.0f) * Quaternion.RotationZ(MathF.PI);

    public static Entry Get(ItemType type) => type switch
    {
        ItemType.Ak47 => new("assets/models/ak47.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.AWP => new("assets/models/sniper_rifle.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.Glock => new("assets/models/glock.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
        ItemType.BodyArmor => new("assets/models/body_armor.gltf", null, Vector3.Zero, Quaternion.Identity, "assets/sfx/ak47_shot.wav", Color.Yellow),

        // Unknown type off the wire: AK stand-ins rather than a crash.
        _ => new("assets/models/ak47.gltf", "right_hand", HandSeat, HandRotation, "assets/sfx/ak47_shot.wav", Color.Yellow),
    };
}
```

```bash
git rm Client/View/WeaponCosmetics.cs
```

- [ ] **Step 7: `Client/View/ObjectViewFactory.cs` — builders read ItemState**

In the three item builder entries, replace `WeaponCosmetics.Get(obj.Weapon.Type).ModelPath` with `ItemCosmetics.Get(obj.Item.Type).ModelPath`, and the hardcoded armor path with the same call:

```csharp
            [ObjectType.WeaponPickup] = obj => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Get(obj.Item.Type).ModelPath)),
                  new PickupBobScript { Object = obj } },
            [ObjectType.EquippedWeapon] = obj => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Get(obj.Item.Type).ModelPath)),
                  new WeaponAttachScript { Object = obj } },
            [ObjectType.ArmorPickup] = obj => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Get(obj.Item.Type).ModelPath)),
                  new PickupBobScript { Object = obj } },
```

- [ ] **Step 8: Client sim + view read sites**

`Client/Simulation/Player.cs`, in `Equip`:
```csharp
        Stats = ItemConfig.GetWeapon(weapon.Item.Type);
```

`Client/View/PlayerCamera.cs` (~line 75): the stats are already cached on the local player — stop re-looking them up:
```csharp
			if (local.Weapon != null)
			{
				aimingShiftNear = local.Stats.shiftNear;
				aimingShiftFar = local.Stats.shiftFar;
			}
```

`Client/View/ShotEffectsScript.cs`:
- `OnLocalShot`: `PlayEffects(origin, direction, local.Weapon!.Weapon.Type, local.Stats.MaxRange);` → `PlayEffects(origin, direction, local.Weapon!.Item.Type, local.Stats.MaxRange);`
- `OnRemoteFired`: `WeaponConfig.Get(data.Weapon).MaxRange` → `ItemConfig.GetWeapon(data.Weapon).MaxRange`
- `PlayEffects` signature: `WeaponType weapon` → `ItemType weapon`
- `var cosmetics = WeaponCosmetics.Get(weapon);` → `var cosmetics = ItemCosmetics.Get(weapon);`

- [ ] **Step 9: Build, then verify the game is unchanged**

Run: `dotnet build DemiurgeSharp.slnx` — expect 0 errors (a leftover `WeaponType`/`WeaponConfig` reference anywhere will fail the build; the compiler is the checklist here).

Run server + two clients. Expected, exactly as before this task: guns bob and are picked up on walk-over; equipped gun sits in the hand on both screens; fire, reload, HUD ammo, tracers, hit sounds, armor pickup (now rendered from ItemCosmetics) bobbing at (3, 0, 3).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "Migrate item identity into ItemState; delete WeaponType/WeaponConfig/WeaponCosmetics"
```

---

### Task 3: E-to-interact plumbing (walk-over pickup dies)

**Files:**
- Modify: `Common/NetworkProtocol.cs`
- Modify: `Client/Netcode/NetworkManager.cs`
- Modify: `Client/Simulation/Player.cs` (`LocalPlayer`)
- Modify: `Client/View/LocalPlayerController.cs`
- Modify: `Server/GameServer.cs`
- Modify: `Server/GameWorld.cs`

**Interfaces:**
- Consumes: `WeaponSystem.TryPickup(ServerPlayer)` (existing, deleted in Task 5).
- Produces: `ClientToServerId.PlayerInteract`; `NetworkManager.SendInteract()`; `LocalPlayer.TryInteract()`; `GameWorld.ApplyInteract(ushort clientId)` — the exact hook `ItemSystem` takes over in Task 5.

- [ ] **Step 1: Append the message id**

`Common/NetworkProtocol.cs`:
```csharp
    public enum ClientToServerId : ushort
    {
        PlayerInput = 1,
        PlayerFire,
        PlayerReload,
        PlayerInteract
    }
```

- [ ] **Step 2: Client send path**

`Client/Netcode/NetworkManager.cs`, after `SendReload` (reliable — losing an E press would eat the pickup):
```csharp
        public void SendInteract()
        {
            client.Send(Message.Create(MessageSendMode.Reliable, ClientToServerId.PlayerInteract));
        }
```

`Client/Simulation/Player.cs`, on `LocalPlayer` after `TryReload`:
```csharp
    /// <summary>E pressed: ask the server to pick up / swap whatever is nearby.
    /// Nothing is predicted — the outcome arrives as ordinary object
    /// spawn/despawn replication and flows through Equip/Unequip.</summary>
    public void TryInteract() => network.SendInteract();
```

`Client/View/LocalPlayerController.cs`, after the reload check:
```csharp
		if (Input.IsKeyPressed(Keys.E))
			local.TryInteract();
```

- [ ] **Step 3: Server dispatch + handler; delete per-tick pickup**

`Server/GameServer.cs`, new case in `OnMessageReceived`:
```csharp
                case ClientToServerId.PlayerInteract:
                    world.ApplyInteract(e.FromConnection.Id);
                    break;
```

`Server/GameWorld.cs`, next to `ApplyReload`:
```csharp
        public void ApplyInteract(ushort clientId)
        {
            if (players.TryGetValue(clientId, out var player))
                weapons.TryPickup(player);   // transitional: ItemSystem takes this over in Task 5
        }
```

In `Tick(...)`, DELETE the line `weapons.TryPickup(player);`.

- [ ] **Step 4: Build and verify**

Run: `dotnet build DemiurgeSharp.slnx` — 0 errors.

Run server + one client. Expected: walking over a gun does NOT pick it up anymore; standing near it and pressing E equips it. While armed, E near another gun does nothing (single `WeaponId` slot still — swap arrives in Task 5). E near the armor does nothing yet (TryPickup only matches `ObjectType.WeaponPickup`). Fire/reload unchanged.

- [ ] **Step 5: Commit**

```bash
git add Common/NetworkProtocol.cs Client/Netcode/NetworkManager.cs Client/Simulation/Player.cs Client/View/LocalPlayerController.cs Server/GameServer.cs Server/GameWorld.cs
git commit -m "Add E-to-interact message; retire walk-over pickup"
```

---

### Task 4: Client view goes mask-driven (ItemAttachScript, composition factory)

**Files:**
- Rename: `Client/View/WeaponScripts.cs` → `Client/View/ItemScripts.cs`
- Modify: `Client/View/ItemScripts.cs` (`WeaponAttachScript` → `ItemAttachScript`)
- Modify: `Client/View/ObjectViewFactory.cs`
- Modify: `Client/Program.cs` (`LinkOwned` + despawn handler)

**Interfaces:**
- Consumes: `NetObject.Item`, `NetComponents.Item` (Task 1), `ItemCosmetics.Get` (Task 2).
- Produces: `ItemAttachScript { NetObject Object }` — attaches any `Item+Owner` object per its cosmetics entry. The view no longer references item ObjectTypes anywhere (which is what lets Task 5 delete them).

- [ ] **Step 1: Rename the scripts file and generalize the attach script**

```bash
git mv Client/View/WeaponScripts.cs Client/View/ItemScripts.cs
```

In `ItemScripts.cs`, keep `PickupBobScript` exactly as-is. Replace `WeaponAttachScript` (whole class, including its comment block) with:

```csharp
// Attaches a worn item's view to its owner's body — driven by the replicated
// Owner component. The wire says WHOSE body; WHERE on the body is cosmetic
// (ItemCosmetics): a bone name links via ModelNodeLinkComponent, null follows
// the Player_{id} entity root every frame (body armor). The owner's view may
// not exist yet when this spawns (reliable messages aren't ordered relative to
// each other), so it retries every frame until the player appears.
//
// The entity stays at the scene root on purpose: ModelNodeLinkComponent drives
// its world transform from the bone regardless of hierarchy, root-following
// composes the owner's transform explicitly, and root-level entities keep
// ObjectViewFactory.DestroyView's find-by-name working.
public class ItemAttachScript : SyncScript
{
    public required NetObject Object { get; init; }

    private Entity? owner;
    private bool boneLinked;

    public override void Update()
    {
        owner ??= Entity.Scene?.Entities.FirstOrDefault(e => e.Name == $"Player_{Object.Owner.PlayerId}");
        if (owner == null) return;

        var cosmetics = ItemCosmetics.Get(Object.Item.Type);
        if (cosmetics.AttachNode is { } node)
        {
            if (boneLinked) return;   // latch: ownership never changes in place (transitions are despawn/respawn)
            if (owner.Get<ModelComponent>() is not { } ownerModel) return;

            Entity.Add(new ModelNodeLinkComponent
            {
                Target = ownerModel,
                NodeName = node,
            });
            Entity.Transform.Position = cosmetics.Seat;
            Entity.Transform.Rotation = cosmetics.SeatRotation;
            boneLinked = true;
        }
        else
        {
            // Root-worn items follow the owner's root transform every frame —
            // PlayerViewScript drives that entity, this composes the seat on top.
            Entity.Transform.Position = owner.Transform.Position + Vector3.Transform(cosmetics.Seat, owner.Transform.Rotation);
            Entity.Transform.Rotation = owner.Transform.Rotation * cosmetics.SeatRotation;
        }
    }
}
```

- [ ] **Step 2: Rewrite `Client/View/ObjectViewFactory.cs` — composition over types**

Replace the whole file body (constructor + `CreateView`; `DestroyView` is unchanged):

```csharp

using Demiurge;
using Demiurge.GameClient;
using Stride.CommunityToolkit.Bepu;
using Stride.CommunityToolkit.Rendering.ProceduralModels;
using Stride.Engine;

public class ObjectViewFactory
{
    private readonly Game game;
    private readonly Scene scene;

    // Scenery only. Items never appear here: their model comes from
    // ItemCosmetics and their behavior from the component mask.
    private readonly Dictionary<ObjectType, Func<NetObject, Entity>> builders;

    public ObjectViewFactory(Game game, Scene scene, ObjectRegistry registry)
    {
        this.game = game;
        this.scene = scene;
        builders = new()
        {
            [ObjectType.Crate] = _ => game.Create3DPrimitive(PrimitiveModelType.Cube,
                                          new() { IncludeCollider = false }),
            [ObjectType.TrainingDummy] = _ => new Entity {
                  new ModelComponent(GLTFLoader.LoadModel(game, "assets/models/dummy.gltf")) },
        };
        registry.ObjectSpawned += CreateView;
        registry.ObjectDespawned += DestroyView;
    }

    private void CreateView(NetObject obj)
    {
        bool isItem = obj.Has.HasFlag(NetComponents.Item);

        Entity entity;
        if (isItem)
            entity = new Entity { new ModelComponent(GLTFLoader.LoadModel(game, ItemCosmetics.Get(obj.Item.Type).ModelPath)) };
        else if (builders.TryGetValue(obj.Type, out var build))
            entity = build(obj);
        else return;   // no visual (PlayerStatus, unknown types): skip, don't crash

        entity.Name = $"NetObject_{obj.NetworkId}";

        // View behavior per component the object HAS — the mask decides.
        // Item+Transform sits in the world: the bob presenter OWNS the entity
        // transform (so no NetTransformScript alongside). Item+Owner is worn:
        // the attach presenter owns it instead.
        if (isItem && obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new PickupBobScript { Object = obj });
        if (isItem && obj.Has.HasFlag(NetComponents.Owner)) entity.Add(new ItemAttachScript { Object = obj });
        if (!isItem && obj.Has.HasFlag(NetComponents.Transform)) entity.Add(new NetTransformScript { Object = obj });
        if (obj.Has.HasFlag(NetComponents.Health)) entity.Add(new HealthScaleScript { Object = obj });

        entity.Transform.Position = obj.Transform.Position.ToStride();
        entity.Scene = scene;
    }

    private void DestroyView(NetObject obj)
    {
        if (scene.Entities.FirstOrDefault(e => e.Name == $"NetObject_{obj.NetworkId}") is { } entity)
        {
            scene.Entities.Remove(entity);
            entity.Scene = null;
        }
    }
}
```

- [ ] **Step 3: `Client/Program.cs` — LinkOwned goes mask-based**

Replace `LinkOwned` and the despawn handler:

```csharp
// Bridge the two registries: objects owned by our client id attach to the local
// player. Sim-to-sim glue lives here in the composition root. Mask-driven: an
// owned object with WeaponState is our gun, whatever ItemType it is.
void LinkOwned(LocalPlayer local, NetObject obj)
{
    if (!obj.Has.HasFlag(Demiurge.NetComponents.Owner) || obj.Owner.PlayerId != network.ClientId) return;
    if (obj.Has.HasFlag(Demiurge.NetComponents.Weapon)) local.Equip(obj);
    if (obj.Type == Demiurge.ObjectType.PlayerStatus) local.Status = obj;
}
```

```csharp
objectRegistry.ObjectDespawned += obj =>
{
    if (registry.LocalPlayer is not {} local) return;
    // Unequip guards on reference equality, so a despawning weapon PICKUP
    // (also Weapon-masked, but never our equipped object) is a no-op.
    if (obj.Has.HasFlag(Demiurge.NetComponents.Weapon)) local.Unequip(obj);
    if (ReferenceEquals(local.Status, obj)) local.Status = null;
};
```

- [ ] **Step 4: Build and verify (two clients)**

Run: `dotnet build DemiurgeSharp.slnx` — 0 errors (the compiler will catch any remaining `WeaponAttachScript` reference).

Run server + two clients. Expected: identical behavior to Task 3 — pickups bob, E equips a gun into the hand on both screens, tracers/HUD work. This proves the mask path end-to-end before the server changes underneath it.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Mask-driven object views; generalize WeaponAttachScript into ItemAttachScript"
```

---

### Task 5: Server ItemSystem — slots, swap-drop, ObjectType collapse

**Files:**
- Create: `Server/ItemSystem.cs`
- Modify: `Server/ServerPlayer.cs`
- Modify: `Server/WeaponSystem.cs` (shrinks to fire/reload)
- Modify: `Server/GameWorld.cs`
- Modify: `Common/Component.cs` (`ObjectType` collapse)

**Interfaces:**
- Consumes: `ObjectReplication.Spawn/Despawn/TryGet/All`; `ItemConfig`/`ItemStats` (Task 1); `GameWorld.ApplyInteract` hook (Task 3).
- Produces: `ItemSystem.SpawnPickup(ItemType, Vector3) → ServerObject`, `ItemSystem.ApplyInteract(ServerPlayer)`, `ItemSystem.DespawnFor(ServerPlayer)`; `ServerPlayer.Equipped : Dictionary<EquipSlot, uint>`; `ObjectType { Crate=1, TrainingDummy, PlayerStatus, Item }`.

- [ ] **Step 1: Collapse `ObjectType` in `Common/Component.cs`**

```csharp
    /// <summary>What kind of scenery/logic thing to build on spawn. Items are
    /// NOT here — they're all ObjectType.Item, and ItemState + the component
    /// mask say everything else. Only the view interprets this; the replication
    /// plumbing carries it opaquely. Append-only from here on.</summary>
    public enum ObjectType : ushort {
        Crate = 1,
        TrainingDummy,
        PlayerStatus,
        Item
    }
```

(This renumbers `PlayerStatus`. Wire break already accepted; both ends ship from this build.)

- [ ] **Step 2: `Server/ServerPlayer.cs` — slots**

Replace the `WeaponId` property (keep `Status`, `NextFireTick`, `ReloadDoneTick`):

```csharp
        // What the player wears and holds: slot -> NetworkId of the equipped
        // item object. Live state (ammo) lives ON the objects; the player just
        // holds the references and the fire/reload timing gates below.
        public Dictionary<EquipSlot, uint> Equipped { get; } = new();
```

- [ ] **Step 3: Create `Server/ItemSystem.cs`**

```csharp
using System.Numerics;

namespace Demiurge.GameServer
{
    /// <summary>Pickups, equipping, swapping, dropping — for every item kind.
    /// Owns no storage: items live in ObjectReplication as Item-masked objects,
    /// slot occupancy lives on ServerPlayer.Equipped, and what an item IS comes
    /// from ItemConfig. Transitions only move mask bits around:
    /// pickup = Item|Transform(+traits), equipped = Item|Owner(+traits) — every
    /// trait bit (Weapon, Armor, future ones) carries across untouched.</summary>
    public class ItemSystem
    {
        private readonly ObjectReplication objects;

        private const float PickupRadiusSq = 0.75f * 0.75f;

        public ItemSystem(ObjectReplication objects) => this.objects = objects;

        public ServerObject SpawnPickup(ItemType type, Vector3 position)
        {
            // The config's sections are the mask recipe: a weapon section means
            // a WeaponState bit, an armor section an ArmorState bit, and so on.
            var stats = ItemConfig.Get(type);
            var mask = NetComponents.Item | NetComponents.Transform;
            if (stats.Weapon != null) mask |= NetComponents.Weapon;
            if (stats.Armor != null) mask |= NetComponents.Armor;

            return objects.Spawn(ObjectType.Item, mask, position, obj =>
            {
                obj.Item = new ItemState { Type = type };
                if (stats.Weapon is { } w) obj.Weapon = new WeaponState { CurrentAmmo = w.MagazineCapacity };
                if (stats.Armor is { } a) obj.Armor = new ArmorState { MaxValue = a, Current = a };
            });
        }

        /// <summary>E pressed: equip the nearest pickup in radius, swapping out
        /// whatever occupies its slot. Server-authoritative — the client sends
        /// no target, so there is nothing to validate beyond proximity.</summary>
        public void ApplyInteract(ServerPlayer player)
        {
            // Find first, act after: Despawn/Spawn mutate the object dictionary
            // and must not run inside its enumeration.
            ServerObject? pickup = null;
            float bestSq = PickupRadiusSq;
            foreach (var obj in objects.All)
            {
                if (!obj.Has.HasFlag(NetComponents.Item | NetComponents.Transform)) continue;
                float dSq = Vector3.DistanceSquared(obj.Transform.Position, player.Position);
                if (dSq > bestSq) continue;
                pickup = obj;
                bestSq = dSq;
            }
            if (pickup == null) return;

            var stats = ItemConfig.Get(pickup.Item.Type);
            if (stats.Category != ItemCategory.Equippable) return;   // walk-over inventory items: future

            Equip(player, pickup, stats.Slot);
        }

        private void Equip(ServerPlayer player, ServerObject pickup, EquipSlot slot)
        {
            // Swap: the current occupant drops where the player stands.
            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
                Drop(current, player.Position);

            // pickup -> equipped: despawn + respawn with Transform swapped for
            // Owner. Live state (ammo) rides the carried structs.
            var carried = (pickup.Item, pickup.Weapon, pickup.Armor);
            var mask = (pickup.Has & ~NetComponents.Transform) | NetComponents.Owner;
            objects.Despawn(pickup.NetworkId);

            var equipped = objects.Spawn(ObjectType.Item, mask, player.Position, obj =>
            {
                (obj.Item, obj.Weapon, obj.Armor) = carried;
                obj.Owner = new OwnerState { PlayerId = player.Id };
            });
            player.Equipped[slot] = equipped.NetworkId;
        }

        private void Drop(ServerObject equipped, Vector3 position)
        {
            // equipped -> pickup: the mirror image of Equip's transition.
            var carried = (equipped.Item, equipped.Weapon, equipped.Armor);
            var mask = (equipped.Has & ~NetComponents.Owner) | NetComponents.Transform;
            objects.Despawn(equipped.NetworkId);

            objects.Spawn(ObjectType.Item, mask, position,
                obj => (obj.Item, obj.Weapon, obj.Armor) = carried);
        }

        /// <summary>Everything worn leaves with its owner. Call from RemovePlayer.
        /// Miss this and every client keeps orphan views retrying their attach
        /// forever.</summary>
        public void DespawnFor(ServerPlayer player)
        {
            foreach (uint networkId in player.Equipped.Values)
                objects.Despawn(networkId);
            player.Equipped.Clear();
        }
    }
}
```

(Note: `Has.HasFlag(NetComponents.Item | NetComponents.Transform)` requires BOTH bits — that's the pickup test.)

- [ ] **Step 4: `Server/WeaponSystem.cs` shrinks to fire/reload**

DELETE: `SpawnPickup`, `TryPickup`, `DespawnFor`, and the `PickupRadiusSq` constant. Update the class comment:

```csharp
    /// <summary>Server-authoritative fire/reload validation — the one weapon-
    /// specific system. Pickup/equip/swap belong to ItemSystem; weapons live in
    /// ObjectReplication like every item; timing gates live on ServerPlayer.
    /// GameWorld resolves clientId -> ServerPlayer and delegates.</summary>
```

In `ApplyFire`, replace the weapon resolution:

```csharp
            // Unarmed players can't fire. The equipped Hand item is the source
            // of truth for ammo — IF it's a gun (Weapon bit); a future non-gun
            // hand item simply can't fire.
            if (!player.Equipped.TryGetValue(EquipSlot.Hand, out uint weaponId)
                || !objects.TryGet(weaponId, out var weapon)
                || !weapon.Has.HasFlag(NetComponents.Weapon)) return;
            var stats = ItemConfig.GetWeapon(weapon.Item.Type);
```

In `ApplyReload`, the same resolution:

```csharp
            if (!player.Equipped.TryGetValue(EquipSlot.Hand, out uint weaponId)
                || !objects.TryGet(weaponId, out var weapon)
                || !weapon.Has.HasFlag(NetComponents.Weapon)) return;

            var stats = ItemConfig.GetWeapon(weapon.Item.Type);
```

- [ ] **Step 5: `Server/GameWorld.cs` — wire ItemSystem**

Add the field and construct it (ItemSystem needs no `server` — ObjectReplication does all the sending):

```csharp
        private readonly ObjectReplication objects;
        private readonly ItemSystem items;
        private readonly WeaponSystem weapons;
```
```csharp
            objects = new ObjectReplication(server);
            items = new ItemSystem(objects);
            weapons = new WeaponSystem(server, objects);

            items.SpawnPickup(ItemType.BodyArmor, new Vector3(3f, 0f, 3f));
            items.SpawnPickup(ItemType.AWP, new Vector3(3f, 0f, 0f));
            items.SpawnPickup(ItemType.Ak47, new Vector3(-3f, 0f, -3f));
            items.SpawnPickup(ItemType.Glock, new Vector3(-5f, 0f, -5f));
```

`ApplyInteract` hands off to the item system:

```csharp
        public void ApplyInteract(ushort clientId)
        {
            if (players.TryGetValue(clientId, out var player))
                items.ApplyInteract(player);
        }
```

In `RemovePlayer`: `weapons.DespawnFor(player);` → `items.DespawnFor(player);`.

- [ ] **Step 6: Build and verify the full feature (two clients + late joiner)**

Run: `dotnet build DemiurgeSharp.slnx` — 0 errors (stragglers referencing `WeaponPickup`/`EquippedWeapon`/`ArmorPickup`/`EquippedArmor` or `player.WeaponId` fail here by design).

Run server + two clients, then the full matrix:

1. **Armor equips to the root:** E on the bobbing armor → pickup despawns, armor appears on the player model (root-attached) on BOTH screens, moves with the player.
2. **Slots are independent:** with armor worn, E on a rifle → now holding a gun AND wearing armor.
3. **Swap drops with live state:** fire a few AWP rounds, walk to the AK, E → AWP drops at your feet (bobbing pickup), AK equips. E again on the dropped AWP → it comes back with the reduced ammo count on the HUD.
4. **Combat regression:** fire, reload, hit the other player, HUD ammo, tracers, kill/respawn all behave as before.
5. **Disconnect:** close one client → its gun AND armor views vanish on the survivor's screen.
6. **Late joiner:** start a third client → it sees remaining pickups bobbing and the other players' worn items attached correctly.

(Expect the body armor to sit at the player origin — seat tuning is Task 6.)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Add ItemSystem: slot-based equip and swap-drop; collapse item ObjectTypes"
```

---

### Task 6: Seat tuning + docs

**Files:**
- Modify: `Client/View/ItemCosmetics.cs` (BodyArmor `Seat`/`SeatRotation` values)
- Modify: `README.md` (checklist)

**Interfaces:**
- Consumes: `ItemCosmetics.Entry.Seat` / `SeatRotation` (Task 2).
- Produces: final tuned values; no code surface changes.

- [ ] **Step 1: Tune the armor seat by eye**

Run server + one client, equip the armor, and adjust the `ItemType.BodyArmor` entry's `Seat` (start by raising Y until it sits on the torso, e.g. `new Vector3(0f, 0.5f, 0f)`) and `SeatRotation` until it looks right. This is a look-at-the-screen loop — rebuild between tweaks.

- [ ] **Step 2: Verify once more, both clients**

Armor placement reads correctly on the wearer from the other client's view (remember: judge only the focused window).

- [ ] **Step 3: Update `README.md`**

Under `- [ ] add wearables`, check off `- [x] add armor` (leave `add helmet` unchecked — it's now a data-row exercise; see RECIPES.md "Add an equippable item").

- [ ] **Step 4: Commit**

```bash
git add Client/View/ItemCosmetics.cs README.md
git commit -m "Tune body armor seat; check off armor in README"
```

---

## Deviations from the standard task template

This project has no automated test harness, and the changed surface is netcode + a Stride view layer — the established verification method is building and driving the real game with two clients (plus a late joiner for catch-up bugs). Each task therefore replaces the write-test/run-test cycle with an explicit build gate and a concrete observable-behavior checklist. The compiler acts as the migration checklist in Tasks 2 and 5: deleting `WeaponType` and the item ObjectTypes forces every stale call site to surface as a build error.
