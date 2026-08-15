using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Xunit;

namespace Mirelia.Library.Tests;

public sealed class TrackGrouperTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;
    private int _pathCounter;

    public TrackGrouperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-grp-{Guid.NewGuid():N}.db");
        _db = LibraryDatabase.Open(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void EmptyInputYieldsNoRuns()
    {
        Assert.Empty(TrackGrouper.LoadRuns(_db, [], TrackGroupField.Album));
    }

    [Fact]
    public void RunsCoverTheIdArrayAndCarryHeaderAggregates()
    {
        long hoyo = InsertArtist("HOYO-MiX");
        long game = InsertGenre("Game");
        long volume5 = InsertAlbum("Vol.5", albumArtistId: hoyo);
        long volume6 = InsertAlbum("Vol.6", albumArtistId: hoyo);

        long a1 = InsertTrack("A1", volume5, trackNo: 1, dateKey: 20260101,
            added: 100, durationMs: 5_000, genreId: game);
        long a2 = InsertTrack("A2", volume5, trackNo: 2, dateKey: 20260315,
            added: 300, durationMs: 7_000, genreId: game);
        long b1 = InsertTrack("B1", volume6, trackNo: 1, dateKey: 20250101,
            added: 200, durationMs: 3_000, genreId: game);

        List<TrackGroupRun> runs =
            TrackGrouper.LoadRuns(_db, [a1, a2, b1], TrackGroupField.Album);

        Assert.Equal(2, runs.Count);

        TrackGroupRun first = runs[0];
        Assert.Equal(volume5, first.GroupId);
        Assert.Equal("Vol.5", first.Title);
        Assert.Equal("HOYO-MiX", first.Subtitle);
        Assert.Equal("Game", first.Genre);
        Assert.Equal(0, first.StartIndex);
        Assert.Equal(2, first.Count);
        Assert.Equal(12_000, first.DurationMs);
        // Header aggregates take the run's latest date and arrival, matching
        // how AlbumByYear / AlbumByRecentlyAdded rank the album.
        Assert.Equal(2026, first.Year);
        Assert.Equal(300, first.MaxAddedUnix);
        Assert.Equal(a1, first.CoverTrackId);

        TrackGroupRun second = runs[1];
        Assert.Equal(volume6, second.GroupId);
        Assert.Equal("Vol.6", second.Title);
        Assert.Equal(2, second.StartIndex);
        Assert.Equal(1, second.Count);
        Assert.Equal(2025, second.Year);
        Assert.Equal(b1, second.CoverTrackId);

        // Every id lands in exactly one run, in order.
        Assert.Equal(3, runs.Sum(r => r.Count));
        Assert.Equal([0, 2], runs.Select(r => r.StartIndex));
    }

    [Fact]
    public void RunsFollowTheSortOrderTheyAreGiven()
    {
        long older = InsertAlbum("Older");
        long newer = InsertAlbum("Newer");
        long o1 = InsertTrack("O1", older, trackNo: 1, dateKey: 19900101, added: 10);
        long n1 = InsertTrack("N1", newer, trackNo: 1, dateKey: 20240101, added: 20);

        // The grouper reads whatever order SQL produced; it never re-sorts.
        List<long> byYear = TrackPager.SortIds(_db, [n1, o1], TrackSortOrder.AlbumByYear);
        List<TrackGroupRun> runs = TrackGrouper.LoadRuns(_db, byYear, TrackGroupField.Album);
        Assert.Equal([older, newer], runs.Select(r => r.GroupId));

        runs = TrackGrouper.LoadRuns(_db, [n1, o1], TrackGroupField.Album);
        Assert.Equal([newer, older], runs.Select(r => r.GroupId));
    }

    [Fact]
    public void DiscsBecomeSpansInsideTheirRun()
    {
        long album = InsertAlbum("Two Discs");
        long d1t1 = InsertTrack("D1T1", album, trackNo: 1, discNo: 1);
        long d1t2 = InsertTrack("D1T2", album, trackNo: 2, discNo: 1);
        long d2t1 = InsertTrack("D2T1", album, trackNo: 1, discNo: 2);

        TrackGroupRun run = Assert.Single(
            TrackGrouper.LoadRuns(_db, [d1t1, d1t2, d2t1], TrackGroupField.Album));

        Assert.Equal(
            [new DiscSpan(1, 0, 2), new DiscSpan(2, 2, 1)],
            run.Discs);
    }

    [Fact]
    public void UntaggedDiscNumbersFormOneSpan()
    {
        long album = InsertAlbum("No Discs");
        long t1 = InsertTrack("T1", album, trackNo: 1);
        long t2 = InsertTrack("T2", album, trackNo: 2);

        TrackGroupRun run = Assert.Single(
            TrackGrouper.LoadRuns(_db, [t1, t2], TrackGroupField.Album));

        Assert.Equal([new DiscSpan(null, 0, 2)], run.Discs);
    }

    [Fact]
    public void DuplicateIdsKeepTheirSlots()
    {
        long album = InsertAlbum("A");
        long t = InsertTrack("T", album, trackNo: 1, durationMs: 1_000);

        TrackGroupRun run = Assert.Single(
            TrackGrouper.LoadRuns(_db, [t, t, t], TrackGroupField.Album));

        // A static playlist may hold the same track more than once; each
        // occurrence is its own row and counts toward the total.
        Assert.Equal(3, run.Count);
        Assert.Equal(3_000, run.DurationMs);
    }

    [Fact]
    public void MissingTracksKeepTheirSlotsSoIndicesStayAligned()
    {
        long album = InsertAlbum("A");
        long t1 = InsertTrack("T1", album, trackNo: 1);
        long t2 = InsertTrack("T2", album, trackNo: 2);
        const long vanished = 9_999;

        List<TrackGroupRun> runs =
            TrackGrouper.LoadRuns(_db, [t1, vanished, t2], TrackGroupField.Album);

        // The absent id groups as "no group" rather than being dropped, so
        // index 2 still addresses t2 in the caller's array.
        Assert.Equal([0, 1, 2], runs.Select(r => r.StartIndex));
        Assert.Equal(3, runs.Sum(r => r.Count));
        Assert.Equal(TrackGrouper.NoGroup, runs[1].GroupId);
        Assert.Null(runs[1].Title);
    }

    [Fact]
    public void TracksWithoutAnAlbumGroupUnderNoGroup()
    {
        long loose = InsertTrack("Loose", albumId: null, trackNo: 1);

        TrackGroupRun run = Assert.Single(
            TrackGrouper.LoadRuns(_db, [loose], TrackGroupField.Album));

        // Naming the group is the presentation layer's call, so the title
        // stays null rather than becoming "Unknown Album" here.
        Assert.Equal(TrackGrouper.NoGroup, run.GroupId);
        Assert.Null(run.Title);
        Assert.Equal(loose, run.CoverTrackId);
    }

    [Fact]
    public void ANonContiguousGroupYieldsSeveralRuns()
    {
        long a = InsertAlbum("A");
        long b = InsertAlbum("B");
        long a1 = InsertTrack("A1", a, trackNo: 1);
        long b1 = InsertTrack("B1", b, trackNo: 1);
        long a2 = InsertTrack("A2", a, trackNo: 2);

        List<TrackGroupRun> runs =
            TrackGrouper.LoadRuns(_db, [a1, b1, a2], TrackGroupField.Album);

        // Grouped layouts always sort group-contiguously, but a stray order
        // splits the group instead of corrupting the run indices.
        Assert.Equal([a, b, a], runs.Select(r => r.GroupId));
        Assert.Equal([0, 1, 2], runs.Select(r => r.StartIndex));
    }

    [Fact]
    public void GroupingByAlbumArtistFallsBackToTheTrackArtist()
    {
        long various = InsertArtist("Various");
        long solo = InsertArtist("Solo");
        long album = InsertAlbum("Compilation");
        long tagged = InsertTrack("Tagged", album, trackNo: 1, albumArtistId: various);
        long untagged = InsertTrack("Untagged", album, trackNo: 2, artistId: solo);

        List<TrackGroupRun> runs = TrackGrouper.LoadRuns(
            _db, [tagged, untagged], TrackGroupField.AlbumArtist);

        Assert.Equal([various, solo], runs.Select(r => r.GroupId));
        Assert.Equal(["Various", "Solo"], runs.Select(r => r.Title));
        // Artist groups have no second line of their own.
        Assert.All(runs, r => Assert.Null(r.Subtitle));
    }

    [Fact]
    public void TheMostCommonGenreLabelsTheRun()
    {
        long soundtrack = InsertGenre("Soundtrack");
        long pop = InsertGenre("Pop");
        long album = InsertAlbum("Mixed");
        long t1 = InsertTrack("T1", album, trackNo: 1, genreId: pop);
        long t2 = InsertTrack("T2", album, trackNo: 2, genreId: soundtrack);
        long t3 = InsertTrack("T3", album, trackNo: 3, genreId: soundtrack);

        TrackGroupRun run = Assert.Single(
            TrackGrouper.LoadRuns(_db, [t1, t2, t3], TrackGroupField.Album));

        Assert.Equal("Soundtrack", run.Genre);
    }

    [Fact]
    public void UndatedTracksLeaveTheRunYearFromTheirBareYearTag()
    {
        long album = InsertAlbum("A");
        long dated = InsertTrack("Dated", album, trackNo: 1, dateKey: 20200315);
        long yearOnly = InsertTrack("YearOnly", album, trackNo: 2, year: 2022);
        long undated = InsertTrack("Undated", album, trackNo: 3);

        // date_key wins where present; a run with none falls back to year.
        Assert.Equal(2020, Assert.Single(
            TrackGrouper.LoadRuns(_db, [dated], TrackGroupField.Album)).Year);
        Assert.Equal(2022, Assert.Single(
            TrackGrouper.LoadRuns(_db, [yearOnly], TrackGroupField.Album)).Year);
        Assert.Null(Assert.Single(
            TrackGrouper.LoadRuns(_db, [undated], TrackGroupField.Album)).Year);
    }

    private long InsertArtist(string name)
    {
        return (long)_db.Scalar(
            "INSERT INTO artist (name, name_sort, name_search) VALUES ($n, $s, $s) RETURNING id",
            ("$n", name),
            ("$s", name.ToLowerInvariant()))!;
    }

    private long InsertGenre(string name)
    {
        return (long)_db.Scalar(
            "INSERT INTO genre (name, name_sort, name_search) VALUES ($n, $s, $s) RETURNING id",
            ("$n", name),
            ("$s", name.ToLowerInvariant()))!;
    }

    private long InsertAlbum(string name, long? albumArtistId = null)
    {
        return (long)_db.Scalar("""
            INSERT INTO album (name, name_sort, name_search, album_artist_id)
            VALUES ($n, $s, $s, $aa) RETURNING id
            """,
            ("$n", name),
            ("$s", name.ToLowerInvariant()),
            ("$aa", albumArtistId))!;
    }

    private long InsertTrack(
        string title,
        long? albumId,
        int trackNo,
        int? discNo = null,
        long? dateKey = null,
        int? year = null,
        long added = 0,
        long durationMs = 1_000,
        long? genreId = null,
        long? artistId = null,
        long? albumArtistId = null)
    {
        string path = $"{title}-{_pathCounter++}";
        return (long)_db.Scalar("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix, title, album_id,
                artist_id, album_artist_id, genre_id, track_no, disc_no,
                year, date_key, title_sort, title_search, duration_ms,
                sample_rate, channels, codec, is_lossless, added_unix, modified_unix)
            VALUES ($p, lower($p), 1, 0, $t, $al,
                    $ar, $aa, $g, $no, $disc,
                    $y, $dk, lower($t), lower($t), $dur,
                    44100, 2, 'flac', 1, $added, 0)
            RETURNING id
            """,
            ("$p", path),
            ("$t", title),
            ("$al", albumId),
            ("$ar", artistId),
            ("$aa", albumArtistId),
            ("$g", genreId),
            ("$no", trackNo),
            ("$disc", discNo),
            ("$y", year),
            ("$dk", dateKey),
            ("$dur", durationMs),
            ("$added", added))!;
    }
}
