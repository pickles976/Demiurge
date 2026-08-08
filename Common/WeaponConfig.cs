namespace Demiurge
{
    /// <summary>
    /// How the trigger behaves while the fire button is held.
    ///
    /// This is a CLIENT-INPUT rule, not a server gate, and that is deliberate. The server only
    /// ever sees one fire request per shot, so it cannot tell a held trigger from a fast finger;
    /// the honest gate it CAN apply is cadence, and <see cref="WeaponStats.TicksPerShot"/> already
    /// does. Enforcing "one press per shot" server-side would mean inferring the trigger edge from
    /// the separate input stream, which would silently reject an honest player whose click landed
    /// between two 30 Hz input samples — the one failure mode this codebase refuses to ship.
    /// </summary>
    public enum FireMode : byte
    {
        /// <summary>Holding the button keeps firing at the weapon's cadence.</summary>
        Automatic,
        /// <summary>One press, one shot; the cadence still caps how fast presses can pay out.</summary>
        SemiAutomatic,
    }

    /// <summary>Static per-weapon numbers. Never on the wire: the client
    /// predicts with them and the server enforces them, both keyed by
    /// ItemState.Type. Cadence is in server ticks so both ends count the
    /// same clock. It is fractional because 20 rounds/second is 1.5 ticks
    /// at 30 TPS; both cooldown implementations retain the half-tick phase.</summary>
    public readonly record struct WeaponStats(
        int MagazineCapacity,
        float TicksPerShot,
        int ReloadTicks,
        ushort Damage,
        string BallisticsId,
        FireMode FireMode = FireMode.Automatic);

    /// <summary>The weapon trait table: only guns have rows, null means "not a
    /// gun". ItemSystem.SpawnPickup derives the WeaponState mask bit from a row
    /// existing here — the table IS the trait declaration.</summary>
    public static class WeaponConfig
    {
        public static WeaponStats? Get(ItemType type) => ItemCatalog.TryGet(type)?.Weapon;

        /// <summary>
        /// What holding this item does to movement speed. One is the answer for anything without a
        /// weapon row, which is what lets both ends ask the question about whatever is in the hand
        /// without first checking that it is a gun.
        /// </summary>

        /// <summary>For call sites that already gated on the WeaponState bit.
        /// Throws instead of handing back phantom stats — a miswired call site
        /// should be loud, not fire an invisible fallback weapon.</summary>
        public static WeaponStats Require(ItemType type) =>
            Get(type) ?? throw new InvalidOperationException($"{ItemCatalog.DebugName(type)} has no weapon stats");
    }
}
