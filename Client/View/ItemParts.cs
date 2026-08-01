using Demiurge;
using Demiurge.GameClient;
using Stride.Engine;
using Stride.Rendering;

namespace Demiurge.GameClient
{
    /// <summary>
    /// A moving part inside one item model — an SKS bolt travelling back and returning.
    ///
    /// It works by overriding the model's own node transform, which is only possible because the
    /// asset generator gives a model with articulated groups a Skeleton: without one Stride bakes
    /// every node into the vertex buffers and the part has no runtime existence (see the
    /// HasArticulatedGroup note in GltfAssetGenerator). Where the part travels TO is not tuned
    /// here either — it comes from the model's own `bolt_start`/`bolt_end` locators, so the artist
    /// moving the receiver moves the animation with it.
    ///
    /// Nothing else writes these node transforms: a weapon model carries no AnimationComponent, so
    /// unlike the aim-bone override in PlayerViewScript this can write an absolute local transform
    /// and does not have to track what a clip refreshed.
    /// </summary>
    public sealed class MovingPart
    {
        /// <summary>Back in a snap, forward a little slower — a cycling bolt, not a pendulum. Both
        /// inside one 10 rounds/second shot interval, so a held burst never starts a cycle on top
        /// of the one before it.</summary>
        private const float TravelSeconds = 0.025f;
        private const float ReturnSeconds = 0.055f;

        private readonly string node;
        private readonly System.Numerics.Vector3 travel;

        private int index = -1;
        private System.Numerics.Vector3 rest;
        private bool haveRest;
        private float elapsed = float.MaxValue;
        private bool held;

        private MovingPart(string node, System.Numerics.Vector3 travel)
        {
            this.node = node;
            this.travel = travel;
        }

        /// <summary>The part this item has, or null for a model with no `&lt;part&gt;_start` /
        /// `&lt;part&gt;_end` pair. Absence is ordinary: only the SKS has a bolt.</summary>
        public static MovingPart? For(ModelLocators locators, string model, string part)
        {
            if (locators.Get(model, part + "_start") is not { } start
                || locators.Get(model, part + "_end") is not { } end)
                return null;

            var travel = end.Translation - start.Translation;
            return travel.LengthSquared() < 1e-8f ? null : new MovingPart(part, travel);
        }

        /// <summary>Starts one cycle, restarting it if one is already running.</summary>
        public void Cycle() => elapsed = 0f;

        /// <summary>
        /// Locks the part at the end of its travel — an SKS holds its bolt open while the magazine
        /// is being filled, and a rifle that cycles briskly through a reload looks like it is
        /// loading itself.
        ///
        /// Releasing hands over to the ordinary return leg rather than snapping, so the bolt runs
        /// forward the same way it does after a shot.
        /// </summary>
        public void SetHeld(bool value)
        {
            if (value == held) return;
            held = value;
            if (!held) elapsed = TravelSeconds;
        }

        public void Update(ModelComponent? model, float dt)
        {
            var skeleton = model?.Skeleton;
            if (skeleton == null) return;

            if (index < 0)
            {
                index = Array.FindIndex(skeleton.Nodes, n => n.Name == node);
                if (index < 0) return;   // model without the part: no animation rather than a crash
            }

            ref var transform = ref skeleton.NodeTransformations[index].Transform;
            if (!haveRest)
            {
                rest = (System.Numerics.Vector3)transform.Position;
                haveRest = true;
            }

            if (held)
            {
                transform.Position = (rest + travel).ToStride();
                return;
            }

            if (elapsed > TravelSeconds + ReturnSeconds)
            {
                transform.Position = rest.ToStride();
                return;
            }

            elapsed += dt;
            float back = elapsed < TravelSeconds
                ? elapsed / TravelSeconds
                : Math.Max(0f, 1f - (elapsed - TravelSeconds) / ReturnSeconds);
            transform.Position = (rest + travel * back).ToStride();
        }
    }

    /// <summary>
    /// A Minecraft-style tool swing: wind the blade back, accelerate through the chop, and SNAP
    /// back to rest — the return is a discontinuity, not an easing. That is what makes each dig
    /// read as a separate blow instead of a pendulum, and it is why the chop curve ends at full
    /// extension rather than coming home under its own power.
    ///
    /// Pure timing — it produces one pitch angle in the item's own frame and knows nothing about
    /// cameras or bones, which is what lets the same number drive the local view model and
    /// everybody else's third-person view of the same dig.
    /// </summary>
    public sealed class SwingAnimation
    {
        /// <summary>Two arcs per dig. Derived from the dig rate rather than picked, so the swing
        /// stays tied to the edits it is a picture of; the factor is what makes it read as a chop
        /// instead of a slow wave.</summary>
        private const float SwingsPerDig = 2f;
        private static readonly float SwingSeconds = 1f / (Digging.HoldHz * SwingsPerDig);

        private const float WindupFraction = 0.35f;
        private const float WindupRadians = -0.30f;
        private const float ChopRadians = 1.15f;

        private float phase = float.MaxValue;

        /// <summary>Pitch in radians for this frame, positive swinging the blade down and forward.</summary>
        public float Angle { get; private set; }

        public void Update(bool swinging, float dt)
        {
            if (phase > SwingSeconds)
            {
                if (!swinging)
                {
                    Angle = 0f;
                    return;
                }
                phase = 0f;
            }

            phase += dt;
            float t = Math.Clamp(phase / SwingSeconds, 0f, 1f);
            if (t < WindupFraction)
            {
                Angle = WindupRadians * (t / WindupFraction);
                return;
            }

            // Squared, so the blade accelerates into the ground rather than arriving at a constant
            // rate. The arc deliberately ENDS here, at full extension; the next Update finds the
            // phase spent and either restarts the swing or drops Angle to zero in one frame.
            float chop = (t - WindupFraction) / (1f - WindupFraction);
            Angle = WindupRadians + (ChopRadians - WindupRadians) * chop * chop;
        }
    }
}
