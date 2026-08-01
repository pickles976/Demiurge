using Demiurge.GameClient;
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
    /// Priority puts it after NetTransformScript, whose interpolated position it differentiates and
    /// whose yaw-only rotation it replaces.
    /// </summary>
    public sealed class ThrownGrenadeSpinScript : SyncScript
    {
        /// <summary>Metres of travel per full tumble. A grenade leaves the hand at 22 m/s, so this
        /// is about four turns a second on the way out, easing off through the arc.</summary>
        private const float MetresPerTurn = 5f;

        /// <summary>Below this a step is jitter in the interpolation rather than travel, and the
        /// tumble axis it implies is noise.</summary>
        private const float MinStep = 1e-4f;

        private Vector3 previous;
        private bool started;
        private Quaternion spin = Quaternion.Identity;

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
            if (distance > MinStep)
            {
                // End over end: the axis is horizontal and across the direction of travel. A round
                // falling straight down has no such axis, so it keeps the last one it had.
                var axis = Vector3.Cross(Vector3.UnitY, step / distance);
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
