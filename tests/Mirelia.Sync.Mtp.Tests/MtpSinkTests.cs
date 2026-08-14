using System.Text;
using Mirelia.Library.Sync;
using Xunit;

namespace Mirelia.Sync.Mtp.Tests;

/// <summary>
/// Everything the MTP sink does that a real device would not tell us anything
/// extra about: path translation, the directory cache, and the order of the
/// calls an overwrite makes.
/// </summary>
public sealed class MtpSinkTests : IDisposable
{
    private const string RootPath = "Internal shared storage/Music";

    private readonly FakeMtpObjectStore _store = new();
    private readonly List<string> _tempFiles = [];

    /// <summary>Sets up the storage every test roots its sink on.</summary>
    public MtpSinkTests() => _store.AddStorage("Internal shared storage");

    /// <inheritdoc />
    public void Dispose()
    {
        _store.Dispose();
        foreach (string path in _tempFiles)
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RootNamesTheDeviceAndThePathInsideIt()
    {
        using var sink = new MtpSink(_store, RootPath);

        Assert.Equal(@"Fake DAP\Internal shared storage\Music", sink.Root);
    }

    [Fact]
    public void StatReportsSizeAndNoModificationTime()
    {
        _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[42]);
        using var sink = new MtpSink(_store, RootPath);

        SinkEntry? entry = sink.Stat("Artist/Album/01 Song.flac");

        Assert.Equal(new SinkEntry(42, 0), entry);
    }

    [Fact]
    public void StatMatchesNamesWithoutRegardToCase()
    {
        _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[7]);
        using var sink = new MtpSink(_store, RootPath);

        Assert.NotNull(sink.Stat("ARTIST/album/01 SONG.FLAC"));
    }

    [Fact]
    public void StatIsNullForAnAbsentFile()
    {
        _store.AddFolder($"{RootPath}/Artist");
        using var sink = new MtpSink(_store, RootPath);

        Assert.Null(sink.Stat("Artist/Missing.flac"));
        Assert.Null(sink.Stat("Nowhere/Missing.flac"));
    }

    [Fact]
    public void StatIsNullForADirectory()
    {
        _store.AddFolder($"{RootPath}/Artist/Album");
        using var sink = new MtpSink(_store, RootPath);

        Assert.Null(sink.Stat("Artist/Album"));
    }

    [Fact]
    public void StatDoesNotCreateTheRoot()
    {
        using var sink = new MtpSink(_store, RootPath);

        Assert.Null(sink.Stat("Artist/Album/01 Song.flac"));
        Assert.DoesNotContain(_store.Log, e => e.StartsWith("mkdir:", StringComparison.Ordinal));
    }

    [Fact]
    public void EachDirectoryIsListedOnlyOnce()
    {
        string album = _store.AddFolder($"{RootPath}/Artist/Album");
        _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[1]);
        _store.AddFile($"{RootPath}/Artist/Album/02 Song.flac", new byte[1]);
        using var sink = new MtpSink(_store, RootPath);

        for (int i = 0; i < 5; i++)
        {
            sink.Stat("Artist/Album/01 Song.flac");
            sink.Stat("Artist/Album/02 Song.flac");
        }

        Assert.Equal(1, _store.ListingsOf(album));
    }

    [Fact]
    public void WriteFileCreatesTheMissingDirectories()
    {
        string source = WriteTempFile("hello"u8.ToArray());
        using var sink = new MtpSink(_store, RootPath);

        sink.WriteFile("Artist/Album/01 Song.flac", source, CancellationToken.None);

        Assert.Equal("hello"u8.ToArray(), _store.Read($"{RootPath}/Artist/Album/01 Song.flac"));
    }

    [Fact]
    public void ADirectoryJustCreatedIsNotListed()
    {
        string source = WriteTempFile([1, 2, 3]);
        using var sink = new MtpSink(_store, RootPath);

        sink.WriteFile("Artist/Album/01 Song.flac", source, CancellationToken.None);
        sink.WriteFile("Artist/Album/02 Song.flac", source, CancellationToken.None);

        // Only the device root and the storage were listed: Music, Artist and
        // Album were all made here, so nothing can be inside them.
        Assert.Equal(2, _store.Log.Count(e => e.StartsWith("children:", StringComparison.Ordinal)));
    }

    [Fact]
    public void OverwriteDeletesTheOldObjectFirst()
    {
        string stale = _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[3]);
        string source = WriteTempFile("fresh"u8.ToArray());
        using var sink = new MtpSink(_store, RootPath);

        sink.WriteFile("Artist/Album/01 Song.flac", source, CancellationToken.None);

        // MTP has no truncate, and a same-named create leaves two objects.
        int deleted = _store.Log.IndexOf($"delete:{stale}");
        int created = _store.Log.FindIndex(e => e.StartsWith("create:", StringComparison.Ordinal));
        Assert.True(deleted >= 0 && deleted < created);
        Assert.False(_store.Exists(stale));
        Assert.Equal("fresh"u8.ToArray(), _store.Read($"{RootPath}/Artist/Album/01 Song.flac"));
    }

    [Fact]
    public void OverwriteLeavesOneObjectBehind()
    {
        _store.AddFile($"{RootPath}/Artist/01 Song.flac", new byte[3]);
        string source = WriteTempFile(new byte[9]);
        using var sink = new MtpSink(_store, RootPath);

        sink.WriteFile("Artist/01 Song.flac", source, CancellationToken.None);
        sink.WriteFile("Artist/01 Song.flac", source, CancellationToken.None);

        Assert.Equal(new SinkEntry(9, 0), sink.Stat("Artist/01 Song.flac"));
        Assert.Single(sink.Enumerate());
    }

    [Fact]
    public void WriteBytesLandsAtTheGivenPath()
    {
        using var sink = new MtpSink(_store, "Internal shared storage/Playlists");

        sink.WriteBytes("Favorites.m3u8", Encoding.UTF8.GetBytes("#EXTM3U\r\n"));

        Assert.Equal(
            Encoding.UTF8.GetBytes("#EXTM3U\r\n"),
            _store.Read("Internal shared storage/Playlists/Favorites.m3u8"));
    }

    [Fact]
    public void CancellationLeavesNothingBehind()
    {
        string source = WriteTempFile(new byte[4096]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var sink = new MtpSink(_store, RootPath);

        Assert.Throws<OperationCanceledException>(
            () => sink.WriteFile("Artist/Album/01 Song.flac", source, cts.Token));
        Assert.Null(_store.Read($"{RootPath}/Artist/Album/01 Song.flac"));
        Assert.Null(sink.Stat("Artist/Album/01 Song.flac"));
    }

    [Fact]
    public void DeleteRemovesTheFileAndTheDirectoriesItEmpties()
    {
        _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[1]);
        string album = _store.AddFolder($"{RootPath}/Artist/Album");
        string artist = _store.AddFolder($"{RootPath}/Artist");
        string root = _store.AddFolder(RootPath);
        using var sink = new MtpSink(_store, RootPath);

        sink.Delete("Artist/Album/01 Song.flac");

        Assert.False(_store.Exists(album));
        Assert.False(_store.Exists(artist));

        // The root is configuration, not something a sync gets to remove.
        Assert.True(_store.Exists(root));
    }

    [Fact]
    public void DeleteKeepsDirectoriesThatStillHoldSomething()
    {
        _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[1]);
        _store.AddFile($"{RootPath}/Artist/Album/02 Song.flac", new byte[1]);
        string album = _store.AddFolder($"{RootPath}/Artist/Album");
        using var sink = new MtpSink(_store, RootPath);

        sink.Delete("Artist/Album/01 Song.flac");

        Assert.True(_store.Exists(album));
        Assert.Equal(new[] { "Artist/Album/02 Song.flac" }, sink.Enumerate());
    }

    [Fact]
    public void DeletingWhatIsNotThereIsNotAnError()
    {
        using var sink = new MtpSink(_store, RootPath);

        sink.Delete("Artist/Album/01 Song.flac");

        Assert.DoesNotContain(_store.Log, e => e.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void EnumerateWalksTheWholeRoot()
    {
        _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[1]);
        _store.AddFile($"{RootPath}/Artist/Album/Disc 2/01 Song.flac", new byte[1]);
        _store.AddFile($"{RootPath}/Loose.mp3", new byte[1]);
        _store.AddFile("Internal shared storage/Elsewhere.mp3", new byte[1]);
        using var sink = new MtpSink(_store, RootPath);

        string[] expected =
        [
            "Artist/Album/01 Song.flac",
            "Artist/Album/Disc 2/01 Song.flac",
            "Loose.mp3",
        ];
        Assert.Equal(expected, sink.Enumerate().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EnumerateIsEmptyWhenTheRootDoesNotExist()
    {
        using var sink = new MtpSink(_store, RootPath);

        Assert.Empty(sink.Enumerate());
    }

    [Fact]
    public void FreeSpaceComesFromTheStorageObject()
    {
        using var sink = new MtpSink(_store, RootPath);

        Assert.Equal(1024L * 1024 * 1024, sink.FreeSpaceBytes);

        // The music root need not exist for the storage to report its space.
        Assert.DoesNotContain(_store.Log, e => e.StartsWith("mkdir:", StringComparison.Ordinal));
    }

    [Fact]
    public void FreeSpaceIsNullWhenTheDeviceDoesNotReportIt()
    {
        _store.FreeSpace = null;
        using var sink = new MtpSink(_store, RootPath);

        Assert.Null(sink.FreeSpaceBytes);
    }

    [Fact]
    public void FreeSpaceIsNullWhenTheStorageIsAbsent()
    {
        using var sink = new MtpSink(_store, "SD card/Music");

        Assert.Null(sink.FreeSpaceBytes);
    }

    [Fact]
    public void WritingToAnAbsentStorageFails()
    {
        string source = WriteTempFile([1]);
        using var sink = new MtpSink(_store, "SD card/Music");

        IOException error = Assert.Throws<IOException>(
            () => sink.WriteFile("Artist/01 Song.flac", source, CancellationToken.None));
        Assert.Contains("SD card", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceErrorOnOneFileStaysAnIOException()
    {
        string album = _store.AddFolder($"{RootPath}/Artist/Album");
        string source = WriteTempFile([1]);
        using var sink = new MtpSink(_store, RootPath);
        sink.Stat("Artist/Album/01 Song.flac");
        _store.FailingObjectIds.Add(album);

        // SyncRunner continues past IOException per file; anything else would
        // turn one unwritable object into a dead sync.
        Assert.Throws<IOException>(
            () => sink.WriteFile("Artist/Album/01 Song.flac", source, CancellationToken.None));
    }

    [Fact]
    public void APathLeavingTheRootIsRejected()
    {
        using var sink = new MtpSink(_store, RootPath);

        Assert.Throws<InvalidOperationException>(() => sink.Stat("../Elsewhere.mp3"));
    }

    [Fact]
    public void BackslashesAreAcceptedAsSeparators()
    {
        _store.AddFile($"{RootPath}/Artist/Album/01 Song.flac", new byte[5]);
        using var sink = new MtpSink(_store, RootPath);

        Assert.Equal(new SinkEntry(5, 0), sink.Stat(@"Artist\Album\01 Song.flac"));
    }

    private string WriteTempFile(byte[] content)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }
}
