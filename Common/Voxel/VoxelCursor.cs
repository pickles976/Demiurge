namespace Demiurge
{
    /// <summary>
    /// A one-chunk memo over <see cref="ChunkMap"/>, for a burst of voxel reads that are all near
    /// each other.
    ///
    /// <see cref="ChunkMap.TryGetVoxel"/> resolves the owning chunk on EVERY call — a
    /// <see cref="ChunkTransforms.ChunkAt"/> plus a ConcurrentDictionary lookup — which is the right
    /// shape for a scattered read and the wrong one for a stencil. One
    /// <see cref="TerrainCollision.TrySample"/> reads 56 voxels that lie inside a 4-voxel cube, so
    /// it resolved the same chunk 56 times; <see cref="TerrainCollision.TryDeepestContact"/> tripled
    /// that. Remembering the last chunk collapses those to one lookup, or a handful when the stencil
    /// straddles a border.
    ///
    /// This is a caching layer and nothing else: it returns exactly what TryGetVoxel returns for
    /// every input, including the out-of-range Y sentinels and the "no data" false that callers must
    /// never read as air. VoxelCursorTests pins that equivalence, which is what lets the collision
    /// maths above it stay untouched and bit-identical.
    ///
    /// Mutable struct — pass it by ref. The remembered chunk reference is held across the burst,
    /// which is no more exposed to a concurrent edit than the same reads going one at a time were;
    /// navigation workers already read the map optimistically by design.
    /// </summary>
    public struct VoxelCursor
    {
        private readonly ChunkMap map;
        private ChunkIndex index;
        private TerrainChunk? chunk;
        private bool resolved;

        public VoxelCursor(ChunkMap map)
        {
            this.map = map;
            index = default;
            chunk = null;
            resolved = false;
        }

        /// <summary>As <see cref="ChunkMap.TryGetVoxel"/>, reusing the previously resolved chunk
        /// whenever this read lands in it.</summary>
        public bool TryGet(int worldX, int worldY, int worldZ, out Voxel voxel)
        {
            voxel = default;

            if (worldY < ChunkConstants.WorldMinY) { voxel = Voxel.OutsideBelow; return true; }
            if (worldY >= ChunkConstants.WorldMaxY) { voxel = Voxel.OutsideAbove; return true; }

            var wanted = ChunkTransforms.ChunkAt(worldX, worldZ);
            if (!resolved || !wanted.Equals(index))
            {
                chunk = map.Get(wanted);
                index = wanted;
                resolved = true;
            }

            // A missing chunk is remembered as missing too: a stencil hanging off the loaded world
            // would otherwise re-miss the dictionary once per corner.
            if (chunk is null) return false;

            voxel = chunk[ChunkTransforms.WorldVoxelIndex(worldX, worldY, worldZ)];
            return true;
        }
    }
}
