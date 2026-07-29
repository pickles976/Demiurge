using System.Numerics;

namespace Demiurge
{
    /// <summary>Shared shot geometry, like PlayerMovement: pure math either end can run.
    /// The client and server use it against each swept projectile segment. A hit
    /// registers at the closest-approach distance along that segment.</summary>
    public static class GunMath
    {
        /// <summary>Distance along the ray (origin, normalized direction) at which it
        /// passes within GunConfig.HitRadius of center; null on miss or beyond
        /// segmentLength.</summary>
        public static float? HitDistance(Vector3 origin, Vector3 direction, Vector3 center, float segmentLength)
        {
            var toCenter = center - origin;
            float t = Vector3.Dot(toCenter, direction);
            if (t < 0 || t > segmentLength) return null;

            float missSq = (toCenter - direction * t).LengthSquared();
            if (missSq > GunConfig.HitRadius * GunConfig.HitRadius) return null;

            return t;
        }
    }
}
