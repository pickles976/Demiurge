using System.Numerics;

namespace Demiurge.Tests;

public sealed class RuntimeMapTests
{
    [Fact]
    public void SaveLoadRoundTripPreservesChunksPlacementsAndHashes()
    {
        var map = CreateMap();
        string directory = Path.Combine(Path.GetTempPath(), "demiurge-map-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "runtime.dmap");

        try
        {
            RuntimeMapSerializer.Save(path, map);
            var loaded = RuntimeMapSerializer.Load(path);

            Assert.Equal(map.MapId, loaded.MapId);
            Assert.Equal(map.Name, loaded.Name);
            Assert.Equal(map.Terrain.Count, loaded.Terrain.Count);
            Assert.Equal(map.SourceHash, loaded.SourceHash);
            Assert.Equal(map.ContentHash, loaded.ContentHash);
            Assert.Equal(map.Placements, loaded.Placements);

            var origin = loaded.Terrain.Get(new ChunkIndex { x = 0, z = 0 })!;
            Assert.Equal(BlockType.BlockType_Stone, origin[0].Material);
            Assert.Equal(BlockType.BlockType_Air, origin[ChunkConstants.ChunkSize].Material);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadRejectsCorruptedContent()
    {
        var map = CreateMap();
        string directory = Path.Combine(Path.GetTempPath(), "demiurge-map-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "runtime.dmap");

        try
        {
            RuntimeMapSerializer.Save(path, map);
            byte[] bytes = File.ReadAllBytes(path);
            bytes[80] ^= 0x40;
            File.WriteAllBytes(path, bytes);

            var exception = Assert.Throws<InvalidDataException>(() => RuntimeMapSerializer.Load(path));
            Assert.Contains("hash", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ValidationRejectsPlayerSpawnIntersectingTerrain()
    {
        var valid = CreateMap();
        var map = new RuntimeMap
        {
            MapId = valid.MapId,
            Name = valid.Name,
            Terrain = valid.Terrain,
            SourceHash = valid.SourceHash,
            Placements =
            [
                new RuntimePlacement(RuntimePlacementKind.PlayerSpawn, new Vector3(0, 0, 0)),
            ],
        };

        var result = RuntimeMapValidation.Validate(map);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("intersects terrain"));
    }

    [Theory]
    [InlineData("valid-map", true)]
    [InlineData("valid_map_2", true)]
    [InlineData("../escape", false)]
    [InlineData("bad name", false)]
    [InlineData("", false)]
    public void MapNamesAreRestrictedToSafeSlugs(string name, bool expected)
        => Assert.Equal(expected, MapPathResolver.IsValidName(name));

    private static RuntimeMap CreateMap()
    {
        var terrain = new ChunkMap();
        for (int z = WorldGen.Min.z; z <= WorldGen.Max.z; z++)
            for (int x = WorldGen.Min.x; x <= WorldGen.Max.x; x++)
            {
                var chunk = new TerrainChunk(new ChunkIndex { x = x, z = z });
                chunk.FillSlab(0, Voxel.OutsideBelow);
                for (int y = 1; y < ChunkConstants.ChunkHeight; y++)
                    chunk.FillSlab(y, Voxel.OutsideAbove);
                terrain.Insert(chunk);
            }

        return new RuntimeMap
        {
            MapId = Guid.NewGuid(),
            Name = "round-trip",
            Terrain = terrain,
            SourceHash = Enumerable.Range(0, RuntimeMapSerializer.HashBytes).Select(i => (byte)i).ToArray(),
            Placements =
            [
                new RuntimePlacement(
                    RuntimePlacementKind.PlayerSpawn,
                    new Vector3(0, 1, 0),
                    Team: 2),
                new RuntimePlacement(RuntimePlacementKind.Pickup, new Vector3(2, 1, 2), Item: ItemType.Ak47),
                new RuntimePlacement(
                    RuntimePlacementKind.Mob,
                    new Vector3(-2, 1, -2),
                    Yaw: 1.5f,
                    Item: ItemType.Glock,
                    Team: 2),
                new RuntimePlacement(
                    RuntimePlacementKind.Flag,
                    new Vector3(4, 1, 4),
                    Team: 0),
            ],
        };
    }
}
