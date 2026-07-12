namespace Demiurge
{
    /// <summary>Shot geometry globals that are NOT per-weapon: HitRadius models
    /// target size (stand-in collider around an object's origin) and MuzzleHeight
    /// models the character rig. Per-weapon numbers live in WeaponConfig.</summary>
    public static class GunConfig
    {
        public const float HitRadius = 0.6f;
        public const float MuzzleHeight = 0.4f;
    }
}
