namespace Demiurge
{
    /// <summary>
    /// A piecewise-linear curve over sorted control points. Small, but it is the piece of terrain
    /// generation that actually decides what the world looks like.
    ///
    /// Why terrain needs a curve rather than a multiply: a multiply maps noise to height
    /// proportionally, so every region comes out equally hilly and every transition is equally
    /// gradual — a world of medium hills with no plains and no mountains. A curve can hold a SHELF, a
    /// wide span of input mapping to one output, and that is where flat regions come from. Put a steep
    /// segment beside the shelf and you get the cliff at the edge of a plateau.
    ///
    /// The load-bearing idea: flatness is a property of the CURVE, not of low-amplitude noise. The
    /// noise still wanders across the shelf; the curve ignores it.
    /// </summary>
    /// <remarks>
    /// A class, not a struct: a struct always has an implicit parameterless constructor, so
    /// <c>default(Spline)</c> would hold null arrays and every Evaluate would be a
    /// NullReferenceException that the validation below cannot prevent. Splines are built once into
    /// static fields, so there is nothing to gain by making them copyable anyway.
    /// </remarks>
    public sealed class Spline
    {
        readonly float[] inputs;
        readonly float[] outputs;

        public Spline(params (float input, float output)[] points)
        {
            if (points is null || points.Length == 0)
                throw new ArgumentException("a spline needs at least one control point", nameof(points));

            inputs = new float[points.Length];
            outputs = new float[points.Length];

            for (int i = 0; i < points.Length; i++)
            {
                // Strictly ascending, because Evaluate walks forward and a duplicate input would make
                // the segment zero-width and the interpolation a division by zero.
                if (i > 0 && points[i].input <= points[i - 1].input)
                    throw new ArgumentException(
                        $"control points must ascend in input; point {i} is {points[i].input} after {points[i - 1].input}",
                        nameof(points));

                inputs[i] = points[i].input;
                outputs[i] = points[i].output;
            }
        }

        /// <summary>
        /// Clamps outside the control range rather than extrapolating. Extrapolating a hand-tuned
        /// terrain curve past its ends is how you get a mountain poking out of the top of the world.
        /// </summary>
        public float Evaluate(float t)
        {
            if (t <= inputs[0]) return outputs[0];

            int last = inputs.Length - 1;
            if (t >= inputs[last]) return outputs[last];

            int i = 1;
            while (inputs[i] < t) i++;

            float fraction = (t - inputs[i - 1]) / (inputs[i] - inputs[i - 1]);
            return outputs[i - 1] + (outputs[i] - outputs[i - 1]) * fraction;
        }

        /// <summary>
        /// Output bounds. Exact, because the curve is piecewise linear between its control points, so
        /// it cannot exceed them. Terrain uses these to prove the height budget fits the world.
        /// </summary>
        public float MinOutput
        {
            get
            {
                float min = outputs[0];
                for (int i = 1; i < outputs.Length; i++) min = MathF.Min(min, outputs[i]);
                return min;
            }
        }

        /// <inheritdoc cref="MinOutput"/>
        public float MaxOutput
        {
            get
            {
                float max = outputs[0];
                for (int i = 1; i < outputs.Length; i++) max = MathF.Max(max, outputs[i]);
                return max;
            }
        }
    }
}
