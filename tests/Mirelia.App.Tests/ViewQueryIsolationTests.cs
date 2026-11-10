using Mirelia.App.Services;
using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Xunit;

namespace Mirelia.App.Tests;

/// <summary>
/// View queries (evaluating a playlist, sorting an id array, browsing) are the
/// only reads that can run long. They get their own connection so that the
/// short reads the UI needs while a view is on screen — row hydration, cover
/// paths — do not queue behind them. Sharing one connection is what turned "a
/// slow playlist" into "the app is dead".
/// </summary>
/// <remarks>
/// Each test holds one lane with an operation that does not finish until the
/// test releases it, then requires a call on the other lane to complete. A call
/// that shared the held lane could not complete, so the result does not depend
/// on how fast the machine is.
/// </remarks>
public sealed class ViewQueryIsolationTests : IDisposable
{
    // Bounds only the failure case, where the call is stuck behind the held
    // lane; a passing run never waits on it.
    private static readonly TimeSpan HangLimit = TimeSpan.FromSeconds(30);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mirelia-iso-{Guid.NewGuid():N}");

    private readonly string _dbPath;

    public ViewQueryIsolationTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "library.db");
        Seed(_dbPath, 10);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task HydrationCompletesWhileAViewQueryIsRunning()
    {
        using var service = new LibraryService(_dbPath);
        long[] ids = await service.BrowseAllAsync(TrackSortOrder.Artist);

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> view = service.RunViewAsync(() =>
        {
            entered.SetResult();
            release.Wait();
            return 0;
        });

        try
        {
            await entered.Task;
            Dictionary<long, TrackPageRow> rows =
                await service.HydrateAsync(ids).WaitAsync(HangLimit);
            Assert.Equal(ids.Length, rows.Count);
        }
        finally
        {
            release.Set();
        }

        await view;
    }

    [Fact]
    public async Task SortingCompletesWhileTheShortReadConnectionIsBusy()
    {
        using var service = new LibraryService(_dbPath);
        long[] ids = await service.BrowseAllAsync(TrackSortOrder.Artist);

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> shortRead = service.RunAsync(() =>
        {
            entered.SetResult();
            release.Wait();
            return 0;
        });

        try
        {
            await entered.Task;
            long[] sorted = await service.SortIdsAsync(ids, TrackSortOrder.Album).WaitAsync(HangLimit);
            Assert.Equal(ids.Order(), sorted.Order());
        }
        finally
        {
            release.Set();
        }

        await shortRead;
    }

    private static void Seed(string path, int count)
    {
        using LibraryDatabase db = LibraryDatabase.Open(path);
        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        for (int i = 0; i < count; i++)
        {
            db.Execute("""
                INSERT INTO track (
                    path, path_lower, size_bytes, mtime_unix, title,
                    title_sort, title_search, duration_ms,
                    sample_rate, channels, codec, is_lossless,
                    added_unix, modified_unix)
                VALUES ($p, $p, 1, 0, $t, $t, $t, 1000, 44100, 2, 'flac', 1, 0, 0)
                """,
                ("$p", $"t{i:D6}"),
                ("$t", $"track {i:D6}"));
        }

        tx.Commit();
    }
}
