using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Mirelia.Library.Database;

/// <summary>
/// The library SQLite database: opening, pragmas, migrations, and the
/// managed functions (REGEXP) the query compiler relies on.
/// </summary>
public sealed class LibraryDatabase : IDisposable
{
    private LibraryDatabase(SqliteConnection connection)
    {
        Connection = connection;
    }

    private SqliteTransaction? _activeTransaction;

    /// <summary>The open connection. Owned by this instance.</summary>
    public SqliteConnection Connection { get; }

    /// <summary>
    /// Starts a transaction. Commands created through this instance attach
    /// to it automatically; call <see cref="TransactionScope.Commit"/> before
    /// disposing or the transaction rolls back.
    /// </summary>
    public TransactionScope BeginTransaction()
    {
        if (_activeTransaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active.");
        }

        _activeTransaction = Connection.BeginTransaction();
        return new TransactionScope(this, _activeTransaction);
    }

    /// <summary>A committable transaction handle; rolls back if not committed.</summary>
    public sealed class TransactionScope : IDisposable
    {
        private readonly LibraryDatabase _db;
        private readonly SqliteTransaction _transaction;

        internal TransactionScope(LibraryDatabase db, SqliteTransaction transaction)
        {
            _db = db;
            _transaction = transaction;
        }

        /// <summary>Commits. Run no further commands inside this scope afterwards.</summary>
        public void Commit()
        {
            _transaction.Commit();
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _transaction.Dispose();
            _db._activeTransaction = null;
        }
    }

    /// <summary>
    /// Opens (creating if necessary) the database at <paramref name="path"/>,
    /// applies pragmas and pending migrations.
    /// </summary>
    public static LibraryDatabase Open(string path)
    {
        if (path != ":memory:")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();

        var db = new LibraryDatabase(connection);
        try
        {
            db.Execute("""
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous  = NORMAL;
                PRAGMA foreign_keys = ON;
                PRAGMA temp_store   = MEMORY;
                PRAGMA mmap_size    = 268435456;
                """);
            db.RequireSqliteVersion();
            db.RegisterFunctions();
            db.Migrate();
            return db;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Checks whether <paramref name="path"/> is a library this build can
    /// open, without opening it for real. Returns null when it is, otherwise
    /// a sentence explaining why not. <see cref="Open"/> adopts an empty file
    /// as a brand-new library, so a user picking the wrong file needs to be
    /// stopped before that happens.
    /// </summary>
    public static string? DescribeLibraryProblem(string path)
    {
        if (!File.Exists(path))
        {
            return $"{path} does not exist.";
        }

        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();

            using SqliteCommand meta = connection.CreateCommand();
            meta.CommandText =
                "SELECT value FROM meta WHERE key = 'schema_version'";
            if (meta.ExecuteScalar() is not string raw
                || !int.TryParse(raw, CultureInfo.InvariantCulture, out int version))
            {
                return $"{Path.GetFileName(path)} is not a Mirelia library.";
            }

            return version > Schema.CurrentVersion
                ? $"{Path.GetFileName(path)} is a v{version} library, newer than this build (v{Schema.CurrentVersion})."
                : null;
        }
        catch (SqliteException)
        {
            // No meta table, not SQLite at all, or unreadable — same answer.
            return $"{Path.GetFileName(path)} is not a Mirelia library.";
        }
    }

    /// <summary>Current value of <c>meta['library_generation']</c>.</summary>
    public long GetLibraryGeneration()
    {
        object? value = Scalar("SELECT value FROM meta WHERE key = 'library_generation'");
        return value is string s ? long.Parse(s, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Bumps the generation counter. Call after any write that could change
    /// smart-playlist membership; cached results are keyed on it.
    /// </summary>
    public void BumpLibraryGeneration()
    {
        Execute("""
            UPDATE meta SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            WHERE key = 'library_generation'
            """);
    }

    /// <summary>Runs a statement with optional $-parameters.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = Command(sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Runs a scalar query with optional $-parameters.</summary>
    public object? Scalar(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = Command(sql, parameters);
        return cmd.ExecuteScalar();
    }

    /// <summary>
    /// Asks SQLite to abandon whatever statement is running on this
    /// connection; it fails with <c>SQLITE_INTERRUPT</c> instead of running to
    /// completion. Safe to call when nothing is running, and safe from another
    /// thread — that is the point, since the caller wanting to cancel is never
    /// the thread stuck in the query.
    /// </summary>
    public void Interrupt()
    {
        if (Connection.Handle is { } handle)
        {
            SQLitePCL.raw.sqlite3_interrupt(handle);
        }
    }

    /// <summary>Whether an exception is SQLite reporting an interrupted statement.</summary>
    public static bool IsInterrupted(SqliteException exception)
        => exception.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT;

    /// <summary>Creates a command with optional $-parameters.</summary>
    public SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand cmd = Connection.CreateCommand();
        cmd.Transaction = _activeTransaction;
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Connection.Dispose();
    }

    /// <summary>
    /// Computes date_key for every row that lacks one, from date_text or
    /// year. Runs inside the v3 migration so existing libraries sort
    /// correctly without a rescan; harmless to call again.
    /// </summary>
    public void BackfillDateKeys()
    {
        var pending = new List<(long Id, string? DateText, int? Year)>();
        using (SqliteCommand cmd = Command(
            "SELECT id, date_text, year FROM track WHERE date_key IS NULL"))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                pending.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2)));
            }
        }

        foreach ((long id, string? dateText, int? year) in pending)
        {
            if (Scanning.DateKey.Compute(dateText, year) is { } key)
            {
                Execute(
                    "UPDATE track SET date_key = $k WHERE id = $id",
                    ("$k", key),
                    ("$id", id));
            }
        }
    }

    private void RequireSqliteVersion()
    {
        // contentless_delete FTS5 tables need SQLite 3.43+.
        string version = (string)Scalar("SELECT sqlite_version()")!;
        Version parsed = Version.Parse(version);
        if (parsed < new Version(3, 43))
        {
            throw new InvalidOperationException(
                $"SQLite {version} is too old; 3.43+ is required for contentless FTS deletes.");
        }
    }

    private void RegisterFunctions()
    {
        // Backs the '~=' DSL operator: X REGEXP pattern => regexp(pattern, X).
        // Bounded execution time so a hostile pattern cannot hang a query.
        Connection.CreateFunction(
            "regexp",
            (string? pattern, string? input) =>
            {
                if (pattern is null || input is null)
                {
                    return false;
                }

                try
                {
                    return Regex.IsMatch(
                        input,
                        pattern,
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(250));
                }
                catch (RegexMatchTimeoutException)
                {
                    return false;
                }
            },
            isDeterministic: true);
    }

    private void Migrate()
    {
        using TransactionScope tx = BeginTransaction();
        long tableCount = (long)Scalar(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'meta'")!;
        int version = 0;
        if (tableCount > 0)
        {
            object? raw = Scalar("SELECT value FROM meta WHERE key = 'schema_version'");
            version = raw is string s ? int.Parse(s, CultureInfo.InvariantCulture) : 0;
        }

        if (version > Schema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database schema v{version} is newer than this build (v{Schema.CurrentVersion}).");
        }

        if (version == 0)
        {
            Execute(Schema.V1);
            Execute("INSERT INTO meta(key, value) VALUES ('schema_version', '1')");
            Execute("INSERT INTO meta(key, value) VALUES ('library_generation', '0')");
            version = 1;
        }

        if (version == 1)
        {
            Execute(Schema.V2);
            Execute("UPDATE meta SET value = '2' WHERE key = 'schema_version'");
            version = 2;
        }

        if (version == 2)
        {
            Execute(Schema.V3);
            BackfillDateKeys();
            Execute("UPDATE meta SET value = '3' WHERE key = 'schema_version'");
            version = 3;
        }

        if (version == 3)
        {
            Execute(Schema.V4);
            Execute("UPDATE meta SET value = '4' WHERE key = 'schema_version'");
            version = 4;
        }

        if (version == 4)
        {
            Execute(Schema.V5);
            Execute("UPDATE meta SET value = '5' WHERE key = 'schema_version'");
            version = 5;
        }

        if (version == 5)
        {
            Execute(Schema.V6);
            Execute("UPDATE meta SET value = '6' WHERE key = 'schema_version'");
        }

        tx.Commit();
    }
}
