using System.Numerics;

namespace Demiurge.Editor;

public sealed class EditorTerrainEvaluator
{
    public ChunkMap EvaluateAll(EditorDocument document)
    {
        var operationIndex = EditorOperationIndex.Build(document, this);
        var map = new ChunkMap();
        for (int x = WorldGen.Min.x; x <= WorldGen.Max.x; x++)
            for (int z = WorldGen.Min.z; z <= WorldGen.Max.z; z++)
            {
                var index = new ChunkIndex { x = x, z = z };
                map.Insert(EvaluateChunk(index, operationIndex));
            }
        return map;
    }

    public TerrainChunk EvaluateChunk(EditorDocument document, ChunkIndex index)
        => EvaluateChunk(index, EditorOperationIndex.Build(document, this));

    public IReadOnlyDictionary<ChunkIndex, TerrainChunk> EvaluateChunks(
        EditorDocument document,
        IEnumerable<ChunkIndex> chunks)
    {
        var operationIndex = EditorOperationIndex.Build(document, this);
        return chunks
            .Distinct()
            .ToDictionary(index => index, index => EvaluateChunk(index, operationIndex));
    }

    public EditorOperationDiagnostics Diagnostics(EditorDocument document)
        => EditorOperationIndex.Build(document, this).Diagnostics;

    private static TerrainChunk EvaluateChunk(ChunkIndex index, EditorOperationIndex operationIndex)
    {
        var chunk = ChunkGenerator.GenerateChunk(index);

        foreach (var operation in operationIndex.For(index))
        {
            if (operation.Stroke is { } stroke)
            {
                if (!BlockCatalog.TryResolve(stroke.MaterialId, out var material))
                    throw new InvalidDataException($"Unknown material {stroke.MaterialId}");
                foreach (var dab in stroke.Dabs)
                    TerrainEdits.ApplyBox(
                        chunk, dab.Vector, stroke.HalfExtent.Vector, stroke.Mode,
                        stroke.Mode == EditMode.Subtract ? BlockType.BlockType_Air : material,
                        stroke.Shape, stroke.Strength);
            }
            else if (operation.Block is { } block)
            {
                if (!BlockCatalog.TryResolve(block.BlockId, out var material))
                    throw new InvalidDataException($"Unknown material {block.BlockId}");
                TerrainEdits.ApplyBlockCell(chunk, block.Cell.SamplePosition, material);
            }
        }

        chunk.CollapseUniformSlabs();
        return chunk;
    }

    public IReadOnlySet<ChunkIndex> AffectedChunks(TerrainStroke stroke)
    {
        var chunks = new HashSet<ChunkIndex>();
        foreach (var dab in stroke.Dabs)
        {
            var (low, high) = TerrainEdits.AffectedBounds(dab.Vector, stroke.HalfExtent.Vector);
            var first = ChunkTransforms.ChunkAt(low.X, low.Z);
            var last = ChunkTransforms.ChunkAt(high.X, high.Z);
            for (int z = first.z; z <= last.z; z++)
                for (int x = first.x; x <= last.x; x++)
                    chunks.Add(new ChunkIndex { x = x, z = z });
        }
        return chunks;
    }

    public static IReadOnlySet<ChunkIndex> AffectedChunks(EditorBlockPlacement block)
    {
        var (low, high) = TerrainEdits.AffectedBounds(block.Cell.SamplePosition, new Vector3(0.5f));
        var first = ChunkTransforms.ChunkAt(low.X, low.Z);
        var last = ChunkTransforms.ChunkAt(high.X, high.Z);
        var chunks = new HashSet<ChunkIndex>();
        for (int z = first.z; z <= last.z; z++)
            for (int x = first.x; x <= last.x; x++)
                chunks.Add(new ChunkIndex { x = x, z = z });
        return chunks;
    }

    public static RuntimeMap Bake(EditorDocument document)
    {
        var terrain = new EditorTerrainEvaluator().EvaluateAll(document);
        return Bake(document, terrain);
    }

    /// <summary>
    /// Builds runtime metadata around an already-current editor terrain field. This is the fast
    /// in-memory playtest path; persisted bakes still call <see cref="Bake(EditorDocument)"/> and
    /// independently replay the source document.
    /// </summary>
    public static RuntimeMap Bake(EditorDocument document, ChunkMap terrain)
    {
        var validation = EditorValidation.Validate(document);
        if (!validation.IsValid)
            throw new InvalidDataException(string.Join("; ", validation.Errors));

        var placements = document.Placements
            .OrderBy(placement => placement.Kind)
            .ThenBy(placement => placement.Id)
            .Select(placement => ToRuntimePlacement(placement, terrain))
            .ToArray();
        var runtime = new RuntimeMap
        {
            MapId = document.MapId,
            Name = document.Name,
            Terrain = terrain,
            Placements = placements,
            SourceHash = SourceMapSerializer.Hash(document),
        };
        var runtimeValidation = RuntimeMapValidation.Validate(runtime);
        if (!runtimeValidation.IsValid)
            throw new InvalidDataException(string.Join("; ", runtimeValidation.Errors));
        return runtime;
    }

    private static RuntimePlacement ToRuntimePlacement(EditorPlacement placement, ChunkMap terrain)
    {
        var position = EditorPlacementPosition.Resolve(terrain, placement);
        return placement.Kind switch
        {
            EditorPlacementKind.Pickup => new RuntimePlacement(
                RuntimePlacementKind.Pickup, position, placement.Yaw,
                ItemCatalog.TryResolve(placement.ArchetypeId, out var item)
                    ? item
                    : throw new InvalidDataException($"Unknown item {placement.ArchetypeId}")),
            EditorPlacementKind.Mob => new RuntimePlacement(
                RuntimePlacementKind.Mob, position, placement.Yaw,
                Item: ResolveMobWeapon(placement),
                Team: placement.Team),
            EditorPlacementKind.PlayerSpawn => new RuntimePlacement(
                RuntimePlacementKind.PlayerSpawn, position, placement.Yaw,
                SpawnId: placement.ArchetypeId["demiurge:spawn/".Length..],
                Team: placement.Team),
            EditorPlacementKind.Flag => new RuntimePlacement(
                RuntimePlacementKind.Flag, position, placement.Yaw,
                SpawnId: "flag",
                Team: 0),
            _ => throw new InvalidDataException($"Unknown placement kind {placement.Kind}"),
        };
    }

    private static ItemType ResolveMobWeapon(EditorPlacement placement)
    {
        string weaponId = placement.WeaponId ?? ItemCatalog.Id(ItemType.Ak47);
        if (!ItemCatalog.TryResolve(weaponId, out var weapon) || WeaponConfig.Get(weapon) is null)
            throw new InvalidDataException($"Unknown mob weapon {weaponId}");
        return weapon;
    }

    private sealed class EditorOperationIndex
    {
        private readonly Dictionary<ChunkIndex, List<IndexedOperation>> byChunk = [];

        public EditorOperationDiagnostics Diagnostics { get; private set; }

        public static EditorOperationIndex Build(
            EditorDocument document,
            EditorTerrainEvaluator evaluator)
        {
            var result = new EditorOperationIndex();
            foreach (var stroke in document.TerrainStrokes)
                foreach (var chunk in evaluator.AffectedChunks(stroke))
                    result.Add(chunk, new IndexedOperation(stroke.Sequence, stroke, null));
            foreach (var block in document.Blocks)
                foreach (var chunk in AffectedChunks(block))
                    result.Add(chunk, new IndexedOperation(block.Sequence, null, block));

            foreach (var operations in result.byChunk.Values)
                operations.Sort(static (left, right) =>
                {
                    int sequence = left.Sequence.CompareTo(right.Sequence);
                    if (sequence != 0) return sequence;
                    Guid leftId = left.Stroke?.Id ?? left.Block!.Id;
                    Guid rightId = right.Stroke?.Id ?? right.Block!.Id;
                    return leftId.CompareTo(rightId);
                });

            result.Diagnostics = new EditorOperationDiagnostics(
                document.TerrainStrokes.Count,
                document.Blocks.Count,
                result.byChunk.Count == 0 ? 0 : result.byChunk.Values.Max(value => value.Count));
            return result;
        }

        public IReadOnlyList<IndexedOperation> For(ChunkIndex chunk)
            => byChunk.TryGetValue(chunk, out var operations) ? operations : [];

        private void Add(ChunkIndex chunk, IndexedOperation operation)
        {
            if (!byChunk.TryGetValue(chunk, out var operations))
                byChunk.Add(chunk, operations = []);
            operations.Add(operation);
        }
    }

    private readonly record struct IndexedOperation(
        long Sequence,
        TerrainStroke? Stroke,
        EditorBlockPlacement? Block);
}

public readonly record struct EditorOperationDiagnostics(
    int TerrainStrokeCount,
    int BlockCount,
    int MaximumChunkOverlap);
