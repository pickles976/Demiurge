namespace Demiurge.Editor;

public sealed class EditorHistory
{
    private readonly List<IEditorCommand> undo = [];
    private readonly List<IEditorCommand> redo = [];

    public int Capacity { get; }
    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;

    public EditorHistory(int capacity = 512)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        Capacity = capacity;
    }

    public void Record(IEditorCommand command)
    {
        undo.Add(command);
        redo.Clear();
        if (undo.Count > Capacity) undo.RemoveAt(0);
    }

    public IEditorCommand? TakeUndo()
    {
        if (undo.Count == 0) return null;
        int index = undo.Count - 1;
        var command = undo[index];
        undo.RemoveAt(index);
        redo.Add(command);
        return command;
    }

    public IEditorCommand? TakeRedo()
    {
        if (redo.Count == 0) return null;
        int index = redo.Count - 1;
        var command = redo[index];
        redo.RemoveAt(index);
        undo.Add(command);
        return command;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
    }
}
