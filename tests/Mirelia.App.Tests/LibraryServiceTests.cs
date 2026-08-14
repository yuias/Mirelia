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
    private string MakeLibrary(string name)
    {
        string path = Path.Combine(_root, $"{name}.db");
        using var db = LibraryDatabase.Open(path);
        new PlaylistStore(db).CreateStatic(name);
        return path;
    }
}
