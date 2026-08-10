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
        internal ObjectReplication Objects => objects;

        /// <summary>How a replicated object looks to <see cref="PickupTargeting"/>.</summary>
        private static PickupTargeting.Candidate Describe(ServerObject obj)
            => new(obj.Has, obj.Item.Type, obj.Transform.Position);

        public ItemSystem(ObjectReplication objects) => this.objects = objects;

        /// <summary>
        /// The server's clock, as of the last <see cref="Tick"/>.
        ///
        /// Held rather than passed because dropping is reachable from six places — E, a swap, a
        /// put-down, a death, an admin equip, a consumed stack — and only two of them are anywhere
        /// near a tick counter. Threading one through all six to stamp a deadline would put the
        /// parameter in signatures that have no other use for it, and a drop that read the clock a
        /// tick late would still expire at the right second.
        /// </summary>
        private uint now;

        /// <summary>
        /// Sweeps away expired litter. Every dropped item carries its own deadline, so this is one
        /// pass over the object table rather than a timer per object, and an item placed by a map or
        /// set down deliberately has no deadline at all and is never considered.
        /// </summary>
        public void Tick(uint tick)
        {
            now = tick;

            List<uint>? expired = null;
            foreach (var obj in objects.All)
            {
                if (obj.DespawnAtTick == 0 || tick < obj.DespawnAtTick) continue;
                (expired ??= []).Add(obj.NetworkId);
            }
            if (expired is null) return;

            // Collected first: Despawn mutates the dictionary All enumerates.
            foreach (uint id in expired) objects.Despawn(id);
        }

        public ServerObject SpawnPickup(ItemType type, Vector3 position)
            => SpawnPickup(type, position, ObjectType.Item);

        /// <summary>
        /// A map-authored weapon crate: the same pickup, presented as a container until somebody
        /// takes it. Only the ObjectType differs, and it lives exactly as long as the world pickup
        /// does — picking up and dropping both respawn the object as <see cref="ObjectType.Item"/>,
        /// so a crated weapon is a crate on the ground and a weapon everywhere else.
        /// </summary>
        public ServerObject SpawnSupplyCrate(ItemType type, Vector3 position)
            => SpawnPickup(type, position, ObjectType.Crate);

        private ServerObject SpawnPickup(ItemType type, Vector3 position, ObjectType presentation)
        {
            // The trait tables are the mask recipe: a WeaponConfig row means a
            // WeaponState bit, an ArmorConfig row an ArmorState bit, and so on.
            var weapon = WeaponConfig.Get(type);
            var armor = ArmorConfig.Get(type);
            var mask = NetComponents.Item | NetComponents.Transform;
            if (weapon != null) mask |= NetComponents.Weapon;
            if (armor != null) mask |= NetComponents.Armor;

            return objects.Spawn(presentation, mask, position, obj =>
            {
                obj.Item = new ItemState { Type = type };
                if (weapon is { } w)
                    obj.Weapon = new WeaponState
                    {
                        CurrentAmmo = w.MagazineCapacity,
                        ReserveAmmo = ReserveFor(type, w),
                    };
                if (armor is { } a) obj.Armor = new ArmorState { MaxValue = a.Max, Current = a.Max };
            });
        }

        /// <summary>
        /// The spare rounds a weapon is issued with: <see cref="ItemConfig.SpareMagazines"/> on top
        /// of the full magazine already in it.
        ///
        /// A grenade stack is the exception, and by rule rather than by name: its magazine IS the
        /// number of grenades a man carries and there is no reload that could spend a reserve, so a
        /// non-empty one would be rounds that exist and can never be reached.
        /// </summary>
        private static int ReserveFor(ItemType type, in WeaponStats weapon)
            => ItemCatalog.HasBehavior(type, ItemBehavior.Grenade)
                ? 0
                : weapon.MagazineCapacity * ItemConfig.SpareMagazines;

        public ServerObject SpawnEquipped(ServerPlayer player, ItemType type, bool dropReplaced = true)
            => SpawnOwned(player, type, ItemConfig.Get(type).Slot, null, dropReplaced);

        /// <summary>Creates one persistent hotbar stack. Only the selected slot is usable/rendered.</summary>
        public ServerObject SpawnHotbar(
            ServerPlayer player,
            ItemType type,
            HotbarSlot hotbar,
            int? ammo = null,
            bool dropReplaced = false)
            => SpawnOwned(player, type, HotbarConfig.StorageSlot(hotbar), ammo, dropReplaced);

        /// <summary>
        /// Standard infantry inventory shared by every NPC. Slot 2 is a real shovel object now: it
        /// has no WeaponConfig row, so it carries no WeaponState and fire/reload skip it, but it
        /// gives the slot something to render — on the hip when stowed, in hand when selected.
        /// Selecting the slot is still what authorizes digging.
        /// </summary>
        internal void SpawnInfantryLoadout(ServerPlayer actor, ItemType? primaryWeapon = null)
        {
            ItemType primary = primaryWeapon ?? DefaultPrimary(actor);
            SpawnHotbar(actor, primary, HotbarSlot.Primary);
            SpawnHotbar(actor, ItemCatalog.RequireBehavior(ItemBehavior.Shovel), HotbarSlot.Shovel);
            if (CarriesGrenades(actor, primary)) SpawnGrenades(actor);
            actor.Hotbar = HotbarSlot.Primary;
        }

        /// <summary>
        /// The gun a man is issued when he has none: a mob's from the standard NPC primary, a
        /// player's from the class he picked. One function, so the first spawn and every respawn
        /// cannot disagree about what a class means.
        /// </summary>
        private static ItemType DefaultPrimary(ServerPlayer actor)
            => actor.IsMob
                ? ItemConfig.DefaultNpcPrimaryWeapon
                : PlayerClasses.Weapon(actor.Class);

        /// <summary>
        /// Who is issued a grenade stack: every player, and the NPCs carrying the assault gun.
        ///
        /// One predicate for both loadout paths. They are the same rule and were the same three
        /// lines twice, which is exactly the shape that lets a spawn and a respawn quietly disagree
        /// about what a man is carrying.
        ///
        /// Players are unconditional because a player is not a squad role — nobody assigns him a
        /// weapon, and stripping his grenades for picking up a rifle would read as a bug.
        /// </summary>
        private static bool CarriesGrenades(ServerPlayer actor, ItemType primary)
            => !actor.IsMob || NpcSquadLoadout.CarriesGrenades(primary);

        private void SpawnGrenades(ServerPlayer actor)
        {
            ItemType grenade = ItemCatalog.RequireBehavior(ItemBehavior.Grenade);
            SpawnHotbar(
                actor,
                grenade,
                HotbarSlot.Grenade,
                ammo: WeaponConfig.Require(grenade).MagazineCapacity);
        }

        /// <summary>
        /// Restores every equipped weapon to a full magazine and a full reserve after a death, and
        /// recreates the default slots that are gone.
        ///
        /// The primary is now always one of those: <see cref="DropOnDeath"/> leaves it on the ground,
        /// so a man who had picked a PPSH up respawns with the standard issue rather than with the
        /// gun he died holding — that gun is lying where he died, and somebody else may have it.
        /// </summary>
        internal void RefillRespawnLoadout(ServerPlayer actor)
        {
            bool hasPrimary = false;
            bool hasShovel = false;
            bool hasGrenades = false;
            ItemType primary = DefaultPrimary(actor);
            foreach (var pair in actor.Equipped)
            {
                if (!objects.TryGet(pair.Value, out var item)
                    || !item.Has.HasFlag(NetComponents.Item))
                    continue;

                // The shovel is the one default slot with no magazine to refill, so it is checked
                // before the weapon filter rather than inside it.
                hasShovel |= pair.Key == EquipSlot.HotbarShovel;

                if (!item.Has.HasFlag(NetComponents.Weapon)
                    || WeaponConfig.Get(item.Item.Type) is not { } weapon)
                    continue;

                item.Weapon.CurrentAmmo = weapon.MagazineCapacity;
                item.Weapon.ReserveAmmo = ReserveFor(item.Item.Type, weapon);
                item.Dirty |= NetComponents.Weapon;
                if (pair.Key is EquipSlot.HotbarPrimary or EquipSlot.Hand)
                {
                    hasPrimary = true;
                    // What he is carrying NOW, not what he spawned with: a mob that picked up a
                    // PPSH is an assaulter for the purposes of the rule below.
                    primary = item.Item.Type;
                }
                hasGrenades |= pair.Key == EquipSlot.HotbarGrenade
                    && ItemCatalog.HasBehavior(item.Item.Type, ItemBehavior.Grenade);
            }

            if (!hasPrimary) SpawnHotbar(actor, primary, HotbarSlot.Primary);
            if (!hasShovel)
                SpawnHotbar(actor, ItemCatalog.RequireBehavior(ItemBehavior.Shovel), HotbarSlot.Shovel);

            // A stack he still has was refilled by the loop above, whoever he is. This only decides
            // who is ISSUED a new one, so a man who came by grenades some other way keeps them
            // rather than having them taken off him at the respawn wave.
            if (!hasGrenades && CarriesGrenades(actor, primary)) SpawnGrenades(actor);
            actor.Hotbar = HotbarSlot.Primary;
        }

        private ServerObject SpawnOwned(
            ServerPlayer player,
            ItemType type,
            EquipSlot slot,
            int? ammo,
            bool dropReplaced)
        {
            var stats = ItemConfig.Get(type);
            if (!ItemConfig.IsHeld(type))
                throw new InvalidOperationException($"{type} cannot be equipped");

            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
            {
                if (dropReplaced)
                    Drop(current, player.Position, player.Yaw, LitterDeadline());
                else
                    objects.Despawn(current.NetworkId);
            }

            var weapon = WeaponConfig.Get(type);
            var armor = ArmorConfig.Get(type);
            var mask = NetComponents.Item | NetComponents.Owner | NetComponents.Attachment;
            if (weapon != null) mask |= NetComponents.Weapon;
            if (armor != null) mask |= NetComponents.Armor;

            var equipped = objects.Spawn(ObjectType.Item, mask, player.Position, obj =>
            {
                obj.Item = new ItemState { Type = type };
                obj.Owner = new OwnerState { PlayerId = player.Id };
                obj.Attachment = new AttachmentState { Slot = slot };
                if (weapon is { } w)
                    obj.Weapon = new WeaponState
                    {
                        CurrentAmmo = Math.Clamp(ammo ?? w.MagazineCapacity, 0, w.MagazineCapacity),
                        ReserveAmmo = ReserveFor(type, w),
                    };
                if (armor is { } a) obj.Armor = new ArmorState { MaxValue = a.Max, Current = a.Max };
            });
            player.Equipped[slot] = equipped.NetworkId;
            return equipped;
        }

        /// <summary>
        /// E pressed: equip the nearest pickup in radius, swapping out whatever occupies its slot.
        /// Server-authoritative — the client sends no target, so there is nothing to validate beyond
        /// proximity and who is using what.
        /// </summary>
        /// <param name="actors">
        /// Everyone, so a thing somebody is WORKING can be excluded. A mortar with a gunner on it is
        /// not a pickup: taking it out from under him would leave him operating an object that no
        /// longer exists where he is standing. Passed in rather than tracked here because who is
        /// operating what lives on <see cref="ServerPlayer.OperatingObjectId"/>, and a second copy of
        /// that here could disagree with it.
        /// </param>
        public void ApplyInteract(ServerPlayer player, IEnumerable<ServerPlayer> actors)
        {
            // Hands full: E puts the thing down instead of looking for another one. One key, and it
            // reads the same way round every time — E is "change what is in my hands".
            if (player.IsCarrying)
            {
                PutDown(player);
                return;
            }

            // Find first, act after: Despawn/Spawn mutate the object dictionary
            // and must not run inside its enumeration.
            //
            // The choice itself is PickupTargeting's, in Common, because the client runs the same
            // one to decide what its "Press E" prompt should name — see that file.
            var pickup = PickupTargeting.Nearest(
                player.Position,
                objects.All.Where(candidate => !IsBeingWorked(candidate.NetworkId, actors)),
                Describe);
            if (pickup == null) return;

            TryTake(player, pickup, actors);
        }

        /// <summary>AI-targetable form of interact: same validation and transition, exact object.</summary>
        internal bool TryTake(
            ServerPlayer player,
            ServerObject pickup,
            IEnumerable<ServerPlayer> actors)
        {
            if (player.IsCarrying
                || IsBeingWorked(pickup.NetworkId, actors)
                || !PickupTargeting.IsAvailable(Describe(pickup))
                || Vector3.DistanceSquared(player.Position, pickup.Transform.Position)
                    > PickupTargeting.RadiusSquared)
                return false;

            // Something hauled goes into the hands, not into the kit: it must not take the rifle's
            // slot, because picking it up is not a swap. Whatever was already being carried IS
            // swapped out — you have one pair of hands.
            var slot = ItemConfig.IsCarryable(pickup.Item.Type)
                ? EquipSlot.Carried
                : pickup.Has.HasFlag(NetComponents.Weapon)
                    ? HotbarConfig.StorageSlot(HotbarConfig.SlotFor(pickup.Item.Type))
                    : ItemConfig.Get(pickup.Item.Type).Slot;
            Equip(player, pickup, slot);
            return true;
        }

        /// <summary>
        /// Sets down what the player is hauling, where he stands and facing where he faces. That
        /// heading is the whole point for a mortar — it is the line the tube then traverses around —
        /// so putting one down is an act of aiming, not of tidying up.
        /// </summary>
        public void PutDown(ServerPlayer player)
        {
            if (!player.Equipped.Remove(EquipSlot.Carried, out uint carriedId)) return;
            if (!objects.TryGet(carriedId, out var carried)) return;

            // No deadline. Setting a mortar down is emplacing it, and an emplacement that dissolved
            // after a minute would be a weapon the map quietly took back off the player.
            Drop(carried, player.Position, player.Yaw, despawnAtTick: 0);
        }

        private void Equip(ServerPlayer player, ServerObject pickup, EquipSlot slot)
        {
            // Swap: the current occupant drops where the player stands, and starts rotting — a gun
            // he chose to put down for a better one is litter like any other.
            if (player.Equipped.Remove(slot, out uint currentId) && objects.TryGet(currentId, out var current))
                Drop(current, player.Position, player.Yaw, LitterDeadline());

            // pickup -> equipped: despawn + respawn with Transform swapped for
            // Owner + Attachment. CopyComponents carries every shared bit — live
            // state (ammo) and any future trait ride along automatically.
            var mask = (pickup.Has & ~NetComponents.Transform) | NetComponents.Owner | NetComponents.Attachment;
            objects.Despawn(pickup.NetworkId);

            var equipped = objects.Spawn(ObjectType.Item, mask, player.Position, obj =>
            {
                ServerObject.CopyComponents(pickup, obj, pickup.Has & mask);
                obj.Owner = new OwnerState { PlayerId = player.Id };
                obj.Attachment = new AttachmentState { Slot = slot };
            });
            player.Equipped[slot] = equipped.NetworkId;
        }

        /// <summary>
        /// Puts an equipped item back in the world at <paramref name="yaw"/> — the direction its
        /// owner was facing as they let go of it.
        ///
        /// For most items that is decoration. For a mortar it is the emplacement: the tube traverses
        /// a sector either side of this heading and cannot be re-laid without picking the thing up,
        /// so which way a man was looking when he set it down is a lasting fact about the world and
        /// has to survive the equipped-to-pickup transition rather than being reset to zero.
        /// </summary>
        /// <summary>When something dropped right now stops being worth walking to.</summary>
        private uint LitterDeadline() => now + (uint)ItemConfig.DroppedLifetimeTicks;

        private void Drop(ServerObject equipped, Vector3 position, float yaw, uint despawnAtTick)
        {
            // equipped -> pickup: the mirror image of Equip's transition. The
            // spawn position IS the pickup's Transform, so it must not be copied
            // (equipped.Has has no Transform bit, so the shared mask excludes it).
            var mask = (equipped.Has & ~(NetComponents.Owner | NetComponents.Attachment)) | NetComponents.Transform;
            objects.Despawn(equipped.NetworkId);

            objects.Spawn(ObjectType.Item, mask, position, obj =>
            {
                ServerObject.CopyComponents(equipped, obj, equipped.Has & mask);
                obj.Transform.Yaw = yaw;
                obj.DespawnAtTick = despawnAtTick;
            });
        }

        /// <summary>
        /// Removes a one-use equipped item without dropping it. The expected network ID prevents a
        /// delayed use request from consuming whatever was subsequently swapped into the slot.
        /// </summary>
        public bool ConsumeEquipped(
            ServerPlayer player,
            EquipSlot slot,
            uint expectedNetworkId)
        {
            if (!player.Equipped.TryGetValue(slot, out uint equippedId)
                || equippedId != expectedNetworkId)
                return false;

            player.Equipped.Remove(slot);
            objects.Despawn(equippedId);
            return true;
        }

        /// <summary>
        /// What the actor has in a hotbar slot, or null when the slot is empty.
        ///
        /// The Hand fallback is the same one WeaponSystem.TryGetActiveWeapon applies, and it is not
        /// optional here: an administrative `give` predates the hotbar and lands in Hand, where slot
        /// 1 still selects it — and the client's own per-slot map files those under Primary too.
        /// </summary>
        public ItemType? HeldItem(ServerPlayer player, HotbarSlot hotbar)
        {
            if (!HotbarConfig.IsValid(hotbar)) return null;

            var slot = HotbarConfig.StorageSlot(hotbar);
            if (hotbar == HotbarSlot.Primary && !player.Equipped.ContainsKey(slot))
                slot = EquipSlot.Hand;

            return player.Equipped.TryGetValue(slot, out uint itemId) && objects.TryGet(itemId, out var item)
                ? item.Item.Type
                : null;
        }

        /// <summary>
        /// The movement multiplier for whatever is in the actor's selected slot, which is the
        /// server's half of <see cref="PlayerMovement.Step"/>'s speedScale. The client predicts the
        /// same number from its own map of that slot; see <see cref="ItemStats.MoveSpeedScale"/>
        /// for why only weapons are allowed to carry one.
        /// </summary>
        public float MoveSpeedScale(ServerPlayer player, HotbarSlot hotbar)
            => CarriedItem(player) is { } hauled ? ItemConfig.MoveSpeedScale(hauled)
             : HeldItem(player, hotbar) is { } type ? ItemConfig.MoveSpeedScale(type)
             : 1f;

        /// <summary>Whether anybody currently has this object as their emplacement.</summary>
        internal static bool IsBeingWorked(uint networkId, IEnumerable<ServerPlayer> actors)
        {
            foreach (var actor in actors)
                if (actor.OperatingObjectId == networkId)
                    return true;
            return false;
        }

        /// <summary>
        /// The emplaced carryable within reach of this player, or null — what F operates. Uses the
        /// same PickupTargeting rule E does, filtered to things that are set down rather than
        /// merely lying about, so the two keys always agree on which object is meant.
        ///
        /// A tube somebody else is already on is excluded for the same reason E excludes it: one
        /// gunner per weapon, and two men laying the same mortar on different bearings is not a
        /// state the firing code has any answer for.
        /// </summary>
        public ServerObject? EmplacedInReach(ServerPlayer player, IEnumerable<ServerPlayer> actors)
        {
            var nearest = PickupTargeting.Nearest(
                player.Position,
                objects.All.Where(candidate =>
                    candidate.NetworkId == player.OperatingObjectId
                    || !IsBeingWorked(candidate.NetworkId, actors)),
                Describe);
            return nearest is not null && ItemConfig.IsCarryable(nearest.Item.Type) ? nearest : null;
        }

        /// <summary>What this player is hauling, or null. Its weight is what he moves at.</summary>
        public ItemType? CarriedItem(ServerPlayer player)
            => player.Equipped.TryGetValue(EquipSlot.Carried, out uint id)
               && objects.TryGet(id, out var item)
                ? item.Item.Type
                : null;

        /// <summary>
        /// Applies a requested hotbar selection, or refuses it because the man's hands are full.
        /// Every path that honours a client's slot choice goes through here, so "you cannot switch
        /// while carrying" is one rule in one place rather than a condition three call sites have to
        /// remember.
        /// </summary>
        public void SelectHotbar(ServerPlayer player, HotbarSlot hotbar)
        {
            if (player.IsCarrying) return;
            player.Hotbar = hotbar;
        }

        /// <summary>
        /// Leaves a dead man's weapon where he fell, for whoever walks past it.
        ///
        /// His GUN and nothing else. The rest of the kit is deliberately untouched — his shovel,
        /// his armour and his grenades stay equipped and are refilled at the respawn wave, exactly
        /// as they were before, because a shovel on the ground is litter nobody crosses a field for
        /// and stripping a man's armour on death is a separate decision nobody has made.
        ///
        /// What lands is the weapon he ACTUALLY had, not a fresh issue: the magazine and the reserve
        /// ride the object through <see cref="Drop"/> on the same CopyComponents line, so a man
        /// killed mid-reload leaves a nearly empty rifle and taking it is a real gamble.
        /// </summary>
        public void DropOnDeath(ServerPlayer player)
        {
            // A grenade stack is not a weapon you pick up off the ground — it is ammunition, and one
            // dropped by every casualty would carpet a contested position in them. A carryable is
            // exempt for the opposite reason: it is emplaced, not held, and PutDown is how it moves.
            var dropped = player.Equipped
                .Where(pair => pair.Key != EquipSlot.Carried
                               && objects.TryGet(pair.Value, out var item)
                               && item.Has.HasFlag(NetComponents.Weapon)
                               && !ItemCatalog.HasBehavior(item.Item.Type, ItemBehavior.Grenade))
                .ToArray();

            // Collected first: Drop despawns and respawns objects, so it mutates what it iterates.
            foreach (var (slot, id) in dropped)
            {
                player.Equipped.Remove(slot);
                if (objects.TryGet(id, out var item))
                    Drop(item, player.Position, player.Yaw, LitterDeadline());
            }
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
