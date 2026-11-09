using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;

namespace Mirelia.Library.Querying;

/// <summary>The field a grouped layout groups tracks by.</summary>
public enum TrackGroupField
{
    /// <summary>Group by album (<c>track.album_id</c>).</summary>
    Album,

    /// <summary>Group by album artist, falling back to the track artist.</summary>
    AlbumArtist,
}

/// <summary>One section's row range inside a run: a disc, or an album in an artist group.</summary>
/// <param name="DiscNo">Disc number, or null when the tracks carry none or the run is an artist's.</param>
/// <param name="StartIndex">Index into the id array the run was built from.</param>
/// <param name="Count">Number of rows in the span.</param>
/// <param name="AlbumTitle">The album this span covers in an artist group; otherwise null.</param>
public sealed record DiscSpan(int? DiscNo, int StartIndex, int Count, string? AlbumTitle = null);

/// <summary>
/// A contiguous run of rows belonging to one group, with the aggregates a
/// group header shows. Titles are null when the group field is unset on the
/// tracks; the presentation layer decides how to name that.
/// </summary>
/// <param name="GroupId">Album or artist id; 0 when the group field is null.</param>
/// <param name="Title">Album or artist name, or null when unset.</param>
/// <param name="Subtitle">Album artist name for album groups; null otherwise.</param>
/// <param name="Artist">Most common track artist in the run, or null when none is tagged.</param>
/// <param name="Genre">Most common genre in the run, or null when none is tagged.</param>
/// <param name="Year">Year of the run's latest release date, or null when undated.</param>
/// <param name="StartIndex">Index into the id array the run was built from.</param>
/// <param name="Count">Number of rows in the run.</param>
/// <param name="DurationMs">Total playing time of the run.</param>
/// <param name="MaxAddedUnix">When the run's most recent track entered the library.</param>
/// <param name="CoverTrackId">First present track of the run: the artwork source.</param>
/// <param name="Discs">The run's disc spans, in row order.</param>
public sealed record TrackGroupRun(
    long GroupId,
    string? Title,
    string? Subtitle,
    string? Artist,
    string? Genre,
    int? Year,
    int StartIndex,
    int Count,
    long DurationMs,
    long MaxAddedUnix,
    long CoverTrackId,
    IReadOnlyList<DiscSpan> Discs);

/// <summary>
/// Cuts an ordered id array into the contiguous group runs a grouped layout
/// renders as blocks. The order comes from SQL (<see cref="TrackPager"/>);
/// this only reads it, so the "never sort in memory" invariant holds.
///
/// A grouped layout always sorts group-contiguously, so each group yields one
/// run. Any other order is still handled — the group simply yields several
/// runs — rather than treated as an error.
/// </summary>
public static class TrackGrouper
{
    /// <summary>Group id standing for "the group field is null on these tracks".</summary>
    public const long NoGroup = 0;

    /// <summary>
    /// Builds the group runs covering <paramref name="ids"/> in order. Run
    /// indices address <paramref name="ids"/> itself: ids with no track row
    /// (deleted since the array was built) still occupy their slot, so
    /// indices never drift out of step with the caller's array.
    /// </summary>
    public static List<TrackGroupRun> LoadRuns(
        LibraryDatabase db, IReadOnlyList<long> ids, TrackGroupField field)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return Materialize(db, ReadRuns(db, ids, field), field);
    }

    /// <summary>Resolves each run's names once the pass over the rows is done.</summary>
    private static List<TrackGroupRun> Materialize(
        LibraryDatabase db, List<RunBuilder> runs, TrackGroupField field)
    {
        if (runs.Count == 0)
        {
            return [];
        }

        Dictionary<long, (string? Title, string? Subtitle)> names =
            LoadGroupNames(db, runs.Select(r => r.GroupId), field);
        Dictionary<long, string> artists =
            LoadArtistNames(db, runs.Select(r => r.TopArtistId));
        Dictionary<long, string> genres = LoadGenreNames(db);
        Dictionary<long, (string? Title, string? Subtitle)> albums = field == TrackGroupField.AlbumArtist
            ? LoadGroupNames(db, runs.SelectMany(r => r.SectionAlbumIds), TrackGroupField.Album)
            : [];

        var result = new List<TrackGroupRun>(runs.Count);
        foreach (RunBuilder run in runs)
        {
            (string? title, string? subtitle) = names.GetValueOrDefault(run.GroupId);
            result.Add(new TrackGroupRun(
                run.GroupId,
                title,
                subtitle,
                run.TopArtistId is { } artistId ? artists.GetValueOrDefault(artistId) : null,
                run.TopGenreId is { } genreId ? genres.GetValueOrDefault(genreId) : null,
                run.Year,
                run.StartIndex,
                run.Count,
                run.DurationMs,
                run.MaxAddedUnix,
                run.CoverTrackId,
                run.BuildDiscs(albums)));
        }

        return result;
    }

    /// <summary>
    /// Orders a view and cuts it into runs in one statement, returning both
    /// the ordered ids and their runs.
    ///
    /// The alternative — sort, then walk the sorted ids again to group them —
    /// pays for two full passes over the view, and the second was measured at
    /// nearly two thirds of the cost of loading an album-grouped view.
    /// </summary>
    /// <param name="ids">
    /// The view's ids, or null for the whole library (which then needs no id
    /// array to be built, shipped and re-parsed at all).
    /// </param>
    public static (long[] Ids, List<TrackGroupRun> Runs) LoadOrderedRuns(
        LibraryDatabase db,
        IReadOnlyList<long>? ids,
        TrackSortOrder order,
        TrackGroupField field)
        => LoadOrderedRuns(db, ids, SortPresets.SpecFor(order), field);

    /// <summary>
    /// Orders a view by an arbitrary sort order and cuts it into runs in one
    /// statement. A null <paramref name="spec"/> keeps the input's own order —
    /// the only order the preset clauses cannot express.
    /// </summary>
    /// <param name="db">The library.</param>
    /// <param name="ids">The view's ids, or null for the whole library.</param>
    /// <param name="spec">The sort order, or null to keep the input order.</param>
    /// <param name="field">What the layout groups by.</param>
    public static (long[] Ids, List<TrackGroupRun> Runs) LoadOrderedRuns(
        LibraryDatabase db,
        IReadOnlyList<long>? ids,
        SortSpec? spec,
        TrackGroupField field)
    {
        if (ids is { Count: 0 })
        {
            return ([], []);
        }

        (long[] ordered, List<RunBuilder> runs) = ReadRuns(db, ids, field, spec, ordering: true);
        return (ordered, Materialize(db, runs, field));
    }

    /// <summary>
    /// Single pass over the ids reading only numeric columns — names are
    /// looked up per group afterwards, so a 100k-row view never materializes
    /// 100k album strings.
    /// </summary>
    private static List<RunBuilder> ReadRuns(
        LibraryDatabase db, IReadOnlyList<long> ids, TrackGroupField field)
        => ReadRuns(db, ids, field, spec: null, ordering: false).Runs;

    private static (long[] Ids, List<RunBuilder> Runs) ReadRuns(
        LibraryDatabase db,
        IReadOnlyList<long>? ids,
        TrackGroupField field,
        SortSpec? spec,
        bool ordering)
    {
        string groupColumn = field switch
        {
            // A track with no album artist still belongs to its artist; that
            // is what a listener means by "grouped by artist".
            TrackGroupField.AlbumArtist => "COALESCE(t.album_artist_id, t.artist_id)",
            _ => "t.album_id",
        };

        // Ordering here means the caller does not sort first and walk the
        // result again. A spec-less sort is the input's own order, which no
        // ORDER BY over fields can express — only json_each's index can.
        bool sorting = ordering && spec is not null;
        string orderBy = sorting
            ? TrackPager.OrderClauseFor(spec!, field)
            : ids is not null ? "je.key" : "t.id";

        // Only a compiled clause reaches for the joined tables.
        string joins = sorting
            ? """

              LEFT JOIN artist ar ON ar.id = t.artist_id
              LEFT JOIN artist aa ON aa.id = t.album_artist_id
              LEFT JOIN album al ON al.id = t.album_id
              LEFT JOIN genre g ON g.id = t.genre_id
              """
            : string.Empty;

        // Column 0 names the row's id and column 1 is the track behind it, and
        // they differ: a LEFT JOIN keeps one output row per input id even when
        // the track vanished, and that row still has to name the id it stood
        // for or every later index would shift.
        string source = ids is null
            ? "FROM track t"
            : """
              FROM json_each($ids) je
              LEFT JOIN track t ON t.id = je.value
              """;
        string idColumn = ids is null ? "t.id" : "je.value";

        string sql = $"""
            SELECT {idColumn}, t.id, COALESCE({groupColumn}, 0), t.disc_no, t.duration_ms,
                   t.added_unix, t.date_key, t.year, t.genre_id, t.artist_id, t.album_id
            {source}{joins}
            ORDER BY {orderBy}
            """;

        // json_each keeps this a single statement regardless of id count.
        using SqliteCommand cmd = ids is null
            ? db.Command(sql)
            : db.Command(sql, ("$ids", "[" + string.Join(',', ids) + "]"));
        using SqliteDataReader reader = cmd.ExecuteReader();

        var ordered = new List<long>(ids?.Count ?? 0);
        var runs = new List<RunBuilder>();
        RunBuilder? current = null;
        int index = 0;
        while (reader.Read())
        {
            ordered.Add(reader.GetInt64(0));
            long groupId = reader.GetInt64(2);
            if (current is null || current.GroupId != groupId)
            {
                current = new RunBuilder(groupId, index);
                runs.Add(current);
            }

            // An artist's block is divided by album. Disc numbers repeat from
            // one album to the next, so they cannot divide it.
            bool byAlbum = field == TrackGroupField.AlbumArtist;
            current.Add(
                trackId: reader.IsDBNull(1) ? null : reader.GetInt64(1),
                index: index,
                discNo: byAlbum || reader.IsDBNull(3) ? null : reader.GetInt32(3),
                sectionAlbumId: !byAlbum || reader.IsDBNull(10) ? null : reader.GetInt64(10),
                durationMs: reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                addedUnix: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                dateKey: reader.IsDBNull(6) ? null : reader.GetInt64(6),
                year: reader.IsDBNull(7) ? null : reader.GetInt32(7),
                genreId: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                artistId: reader.IsDBNull(9) ? null : reader.GetInt64(9));
            index++;
        }

        foreach (RunBuilder run in runs)
        {
            run.Close();
        }

        return ([.. ordered], runs);
    }

    /// <summary>Group display names, looked up once per distinct group id.</summary>
    private static Dictionary<long, (string? Title, string? Subtitle)> LoadGroupNames(
        LibraryDatabase db, IEnumerable<long> groupIds, TrackGroupField field)
    {
        long[] distinct = [.. groupIds.Where(id => id != NoGroup).Distinct()];
        var result = new Dictionary<long, (string? Title, string? Subtitle)>(distinct.Length);
        if (distinct.Length == 0)
        {
            return result;
        }

        foreach (long[] chunk in distinct.Chunk(512))
        {
            string placeholders = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using SqliteCommand cmd = db.Command(field == TrackGroupField.AlbumArtist
                ? $"SELECT ar.id, ar.name, NULL FROM artist ar WHERE ar.id IN ({placeholders})"
                : $"""
                   SELECT al.id, al.name, aa.name FROM album al
                   LEFT JOIN artist aa ON aa.id = al.album_artist_id
                   WHERE al.id IN ({placeholders})
                   """);
            for (int i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetInt64(0)] = (
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        return result;
    }

    /// <summary>Names of the runs' representative artists, one lookup for all of them.</summary>
    private static Dictionary<long, string> LoadArtistNames(
        LibraryDatabase db, IEnumerable<long?> artistIds)
    {
        long[] distinct = [.. artistIds.OfType<long>().Distinct()];
        var result = new Dictionary<long, string>(distinct.Length);
        foreach (long[] chunk in distinct.Chunk(512))
        {
            string placeholders = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using SqliteCommand cmd = db.Command(
                $"SELECT id, name FROM artist WHERE id IN ({placeholders})");
            for (int i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetInt64(0)] = reader.GetString(1);
            }
        }

        return result;
    }

    /// <summary>
    /// The whole genre table. It holds tens of rows, not thousands, so one
    /// unfiltered read beats chunking the ids we happened to see.
    /// </summary>
    private static Dictionary<long, string> LoadGenreNames(LibraryDatabase db)
    {
        using SqliteCommand cmd = db.Command("SELECT id, name FROM genre");
        using SqliteDataReader reader = cmd.ExecuteReader();
        var result = new Dictionary<long, string>();
        while (reader.Read())
        {
            result[reader.GetInt64(0)] = reader.GetString(1);
        }

        return result;
    }

    /// <summary>
    /// Remembers the most common id fed to it. Ties go to the id seen first,
    /// so a header label does not flip between equally common values.
    /// </summary>
    private sealed class TopCounter
    {
        private readonly Dictionary<long, int> _counts = [];
        private int _best;

        public long? TopId { get; private set; }

        public void Add(long? id)
        {
            if (id is not { } value)
            {
                return;
            }

            int count = _counts[value] = _counts.GetValueOrDefault(value) + 1;
            if (count > _best)
            {
                _best = count;
                TopId = value;
            }
        }
    }

    /// <summary>Accumulates one run's aggregates while the reader walks it.</summary>
    private sealed class RunBuilder(long groupId, int startIndex)
    {
        private readonly TopCounter _genres = new();
        private readonly TopCounter _artists = new();
        private readonly List<(DiscSpan Span, long? AlbumId)> _discs = [];
        private int _discStart = startIndex;
        private int _discCount;
        private int? _discNo;
        private long? _discAlbumId;
        private bool _discOpen;
        private long? _maxDateKey;
        private int? _maxYear;

        public long GroupId { get; } = groupId;

        public int StartIndex { get; } = startIndex;

        public int Count { get; private set; }

        public long DurationMs { get; private set; }

        public long MaxAddedUnix { get; private set; }

        public long CoverTrackId { get; private set; }

        public long? TopGenreId => _genres.TopId;

        public long? TopArtistId => _artists.TopId;

        public IEnumerable<long> SectionAlbumIds => _discs.Select(d => d.AlbumId).OfType<long>();

        public List<DiscSpan> BuildDiscs(Dictionary<long, (string? Title, string? Subtitle)> albums)
            =>
            [
                .. _discs.Select(d => d.AlbumId is { } albumId
                    ? d.Span with { AlbumTitle = albums.GetValueOrDefault(albumId).Title }
                    : d.Span),
            ];

        /// <summary>The run's release year: its latest date, matching how AlbumByYear ranks it.</summary>
        public int? Year => _maxDateKey is { } key ? (int)(key / 10000) : _maxYear;

        public void Add(
            long? trackId, int index, int? discNo, long? sectionAlbumId, long durationMs,
            long? addedUnix, long? dateKey, int? year, long? genreId, long? artistId)
        {
            if (!_discOpen || discNo != _discNo || sectionAlbumId != _discAlbumId)
            {
                CloseDisc();
                _discNo = discNo;
                _discAlbumId = sectionAlbumId;
                _discStart = index;
                _discCount = 0;
                _discOpen = true;
            }

            _discCount++;
            Count++;
            DurationMs += durationMs;

            if (CoverTrackId == 0 && trackId is { } id)
            {
                CoverTrackId = id;
            }

            if (addedUnix is { } added && added > MaxAddedUnix)
            {
                MaxAddedUnix = added;
            }

            if (dateKey is { } key && (_maxDateKey is null || key > _maxDateKey))
            {
                _maxDateKey = key;
            }

            if (year is { } y && (_maxYear is null || y > _maxYear))
            {
                _maxYear = y;
            }

            _genres.Add(genreId);
            _artists.Add(artistId);
        }

        /// <summary>Seals the last open disc span once the run's rows are in.</summary>
        public void Close() => CloseDisc();

        private void CloseDisc()
        {
            if (_discOpen && _discCount > 0)
            {
                _discs.Add((new DiscSpan(_discNo, _discStart, _discCount), _discAlbumId));
            }

            _discOpen = false;
        }
    }
}
