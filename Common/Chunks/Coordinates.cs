
using System.Numerics;

namespace Demiurge
{

    public class ChunkTransforms {


        public static List<ChunkIndex> GetIndicesFromCenterAndDistance(Vector3 center, float distance)
        {
            int xMinus = ConvertVector3ToChunkCoordinates(center - Vector3.UnitX * distance).x;
            int xPlus = ConvertVector3ToChunkCoordinates(center + Vector3.UnitX * distance).x;

            int zMinus = ConvertVector3ToChunkCoordinates(center - Vector3.UnitZ * distance).y;
            int zPlus = ConvertVector3ToChunkCoordinates(center + Vector3.UnitZ * distance).y;

            // Uses LINQ, Enumerable
            return (from x in Enumerable.Range(xMinus, Math.Max(0, xPlus - xMinus))
                    from z in Enumerable.Range(zMinus, Math.Max(0, zPlus - zMinus))
                    select new ChunkIndex {x= x, y= z})
                    .ToList();

        }

        public static ChunkCorners GetChunkCornersInWorldSpace(ChunkIndex index)
        {
            int extent = ChunkConstants.ChunkWidth / 2;
            Vector3 center = ConvertChunkCoordinatesToVector3(index);
            return new ChunkCorners
            {
                xPlus = center.X + extent,
                xMinus = center.X - extent,
                zPlus = center.Z + extent,
                zMinus = center.Z - extent,
            };
        }

        public static int ConvertLocalBlockCoordsToBlockIndex(BlockCoords coords)
        {
            return (coords.y * ChunkConstants.ChunkWidth) + (coords.x % ChunkConstants.ChunkWidth);
        }

        public static Vector3 ConvertChunkCoordinatesToVector3(ChunkIndex index)
        {
            return new Vector3
            {
                X = index.x * ChunkConstants.ChunkWidth,
                Y = 0,
                Z = index.y * ChunkConstants.ChunkWidth
            };
        }

        public static ChunkIndex ConvertVector3ToChunkCoordinates(Vector3 position)
        {
            var x = position.X;
            var y = position.Y;

            if (x < 0.0)
            {
                x -= 16.0f;
            }

            if (y < 0.0)
            {
                y -= 16.0f;
            }

            return new ChunkIndex
            {
                x=(int)x / ChunkConstants.ChunkWidth,
                y=(int)y / ChunkConstants.ChunkWidth,
            };
        }

        public static BlockCoords ConvertVector3ToLocalBlockCoordinates(Vector3 position)
        {
            ChunkIndex index = ConvertVector3ToChunkCoordinates(position);
            Vector3 origin = ConvertChunkCoordinatesToVector3(index);
            Vector3 delta = position - origin;

            return new BlockCoords {
                x = (ushort)MathF.Floor(delta.X % ChunkConstants.ChunkWidth),
                y = (ushort)MathF.Floor(delta.Y % ChunkConstants.ChunkWidth)
            };

        }
    }
}