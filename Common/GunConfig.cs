namespace Demiurge
{
    /// <summary>Shot geometry globals that are NOT per-weapon: HitRadius models
    /// target size (stand-in collider around an object's origin). Per-weapon numbers
    /// live in ItemConfig.
    ///
    /// A shot's ORIGIN is deliberately not here any more. It used to be a MuzzleHeight
    /// constant on the player's centre axis, which put every gun's bullets in the same
    /// wrong place; it now comes from the barrel of the weapon actually held, measured
    /// off that model — client-side, in WeaponMount, since the server only ever
    /// range-checks the origin it is handed and never computes one.</summary>
    public static class GunConfig
    {
        public const float HitRadius = 0.6f;
        public const float PlayerCenterHeight = 0.5f;
    }
}
