using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// The aiming reticle: a centre-screen crosshair, plus a marker on the terrain the camera is
    /// pointing at.
    ///
    /// Centre-screen and not at the cursor, because <see cref="ShoulderCameraScript"/> locks the
    /// mouse to the middle of the window and hides it — there is no cursor to sit under. That is
    /// also why <see cref="CursorReticleScript"/> cannot be reused: it belongs to the dead
    /// cursor-aimed <see cref="ThirdPersonCameraScript"/>.
    ///
    /// The two marks answer different questions and both are needed while the camera sits off the
    /// shoulder. The crosshair is where the CAMERA looks; the world marker is where that line
    /// actually meets the ground, which is the thing you are about to shoot. They only coincide once
    /// the gun is made to aim at the same point.
    /// </summary>
    public class ReticleScript : SyncScript
    {
        public required PlayerRegistry Registry { get; init; }

        // Screen-space, in pixels from the centre. Aiming pulls the gap in — the usual shorthand for
        // the shot being tighter, and it costs nothing to read.
        const float HipGap = 9f;
        const float AimGap = 5f;
        const float ArmLength = 6f;

        /// <summary>Radius of the world marker, in metres at one metre — scaled by range below so it
        /// stays the same size on screen instead of shrinking to nothing across a valley.</summary>
        const float MarkerAngularRadius = 0.012f;

        public override void Update()
        {
            // The fly camera detaches from the player and owns the view; a reticle for a gun nobody
            // is holding is just clutter on the debug view.
            if (Entity.Get<DebugFlyCameraScript>()?.Active == true) return;
            if (Registry.LocalPlayer is not { } local) return;

            bool aiming = local.State.HasFlag(PlayerStateFlags.Aiming);
            DrawCrosshair(aiming ? AimGap : HipGap, aiming ? Color.White : new Color(255, 255, 255, 160));

            // Read back from the controller rather than cast again here. Two casts would be two
            // answers, and the one drawn would not be the one shot at — which is the whole failure
            // this reticle exists to make visible.
            if (Entity.Get<LocalPlayerController>()?.AimPoint is not { } aimPoint) return;

            var marked = aimPoint.ToStride();
            float range = Vector3.Distance(Entity.Transform.Position, marked);

            // Drawn as a mark lying at a place IN THE WORLD rather than a screen-space dot, so it
            // picks up the slope of whatever it is sitting on. Scaled by range to hold its size on
            // screen instead of shrinking to nothing across a valley.
            LineRenderer.DrawPoint(marked, Color.White, MarkerAngularRadius * range);
        }

        static void DrawCrosshair(float gap, Color color)
        {
            // 2D coordinates are pixels centred on the screen, so the centre is literally zero.
            foreach (var axis in new[] { Vector2.UnitX, Vector2.UnitY })
            {
                LineRenderer.DrawLine2D(axis * gap, axis * (gap + ArmLength), color);
                LineRenderer.DrawLine2D(-axis * gap, -axis * (gap + ArmLength), color);
            }
        }
    }
}
