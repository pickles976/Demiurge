namespace Demiurge
{
    [Flags]
    public enum PlayerStateFlags
    {
        None      = 0,
        Moving    = 1 << 0,
        Sprinting = 1 << 1,
        Crouching = 1 << 2,
        Jumping   = 1 << 3,
        Aiming    = 1 << 4,
        Shooting  = 1 << 5,
        Reloading = 1 << 6,
    }

    public static class PlayerStateFlagExtensions
    {
        public static PlayerStateFlags With(this PlayerStateFlags flags, PlayerStateFlags flag, bool on)
            => on ? flags | flag : flags & ~flag;
    }
}
