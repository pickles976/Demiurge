using Stride.Engine;
using NVector3 = System.Numerics.Vector3;

namespace Demiurge
{
    /// <summary>
    /// Assembles the <see cref="TerrainView"/> that drives LOD selection, from a Stride camera and
    /// whoever is doing the observing.
    ///
    /// The LENS comes from the camera — <c>VerticalFieldOfView</c> is what
    /// <see cref="FirstPersonCameraScript"/> lerps when the player aims, and reading it live is the
    /// entire mechanism by which scoping in refines terrain. The EYE and FACING come from whichever
    /// observer the session selects: normally the player, but the F3 camera while it is detached.
    /// That makes high detail follow the area being spectated without letting ordinary camera shake
    /// continuously churn the player's LOD selection.
    /// </summary>
    public static class TerrainViewBuilder
    {
        /// <summary>Fallback field of view for the frames before a camera exists. Matches
        /// <see cref="FirstPersonCameraScript"/>'s hip value, so selection does not jump once it does.</summary>
        const float FallbackFieldOfViewDegrees = 74f;

        public static TerrainView For(Game game, CameraComponent? camera, NVector3 eye, NVector3 forward)
        {
            var (tanHalfFov, aspect, height) = Lens(game, camera);

            return new TerrainView(
                eye, forward, NVector3.UnitY, tanHalfFov, aspect, height, TerrainLod.FrustumMarginDegrees);
        }

        /// <summary>
        /// A view with the right lens but no facing, for the frames before there is an observer to ask.
        /// Refusing to select anything would leave the world blank instead of merely coarse.
        /// </summary>
        public static TerrainView Everywhere(Game game, CameraComponent? camera, NVector3 eye)
        {
            var (tanHalfFov, _, height) = Lens(game, camera);

            return TerrainView.Everywhere(eye, tanHalfFov, height);
        }

        /// <summary>
        /// The world direction a yaw/pitch pair looks in.
        ///
        /// Yaw is atan2(x, z) — the convention the whole codebase uses, see GunMath and
        /// CombatBehavior — and positive pitch is up, matching how FirstPersonCameraScript builds the
        /// rotation it hands back to the controller.
        /// </summary>
        public static NVector3 Facing(float yaw, float pitch)
        {
            float horizontal = MathF.Cos(pitch);

            return new NVector3(
                MathF.Sin(yaw) * horizontal,
                MathF.Sin(pitch),
                MathF.Cos(yaw) * horizontal);
        }

        /// <summary>The direction an entity with a camera-style transform is pointing. Stride cameras
        /// look down their own -Z.</summary>
        public static NVector3 Facing(Entity entity)
        {
            var forward = Stride.Core.Mathematics.Vector3.Transform(
                -Stride.Core.Mathematics.Vector3.UnitZ, entity.Transform.Rotation);

            return new NVector3(forward.X, forward.Y, forward.Z);
        }

        static (float TanHalfFov, float Aspect, float Height) Lens(Game game, CameraComponent? camera)
        {
            var bounds = game.Window.ClientBounds;

            float height = MathF.Max(bounds.Height, 1f);
            float aspect = MathF.Max(bounds.Width, 1f) / height;
            float degrees = camera?.VerticalFieldOfView ?? FallbackFieldOfViewDegrees;

            return (MathF.Tan(degrees * MathF.PI / 360f), aspect, height);
        }
    }
}
