namespace Demiurge;

/// <summary>Deterministic binary min-heap with decrease-key, keyed by packed navigation cells.</summary>
public sealed class NavHeap
{
    private readonly record struct Entry(long Key, float Priority, long Sequence);

    private readonly List<Entry> entries = [];
    private readonly Dictionary<long, int> positions = [];
    private long nextSequence;

    public int Count => entries.Count;

    public void EnqueueOrDecrease(long key, float priority)
    {
        if (positions.TryGetValue(key, out int index))
        {
            var current = entries[index];
            if (priority >= current.Priority) return;
            entries[index] = current with { Priority = priority };
            BubbleUp(index);
            return;
        }

        int added = entries.Count;
        entries.Add(new Entry(key, priority, nextSequence++));
        positions[key] = added;
        BubbleUp(added);
    }

    public bool TryDequeue(out long key)
    {
        if (entries.Count == 0)
        {
            key = default;
            return false;
        }

        key = entries[0].Key;
        positions.Remove(key);
        int last = entries.Count - 1;
        if (last == 0)
        {
            entries.RemoveAt(last);
            return true;
        }

        entries[0] = entries[last];
        positions[entries[0].Key] = 0;
        entries.RemoveAt(last);
        BubbleDown(0);
        return true;
    }

    private void BubbleUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!Before(entries[index], entries[parent])) break;
            Swap(index, parent);
            index = parent;
        }
    }

    private void BubbleDown(int index)
    {
        while (true)
        {
            int left = index * 2 + 1;
            if (left >= entries.Count) return;
            int right = left + 1;
            int best = right < entries.Count && Before(entries[right], entries[left])
                ? right
                : left;
            if (!Before(entries[best], entries[index])) return;
            Swap(index, best);
            index = best;
        }
    }

    private static bool Before(in Entry a, in Entry b)
        => a.Priority < b.Priority
           || a.Priority == b.Priority && a.Sequence < b.Sequence;

    private void Swap(int a, int b)
    {
        (entries[a], entries[b]) = (entries[b], entries[a]);
        positions[entries[a].Key] = a;
        positions[entries[b].Key] = b;
    }
}
