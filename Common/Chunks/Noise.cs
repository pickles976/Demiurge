using NoiseDotNet;

namespace Demiurge
{


    public class NoiseGen {
        private static NoiseSettings settings = new ( xFreq: 0.1f, yFreq: 0.1f, seed: 100);


        public static float[] GenerateNoiseForChunk(ChunkIndex index)
        {
            int totalPoints = ChunkConstants.ChunkSize;
            float[] output = new float[totalPoints];

            ChunkCorners corners = ChunkTransforms.GetChunkCornersInWorldSpace(index);

            // Assuming integer coordinates and square chunk
            int n = (int)(corners.xPlus - corners.xMinus); // or use a precomputed chunk size
            int m = (int)(corners.zPlus - corners.zMinus);
            // If the chunk is N x N, then n == m and totalPoints == n * m.

            float[] xCoords = new float[totalPoints];
            float[] yCoords = new float[totalPoints];

            int idx = 0;
            for (int i = 0; i < n; i++)
            {
                float x = corners.xMinus + i;
                for (int j = 0; j < m; j++)
                {
                    float z = corners.zMinus + j;
                    xCoords[idx] = x;
                    yCoords[idx] = z;
                    idx++;
                }
            }

            Noise.GradientNoise2D(xCoords: xCoords, yCoords: yCoords, output: output, settings);
            return output;
        }
    }

}