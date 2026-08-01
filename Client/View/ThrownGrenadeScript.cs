using Demiurge.GameClient;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace Demiurge
{
    /// <summary>
    /// Tumbles a grenade in flight, end over end along its own arc.
    ///
    /// Purely a picture: the server's grenade is a point with a radius and neither its bounce nor
    /// its blast cares which way the model is pointing, so this never leaves the client and nothing
    /// has to agree about it. Two clients can disagree about the tumble and nothing is wrong.
    ///
    /// It runs off DISTANCE TRAVELLED rather than a fixed spin rate, which is what makes it stop
    /// looking wrong at the ends: a grenade slows as it arcs, skips, and settles, and a rotation
    /// driven by how far it moved slows and stops with it for free — no rest detection, no
    /// thresholds, and no way to leave one spinning on the ground.
    ///
    /// It also owns the bounce sound, for the same reason and by the same means: a bounce is a sharp
    /// change of direction in a path the client can already see, so it needs no event on the wire.
    ///
    /// Priority puts it after NetTransformScript, whose interpolated position it differentiates and
    /// whose yaw-only rotation it replaces.
    /// </summary>
    public sealed class ThrownGrenadeScript : SyncScript
    {
        private const string BounceSound = "assets/sfx/grenade_bounce.wav";

        /// <summary>
        /// Below this a contact is a settle, not a bounce. A grenade coming to rest taps the ground
        /// several times in a row with almost no speed, and playing a clang for each is worse than
        /// playing none.
        /// </summary>
        private const float MinBounceSpeed = 3.5f;

        /// <summary>
        /// A bounce is the VERTICAL component flipping from falling to rising. Testing the whole
        /// direction for a reversal was wrong: a grenade thrown flat keeps most of its horizontal
        /// speed through a bounce (TangentialRetention is 0.76 against a GroundRestitution of 0.32),
        /// so the path barely turns at all and the sound never played. The bounce is in the Y.
        /// </summary>
        private const float FallingY = -0.15f;
        private const float RisingY = 0.05f;

        /// <summary>Metres of travel per full tumble. A grenade leaves the hand at 22 m/s, so this
        /// is about four turns a second on the way out, easing off through the arc.</summary>
        private const float MetresPerTurn = 5f;

        /// <summary>Below this a step is jitter in the interpolation rather than travel, and the
        /// tumble axis it implies is noise.</summary>
        private const float MinStep = 1e-4f;

        private Vector3 previous;
        private bool started;
        private Quaternion spin = Quaternion.Identity;
        private Vector3 lastDirection;
        private float lastSpeed;
        private SoundManager sound = null!;

        public override void Start() => sound = Services.GetSafeServiceAs<SoundManager>();

        public override void Update()
        {
            var position = Entity.Transform.Position;
            if (!started)
            {
                previous = position;
                started = true;
                // Stand it up rather than leaving it flat on its first frame; the tumble takes over
                // from there.
                Entity.Transform.Rotation = spin;
                return;
            }

            var step = position - previous;
            previous = position;

            float distance = step.Length();
            float dt = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            if (distance > MinStep)
            {
                var direction = step / distance;
                float speed = dt > 0f ? distance / dt : 0f;

                // Speed going IN, not coming out: a bounce sheds most of its energy, so the arrival
                // is what was loud.
                if (lastSpeed >= MinBounceSpeed
                    && lastDirection.Y < FallingY
                    && direction.Y > RisingY)
                    sound.PlayOneShotSpatial(BounceSound, position, falloff: SoundFalloff.Bounce);

                lastDirection = direction;
                lastSpeed = speed;

                // End over end: the axis is horizontal and across the direction of travel. A round
                // falling straight down has no such axis, so it keeps the last one it had.
                var axis = Vector3.Cross(Vector3.UnitY, direction);
                if (axis.LengthSquared() > 1e-6f)
                {
                    axis.Normalize();
                    // Applied in WORLD space, hence spin * delta: Stride multiplies as "apply the
                    // left rotation, THEN the right".
                    spin *= Quaternion.RotationAxis(
                        axis,
                        distance / MetresPerTurn * MathUtil.TwoPi);
                    spin.Normalize();
                }
            }

            Entity.Transform.Rotation = spin;
        }
    }
}
