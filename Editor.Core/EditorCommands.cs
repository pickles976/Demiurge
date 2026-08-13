namespace Demiurge.Editor;

public sealed record EditorChange(
    IReadOnlySet<ChunkIndex> TerrainChunks,
    IReadOnlySet<Guid> PlacementIds)
{
    public static EditorChange None { get; } =
        new(new HashSet<ChunkIndex>(), new HashSet<Guid>());

    public static EditorChange ForStroke(TerrainStroke stroke, EditorTerrainEvaluator evaluator)
        => new(evaluator.AffectedChunks(stroke), new HashSet<Guid>());

    public static EditorChange ForBlocks(IEnumerable<EditorBlockPlacement> blocks)
        => new(
            blocks.SelectMany(EditorTerrainEvaluator.AffectedChunks).ToHashSet(),
            new HashSet<Guid>());

    public static EditorChange ForPlacements(params Guid[] ids)
        => new(new HashSet<ChunkIndex>(), ids.ToHashSet());

    public static EditorChange Merge(EditorChange left, EditorChange right)
        => new(
            left.TerrainChunks.Concat(right.TerrainChunks).ToHashSet(),
            left.PlacementIds.Concat(right.PlacementIds).ToHashSet());
}

public interface IEditorCommand
{
    string Description { get; }
    EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator);
    EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator);
}

public sealed class AddTerrainStrokeCommand : IEditorCommand
{
    private readonly TerrainStroke stroke;
    public string Description => $"Terrain {stroke.Mode.ToString().ToLowerInvariant()} stroke";

    public AddTerrainStrokeCommand(TerrainStroke stroke) => this.stroke = stroke;

    public EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        if (document.TerrainStrokes.Any(existing => existing.Id == stroke.Id))
            throw new InvalidOperationException($"Terrain stroke {stroke.Id} already exists");
        document.TerrainStrokes.Add(stroke);
        return EditorChange.ForStroke(stroke, evaluator);
    }

    public EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        document.TerrainStrokes.RemoveAll(existing => existing.Id == stroke.Id);
        return EditorChange.ForStroke(stroke, evaluator);
    }
}

public sealed class SetBlocksCommand : IEditorCommand
{
    private readonly Dictionary<Int3, EditorBlockPlacement?> before;
    private readonly Dictionary<Int3, EditorBlockPlacement?> after;
    public string Description { get; }

    public SetBlocksCommand(
        string description,
        IReadOnlyDictionary<Int3, EditorBlockPlacement?> before,
        IReadOnlyDictionary<Int3, EditorBlockPlacement?> after)
    {
        Description = description;
        this.before = new(before);
        this.after = new(after);
    }

    public EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator)
        => Set(document, before.Keys.Concat(after.Keys), after);

    public EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator)
        => Set(document, before.Keys.Concat(after.Keys), before);

    private static EditorChange Set(
        EditorDocument document,
        IEnumerable<Int3> cells,
        IReadOnlyDictionary<Int3, EditorBlockPlacement?> values)
    {
        var uniqueCells = cells.ToHashSet();
        var changed = document.Blocks.Where(block => uniqueCells.Contains(block.Cell)).ToList();
        document.Blocks.RemoveAll(block => uniqueCells.Contains(block.Cell));
        foreach (var cell in uniqueCells)
            if (values.TryGetValue(cell, out var value) && value is not null)
            {
                document.Blocks.Add(value);
                changed.Add(value);
            }
        return EditorChange.ForBlocks(changed);
    }
}

public sealed class AddPlacementCommand : IEditorCommand
{
    private readonly EditorPlacement placement;
    public string Description => $"Place {placement.Kind}";

    public AddPlacementCommand(EditorPlacement placement) => this.placement = placement;

    public EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        if (document.Placements.Any(existing => existing.Id == placement.Id))
            throw new InvalidOperationException($"Placement {placement.Id} already exists");
        document.Placements.Add(placement);
        return EditorChange.ForPlacements(placement.Id);
    }

    public EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        document.Placements.RemoveAll(existing => existing.Id == placement.Id);
        return EditorChange.ForPlacements(placement.Id);
    }
}

/// <summary>
/// Many placements as one undo step. A brush stroke that painted forty trees and took forty presses
/// of undo to remove would be unusable, which is the same reason <see cref="SetBlocksCommand"/>
/// exists for blocks.
/// </summary>
public sealed class AddPlacementsCommand : IEditorCommand
{
    private readonly IReadOnlyList<EditorPlacement> placements;
    public string Description { get; }

    public AddPlacementsCommand(string description, IReadOnlyList<EditorPlacement> placements)
    {
        this.placements = placements;
        Description = description;
    }

    public EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        var existing = document.Placements.Select(placement => placement.Id).ToHashSet();
        foreach (var placement in placements)
        {
            if (!existing.Add(placement.Id))
                throw new InvalidOperationException($"Placement {placement.Id} already exists");
            document.Placements.Add(placement);
        }

        return Changed();
    }

    public EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        var removing = placements.Select(placement => placement.Id).ToHashSet();
        document.Placements.RemoveAll(placement => removing.Contains(placement.Id));
        return Changed();
    }

    private EditorChange Changed()
        => EditorChange.ForPlacements([.. placements.Select(placement => placement.Id)]);
}

/// <summary>The inverse, for the eraser: a stroke's worth of removals as one step.</summary>
public sealed class DeletePlacementsCommand : IEditorCommand
{
    private readonly IReadOnlyList<EditorPlacement> placements;
    public string Description { get; }

    public DeletePlacementsCommand(string description, IReadOnlyList<EditorPlacement> placements)
    {
        this.placements = placements;
        Description = description;
    }

    public EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        var removing = placements.Select(placement => placement.Id).ToHashSet();
        document.Placements.RemoveAll(placement => removing.Contains(placement.Id));
        return Changed();
    }

    public EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        document.Placements.AddRange(placements);
        return Changed();
    }

    private EditorChange Changed()
        => EditorChange.ForPlacements([.. placements.Select(placement => placement.Id)]);
}

public sealed class UpdatePlacementCommand : IEditorCommand
{
    private readonly EditorPlacement before;
    private readonly EditorPlacement after;
    public string Description { get; }

    public UpdatePlacementCommand(string description, EditorPlacement before, EditorPlacement after)
    {
        Description = description;
        this.before = before;
        this.after = after;
    }

    public EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator)
        => Replace(document, before.Id, after);

    public EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator)
        => Replace(document, after.Id, before);

    private static EditorChange Replace(EditorDocument document, Guid id, EditorPlacement value)
    {
        int index = document.Placements.FindIndex(placement => placement.Id == id);
        if (index < 0) throw new InvalidOperationException($"Placement {id} does not exist");
        document.Placements[index] = value;
        return EditorChange.ForPlacements(id);
    }
}

public sealed class DeletePlacementCommand : IEditorCommand
{
    private readonly EditorPlacement placement;
    public string Description => $"Delete {placement.Kind}";

    public DeletePlacementCommand(EditorPlacement placement) => this.placement = placement;

    public EditorChange Apply(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        document.Placements.RemoveAll(existing => existing.Id == placement.Id);
        return EditorChange.ForPlacements(placement.Id);
    }

    public EditorChange Revert(EditorDocument document, EditorTerrainEvaluator evaluator)
    {
        document.Placements.Add(placement);
        return EditorChange.ForPlacements(placement.Id);
    }
}
