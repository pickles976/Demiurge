using Stride.Core.Mathematics;

namespace Demiurge 
{

    public static class MathExtensions
    {
        /// <summary>
        /// Linear remap from [inMin, inMax] to [outMin, outMax] (unclamped, matches Bevy's f32::remap).
        /// </summary>
        public static float Remap(this float value, float inMin, float inMax, float outMin, float outMax)
            => outMin + (value - inMin) / (inMax - inMin) * (outMax - outMin);

        /// <summary>
        /// Floors to 0 below min, clamps to max above max, otherwise passes through.
        /// (Same semantics as the Rust `step` in player.rs — kept for parity.)
        /// </summary>
        public static float Step(this float value, float min, float max)
            => value < min ? 0f : value > max ? max : value;

        /// <summary>
        /// 2D wedge / perpendicular dot product: a.X*b.Y - a.Y*b.X.
        /// </summary>
        public static float Wedge(this Vector2 a, Vector2 b)
            => a.X * b.Y - a.Y * b.X;

        public static Vector3 FlattenY(Vector3 v)
        {
            v.Y = 0f;
            v.Normalize();
            return v;
        }

        public static Vector2 MousePosToScreenCoords(Vector2 v, Rectangle rect)
        {
            var centered = v - new Vector2(0.5f, 0.5f);
            return new Vector2(centered.X * rect.Width, 1.0f - (centered.Y * rect.Height));
        }
    }

}
