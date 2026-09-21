using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Xunit;

namespace Mirelia.Library.Tests;

public sealed class LibraryDatabaseTests : IDisposable
{
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in _paths)
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMireliaLibraryHasNoProblem()
    {
        string path = TempPath();
        using (LibraryDatabase.Open(path))
        {
        }

        Assert.Null(LibraryDatabase.DescribeLibraryProblem(path));
    }

    [Fact]
    public void AMissingFileIsRefused()
    {
        string path = TempPath();
        Assert.NotNull(LibraryDatabase.DescribeLibraryProblem(path));
    }

    [Fact]
    public void AFileThatIsNotSqliteIsRefused()
    {
        string path = TempPath();
        File.WriteAllText(path, "not a database");

        Assert.NotNull(LibraryDatabase.DescribeLibraryProblem(path));
    }

    [Fact]
    public void ASqliteFileWithoutTheMetaTableIsRefused()
    {
        // The interesting case: Open() would happily adopt this as a new
        // library and start creating tables in the user's unrelated file.
        string path = TempPath();
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated(id INTEGER PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        Assert.NotNull(LibraryDatabase.DescribeLibraryProblem(path));
    }

    [Fact]
    public void ANewerSchemaIsRefusedWithItsVersion()
    {
        string path = TempPath();
        int future;
        using (LibraryDatabase db = LibraryDatabase.Open(path))
        {
            future = int.Parse((string)db.Scalar(
                "SELECT value FROM meta WHERE key = 'schema_version'")!) + 1;
            db.Execute(
                "UPDATE meta SET value = $v WHERE key = 'schema_version'",
                ("$v", future.ToString()));
        }

        string? problem = LibraryDatabase.DescribeLibraryProblem(path);
        Assert.NotNull(problem);
        Assert.Contains($"v{future}", problem);
    }

    [Fact]
    public void ReopeningAV6LibraryAddsThePersistentIdColumn()
    {
        string path = TempPath();
        using (LibraryDatabase db = LibraryDatabase.Open(path))
        {
            // Wind the database back to what a v6 build left behind, so the
            // v7 step runs against a real library rather than an empty file.
            db.Execute("DROP INDEX ix_playlist_mb_persistent_id");
            db.Execute("ALTER TABLE playlist DROP COLUMN mb_persistent_id");
            db.Execute("UPDATE meta SET value = '6' WHERE key = 'schema_version'");
        }

        using (LibraryDatabase db = LibraryDatabase.Open(path))
        {
            Assert.Equal("7", db.Scalar("SELECT value FROM meta WHERE key = 'schema_version'"));
            Assert.Equal(1L, db.Scalar("""
                SELECT count(*) FROM pragma_table_info('playlist')
                WHERE name = 'mb_persistent_id'
                """));
            Assert.Equal(1L, db.Scalar("""
                SELECT count(*) FROM sqlite_master
                WHERE type = 'index' AND name = 'ix_playlist_mb_persistent_id'
                """));
        }
    }

    private string TempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mirelia-open-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }
}
