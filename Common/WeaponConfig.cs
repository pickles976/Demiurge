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
        float MaxRange,
        float shiftNear,
        float shiftFar);

    public static class WeaponConfig
    {
        public static WeaponStats Get(WeaponType type) => type switch
        {
            WeaponType.Ak47 => new(MagazineCapacity: 30, TicksPerShot: 3, ReloadTicks: 45, Damage: 10, MaxRange: 100f, shiftNear: 2.5f, shiftFar: 6.0f),
            WeaponType.AWP => new(MagazineCapacity: 5, TicksPerShot: 60, ReloadTicks: 45, Damage: 75, MaxRange: 200f, shiftNear: 2.5f, shiftFar: 12.5f),
            WeaponType.Glock => new(MagazineCapacity: 15, TicksPerShot: 7, ReloadTicks: 20, Damage: 5, MaxRange: 50, shiftNear: 1.5f, shiftFar: 2.5f),

            // Unknown type off the wire: fall back to AK numbers rather than crash.
            _ => new(30, 3, 45, 10, 100f, 1.5f, 2.5f),
        };
    }
}
