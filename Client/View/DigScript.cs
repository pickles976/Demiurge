using Demiurge.GameClient;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Input;

namespace Demiurge
{
    /// <summary>
    /// Digging with your hands: outlines the voxel you are about to take out, and takes it out on
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

        /// <summary>Points around the brush ring. Enough that the curve reads as one at arm's
        /// length, few enough that projecting each is free.</summary>
        private readonly System.Numerics.Vector3[] ring = new System.Numerics.Vector3[24];
        private readonly List<Vector3> ringWorld = new(24);

        /// <summary>Local rate limit, matching the server's TicksPerDig. Not a substitute for the
        /// server's gate — it just stops us spamming requests it would throw away.</summary>
        private const float MinDigInterval = 6f / NetworkConfig.TickRate;

        private float sinceDig;
        private bool wasDown;

        /// <summary>The voxel currently under the crosshair, or null if nothing is in reach.</summary>
        public System.Numerics.Vector3? Target { get; private set; }

        public override void Update()
        {
            sinceDig += (float)Game.UpdateTime.Elapsed.TotalSeconds;
            Target = null;

            if (Entity.Get<DebugFlyCameraScript>()?.Active == true) return;
            if (Registry.LocalPlayer is not { } local) return;

            // Hands only. Holding a gun means left click shoots, and the two must not both fire off
            // the same button.
            if (local.IsArmed) return;

            if (FindTarget() is not var (hit, target)) return;
            Target = target;

            DrawBrush(target, hit.Normal);

            // Edge-triggered: one dig per click, and holding the button repeats at the rate limit
            // rather than every frame.
            bool down = Input.IsMouseButtonDown(MouseButton.Left);
            bool pressed = down && !wasDown;
            wasDown = down;

            if ((pressed || down) && sinceDig >= MinDigInterval)
            {
                Network.SendDig(new PlayerDigData { Target = target });
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
        private (TerrainHit Hit, System.Numerics.Vector3 Target)? FindTarget()
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
            return Digging.InReach(local.Position, target) ? (hit, target) : null;
        }

        /// <summary>
        /// The brush footprint: a ring the size of the bite, lying on the surface.
        ///
        /// Replaces outlining the affected triangles, which showed the wrong thing. Those edges are
        /// the MESHER's tessellation — an implementation detail whose density and direction vary
        /// with how that patch happened to triangulate — so the outline looked noisy and changed
        /// shape as you swept across ground that was not changing. A ring is a statement about the
        /// TOOL: this much comes out, from here.
        /// </summary>
        private void DrawBrush(System.Numerics.Vector3 target, System.Numerics.Vector3 normal)
        {
            int count = Digging.ProjectedRing(Terrain.Map, target, normal, ring);
            if (count == 0) return;   // bite fully buried: nothing would open, so show nothing

            ringWorld.Clear();
            for (int i = 0; i < count; i++) ringWorld.Add(ring[i].ToStride());

            LineRenderer.DrawPolyline(ringWorld, new Color(255, 255, 255, 230), closed: true);
        }
    }
}
