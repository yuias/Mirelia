using System.Diagnostics;
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
public sealed class ViewQueryIsolationTests : IDisposable
{
    private const int TrackCount = 40_000;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mirelia-iso-{Guid.NewGuid():N}");

    private readonly string _dbPath;

    public ViewQueryIsolationTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "library.db");
        Seed(_dbPath, TrackCount);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ShortReadsDoNotQueueBehindALongViewQuery()
    {
        using var service = new LibraryService(_dbPath);
        long[] ids = await service.BrowseAllAsync(TrackSortOrder.Artist);
        Assert.Equal(TrackCount, ids.Length);

        var clock = Stopwatch.StartNew();
        Task view = Task.Run(async () =>
        {
            // Re-sorting the whole id array is the heaviest thing the reload
            // path does: a JSON array of every id, joined against five tables.
            for (int i = 0; i < 8; i++)
            {
                await service.SortIdsAsync(ids, TrackSortOrder.Album);
            }
        });

        // Hydration is what the grid does while scrolling; it must stay
        // responsive while the view queries run.
        var shortReads = Stopwatch.StartNew();
        for (int i = 0; i < 20; i++)
        {
            await service.HydrateAsync([.. ids.Skip(i * 100).Take(100)]);
        }

        shortReads.Stop();
        await view;
        clock.Stop();

        // Serialized, the twenty short reads would take about as long as the
        // view work they were interleaved with; overlapping, they cost only
        // themselves. Half is a wide margin either way.
        Assert.True(
            shortReads.Elapsed < clock.Elapsed / 2,
            $"twenty short reads took {shortReads.ElapsedMilliseconds} ms against "
            + $"{clock.ElapsedMilliseconds} ms of view queries — they were "
            + "serialized behind them");
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
