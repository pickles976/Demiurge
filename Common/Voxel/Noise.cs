using NoiseDotNet;
using System.Numerics;

namespace Demiurge
{


    public class NoiseGen
    {
        private static NoiseSettings settings = new NoiseSettings
        {
            XFrequency = 0.02f,
            YFrequency = 0.02f,
            Amplitude = 1.0f,
            Seed = 100
        };


        /// <summary>
        /// Height for every block in a chunk, indexed exactly like a chunk's 256-entry column array.
        /// </summary>
        public static float[] GenerateNoiseForChunk(ChunkIndex index)
        {
            int totalPoints = ChunkConstants.ChunkSize;
            float[] output = new float[totalPoints];

            float[] xCoords = new float[totalPoints];
            float[] yCoords = new float[totalPoints];

            for (int i = 0; i < totalPoints; i++)
            {
                Vector2 position = ChunkTransforms.ColumnWorldPosition(index, i);
                xCoords[i] = position.X;
                yCoords[i] = position.Y;   // world Z; NoiseSettings applies the frequency scaling
            }

            Noise.GradientNoise2D(xCoords: xCoords, yCoords: yCoords, output: output, settings);
            return output;
        }
    }

}
