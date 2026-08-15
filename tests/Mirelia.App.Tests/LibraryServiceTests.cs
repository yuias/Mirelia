using Mirelia.App.Services;
using Mirelia.Library.Database;
using Mirelia.Library.Playlists;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class LibraryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mirelia-switch-{Guid.NewGuid():N}");

    public LibraryServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task SwitchToMovesEveryReadToTheNewLibrary()
    {
        string alpha = MakeLibrary("alpha");
        string beta = MakeLibrary("beta");

        using var service = new LibraryService(alpha);
        Assert.Equal("alpha", (await service.GetPlaylistsAsync()).Single().Name);

        await service.SwitchToAsync(beta);

        Assert.Equal(beta, service.DatabasePath);
        Assert.Equal("beta", (await service.GetPlaylistsAsync()).Single().Name);
    }

    [Fact]
    public async Task SwitchToLeavesTheCurrentLibraryOpenWhenTheNewOneWillNotOpen()
    {
        string alpha = MakeLibrary("alpha");
        string unopenable = Path.Combine(_root, "a-directory-not-a-file");
        Directory.CreateDirectory(unopenable);

        using var service = new LibraryService(alpha);

        await Assert.ThrowsAnyAsync<Exception>(() => service.SwitchToAsync(unopenable));

        Assert.Equal(alpha, service.DatabasePath);
        Assert.Equal("alpha", (await service.GetPlaylistsAsync()).Single().Name);
    }

    /// <summary>A library carrying one playlist named after itself.</summary>
    [Fact]
    public async Task ACancelledReadIsAbandonedAndLeavesTheGateFree()
    {
        using var service = new LibraryService(MakeLibrary("alpha"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.BrowseAllAsync(
                Mirelia.Library.Querying.TrackSortOrder.Artist, cts.Token));

        // Every database call shares one gate, so a cancelled read that failed
        // to release it would wedge the whole app — which is the failure this
        // cancellation exists to prevent in the first place.
        Assert.Empty(await service.BrowseAllAsync(
            Mirelia.Library.Querying.TrackSortOrder.Artist));
    }

    private string MakeLibrary(string name)
    {
        string path = Path.Combine(_root, $"{name}.db");
        using var db = LibraryDatabase.Open(path);
        new PlaylistStore(db).CreateStatic(name);
        return path;
    }
}
