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
        /// <summary>Filled bags, stacked. What a player builds with — and what a shovel can take
        /// back out again, since it is soil in a sack.</summary>
        BlockType_Sandbags,
        /// <summary>Dressed stone. Built rather than generated, and as un-diggable as the rock it
        /// was cut from.</summary>
        BlockType_StoneBricks,

        /// <summary>
        /// Timber — planking, revetment, a plank bridge. A shovel is the wrong tool for it, so it
        /// is not soil; a charge does not care what it is made of, so it blasts exactly as readily
        /// as the dirt beside it.
        /// </summary>
        BlockType_Wood,

        /// <summary>
        /// The gridded floor of the structure editor. Scaffolding rather than terrain: it exists to
        /// be built on and measured against, so it is on neither the soil list nor the blast list —
        /// nothing a player carries can touch it, for the same reason natural rock cannot be dug.
        /// A structure captured off it contains the blocks placed ON the floor, never the floor.
        /// </summary>
        BlockType_Debug,
    }

    /// <summary>
    /// What a block IS, as far as the rules care. Density decides whether a voxel exists; these
    /// answer what it behaves like once it does.
    /// </summary>
    public static class Blocks
    {
        /// <summary>
        /// Whether a shovel can move this material.
        ///
        /// The list is the whole of the soil rule and it lives here rather than inside the edit loop
        /// so that "can I dig it" and "can I build it back" cannot drift apart. Sandbags are on it
        /// deliberately: a player who can stack them and not take them down again can permanently
        /// wall off ground the terrain rules say is diggable. Stone and stone brick are not, which
        /// is what makes masonry the permanent half of building.
        /// </summary>
        public static bool IsSoil(BlockType type) => type
            is BlockType.BlockType_Air
            or BlockType.BlockType_Grass
            or BlockType.BlockType_Dirt
            or BlockType.BlockType_Sandbags;

        /// <summary>
        /// Whether an explosion can take this material out.
        ///
        /// Everything a shovel can move, plus the built materials: a charge brings a brick wall or a
        /// timber revetment down and a spade does not. Natural stone is on neither list, which is
        /// what keeps the shape of the map a fixed thing that players build on and inside rather
        /// than something a few grenades can rewrite.
        ///
        /// Note that the two lists being separate is the whole reason wood needs no rule of its own.
        /// "Cannot be dug, blasts like dirt" is not a third category — it is being off IsSoil and on
        /// this one, which is a position masonry already occupies.
        /// </summary>
        public static bool IsBlastable(BlockType type)
            => IsSoil(type)
            || type is BlockType.BlockType_StoneBricks or BlockType.BlockType_Wood;

        /// <summary>
        /// Whether an edit in this mode may remove this material. The one place the two material
        /// rules are selected between, so a new subtractive mode has to answer the question here
        /// rather than growing another filter inside the edit loop.
        /// </summary>
        public static bool CanRemove(EditMode mode, BlockType material) => mode switch
        {
            EditMode.SubtractSoil => IsSoil(material),
            EditMode.SubtractBlast => IsBlastable(material),
            _ => true,
        };
    }
}
