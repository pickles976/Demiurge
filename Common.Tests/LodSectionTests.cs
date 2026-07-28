using System.Numerics;

namespace Demiurge.Tests;

/// <summary>
/// Level-of-detail geometry and downsampling. The arithmetic here decides where coarse terrain is DRAWN,
/// so an error is a landscape offset from the one you can walk on rather than an exception.
/// </summary>
public class LodSectionTests
{
    [Fact]
    public void LevelZeroIsExactlyASection()
    {
        var section = new SectionIndex(3, 5, -7);
        var box = LodSection.Of(section);

        Assert.Equal(0, box.Level);
        Assert.Equal(1, box.Stride);
        Assert.Equal(ChunkConstants.SectionHeight, box.Size);

        (int originX, int originZ) = ChunkTransforms.ChunkOrigin(section.Chunk);
        Assert.Equal(originX, box.OriginX);
        Assert.Equal(originZ, box.OriginZ);
        Assert.Equal(section.BaseY, box.OriginY);
    }

    [Theory]
    [InlineData(0, 16, 8)]
    [InlineData(1, 32, 4)]
    [InlineData(2, 64, 2)]
    public void EachLevelDoublesTheBoxAndHalvesTheColumn(int level, int size, int perColumn)
    {
        Assert.Equal(size, new LodSection(0, 0, 0, level).Size);
        Assert.Equal(perColumn, LodSection.SectionsPerColumn(level));

        // The column must tile the world height exactly, or terrain is clipped or duplicated.
        Assert.Equal(ChunkConstants.ChunkHeight, size * perColumn);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BoxesTileTheWorldWithoutGapOrOverlap(int level)
    {
        var box = new LodSection(2, 1, -3, level);
        var next = new LodSection(3, 2, -2, level);

        Assert.Equal(box.OriginX + box.Size, next.OriginX);
        Assert.Equal(box.OriginY + box.Size, next.OriginY);
        Assert.Equal(box.OriginZ + box.Size, next.OriginZ);
    }

    /// <summary>
    /// A coarse box's apron reaches further in world voxels than a fine one's, so its chunk dependency
    /// grows with level. Getting this wrong means meshing against a chunk that is still being written.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FootprintCoversTheBoxAndItsApron(int level)
    {
        var box = new LodSection(0, 0, 0, level);
        var (min, max) = box.ChunkFootprint();

        int reach = ChunkMesher.MeshDependencyRadius * box.Stride;

        Assert.Equal(ChunkTransforms.ChunkAt(box.OriginX - reach, box.OriginZ - reach), min);
        Assert.Equal(ChunkTransforms.ChunkAt(box.OriginX + box.Size - 1 + reach, box.OriginZ + box.Size - 1 + reach), max);

        // And it must at least contain the box's own chunks.
        Assert.True(min.x <= ChunkTransforms.ChunkAt(box.OriginX, box.OriginZ).x);
        Assert.True(max.x >= ChunkTransforms.ChunkAt(box.OriginX + box.Size - 1, box.OriginZ).x);
    }

    // ---- Downsampling ----

    /// <summary>
    /// The whole point of a level of detail: the coarse surface has to sit where the fine one did. Flat
    /// ground makes that exact — every level should put the surface at the same world height.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void CoarseMeshSitsAtTheSameHeightAsFine(int level)
    {
        const float Surface = 40.5f;

        var map = SyntheticTerrain.Build((x, y, z) => y - Surface, chunkRadius: 4);
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        var box = new LodSection(0, LevelContaining(Surface, level), 0, level);

        Assert.True(ChunkMesher.TryFillScratch(map, box, scratch), "flat ground should always fill");

        var mesh = ChunkMesher.GenerateMeshDualContouring(scratch);
        Assert.NotEmpty(mesh.Positions);

        // Positions are in cell units; the transform scales by Stride and offsets by the origin.
        foreach (var position in mesh.Positions)
        {
            float worldY = box.OriginY + position.Y * box.Stride;
            Assert.InRange(worldY, Surface - box.Stride, Surface + box.Stride);
        }
    }

    [Fact]
    public void DefaultMesherUsesSurfaceNetsPlacement()
    {
        var centre = new Vector3(8f, 40f, 8f);
        var map = SyntheticTerrain.Build(
            (x, y, z) => Vector3.Distance(new Vector3(x, y, z), centre) - 6f,
            chunkRadius: 2);
        var scratch = new Sample[ChunkMesher.ScratchVolume];
        var section = new LodSection(0, LevelContaining(centre.Y, 0), 0, 0);

        Assert.True(ChunkMesher.TryFillScratch(map, section, scratch));

        var selected = ChunkMesher.GenerateMesh(scratch);
        var dual = ChunkMesher.GenerateMeshDualContouring(scratch);
        var surfaceNets = ChunkMesher.GenerateMeshFromSurfaceNet(scratch);

        Assert.Equal(surfaceNets.Positions, selected.Positions);
        Assert.Equal(surfaceNets.Indices, selected.Indices);
        Assert.Contains(
            selected.Positions.Zip(dual.Positions),
            pair => Vector3.DistanceSquared(pair.First, pair.Second) > 1e-6f);
    }

    /// <summary>Material must survive downsampling as a real block type, not average into nonsense or air.</summary>
    [Fact]
    public void CoarseSamplesKeepANonAirMaterial()
    {
        var map = SyntheticTerrain.Build((x, y, z) => y - 40.5f, chunkRadius: 4);
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        var box = new LodSection(0, 0, 0, 2);
        Assert.True(ChunkMesher.TryFillScratch(map, box, scratch));

        // Deep below the surface every sample is solid, so none may vote air.
        bool anySolid = false;
        foreach (var sample in scratch)
        {
            if (sample.Distance >= 0f) continue;

            anySolid = true;
            Assert.NotEqual(BlockType.BlockType_Air, sample.Material);
        }

        Assert.True(anySolid, "expected some solid samples below the surface");
    }

    /// <summary>
    /// The banding regression. A column is one voxel of grass over about three of dirt, so any coarse
    /// block holding the surface contains MORE DIRT THAN GRASS — a most-common vote returns dirt, and
    /// whether it does depends on where the surface falls inside the block, so the error appears as
    /// stripes aligned to the LOD grid rather than as uniform wrongness.
    ///
    /// The surface height here is chosen so the block spanning y 40..43 holds two dirt voxels and one
    /// grass one: the exact case a vote gets wrong.
    /// </summary>
    [Fact]
    public void CoarseSurfaceKeepsTheMaterialYouCanSee()
    {
        const float Surface = 42.5f;      // grass at 42, dirt at 41 and 40

        var map = SyntheticTerrain.Build((x, y, z) => y - Surface, chunkRadius: 4);

        // Sanity: the fine field really does band the way the vote would trip over.
        Assert.Equal(BlockType.BlockType_Grass, MaterialAt(map, 42));
        Assert.Equal(BlockType.BlockType_Dirt, MaterialAt(map, 41));
        Assert.Equal(BlockType.BlockType_Dirt, MaterialAt(map, 40));

        var scratch = new Sample[ChunkMesher.ScratchVolume];
        var box = new LodSection(0, LevelContaining(Surface, 2), 0, 2);

        Assert.True(ChunkMesher.TryFillScratch(map, box, scratch));

        // Find the shallowest solid coarse sample — the one the surface passes through.
        var surfaceMaterial = BlockType.BlockType_Air;
        float shallowest = float.NegativeInfinity;

        foreach (var sample in scratch)
        {
            if (sample.Distance >= 0f || sample.Distance <= shallowest) continue;

            shallowest = sample.Distance;
            surfaceMaterial = sample.Material;
        }

        Assert.Equal(BlockType.BlockType_Grass, surfaceMaterial);
    }

    /// <summary>
    /// The banding, at the grid position that actually produces it: a surface just ABOVE a coarse block
    /// boundary. The block below is entirely solid and its shallowest voxel is already more than a voxel
    /// deep, so it reports Dirt — and because the block containing the surface averages to air, that dirt
    /// block is the solid end of the crossing edge and the mesher paints the quad with it.
    /// </summary>
    [Theory]
    [InlineData(40.1f)]     // surface barely above the y=40 block boundary
    [InlineData(40.4f)]
    [InlineData(44.2f)]
    public void CoarseMaterialIsGrassWhereverTheSurfaceFalls(float surface)
    {
        var map = SyntheticTerrain.Build((x, y, z) => y - surface, chunkRadius: 4);
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        var box = new LodSection(0, LevelContaining(surface, 2), 0, 2);
        Assert.True(ChunkMesher.TryFillScratch(map, box, scratch));

        var material = BlockType.BlockType_Air;
        float shallowest = float.NegativeInfinity;

        foreach (var sample in scratch)
        {
            if (sample.Distance >= 0f || sample.Distance <= shallowest) continue;

            shallowest = sample.Distance;
            material = sample.Material;
        }

        Assert.Equal(BlockType.BlockType_Grass, material);
    }

    static BlockType MaterialAt(ChunkMap map, int worldY)
    {
        Assert.True(map.TryGetVoxel(0, worldY, 0, out var voxel));
        return voxel.Material;
    }

    [Fact]
    public void MissingChunksFailRatherThanMeshingHoles()
    {
        var map = SyntheticTerrain.BuildOne(new ChunkIndex { x = 0, z = 0 }, (x, y, z) => y - 40.5f);
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        // A level-2 box spans 4x4 chunks; only one exists.
        Assert.False(ChunkMesher.TryFillScratch(map, new LodSection(0, 0, 0, 2), scratch));
    }

    /// <summary>
    /// Lazy slabs are the storage layer's whole memory budget, so a regression that silently materialises
    /// everything would be invisible except as 12x the resident set.
    /// </summary>
    [Fact]
    public void GeneratedChunksAllocateOnlyTheSlabsTheyNeed()
    {
        var chunk = ChunkGenerator.GenerateChunk(new ChunkIndex { x = 0, z = 0 });

        Assert.True(chunk.AllocatedSlabs < ChunkConstants.ChunkHeight / 4,
            $"{chunk.AllocatedSlabs} of {ChunkConstants.ChunkHeight} slabs allocated; most of a column is uniform");

        // And uniform slabs still read back correctly, which is the part a null slab could get wrong.
        for (int i = 0; i < ChunkConstants.ChunkVolume; i += 997)
            Assert.True(chunk[i].Density != 0 || chunk[i].Material == BlockType.BlockType_Air);
    }

    /// <summary>Decoding must collapse uniform slabs too, or a streamed chunk costs 13x a generated one.</summary>
    [Fact]
    public void DecodedChunksStayCompact()
    {
        var source = ChunkGenerator.GenerateChunk(new ChunkIndex { x = 3, z = -4 });
        var decoded = new TerrainChunk(source.index);

        var buffer = new byte[ChunkTransport.MaxPayloadBytes];
        var (slabs, length) = ChunkWire.Encode(source, 0, buffer);
        ChunkWire.Decode(decoded, 0, slabs, buffer.AsSpan(0, length));

        Assert.Equal(source.AllocatedSlabs, decoded.AllocatedSlabs);

        for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
            Assert.Equal(source[i].Density, decoded[i].Density);
    }

    // ---- Skirts ----

    /// <summary>
    /// The skirt exists to hide seams between levels, so it must add geometry at the box edge and leave
    /// the original mesh untouched — a skirt that reindexes the surface would corrupt the submeshes.
    /// </summary>
    [Fact]
    public void SkirtAppendsWithoutDisturbingTheSurface()
    {
        const float Surface = 40.5f;

        var map = SyntheticTerrain.Build((x, y, z) => y - Surface, chunkRadius: 4);
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        Assert.True(ChunkMesher.TryFillScratch(map, new LodSection(0, LevelContaining(Surface, 1), 0, 1), scratch));

        var mesh = ChunkMesher.GenerateMeshDualContouring(scratch);
        var skirted = ChunkMesher.AddSkirt(mesh, depth: 2f);

        Assert.True(skirted.Indices.Length > mesh.Indices.Length, "flat ground has a boundary to skirt");
        Assert.True(skirted.Positions.Length > mesh.Positions.Length);

        // Every original index and position is preserved in place.
        for (int i = 0; i < mesh.Indices.Length; i++) Assert.Equal(mesh.Indices[i], skirted.Indices[i]);
        for (int i = 0; i < mesh.Positions.Length; i++) Assert.Equal(mesh.Positions[i], skirted.Positions[i]);

        // The added run is one extra submesh covering exactly the appended indices.
        Assert.Equal(mesh.Submeshes.Length + 1, skirted.Submeshes.Length);

        var added = skirted.Submeshes[^1];
        Assert.Equal(mesh.Indices.Length, added.Start);
        Assert.Equal(skirted.Indices.Length - mesh.Indices.Length, added.Count);
    }

    [Fact]
    public void SkirtHangsDownwardOnly()
    {
        const float Surface = 40.5f;

        var map = SyntheticTerrain.Build((x, y, z) => y - Surface, chunkRadius: 4);
        var scratch = new Sample[ChunkMesher.ScratchVolume];

        Assert.True(ChunkMesher.TryFillScratch(map, new LodSection(0, LevelContaining(Surface, 1), 0, 1), scratch));

        var mesh = ChunkMesher.GenerateMeshDualContouring(scratch);
        float lowest = float.MaxValue;
        foreach (var p in mesh.Positions) lowest = MathF.Min(lowest, p.Y);

        var skirted = ChunkMesher.AddSkirt(mesh, depth: 2f);

        float skirtLowest = float.MaxValue;
        foreach (var p in skirted.Positions) skirtLowest = MathF.Min(skirtLowest, p.Y);

        Assert.True(skirtLowest < lowest, "the skirt must extend below the surface it is hiding a gap under");
        Assert.InRange(skirtLowest, lowest - 2f - 1e-3f, lowest);
    }

    [Fact]
    public void EmptyMeshGetsNoSkirt()
        => Assert.Equal(MeshData.Empty, ChunkMesher.AddSkirt(MeshData.Empty, depth: 2f));

    /// <summary>Which vertical box contains a given world height at this level.</summary>
    static int LevelContaining(float worldY, int level)
    {
        int size = ChunkConstants.SectionHeight << level;
        return (int)((worldY - ChunkConstants.WorldMinY) / size);
    }
}
