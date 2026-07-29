using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// The aiming reticle: a centre-screen dot and four posts.
    ///
    /// Centre-screen and not at the cursor, because <see cref="FirstPersonCameraScript"/> locks the
    /// mouse to the middle of the window and hides it — there is no cursor to sit under. That is
    /// also why <see cref="CursorReticleScript"/> cannot be reused: it belongs to the dead
    /// cursor-aimed <see cref="ThirdPersonCameraScript"/>.
    /// </summary>
    public class ReticleScript : SyncScript
    {
        public required PlayerRegistry Registry { get; init; }
        public required ClientInputState InputState { get; init; }

        // Screen-space, in pixels from the centre. The MOA remains physical; this visual
        // scale deliberately exaggerates its movement so recoil and recovery are easy to read.
        const float HipGap = 12f;
        const float AimGap = 7f;
        const float ArmLength = 7f;
        const float BloomVisualScale = 2.25f;
        const float MaxBloomGap = 52f;
        const float DotRadius = 1.5f;
        static readonly Color DotColor = new(255, 255, 255, 165);

        public override void Update()
        {
            // The fly camera detaches from the player and owns the view; a reticle for a gun nobody
            // is holding is just clutter on the debug view.
            if (InputState.TerminalOpen || Entity.Get<DebugFlyCameraScript>()?.Active == true) return;
            if (Registry.LocalPlayer is not { } local) return;

            bool aiming = local.State.HasFlag(PlayerStateFlags.Aiming);
            float baseGap = aiming ? AimGap : HipGap;
            float bloom = MathUtil.Clamp(
                SpreadGap(local.CurrentSpreadMoa) * BloomVisualScale,
                0f,
                MaxBloomGap);
            float gap = local.IsArmed ? MathF.Max(baseGap, bloom) : baseGap;
            DrawCrosshair(
                gap,
                aiming ? new Color(255, 255, 255, 235) : new Color(255, 255, 255, 205));
        }

        /// <summary>
        /// Converts the 95%-containment angular radius into screen pixels at the camera's
        /// current FOV. The crosshair therefore blooms from the same MOA used to sample shots.
        /// </summary>
        float SpreadGap(float moa)
        {
            var camera = Entity.Get<CameraComponent>();
            if (camera == null || Game.Window.ClientBounds.Height <= 0) return 0f;

            float angularRadius = moa * 0.5f * Spread.RadiansPerMoa;
            float halfFov = MathUtil.DegreesToRadians(camera.VerticalFieldOfView) * 0.5f;
            return MathF.Tan(angularRadius)
                / MathF.Tan(halfFov)
                * Game.Window.ClientBounds.Height
                * 0.5f;
        }

        static void DrawCrosshair(float gap, Color color)
        {
            // At this radius the two-pixel line stroke reads as a soft translucent dot.
            LineRenderer.Circle2D(Vector2.Zero, DotRadius, DotColor, segments: 12);

            // 2D coordinates are pixels centred on the screen, so the centre is literally zero.
            foreach (var axis in new[] { Vector2.UnitX, Vector2.UnitY })
            {
                LineRenderer.DrawLine2D(axis * gap, axis * (gap + ArmLength), color);
                LineRenderer.DrawLine2D(-axis * gap, -axis * (gap + ArmLength), color);
            }
        }
    }
}
