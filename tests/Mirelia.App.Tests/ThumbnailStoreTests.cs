using Mirelia.App.Services;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class ThumbnailStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mirelia-thumbs-{Guid.NewGuid():N}");

    private readonly string _libraryPath;

    public ThumbnailStoreTests()
    {
        Directory.CreateDirectory(_root);
        _libraryPath = Path.Combine(_root, "library.db");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void StoredThumbnailsSurviveReopening()
    {
        byte[] thumbnail = [1, 2, 3, 4];
        using (var store = new ThumbnailStore(_libraryPath))
        {
            store.Put(7, thumbnail);
        }

        // The point of the store: a cover is read and re-encoded once ever,
        // not once per launch.
        using var reopened = new ThumbnailStore(_libraryPath);
        Assert.True(reopened.TryGet(7, out byte[]? found));
        Assert.Equal(thumbnail, found);
    }

    [Fact]
    public void HavingNoCoverIsItselfWorthStoring()
    {
        using var store = new ThumbnailStore(_libraryPath);
        store.Put(7, null);

        // "No cover" costs a file read to establish, so it must be
        // distinguishable from "not looked at yet".
        Assert.True(store.TryGet(7, out byte[]? found));
        Assert.Null(found);
        Assert.False(store.TryGet(8, out _));
    }

    [Fact]
    public void RewritingATrackReplacesItsThumbnail()
    {
        using var store = new ThumbnailStore(_libraryPath);
        store.Put(7, [1, 1, 1]);
        store.Put(7, [2, 2]);

        Assert.True(store.TryGet(7, out byte[]? found));
        Assert.Equal([2, 2], found);
    }

    [Fact]
    public void ClearEmptiesTheStore()
    {
        using var store = new ThumbnailStore(_libraryPath);
        store.Put(7, [1]);
        store.Put(8, null);

        store.Clear();

        Assert.False(store.TryGet(7, out _));
        Assert.False(store.TryGet(8, out _));
    }

    [Fact]
    public void TheStoreIsBoundedAndDropsItsOldestEntries()
    {
        using var store = new ThumbnailStore(_libraryPath, maxBytes: 256 * 1024);
        byte[] thumbnail = new byte[16 * 1024];

        // Album blocks ask for one track per album, but Now Playing asks for
        // whichever track is audible, so rows accumulate with listening; the
        // store has to stay bounded on its own.
        for (int i = 0; i < 100; i++)
        {
            store.Put(i, thumbnail);
        }

        Assert.False(store.TryGet(0, out _));
        Assert.True(store.TryGet(99, out _));
    }

    [Fact]
    public void TheStoreSitsBesideItsLibrary()
    {
        // Per library, because track ids do not survive a switch — and
        // obviously deletable, because it is only a cache.
        Assert.Equal(
            Path.Combine(_root, "library.covers.db"),
            ThumbnailStore.PathFor(_libraryPath));
    }
}
