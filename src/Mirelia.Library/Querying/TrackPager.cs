using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;

namespace Mirelia.Library.Querying;

/// <summary>Predefined, DB-backed sort orders for browsing.</summary>
public enum TrackSortOrder
{
    /// <summary>Artist sort key, then album, disc, track.</summary>
    Artist,

    /// <summary>Album sort key, then disc, track.</summary>
    Album,

    /// <summary>Title sort key.</summary>
    Title,

    /// <summary>Most recently added first.</summary>
    RecentlyAdded,

    /// <summary>Release year, then album, disc, track.</summary>
    Year,

    /// <summary>Albums ordered by their (max) year; tracks in album order.</summary>
    AlbumByYear,

    /// <summary>Albums ordered by when they arrived; tracks in album order.</summary>
    AlbumByRecentlyAdded,

    /// <summary>The playlist's own entry order (static playlists / smart DSL order).</summary>
    PlaylistOrder,

    /// <summary>Album artist sort key, then album, disc, track.</summary>
    AlbumArtist,
}

/// <summary>A header-click column sort: grid column key plus direction.</summary>
/// <param name="ColumnKey">Stable column key (matches the app's column registry).</param>
/// <param name="Descending">True for descending.</param>
public sealed record ColumnSort(string ColumnKey, bool Descending);

/// <summary>A fully hydrated grid row.</summary>
/// <param name="Id">Track id.</param>
/// <param name="Title">Title (falls back to file name at scan time).</param>
/// <param name="Artist">Artist name, or null.</param>
/// <param name="Album">Album name, or null.</param>
/// <param name="AlbumArtist">Album artist name, or null.</param>
/// <param name="Genre">Genre name, or null.</param>
/// <param name="TrackNo">Track number, or null.</param>
/// <param name="Year">Release year, or null.</param>
/// <param name="DateText">Raw release date string, or null.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
/// <param name="Codec">Canonical codec id.</param>
/// <param name="BitrateKbps">Average bitrate, or null.</param>
/// <param name="AddedUnix">When the track entered the library.</param>
/// <param name="Rating">Star rating, 0-100, or null if unrated.</param>
public sealed record TrackPageRow(
    long Id,
    string? Title,
    string? Artist,
    string? Album,
    string? AlbumArtist,
    string? Genre,
    int? TrackNo,
    int? Year,
    string? DateText,
    long DurationMs,
    string Codec,
    int? BitrateKbps,
    long AddedUnix,
    int? Rating);

/// <summary>
/// ID-array browsing and page hydration. Sorting always happens in SQL
/// (kickoff §4.3: never sort in memory; the UI holds only IDs).
/// </summary>
public static class TrackPager
{
    /// <summary>
    /// The ORDER BY for a spec. Internal so <see cref="TrackGrouper"/> can
    /// order and group in the same statement instead of sorting first and
    /// walking the result again.
    /// </summary>
    internal static string OrderClauseFor(SortSpec spec, TrackGroupField? group = null)
        => SortCompiler.Compile(spec, group);

    /// <summary>
    /// The spec behind a header-click column sort, or null when the column
    /// cannot be sorted on (an unknown key from stale settings).
    /// </summary>
    public static SortSpec? SpecFor(ColumnSort sort)
        => SortFields.Find(sort.ColumnKey) is null
            ? null
            : SortSpec.Of(sort.ColumnKey, sort.Descending);

    /// <summary>Whether a grid column key has a SQL sort mapping.</summary>
    public static bool IsSortableColumn(string columnKey)
        => SortFields.Find(columnKey) is not null;

    /// <summary>
    /// Re-sorts an id array in SQL (sorting never happens in memory, §4.3).
    /// Used for playlist and search views; duplicates keep their
    /// multiplicity. <see cref="TrackSortOrder.PlaylistOrder"/> is identity.
    /// </summary>
    public static List<long> SortIds(
        LibraryDatabase db, IReadOnlyList<long> ids, TrackSortOrder order)
    {
        return SortPresets.SpecFor(order) is { } spec ? SortIds(db, ids, spec) : [.. ids];
    }

    /// <summary>
    /// Re-sorts an id array in SQL by a header-click column sort. Unknown
    /// column keys (stale settings) return the input order unchanged.
    /// </summary>
    public static List<long> SortIds(LibraryDatabase db, IReadOnlyList<long> ids, ColumnSort sort)
    {
        return SpecFor(sort) is { } spec ? SortIds(db, ids, spec) : [.. ids];
    }

    /// <summary>Re-sorts an id array in SQL by an arbitrary sort order.</summary>
    /// <param name="db">The library.</param>
    /// <param name="ids">The view's ids; duplicates keep their multiplicity.</param>
    /// <param name="spec">The sort order.</param>
    /// <param name="group">
    /// The layout's group field when the result feeds a grouped layout, so the
    /// order stays group-contiguous; null for the flat grid.
    /// </param>
    public static List<long> SortIds(
        LibraryDatabase db,
        IReadOnlyList<long> ids,
        SortSpec spec,
        TrackGroupField? group = null)
    {
        return ids.Count == 0 ? [.. ids] : SortIdsBy(db, ids, SortCompiler.Compile(spec, group));
    }

    private static List<long> SortIdsBy(
        LibraryDatabase db, IReadOnlyList<long> ids, string orderClause)
    {
        // json_each keeps this a single statement regardless of id count.
        string json = "[" + string.Join(',', ids) + "]";
        using SqliteCommand cmd = db.Command($"""
            SELECT t.id FROM json_each($ids) je
            JOIN track t ON t.id = je.value
            LEFT JOIN artist ar ON ar.id = t.artist_id
            LEFT JOIN artist aa ON aa.id = t.album_artist_id
            LEFT JOIN album al ON al.id = t.album_id
            LEFT JOIN genre g ON g.id = t.genre_id
            ORDER BY {orderClause}
            """,
            ("$ids", json));
        using SqliteDataReader reader = cmd.ExecuteReader();
        var result = new List<long>(ids.Count);
        while (reader.Read())
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    /// <summary>All track ids in the given order.</summary>
    public static List<long> BrowseAll(LibraryDatabase db, TrackSortOrder order)
        => BrowseAllBy(db, ClauseOrIdOrder(SortPresets.SpecFor(order)));

    /// <summary>
    /// All track ids sorted by a header-click column sort. Unknown column
    /// keys (stale settings) fall back to id order.
    /// </summary>
    public static List<long> BrowseAll(LibraryDatabase db, ColumnSort sort)
        => BrowseAllBy(db, ClauseOrIdOrder(SpecFor(sort)));

    /// <summary>All track ids in an arbitrary sort order.</summary>
    /// <param name="db">The library.</param>
    /// <param name="spec">The sort order.</param>
    /// <param name="group">The group field of a grouped layout, or null.</param>
    public static List<long> BrowseAll(
        LibraryDatabase db, SortSpec spec, TrackGroupField? group = null)
        => BrowseAllBy(db, SortCompiler.Compile(spec, group));

    // Browsing the whole library has no input order to fall back on, so a
    // spec-less sort becomes id order rather than nothing.
    private static string ClauseOrIdOrder(SortSpec? spec)
        => spec is null ? "t.id" : SortCompiler.Compile(spec);

    private static List<long> BrowseAllBy(LibraryDatabase db, string orderClause)
    {
        return ReadIds(db, $"""
            SELECT t.id FROM track t
            LEFT JOIN artist ar ON ar.id = t.artist_id
            LEFT JOIN artist aa ON aa.id = t.album_artist_id
            LEFT JOIN album al ON al.id = t.album_id
            LEFT JOIN genre g ON g.id = t.genre_id
            ORDER BY {orderClause}
            """);
    }

    /// <summary>The most recently added track ids.</summary>
    public static List<long> BrowseRecentlyAdded(LibraryDatabase db, int limit = 1000)
    {
        return ReadIds(db, $"""
            SELECT t.id FROM track t
            ORDER BY t.added_unix DESC, t.id DESC
            LIMIT {limit}
            """);
    }

    /// <summary>Total number of tracks.</summary>
    public static long Count(LibraryDatabase db)
    {
        return (long)db.Scalar("SELECT count(*) FROM track")!;
    }

    /// <summary>Absolute file path of one track, or null if it vanished.</summary>
    public static string? GetTrackPath(LibraryDatabase db, long id)
    {
        return db.Scalar("SELECT path FROM track WHERE id = $id", ("$id", id)) as string;
    }

    /// <summary>Stored ReplayGain track gain in dB, or null if not scanned.</summary>
    public static double? GetTrackGain(LibraryDatabase db, long id)
    {
        return db.Scalar("SELECT rg_track_gain FROM track WHERE id = $id", ("$id", id)) as double?;
    }

    /// <summary>Tracks that have no ReplayGain values yet.</summary>
    public static List<(long Id, string Path)> GetTracksMissingReplayGain(LibraryDatabase db)
    {
        using SqliteCommand cmd = db.Command(
            "SELECT id, path FROM track WHERE rg_track_gain IS NULL ORDER BY id");
        using SqliteDataReader reader = cmd.ExecuteReader();
        var result = new List<(long, string)>();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        return result;
    }

    /// <summary>Stores scanned ReplayGain values for one track.</summary>
    public static void SetTrackReplayGain(LibraryDatabase db, long id, double gainDb, double peak)
    {
        db.Execute(
            "UPDATE track SET rg_track_gain = $g, rg_track_peak = $p WHERE id = $id",
            ("$g", gainDb),
            ("$p", peak),
            ("$id", id));
    }

    /// <summary>
    /// Stores a track's rating (0-100, or null for unrated). Unlike
    /// ReplayGain, <c>rating</c> is a DSL field (docs/query-dsl.md), so this
    /// bumps the generation counter to invalidate cached smart-playlist
    /// membership that may depend on it.
    /// </summary>
    public static void SetTrackRating(LibraryDatabase db, long id, int? rating)
    {
        db.Execute(
            "UPDATE track SET rating = $r WHERE id = $id",
            ("$r", rating),
            ("$id", id));
        db.BumpLibraryGeneration();
    }

    /// <summary>Hydrates one page of rows; missing ids are simply absent.</summary>
    public static Dictionary<long, TrackPageRow> HydratePage(
        LibraryDatabase db, IReadOnlyList<long> ids)
    {
        var result = new Dictionary<long, TrackPageRow>(ids.Count);
        foreach (long[] chunk in ids.Chunk(512))
        {
            string placeholders = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using SqliteCommand cmd = db.Command($"""
                SELECT t.id, t.title, ar.name, al.name, aa.name, g.name,
                       t.track_no, t.year, t.date_text,
                       t.duration_ms, t.codec, t.bitrate_kbps, t.added_unix, t.rating
                FROM track t
                LEFT JOIN artist ar ON ar.id = t.artist_id
                LEFT JOIN artist aa ON aa.id = t.album_artist_id
                LEFT JOIN album al ON al.id = t.album_id
                LEFT JOIN genre g ON g.id = t.genre_id
                WHERE t.id IN ({placeholders})
                """);
            for (int i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new TrackPageRow(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.GetInt64(9),
                    reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    reader.GetInt64(12),
                    reader.IsDBNull(13) ? null : reader.GetInt32(13));
                result[row.Id] = row;
            }
        }

        return result;
    }

    private static List<long> ReadIds(LibraryDatabase db, string sql)
    {
        using SqliteCommand cmd = db.Command(sql);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }
}
