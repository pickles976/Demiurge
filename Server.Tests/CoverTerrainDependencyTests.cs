using System.Numerics;
using Demiurge.GameServer;

namespace Demiurge.ServerTests;

public sealed class CoverTerrainDependencyTests
{
    private static readonly Vector3 Cover = new(8f, 12f, 8f);
    private static readonly Vector3 Threat = new(40f, 12f, 8f);

    [Fact]
    public void DistantEditDoesNotInvalidateCover()
    {
        var map = new ChunkMap();
        long generation = map.EditVersion;
        ChunkIndex[] dependencies = CoverTerrainDependency.Capture(Cover, Cover, Threat);

        var distant = new ChunkIndex { x = 0, z = 10 };
        EditChunk(map, distant);

        Assert.False(CoverTerrainDependency.ChangedSince(map, dependencies, generation));
    }

    [Fact]
    public void EditAlongSightlineInvalidatesCover()
    {
        var map = new ChunkMap();
        long generation = map.EditVersion;
        ChunkIndex[] dependencies = CoverTerrainDependency.Capture(Cover, Cover, Threat);

        var sightline = new ChunkIndex { x = 1, z = 0 };
        EditChunk(map, sightline);

        Assert.True(CoverTerrainDependency.ChangedSince(map, dependencies, generation));
    }

    [Fact]
    public void EditAtCoverInvalidatesCoverAfterAnUnrelatedGenerationWasPromoted()
    {
        var map = new ChunkMap();
        ChunkIndex[] dependencies = CoverTerrainDependency.Capture(Cover, Cover, Threat);
        long generation = map.EditVersion;

        EditChunk(map, new ChunkIndex { x = 0, z = 10 });
        Assert.False(CoverTerrainDependency.ChangedSince(map, dependencies, generation));

        // MobSystem promotes an unaffected choice to this generation, avoiding repeated scans.
        generation = map.EditVersion;
        EditChunk(map, new ChunkIndex { x = 0, z = 0 });

        Assert.True(CoverTerrainDependency.ChangedSince(map, dependencies, generation));
    }

    [Fact]
    public void DiagonalRayDoesNotDependOnWholeBoundingRectangle()
    {
        ChunkIndex[] dependencies = CoverTerrainDependency.Capture(
            new Vector3(8f, 12f, 8f),
            new Vector3(8f, 12f, 8f),
            new Vector3(56f, 12f, 56f));

        Assert.Contains(new ChunkIndex { x = 1, z = 1 }, dependencies);
        Assert.DoesNotContain(new ChunkIndex { x = 0, z = 3 }, dependencies);
        Assert.DoesNotContain(new ChunkIndex { x = 3, z = 0 }, dependencies);
    }

    [Fact]
    public void FullMapResetInvalidatesCover()
    {
        var map = new ChunkMap();
        long generation = map.EditVersion;
        ChunkIndex[] dependencies = CoverTerrainDependency.Capture(Cover, Cover, Threat);

        map.Reset();

        Assert.True(CoverTerrainDependency.ChangedSince(map, dependencies, generation));
    }

    private static void EditChunk(ChunkMap map, ChunkIndex chunk)
    {
        (int originX, int originZ) = ChunkTransforms.ChunkOrigin(chunk);
        TerrainEdits.ApplyBox(
            map,
            new Vector3(
                originX + ChunkConstants.ChunkWidth / 2f,
                12f,
                originZ + ChunkConstants.ChunkWidth / 2f),
            new Vector3(0.1f),
            EditMode.Subtract,
            BlockType.BlockType_Air);
    }
}
