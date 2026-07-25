
using System.Numerics;

namespace Demiurge
{

    public class ChunkTransforms {

        // If we pass in a chunk and the index of a point in it's 2D chunk array, we will get a global block coordinate
        public static Vector2 ConvertChunkCoordinatesAndBlockIndexToGlobalBlockCoordinates(ChunkIndex chunkIndex, int index)
        {
            var fine  = (index % ChunkConstants.ChunkWidth, index / ChunkConstants.ChunkWidth);
            var coarse = (chunkIndex.x * ChunkConstants.ChunkWidth, chunkIndex.y * ChunkConstants.ChunkWidth);

            return new Vector2
            {
                X = coarse.Item1 + fine.Item1,
                Y = coarse.Item2 + fine.Item2
            };
        }

        // ---- Voxel indexing: local 3D coords <-> flat array index, within ONE chunk ----

        /// <summary>
        /// Local voxel coords -> index into the chunk's flat voxel array. y*256 + z*16 + x,
        /// chosen so the 2D column formula (z*16 + x) is exactly the y=0 slice of it.
        /// </summary>
        public static int GetVoxelIndexFromLocalBlockCoordinates(int x, int y, int z) => (y * ChunkConstants.ChunkSize) + (z * ChunkConstants.ChunkWidth) + x; // y*256 + z*16 + x

        public static int GetVoxelIndexFromLocalVoxelCoordinates(LocalVoxelCoords coords)
            => GetVoxelIndexFromLocalBlockCoordinates(coords.x, coords.y, coords.z);

        /// <summary>Inverse of the above: unpack a flat index back into local voxel coords.</summary>
        public static LocalVoxelCoords GetLocalVoxelCoordinatesFromVoxelIndex(int index)
        {
            return new LocalVoxelCoords
            {
                x = index % ChunkConstants.ChunkWidth,
                y = index / ChunkConstants.ChunkSize,
                z = (index / ChunkConstants.ChunkWidth) % ChunkConstants.ChunkWidth,
            };
        }

        /// <summary>
        /// Which COLUMN a voxel sits in, as an index into a 256-entry heightmap. Falls straight
        /// out of the layout: taking the index mod 256 strips the y term and leaves z*16 + x.
        /// </summary>
        public static int GetColumnIndexFromVoxelIndex(int index) => index % ChunkConstants.ChunkSize;

        /// <summary>A voxel's local height. The other half of the same split: index / 256.</summary>
        public static int GetLocalYFromVoxelIndex(int index) => index / ChunkConstants.ChunkSize;

        /// <summary>
        /// World Y is unbounded but a chunk only spans [0, ChunkHeight), so anything derived from
        /// a world position has to be range-checked before it indexes the array.
        /// </summary>
        public static bool IsInsideChunk(LocalVoxelCoords coords)
            => coords.x >= 0 && coords.x < ChunkConstants.ChunkWidth
            && coords.z >= 0 && coords.z < ChunkConstants.ChunkWidth
            && coords.y >= 0 && coords.y < ChunkConstants.ChunkHeight;

        /// <summary>
        /// World-space position of a voxel's bottom-left-front CORNER — the block occupies
        /// [position, position + TileSize) on each axis. Add half a tile to centre a cube mesh.
        /// </summary>
        public static Vector3 ConvertChunkAndVoxelIndexToGlobalBlockPosition(ChunkIndex chunkIndex, int voxelIndex)
        {
            LocalVoxelCoords local = GetLocalVoxelCoordinatesFromVoxelIndex(voxelIndex);
            Vector3 origin = ConvertChunkCoordinatesToVector3(chunkIndex);

            // origin.Y is 0 and there is no vertical chunking, so local y IS world y.
            return new Vector3(origin.X + local.x, local.y, origin.Z + local.z);
        }

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

        /// <summary>
        /// Chunk coordinates pin to the BOTTOM-LEFT corner (see README), so a chunk spans
        /// [origin, origin + ChunkWidth) — NOT origin +/- half a width. Nothing in the noise path
        /// uses this any more; sample per block index instead, so generation and rendering can't
        /// disagree about where a chunk sits.
        /// </summary>
        public static ChunkCorners GetChunkCornersInWorldSpace(ChunkIndex index)
        {
            Vector3 origin = ConvertChunkCoordinatesToVector3(index);
            return new ChunkCorners
            {
                xMinus = origin.X,
                xPlus = origin.X + ChunkConstants.ChunkWidth,
                zMinus = origin.Z,
                zPlus = origin.Z + ChunkConstants.ChunkWidth,
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

        /// <summary>
        /// Chunk space is the ground plane: X stays X, and Z becomes the SECOND axis. Projecting
        /// through Vector2 first keeps that explicit — reading position.Y here (height) instead of
        /// position.Z is the mistake this split exists to prevent.
        /// </summary>
        public static ChunkIndex ConvertVector3ToChunkCoordinates(Vector3 position)
            => ConvertVector2ToChunkCoordinates(new Vector2(position.X, position.Z));

        /// <summary>
        /// Mapping a coordinate to the cell containing it is floor division, so do floor division.
        ///
        /// DIVERGES FROM THE RUST REFERENCE, deliberately. It shifts negatives down a whole chunk
        /// and then truncates toward zero, which approximates floor but is wrong at exact negative
        /// multiples of the width: world x = -16 is the FIRST column of chunk -1, yet that trick
        /// reports chunk -2. Every negative chunk's first row and column resolved into its
        /// neighbour. All of the reference's own test vectors still hold under real flooring — its
        /// tests just never probe an exact boundary.
        /// </summary>
        public static ChunkIndex ConvertVector2ToChunkCoordinates(Vector2 position)
        {
            return new ChunkIndex
            {
                x = (int)MathF.Floor(position.X / ChunkConstants.ChunkWidth),
                y = (int)MathF.Floor(position.Y / ChunkConstants.ChunkWidth),
            };
        }

        public static BlockCoords ConvertVector3ToLocalBlockCoordinates(Vector3 position)
        {
            ChunkIndex index = ConvertVector3ToChunkCoordinates(position);
            Vector3 origin = ConvertChunkCoordinatesToVector3(index);
            Vector3 delta = position - origin;

            // delta.Z, not delta.Y — BlockCoords.y is the chunk's second GROUND axis, not height.
            return new BlockCoords {
                x = (ushort)MathF.Floor(delta.X % ChunkConstants.ChunkWidth),
                y = (ushort)MathF.Floor(delta.Z % ChunkConstants.ChunkWidth)
            };

        }

        /// <summary>
        /// The full 3D version: a world position -> the voxel containing it, local to its chunk.
        /// Height comes straight from world Y since there is no vertical chunking. The result can
        /// fall outside the chunk vertically — check <see cref="IsInsideChunk"/> before indexing.
        /// </summary>
        public static LocalVoxelCoords ConvertVector3ToLocalVoxelCoordinates(Vector3 position)
        {
            BlockCoords ground = ConvertVector3ToLocalBlockCoordinates(position);

            return new LocalVoxelCoords
            {
                x = ground.x,
                y = (int)MathF.Floor(position.Y),
                z = ground.y,   // BlockCoords.y is the ground axis, i.e. world Z
            };
        }
    }
}