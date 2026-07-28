using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Triangle winding, per axis. A dual mesher builds each quad from the 4 cells around a
/// sign-changing grid edge, and the three axes use separate offset tables, so one table with the
/// wrong handedness inverts only that axis's faces. Each test below uses a density field whose
/// surface crosses edges along exactly ONE axis, which isolates one table.
/// </summary>
public class SurfaceNetWindingTests
{
    /// <summary>
    /// A 3x3 chunk neighbourhood around (0,0) filled from `field`, so TryFillScratch has every
    /// neighbour it needs and the centre chunk meshes.
    /// </summary>
    static ChunkMap MapWithField(Func<int, int, int, float> field)
    {
        var map = new ChunkMap();

        for (int cx = -1; cx <= 1; cx++)
        {
            for (int cz = -1; cz <= 1; cz++)
            {
                var index = new ChunkIndex { x = cx, z = cz };
                var chunk = new TerrainChunk(index);
                (int originX, int originZ) = ChunkTransforms.ChunkOrigin(index);

                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    var local = ChunkTransforms.LocalVoxelCoords(i);

                    float d = field(originX + local.x,
                                    ChunkConstants.WorldMinY + local.y,
                                    originZ + local.z);

                    var vi = new Voxel { Distance = d };
                    vi.Material = ChunkGenerator.DensityToMaterial(vi.Distance, d);
                    chunk[i] = vi;
                }

                map.Insert(chunk);
            }
        }

        return map;
    }

    static MeshData MeshCentreChunk(Func<int, int, int, float> field)
    {
        var scratch = new Sample[ChunkMesher.ScratchVolume];
        Assert.True(ChunkMesher.TryFillScratch(MapWithField(field), SectionIndex.Of(new ChunkIndex { x = 0, z = 0 }, 0), scratch));
        return ChunkMesher.GenerateMeshFromSurfaceNet(scratch);
    }

    /// <summary>
    /// +1 if every triangle's winding puts Cross(p1-p0, p2-p0) along `outward`, -1 if every
    /// triangle puts it against. Fails if the triangles disagree with each other, which is the
    /// per-axis handedness bug.
    ///
    /// Compares against a KNOWN outward direction rather than against MeshData.Normals: the
    /// vertical sentinels (+/-1000) swamp the gradient at world Y 0 and 127, so computed normals
    /// are unreliable exactly where these synthetic planes reach the world's floor and ceiling.
    /// Harmless in real terrain, where no surface exists that deep.
    /// </summary>
    static int WindingSign(MeshData mesh, Vector3 outward)
    {
        Assert.True(mesh.Indices.Length > 0, "no geometry produced");

        int sign = 0;

        for (int t = 0; t < mesh.Indices.Length; t += 3)
        {
            int i0 = mesh.Indices[t], i1 = mesh.Indices[t + 1], i2 = mesh.Indices[t + 2];

            var geometric = Vector3.Cross(mesh.Positions[i1] - mesh.Positions[i0],
                                          mesh.Positions[i2] - mesh.Positions[i0]);

            float d = Vector3.Dot(geometric, outward);
            if (MathF.Abs(d) < 1e-6f) continue;   // degenerate triangle carries no information

            int s = d > 0f ? 1 : -1;
            if (sign == 0) sign = s;
            else Assert.Equal(sign, s);
        }

        Assert.NotEqual(0, sign);
        return sign;
    }

    // A flat horizontal surface at y = 12.5. Density varies only with y, so only VERTICAL grid
    // edges change sign and only QuadCellOffsets[1] is exercised.
    static int HorizontalWinding() => WindingSign(MeshCentreChunk((x, y, z) => y - 12.5f), Vector3.UnitY);

    // A vertical plane at x = 7.5 — only x edges change sign. QuadCellOffsets[0].
    static int FacingXWinding() => WindingSign(MeshCentreChunk((x, y, z) => x - 7.5f), Vector3.UnitX);

    // A vertical plane at z = 7.5 — only z edges change sign. QuadCellOffsets[2].
    static int FacingZWinding() => WindingSign(MeshCentreChunk((x, y, z) => z - 7.5f), Vector3.UnitZ);

    [Fact] public void HorizontalSurfaceWindsConsistently() => HorizontalWinding();
    [Fact] public void SurfaceFacingXWindsConsistently() => FacingXWinding();
    [Fact] public void SurfaceFacingZWindsConsistently() => FacingZWinding();

    /// <summary>
    /// The three tables must agree, or one axis of every mesh is inside-out. This is the assertion
    /// that catches a reversed row; whether the shared sign is the one Stride wants is a separate,
    /// single question settled by looking at the game once.
    /// </summary>
    [Fact]
    public void AllThreeAxesWindTheSameWay()
    {
        int horizontal = HorizontalWinding();

        Assert.Equal(horizontal, FacingXWinding());
        Assert.Equal(horizontal, FacingZWinding());
    }

    /// <summary>
    /// Pins the shared sign so a future edit to the winding branch or the offset tables can't
    /// silently flip every face. Stride culls back faces with FrontFaceCounterClockwise = false.
    /// </summary>
    [Fact]
    public void WindingMatchesTheOrientationStrideDraws()
    {
        Assert.Equal(ExpectedWindingSign, HorizontalWinding());
    }

    const int ExpectedWindingSign = -1;
}

/// <summary>
/// Whether two adjacent chunks knit together across their shared border.
/// </summary>
public class ChunkSeamTests
{
    static MeshData Mesh(ChunkIndex index, Func<int, int, int, float> field)
    {
        var map = new ChunkMap();
        for (int cx = -2; cx <= 3; cx++)
            for (int cz = -2; cz <= 3; cz++)
            {
                var i = new ChunkIndex { x = cx, z = cz };
                var chunk = new TerrainChunk(i);
                (int ox, int oz) = ChunkTransforms.ChunkOrigin(i);
                for (int v = 0; v < ChunkConstants.ChunkVolume; v++)
                {
                    var l = ChunkTransforms.LocalVoxelCoords(v);
                    float d = field(ox + l.x, ChunkConstants.WorldMinY + l.y, oz + l.z);
                    var vv = new Voxel { Distance = d };
                    vv.Material = ChunkGenerator.DensityToMaterial(vv.Distance, d);
                    chunk[v] = vv;
                }
                map.Insert(chunk);
            }

        var scratch = new Sample[ChunkMesher.ScratchVolume];
        Assert.True(ChunkMesher.TryFillScratch(map, SectionIndex.Of(index, 0), scratch));
        return ChunkMesher.GenerateMeshFromSurfaceNet(scratch);
    }

    static readonly Func<int, int, int, float> FlatAt12Point5 = (x, y, z) => y - 12.5f;

    /// <summary>World-space x range covered by a chunk's triangles.</summary>
    static (float min, float max) WorldXRange(ChunkIndex index)
    {
        var mesh = Mesh(index, FlatAt12Point5);
        (int originX, _) = ChunkTransforms.ChunkOrigin(index);

        float min = float.MaxValue, max = float.MinValue;
        foreach (int i in mesh.Indices)
        {
            float x = originX + mesh.Positions[i].X;
            min = MathF.Min(min, x);
            max = MathF.Max(max, x);
        }
        return (min, max);
    }

    /// <summary>
    /// A chunk's surface must reach half a cell PAST its own first column, because it builds
    /// vertices for the cell layer at local -1. If this comes back as 0.5 the -1 layer is missing
    /// and every border is a one-cell gap.
    /// </summary>
    [Fact]
    public void FlatSurfaceSpansTheCellLayerBeforeTheChunk()
    {
        var (min, max) = WorldXRange(new ChunkIndex { x = 0, z = 0 });

        Assert.Equal(-0.5f, min, 3);
        Assert.Equal(15.5f, max, 3);
    }

    /// <summary>
    /// The load-bearing one: adjacent chunks must meet exactly, with no gap and no overlap.
    /// </summary>
    [Fact]
    public void AdjacentChunksMeetWithoutAGap()
    {
        var left = WorldXRange(new ChunkIndex { x = 0, z = 0 });
        var right = WorldXRange(new ChunkIndex { x = 1, z = 0 });

        Assert.Equal(left.max, right.min, 3);
    }
}
