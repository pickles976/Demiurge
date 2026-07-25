using NoiseDotNet;
using System.Numerics;

namespace Demiurge
{


    public class NoiseGen
    {
        private static NoiseSettings settings = new NoiseSettings
        {
            XFrequency = 0.1f,
            YFrequency = 0.1f,
            Amplitude = 1.0f,
            Seed = 100
        };


        /// <summary>
        /// Height for every block in a chunk, indexed exactly like <see cref="TerrainChunk.tiles"/>.
        /// </summary>
        public static float[] GenerateNoiseForChunk(ChunkIndex index)
        {
            int totalPoints = ChunkConstants.ChunkSize;
            float[] output = new float[totalPoints];

            float[] xCoords = new float[totalPoints];
            float[] yCoords = new float[totalPoints];

            for (int i = 0; i < totalPoints; i++)
            {
                Vector2 position = ChunkTransforms.ConvertChunkCoordinatesAndBlockIndexToGlobalBlockCoordinates(index, i);
                xCoords[i] = position.X;
                yCoords[i] = position.Y;   // world Z; NoiseSettings applies the frequency scaling
            }

            Noise.GradientNoise2D(xCoords: xCoords, yCoords: yCoords, output: output, settings);
            return output;
        }
    }

}
