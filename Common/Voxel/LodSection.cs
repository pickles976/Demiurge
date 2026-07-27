namespace Demiurge
{
    /// <summary>
    /// A meshable box at some level of detail. Level 0 is one 16³ section and is exactly a
    /// <see cref="SectionIndex"/>; each level up doubles the voxel size, so a level-L box covers
    /// 16·2^L world voxels per axis and one chunk column holds 8/2^L of them.
    ///
    /// The mesher does not need to know: it always sees a 16³ grid of cells. What changes is the STRIDE
    /// the scratch buffer is sampled at, and the scale on the entity's transform. Mesh positions come out
    /// in grid units either way, and uniform scale preserves normals, so the same code renders every level.
    /// </summary>
    public readonly record struct LodSection(int X, int Y, int Z, int Level)
    {
        /// <summary>
        /// Coarsest level. 2 means a distant box covers 4×4 chunks and is sampled every 4th voxel;
        /// beyond that the ±2.54-voxel quantization band is so much smaller than a cell that crossings
        /// always interpolate to the midpoint and the surface goes visibly blocky.
        /// </summary>
        public const int MaxLevel = 2;

        /// <summary>World voxels between adjacent grid samples.</summary>
        public int Stride => 1 << Level;

        /// <summary>World voxels this box spans per axis.</summary>
        public int Size => ChunkConstants.SectionHeight * Stride;

        public int OriginX => X * Size;
        public int OriginZ => Z * Size;
        public int OriginY => ChunkConstants.WorldMinY + Y * Size;

        public static int SectionsPerColumn(int level)
            => ChunkConstants.ChunkHeight / (ChunkConstants.SectionHeight << level);

        /// <summary>Level 0 is a plain section, and the two must agree or LOD 0 would double up.</summary>
        public static LodSection Of(SectionIndex section) => new(section.x, section.y, section.z, 0);

        /// <summary>
        /// The chunks this box's mesh READS, apron included. The apron reaches
        /// <see cref="ChunkMesher.MeshDependencyRadius"/> SAMPLES past the box, which at a stride of 4 is
        /// 8 world voxels — half a chunk — so a coarse box depends on more chunks than a fine one.
        /// </summary>
        public (ChunkIndex Min, ChunkIndex Max) ChunkFootprint()
        {
            int reach = ChunkMesher.MeshDependencyRadius * Stride;

            return (ChunkTransforms.ChunkAt(OriginX - reach, OriginZ - reach),
                    ChunkTransforms.ChunkAt(OriginX + Size - 1 + reach, OriginZ + Size - 1 + reach));
        }
    }
}
