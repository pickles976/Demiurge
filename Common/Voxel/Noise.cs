using NoiseDotNet;
using System.Numerics;

namespace Demiurge
{
    /// <summary>
    /// The noise fields terrain is built from. Supplies the wandering; <see cref="TerrainShape"/> owns
    /// what the wandering means.
    ///
    /// Two facts about NoiseDotNet's fractal helper that shape this file. It accumulates octaves
    /// WITHOUT normalising, so the raw output range is the sum of the octave amplitudes and has to be
    /// divided back down here. And it increments the seed once per octave, so the three fields' seeds
    /// must be spaced further apart than their octave counts or they would sample the same gradients
    /// and the fields would correlate — mountains would sit exactly where the detail peaks are.
    /// </summary>
    public class NoiseGen
    {
        /// <summary>The world seed. Each field offsets from it; see the note above about spacing.</summary>
        public const int Seed = 100;

        const int ErosionSeed = Seed;
        const int DetailSeed  = Seed + 100;
        const int RidgeSeed   = Seed + 200;

        // Frequencies are 1 / wavelength in voxels. Erosion's wavelength is what sets how big a plain
        // or a mountain range is, so it is the number to reach for when regions feel wrong.
        //
        // Detail and ridge wavelengths trade steepness against noise, but far less than you would expect:
        // at persistence 0.5 and lacunarity 2 every octave contributes the SAME gradient magnitude, so
        // shortening the base wavelength mostly adds fine detail rather than steepness. Measured, going
        // from 84/125 to 60/80 moved the world's steepest slope only 47 -> 49 degrees, though it did
        // multiply the number of columns past 40 degrees by eight.
        //
        // The real limit is structural: a smooth fbm HEIGHTMAP cannot make cliffs. Getting genuinely
        // steep faces needs a different mechanism — terracing, a spline applied to slope itself, or a 3D
        // density term — and the last of those is the one that also buys overhangs.
        const float ErosionWavelength = 220f;
        const float DetailWavelength  = 60f;
        const float RidgeWavelength   = 80f;

        static readonly FractalSettings DetailFractal = new(octaves: 4, persistence: 0.5f, lacunarity: 2f);
        static readonly FractalSettings RidgeFractal  = new(octaves: 3, persistence: 0.5f, lacunarity: 2f);

        /// <summary>
        /// Surface height in world units for every column of a chunk, PADDED by one on each side so
        /// slope can be central-differenced at the chunk edge. Index it with
        /// <see cref="ChunkTransforms.PaddedColumnIndexOf"/>, never by hand.
        /// </summary>
        public static float[] GenerateHeightsForChunk(ChunkIndex index)
        {
            int count = ChunkTransforms.PaddedColumns;

            var worldX = new float[count];
            var worldZ = new float[count];

            for (int i = 0; i < count; i++)
            {
                Vector2 position = ChunkTransforms.PaddedColumnWorldPosition(index, i);
                worldX[i] = position.X;
                worldZ[i] = position.Y;   // world Z; NoiseSettings applies the frequency scaling
            }

            var erosion = new float[count];
            var detail  = new float[count];
            var ridge   = new float[count];

            Noise.GradientNoise2D(worldX, worldZ, erosion, Settings(ErosionWavelength, ErosionSeed));
            Noise.GradientNoise2DFractal(worldX, worldZ, detail, Settings(DetailWavelength, DetailSeed), DetailFractal);
            Noise.GradientNoise2DFractal(worldX, worldZ, ridge, Settings(RidgeWavelength, RidgeSeed), RidgeFractal);

            float detailScale = 1f / AmplitudeSum(DetailFractal);
            float ridgeScale  = 1f / AmplitudeSum(RidgeFractal);

            var heights = new float[count];
            for (int i = 0; i < count; i++)
            {
                heights[i] = TerrainShape.Height(
                    erosion: Unit(erosion[i]),
                    detail:  Unit(detail[i] * detailScale),
                    ridge:   Fold(Unit(ridge[i] * ridgeScale)));
            }

            return heights;
        }

        /// <summary>
        /// Folds noise into a ridge: 0 in the valleys, 1 along the crest. The absolute value creates a
        /// crease in an otherwise smooth field, and a crease in the parameter becomes a ridgeline in the
        /// terrain. This is why mountains here do not look like Perlin blobs.
        /// </summary>
        static float Fold(float noise) => 1f - MathF.Abs(noise);

        /// <summary>
        /// Clamped to [-1, 1], which is the domain <see cref="TerrainShape"/>'s splines are drawn over.
        /// Clamping rather than rescaling because the splines clamp at their ends anyway, so an
        /// occasional outlier costs a flat spot rather than a mountain through the top of the world.
        /// </summary>
        static float Unit(float noise) => Math.Clamp(noise, -1f, 1f);

        /// <summary>
        /// What the fractal helper's octaves sum to, which is what its output has to be divided by. Four
        /// octaves at persistence 0.5 reach 1.875, so skipping this would push most of the field past
        /// the ends of every spline and flatten the world into two altitudes.
        /// </summary>
        static float AmplitudeSum(in FractalSettings fractal)
        {
            float sum = 0f;
            float amplitude = 1f;

            for (int i = 0; i < fractal.Octaves; i++)
            {
                sum += amplitude;
                amplitude *= fractal.Persistence;
            }

            return sum;
        }

        static NoiseSettings Settings(float wavelength, int seed) => new()
        {
            XFrequency = 1f / wavelength,
            YFrequency = 1f / wavelength,
            Amplitude = 1f,
            Seed = seed
        };
    }
}
