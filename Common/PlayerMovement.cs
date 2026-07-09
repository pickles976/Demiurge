using System.Numerics;

namespace Demiurge
{
    public static class PlayerMovement
    {
        public const float WalkSpeed = 3f;
        public const float SprintSpeed = 4f;
        public const float SlowSpeed = 1f;

        public const PlayerStateFlags SlowingStates =
            PlayerStateFlags.Crouching | PlayerStateFlags.Aiming |
            PlayerStateFlags.Shooting  | PlayerStateFlags.Reloading;

        // Pure function: the client predicts with it, the server runs it
        // authoritatively, and reconciliation will replay it. Keep it side-effect free.
        public static Vector3 Step(Vector3 position, Vector3 intent, PlayerStateFlags flags, float dt)
        {
            if (intent != Vector3.Zero) 
                intent = Vector3.Normalize(intent);

            float speed = (flags & SlowingStates) != 0              ? SlowSpeed
                        : flags.HasFlag(PlayerStateFlags.Sprinting) ? SprintSpeed
                        : WalkSpeed;
                        
            return position + intent * speed * dt;
        }
    }
}
