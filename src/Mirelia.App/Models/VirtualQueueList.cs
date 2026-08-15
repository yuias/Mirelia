using System.Collections;
using Mirelia.App.Services;
using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

/// <summary>
/// The queue pane's rows, virtualized the way the track grid is: a cheap
/// placeholder per entry, with display text hydrated page-wise as rows are
/// realized and evicted LRU behind them.
///
/// The queue is as long as the view playback started from — playing one track
/// out of the Music view queues the whole library — so the pane cannot afford
/// to hydrate or retain all of it.
///
/// All members must be called on the UI thread.
/// </summary>
public sealed class VirtualQueueList : IReadOnlyList<QueueRowViewModel>, IList
{
    /// <summary>Rows per hydration request.</summary>
    public const int PageSize = 128;

    // The pane is one narrow column; a few screens' worth of text is plenty.
    private const int MaxHydratedPages = 8;

    private readonly QueueRowViewModel[] _rows;
    private readonly Action<int, long[]> _requestPage;
    private readonly HashSet<int> _hydrated = [];
    private readonly HashSet<int> _inFlight = [];
    private readonly LinkedList<int> _lru = [];
    private int _currentIndex = -1;

    /// <summary>Creates the rows for a queue snapshot.</summary>
    /// <param name="entries">The queue, in play order.</param>
    /// <param name="requestPage">
    /// Called once per page with (pageIndex, trackIds) when hydration is
    /// needed; the callee eventually calls <see cref="ApplyPage"/>.
    /// </param>
    public VirtualQueueList(IReadOnlyList<QueueEntry> entries, Action<int, long[]> requestPage)
    {
        _requestPage = requestPage;
        _rows = new QueueRowViewModel[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            _rows[i] = new QueueRowViewModel
            {
                EntryId = entries[i].EntryId,
                TrackId = entries[i].TrackId,
                Index = i,
                IsAlternate = i % 2 == 1,
            };
        }
    }

    /// <inheritdoc />
    public int Count => _rows.Length;

    /// <inheritdoc />
    public QueueRowViewModel this[int index] => _rows[index];

    /// <summary>
    /// Reports that a row was realized on screen; hydrates its page if needed.
    /// </summary>
    public void NotifyVisible(int index)
    {
        if (index < 0 || index >= _rows.Length)
        {
            return;
        }

        int page = index / PageSize;
        if (_hydrated.Contains(page))
        {
            Touch(page);
            return;
        }

        if (!_inFlight.Add(page))
        {
            return;
        }

        int start = page * PageSize;
        int count = Math.Min(PageSize, _rows.Length - start);
        var trackIds = new long[count];
        for (int i = 0; i < count; i++)
        {
            trackIds[i] = _rows[start + i].TrackId;
        }

        _requestPage(page, trackIds);
    }

    /// <summary>Fills a page's rows and records it in the LRU window.</summary>
    public void ApplyPage(int page, IReadOnlyDictionary<long, TrackPageRow> data)
    {
        _inFlight.Remove(page);
        int start = page * PageSize;
        int count = Math.Min(PageSize, _rows.Length - start);
        if (count <= 0)
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            QueueRowViewModel row = _rows[start + i];
            row.Apply(data.GetValueOrDefault(row.TrackId));
        }

        _hydrated.Add(page);
        _lru.AddFirst(page);
        EvictIfNeeded();
    }

    /// <summary>
    /// Marks the audible entry and returns its row. Only the two rows whose
    /// state changes are touched, so this stays O(1) on a library-sized queue.
    /// </summary>
    public QueueRowViewModel? SetCurrent(long? entryId)
    {
        if (_currentIndex >= 0 && _currentIndex < _rows.Length)
        {
            _rows[_currentIndex].IsCurrent = false;
        }

        _currentIndex = entryId is { } id ? IndexOfEntry(id) : -1;
        if (_currentIndex < 0)
        {
            return null;
        }

        QueueRowViewModel row = _rows[_currentIndex];
        row.IsCurrent = true;

        // The audible row is the one the pane scrolls to, so it is worth
        // hydrating even if it was never realized.
        NotifyVisible(_currentIndex);
        return row;
    }

    /// <summary>Index of a queue entry, or -1 when it is no longer queued.</summary>
    public int IndexOfEntry(long entryId)
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            if (_rows[i].EntryId == entryId)
            {
                return i;
            }
        }

        return -1;
    }

    private void Touch(int page)
    {
        LinkedListNode<int>? node = _lru.Find(page);
        if (node is not null && node != _lru.First)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private void EvictIfNeeded()
    {
        while (_hydrated.Count > MaxHydratedPages && _lru.Last is { } oldest)
        {
            int page = oldest.Value;
            _lru.RemoveLast();
            _hydrated.Remove(page);

            int start = page * PageSize;
            int count = Math.Min(PageSize, _rows.Length - start);
            for (int i = 0; i < count; i++)
            {
                _rows[start + i].Reset();
            }
        }
    }

    /// <inheritdoc />
    public IEnumerator<QueueRowViewModel> GetEnumerator()
        => ((IEnumerable<QueueRowViewModel>)_rows).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Non-generic IList: the ListBox uses it for index-based access and
    // selection bookkeeping. Mutation is not supported.

    bool IList.IsFixedSize => true;

    bool IList.IsReadOnly => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    object? IList.this[int index]
    {
        get => _rows[index];
        set => throw new NotSupportedException();
    }

    int IList.Add(object? value) => throw new NotSupportedException();

    void IList.Clear() => throw new NotSupportedException();

    bool IList.Contains(object? value) => ((IList)this).IndexOf(value) >= 0;

    int IList.IndexOf(object? value)
        => value is QueueRowViewModel row && row.Index < _rows.Length && _rows[row.Index] == row
            ? row.Index
            : -1;

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index) => _rows.CopyTo(array, index);
}
