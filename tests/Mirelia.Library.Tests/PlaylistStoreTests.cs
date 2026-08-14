using Mirelia.Library.Database;
using Mirelia.Library.Playlists;
using Mirelia.Query.Compilation;
using Xunit;

namespace Mirelia.Library.Tests;

public sealed class PlaylistStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;
    private readonly PlaylistStore _store;
    private readonly long[] _trackIds;

    public PlaylistStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-pl-{Guid.NewGuid():N}.db");
        _db = LibraryDatabase.Open(_dbPath);
        _store = new PlaylistStore(_db);

        _trackIds = new long[5];
        for (int i = 0; i < _trackIds.Length; i++)
        {
            _trackIds[i] = InsertTrack($"Song {i}", rating: i * 20);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    private long InsertTrack(string title, int rating)
    {
        return (long)_db.Scalar("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix, title,
                title_sort, title_search, duration_ms, sample_rate, channels,
                codec, is_lossless, rating, added_unix, modified_unix)
            VALUES ($t, lower($t), 1, 0, $t, lower($t), lower($t),
                    180000, 44100, 2, 'flac', 1, $r, 0, 0)
            RETURNING id
            """,
            ("$t", title),
            ("$r", rating))!;
    }

    [Fact]
    public void StaticPlaylistKeepsOrderAndAllowsDuplicates()
    {
        PlaylistInfo p = _store.CreateStatic("Mix");
        _store.AddTracks(p.Id, [_trackIds[2], _trackIds[0], _trackIds[2]]);
        Assert.Equal([_trackIds[2], _trackIds[0], _trackIds[2]], _store.GetStaticTracks(p.Id));
    }

    [Fact]
    public void RemoveAtAndMoveRenumberPositions()
    {
        PlaylistInfo p = _store.CreateStatic("Edit");
        _store.AddTracks(p.Id, _trackIds);

        _store.RemoveAt(p.Id, [1, 3]);
        Assert.Equal([_trackIds[0], _trackIds[2], _trackIds[4]], _store.GetStaticTracks(p.Id));

        _store.Move(p.Id, from: 2, to: 0);
        Assert.Equal([_trackIds[4], _trackIds[0], _trackIds[2]], _store.GetStaticTracks(p.Id));
    }

    [Fact]
    public void MoveToALaterPositionUsesPostRemovalIndexing()
    {
        PlaylistInfo p = _store.CreateStatic("Reorder");
        _store.AddTracks(p.Id, _trackIds); // positions 0..4

        // `to` counts positions in the list *after* `from` is removed (the
        // grid's drag-drop reorder relies on this exact convention — see
        // MainWindow.axaml.cs GridDropIndex), so moving 0 -> 2 lands the
        // entry between the entries originally at positions 2 and 3, not 1
        // and 2 as a naive pre-removal reading of `to` would suggest.
        _store.Move(p.Id, from: 0, to: 2);
        Assert.Equal(
            [_trackIds[1], _trackIds[2], _trackIds[0], _trackIds[3], _trackIds[4]],
            _store.GetStaticTracks(p.Id));
    }

    [Fact]
    public void SmartPlaylistIsCanonicalizedAndEvaluates()
    {
        PlaylistInfo p = _store.CreateSmart("Good", "rating   >=   60 order by title");
        Assert.Equal("rating >= 60 ORDER BY title", p.QueryText);

        long[] ids = _store.Evaluate(p);
        Assert.Equal([_trackIds[3], _trackIds[4]], ids);
    }

    [Fact]
    public void SmartEvaluationIsCachedPerGeneration()
    {
        PlaylistInfo p = _store.CreateSmart("Cached", "rating >= 0");
        long[] first = _store.Evaluate(p);
        long[] second = _store.Evaluate(p);
        Assert.Same(first, second);

        _db.BumpLibraryGeneration();
        long[] third = _store.Evaluate(p);
        Assert.NotSame(first, third);
    }

    [Fact]
    public void PlaylistMembershipQueriesWork()
    {
        PlaylistInfo staticList = _store.CreateStatic("Picks");
        _store.AddTracks(staticList.Id, [_trackIds[1], _trackIds[3]]);

        PlaylistInfo smart = _store.CreateSmart("From Picks", """playlist = "Picks" ORDER BY title""");
        Assert.Equal([_trackIds[1], _trackIds[3]], _store.Evaluate(smart));

        PlaylistInfo inverted = _store.CreateSmart(
            "Not Picks", """NOT playlist = "Picks" ORDER BY title""");
        Assert.Equal([_trackIds[0], _trackIds[2], _trackIds[4]], _store.Evaluate(inverted));
    }

    [Fact]
    public void StaticEditsInvalidateDependentSmartResults()
    {
        PlaylistInfo staticList = _store.CreateStatic("Live");
        PlaylistInfo smart = _store.CreateSmart("Live Mirror", """playlist = "Live" """);
        Assert.Empty(_store.Evaluate(smart));

        _store.AddTracks(staticList.Id, [_trackIds[0]]);
        Assert.Equal([_trackIds[0]], _store.Evaluate(smart));
    }

    [Fact]
    public void ASmartPlaylistCanReferenceAnotherSmartPlaylist()
    {
        // Tracks are rated 0, 20, 40, 60, 80.
        _store.CreateSmart("Rated", "rating >= 40");
        PlaylistInfo narrowed = _store.CreateSmart(
            "Rated Not Top", """playlist = "Rated" AND rating < 80 ORDER BY title""");

        // A smart playlist has no entries, so this only works if the
        // reference compiles to the referenced playlist's own predicate.
        Assert.Equal([_trackIds[2], _trackIds[3]], _store.Evaluate(narrowed));
    }

    [Fact]
    public void ANegatedReferenceToASmartPlaylistExcludesIt()
    {
        _store.CreateSmart("Rated", "rating >= 40");
        PlaylistInfo rest = _store.CreateSmart(
            "Unrated", """playlist != "Rated" ORDER BY title""");

        Assert.Equal([_trackIds[0], _trackIds[1]], _store.Evaluate(rest));
    }

    [Fact]
    public void AReferencedSmartPlaylistStaysLive()
    {
        PlaylistInfo source = _store.CreateSmart("Rated", "rating >= 80");
        PlaylistInfo mirror = _store.CreateSmart("Mirror", """playlist = "Rated" """);
        Assert.Equal([_trackIds[4]], _store.Evaluate(mirror));

        _store.UpdateSmartQuery(source.Id, "rating >= 60");
        Assert.Equal([_trackIds[3], _trackIds[4]], _store.Evaluate(mirror).Order().ToArray());
    }

    [Fact]
    public void ALimitedPlaylistCannotBeReferenced()
    {
        _store.CreateSmart("Top", "rating >= 40 ORDER BY rating DESC LIMIT 1");

        // A limit picks rows out of an ordered set, and a predicate about one
        // track has no set to order — silently ignoring it would widen the
        // reference to every rated track.
        Assert.Throws<QueryCompileException>(
            () => _store.CreateSmart("Mirror", """playlist = "Top" """));
    }

    [Fact]
    public void ReferenceCyclesAreRejected()
    {
        _store.CreateStatic("Seed");
        _store.CreateSmart("A", """playlist = "B" """);
        // B -> A would close the loop A -> B -> A.
        Assert.Throws<QueryCompileException>(
            () => _store.CreateSmart("B", """playlist = "A" """));
        // Self-reference is the smallest cycle.
        Assert.Throws<QueryCompileException>(
            () => _store.CreateSmart("Self", """playlist = "Self" """));
    }

    [Fact]
    public void NamesMustBeUnique()
    {
        _store.CreateStatic("Once");
        Assert.Throws<InvalidOperationException>(() => _store.CreateStatic("Once"));
        PlaylistInfo other = _store.CreateStatic("Other");
        Assert.Throws<InvalidOperationException>(() => _store.Rename(other.Id, "Once"));
    }

    [Fact]
    public void FoldersNestAndRejectSubtreeCycles()
    {
        PlaylistInfo outer = _store.CreateFolder("Outer");
        PlaylistInfo inner = _store.CreateFolder("Inner");
        PlaylistInfo list = _store.CreateStatic("Nested");

        _store.SetParent(inner.Id, outer.Id);
        _store.SetParent(list.Id, inner.Id);
        List<PlaylistInfo> all = _store.List();
        Assert.Equal(outer.Id, all.First(p => p.Id == inner.Id).ParentId);
        Assert.Equal(inner.Id, all.First(p => p.Id == list.Id).ParentId);

        // A folder cannot move into its own subtree, and playlists cannot parent.
        Assert.Throws<InvalidOperationException>(() => _store.SetParent(outer.Id, inner.Id));
        Assert.Throws<InvalidOperationException>(() => _store.SetParent(outer.Id, outer.Id));
        Assert.Throws<InvalidOperationException>(() => _store.SetParent(inner.Id, list.Id));
    }

    [Fact]
    public void DeletingAFolderReparentsChildren()
    {
        PlaylistInfo outer = _store.CreateFolder("Keep");
        PlaylistInfo doomed = _store.CreateFolder("Doomed");
        PlaylistInfo list = _store.CreateStatic("Orphan?");
        _store.SetParent(doomed.Id, outer.Id);
        _store.SetParent(list.Id, doomed.Id);

        _store.Delete(doomed.Id);
        Assert.Equal(outer.Id, _store.List().First(p => p.Id == list.Id).ParentId);
    }

    [Fact]
    public void DeleteCascadesEntries()
    {
        PlaylistInfo p = _store.CreateStatic("Gone");
        _store.AddTracks(p.Id, _trackIds);
        _store.Delete(p.Id);
        Assert.Equal(0L, _db.Scalar(
            "SELECT count(*) FROM playlist_entry WHERE playlist_id = $p", ("$p", p.Id)));
    }
}
