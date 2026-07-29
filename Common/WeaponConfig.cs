namespace Demiurge
{
    /// <summary>Static per-weapon numbers. Never on the wire: the client
    /// predicts with them and the server enforces them, both keyed by
    /// ItemState.Type. Cadence is in server ticks so both ends count the
    /// same clock.</summary>
    public readonly record struct WeaponStats(
        int MagazineCapacity,
        int TicksPerShot,
        int ReloadTicks,
        ushort Damage,
        WeaponBallisticsProfile BallisticsProfile);

    /// <summary>The weapon trait table: only guns have rows, null means "not a
    /// gun". ItemSystem.SpawnPickup derives the WeaponState mask bit from a row
    /// existing here — the table IS the trait declaration.</summary>
    public static class WeaponConfig
    {
        public static WeaponStats? Get(ItemType type) => type switch
        {
            ItemType.Ak47 => new WeaponStats(MagazineCapacity: 30, TicksPerShot: 3, ReloadTicks: 45, Damage: 30, BallisticsProfile: WeaponBallisticsProfile.Carbine),
            ItemType.AWP => new WeaponStats(MagazineCapacity: 5, TicksPerShot: 60, ReloadTicks: 45, Damage: 75, BallisticsProfile: WeaponBallisticsProfile.SniperRifle),
            ItemType.Glock => new WeaponStats(MagazineCapacity: 15, TicksPerShot: 7, ReloadTicks: 20, Damage: 5, BallisticsProfile: WeaponBallisticsProfile.Pistol),
            // A grenade stack uses ammo as its remaining count. Each throw automatically cycles
            // the next grenade for 1.5 seconds; R is never needed for this item.
            ItemType.Grenade => new WeaponStats(MagazineCapacity: 4, TicksPerShot: 1, ReloadTicks: GrenadeConfig.ReloadTicks, Damage: 0, BallisticsProfile: WeaponBallisticsProfile.Throwable),
            _ => null,
        };

        /// <summary>For call sites that already gated on the WeaponState bit.
        /// Throws instead of handing back phantom stats — a miswired call site
        /// should be loud, not fire an invisible AK.</summary>
        public static WeaponStats Require(ItemType type) =>
            Get(type) ?? throw new InvalidOperationException($"{type} has no weapon stats");
    }
}
