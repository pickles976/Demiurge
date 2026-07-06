using Stride.Core.Mathematics;
using Stride.Rendering;

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
            => value < min ? 0.0f : value > max ? max : value;

        /// <summary>
        /// 2D wedge / perpendicular dot product: a.X*b.Y - a.Y*b.X.
        /// </summary>
        public static float Wedge(this Vector2 a, Vector2 b)
            => a.X * b.Y - a.Y * b.X;

        public static Vector3 FlattenY(this Vector3 v)
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

        public static Vector2 WorldToMouse(Vector3 worldPosition, Matrix viewProj)
        {
            // 2. Transform the world position to homogeneous clip space (Vector4)
            Vector4 clipSpacePos = Vector4.Transform(new Vector4(worldPosition, 1f), viewProj);

            // 3. Perform perspective division to get Normalized Device Coordinates (NDC)
            // Results in a range of [-1, 1] for X, Y, and Z
            if (clipSpacePos.W != 0)
            {
                clipSpacePos.X /= clipSpacePos.W;
                clipSpacePos.Y /= clipSpacePos.W;
                clipSpacePos.Z /= clipSpacePos.W;
            }

            // Note: If clipSpacePos.Z < 0, the point is behind the camera.

            // 4. Remap NDC -> mouse space so the result matches Input.MousePosition,
            //    which is what MousePosToScreenCoords expects as input.
            //    NDC:   X,Y in [-1, 1], origin at center, +Y up.
            //    Mouse: X,Y in [ 0, 1], origin at top-left, +Y down.
            float mouseX = clipSpacePos.X * 0.5f + 0.5f;
            float mouseY = 1f - (clipSpacePos.Y * 0.5f + 0.5f); // == (1 - ndcY) * 0.5, flips Y

            return new Vector2(mouseX, mouseY);
        }

        public static Vector2 WorldToScreen(Vector3 worldPosition, Matrix viewProj, Rectangle rect)
        {
            return MousePosToScreenCoords(WorldToMouse(worldPosition, viewProj), rect);
        }

        public static Stride.Core.Mathematics.Vector3 ToStride(this System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    }

}
