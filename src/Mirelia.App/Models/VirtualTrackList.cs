using System.Collections;

namespace Mirelia.App.Models;

/// <summary>
/// The track-list virtualization from kickoff §4.3, adapted to DataGrid's
/// collection view (which snapshots the source list at load): rows exist as
/// cheap placeholder stubs for the whole result set, but their *content* is
/// hydrated page-by-page only when rows become visible
/// (<see cref="NotifyVisible"/>, driven by DataGrid.LoadingRow) and is
/// evicted LRU so memory tracks the viewport, not the library.
///
/// All members must be called on the UI thread; hydration results come
/// back through <see cref="ApplyPage"/> on the UI thread as well.
/// </summary>
public sealed class VirtualTrackList : IReadOnlyList<TrackRow>, IList
{
    /// <summary>Rows per hydration request.</summary>
    public const int PageSize = 256;

    private const int MaxHydratedPages = 32;

    private readonly TrackRow[] _rows;
    private readonly long[] _ids;
    private readonly Action<int, long[]> _requestPage;
    private readonly HashSet<int> _hydrated = [];
    private readonly HashSet<int> _inFlight = [];
    private readonly LinkedList<int> _lru = [];
    private readonly List<int> _nowPlayingIndexes = [];
    private long? _nowPlayingId;

    /// <summary>Creates a list over an id array.</summary>
    /// <param name="ids">Result-set ids, already sorted by the database.</param>
    /// <param name="requestPage">
    /// Called once per page with (pageIndex, pageIds) when hydration is
    /// needed; the callee eventually calls <see cref="ApplyPage"/>.
    /// </param>
    public VirtualTrackList(long[] ids, Action<int, long[]> requestPage)
    {
        _ids = ids;
        _requestPage = requestPage;
        _rows = new TrackRow[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            _rows[i] = new TrackRow { Id = ids[i], Index = i };
        }
    }

    /// <inheritdoc />
    public int Count => _rows.Length;

    /// <summary>
    /// Pages hydrated so far. Diagnostic: with visibility-driven hydration
    /// this stays near viewport-size ÷ <see cref="PageSize"/>.
    /// </summary>
    public int PagesRequested { get; private set; }

    /// <summary>The underlying id array (no copy).</summary>
    public long[] Ids => _ids;

    /// <summary>The track id at a row index.</summary>
    public long IdAt(int index) => _ids[index];

    /// <summary>The id last passed to <see cref="SetNowPlaying"/>.</summary>
    public long? NowPlayingId => _nowPlayingId;

    /// <summary>
    /// Marks every row holding <paramref name="trackId"/> (null clears). With
    /// the same id as before, only the play state of the marked rows changes.
    /// </summary>
    public void SetNowPlaying(long? trackId, bool isPlaying)
    {
        NowPlayingState state = isPlaying ? NowPlayingState.Playing : NowPlayingState.Paused;
        if (trackId == _nowPlayingId)
        {
            if (trackId is not null)
            {
                foreach (int index in _nowPlayingIndexes)
                {
                    _rows[index].NowPlaying = state;
                }
            }

            return;
        }

        foreach (int index in _nowPlayingIndexes)
        {
            _rows[index].NowPlaying = NowPlayingState.None;
        }

        _nowPlayingIndexes.Clear();
        _nowPlayingId = trackId;
        if (trackId is not { } id)
        {
            return;
        }

        // Every row exists from construction, so flagging here covers rows that
        // are not hydrated or realized yet.
        for (int i = 0; i < _ids.Length; i++)
        {
            if (_ids[i] == id)
            {
                _rows[i].NowPlaying = state;
                _nowPlayingIndexes.Add(i);
            }
        }
    }

    /// <summary>
    /// First index at or after <paramref name="startIndex"/> holding
    /// <paramref name="trackId"/>, wrapping to the start; -1 if absent.
    /// <paramref name="startIndex"/> is clamped to [0, Count].
    /// </summary>
    public int IndexOf(long trackId, int startIndex = 0)
    {
        int start = Math.Clamp(startIndex, 0, _ids.Length);
        for (int i = start; i < _ids.Length; i++)
        {
            if (_ids[i] == trackId)
            {
                return i;
            }
        }

        for (int i = 0; i < start; i++)
        {
            if (_ids[i] == trackId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <inheritdoc />
    public TrackRow this[int index] => _rows[index];

    /// <summary>
    /// Reports that a row was realized on screen; hydrates its page if
    /// needed. Called from DataGrid.LoadingRow.
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
        int count = Math.Min(PageSize, _ids.Length - start);
        PagesRequested++;
        _requestPage(page, _ids[start..(start + count)]);
    }

    /// <summary>Fills a page's rows and records it in the LRU window.</summary>
    public void ApplyPage(
        int page, IReadOnlyDictionary<long, Mirelia.Library.Querying.TrackPageRow> data)
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
            TrackRow row = _rows[start + i];
            if (data.TryGetValue(row.Id, out Mirelia.Library.Querying.TrackPageRow? hydrated))
            {
                row.Apply(hydrated);
            }
        }

        _hydrated.Add(page);
        _lru.AddFirst(page);
        EvictIfNeeded();
    }

    private void Touch(int page)
    {
        // The LRU list is at most MaxHydratedPages long; a linear scan is cheap.
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

            // Return the rows to placeholders so the evicted page's strings
            // become collectable; a revisit re-hydrates via NotifyVisible.
            int start = page * PageSize;
            int count = Math.Min(PageSize, _rows.Length - start);
            for (int i = 0; i < count; i++)
            {
                _rows[start + i].Reset();
            }
        }
    }

    /// <inheritdoc />
    public IEnumerator<TrackRow> GetEnumerator()
        => ((IEnumerable<TrackRow>)_rows).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // Non-generic IList: DataGrid's collection view uses it for index-based
    // access and selection bookkeeping. Mutation is not supported.

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

    bool IList.Contains(object? value)
        => value is TrackRow row && row.Index < _rows.Length && _rows[row.Index] == row;

    int IList.IndexOf(object? value)
        => value is TrackRow row && row.Index < _rows.Length && _rows[row.Index] == row
            ? row.Index
            : -1;

    void IList.Insert(int index, object? value) => throw new NotSupportedException();

    void IList.Remove(object? value) => throw new NotSupportedException();

    void IList.RemoveAt(int index) => throw new NotSupportedException();

    void ICollection.CopyTo(Array array, int index)
        => _rows.CopyTo(array, index);
}
