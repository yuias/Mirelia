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
        Assert.Equal("3", _db.Scalar("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal(1L, _db.Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'view_settings'"));
        // v3's date_key column exists (the query throws if it does not);
        // an empty table yields SQL NULL, surfaced as DBNull.
        Assert.Equal(DBNull.Value, _db.Scalar("SELECT MAX(date_key) FROM track"));
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
