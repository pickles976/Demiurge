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
