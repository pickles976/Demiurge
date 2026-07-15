# Generalized Item System — Design

**Date:** 2026-07-15
**Status:** Approved

## Problem

Pickup/equip logic was pulled into `WeaponSystem` prematurely, and item kinds are
multiplying as paired `ObjectType` values (`WeaponPickup`/`EquippedWeapon`,
`ArmorPickup`/`EquippedArmor`) with near-identical handling. We want armor,
weapons, hats, helmets, and backpacks to share one system: one pickup path, one
equip path, one attach-to-player-model path, without a new component or
ObjectType per item kind.

## Requirements

- **Equippables vs Items.** Equippables (weapons, armor, hats…) are worn/held in
  a slot and picked up with **E**. Items (future) are walked over and go into an
  inventory. This pass builds equippables only, but the data model names both
  categories so items slot in later.
- **Slots.** Every equippable declares an equip slot. Adding a new slot must be
  trivial (one enum line). A player can hold one equippable per slot
  simultaneously (gun + armor + hat).
- **E to pick up; swap if occupied.** Pressing E near a pickup equips it. If the
  slot is occupied, the current occupant drops back into the world as a pickup
  (live state, e.g. ammo, preserved) and the new one equips. Walk-over
  auto-pickup for weapons is deleted — one interact path for everything.
- **Armor is visual-only this pass.** It equips, replicates, and parents to the
  player model root on every client. Damage math is untouched.
- **Containers: design for, don't build.** A backpack later gains a replicated
  inventory component; nothing is implemented now.
- **Component-driven (Approach B).** Semantics live in the component mask, not
  in ObjectType. Chosen over (A) keeping `ItemPickup`/`EquippedItem`
  ObjectTypes, and (C) keeping per-category ObjectTypes with shared code, for
  long-term scalability: new item kinds and behaviors compose from components
  instead of enumerating types.

## Design

### 1. Wire protocol & components

- **`ItemType : ushort`** (Common, append-only): the one identity enum for every
  item — `Ak47`, `AWP`, `Glock`, `BodyArmor`, later `Helmet`, `Backpack`…
  `WeaponType` is deleted and merged into it.
- **New net component `ItemState { ItemType Type }`** — next `NetComponents`
  bit, new field in `ComponentBundle`, `ServerObject`, `NetObject`, and the
  serialize/deserialize if-chains. Its presence is what makes an object an item.
- **`WeaponState` slims to `{ int CurrentAmmo }`** — live weapon state only,
  present only on weapons. Identity lives in `ItemState`.
- **Mask semantics:**
  - `ItemState + Transform` → pickup sitting in the world
  - `ItemState + Owner` → equipped on a player
  - `+ WeaponState` → it is a gun, in either of the above states
- **`ArmorState`** stays as-is and rides on `BodyArmor` objects (pickup and
  equipped) so the pipeline replicates it, but nothing consumes it this pass —
  damage absorption is a future door.
- **`ObjectType`** loses `WeaponPickup`, `EquippedWeapon`, `ArmorPickup`,
  `EquippedArmor` and keeps non-item kinds (`Crate`, `TrainingDummy`,
  `PlayerStatus`). Items spawn as a single `ObjectType.Item` value that means
  only "no scenery builder" — the mask and `ItemState` carry all semantics.
- **Wire break accepted:** deleting `WeaponType` and re-slotting the bundle is a
  one-time break. Client and server ship from the same repo with no rolling
  deployments, so the append-only rule resumes after this change.

### 2. Static data (the extension points)

- **`EquipSlot` enum** (Common): `Hand`, `Chest`, `Head`, `Back`. New slot = one
  enum line; occupancy is a dictionary, nothing else changes.
- **`ItemConfig`** (Common, keyed by `ItemType`):
  - `Category`: `Equippable` now; `Item` reserved for walk-over inventory loot.
  - `EquipSlot` for equippables.
  - Weapon stats (the current `WeaponConfig` numbers) as a weapon-only section,
    null/absent for non-weapons.
  - Nothing static rides the wire; both ends look stats up by `ItemType`.
- **`ItemCosmetics`** (Client, keyed by `ItemType`): model path, attach node
  name (`"right_hand"` for guns; `null` = parent to the player entity root —
  armor), seat offset + rotation, sounds, tracer color. Absorbs
  `WeaponCosmetics`. Unknown `ItemType` falls back to a stand-in entry.

### 3. Server

- **New `ItemSystem`**: spawn-pickup, interact resolution, equip, swap-drop,
  despawn-on-disconnect — for all items, driven by mask + `ItemConfig`. Owns no
  object storage; goes through `ObjectReplication` like everything else.
- **`WeaponSystem`** shrinks to fire/reload validation only. It resolves the gun
  via `player.Equipped[EquipSlot.Hand]` and requires `WeaponState` in its mask.
  No pickup code remains.
- **`ServerPlayer.WeaponId` → `Dictionary<EquipSlot, uint> Equipped`.**
- **Transitions are despawn → respawn** (as today): pickup→equipped despawns the
  `ItemState + Transform` object and spawns an `ItemState + Owner` one, carrying
  live state over. `Has` stays immutable; dirty masks, catch-up, and the
  pending-update queue are untouched. Mutable masks were considered and
  deferred: more powerful, but they complicate late-join and message ordering
  for no present need, and the choice is server-internal — invisible to this
  wire format — so it can be revisited later.

### 4. Interact & swap flow

1. Client: **E** sends a new reliable `ClientToServerId.Interact` message, empty
   payload — targeting is server-authoritative.
2. Server: find the nearest object whose mask has `ItemState + Transform` within
   pickup radius of the player (the existing O(n) scan, now on demand; the
   per-tick `TryPickup` call is deleted).
3. `ItemConfig[type].Category == Equippable` → resolve its `EquipSlot`.
4. Slot empty → equip. Slot occupied → **swap**: despawn the equipped object,
   respawn it as a pickup at the player's position (live state carried), then
   equip the new one. Two despawns + two spawns through `ObjectReplication`.
5. Future walk-over items: a one-line category branch at step 3's location.

### 5. Client view — composition, not types

`ObjectViewFactory` stops dispatching solely on `ObjectType`:

- **Base entity:** has `ItemState` → model from `ItemCosmetics`; else the
  `ObjectType` builder table (Crate, Dummy); else no view (PlayerStatus).
- **Presenters attach by mask:**
  - `ItemState + Transform` → `PickupBobScript` (owns the entity transform, as
    today — no `NetTransformScript` alongside it)
  - `Transform` without `ItemState` → `NetTransformScript`
  - `ItemState + Owner` → **`ItemAttachScript`** — generalized
    `WeaponAttachScript`: reads attach node + seat from `ItemCosmetics`; a node
    name attaches via `ModelNodeLinkComponent`, `null` parents to the
    `Player_{id}` entity root. Retries every frame until the owner's view
    exists, as today.
  - `Health` → `HealthScaleScript`
- Destroy-by-name (`NetObject_{id}` at scene root) is unchanged.

### 6. Edge cases

- Interact with nothing in range, spam-E, unknown `ItemType`: server ignores;
  client cosmetics falls back to stand-in.
- Disconnect: `ItemSystem.DespawnFor` despawns every entry in `Equipped`.
- Swap mid-reload: dropped gun keeps its ammo; `NextFireTick` /
  `ReloadDoneTick` stay on the player, so a swapped-in gun cannot insta-fire.
- Raycast rule unchanged: no `Transform` = not hittable; equipped items carry no
  `Transform`.

### 7. Verification

Two local clients (note: an unfocused Stride window throttles updates — judge
smoothness only on the focused one):

- E on armor → bobbing pickup despawns, armor parents to the player model root
  on both screens.
- Swap two rifles → old one drops at the player's feet with its ammo preserved.
- Fire/reload still validate; HUD ammo behaves.
- Late joiner sees existing pickups and worn items correctly.

## Future doors (explicitly not built now)

- **Items category:** walk-over pickup into inventory; `ItemConfig.Category`
  already names it.
- **Containers:** a new `InventoryState` net component on a backpack — one new
  component, no new object kinds.
- **Armor damage absorption:** wire `ArmorState` into `WeaponSystem.ApplyFire`.
- **Mutable component masks:** if in-place transitions ever pay for themselves.
