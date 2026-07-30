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

        /// <summary>
        /// Distance along the ray at which it passes closest to a player standing on
        /// <paramref name="feet"/>; null on miss or beyond segmentLength.
        ///
        /// A player is a capsule, not a point. This used to be one <see cref="HitDistance"/> sphere
        /// centred 0.5 m up, spanning about the hips: a standing player's head and shoulders were not
        /// hittable by anyone, so peeking over cover with only the head exposed was literal
        /// invulnerability, and AI perception aiming at that same low point could not even see such a
        /// target. The capsule is the one the movement solver collides with, so the volume that stops
        /// a bullet is the volume that occupies the world.
        ///
        /// Crouching deliberately needs no case here: it lowers the eye, not the body, and cover still
        /// stops the shot because terrain is tested as a distance ceiling before any player is.
        /// </summary>
        public static float? PlayerHitDistance(
            Vector3 origin,
            Vector3 direction,
            Vector3 feet,
            float segmentLength)
        {
            float radius = GunConfig.HitRadius;
            float height = PlayerMovement.Body.Height;
            // Cap centres, so the swept volume spans exactly [feet.Y, feet.Y + height].
            float capOffset = MathF.Min(radius, height * 0.5f);
            Vector3 axisStart = feet + Vector3.UnitY * capOffset;
            Vector3 axisEnd = feet + Vector3.UnitY * (height - capOffset);

            float t = ClosestApproachOnRay(
                origin,
                direction,
                segmentLength,
                axisStart,
                axisEnd,
                out Vector3 onAxis);
            float missSq = (origin + direction * t - onAxis).LengthSquared();
            return missSq > radius * radius ? null : t;
        }

        /// <summary>
        /// Ray parameter of closest approach between a bounded ray and a segment, plus the segment
        /// point it is closest to. Standard clamped two-segment solve; the degenerate case is the ray
        /// running parallel to the segment, where the perpendicular foot is used instead.
        /// </summary>
        private static float ClosestApproachOnRay(
            Vector3 origin,
            Vector3 direction,
            float segmentLength,
            Vector3 from,
            Vector3 to,
            out Vector3 closestOnSegment)
        {
            Vector3 axis = to - from;
            Vector3 offset = origin - from;
            float directionLengthSquared = Vector3.Dot(direction, direction);
            float projection = Vector3.Dot(direction, axis);
            float axisLengthSquared = Vector3.Dot(axis, axis);
            float alongDirection = Vector3.Dot(direction, offset);
            float alongAxis = Vector3.Dot(axis, offset);

            float denominator = directionLengthSquared * axisLengthSquared - projection * projection;
            float axisParameter = axisLengthSquared <= 1e-8f
                ? 0f
                : MathF.Abs(denominator) <= 1e-8f
                    // Parallel: every axis point is equally distant, so use the foot of the origin.
                    ? Math.Clamp(alongAxis / axisLengthSquared, 0f, 1f)
                    : Math.Clamp(
                        (directionLengthSquared * alongAxis - projection * alongDirection)
                        / denominator,
                        0f,
                        1f);

            closestOnSegment = from + axis * axisParameter;
            float rayParameter = directionLengthSquared <= 1e-8f
                ? 0f
                : (projection * axisParameter - alongDirection) / directionLengthSquared;
            return Math.Clamp(rayParameter, 0f, segmentLength);
        }
    }
}
