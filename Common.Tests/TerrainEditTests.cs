using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Additive and subtractive edits. Every case runs on a flat synthetic field so the expected
/// surface positions are exact numbers rather than "looks about right".
/// </summary>
public class TerrainEditTests
{
    const int GroundHeight = 12;   // solid below y = 12.5, air above

    /// <summary>A chunk whose density is a flat surface at y = 12.5.</summary>
    static TerrainChunk FlatChunk(ChunkIndex index)
    {
        var chunk = new TerrainChunk(index);

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
        {
            float d = ChunkTransforms.LocalYOf(i) + ChunkConstants.WorldMinY - (GroundHeight + 0.5f);
            var vi = new Voxel { Distance = d };
            vi.Material = ChunkGenerator.DensityToMaterial(vi.Distance, d);
            chunk[i] = vi;
        }

        return chunk;
    }

    static float DensityAt(TerrainChunk chunk, int localX, int worldY, int localZ)
        => chunk[ChunkTransforms.LocalVoxelIndex(localX, worldY - ChunkConstants.WorldMinY, localZ)].Distance;

    // ---- The box distance function itself ----

    [Theory]
    [InlineData(0f, 0f, 0f, -1f)]      // centre of a 1x1x1 half-extent box: 1 from every face
    [InlineData(0.5f, 0f, 0f, -0.5f)]  // half way to the +x face
    [InlineData(1f, 0f, 0f, 0f)]       // on the face
    [InlineData(3f, 0f, 0f, 2f)]       // 2 outside along one axis
    public void BoxDistanceIsSignedAndExact(float px, float py, float pz, float expected)
    {
        float d = TerrainEdits.BoxDistance(new Vector3(px, py, pz), Vector3.One);
        Assert.Equal(expected, d, 4);
    }

    /// <summary>Outside a corner it must be the true diagonal distance, not the Chebyshev one.</summary>
    [Fact]
    public void BoxDistanceIsEuclideanOutsideCorners()
    {
        float d = TerrainEdits.BoxDistance(new Vector3(4f, 4f, 0f), Vector3.One);
        Assert.Equal(MathF.Sqrt(3f * 3f + 3f * 3f), d, 4);
    }

    // ---- Adding ----

    /// <summary>
    /// The wall is 2 wide centred on local x = 8, so its faces are at x = 7 and x = 9: those
    /// columns must read exactly 0 and the interior must be negative.
    /// </summary>
    [Fact]
    public void WallFacesLandOnExactVoxelBoundaries()
    {
        var chunk = FlatChunk(new ChunkIndex { x = 0, z = 0 });
        TerrainEdits.AddWall(chunk);

        int wellAboveGround = GroundHeight + 10;

        Assert.Equal(0f, DensityAt(chunk, 7, wellAboveGround, 8), 4);
        Assert.Equal(0f, DensityAt(chunk, 9, wellAboveGround, 8), 4);
        Assert.True(DensityAt(chunk, 8, wellAboveGround, 8) < 0f, "wall interior should be solid");
        Assert.True(DensityAt(chunk, 5, wellAboveGround, 8) > 0f, "outside the wall should be air");
    }

    /// <summary>The wall stops at 32 above WorldMinY, so the voxel above that is still air.</summary>
    [Fact]
    public void WallStopsAtItsStatedHeight()
    {
        var chunk = FlatChunk(new ChunkIndex { x = 0, z = 0 });
        TerrainEdits.AddWall(chunk);

        Assert.True(DensityAt(chunk, 8, ChunkConstants.WorldMinY + 31, 8) < 0f, "31 should be inside");
        Assert.Equal(0f, DensityAt(chunk, 8, ChunkConstants.WorldMinY + 32, 8), 4);
        Assert.True(DensityAt(chunk, 8, ChunkConstants.WorldMinY + 33, 8) > 0f, "33 should be air");
    }

    /// <summary>Newly solid voxels need a material, or the density/material invariant breaks.</summary>
    [Fact]
    public void AddingLabelsNewlySolidVoxelsAndLeavesOldRockAlone()
    {
        var chunk = FlatChunk(new ChunkIndex { x = 0, z = 0 });

        // Stand in for a vein: rock that is already solid and carries a material of its own.
        int buried = GroundHeight - 5;
        int i = ChunkTransforms.LocalVoxelIndex(8, buried - ChunkConstants.WorldMinY, 8);
        { var v = chunk[i]; v.Material = BlockType.BlockType_Grass; chunk[i] = v; }

        TerrainEdits.AddWall(chunk, BlockType.BlockType_Stone);

        int inWall = GroundHeight + 10;
        int j = ChunkTransforms.LocalVoxelIndex(8, inWall - ChunkConstants.WorldMinY, 8);
        Assert.Equal(BlockType.BlockType_Stone, chunk[j].Material);

        // Already solid and still solid: the edit must not repaint it.
        Assert.Equal(BlockType.BlockType_Grass, chunk[i].Material);
    }

    // ---- Subtracting ----

    [Fact]
    public void TrenchCarvesAirBelowGroundAndLeavesRockBeside()
    {
        var chunk = FlatChunk(new ChunkIndex { x = 0, z = 1 });
        TerrainEdits.CarveTrench(chunk);

        int buried = GroundHeight - 5;

        Assert.True(DensityAt(chunk, 8, buried, 8) > 0f, "trench interior should be air");
        Assert.True(DensityAt(chunk, 5, buried, 8) < 0f, "rock beside the trench should remain");
    }

    [Fact]
    public void TrenchTurnsCarvedVoxelsToAir()
    {
        var chunk = FlatChunk(new ChunkIndex { x = 0, z = 1 });
        TerrainEdits.CarveTrench(chunk);

        int i = ChunkTransforms.LocalVoxelIndex(8, (GroundHeight - 5) - ChunkConstants.WorldMinY, 8);
        Assert.Equal(BlockType.BlockType_Air, chunk[i].Material);
    }

    /// <summary>
    /// The reason the operator has to be applied past the shape's bounds. A voxel just outside the
    /// trench must be rewritten to its distance from the new trench wall, not left holding its
    /// distance from the terrain surface.
    ///
    /// Probed 1 voxel down rather than 5: density quantizes to about +/-2.54 voxels, so anything
    /// deeper clamps and both readings would come back as the same clamped value. Harmless for
    /// meshing, which only cares near the isosurface, but it means depth assertions have to stay
    /// inside the representable range.
    /// </summary>
    [Fact]
    public void SubtractingRewritesDensityOutsideTheBox()
    {
        var index = new ChunkIndex { x = 0, z = 1 };
        int buried = GroundHeight - 1;

        float before = DensityAt(FlatChunk(index), 6, buried, 8);

        var chunk = FlatChunk(index);
        TerrainEdits.CarveTrench(chunk);
        float after = DensityAt(chunk, 6, buried, 8);

        Assert.Equal(-1.5f, before, 4);   // 1.5 below the terrain surface
        Assert.Equal(-1f, after, 4);      // now 1 from the trench wall at x = 7
    }

    /// <summary>
    /// The rewritten region is the box plus Margin and no more. The wall is 2 wide at local x = 8,
    /// so x = 5..11 changes and the columns beyond that must be bit-identical to the untouched
    /// field — which is what keeps an edit from dirtying the ±X neighbours as well.
    /// </summary>
    [Fact]
    public void EditsOnlyReachMarginPastTheBox()
    {
        var index = new ChunkIndex { x = 0, z = 0 };

        var untouched = FlatChunk(index);
        var edited = FlatChunk(index);
        TerrainEdits.AddWall(edited);

        foreach (int x in new[] { 0, 4, 12, 15 })
        {
            for (int y = ChunkConstants.WorldMinY; y < ChunkConstants.WorldMaxY; y++)
                Assert.Equal(DensityAt(untouched, x, y, 8), DensityAt(edited, x, y, 8), 5);
        }

        // ...and the column at the edge of the margin genuinely did change.
        Assert.NotEqual(DensityAt(untouched, 5, GroundHeight + 10, 8),
                        DensityAt(edited, 5, GroundHeight + 10, 8), 5);
    }
}

public class SdfCleanupTests
{
    const int SampleY = 20;

    static ChunkMap Air()
        => SyntheticTerrain.Build((x, y, z) => 4f);

    static void SetSolid(ChunkMap map, int worldX, int worldY, int worldZ, float distance = -0.1f)
    {
        var chunk = map.Get(ChunkTransforms.ChunkAt(worldX, worldZ))!;
        int index = ChunkTransforms.WorldVoxelIndex(worldX, worldY, worldZ);
        var voxel = chunk[index];
        voxel.Distance = distance;
        voxel.Material = BlockType.BlockType_Stone;
        chunk[index] = voxel;
    }

    static Voxel At(ChunkMap map, int worldX, int worldY, int worldZ)
    {
        Assert.True(map.TryGetVoxel(worldX, worldY, worldZ, out var voxel));
        return voxel;
    }

    [Fact]
    public void SphericalSubtractRemovesTinyEnclosedSolidFragment()
    {
        var map = Air();
        SetSolid(map, 2, SampleY, 0);

        TerrainEdits.ApplyBox(map, new Vector3(0, SampleY, 0), Digging.Bite,
            EditMode.Subtract, BlockType.BlockType_Air, EditShape.Sphere);

        var voxel = At(map, 2, SampleY, 0);
        Assert.True(voxel.Distance > 0f);
        Assert.Equal(BlockType.BlockType_Air, voxel.Material);
    }

    [Fact]
    public void ComponentTouchingCleanupBoundaryIsProtected()
    {
        var map = Air();
        SetSolid(map, 0, SampleY, 2);
        SetSolid(map, 1, SampleY, 2);

        int removed = TerrainEdits.CullTinySolidComponents(
            map, (0, SampleY - 2, 0), (4, SampleY + 2, 4));

        Assert.Equal(0, removed);
        Assert.True(At(map, 0, SampleY, 2).Distance < 0f);
        Assert.True(At(map, 1, SampleY, 2).Distance < 0f);
    }

    [Fact]
    public void ComponentAboveTinyThresholdIsProtected()
    {
        var map = Air();
        for (int x = 1; x <= 5; x++) SetSolid(map, x, SampleY, 2);

        int removed = TerrainEdits.CullTinySolidComponents(
            map, (0, SampleY - 2, 0), (6, SampleY + 2, 4));

        Assert.Equal(0, removed);
        for (int x = 1; x <= 5; x++) Assert.True(At(map, x, SampleY, 2).Distance < 0f);
    }

    [Fact]
    public void DeepSolidSampleIsProtected()
    {
        var map = Air();
        SetSolid(map, 2, SampleY, 2, distance: -0.8f);

        int removed = TerrainEdits.CullTinySolidComponents(
            map, (0, SampleY - 2, 0), (4, SampleY + 2, 4));

        Assert.Equal(0, removed);
        Assert.Equal(-0.8f, At(map, 2, SampleY, 2).Distance, 2);
    }

    [Fact]
    public void TinyComponentIsRemovedAcrossChunkBoundary()
    {
        var map = Air();
        SetSolid(map, 15, SampleY, 0);
        SetSolid(map, 16, SampleY, 0);

        int removed = TerrainEdits.CullTinySolidComponents(
            map, (13, SampleY - 2, -2), (19, SampleY + 2, 2));

        Assert.Equal(2, removed);
        Assert.True(At(map, 15, SampleY, 0).Distance > 0f);
        Assert.True(At(map, 16, SampleY, 0).Distance > 0f);
    }

    [Fact]
    public void CleanupEliminatesTheFloatingSurfaceNetMesh()
    {
        var map = Air();
        SetSolid(map, 2, SampleY, 2);
        int sectionY = (SampleY - ChunkConstants.WorldMinY) / ChunkConstants.SectionHeight;
        var section = SectionIndex.Of(new ChunkIndex { x = 0, z = 0 }, sectionY);
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        Assert.True(ChunkMesher.TryFillScratch(map, section, scratch));
        Assert.NotEmpty(ChunkMesher.GenerateMeshFromSurfaceNet(scratch).Indices);

        int removed = TerrainEdits.CullTinySolidComponents(
            map, (0, SampleY - 2, 0), (4, SampleY + 2, 4));

        Assert.Equal(1, removed);
        Assert.True(ChunkMesher.TryFillScratch(map, section, scratch));
        Assert.Empty(ChunkMesher.GenerateMeshFromSurfaceNet(scratch).Indices);
    }
}

/// <summary>
/// Indices are grouped per material so each can draw with its own texture. These pin the grouping,
/// not the appearance.
/// </summary>
public class SubmeshTests
{
    const int GroundHeight = 12;

    static MeshData MeshWith(Action<TerrainChunk>? edit)
    {
        var map = new ChunkMap();

        for (int cx = -2; cx <= 2; cx++)
            for (int cz = -2; cz <= 2; cz++)
            {
                var index = new ChunkIndex { x = cx, z = cz };
                var chunk = new TerrainChunk(index);

                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                {
                    float d = ChunkTransforms.LocalYOf(i) + ChunkConstants.WorldMinY - (GroundHeight + 0.5f);
                    var vi = new Voxel { Distance = d };
                    vi.Material = ChunkGenerator.DensityToMaterial(vi.Distance, d);
                    chunk[i] = vi;
                }

                if (cx == 0 && cz == 0) edit?.Invoke(chunk);
                map.Insert(chunk);
            }

        var scratch = new Sample[ChunkMesher.ScratchVolume];
        Assert.True(ChunkMesher.TryFillScratch(map, SectionIndex.Of(new ChunkIndex { x = 0, z = 0 }, 0), scratch));
        return ChunkMesher.GenerateMeshFromSurfaceNet(scratch);
    }

    /// <summary>Every index belongs to exactly one submesh, and the ranges are contiguous from 0.</summary>
    [Fact]
    public void SubmeshesPartitionTheIndexBuffer()
    {
        var mesh = MeshWith(c => TerrainEdits.AddWall(c));

        int expectedStart = 0;
        foreach (var submesh in mesh.Submeshes)
        {
            Assert.Equal(expectedStart, submesh.Start);
            Assert.True(submesh.Count > 0, "an empty submesh should not be emitted");
            Assert.Equal(0, submesh.Count % 3);      // whole triangles
            expectedStart += submesh.Count;
        }

        Assert.Equal(mesh.Indices.Length, expectedStart);
    }

    /// <summary>
    /// Flat terrain's surface sits half a voxel deep, which DensityToMaterial labels Grass — so the
    /// only quads are Grass ones. Deeper stone has no sign change and therefore no geometry.
    /// </summary>
    [Fact]
    public void PlainTerrainProducesOnlyItsSurfaceMaterial()
    {
        var mesh = MeshWith(null);

        Assert.Equal([BlockType.BlockType_Grass], mesh.Submeshes.Select(s => s.Material));
    }

    /// <summary>A stone wall must contribute its own submesh, which is what gets the stone texture.</summary>
    [Fact]
    public void StoneWallGetsItsOwnSubmesh()
    {
        var mesh = MeshWith(c => TerrainEdits.AddWall(c, BlockType.BlockType_Stone));

        var materials = mesh.Submeshes.Select(s => s.Material).ToList();

        Assert.Contains(BlockType.BlockType_Stone, materials);
        Assert.Contains(BlockType.BlockType_Grass, materials);
        Assert.DoesNotContain(BlockType.BlockType_Air, materials);
    }
}
