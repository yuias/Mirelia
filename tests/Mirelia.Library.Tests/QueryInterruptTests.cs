using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>
/// A query that runs long enough to matter must be abandonable. Without this,
/// one pathological smart playlist holds the library's gate and every later
/// view load queues behind it forever.
/// </summary>
public sealed class QueryInterruptTests : IDisposable
{
    // Counts far enough that it cannot finish while the test runs.
    private const string EndlessQuery = """
        WITH RECURSIVE counter(x) AS (
            SELECT 1 UNION ALL SELECT x + 1 FROM counter WHERE x < 100000000000)
        SELECT count(*) FROM counter
        """;

    private readonly string _dbPath;
    private readonly LibraryDatabase _db;

    public QueryInterruptTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-int-{Guid.NewGuid():N}.db");
        _db = LibraryDatabase.Open(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void InterruptStopsARunningStatement()
    {
        Exception? caught = null;
        Task query = Task.Run(() =>
        {
            try
            {
                _db.Scalar(EndlessQuery);
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });

        // Interrupting before the statement is actually executing does
        // nothing, so keep asking until it takes.
        DateTime deadline = DateTime.UtcNow.AddSeconds(20);
        while (!query.IsCompleted && DateTime.UtcNow < deadline)
        {
            _db.Interrupt();
            Thread.Sleep(20);
        }

        Assert.True(query.IsCompleted, "the query was never interrupted");
        SqliteException failure = Assert.IsType<SqliteException>(caught);
        Assert.True(
            LibraryDatabase.IsInterrupted(failure),
            $"expected an interrupt, got SQLite error {failure.SqliteErrorCode}");
    }

    [Fact]
    public void InterruptingAnIdleConnectionIsHarmless()
    {
        _db.Interrupt();

        // The connection stays usable: cancelling a reload that had already
        // finished must not poison the next one.
        Assert.Equal(0L, _db.Scalar("SELECT count(*) FROM track"));
    }
}
