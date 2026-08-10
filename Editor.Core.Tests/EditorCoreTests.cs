using System.Numerics;
using Demiurge.Editor;

namespace Demiurge.Editor.Tests;

public sealed class EditorCoreTests
{
    /// <summary>
    /// The structure editor's world is a flat pad of the size it says it is. Asserted through the
    /// document rather than against the generator directly, because the property that matters is
    /// that the DECLARED base terrain is the one evaluation builds — that declaration existed and
    /// was ignored until this world needed it.
    /// </summary>
    [Fact]
    public void StructureWorldIsAFlatDebugPadOfTheDeclaredSize()
    {
        var document = EditorDocument.CreateStructureWorld("scratch");
        var evaluator = new EditorTerrainEvaluator();
        var map = new ChunkMap();
        foreach (var index in new[]
                 {
                     ChunkTransforms.ChunkAt(0, 0),
                     ChunkTransforms.ChunkAt((int)StructureWorld.HalfExtent + 8, 0),
                 })
            map.Insert(evaluator.EvaluateChunk(document, index));

        int floor = StructureWorld.FloorY;

        // Standing on the pad: solid below the surface, air above it.
        Assert.True(map.TryGetVoxel(0, floor - 1, 0, out var below));
        Assert.True(below.Distance < 0f);
        Assert.Equal(BlockType.BlockType_Debug, below.Material);
        Assert.True(map.TryGetVoxel(0, floor + 1, 0, out var above));
        Assert.True(above.Distance > 0f);

        // Past the edge there is no pad at all — that is what makes the working area visible.
        int outside = (int)StructureWorld.HalfExtent + 8;
        Assert.True(map.TryGetVoxel(outside, floor - 1, 0, out var beyond));
        Assert.True(beyond.Distance > 0f);

        // ...but the world floor is still sealed, wherever you are.
        Assert.True(map.TryGetVoxel(outside, ChunkConstants.WorldMinY, 0, out var bedrock));
        Assert.True(bedrock.Distance < 0f);
    }

    /// <summary>
    /// The scratch pad validates as itself and never becomes a map file. Both halves matter: the
    /// first because bounds used to be checked against the runtime map's extent whatever the
    /// document declared, the second because the shutdown autosave would otherwise create a map
    /// directory for a world nobody asked to keep.
    /// </summary>
    [Fact]
    public void StructureWorldValidatesButCannotBeSavedAsAMap()
    {
        var document = EditorDocument.CreateStructureWorld("scratch");

        Assert.True(document.IsStructureWorld);
        Assert.True(EditorValidation.Validate(document).IsValid);

        var maps = new MapRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Throws<InvalidOperationException>(() => maps.Save(document));
        Assert.Throws<InvalidOperationException>(() => maps.SaveAutosave(document));
        Assert.False(Directory.Exists(maps.Paths.Root));
    }

    /// <summary>
    /// A capture is bounded by the blocks themselves, and anchored at the base of that box's
    /// horizontal centre. The centre is what makes rotation turn a structure in place instead of
    /// swinging it away from the cursor, so it is asserted as a property of the pair rather than as
    /// a pivot coordinate: the same set of cells, rotated, occupies the same footprint.
    /// </summary>
    [Fact]
    public void CaptureBoundsItselfAndRotatesAboutItsCentre()
    {
        // A 3x1x3 pad of blocks from (10,20,30) to (12,20,32), plus one block above the middle.
        var blocks = new List<EditorBlockPlacement>();
        for (int x = 10; x <= 12; x++)
            for (int z = 30; z <= 32; z++)
                blocks.Add(BlockAt(new Int3(x, 20, z)));
        blocks.Add(BlockAt(new Int3(11, 21, 31)));

        var structure = StructureLibrary.Capture("pad", blocks);

        Assert.Equal(blocks.Count, structure.Blocks.Count);
        // Centre of 10..12 and 30..32, at the lowest occupied layer.
        Assert.Equal(new Int3(11, 20, 31), structure.Pivot);

        var footprint = structure.Blocks
            .Select(block => StructureLibrary.Transform(block.Offset, 0, mirrorX: false))
            .OrderBy(o => o.X).ThenBy(o => o.Y).ThenBy(o => o.Z)
            .ToArray();
        var turned = structure.Blocks
            .Select(block => StructureLibrary.Transform(block.Offset, 1, mirrorX: false))
            .OrderBy(o => o.X).ThenBy(o => o.Y).ThenBy(o => o.Z)
            .ToArray();
        Assert.Equal(footprint, turned);
    }

    [Fact]
    public void CaptureRefusesAnEmptyPad()
        => Assert.Throws<ArgumentException>(() => StructureLibrary.Capture("empty", []));

    private static EditorBlockPlacement BlockAt(Int3 cell) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = 1,
        Cell = cell,
        BlockId = "demiurge:stone",
    };

    [Fact]
    public void TargetingUsesCorrectSidesAcrossNegativeBoundary()
    {
        var cells = EditorTargeting.Cells(new Vector3(-1f, 4.25f, 2.25f), Vector3.UnitX);
        Assert.Equal(new Int3(-2, 4, 2), cells.Solid);
        Assert.Equal(new Int3(-1, 4, 2), cells.Air);
    }

    [Fact]
    public void BlockTargetingSelectsSamplesAcrossTheSurface()
    {
        var samples = EditorTargeting.Samples(
            new Vector3(3.25f, 12.5f, -2.25f), Vector3.UnitY);

        Assert.Equal(new Int3(3, 12, -2), samples.Solid);
        Assert.Equal(new Int3(3, 13, -2), samples.Air);
    }

    [Fact]
    public void BlockBrushPreservesDimensionsAndBounds()
    {
        var anchor = new Int3(10, 20, 30);
        var size = new Int3(3, 2, 1);

        var cells = BlockBrush.Cells(anchor, size).ToArray();
        var (min, max) = BlockBrush.Bounds(anchor, size);

        Assert.Equal(6, cells.Length);
        Assert.Equal(6, cells.Distinct().Count());
        Assert.Equal(new Int3(9, 20, 30), min);
        Assert.Equal(new Int3(11, 21, 30), max);
        Assert.All(cells, cell =>
            Assert.True(
                cell.X >= min.X && cell.X <= max.X
                && cell.Y >= min.Y && cell.Y <= max.Y
                && cell.Z >= min.Z && cell.Z <= max.Z));
    }

    [Fact]
    public void EditorCommandsConfigureOrganicAndBlockBrushes()
    {
        var session = new EditorSession(EditorDocument.Create("tool-settings"));
        var settings = new EditorToolSettings();

        Assert.True(EditorCommandParser.Execute(
            "editor terrain shape organic", settings, session).Success);
        Assert.Equal(EditShape.Organic, settings.TerrainShape);

        Assert.True(EditorCommandParser.Execute(
            "editor block size 5 2 1", settings, session).Success);
        Assert.Equal(new Int3(5, 2, 1), settings.BlockSize);
        Assert.False(EditorCommandParser.Execute(
            "editor block rotate 90", settings, session).Success);

        Assert.True(EditorCommandParser.Execute(
            "editor object team 3", settings, session).Success);
        Assert.True(EditorCommandParser.Execute(
            "editor object flag", settings, session).Success);
        Assert.Equal(3, settings.ObjectTeam);
        Assert.Equal(EditorObjectChoiceKind.Flag, settings.ObjectKind);
        Assert.Equal("demiurge:flag", settings.ObjectId);
    }

    [Fact]
    public void PlacementIdsResolveDisplayedPrefixes()
    {
        var document = EditorDocument.Create("placement-ids");
        var mob = new EditorPlacement
        {
            Id = Guid.Parse("a1b2c3d4-1111-2222-3333-444444444444"),
            Kind = EditorPlacementKind.Mob,
            ArchetypeId = "demiurge:mob",
            Cell = new Int3(2, 80, 2),
            WeaponId = "demiurge:ppsh",
            Team = 2,
        };
        document.Placements.Add(mob);

        Assert.Equal("a1b2c3d4", EditorPlacementIds.Display(mob.Id));
        Assert.Equal(mob.Id, EditorPlacementIds.Resolve(document, "a1b2c3d4").Id);
        Assert.Equal(mob.Id, EditorPlacementIds.Resolve(document, mob.Id.ToString()).Id);
    }

    [Fact]
    public void PickingTakesTheNearestPlacementInsideTheRayLimit()
    {
        // No terrain, so every placement resolves to its cell centre.
        var terrain = new ChunkMap();
        var near = MobAt(new Int3(0, 0, 4));
        var far = MobAt(new Int3(0, 0, 20));
        EditorPlacement[] placements = [far, near];
        var origin = new Vector3(0.5f, 0.9f, 0.5f);

        Assert.Equal(
            near.Id,
            EditorPlacementPicker.Pick(placements, terrain, origin, Vector3.UnitZ, 100f));

        // The terrain hit distance clips picking, so an object behind a hill is not reachable.
        Assert.Null(EditorPlacementPicker.Pick(placements, terrain, origin, Vector3.UnitZ, 2f));

        // A ray that misses the bounds picks nothing, even pointed the right way.
        Assert.Null(EditorPlacementPicker.Pick(
            placements, terrain, origin + new Vector3(3f, 0f, 0f), Vector3.UnitZ, 100f));
    }

    [Fact]
    public void PlacementBoundsSitOnTheSurfaceAndCoverTheView()
    {
        var (min, max) = EditorPlacementBounds.Local(EditorPlacementKind.Mob);
        Assert.Equal(0f, min.Y);
        Assert.Equal(PlayerMovement.Body.Height, max.Y);

        var (flagMin, flagMax) = EditorPlacementBounds.Local(EditorPlacementKind.Flag);
        Assert.Equal(0f, flagMin.Y);
        Assert.True(flagMax.Y > max.Y, "the flag pole is taller than a man");
    }

    private static EditorPlacement MobAt(Int3 cell) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EditorPlacementKind.Mob,
        ArchetypeId = "demiurge:mob",
        Cell = cell,
        Team = 1,
    };

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
        document.Placements.Add(new EditorPlacement
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Kind = EditorPlacementKind.Mob,
            ArchetypeId = "demiurge:mob",
            Cell = new Int3(2, 50, 2),
            WeaponId = "demiurge:ppsh",
            Team = 2,
        });

        string directory = Path.Combine(Path.GetTempPath(), "demiurge-editor-tests", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "source.json");
        try
        {
            SourceMapSerializer.Save(path, document);
            var loaded = SourceMapSerializer.Load(path);
            Assert.Equal(SourceMapSerializer.Hash(document), SourceMapSerializer.Hash(loaded));
            Assert.Equal([1L, 2L], loaded.Blocks.Select(block => block.Sequence));
            Assert.Equal(
                "demiurge:ppsh",
                Assert.Single(loaded.Placements, placement =>
                    placement.Kind == EditorPlacementKind.Mob).WeaponId);
            Assert.Equal(
                2,
                Assert.Single(loaded.Placements, placement =>
                    placement.Kind == EditorPlacementKind.Mob).Team);
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

    [Fact]
    public void RestoringPlaytestTerrainDoesNotChangeSourceState()
    {
        var session = new EditorSession(EditorDocument.Create("playtest-restore"));
        var index = new ChunkIndex { x = 0, z = 0 };
        var chunk = Assert.IsType<TerrainChunk>(session.Terrain.Get(index));
        int voxelIndex = ChunkTransforms.WorldVoxelIndex(4, ChunkConstants.WorldMinY, 4);
        Voxel source = chunk[voxelIndex];
        Assert.NotEqual(default, source);
        chunk[voxelIndex] = default;
        int changeCount = 0;
        session.Changed += _ => changeCount++;

        session.RestoreTerrain([index]);

        Assert.Equal(source, session.Terrain.Get(index)![voxelIndex]);
        Assert.False(session.Dirty);
        Assert.Equal(0, session.History.UndoCount);
        Assert.Equal(1, changeCount);
    }

    [Fact]
    public void EvaluatedBlockUsesItsCanonicalMaterial()
    {
        var document = EditorDocument.Create("block-material");
        document.Blocks.Add(new EditorBlockPlacement
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            Cell = new Int3(8, 90, 8),
            BlockId = "demiurge:stone",
        });

        var chunk = new EditorTerrainEvaluator().EvaluateChunk(
            document, new ChunkIndex { x = 0, z = 0 });

        var voxel = chunk[ChunkTransforms.WorldVoxelIndex(8, 90, 8)];
        Assert.Equal(-0.5f, voxel.Distance, 4);
        Assert.Equal(BlockType.BlockType_Stone, voxel.Material);
        Assert.True(chunk[ChunkTransforms.WorldVoxelIndex(9, 90, 8)].Distance > 0f);
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
        document.Placements.Add(new EditorPlacement
        {
            Id = Guid.NewGuid(),
            Kind = EditorPlacementKind.Mob,
            ArchetypeId = "demiurge:mob",
            Cell = document.Placements[0].Cell with { X = 2 },
            WeaponId = "demiurge:ppsh",
            Team = 2,
        });
        document.Placements.Add(new EditorPlacement
        {
            Id = Guid.NewGuid(),
            Kind = EditorPlacementKind.Flag,
            ArchetypeId = "demiurge:flag",
            Cell = document.Placements[0].Cell with { X = 4 },
            Team = 0,
        });
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
            Assert.Equal(
                ItemType.Ppsh,
                Assert.Single(loaded.Placements, placement =>
                    placement.Kind == RuntimePlacementKind.Mob).Item);
            Assert.Equal(
                2,
                Assert.Single(loaded.Placements, placement =>
                    placement.Kind == RuntimePlacementKind.Mob).Team);
            Assert.Equal(
                0,
                Assert.Single(loaded.Placements, placement =>
                    placement.Kind == RuntimePlacementKind.Flag).Team);

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

    [Fact]
    public void PlacementPositionUsesLocalSdfSurface()
    {
        var document = EditorDocument.Create("placement-surface");
        var terrain = new EditorTerrainEvaluator().EvaluateAll(document);
        float surfaceY = SurfaceQuery.SurfacePosition(terrain, 0.5f, 0.5f).Y;
        var cell = new Int3(0, (int)MathF.Floor(surfaceY), 0);

        var position = EditorPlacementPosition.Resolve(terrain, cell);

        Assert.Equal(0.5f, position.X);
        Assert.Equal(0.5f, position.Z);
        Assert.True(TerrainCollision.TrySampleRaw(terrain, position, out float distance));
        Assert.InRange(MathF.Abs(distance), 0f, 0.03f);
    }
}
