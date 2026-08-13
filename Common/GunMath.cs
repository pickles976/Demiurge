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
            => SphereDistance(origin, direction, center, GunConfig.HitRadius, segmentLength);

        /// <summary>
        /// Where a shot meets a standing trunk: an upright cylinder from <paramref name="baseCentre"/>
        /// up by <paramref name="height"/>. Null on a miss, or past <paramref name="segmentLength"/>.
        ///
        /// A sphere will not do for this. Every other object in the world is roughly as wide as it is
        /// tall and one sphere at its origin is a fair description; a tree is eight metres of thin,
        /// and the sphere that covers its trunk would also swallow everything standing beside it.
        ///
        /// A shot that starts INSIDE the cylinder is stopped where it leaves — someone with his back
        /// to a trunk shoots out of it rather than being trapped in it.
        /// </summary>
        public static float? TrunkHitDistance(
            Vector3 origin,
            Vector3 direction,
            Vector3 baseCentre,
            float radius,
            float height,
            float segmentLength)
        {
            // Solved in plan: an upright cylinder is a circle to anything looking down at it, and the
            // height only decides whether the crossing counts.
            float ox = origin.X - baseCentre.X;
            float oz = origin.Z - baseCentre.Z;
            float a = direction.X * direction.X + direction.Z * direction.Z;

            // Straight up or down. It can only be inside the trunk if it started there, and a shot
            // fired from inside one is already stopped by the branch below.
            if (a < 1e-8f) return null;

            float b = ox * direction.X + oz * direction.Z;
            float c = ox * ox + oz * oz - radius * radius;
            float discriminant = b * b - a * c;
            if (discriminant < 0f) return null;

            float root = MathF.Sqrt(discriminant);
            float near = (-b - root) / a;
            float far = (-b + root) / a;

            return Crossing(near) ?? Crossing(far);

            float? Crossing(float t)
            {
                if (t < 0f || t > segmentLength) return null;
                float y = origin.Y + direction.Y * t;
                return y >= baseCentre.Y && y <= baseCentre.Y + height ? t : null;
            }
        }

        /// <summary>The same test against an arbitrary radius. One implementation, so a head and a
        /// crate cannot end up disagreeing about what "the ray passed within r" means.</summary>
        private static float? SphereDistance(
            Vector3 origin,
            Vector3 direction,
            Vector3 center,
            float radius,
            float segmentLength)
        {
            var toCenter = center - origin;
            float t = Vector3.Dot(toCenter, direction);
            if (t < 0 || t > segmentLength) return null;

            float missSq = (toCenter - direction * t).LengthSquared();
            if (missSq > radius * radius) return null;

            return t;
        }

        /// <summary>
        /// Where a shot struck an actor, and whether it struck the head.
        ///
        /// One result rather than two calls: the head sphere lives INSIDE the body capsule, so
        /// "was this a head hit" is a property of a hit that already happened, not a second
        /// question that could answer yes when the first answered no.
        /// </summary>
        public readonly record struct PlayerHit(float Distance, bool Head);

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
            => PlayerHitAt(origin, direction, feet, segmentLength, crouching: false)?.Distance;

        public static float? PlayerHitDistance(
            Vector3 origin,
            Vector3 direction,
            Vector3 feet,
            float segmentLength,
            PlayerStateFlags state,
            float yaw)
            => PlayerHitAt(origin, direction, feet, segmentLength, state, yaw)?.Distance;

        /// <summary>
        /// The same test, reporting whether the shot found the head as well as where it landed.
        ///
        /// The distance is the capsule's, head hit or not, so the multiplier changes what a hit is
        /// worth and never where it registers or which of two targets a bullet reaches first. The
        /// head is a sphere rather than a second capsule because it is one rigid lump of geometry,
        /// and it follows <paramref name="crouching"/> because the model's head does.
        ///
        /// Two known approximations, both deliberate: the sphere does not swing with the aim pitch
        /// the way the rendered head does — <c>upper_chest</c> parents the neck — and it does not
        /// ride the walk cycle's bob. Both move the head by centimetres against a 0.20 m radius.
        /// </summary>
        public static PlayerHit? PlayerHitAt(
            Vector3 origin,
            Vector3 direction,
            Vector3 feet,
            float segmentLength,
            bool crouching)
            => PlayerHitAt(
                origin,
                direction,
                feet,
                segmentLength,
                crouching ? PlayerStateFlags.Crouching : PlayerStateFlags.None,
                yaw: 0f);

        public static PlayerHit? PlayerHitAt(
            Vector3 origin,
            Vector3 direction,
            Vector3 feet,
            float segmentLength,
            PlayerStateFlags state,
            float yaw)
        {
            float radius;
            Vector3 axisStart;
            Vector3 axisEnd;
            if (state.HasFlag(PlayerStateFlags.Prone))
            {
                radius = GunConfig.ProneBodyRadius;
                var forward = new Vector3(MathF.Sin(yaw), 0f, MathF.Cos(yaw));
                float halfAxis = MathF.Max(0f, GunConfig.ProneBodyLength * 0.5f - radius);
                var centre = feet + Vector3.UnitY * GunConfig.ProneBodyCenterHeight;
                axisStart = centre - forward * halfAxis;
                axisEnd = centre + forward * halfAxis;
            }
            else
            {
                radius = GunConfig.HitRadius;
                float height = PlayerMovement.Body.Height;
                // Cap centres, so the swept volume spans exactly [feet.Y, feet.Y + height].
                float capOffset = MathF.Min(radius, height * 0.5f);
                axisStart = feet + Vector3.UnitY * capOffset;
                axisEnd = feet + Vector3.UnitY * (height - capOffset);
            }

            float t = ClosestApproachOnRay(
                origin,
                direction,
                segmentLength,
                axisStart,
                axisEnd,
                out Vector3 onAxis);
            float missSq = (origin + direction * t - onAxis).LengthSquared();
            if (missSq > radius * radius) return null;

            bool head = SphereDistance(
                origin,
                direction,
                GunConfig.HeadCenter(feet, state, yaw),
                GunConfig.HeadRadius,
                segmentLength) is not null;
            return new PlayerHit(t, head);
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
