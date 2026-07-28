using System.Numerics;
using Demiurge.Editor;

namespace Demiurge.Editor.Tests;

public sealed class EditorCoreTests
{
    [Fact]
    public void TargetingUsesCorrectSidesAcrossNegativeBoundary()
    {
        var cells = EditorTargeting.Cells(new Vector3(-1f, 4.25f, 2.25f), Vector3.UnitX);
        Assert.Equal(new Int3(-2, 4, 2), cells.Solid);
        Assert.Equal(new Int3(-1, 4, 2), cells.Air);
    }

    [Fact]
    public void SourceRoundTripIsCanonical()
    {
        var document = EditorDocument.Create("canonical");
        document.Blocks.Add(new EditorBlockPlacement
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Sequence = 2,
            Cell = new Int3(2, 50, 2),
            BlockId = "demiurge:stone",
        });
        document.Blocks.Add(new EditorBlockPlacement
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Sequence = 1,
            Cell = new Int3(1, 50, 1),
            BlockId = "demiurge:dirt",
        });

        string directory = Path.Combine(Path.GetTempPath(), "demiurge-editor-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "source.json");
        try
        {
            SourceMapSerializer.Save(path, document);
            var loaded = SourceMapSerializer.Load(path);
            Assert.Equal(SourceMapSerializer.Hash(document), SourceMapSerializer.Hash(loaded));
            Assert.Equal([1L, 2L], loaded.Blocks.Select(block => block.Sequence));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TerrainAndBlockCommandsUndoExactly()
    {
        var document = EditorDocument.Create("undo");
        var session = new EditorSession(document);
        var target = new Int3(0, 90, 0);

        var block = new EditorBlockPlacement
        {
            Id = Guid.NewGuid(),
            Sequence = session.AllocateSequence(),
            Cell = target,
            BlockId = "demiurge:stone",
        };
        session.Execute(new SetBlocksCommand(
            "place block",
            new Dictionary<Int3, EditorBlockPlacement?> { [target] = null },
            new Dictionary<Int3, EditorBlockPlacement?> { [target] = block }));
        Assert.NotNull(session.BlockAt(target));

        session.Undo();
        Assert.Null(session.BlockAt(target));
        session.Redo();
        Assert.Equal(block.Id, session.BlockAt(target)?.Id);
    }

    [Theory]
    [InlineData(0, false, 1, 2)]
    [InlineData(1, false, -2, 1)]
    [InlineData(2, false, -1, -2)]
    [InlineData(0, true, -1, 2)]
    public void StructureTransformRotatesAndMirrors(
        int turns, bool mirror, int expectedX, int expectedZ)
    {
        var result = StructureLibrary.Transform(new Int3(1, 3, 2), turns, mirror);
        Assert.Equal(new Int3(expectedX, 3, expectedZ), result);
    }

    [Fact]
    public void BakedRuntimeRoundTripMatchesEditorTerrain()
    {
        var document = EditorDocument.Create("bake-parity");
        var runtime = EditorTerrainEvaluator.Bake(document);
        string directory = Path.Combine(
            Path.GetTempPath(), "demiurge-editor-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "runtime.dmap");

        try
        {
            RuntimeMapSerializer.Save(path, runtime);
            var loaded = RuntimeMapSerializer.Load(path);

            Assert.Equal(document.MapId, loaded.MapId);
            Assert.Equal(SourceMapSerializer.Hash(document), loaded.SourceHash);
            Assert.Equal(runtime.ContentHash, loaded.ContentHash);
            Assert.Equal(runtime.Terrain.Count, loaded.Terrain.Count);

            foreach (var expected in runtime.Terrain.Snapshot())
            {
                var actual = loaded.Terrain.Get(expected.index);
                Assert.NotNull(actual);
                for (int i = 0; i < ChunkConstants.ChunkVolume; i++)
                    Assert.Equal(expected[i], actual![i]);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RepositoryDetectsAndRecoversNewerAutosave()
    {
        string root = Path.Combine(
            Path.GetTempPath(), "demiurge-editor-tests", Guid.NewGuid().ToString("N"));
        var repository = new MapRepository(root);
        var source = EditorDocument.Create("recover");

        try
        {
            repository.Save(source);
            var autosave = source with
            {
                Placements =
                [
                    .. source.Placements,
                    new EditorPlacement
                    {
                        Id = Guid.NewGuid(),
                        Kind = EditorPlacementKind.Mob,
                        ArchetypeId = "demiurge:mob",
                        Cell = new Int3(2, 80, 2),
                    },
                ],
            };
            repository.SaveAutosave(autosave);
            File.SetLastWriteTimeUtc(
                repository.Paths.AutosavePath(source.Name),
                File.GetLastWriteTimeUtc(repository.Paths.SourcePath(source.Name)).AddSeconds(1));

            Assert.True(repository.HasNewerAutosave(source.Name));
            Assert.Equal(2, repository.LoadAutosave(source.Name).Placements.Count);

            repository.DeleteAutosave(source.Name);
            Assert.False(repository.HasAutosave(source.Name));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ValidationRejectsBrushesCrossingEditableBounds()
    {
        var document = EditorDocument.Create("brush-bounds");
        document.TerrainStrokes.Add(new TerrainStroke
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Mode = EditMode.Add,
            Shape = EditShape.Sphere,
            HalfExtent = new Float3(2, 2, 2),
            Strength = 1,
            MaterialId = "demiurge:stone",
            Dabs =
            [
                new Float3(
                    WorldGen.MeshableMin.x * ChunkConstants.ChunkWidth,
                    50,
                    0),
            ],
        });

        var result = EditorValidation.Validate(document);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("outside editable bounds"));
    }

    [Fact]
    public void DiagnosticsReportMaximumOperationsOverlappingAChunk()
    {
        var document = EditorDocument.Create("diagnostics");
        document.Blocks.AddRange(
        [
            new EditorBlockPlacement
            {
                Id = Guid.NewGuid(),
                Sequence = 1,
                Cell = new Int3(0, 80, 0),
                BlockId = "demiurge:stone",
            },
            new EditorBlockPlacement
            {
                Id = Guid.NewGuid(),
                Sequence = 2,
                Cell = new Int3(1, 80, 1),
                BlockId = "demiurge:dirt",
            },
        ]);

        var diagnostics = new EditorTerrainEvaluator().Diagnostics(document);

        Assert.Equal(2, diagnostics.BlockCount);
        Assert.True(diagnostics.MaximumChunkOverlap >= 2);
    }

    [Fact]
    public void PreviewValidationRejectsSpawnInsideTerrain()
    {
        var document = EditorDocument.Create("spawn-clearance");
        document.Placements[0] = document.Placements[0] with
        {
            Cell = new Int3(0, ChunkConstants.WorldMinY, 0),
        };
        var terrain = new EditorTerrainEvaluator().EvaluateAll(document);

        var result = EditorValidation.Validate(document, terrain);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("intersects terrain"));
    }
}
