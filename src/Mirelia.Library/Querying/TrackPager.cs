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
}

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
    long AddedUnix);

/// <summary>
/// ID-array browsing and page hydration. Sorting always happens in SQL
/// (kickoff §4.3: never sort in memory; the UI holds only IDs).
/// </summary>
public static class TrackPager
{
    private static string OrderClause(TrackSortOrder order) => order switch
    {
        TrackSortOrder.Artist => "ar.name_sort, al.name_sort, t.disc_no, t.track_no, t.title_sort",
        TrackSortOrder.Album => "al.name_sort, t.disc_no, t.track_no, t.title_sort",
        TrackSortOrder.Title => "t.title_sort",
        TrackSortOrder.RecentlyAdded => "t.added_unix DESC, t.id DESC",
        // date_key normalizes mixed date spellings (2025, 2025/10/5,
        // 2025-08-12) into one sortable yyyymmdd int; undated tracks sink
        // to the end.
        TrackSortOrder.Year =>
            "COALESCE(t.date_key, 99999999), al.name_sort, t.disc_no, t.track_no",
        // Compound sorts: the album's aggregate keys the album blocks,
        // album order keys the tracks inside (window fns are fine in ORDER BY).
        // MAX skips NULLs, so one undated track cannot sink its album; only
        // fully undated albums fall to the end via the outer COALESCE.
        TrackSortOrder.AlbumByYear =>
            "COALESCE(MAX(t.date_key) OVER (PARTITION BY t.album_id), 99999999), "
            + "al.name_sort, t.disc_no, t.track_no",
        TrackSortOrder.AlbumByRecentlyAdded =>
            "MAX(t.added_unix) OVER (PARTITION BY t.album_id) DESC, al.name_sort, t.disc_no, t.track_no",
        _ => "t.id",
    };

    /// <summary>
    /// Re-sorts an id array in SQL (sorting never happens in memory, §4.3).
    /// Used for playlist and search views; duplicates keep their
    /// multiplicity. <see cref="TrackSortOrder.PlaylistOrder"/> is identity.
    /// </summary>
    public static List<long> SortIds(
        LibraryDatabase db, IReadOnlyList<long> ids, TrackSortOrder order)
    {
        if (order == TrackSortOrder.PlaylistOrder || ids.Count == 0)
        {
            return [.. ids];
        }

        // json_each keeps this a single statement regardless of id count.
        string json = "[" + string.Join(',', ids) + "]";
        using SqliteCommand cmd = db.Command($"""
            SELECT t.id FROM json_each($ids) je
            JOIN track t ON t.id = je.value
            LEFT JOIN artist ar ON ar.id = t.artist_id
            LEFT JOIN album al ON al.id = t.album_id
            ORDER BY {OrderClause(order)}
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
    {
        return ReadIds(db, $"""
            SELECT t.id FROM track t
            LEFT JOIN artist ar ON ar.id = t.artist_id
            LEFT JOIN album al ON al.id = t.album_id
            ORDER BY {OrderClause(order)}
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
                       t.duration_ms, t.codec, t.bitrate_kbps, t.added_unix
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
                    reader.GetInt64(12));
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
