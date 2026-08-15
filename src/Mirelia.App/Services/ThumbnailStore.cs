using Microsoft.Data.Sqlite;

namespace Mirelia.App.Services;

/// <summary>
/// Cover thumbnails kept on disk beside the library, so a cover is read from
/// its file and re-encoded once ever rather than once per launch.
///
/// Entries are keyed by track id, which means two tracks of the same album can
/// each hold a copy — album blocks only ever ask for one track per album, but
/// Now Playing asks for whichever track is audible, so the row count grows
/// with listening. The store is therefore capped and evicts its oldest entries;
/// it is a cache, and deleting the file loses nothing but the work to rebuild
/// it.
///
/// Nothing here validates against the source file. Re-tagged artwork keeps
/// showing the old thumbnail until the cache is cleared — the price of never
/// touching the disk on a hit, and why clearing it is offered in the UI.
/// </summary>
public sealed class ThumbnailStore : IDisposable
{
    /// <summary>Total thumbnail bytes kept before the oldest are dropped.</summary>
    public const long DefaultMaxBytes = 128 * 1024 * 1024;

    // Checking the total on every insert would mean an aggregate over the
    // whole table per cover; every so often is enough for a bound this loose.
    private const int InsertsPerSweep = 64;

    private readonly object _lock = new();
    private readonly SqliteConnection _connection;
    private readonly long _maxBytes;
    private int _insertsSinceSweep;

    /// <summary>Opens (creating if needed) the store for a library file.</summary>
    /// <param name="libraryPath">The library this store caches covers for.</param>
    /// <param name="maxBytes">
    /// Total thumbnail bytes to keep. The bound is loose either way — it is
    /// checked every <see cref="InsertsPerSweep"/> inserts, not every one — so
    /// it exists to stop unbounded growth, not to hit a number.
    /// </param>
    public ThumbnailStore(string libraryPath, long maxBytes = DefaultMaxBytes)
    {
        _maxBytes = maxBytes;
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = PathFor(libraryPath),
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous  = NORMAL;
            CREATE TABLE IF NOT EXISTS thumbnail (
                track_id     INTEGER PRIMARY KEY,
                webp         BLOB,
                created_unix INTEGER NOT NULL
            );
            """);
    }

    /// <summary>Where the store for a given library lives.</summary>
    public static string PathFor(string libraryPath)
        => Path.ChangeExtension(libraryPath, ".covers.db");

    /// <summary>
    /// The stored thumbnail for a track. <paramref name="thumbnail"/> is null
    /// both when nothing is stored and when the stored answer is "this track
    /// has no cover" — the return value tells those apart.
    /// </summary>
    public bool TryGet(long trackId, out byte[]? thumbnail)
    {
        lock (_lock)
        {
            using SqliteCommand cmd = Command(
                "SELECT webp FROM thumbnail WHERE track_id = $id", ("$id", trackId));
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                thumbnail = null;
                return false;
            }

            thumbnail = reader.IsDBNull(0) ? null : (byte[])reader.GetValue(0);
            return true;
        }
    }

    /// <summary>
    /// Stores a thumbnail, or a null one to record that the track has no
    /// cover — that answer costs a file read to recompute, so it is worth
    /// keeping too.
    /// </summary>
    public void Put(long trackId, byte[]? thumbnail)
    {
        lock (_lock)
        {
            Execute(
                """
                INSERT INTO thumbnail (track_id, webp, created_unix)
                VALUES ($id, $b, $t)
                ON CONFLICT(track_id) DO UPDATE SET
                    webp = excluded.webp, created_unix = excluded.created_unix
                """,
                ("$id", trackId),
                ("$b", thumbnail),
                ("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

            if (++_insertsSinceSweep >= InsertsPerSweep)
            {
                _insertsSinceSweep = 0;
                Sweep();
            }
        }
    }

    /// <summary>Empties the store; it refills as covers are shown again.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            Execute("DELETE FROM thumbnail");
            _insertsSinceSweep = 0;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();

    // Caller holds the lock.
    private void Sweep()
    {
        // Oldest first, until the total is back under the cap. Rows with no
        // thumbnail cost nothing and are left alone.
        Execute(
            """
            DELETE FROM thumbnail WHERE track_id IN (
                SELECT track_id FROM (
                    SELECT track_id,
                           SUM(length(webp)) OVER (
                               ORDER BY created_unix DESC, track_id DESC) AS running
                    FROM thumbnail WHERE webp IS NOT NULL)
                WHERE running > $cap)
            """,
            ("$cap", _maxBytes));
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = Command(sql, parameters);
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return cmd;
    }
}
