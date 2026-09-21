using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Xunit;

namespace Mirelia.Library.Tests;

public sealed class ViewSettingsTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;

    public ViewSettingsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-vs-{Guid.NewGuid():N}.db");
        _db = LibraryDatabase.Open(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void FreshDatabaseIsCurrentSchema()
    {
        Assert.Equal("6", _db.Scalar("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal(1L, _db.Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'view_settings'"));
        // v3's date_key column exists (the query throws if it does not);
        // an empty table yields SQL NULL, surfaced as DBNull.
        Assert.Equal(DBNull.Value, _db.Scalar("SELECT MAX(date_key) FROM track"));
        Assert.Equal(3L, _db.Scalar("""
            SELECT count(*) FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('sync_target', 'sync_target_scope', 'sync_target_file')
            """));
    }

    [Fact]
    public void SettingsRoundTripPerScope()
    {
        var store = new ViewSettingsStore(_db);
        Assert.Null(store.Get("source:music").Columns);

        store.Set("source:music", new ViewSettings
        {
            Columns = ["title", "artist", "date"],
            Sort = TrackSortOrder.AlbumByYear,
        });
        store.Set("playlist:7", new ViewSettings { Sort = TrackSortOrder.PlaylistOrder });

        ViewSettings music = store.Get("source:music");
        Assert.Equal(["title", "artist", "date"], music.Columns);
        Assert.Equal(TrackSortOrder.AlbumByYear, music.Sort);
        Assert.Null(store.Get("playlist:7").Columns);
        Assert.Equal(TrackSortOrder.PlaylistOrder, store.Get("playlist:7").Sort);

        store.Set("source:music", new ViewSettings { Columns = ["title"] });
        Assert.Equal(["title"], store.Get("source:music").Columns);
    }

    [Fact]
    public void SortIdsOrdersAlbumsByYearThenTrack()
    {
        long oldAlbum = InsertAlbum("Old Album", "old album");
        long newAlbum = InsertAlbum("New Album", "new album");
        // Interleave inserts so id order differs from every sort key.
        long n2 = InsertTrack("N2", newAlbum, year: 2024, trackNo: 2, added: 100);
        long o1 = InsertTrack("O1", oldAlbum, year: 1990, trackNo: 1, added: 300);
        long n1 = InsertTrack("N1", newAlbum, year: 2024, trackNo: 1, added: 200);
        long o2 = InsertTrack("O2", oldAlbum, year: 1990, trackNo: 2, added: 50);

        long[] ids = [n2, o1, n1, o2];
        Assert.Equal(
            [o1, o2, n1, n2],
            TrackPager.SortIds(_db, ids, TrackSortOrder.AlbumByYear));
        // Old Album's newest arrival is 300 vs New Album's 200, so despite
        // its year it comes first when sorting albums by arrival.
        Assert.Equal(
            [o1, o2, n1, n2],
            TrackPager.SortIds(_db, ids, TrackSortOrder.AlbumByRecentlyAdded));
        Assert.Equal(ids, TrackPager.SortIds(_db, ids, TrackSortOrder.PlaylistOrder));
    }

    [Fact]
    public void SettingsRoundTripColumnWidths()
    {
        var store = new ViewSettingsStore(_db);
        store.Set("source:music", new ViewSettings
        {
            ColumnWidths = new Dictionary<string, double> { ["title"] = 220.5, ["artist"] = 150 },
        });

        ViewSettings music = store.Get("source:music");
        Assert.Equal(220.5, music.ColumnWidths?["title"]);
        Assert.Equal(150, music.ColumnWidths?["artist"]);
        // Rows written before column widths existed read as "none" (backward compatibility).
        Assert.Null(store.Get("playlist:7").ColumnWidths);
    }

    [Fact]
    public void ResettingColumnsLeavesTheLayoutStyleAlone()
    {
        var store = new ViewSettingsStore(_db);
        store.Set("playlist:7", new ViewSettings
        {
            Columns = ["title"],
            Layout = "albums",
            Sort = TrackSortOrder.AlbumByYear,
        });

        store.ClearAllColumns();

        // "Reset columns" is about columns; it must not drop a view back to
        // the flat grid.
        ViewSettings after = store.Get("playlist:7");
        Assert.Null(after.Columns);
        Assert.Equal("albums", after.Layout);
        Assert.Equal(TrackSortOrder.AlbumByYear, after.Sort);
        // Rows written before layout styles existed read as "the flat grid".
        Assert.Null(store.Get("source:music").Layout);
    }

    [Fact]
    public void ClearColumnsExceptSparesTheKeptScope()
    {
        var store = new ViewSettingsStore(_db);
        store.Set("source:music", new ViewSettings
        {
            Columns = ["title", "artist"],
            ColumnWidths = new Dictionary<string, double> { ["title"] = 200 },
        });
        store.Set("playlist:7", new ViewSettings
        {
            Columns = ["title", "genre"],
            ColumnWidths = new Dictionary<string, double> { ["genre"] = 90 },
            Layout = "albums",
            Sort = TrackSortOrder.AlbumByYear,
        });
        store.Set("folder:3", new ViewSettings { Columns = ["path"] });

        store.ClearColumnsExcept("source:music");

        // Music keeps its layout; it is the one the others now follow.
        ViewSettings music = store.Get("source:music");
        Assert.Equal(["title", "artist"], music.Columns);
        Assert.Equal(200, music.ColumnWidths?["title"]);

        // Everyone else drops columns and widths only — a null Columns is
        // what makes a view inherit, and sort and layout are not columns.
        ViewSettings playlist = store.Get("playlist:7");
        Assert.Null(playlist.Columns);
        Assert.Null(playlist.ColumnWidths);
        Assert.Equal("albums", playlist.Layout);
        Assert.Equal(TrackSortOrder.AlbumByYear, playlist.Sort);
        Assert.Null(store.Get("folder:3").Columns);
    }

    [Fact]
    public void SettingsRoundTripColumnSort()
    {
        var store = new ViewSettingsStore(_db);
        store.Set("source:music", new ViewSettings
        {
            SortColumn = "date",
            SortDescending = true,
        });

        ViewSettings music = store.Get("source:music");
        Assert.Equal("date", music.SortColumn);
        Assert.True(music.SortDescending);
        // Rows written before the column-sort fields existed read as "none".
        Assert.Null(store.Get("playlist:7").SortColumn);
    }

    [Fact]
    public void ColumnSortOrdersByKeyAndDirection()
    {
        long albumA = InsertAlbum("A", "a");
        long albumB = InsertAlbum("B", "b");
        long t1 = InsertTrack("Charlie", albumB, year: 2020, trackNo: 3, added: 10);
        long t2 = InsertTrack("Alpha", albumA, year: 2024, trackNo: 1, added: 30);
        long t3 = InsertTrack("Bravo", albumA, year: 2022, trackNo: 2, added: 20);

        long[] ids = [t1, t2, t3];
        Assert.Equal(
            [t2, t3, t1],
            TrackPager.SortIds(_db, ids, new ColumnSort("title", Descending: false)));
        Assert.Equal(
            [t1, t3, t2],
            TrackPager.SortIds(_db, ids, new ColumnSort("title", Descending: true)));
        Assert.Equal(
            [t2, t3, t1],
            TrackPager.SortIds(_db, ids, new ColumnSort("date", Descending: true)));
        Assert.Equal(
            [t1, t3, t2],
            TrackPager.BrowseAll(_db, new ColumnSort("added", Descending: false)));
        // Unknown keys (stale settings) leave the input order untouched.
        Assert.Equal(ids, TrackPager.SortIds(_db, ids, new ColumnSort("bogus", false)));
        Assert.False(TrackPager.IsSortableColumn("bogus"));
    }

    [Fact]
    public void ColumnSortKeepsNullValuesLastInBothDirections()
    {
        long album = InsertAlbum("A", "a");
        long dated = InsertTrack("Dated", album, year: 2020, trackNo: 1, added: 1);
        long undated = (long)_db.Scalar("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix, title, album_id,
                title_sort, title_search, duration_ms,
                sample_rate, channels, codec, is_lossless, added_unix, modified_unix)
            VALUES ('u', 'u', 1, 0, 'Undated', $al, 'undated', 'undated',
                    1000, 44100, 2, 'flac', 1, 2, 0)
            RETURNING id
            """,
            ("$al", album))!;

        long[] ids = [undated, dated];
        Assert.Equal(
            [dated, undated],
            TrackPager.SortIds(_db, ids, new ColumnSort("date", Descending: false)));
        Assert.Equal(
            [dated, undated],
            TrackPager.SortIds(_db, ids, new ColumnSort("date", Descending: true)));
    }

    [Fact]
    public void SetTrackRatingWritesAndBumpsGeneration()
    {
        long album = InsertAlbum("A", "a");
        long t = InsertTrack("T", album, year: 2000, trackNo: 1, added: 1);
        long generation = _db.GetLibraryGeneration();

        TrackPager.SetTrackRating(_db, t, 80);
        Assert.Equal(generation + 1, _db.GetLibraryGeneration());
        Assert.Equal(80, TrackPager.HydratePage(_db, [t])[t].Rating);

        // Clearing back to unrated (null) is also a membership-affecting
        // write, so it bumps the generation again.
        TrackPager.SetTrackRating(_db, t, null);
        Assert.Equal(generation + 2, _db.GetLibraryGeneration());
        Assert.Null(TrackPager.HydratePage(_db, [t])[t].Rating);
    }

    [Fact]
    public void ColumnSortByRatingKeepsNullsLastInBothDirections()
    {
        long album = InsertAlbum("A", "a");
        long rated = InsertTrack("Rated", album, year: 2020, trackNo: 1, added: 1);
        long unrated = InsertTrack("Unrated", album, year: 2020, trackNo: 2, added: 2);
        TrackPager.SetTrackRating(_db, rated, 100);

        long[] ids = [unrated, rated];
        Assert.Equal(
            [rated, unrated],
            TrackPager.SortIds(_db, ids, new ColumnSort("rating", Descending: false)));
        Assert.Equal(
            [rated, unrated],
            TrackPager.SortIds(_db, ids, new ColumnSort("rating", Descending: true)));
    }

    [Fact]
    public void SortIdsPreservesDuplicates()
    {
        long album = InsertAlbum("A", "a");
        long t = InsertTrack("T", album, year: 2000, trackNo: 1, added: 1);
        List<long> sorted = TrackPager.SortIds(_db, [t, t], TrackSortOrder.Title);
        Assert.Equal([t, t], sorted);
    }

    private long InsertAlbum(string name, string sort)
    {
        return (long)_db.Scalar(
            "INSERT INTO album (name, name_sort, name_search) VALUES ($n, $s, $s) RETURNING id",
            ("$n", name),
            ("$s", sort))!;
    }

    [Fact]
    public void ExpandedFoldersRoundTrip()
    {
        var store = new ViewSettingsStore(_db);

        Assert.Empty(store.GetExpandedFolders());

        store.SetExpandedFolders([7, 3]);

        Assert.Equal([7L, 3L], store.GetExpandedFolders());
    }

    [Fact]
    public void ClearingColumnsLeavesTheExpandedFoldersAlone()
    {
        var store = new ViewSettingsStore(_db);
        store.SetExpandedFolders([7, 3]);
        store.Set("source:music", new ViewSettings { Columns = ["title"] });

        store.ClearAllColumns();

        Assert.Null(store.Get("source:music").Columns);
        Assert.Equal([7L, 3L], store.GetExpandedFolders());
    }

    private long InsertTrack(string title, long albumId, int year, int trackNo, long added)
    {
        return (long)_db.Scalar("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix, title, album_id,
                track_no, year, date_key, title_sort, title_search, duration_ms,
                sample_rate, channels, codec, is_lossless, added_unix, modified_unix)
            VALUES ($t, lower($t), 1, 0, $t, $al, $no, $y, $y * 10000 + 101,
                    lower($t), lower($t),
                    1000, 44100, 2, 'flac', 1, $added, 0)
            RETURNING id
            """,
            ("$t", title),
            ("$al", albumId),
            ("$no", trackNo),
            ("$y", year),
            ("$added", added))!;
    }
}
