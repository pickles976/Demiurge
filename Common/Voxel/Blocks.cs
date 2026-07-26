namespace Demiurge
{
    /// <summary>
    /// Two bytes per voxel.
    ///
    /// <see cref="Density"/> is a QUANTIZED signed distance in voxels: negative is solid, >= 0 is
    /// air, and <see cref="Material"/> is meaningless wherever it's air. Read and write it through
    /// <see cref="Distance"/> rather than touching the raw byte.
    ///
    /// <see cref="Scale"/> 50 gives 0.02-voxel resolution across a range of about +/-2.54 voxels.
    /// Anything deeper clamps, which is harmless: a dual mesher only looks at cells whose corners
    /// disagree in sign, so precision matters near the isosurface and nowhere else.
    ///
    /// Material is derived from the UNQUANTIZED distance at generation time for exactly that reason.
    /// Deriving it from the clamped value would make everything more than 2.54 voxels down the same
    /// material, because that's where the depth information stops existing.
    /// </summary>
    public struct Voxel
    {
        public const float Scale = 50f;
        public const float InverseScale = 1f / Scale;

        /// <summary>-127, not -128, so negating a quantized distance is always representable.</summary>
        public const sbyte Minimum = -127;
        public const sbyte Maximum = 127;

        public sbyte Density;
        public BlockType Material;

        /// <summary>Signed distance in voxels. The setter quantizes and clamps.</summary>
        public float Distance
        {
            get => Density * InverseScale;
            set => Density = Quantize(value);
        }

        public static sbyte Quantize(float distance)
            => (sbyte)Math.Clamp(MathF.Round(distance * Scale), Minimum, Maximum);

        /// <summary>Below the world: solid, so no surface forms and the world stays sealed.</summary>
        public static readonly Voxel OutsideBelow = new() { Density = Minimum, Material = BlockType.BlockType_Stone };

        /// <summary>Above the world: air.</summary>
        public static readonly Voxel OutsideAbove = new() { Density = Maximum, Material = BlockType.BlockType_Air };
    }

    /// <summary>
    /// A dequantized voxel, as the mesher wants it.
    ///
    /// Storage is quantized because storage is what scales with world size; the mesher reads each
    /// sample a dozen or more times, so it gets floats. Converting per read cost 1.4 ms per chunk
    /// re-mesh — more than the rest of the mesher put together — so the conversion happens once,
    /// during <see cref="ChunkMesher.TryFillScratch"/>.
    /// </summary>
    public struct Sample
    {
        public float Distance;
        public BlockType Material;

        public static Sample From(Voxel voxel)
            => new() { Distance = voxel.Distance, Material = voxel.Material };
    }

    /// <summary>
    /// Wire protocol: append only, never reorder or delete. Byte-backed to keep Voxel at two bytes.
    /// </summary>
    public enum BlockType : byte
    {
        BlockType_Air = 0,
        BlockType_Grass,
        BlockType_Dirt,
        BlockType_Stone,
    }
}
