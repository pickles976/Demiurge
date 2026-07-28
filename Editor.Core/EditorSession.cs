namespace Demiurge.Editor;

public sealed class EditorSession
{
    private readonly EditorTerrainEvaluator evaluator;
    private readonly EditorHistory history;
    private long nextSequence;

    public EditorDocument Document { get; }
    public ChunkMap Terrain { get; }
    public bool Dirty { get; private set; }
    public EditorHistory History => history;

    public event Action<EditorChange>? Changed;

    public EditorSession(
        EditorDocument document,
        EditorTerrainEvaluator? evaluator = null,
        EditorHistory? history = null)
    {
        Document = document;
        this.evaluator = evaluator ?? new EditorTerrainEvaluator();
        this.history = history ?? new EditorHistory();
        Terrain = this.evaluator.EvaluateAll(document);
        nextSequence = Math.Max(
            document.TerrainStrokes.Select(stroke => stroke.Sequence).DefaultIfEmpty().Max(),
            document.Blocks.Select(block => block.Sequence).DefaultIfEmpty().Max()) + 1;
    }

    public long AllocateSequence() => nextSequence++;

    public EditorChange Execute(IEditorCommand command)
    {
        var change = command.Apply(Document, evaluator);
        history.Record(command);
        Dirty = true;
        ApplyTerrainChange(change);
        Changed?.Invoke(change);
        return change;
    }

    public EditorChange? Undo()
    {
        var command = history.TakeUndo();
        if (command is null) return null;
        var change = command.Revert(Document, evaluator);
        Dirty = true;
        ApplyTerrainChange(change);
        Changed?.Invoke(change);
        return change;
    }

    public EditorChange? Redo()
    {
        var command = history.TakeRedo();
        if (command is null) return null;
        var change = command.Apply(Document, evaluator);
        Dirty = true;
        ApplyTerrainChange(change);
        Changed?.Invoke(change);
        return change;
    }

    public void MarkSaved() => Dirty = false;

    public RuntimeMap Bake() => EditorTerrainEvaluator.Bake(Document);

    public EditorBlockPlacement? BlockAt(Int3 cell)
        => Document.Blocks.FirstOrDefault(block => block.Cell == cell);

    public EditorPlacement? Placement(Guid id)
        => Document.Placements.FirstOrDefault(placement => placement.Id == id);

    /// <summary>
    /// Replays source operations for chunks temporarily changed by an in-editor playtest. This is
    /// intentionally outside history and does not change the document's dirty state.
    /// </summary>
    public EditorChange RestoreTerrain(IEnumerable<ChunkIndex> chunks)
    {
        var affected = chunks
            .Where(chunk =>
                chunk.x >= WorldGen.Min.x && chunk.x <= WorldGen.Max.x
                && chunk.z >= WorldGen.Min.z && chunk.z <= WorldGen.Max.z)
            .ToHashSet();
        if (affected.Count == 0) return EditorChange.None;

        var change = new EditorChange(affected, new HashSet<Guid>());
        ApplyTerrainChange(change);
        Changed?.Invoke(change);
        return change;
    }

    private void ApplyTerrainChange(EditorChange change)
    {
        var replacements = evaluator.EvaluateChunks(
            Document,
            change.TerrainChunks.Where(chunk =>
                chunk.x >= WorldGen.Min.x && chunk.x <= WorldGen.Max.x
                && chunk.z >= WorldGen.Min.z && chunk.z <= WorldGen.Max.z));
        foreach (var (chunk, replacement) in replacements)
            Terrain.Insert(replacement);
    }

    public EditorOperationDiagnostics Diagnostics()
        => evaluator.Diagnostics(Document);
}
