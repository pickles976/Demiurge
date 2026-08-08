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
        /// <summary>Both hands are full of something hauled. Replicated because it changes what a
        /// man LOOKS like to everyone else, not just to himself: his rifle is not in his hands, so
        /// it must not be drawn there.</summary>
        Carrying  = 1 << 7,
        /// <summary>Standing at an emplaced weapon and working it. Replicated because it decides
        /// what a man looks like he is doing, and because his own client draws a different camera
        /// for it.</summary>
        Operating = 1 << 8,
        /// <summary>Lying flat. Unlike crouching this is a toggled stance, and sprinting always
        /// clears it before movement is simulated.</summary>
        Prone     = 1 << 9,
    }

    /// <summary>
    /// The flags the SERVER decides, as opposed to the ones a client reads off its own keyboard.
    ///
    /// The distinction is load-bearing on the local player and nowhere else. His state is rebuilt
    /// from input every frame — that is what makes movement predictable — so anything the server
    /// owns has to be merged back in from the wire or it is overwritten a frame after it arrives.
    /// Carrying and Operating are both consequences of what the server did with a request, not of
    /// what a key is doing now, and neither can be derived from input at all.
    /// </summary>
    public static class ServerAuthoredState
    {
        public const PlayerStateFlags Mask = PlayerStateFlags.Carrying | PlayerStateFlags.Operating;

        /// <summary>Local input state with the server's own bits laid back over it.</summary>
        public static PlayerStateFlags Merge(PlayerStateFlags local, PlayerStateFlags fromServer)
            => (local & ~Mask) | (fromServer & Mask);
    }

    public static class PlayerStateFlagExtensions
    {
        public static PlayerStateFlags With(this PlayerStateFlags flags, PlayerStateFlags flag, bool on)
            => on ? flags | flag : flags & ~flag;

        /// <summary>A sprint request wins over prone on both sides of prediction. Keeping this in
        /// Common prevents a fabricated or reordered input packet from producing an impossible
        /// sprinting-prone state on the server.</summary>
        public static PlayerStateFlags StandForSprint(this PlayerStateFlags flags)
            => flags.HasFlag(PlayerStateFlags.Sprinting)
                ? flags & ~PlayerStateFlags.Prone
                : flags;
    }
}
