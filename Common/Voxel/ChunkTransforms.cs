using System.Numerics;

// COORDINATE CONVENTIONS. Everything here follows from five rules.
//
// 1. ChunkIndex is 2D: (x, z). Chunks tile the ground plane and span the full world height, so
//    there is no vertical chunking and no chunk index for height.
// 2. World Y is height. The type is unbounded; only [WorldMinY, WorldMaxY) holds a chunk.
// 3. A chunk's coordinates pin its BOTTOM-LEFT corner, so it spans [origin, origin + ChunkWidth).
// 4. Voxels index z-major inside a chunk: y*256 + z*16 + x. Chosen so the 2D column formula
//    z*16 + x is exactly the y = 0 slice of it.
// 5. Negative coordinates use real floor division. This DIVERGES from the Rust reference, which
//    shifts and truncates — an approximation that misplaces exact negative multiples of the width,
//    sending every negative chunk's first row and column into its neighbour. All of the
//    reference's own test vectors still pass under real flooring.
//
// The rule that keeps it honest: never compute a block's world position twice. Noise generation
// and meshing both go through the functions below, so an array slot cannot mean different places
// to the two of them. The July 2026 porting bugs were all a second, hand-rolled walk of a chunk
// drifting out of sync — a transpose and a half-chunk offset at once.

namespace Demiurge
{

    public static class ChunkTransforms {

        /// <summary>
        /// A PAIR: FloorDiv picks the chunk, Mod the offset inside it. C#'s bare `/` and `%` break
        /// negative coordinates — -17 % 16 is -1, which indexes outside the chunk.
        /// </summary>
        static int FloorDiv(int a, int b) => (a >= 0 ? a : a - b + 1) / b;
        static int Mod(int a, int b) => ((a % b) + b) % b;

        // ---- Chunk <-> world ----

        /// <summary>Which chunk a world voxel belongs to.</summary>
        public static ChunkIndex ChunkAt(int worldX, int worldZ) =>
            new()
            {
                x = FloorDiv(worldX, ChunkConstants.ChunkWidth),
                z = FloorDiv(worldZ, ChunkConstants.ChunkWidth)
            };

        /// <summary>Which chunk a world position is in. Height is ignored.</summary>
        public static ChunkIndex ChunkAt(Vector3 position)
            => ChunkAt((int)MathF.Floor(position.X), (int)MathF.Floor(position.Z));

        /// <summary>A chunk's origin in world voxel coordinates.</summary>
        public static (int x, int z) ChunkOrigin(ChunkIndex index)
            => (index.x * ChunkConstants.ChunkWidth, index.z * ChunkConstants.ChunkWidth);

        /// <summary>The same origin as a world position, for placing a chunk's entity.</summary>
        public static Vector3 ChunkOriginPosition(ChunkIndex index)
        {
            (int x, int z) = ChunkOrigin(index);
            return new Vector3(x, 0, z);
        }

        /// <summary>World column of one entry in a chunk's 256-entry heightmap.</summary>
        public static Vector2 ColumnWorldPosition(ChunkIndex chunkIndex, int index)
        {
            (int originX, int originZ) = ChunkOrigin(chunkIndex);

            return new Vector2(originX + (index % ChunkConstants.ChunkWidth),
                               originZ + (index / ChunkConstants.ChunkWidth));
        }

        // ---- Voxel indexing ----

        /// <summary>Local voxel coords -> index into the chunk's flat voxel array.</summary>
        public static int LocalVoxelIndex(int x, int y, int z)
            => (y * ChunkConstants.ChunkSize) + (z * ChunkConstants.ChunkWidth) + x;

        /// <summary>Unpack a flat index back into local voxel coords.</summary>
        public static (int x, int y, int z) LocalVoxelCoords(int index)
            => (index % ChunkConstants.ChunkWidth,
                index / ChunkConstants.ChunkSize,
                (index / ChunkConstants.ChunkWidth) % ChunkConstants.ChunkWidth);

        /// <summary>
        /// A world voxel's slot in its OWN chunk's flat array. Caller must range-check world Y
        /// against [WorldMinY, WorldMaxY) first; this doesn't.
        /// </summary>
        public static int WorldVoxelIndex(int worldX, int worldY, int worldZ)
            => LocalVoxelIndex(
                    Mod(worldX, ChunkConstants.ChunkWidth),
                    worldY - ChunkConstants.WorldMinY,
                    Mod(worldZ, ChunkConstants.ChunkWidth));

        /// <summary>Which column a voxel sits in: mod 256 strips the y term, leaving z*16 + x.</summary>
        public static int ColumnIndexOf(int index) => index % ChunkConstants.ChunkSize;

        /// <summary>A local column's slot in a 256-entry column array. The inverse of ColumnIndexOf's range.</summary>
        public static int ColumnIndex(int localX, int localZ) => localZ * ChunkConstants.ChunkWidth + localX;

        // ---- Padded column arrays ----
        //
        // Slope has to be central-differenced, so an edge column needs its neighbour's height — which
        // lives in the next chunk. Rather than looking across chunks, column arrays are generated one
        // wider on every side: local coordinates run [-1, ChunkWidth] instead of [0, ChunkWidth).
        // The noise is a pure function of world position, so the extra ring costs nothing but samples.
        //
        // These live here, beside the unpadded versions, because a padded-vs-unpadded index mixup is
        // exactly the class of bug the "never compute a block's world position twice" rule exists to
        // prevent — the July 2026 transpose and half-chunk offset were both hand-rolled index walks.

        /// <summary>Width of a column array padded by one on each side.</summary>
        public const int PaddedWidth = ChunkConstants.ChunkWidth + 2;

        /// <summary>Entries in a padded column array.</summary>
        public const int PaddedColumns = PaddedWidth * PaddedWidth;

        /// <summary>Padded slot for LOCAL column coords in [-1, ChunkWidth].</summary>
        public static int PaddedColumnIndex(int localX, int localZ)
            => (localZ + 1) * PaddedWidth + (localX + 1);

        /// <summary>World column of one entry in a padded column array.</summary>
        public static Vector2 PaddedColumnWorldPosition(ChunkIndex chunkIndex, int index)
        {
            (int originX, int originZ) = ChunkOrigin(chunkIndex);

            return new Vector2(originX + (index % PaddedWidth) - 1,
                               originZ + (index / PaddedWidth) - 1);
        }

        /// <summary>
        /// Padded slot for the column a VOXEL sits in. The one bridge between voxel indices and padded
        /// column arrays, so generation never open-codes the +1.
        /// </summary>
        public static int PaddedColumnIndexOf(int voxelIndex)
        {
            int column = ColumnIndexOf(voxelIndex);

            return PaddedColumnIndex(column % ChunkConstants.ChunkWidth,
                                     column / ChunkConstants.ChunkWidth);
        }

        /// <summary>A voxel's local height — the other half of the same split.</summary>
        public static int LocalYOf(int index) => index / ChunkConstants.ChunkSize;
    }
}
