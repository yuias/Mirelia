using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Xunit;

namespace Mirelia.Library.Tests;

public sealed class SortOrderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;

    public SortOrderTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-so-{Guid.NewGuid():N}.db");
        _db = LibraryDatabase.Open(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void FreshDatabaseHasTheSortOrderTable()
    {
        Assert.Equal("7", _db.Scalar("SELECT value FROM meta WHERE key = 'schema_version'"));
        Assert.Equal(1L, _db.Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'sort_order'"));
    }

    [Fact]
    public void CustomOrdersRoundTripAndKeepTheirPosition()
    {
        var store = new SortOrderStore(_db);
        long first = store.Create("By Album Artist", new SortSpec(
            [new SortTerm("albumartist", false), new SortTerm("albumdate", true)]));
        long second = store.Create("Longest First", SortSpec.Of("time", descending: true));

        List<SortOrderInfo> all = store.List();
        Assert.Equal([first, second], all.Select(o => o.Id));
        Assert.Equal("By Album Artist", all[0].Name);
        Assert.Equal(
            [new SortTerm("albumartist", false), new SortTerm("albumdate", true)],
            all[0].Spec.Terms);

        store.Reorder([second, first]);
        Assert.Equal([second, first], store.List().Select(o => o.Id));

        store.Update(first, "Renamed", SortSpec.Of("rating", descending: true));
        SortOrderInfo renamed = store.List().Single(o => o.Id == first);
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal([new SortTerm("rating", true)], renamed.Spec.Terms);

        store.Delete(second);
        Assert.Equal([first], store.List().Select(o => o.Id));
    }

    [Fact]
    public void ViewSettingsCarryACustomSortReference()
    {
        var store = new ViewSettingsStore(_db);
        store.Set("playlist:7", new ViewSettings
        {
            Sort = TrackSortOrder.Album,
            CustomSort = 42,
        });

        ViewSettings stored = store.Get("playlist:7");
        Assert.Equal(42, stored.CustomSort);
        // The preset stays alongside it: it is what the view falls back to
        // when the referenced order is gone.
        Assert.Equal(TrackSortOrder.Album, stored.Sort);
        // Rows written before custom orders existed read as "a preset".
        Assert.Null(store.Get("source:music").CustomSort);
    }

    [Fact]
    public void CompoundSortRanksAlbumsByArtistThenReleaseDate()
    {
        long alice = InsertArtist("Alice", "alice");
        long bob = InsertArtist("Bob", "bob");
        // Alice released "Early" then "Late"; Bob's album sits between them by
        // date, so a date-only sort would interleave the two artists.
        long aliceEarly = InsertAlbum("Early", "early", alice);
        long aliceLate = InsertAlbum("Late", "late", alice);
        long bobMid = InsertAlbum("Mid", "mid", bob);
        long e1 = InsertTrack("E1", aliceEarly, alice, 2000, 1);
        long m1 = InsertTrack("M1", bobMid, bob, 2010, 1);
        long l2 = InsertTrack("L2", aliceLate, alice, 2020, 2);
        long l1 = InsertTrack("L1", aliceLate, alice, 2020, 1);

        var spec = new SortSpec([new SortTerm("albumartist", false), new SortTerm("albumdate", true)]);
        long[] ids = [m1, l2, e1, l1];

        // Flat: Alice's albums newest-first, then Bob's; tracks stay in
        // album order inside each album.
        Assert.Equal([l1, l2, e1, m1], TrackPager.SortIds(_db, ids, spec));

        // Grouped by album: the same order, and every album is one run.
        (long[] ordered, List<TrackGroupRun> runs) =
            TrackGrouper.LoadOrderedRuns(_db, ids, spec, TrackGroupField.Album);
        Assert.Equal([l1, l2, e1, m1], ordered);
        Assert.Equal([aliceLate, aliceEarly, bobMid], runs.Select(r => r.GroupId));
    }

    [Fact]
    public void EveryTermIsAggregatedSoGroupsStayInOnePiece()
    {
        long artist = InsertArtist("A", "a");
        long album = InsertAlbum("Split", "split", artist);
        long other = InsertAlbum("Other", "other", artist);
        // Titles interleave across the two albums, so ordering by title alone
        // would cut both albums into pieces.
        long aa = InsertTrack("Apple", album, artist, 2000, 1);
        long bb = InsertTrack("Banana", other, artist, 2000, 1);
        long cc = InsertTrack("Cherry", album, artist, 2000, 2);

        (long[] ordered, List<TrackGroupRun> runs) = TrackGrouper.LoadOrderedRuns(
            _db, [aa, bb, cc], SortSpec.Of("title"), TrackGroupField.Album);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, run => Assert.Equal(run.Count, ordered.Skip(run.StartIndex).Take(run.Count).Count()));
        // "Split" ranks by its highest title ("Cherry"), so it follows "Other".
        Assert.Equal([bb, aa, cc], ordered);
        Assert.Equal([other, album], runs.Select(r => r.GroupId));
    }

    [Fact]
    public void GroupsWithEqualKeysStayInOnePiece()
    {
        long alice = InsertArtist("Alice", "alice");
        long bob = InsertArtist("Bob", "bob");
        long aliceAlbum = InsertAlbum("Same", "same", alice);
        long bobAlbum = InsertAlbum("Same", "same", bob);
        long a1 = InsertTrack("A1", aliceAlbum, alice, 2000, 1);
        long b1 = InsertTrack("B1", bobAlbum, bob, 2000, 1);
        long a2 = InsertTrack("A2", aliceAlbum, alice, 2000, 2);
        long b2 = InsertTrack("B2", bobAlbum, bob, 2000, 2);

        // Equal release date and equal album name: nothing in the spec or
        // the tail separates the groups before the track numbers would.
        (_, List<TrackGroupRun> albums) = TrackGrouper.LoadOrderedRuns(
            _db, [a1, b1, a2, b2], TrackSortOrder.AlbumByYear, TrackGroupField.Album);
        Assert.Equal([aliceAlbum, bobAlbum], albums.Select(r => r.GroupId));

        (_, List<TrackGroupRun> artists) = TrackGrouper.LoadOrderedRuns(
            _db, [a1, b1, a2, b2], TrackSortOrder.AlbumByYear, TrackGroupField.AlbumArtist);
        Assert.Equal([alice, bob], artists.Select(r => r.GroupId));
    }

    [Fact]
    public void UnknownFieldsAreSkippedRatherThanBreakingTheView()
    {
        long artist = InsertArtist("A", "a");
        long album = InsertAlbum("A", "a", artist);
        long t1 = InsertTrack("Alpha", album, artist, 2000, 1);
        long t2 = InsertTrack("Bravo", album, artist, 2000, 2);

        // A term naming a field this build does not have leaves the rest of
        // the spec working.
        var spec = new SortSpec([new SortTerm("bogus", false), new SortTerm("title", true)]);
        Assert.Equal([t2, t1], TrackPager.SortIds(_db, [t1, t2], spec));

        // A spec with nothing usable still yields a valid, stable order.
        Assert.Equal(
            [t1, t2],
            TrackPager.SortIds(_db, [t2, t1], new SortSpec([new SortTerm("bogus", false)])));
    }

    [Fact]
    public void PresetsCompileToTheSameOrderAsTheirSpecs()
    {
        long artist = InsertArtist("A", "a");
        long albumOld = InsertAlbum("Old", "old", artist);
        long albumNew = InsertAlbum("New", "new", artist);
        long o1 = InsertTrack("O1", albumOld, artist, 1990, 1);
        long n1 = InsertTrack("N1", albumNew, artist, 2024, 1);
        long[] ids = [n1, o1];

        Assert.Equal(
            TrackPager.SortIds(_db, ids, TrackSortOrder.AlbumByYear),
            TrackPager.SortIds(_db, ids, SortPresets.SpecFor(TrackSortOrder.AlbumByYear)!));
        Assert.Equal(
            TrackPager.SortIds(_db, ids, TrackSortOrder.RecentlyAdded),
            TrackPager.SortIds(_db, ids, SortPresets.SpecFor(TrackSortOrder.RecentlyAdded)!));
        // Playlist order is the input order, not an ordering over fields.
        Assert.Null(SortPresets.SpecFor(TrackSortOrder.PlaylistOrder));
    }

    [Fact]
    public void ArtistGroupsSortByNameAndDivideByAlbum()
    {
        long zed = InsertArtist("Zed", "zed");
        long amy = InsertArtist("Amy", "amy");
        long zedAlbum = InsertAlbum("Zed One", "zed one", zed);
        long amyLater = InsertAlbum("Later", "later", amy);
        long amyDebut = InsertAlbum("Debut", "debut", amy);
        long z1 = InsertTrack("Z1", zedAlbum, zed, 2020, 1);
        long l1 = InsertTrack("L1", amyLater, amy, 2010, 1);
        long d1 = InsertTrack("D1", amyDebut, amy, 2000, 1);
        long d2 = InsertTrack("D2", amyDebut, amy, 2000, 2);

        (long[] ordered, List<TrackGroupRun> runs) = TrackGrouper.LoadOrderedRuns(
            _db, [z1, l1, d1, d2], TrackSortOrder.AlbumArtist, TrackGroupField.AlbumArtist);

        Assert.Equal([d1, d2, l1, z1], ordered);
        Assert.Equal([amy, zed], runs.Select(r => r.GroupId));
        Assert.Equal("Amy", runs[0].Title);
        Assert.Equal(
            [new DiscSpan(null, 0, 2, "Debut"), new DiscSpan(null, 2, 1, "Later")],
            runs[0].Discs);
    }

    private long InsertArtist(string name, string sort)
    {
        return (long)_db.Scalar(
            "INSERT INTO artist (name, name_sort, name_search) VALUES ($n, $s, $s) RETURNING id",
            ("$n", name),
            ("$s", sort))!;
    }

    private long InsertAlbum(string name, string sort, long artistId)
    {
        return (long)_db.Scalar("""
            INSERT INTO album (name, name_sort, name_search, album_artist_id)
            VALUES ($n, $s, $s, $a) RETURNING id
            """,
            ("$n", name),
            ("$s", sort),
            ("$a", artistId))!;
    }

    private long InsertTrack(string title, long albumId, long artistId, int year, int trackNo)
    {
        return (long)_db.Scalar("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix, title, album_id,
                artist_id, album_artist_id, track_no, year, date_key,
                title_sort, title_search, duration_ms,
                sample_rate, channels, codec, is_lossless, added_unix, modified_unix)
            VALUES ($t, lower($t), 1, 0, $t, $al, $ar, $ar, $no, $y, $y * 10000 + 101,
                    lower($t), lower($t),
                    1000, 44100, 2, 'flac', 1, 0, 0)
            RETURNING id
            """,
            ("$t", title),
            ("$al", albumId),
            ("$ar", artistId),
            ("$no", trackNo),
            ("$y", year))!;
    }
}
