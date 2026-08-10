using System.Numerics;

namespace Demiurge
{
    public readonly record struct ProjectileStep(
        Vector3 Start,
        Vector3 End,
        Vector3 Velocity,
        float Distance,
        bool Exhausted);

    /// <summary>Fixed-step projectile integration shared by the server and cosmetic client sim.</summary>
    public static class ProjectileMotion
    {
        /// <summary>
        /// Not a weapon range: a common runaway guard for shots fired out of the loaded world.
        /// Normal projectiles end by colliding after gravity carries them into terrain.
        /// </summary>
        public const float SafetyDistance = 1_000f;

        /// <summary>
        /// Gameplay gravity is twice Earth's gravity so drop is readable inside the weapons'
        /// relatively short effective ranges.
        /// </summary>
        public const float Gravity = 19.62f;

        /// <summary>
        /// Seconds for a falling projectile to descend <paramref name="rise"/> metres, given the
        /// vertical speed it has now, or -1 if it never gets there.
        ///
        /// The descending root of <c>rise + vy·t − ½g·t² = 0</c>. Separate from
        /// <see cref="Advance"/> because it answers a different question: Advance FLIES a projectile
        /// a step at a time, and this asks when one already in the air will arrive — which is what
        /// anybody warning about it needs, and what stepping to find out would be a loop for.
        /// </summary>
        public static float SecondsToFall(float rise, float verticalSpeed, float gravity)
        {
            if (gravity <= 0f) return verticalSpeed < 0f ? rise / -verticalSpeed : -1f;

            float discriminant = verticalSpeed * verticalSpeed + 2f * gravity * rise;
            if (discriminant < 0f) return -1f;   // it tops out above the height asked about

            float seconds = (verticalSpeed + MathF.Sqrt(discriminant)) / gravity;
            return seconds >= 0f ? seconds : -1f;
        }

        public static ProjectileStep Advance(
            Vector3 position,
            Vector3 velocity,
            float dt,
            float remainingDistance)
        {
            if (dt <= 0f || remainingDistance <= 0f)
                return new ProjectileStep(position, position, velocity, 0f, true);

            var acceleration = new Vector3(0f, -Gravity, 0f);
            var displacement = velocity * dt + acceleration * (0.5f * dt * dt);
            float distance = displacement.Length();
            if (distance < 1e-8f)
                return new ProjectileStep(position, position, velocity + acceleration * dt, 0f, false);

            float fraction = MathF.Min(1f, remainingDistance / distance);
            float elapsed = dt * fraction;
            var end = position + displacement * fraction;
            var nextVelocity = velocity + acceleration * elapsed;
            float travelled = distance * fraction;
            return new ProjectileStep(
                position,
                end,
                nextVelocity,
                travelled,
                travelled >= remainingDistance - 1e-5f);
        }
    }
}
