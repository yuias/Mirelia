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

/// <summary>One disc's row range inside a run.</summary>
/// <param name="DiscNo">Disc number, or null when the tracks carry none.</param>
/// <param name="StartIndex">Index into the id array the run was built from.</param>
/// <param name="Count">Number of rows in the span.</param>
public sealed record DiscSpan(int? DiscNo, int StartIndex, int Count);

/// <summary>
/// A contiguous run of rows belonging to one group, with the aggregates a
/// group header shows. Titles are null when the group field is unset on the
/// tracks; the presentation layer decides how to name that.
/// </summary>
/// <param name="GroupId">Album or artist id; 0 when the group field is null.</param>
/// <param name="Title">Album or artist name, or null when unset.</param>
/// <param name="Subtitle">Album artist name for album groups; null otherwise.</param>
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

        List<RunBuilder> runs = ReadRuns(db, ids, field);
        if (runs.Count == 0)
        {
            return [];
        }

        Dictionary<long, (string? Title, string? Subtitle)> names =
            LoadGroupNames(db, runs.Select(r => r.GroupId), field);
        Dictionary<long, string> genres = LoadGenreNames(db);

        var result = new List<TrackGroupRun>(runs.Count);
        foreach (RunBuilder run in runs)
        {
            (string? title, string? subtitle) = names.GetValueOrDefault(run.GroupId);
            result.Add(new TrackGroupRun(
                run.GroupId,
                title,
                subtitle,
                run.TopGenreId is { } genreId ? genres.GetValueOrDefault(genreId) : null,
                run.Year,
                run.StartIndex,
                run.Count,
                run.DurationMs,
                run.MaxAddedUnix,
                run.CoverTrackId,
                run.Discs));
        }

        return result;
    }

    /// <summary>
    /// Single pass over the ids reading only numeric columns — names are
    /// looked up per group afterwards, so a 100k-row view never materializes
    /// 100k album strings.
    /// </summary>
    private static List<RunBuilder> ReadRuns(
        LibraryDatabase db, IReadOnlyList<long> ids, TrackGroupField field)
    {
        string groupColumn = field switch
        {
            // A track with no album artist still belongs to its artist; that
            // is what a listener means by "grouped by artist".
            TrackGroupField.AlbumArtist => "COALESCE(t.album_artist_id, t.artist_id)",
            _ => "t.album_id",
        };

        // json_each keeps this a single statement regardless of id count, and
        // the LEFT JOIN keeps one output row per input id even when a track
        // vanished, which is what makes the run indices trustworthy.
        string json = "[" + string.Join(',', ids) + "]";
        using SqliteCommand cmd = db.Command($"""
            SELECT t.id, COALESCE({groupColumn}, 0), t.disc_no, t.duration_ms,
                   t.added_unix, t.date_key, t.year, t.genre_id
            FROM json_each($ids) je
            LEFT JOIN track t ON t.id = je.value
            ORDER BY je.key
            """,
            ("$ids", json));
        using SqliteDataReader reader = cmd.ExecuteReader();

        var runs = new List<RunBuilder>();
        RunBuilder? current = null;
        int index = 0;
        while (reader.Read())
        {
            long groupId = reader.GetInt64(1);
            if (current is null || current.GroupId != groupId)
            {
                current = new RunBuilder(groupId, index);
                runs.Add(current);
            }

            current.Add(
                trackId: reader.IsDBNull(0) ? null : reader.GetInt64(0),
                index: index,
                discNo: reader.IsDBNull(2) ? null : reader.GetInt32(2),
                durationMs: reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                addedUnix: reader.IsDBNull(4) ? null : reader.GetInt64(4),
                dateKey: reader.IsDBNull(5) ? null : reader.GetInt64(5),
                year: reader.IsDBNull(6) ? null : reader.GetInt32(6),
                genreId: reader.IsDBNull(7) ? null : reader.GetInt64(7));
            index++;
        }

        foreach (RunBuilder run in runs)
        {
            run.Close();
        }

        return runs;
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

    /// <summary>Accumulates one run's aggregates while the reader walks it.</summary>
    private sealed class RunBuilder(long groupId, int startIndex)
    {
        private readonly Dictionary<long, int> _genreCounts = [];
        private readonly List<DiscSpan> _discs = [];
        private int _discStart = startIndex;
        private int _discCount;
        private int? _discNo;
        private bool _discOpen;
        private long? _maxDateKey;
        private int? _maxYear;
        private int _topGenreCount;

        public long GroupId { get; } = groupId;

        public int StartIndex { get; } = startIndex;

        public int Count { get; private set; }

        public long DurationMs { get; private set; }

        public long MaxAddedUnix { get; private set; }

        public long CoverTrackId { get; private set; }

        public long? TopGenreId { get; private set; }

        public IReadOnlyList<DiscSpan> Discs => _discs;

        /// <summary>The run's release year: its latest date, matching how AlbumByYear ranks it.</summary>
        public int? Year => _maxDateKey is { } key ? (int)(key / 10000) : _maxYear;

        public void Add(
            long? trackId, int index, int? discNo, long durationMs,
            long? addedUnix, long? dateKey, int? year, long? genreId)
        {
            if (!_discOpen || discNo != _discNo)
            {
                CloseDisc();
                _discNo = discNo;
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

            if (genreId is { } genre)
            {
                // Most common genre wins; ">" (not ">=") makes the first one
                // seen win a tie, so the header stays stable.
                int count = _genreCounts[genre] = _genreCounts.GetValueOrDefault(genre) + 1;
                if (count > _topGenreCount)
                {
                    _topGenreCount = count;
                    TopGenreId = genre;
                }
            }
        }

        /// <summary>Seals the last open disc span once the run's rows are in.</summary>
        public void Close() => CloseDisc();

        private void CloseDisc()
        {
            if (_discOpen && _discCount > 0)
            {
                _discs.Add(new DiscSpan(_discNo, _discStart, _discCount));
            }

            _discOpen = false;
        }
    }
}
