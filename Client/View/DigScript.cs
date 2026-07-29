using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace Demiurge
{
    /// <summary>
    /// Digging with your hands: highlights the voxel sample you are about to edit, and edits it on
    /// left click.
    ///
    /// Nothing here is predicted. The outline is local, but the hole is not: the request goes to the
    /// server and the voxel disappears when the broadcast edit comes back. Predicting the dig would
    /// mean the client carving its own terrain, and then a rejected dig — out of reach, too soon,
    /// armed — would leave a hole in one world and not the other, with no correction path, because
    /// chunks stream once and are never resent. A dig is cheap to wait a round trip for; a
    /// permanently divergent world is not.
    /// </summary>
    public class DigScript : SyncScript
    {
        public required PlayerRegistry Registry { get; init; }
        public required TerrainState Terrain { get; init; }
        public required NetworkManager Network { get; init; }
        public required ClientInputState InputState { get; init; }

        /// <summary>Local rate limit, matching the server's TicksPerDig. Not a substitute for the
        /// server's gate — it just stops us spamming requests it would throw away.</summary>
        private const float MinDigInterval = 1f / Digging.HoldHz;

        private float sinceDig = MinDigInterval;
        private bool wasDown;

        /// <summary>The voxel currently under the crosshair, or null if nothing is in reach.</summary>
        public System.Numerics.Vector3? Target { get; private set; }

        public override void Update()
        {
            sinceDig += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            Target = null;

            if (InputState.TerminalOpen)
            {
                wasDown = false;
                return;
            }
            if (Entity.Get<DebugFlyCameraScript>()?.Active == true) return;
            if (Registry.LocalPlayer is not { } local) return;

            // Slot 2 is the placeholder shovel/empty hand. Empty slot 1 is deliberately not a
            // digging tool, so the number keys always have stable meaning.
            if (local.Hotbar != HotbarSlot.Shovel) return;

            if (FindTarget() is not { } target) return;
            Target = target;

            WorldPreviewRenderer.VoxelSample(target, new Color(255, 255, 255, 230));

            // Edge-triggered: one dig per click, and holding the button repeats at the rate limit
            // rather than every frame.
            bool down = Input.IsMouseButtonDown(MouseButton.Left);
            bool pressed = down && !wasDown;
            wasDown = down;

            if ((pressed || down) && sinceDig >= MinDigInterval)
            {
                Network.SendDig(new PlayerDigData { Target = target, Hotbar = local.Hotbar });
                sinceDig = 0f;
            }
        }

        /// <summary>
        /// The voxel the crosshair is on, within arm's reach.
        ///
        /// Cast in two legs, and the second one is the whole point. The camera decides WHERE you are
        /// looking, because the crosshair is the camera's opinion; but the dig ray is then fired
        /// from the PLAYER toward that point, so it can only ever find something the player could
        /// actually touch.
        ///
        /// Casting straight from the camera — which is what this used to do — let you dig behind
        /// yourself. The camera orbits two or three metres back, so with a wall or a rise between it
        /// and the character the first thing that ray met was on the far side of you, and it lit up
        /// and dug quite happily.
        /// </summary>
        private System.Numerics.Vector3? FindTarget()
        {
            if (Registry.LocalPlayer is not { } local) return null;
            if (Entity.Get<LocalPlayerController>()?.AimPoint is not { } aimPoint) return null;

            var eye = Digging.Eye(local.Position);

            var toAim = aimPoint - eye;
            if (toAim.LengthSquared() < 1e-6f) return null;   // looking at our own eye

            if (TerrainRaycast.Cast(Terrain.Map, eye, System.Numerics.Vector3.Normalize(toAim), Digging.Reach)
                is not { } hit) return null;

            var target = Digging.TargetVoxel(hit.Point, hit.Normal);

            // The same test the server will run, from the same origin — so anything highlighted here
            // is something the server will accept.
            return Digging.InReach(local.Position, target) ? target : null;
        }
    }
}
