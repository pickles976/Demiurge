using Demiurge;
using Demiurge.GameClient;
using Stride.Engine;
using Stride.Rendering;

namespace Demiurge.GameClient
{
    /// <summary>
    /// A moving part inside one item model — a bolt travelling back and returning.
    ///
    /// It works by overriding the model's own node transform, which is only possible because the
    /// asset generator gives a model with articulated groups a Skeleton: without one Stride bakes
    /// every node into the vertex buffers and the part has no runtime existence (see the
    /// HasArticulatedGroup note in GltfAssetGenerator).
    ///
    /// WHERE the part goes is not tuned here: the `&lt;part&gt;_start` and `&lt;part&gt;_end`
    /// locators are two poses of one frame the part is rigidly bolted to, and the part simply
    /// rides that frame. Position and ORIENTATION both, which is why a Mosin's bolt turns its
    /// handle up and THEN draws back — closing in the opposite order — while an SKS's, whose two
    /// locators differ only in position, keeps travelling in a straight line without either model
    /// knowing about the other. Riding the frame is also what makes the turn pivot about the bolt's
    /// own axis rather than about wherever the artist happened to put the node's origin.
    ///
    /// WHEN it goes there comes from the caller's <see cref="WeaponFx.BoltCycle"/>, so the picture
    /// and the sound of one cycle are paced by the same four numbers.
    ///
    /// Nothing else writes these node transforms: a weapon model carries no AnimationComponent, so
    /// unlike the aim-bone override in PlayerViewScript this can write an absolute local transform
    /// and does not have to track what a clip refreshed.
    /// </summary>
    public sealed class MovingPart
    {
        /// <summary>
        /// How much of one leg is spent turning before anything slides. Short because lifting a
        /// handle is a flick and drawing a bolt is a stroke.
        ///
        /// Zero for a part whose two anchors share a rotation — every self-loading action — so their
        /// travel stays the straight line it has always been rather than being squeezed into the
        /// back two thirds of its leg. That is why this costs no extra number on any weapon.
        /// </summary>
        private const float TurnFraction = 0.35f;

        private readonly string node;
        private readonly ModelLocators.Pose start;
        private readonly ModelLocators.Pose end;
        private readonly WeaponFx.BoltCycle cycle;
        private readonly float turnFraction;

        private int index = -1;
        private System.Numerics.Vector3 restTranslation;
        private System.Numerics.Quaternion restRotation;
        private bool haveRest;
        private float elapsed = float.MaxValue;
        private bool held;

        // The part's own transform expressed IN the start locator's frame, computed once the model
        // has handed over its rest pose. Everything the update writes is this, carried by the
        // interpolated frame.
        private System.Numerics.Vector3 localTranslation;
        private System.Numerics.Quaternion localRotation;

        private MovingPart(
            string node,
            ModelLocators.Pose start,
            ModelLocators.Pose end,
            WeaponFx.BoltCycle cycle,
            bool turns)
        {
            this.node = node;
            this.start = start;
            this.end = end;
            this.cycle = cycle;
            turnFraction = turns ? TurnFraction : 0f;
        }

        /// <summary>The part this item has, or null for a model with no `&lt;part&gt;_start` /
        /// `&lt;part&gt;_end` pair. Absence is ordinary: most weapons have no bolt group.</summary>
        public static MovingPart? For(
            ModelLocators locators,
            string model,
            string part,
            WeaponFx.BoltCycle cycle)
        {
            if (locators.Get(model, part + "_start") is not { } start
                || locators.Get(model, part + "_end") is not { } end)
                return null;

            // A pair of locators that neither move nor turn describe no animation at all.
            bool moves = (end.Translation - start.Translation).LengthSquared() >= 1e-8f;
            bool turns = MathF.Abs(System.Numerics.Quaternion.Dot(start.Rotation, end.Rotation)) < 0.999999f;
            return moves || turns ? new MovingPart(part, start, end, cycle, turns) : null;
        }

        /// <summary>Starts one cycle, restarting it if one is already running.</summary>
        public void Cycle() => elapsed = 0f;

        /// <summary>
        /// Holds the part at the end of its travel — a rifle holds its bolt open while the magazine
        /// is being filled, and one that cycles briskly through a reload looks like it is loading
        /// itself.
        ///
        /// Both edges hand over to a leg of the ordinary cycle rather than snapping: taking hold
        /// seeks into the travel leg at wherever the part currently is, so the reload OPENS the
        /// bolt at the same speed a shot does instead of teleporting it back, and releasing seeks to
        /// the return leg so it runs forward the same way. Seeking by position rather than
        /// restarting is what keeps a reload begun mid-cycle from jumping.
        /// </summary>
        public void SetHeld(bool value)
        {
            if (value == held) return;
            held = value;
            elapsed = held
                ? cycle.DelaySeconds + Travel() * cycle.TravelSeconds
                : cycle.DelaySeconds + cycle.TravelSeconds + cycle.HoldSeconds;
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
                restTranslation = (System.Numerics.Vector3)transform.Position;
                restRotation = new System.Numerics.Quaternion(
                    transform.Rotation.X, transform.Rotation.Y, transform.Rotation.Z, transform.Rotation.W);
                haveRest = true;

                // start^-1 * rest, i.e. the rest pose seen from the start locator. The rest pose is
                // the part's LOCAL transform and the locators are in the model's root space; the two
                // coincide because these groups hang directly off the root, which is the same
                // assumption that makes the locator translations usable as travel at all.
                var inverse = System.Numerics.Quaternion.Inverse(start.Rotation);
                localTranslation = System.Numerics.Vector3.Transform(restTranslation - start.Translation, inverse);
                localRotation = System.Numerics.Quaternion.Concatenate(restRotation, inverse);
            }

            // Held stops the clock at the end of the travel leg rather than at the moment the hold
            // began, so the part still draws itself back and only then waits there.
            elapsed = held
                ? MathF.Min(elapsed + dt, cycle.DelaySeconds + cycle.TravelSeconds)
                : elapsed + dt;
            Seat(ref transform, Travel());
        }

        /// <summary>
        /// How far back the part is, 0 at rest and 1 fully travelled, for the current
        /// <see cref="elapsed"/>. Four legs: wait, out, dwell, home.
        /// </summary>
        private float Travel()
        {
            float t = elapsed - cycle.DelaySeconds;
            if (t <= 0f) return 0f;
            if (t < cycle.TravelSeconds) return t / cycle.TravelSeconds;

            t -= cycle.TravelSeconds;
            if (t < cycle.HoldSeconds) return 1f;

            t -= cycle.HoldSeconds;
            return cycle.ReturnSeconds > 0f ? MathF.Max(0f, 1f - t / cycle.ReturnSeconds) : 0f;
        }

        /// <summary>Puts the part back on the frame its two locators describe, <paramref name="s"/>
        /// of the way from start to end.</summary>
        private void Seat(ref TransformTRS transform, float s)
        {
            if (s <= 0f)
            {
                transform.Position = restTranslation.ToStride();
                transform.Rotation = restRotation.ToStride();
                return;
            }

            // Turn FIRST, then slide — the two channels split the one parameter rather than running
            // together over the whole leg, which is what would send a rotating bolt backwards along
            // a corkscrew. Splitting one parameter rather than adding a second timer is also what
            // makes the RETURN come out right for free: run s backwards and the bolt runs forward
            // and only then turns its handle down, which is the order a bolt is actually closed in.
            float turn = turnFraction > 0f ? MathF.Min(1f, s / turnFraction) : 1f;
            float slide = MathF.Max(0f, (s - turnFraction) / (1f - turnFraction));

            var frameRotation = System.Numerics.Quaternion.Slerp(start.Rotation, end.Rotation, turn);
            var frameTranslation = System.Numerics.Vector3.Lerp(start.Translation, end.Translation, slide);

            transform.Position =
                (frameTranslation + System.Numerics.Vector3.Transform(localTranslation, frameRotation)).ToStride();
            transform.Rotation =
                System.Numerics.Quaternion.Concatenate(localRotation, frameRotation).ToStride();
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
