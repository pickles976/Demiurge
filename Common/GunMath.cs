using System.Numerics;

namespace Demiurge
{
    /// <summary>Shared shot geometry, like PlayerMovement: pure math either end can run.
    /// The client uses it to end tracers where the server's authoritative raycast
    /// (GameWorld.RaycastObjects) would land. Keep the semantics identical to the
    /// server's: a hit registers at the closest-approach distance along the ray.</summary>
    public static class GunMath
    {
        /// <summary>Distance along the ray (origin, normalized direction) at which it
        /// passes within GunConfig.HitRadius of center; null on miss or beyond
        /// maxRange (per-weapon, see ItemConfig).</summary>
        public static float? HitDistance(Vector3 origin, Vector3 direction, Vector3 center, float maxRange)
        {
            var toCenter = center - origin;
            float t = Vector3.Dot(toCenter, direction);
            if (t < 0 || t > maxRange) return null;

            float missSq = (toCenter - direction * t).LengthSquared();
            if (missSq > GunConfig.HitRadius * GunConfig.HitRadius) return null;

            return t;
        }
    }
}
