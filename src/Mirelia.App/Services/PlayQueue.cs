namespace Mirelia.App.Services;

/// <summary>
/// One queue slot: a track id plus a stable identity that survives
/// duplicate track ids in the same queue. <see cref="EntryId"/> is what the
/// engine's opaque <c>user_token</c> carries, so the audible entry can be
/// resolved without scanning for a track id that may appear more than once.
/// </summary>
/// <param name="EntryId">Stable identity of this queue slot, unique within the queue's lifetime.</param>
/// <param name="TrackId">The library track this slot plays.</param>
public readonly record struct QueueEntry(long EntryId, long TrackId);

/// <summary>Outcome of <see cref="PlayQueue.Remove"/>, distinguishing removal of the audible entry.</summary>
public enum QueueRemoveResult
{
    /// <summary>No entry had the given id.</summary>
    NotFound,

    /// <summary>A non-current entry was removed; the current entry is unaffected.</summary>
    RemovedOther,

    /// <summary>
    /// The current entry was removed and another entry took its place (the
    /// one that followed it); the caller should start playing it.
    /// </summary>
    RemovedCurrentHasNext,

    /// <summary>The current entry was removed and none followed; the queue has no current entry now.</summary>
    RemovedCurrentQueueEnded,
}

/// <summary>
/// Ordered, mutable playback queue. Entries carry a stable id distinct from
/// the track id so a track that appears more than once can still be
/// addressed unambiguously. The "current" entry is tracked by that id, not
/// by list position, so reordering never disturbs which entry is playing.
/// </summary>
public sealed class PlayQueue
{
    private readonly List<QueueEntry> _entries = [];

    // Entry ids in shuffled play order. Only meaningful while shuffling;
    // kept separate from _entries so the queue pane still shows the order
    // the user built, and so turning shuffle off restores it for free.
    private readonly List<long> _shuffleOrder = [];
    private readonly Random _random;
    private long _nextEntryId = 1;
    private long? _currentEntryId;
    private bool _shuffle;

    /// <summary>Creates a queue, optionally with a seeded <see cref="Random"/> for tests.</summary>
    public PlayQueue(Random? random = null) => _random = random ?? Random.Shared;

    /// <summary>
    /// Whether <see cref="NextEntry"/> draws from a shuffled order instead of
    /// queue order. Turning it on leaves the current entry playing and
    /// shuffles what remains behind it.
    /// </summary>
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value)
            {
                return;
            }

            _shuffle = value;
            _shuffleOrder.Clear();
            if (value)
            {
                ResetShuffleOrder();
            }
        }
    }

    /// <summary>
    /// Whether the ends of the queue join up, so the entry after the last is
    /// the first again (repeat-all). Repeat-one is not expressed here: it
    /// must not change what "next" means, or pressing Next would replay the
    /// same track forever.
    /// </summary>
    public bool WrapAround { get; set; }

    /// <summary>The queue's entries, in play order.</summary>
    public IReadOnlyList<QueueEntry> Entries => _entries;

    /// <summary>Number of entries.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Index of the current entry within <see cref="Entries"/>, or -1 when
    /// nothing is current. O(n); queues are short enough that this is not a
    /// concern, and it avoids keeping a second piece of state to desync.
    /// </summary>
    public int CurrentIndex => _currentEntryId is { } id ? _entries.FindIndex(e => e.EntryId == id) : -1;

    /// <summary>The current entry, or null when nothing is current.</summary>
    public QueueEntry? Current
    {
        get
        {
            int index = CurrentIndex;
            return index >= 0 ? _entries[index] : null;
        }
    }

    /// <summary>The entry that follows the current one, or null.</summary>
    public QueueEntry? NextEntry => Step(forward: true);

    /// <summary>The entry that precedes the current one, or null.</summary>
    public QueueEntry? PreviousEntry => Step(forward: false);

    /// <summary>
    /// The neighbour of the current entry in the active order. Repeated
    /// calls must agree with each other: the answer is what gets preloaded
    /// into the engine's next slot, and a second, different answer would
    /// leave the cursor pointing at a track that is not the audible one.
    /// </summary>
    private QueueEntry? Step(bool forward)
    {
        if (_currentEntryId is not { } currentId)
        {
            return null;
        }

        if (!_shuffle)
        {
            int index = CurrentIndex;
            int step = index + (forward ? 1 : -1);
            if (step < 0 || step >= _entries.Count)
            {
                if (!WrapAround || _entries.Count == 0)
                {
                    return null;
                }

                step = forward ? 0 : _entries.Count - 1;
            }

            return _entries[step];
        }

        SyncShuffleOrder();
        int position = _shuffleOrder.IndexOf(currentId);
        if (position < 0)
        {
            return null;
        }

        int target = position + (forward ? 1 : -1);
        if (target < 0 || target >= _shuffleOrder.Count)
        {
            if (!WrapAround)
            {
                return null;
            }

            target = forward ? 0 : _shuffleOrder.Count - 1;
        }

        return Find(_shuffleOrder[target]);
    }

    /// <summary>
    /// Rebuilds the shuffled order from scratch, current entry first so
    /// enabling shuffle never interrupts what is playing.
    /// </summary>
    private void ResetShuffleOrder()
    {
        _shuffleOrder.Clear();
        var rest = new List<long>(_entries.Count);
        foreach (QueueEntry entry in _entries)
        {
            if (entry.EntryId == _currentEntryId)
            {
                _shuffleOrder.Add(entry.EntryId);
            }
            else
            {
                rest.Add(entry.EntryId);
            }
        }

        for (int i = rest.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }

        _shuffleOrder.AddRange(rest);
    }

    /// <summary>
    /// Reconciles the shuffled order with the entries lazily, so ordinary
    /// mutations (append, remove, move, clear) need no shuffle-specific
    /// code. Entries the order has not seen land at a random position after
    /// the current one, which keeps them reachable in this pass.
    /// </summary>
    private void SyncShuffleOrder()
    {
        var live = new HashSet<long>(_entries.Count);
        foreach (QueueEntry entry in _entries)
        {
            live.Add(entry.EntryId);
        }

        _shuffleOrder.RemoveAll(id => !live.Contains(id));
        var known = new HashSet<long>(_shuffleOrder);
        int cursor = _currentEntryId is { } id ? _shuffleOrder.IndexOf(id) : -1;
        foreach (QueueEntry entry in _entries)
        {
            if (known.Add(entry.EntryId))
            {
                _shuffleOrder.Insert(_random.Next(cursor + 1, _shuffleOrder.Count + 1), entry.EntryId);
            }
        }
    }

    /// <summary>Looks up an entry by id, or null if it is not in the queue.</summary>
    public QueueEntry? Find(long entryId)
    {
        int index = _entries.FindIndex(e => e.EntryId == entryId);
        return index >= 0 ? _entries[index] : null;
    }

    /// <summary>
    /// Replaces the whole queue (play-now) and marks the entry at
    /// <paramref name="startIndex"/> as current.
    /// </summary>
    public IReadOnlyList<QueueEntry> Replace(IReadOnlyList<long> trackIds, int startIndex)
    {
        _entries.Clear();
        foreach (long trackId in trackIds)
        {
            _entries.Add(new QueueEntry(_nextEntryId++, trackId));
        }

        _currentEntryId = _entries.Count == 0
            ? null
            : _entries[Math.Clamp(startIndex, 0, _entries.Count - 1)].EntryId;
        if (_shuffle)
        {
            ResetShuffleOrder();
        }

        return _entries;
    }

    /// <summary>Inserts tracks right after the current entry, in order ("Play Next").</summary>
    public IReadOnlyList<QueueEntry> InsertNext(IReadOnlyList<long> trackIds)
    {
        if (trackIds.Count == 0)
        {
            return [];
        }

        var inserted = new List<QueueEntry>(trackIds.Count);
        foreach (long trackId in trackIds)
        {
            inserted.Add(new QueueEntry(_nextEntryId++, trackId));
        }

        if (_shuffle)
        {
            // "Play Next" is an explicit promise about ordering, so these go
            // straight after the current entry rather than being scattered
            // by the lazy reconciliation — which is why they are placed
            // before the entries themselves land.
            SyncShuffleOrder();
            int cursor = _currentEntryId is { } id ? _shuffleOrder.IndexOf(id) : -1;
            _shuffleOrder.InsertRange(cursor + 1, inserted.Select(e => e.EntryId));
        }

        _entries.InsertRange(CurrentIndex + 1, inserted);
        return inserted;
    }

    /// <summary>Appends tracks to the end of the queue ("Add to Queue").</summary>
    public IReadOnlyList<QueueEntry> Append(IReadOnlyList<long> trackIds)
    {
        if (trackIds.Count == 0)
        {
            return [];
        }

        var appended = new List<QueueEntry>(trackIds.Count);
        foreach (long trackId in trackIds)
        {
            appended.Add(new QueueEntry(_nextEntryId++, trackId));
        }

        _entries.AddRange(appended);
        return appended;
    }

    /// <summary>Removes one entry by id. See <see cref="QueueRemoveResult"/> for how the current pointer reacts.</summary>
    public QueueRemoveResult Remove(long entryId)
    {
        int index = _entries.FindIndex(e => e.EntryId == entryId);
        if (index < 0)
        {
            return QueueRemoveResult.NotFound;
        }

        bool wasCurrent = _currentEntryId == entryId;
        _entries.RemoveAt(index);
        if (!wasCurrent)
        {
            return QueueRemoveResult.RemovedOther;
        }

        if (index < _entries.Count)
        {
            // The following entry slides into the removed slot; it becomes
            // current so playback has somewhere sane to continue.
            _currentEntryId = _entries[index].EntryId;
            return QueueRemoveResult.RemovedCurrentHasNext;
        }

        _currentEntryId = null;
        return QueueRemoveResult.RemovedCurrentQueueEnded;
    }

    /// <summary>
    /// Moves an entry so it lands at <paramref name="newIndex"/> as counted
    /// in the queue's order <em>before</em> the move — the natural index for
    /// a drag-drop caller to pass, since it can read positions straight off
    /// the pre-drag list without accounting for the shift its own removal
    /// causes. Returns false if the entry does not exist or the move is a
    /// no-op.
    /// </summary>
    public bool Move(long entryId, int newIndex)
    {
        int index = _entries.FindIndex(e => e.EntryId == entryId);
        if (index < 0)
        {
            return false;
        }

        newIndex = Math.Clamp(newIndex, 0, _entries.Count - 1);
        if (index == newIndex)
        {
            return false;
        }

        QueueEntry entry = _entries[index];
        _entries.RemoveAt(index);
        if (newIndex > index)
        {
            newIndex--;
        }

        _entries.Insert(newIndex, entry);
        return true;
    }

    /// <summary>Drops every entry except the current one ("Clear" affordance) — playback is left untouched.</summary>
    public void Clear()
    {
        QueueEntry? current = Current;
        _entries.Clear();
        if (current is { } entry)
        {
            _entries.Add(entry);
            _currentEntryId = entry.EntryId;
        }
        else
        {
            _currentEntryId = null;
        }
    }

    /// <summary>
    /// Empties the queue completely, current entry included — unlike
    /// <see cref="Clear"/>, which is the UI affordance and spares whatever is
    /// playing. Used when the entries stop meaning anything at all, as on a
    /// library switch.
    /// </summary>
    public void RemoveAll()
    {
        _entries.Clear();
        _shuffleOrder.Clear();
        _currentEntryId = null;
    }

    /// <summary>Marks an existing entry as current. Returns false if it is not in the queue.</summary>
    public bool SetCurrent(long entryId)
    {
        if (_entries.FindIndex(e => e.EntryId == entryId) < 0)
        {
            return false;
        }

        _currentEntryId = entryId;
        return true;
    }

    /// <summary>
    /// Clears the "current" pointer without touching the entries — used when
    /// playback stops without the queue itself changing (device/mode reset).
    /// </summary>
    public void ClearCurrent() => _currentEntryId = null;
}
