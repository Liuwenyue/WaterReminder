namespace WaterReminder.Services;

/// <summary>
/// Bounded FIFO set of PushWard notification ids used to drop repeated Answers.
/// When full it evicts only the oldest id (instead of clearing the whole set), so a busy
/// stream keeps deduplicating recent notifications. Not thread-safe; callers provide the lock.
/// </summary>
public sealed class SeenIdCache
{
    private readonly int _capacity;
    private readonly HashSet<string> _ids = [];
    private readonly Queue<string> _order = new();

    public SeenIdCache(int capacity = 1000)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count => _ids.Count;

    /// <summary>Adds an id. Returns false when it was already seen (duplicate).</summary>
    public bool Add(string id)
    {
        if (!_ids.Add(id)) return false;
        _order.Enqueue(id);
        while (_ids.Count > _capacity) _ids.Remove(_order.Dequeue());
        return true;
    }

    public bool Contains(string id) => _ids.Contains(id);
}
