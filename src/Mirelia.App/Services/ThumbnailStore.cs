using Microsoft.Data.Sqlite;

namespace Mirelia.App.Services;

/// <summary>What a cached cover belongs to.</summary>
public enum CoverScope
{
    /// <summary>An album; its cover comes from the first track shown of it.</summary>
    Album,

    /// <summary>An album artist, for artist-grouped layouts.</summary>
    AlbumArtist,

    /// <summary>A single track, for tracks that belong to no group.</summary>
    Track,
}

/// <summary>
/// Cover thumbnails kept on disk beside the library, so a cover is read from
/// its file and re-encoded once ever rather than once per launch.
///
/// Entries are keyed by the group they illustrate, not by track: a cover
/// belongs to an album, and storing it per track would keep one copy per track
/// of that album. Tracks whose artwork genuinely differs from their album's
/// are read straight from the file when they play — see
/// <see cref="ArtworkCache"/> — because that is the only place a per-track
/// cover is shown.
///
/// The store is capped and evicts its oldest entries; it is a cache, and
/// deleting the file loses nothing but the work to rebuild it.
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

            -- Superseded by 'cover', which keys by group rather than by track.
            -- Dropping it outright is fine: this file is a cache.
            DROP TABLE IF EXISTS thumbnail;

            CREATE TABLE IF NOT EXISTS cover (
                scope        INTEGER NOT NULL,
                id           INTEGER NOT NULL,
                webp         BLOB,
                created_unix INTEGER NOT NULL,
                PRIMARY KEY (scope, id)
            );
            """);
    }

    /// <summary>Where the store for a given library lives.</summary>
    public static string PathFor(string libraryPath)
        => Path.ChangeExtension(libraryPath, ".covers.db");

    /// <summary>
    /// The stored thumbnail for a group. <paramref name="thumbnail"/> is null
    /// both when nothing is stored and when the stored answer is "this group
    /// has no cover" — the return value tells those apart.
    /// </summary>
    public bool TryGet(CoverScope scope, long id, out byte[]? thumbnail)
    {
        lock (_lock)
        {
            using SqliteCommand cmd = Command(
                "SELECT webp FROM cover WHERE scope = $s AND id = $id",
                ("$s", (int)scope),
                ("$id", id));
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
    /// Stores a thumbnail, or a null one to record that the group has no
    /// cover — that answer costs a file read to recompute, so it is worth
    /// keeping too.
    /// </summary>
    public void Put(CoverScope scope, long id, byte[]? thumbnail)
    {
        lock (_lock)
        {
            Execute(
                """
                INSERT INTO cover (scope, id, webp, created_unix)
                VALUES ($s, $id, $b, $t)
                ON CONFLICT(scope, id) DO UPDATE SET
                    webp = excluded.webp, created_unix = excluded.created_unix
                """,
                ("$s", (int)scope),
                ("$id", id),
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
            Execute("DELETE FROM cover");
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
            DELETE FROM cover WHERE (scope, id) IN (
                SELECT scope, id FROM (
                    SELECT scope, id,
                           SUM(length(webp)) OVER (
                               ORDER BY created_unix DESC, scope DESC, id DESC) AS running
                    FROM cover WHERE webp IS NOT NULL)
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
