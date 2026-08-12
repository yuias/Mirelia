using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;

namespace Mirelia.Library.Sync;

/// <summary>
/// Persistence for sync targets, their selected-playlist scope, and the
/// record of what each target already holds (schema v4).
/// </summary>
public sealed class SyncTargetStore(LibraryDatabase db)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>All targets, ordered by name.</summary>
    public List<SyncTarget> List()
    {
        var targets = new List<SyncTarget>();
        using SqliteCommand cmd = db.Command(
            "SELECT id, name, kind, config_json FROM sync_target ORDER BY name");
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            targets.Add(Read(reader));
        }

        return targets;
    }

    /// <summary>One target by id, or null when it has been deleted.</summary>
    public SyncTarget? Get(long id)
    {
        using SqliteCommand cmd = db.Command(
            "SELECT id, name, kind, config_json FROM sync_target WHERE id = $id",
            ("$id", id));
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>Creates a target.</summary>
    public SyncTarget Create(string name, SyncTargetKind kind, SyncTargetConfig config)
    {
        long now = Now();
        long id = (long)db.Scalar("""
            INSERT INTO sync_target (name, kind, config_json, created_unix, modified_unix)
            VALUES ($n, $k, $c, $t, $t)
            RETURNING id
            """,
            ("$n", name),
            ("$k", KindText(kind)),
            ("$c", JsonSerializer.Serialize(config, Options)),
            ("$t", now))!;

        return new SyncTarget(id, name, kind, config);
    }

    /// <summary>Replaces a target's name and settings.</summary>
    public void Update(long id, string name, SyncTargetConfig config)
    {
        db.Execute("""
            UPDATE sync_target
            SET name = $n, config_json = $c, modified_unix = $t
            WHERE id = $id
            """,
            ("$n", name),
            ("$c", JsonSerializer.Serialize(config, Options)),
            ("$t", Now()),
            ("$id", id));
    }

    /// <summary>Deletes a target; its scope and file records cascade.</summary>
    public void Delete(long id)
    {
        db.Execute("DELETE FROM sync_target WHERE id = $id", ("$id", id));
    }

    /// <summary>Playlist ids a <see cref="SyncScopeKind.Selected"/> target syncs.</summary>
    public List<long> GetScope(long targetId)
    {
        var ids = new List<long>();
        using SqliteCommand cmd = db.Command(
            "SELECT playlist_id FROM sync_target_scope WHERE target_id = $id",
            ("$id", targetId));
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>Replaces the selected-playlist scope wholesale.</summary>
    public void SetScope(long targetId, IEnumerable<long> playlistIds)
    {
        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        db.Execute("DELETE FROM sync_target_scope WHERE target_id = $id", ("$id", targetId));
        foreach (long playlistId in playlistIds.Distinct())
        {
            db.Execute(
                "INSERT INTO sync_target_scope (target_id, playlist_id) VALUES ($t, $p)",
                ("$t", targetId),
                ("$p", playlistId));
        }

        tx.Commit();
    }

    /// <summary>
    /// What the target held as of its last sync, keyed by track id. The
    /// stored size and time describe the *source* file at copy time, which is
    /// what decides whether a re-copy is due.
    /// </summary>
    public Dictionary<long, SyncedFile> GetFiles(long targetId)
    {
        var files = new Dictionary<long, SyncedFile>();
        using SqliteCommand cmd = db.Command("""
            SELECT track_id, rel_path, size_bytes, mtime_unix, content_hash
            FROM sync_target_file WHERE target_id = $id
            """,
            ("$id", targetId));
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            files[reader.GetInt64(0)] = new SyncedFile(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : (byte[])reader[4]);
        }

        return files;
    }

    /// <summary>Records (or updates) one copied file.</summary>
    public void RecordFile(long targetId, SyncedFile file)
    {
        db.Execute("""
            INSERT INTO sync_target_file
                (target_id, track_id, rel_path, rel_path_lower,
                 size_bytes, mtime_unix, content_hash, synced_unix)
            VALUES ($t, $tr, $p, $pl, $s, $m, $h, $now)
            ON CONFLICT(target_id, track_id) DO UPDATE SET
                rel_path = excluded.rel_path,
                rel_path_lower = excluded.rel_path_lower,
                size_bytes = excluded.size_bytes,
                mtime_unix = excluded.mtime_unix,
                content_hash = excluded.content_hash,
                synced_unix = excluded.synced_unix
            """,
            ("$t", targetId),
            ("$tr", file.TrackId),
            ("$p", file.RelPath),
            ("$pl", file.RelPath.ToLowerInvariant()),
            ("$s", file.SizeBytes),
            ("$m", file.MTimeUnix),
            ("$h", file.ContentHash),
            ("$now", Now()));
    }

    /// <summary>Forgets a file record, after the file itself has been removed.</summary>
    public void ForgetFile(long targetId, long trackId)
    {
        db.Execute(
            "DELETE FROM sync_target_file WHERE target_id = $t AND track_id = $tr",
            ("$t", targetId),
            ("$tr", trackId));
    }

    private static SyncTarget Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        ParseKind(reader.GetString(2)),
        Deserialize(reader.GetString(3)));

    private static SyncTargetConfig Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncTargetConfig>(json, Options) ?? new SyncTargetConfig();
        }
        catch (JsonException)
        {
            return new SyncTargetConfig();
        }
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string KindText(SyncTargetKind kind) => kind switch
    {
        SyncTargetKind.Folder => "folder",
        SyncTargetKind.Network => "network",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static SyncTargetKind ParseKind(string kind) => kind switch
    {
        "folder" => SyncTargetKind.Folder,
        "network" => SyncTargetKind.Network,
        _ => throw new InvalidOperationException($"Unknown sync target kind '{kind}'."),
    };
}

/// <summary>One file a target holds, as recorded at its last sync.</summary>
/// <param name="TrackId">Track the file came from.</param>
/// <param name="RelPath">Destination path below the music root, <c>/</c>-separated.</param>
/// <param name="SizeBytes">Source size when it was copied.</param>
/// <param name="MTimeUnix">Source modification time when it was copied.</param>
/// <param name="ContentHash">Source content hash when it was copied, if known.</param>
public sealed record SyncedFile(
    long TrackId,
    string RelPath,
    long SizeBytes,
    long MTimeUnix,
    byte[]? ContentHash);
