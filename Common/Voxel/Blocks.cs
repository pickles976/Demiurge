namespace Demiurge
{
    public struct Voxel
    {
        public float Density; // TODO:  quantize signed distance: <0 solid, >=0 air
        public BlockType Material;

        public static readonly Voxel OutsideBelow = new() { Density = -1000f, Material = BlockType.BlockType_Stone };
        public static readonly Voxel OutsideAbove = new() { Density = +1000f, Material = BlockType.BlockType_Air };
    }

    public enum BlockType
    {
        BlockType_Air = 0,
        BlockType_Grass,
        BlockType_Dirt,
        BlockType_Stone,
    }
}
