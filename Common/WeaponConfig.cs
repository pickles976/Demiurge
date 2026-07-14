namespace Demiurge
{
    /// <summary>Static per-weapon numbers. Never on the wire: the client predicts
    /// with them and the server enforces them, both keyed by WeaponState.Type —
    /// the same client-predicts/server-enforces contract as PlayerMovement.
    /// Cadence is in server ticks so both ends count the same clock.</summary>
    public readonly record struct WeaponStats(
        int MagazineCapacity,
        int TicksPerShot,
        int ReloadTicks,
        ushort Damage,
        float MaxRange);

    public static class WeaponConfig
    {
        public static WeaponStats Get(WeaponType type) => type switch
        {
            WeaponType.Ak47 => new(MagazineCapacity: 30, TicksPerShot: 3, ReloadTicks: 45, Damage: 10, MaxRange: 100f),
            WeaponType.AWP => new(MagazineCapacity: 5, TicksPerShot: 60, ReloadTicks: 45, Damage: 75, MaxRange: 200f),

            // Unknown type off the wire: fall back to AK numbers rather than crash.
            _ => new(30, 3, 45, 10, 100f),
        };
    }
}
