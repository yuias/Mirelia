using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Mirelia.Library.Scanning;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;

namespace Mirelia.App.Services;

/// <summary>
/// Async facade over the library database. One read connection, serialized
/// by a gate so the UI thread never blocks on SQLite; scans run on their own
/// connection (WAL allows a writer alongside readers).
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Opens (creating if needed) the library at <paramref name="dbPath"/>.</summary>
    public LibraryService(string dbPath)
    {
        _dbPath = dbPath;
        _db = LibraryDatabase.Open(dbPath);
    }

    /// <summary>All track ids in a browse order.</summary>
    public Task<long[]> BrowseAllAsync(TrackSortOrder order)
        => RunAsync(() => TrackPager.BrowseAll(_db, order).ToArray());

    /// <summary>Most recently added track ids.</summary>
    public Task<long[]> BrowseRecentlyAddedAsync()
        => RunAsync(() => TrackPager.BrowseRecentlyAdded(_db).ToArray());

    /// <summary>Free-text FTS search, artist-ordered.</summary>
    public Task<long[]> SearchAsync(string text)
    {
        return RunAsync(() =>
        {
            // Built as an AST directly — no parsing, no injection surface.
            var query = new ParsedQuery(
                new FreeText(text),
                [new OrderTerm("artist", Descending: false)],
                Limit: null);
            CompiledQuery compiled = SqlCompiler.Compile(query);
            return TrackQueryRunner.Run(_db, compiled).ToArray();
        });
    }

    /// <summary>Hydrates one page of grid rows.</summary>
    public Task<Dictionary<long, TrackPageRow>> HydrateAsync(long[] ids)
        => RunAsync(() => TrackPager.HydratePage(_db, ids));

    /// <summary>Absolute file path for a track, or null if unknown.</summary>
    public Task<string?> GetPathAsync(long id)
        => RunAsync(() => TrackPager.GetTrackPath(_db, id));

    /// <summary>Total track count.</summary>
    public Task<long> CountAsync()
        => RunAsync(() => TrackPager.Count(_db));

    /// <summary>
    /// Scans a folder on its own database connection so browsing stays
    /// responsive while the scan runs.
    /// </summary>
    public Task<ScanResult> ScanAsync(string directory, IProgress<int>? progress)
    {
        return Task.Run(() =>
        {
            using var scanDb = LibraryDatabase.Open(_dbPath);
            return new LibraryScanner(scanDb).Scan(directory, progress);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        _db.Dispose();
    }

    private async Task<T> RunAsync<T>(Func<T> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(operation).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
