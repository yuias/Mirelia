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
    private long _nextEntryId = 1;
    private long? _currentEntryId;

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
    public QueueEntry? NextEntry
    {
        get
        {
            int index = CurrentIndex;
            return index >= 0 && index + 1 < _entries.Count ? _entries[index + 1] : null;
        }
    }

    /// <summary>The entry that precedes the current one, or null.</summary>
    public QueueEntry? PreviousEntry
    {
        get
        {
            int index = CurrentIndex;
            return index > 0 ? _entries[index - 1] : null;
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
