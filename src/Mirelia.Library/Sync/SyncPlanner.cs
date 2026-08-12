using System.Globalization;
using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Mirelia.Library.Playlists;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

namespace Mirelia.Library.Sync;

/// <summary>
/// Works out what a sync would do, without touching the target. Scope
/// resolution, layout, and the delta all live here so <see cref="SyncRunner"/>
/// only has to execute.
/// </summary>
public sealed class SyncPlanner(LibraryDatabase db, PlaylistStore playlists, SyncTargetStore targets)
{
    /// <summary>Cap on how many same-shaped warnings are reported individually.</summary>
    private const int MaxDetailedWarnings = 10;

    /// <summary>Substituted for an album artist that no tag supplies.</summary>
    private const string VariousArtists = "Various Artists";

    /// <summary>Computes the plan for <paramref name="target"/> against <paramref name="musicSink"/>.</summary>
    public SyncPlan Plan(
        SyncTarget target,
        ISyncFileSink musicSink,
        SyncPlanOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new SyncPlanOptions();
        SyncTargetConfig config = target.Config;
        var warnings = new List<string>();

        (List<PlaylistExport> exports, List<long> trackIds) = ResolveScope(target, warnings);
        ct.ThrowIfCancellationRequested();

        Dictionary<long, TrackLayoutInfo> tracks = LoadLayoutInfo(trackIds);
        var skippedByPolicy = new Dictionary<string, int>(StringComparer.Ordinal);
        List<long> deliverable = ApplyDeliveryPolicy(trackIds, tracks, config.Device, skippedByPolicy);
        Dictionary<long, string> layout = AssignPaths(deliverable, tracks, config, warnings);
        HashSet<long> forced = ResolveForced(options);
        ct.ThrowIfCancellationRequested();

        Dictionary<long, SyncedFile> recorded = targets.GetFiles(target.Id);
        var files = new Dictionary<long, PlannedFile>(layout.Count);
        var copies = new List<SyncCopy>();
        var deletes = new List<SyncDelete>();
        long bytes = 0;
        int unchanged = 0;
        int missingSource = 0;

        foreach (long trackId in deliverable)
        {
            ct.ThrowIfCancellationRequested();
            if (!tracks.TryGetValue(trackId, out TrackLayoutInfo? track)
                || !layout.TryGetValue(trackId, out string? relPath))
            {
                continue;
            }

            if (!File.Exists(track.SourcePath))
            {
                missingSource++;
                continue;
            }

            var planned = new PlannedFile(track, relPath);
            files[trackId] = planned;

            recorded.TryGetValue(trackId, out SyncedFile? previous);
            CopyReason? reason = ClassifyCopy(track, relPath, previous, musicSink);
            if (reason is null && (options.ForceAll || forced.Contains(trackId)))
            {
                reason = CopyReason.Forced;
            }

            if (reason is null)
            {
                unchanged++;
                continue;
            }

            string? previousPath = previous is not null && previous.RelPath != relPath
                ? previous.RelPath
                : null;
            copies.Add(new SyncCopy(planned, reason.Value, previousPath));
            bytes += track.SizeBytes;
        }

        if (config.OrphanPolicy == OrphanPolicy.Delete)
        {
            foreach ((long trackId, SyncedFile file) in recorded)
            {
                // A track whose source vanished stays on the target: the file
                // is not proven stale, only unverifiable.
                if (!layout.ContainsKey(trackId))
                {
                    deletes.Add(new SyncDelete(trackId, file.RelPath));
                }
            }
        }

        AddConfigWarnings(config, warnings);

        return new SyncPlan(
            target,
            files,
            copies,
            deletes,
            exports,
            warnings,
            bytes,
            musicSink.FreeSpaceBytes,
            unchanged,
            missingSource,
            skippedByPolicy);
    }

    /// <summary>
    /// Filters the scope through <see cref="DeliveryPolicy"/>, tallying the
    /// reasons anything was left behind. Skipped tracks never reach the
    /// layout, so a target that already holds one treats it as an orphan.
    /// </summary>
    private static List<long> ApplyDeliveryPolicy(
        List<long> trackIds,
        Dictionary<long, TrackLayoutInfo> tracks,
        DeviceProfile? profile,
        Dictionary<string, int> skipped)
    {
        if (profile is null)
        {
            return trackIds;
        }

        var deliverable = new List<long>(trackIds.Count);
        foreach (long trackId in trackIds)
        {
            if (!tracks.TryGetValue(trackId, out TrackLayoutInfo? track))
            {
                continue;
            }

            DeliveryDecision decision = DeliveryPolicy.Decide(track, profile);
            if (decision.Kind == DeliveryKind.Skip)
            {
                string reason = decision.Reason ?? "skipped";
                skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                continue;
            }

            deliverable.Add(trackId);
        }

        return deliverable;
    }

    /// <summary>Track ids covered by a one-shot force request.</summary>
    private HashSet<long> ResolveForced(SyncPlanOptions options)
    {
        var forced = new HashSet<long>();
        if (options.ForceAll || options.ForcePlaylistIds is not { Count: > 0 } requested)
        {
            return forced;
        }

        List<PlaylistInfo> all = playlists.List();
        HashSet<long> expanded = Expand([.. requested], all);
        foreach (PlaylistInfo playlist in all)
        {
            if (playlist.Kind == PlaylistKind.Folder || !expanded.Contains(playlist.Id))
            {
                continue;
            }

            foreach (long id in playlists.Evaluate(playlist))
            {
                forced.Add(id);
            }
        }

        return forced;
    }

    /// <summary>
    /// Playlists a target would write, with folders expanded to their
    /// descendants and smart playlists materialized.
    /// </summary>
    private (List<PlaylistExport> Exports, List<long> TrackIds) ResolveScope(
        SyncTarget target, List<string> warnings)
    {
        SyncTargetConfig config = target.Config;
        var exports = new List<PlaylistExport>();
        var trackIds = new List<long>();
        var seen = new HashSet<long>();

        if (config.Scope == SyncScopeKind.AllMusic)
        {
            using SqliteCommand cmd = db.Command("SELECT id FROM track ORDER BY id");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                trackIds.Add(reader.GetInt64(0));
            }

            return (exports, trackIds);
        }

        List<PlaylistInfo> all = playlists.List();
        HashSet<long>? selected = config.Scope == SyncScopeKind.Selected
            ? Expand([.. targets.GetScope(target.Id)], all)
            : null;

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PlaylistInfo playlist in all)
        {
            if (playlist.Kind == PlaylistKind.Folder
                || (selected is not null && !selected.Contains(playlist.Id)))
            {
                continue;
            }

            long[] members = playlist.Kind == PlaylistKind.Smart
                ? Materialize(playlist, warnings)
                : playlists.Evaluate(playlist);
            if (members.Length == 0)
            {
                continue;
            }

            exports.Add(new PlaylistExport(
                playlist.Id,
                PlaylistFileName(playlist, all, config, usedNames),
                members));

            foreach (long id in members)
            {
                if (seen.Add(id))
                {
                    trackIds.Add(id);
                }
            }
        }

        trackIds.Sort();
        return (exports, trackIds);
    }

    private long[] Materialize(PlaylistInfo playlist, List<string> warnings)
    {
        try
        {
            return playlists.Evaluate(playlist);
        }
        catch (Exception ex) when (ex is QueryParseException or QueryCompileException)
        {
            warnings.Add($"Smart playlist '{playlist.Name}' could not be evaluated: {ex.Message}");
            return [];
        }
    }

    /// <summary>Replaces folder selections with the playlists beneath them.</summary>
    private static HashSet<long> Expand(HashSet<long> selected, List<PlaylistInfo> all)
    {
        var byParent = all
            .Where(p => p.ParentId is not null)
            .GroupBy(p => p.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new HashSet<long>();
        var queue = new Queue<long>(selected);
        while (queue.Count > 0)
        {
            long id = queue.Dequeue();
            if (!result.Add(id) || !byParent.TryGetValue(id, out List<PlaylistInfo>? children))
            {
                continue;
            }

            foreach (PlaylistInfo child in children)
            {
                queue.Enqueue(child.Id);
            }
        }

        return result;
    }

    private static string PlaylistFileName(
        PlaylistInfo playlist,
        List<PlaylistInfo> all,
        SyncTargetConfig config,
        HashSet<string> usedNames)
    {
        string extension = config.PlaylistFormat == "m3u" ? ".m3u" : ".m3u8";
        var ancestors = new List<string>();
        for (PlaylistInfo? node = playlist; node is not null;)
        {
            ancestors.Insert(0, node.Name);
            node = node.ParentId is { } parentId ? all.FirstOrDefault(p => p.Id == parentId) : null;
        }

        string relPath;
        if (config.PlaylistFolders == PlaylistFolderMode.Mirror)
        {
            IEnumerable<string> parts = ancestors
                .Select(a => NameSanitizer.Component(a, config.ReservedChars));
            relPath = string.Join('/', parts);
        }
        else
        {
            // Devices commonly scan only the top playlist directory, so the
            // tree becomes part of the name rather than part of the path.
            relPath = NameSanitizer.Component(
                string.Join(" - ", ancestors), config.ReservedChars);
        }

        string candidate = relPath + extension;
        for (int i = 2; !usedNames.Add(candidate); i++)
        {
            candidate = $"{relPath} ({i}){extension}";
        }

        return candidate;
    }

    /// <summary>
    /// Renders every track through the layout template, resolving collisions
    /// in track-id order so the result is reproducible across runs.
    /// </summary>
    private static Dictionary<long, string> AssignPaths(
        List<long> trackIds,
        Dictionary<long, TrackLayoutInfo> tracks,
        SyncTargetConfig config,
        List<string> warnings)
    {
        var layout = new Dictionary<long, string>(trackIds.Count);
        var used = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int templateErrors = 0;
        int tooLong = 0;

        foreach (long trackId in trackIds)
        {
            if (!tracks.TryGetValue(trackId, out TrackLayoutInfo? track))
            {
                continue;
            }

            string relPath;
            try
            {
                relPath = PathTemplate.Render(config.LayoutTemplate, track, config.ReservedChars);
            }
            catch (PathTemplateException ex)
            {
                if (++templateErrors <= MaxDetailedWarnings)
                {
                    warnings.Add($"'{track.SourcePath}': {ex.Message}");
                }

                continue;
            }

            if (used.ContainsKey(relPath))
            {
                relPath = Disambiguate(relPath, track, used);
            }

            used[relPath] = trackId;
            layout[trackId] = relPath;

            if (DevicePathLength(relPath, config) > config.MaxDevicePathLength && ++tooLong <= MaxDetailedWarnings)
            {
                warnings.Add($"Device path exceeds {config.MaxDevicePathLength} characters: {relPath}");
            }
        }

        if (templateErrors > MaxDetailedWarnings)
        {
            warnings.Add($"... and {templateErrors - MaxDetailedWarnings} more template failures.");
        }

        if (tooLong > MaxDetailedWarnings)
        {
            warnings.Add($"... and {tooLong - MaxDetailedWarnings} more over-long paths.");
        }

        return layout;
    }

    /// <summary>
    /// Suffixes the album directory (or, failing that, the file name) with a
    /// short id derived from the album, so two same-named albums by different
    /// artists stay apart.
    /// </summary>
    private static string Disambiguate(
        string relPath, TrackLayoutInfo track, Dictionary<string, long> used)
    {
        string[] parts = relPath.Split('/');
        string suffix = $" [{NameSanitizer.ShortId(track.AlbumId ?? track.TrackId, 6)}]";

        if (parts.Length >= 2)
        {
            parts[^2] += suffix;
            string candidate = string.Join('/', parts);
            if (!used.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        // Same album, same rendered file name: the track itself must break it.
        string directory = parts.Length >= 2 ? string.Join('/', parts[..^1]) + "/" : string.Empty;
        string name = parts[^1];
        string extension = Path.GetExtension(name);
        string stem = name[..^extension.Length];
        return $"{directory}{stem} [{NameSanitizer.ShortId(track.TrackId, 6)}]{extension}";
    }

    private static int DevicePathLength(string relPath, SyncTargetConfig config)
    {
        string root = config.PathStyle == SyncPathStyle.Absolute && !string.IsNullOrEmpty(config.DeviceRoot)
            ? config.DeviceRoot
            : config.MusicRoot;
        return root.Length + 1 + relPath.Length;
    }

    private static CopyReason? ClassifyCopy(
        TrackLayoutInfo track, string relPath, SyncedFile? previous, ISyncFileSink sink)
    {
        if (previous is null)
        {
            return CopyReason.New;
        }

        if (!string.Equals(previous.RelPath, relPath, StringComparison.OrdinalIgnoreCase))
        {
            return CopyReason.Relocated;
        }

        if (SourceChanged(track, previous))
        {
            return CopyReason.SourceChanged;
        }

        return sink.Stat(relPath) is null ? CopyReason.MissingOnTarget : null;
    }

    private static bool SourceChanged(TrackLayoutInfo track, SyncedFile previous)
    {
        if (track.SizeBytes != previous.SizeBytes)
        {
            return true;
        }

        if (track.ContentHash is { } current && previous.ContentHash is { } stored)
        {
            return !current.AsSpan().SequenceEqual(stored);
        }

        // FAT stores timestamps at two-second resolution, so anything inside
        // that window is the same file as far as any device is concerned.
        return Math.Abs(track.MTimeUnix - previous.MTimeUnix) > 2;
    }

    private static void AddConfigWarnings(SyncTargetConfig config, List<string> warnings)
    {
        if (config.PathStyle == SyncPathStyle.Absolute && string.IsNullOrWhiteSpace(config.DeviceRoot))
        {
            warnings.Add("Absolute playlist paths are selected but no device root is set; "
                + "the PC's own music root will be written instead.");
        }

        if (config.PathStyle == SyncPathStyle.Relative
            && !string.IsNullOrEmpty(config.MusicRoot)
            && !string.IsNullOrEmpty(config.PlaylistDir)
            && !IsAncestor(config.PlaylistDir, config.MusicRoot))
        {
            warnings.Add("The playlist folder is not above the music folder, so relative paths "
                + "will start with '..'. Many devices cannot resolve those.");
        }
    }

    private static bool IsAncestor(string ancestor, string descendant)
    {
        string a = Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar);
        string d = Path.GetFullPath(descendant).TrimEnd(Path.DirectorySeparatorChar);
        return d.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a, d, StringComparison.OrdinalIgnoreCase);
    }

    private Dictionary<long, TrackLayoutInfo> LoadLayoutInfo(List<long> trackIds)
    {
        var rows = new Dictionary<long, TrackLayoutInfo>(trackIds.Count);
        if (trackIds.Count == 0)
        {
            return rows;
        }

        List<string> watchFolders = LoadWatchFolders();
        Dictionary<long, (int Discs, int Artists)> albums = LoadAlbumShape(trackIds);

        foreach (long[] chunk in trackIds.Chunk(512))
        {
            string placeholders = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using SqliteCommand cmd = db.Command($"""
                SELECT t.id, t.path, t.size_bytes, t.mtime_unix, t.content_hash,
                       t.duration_ms, t.codec, t.is_lossless,
                       t.title, ar.name, aa.name, al.name, t.album_id,
                       t.track_no, t.disc_no, g.name, t.year
                FROM track t
                LEFT JOIN artist ar ON ar.id = t.artist_id
                LEFT JOIN artist aa ON aa.id = t.album_artist_id
                LEFT JOIN album  al ON al.id = t.album_id
                LEFT JOIN genre  g  ON g.id  = t.genre_id
                WHERE t.id IN ({placeholders})
                """);
            for (int i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                string path = reader.GetString(1);
                long? albumId = reader.IsDBNull(12) ? null : reader.GetInt64(12);
                (int discs, int artists) = albumId is { } key && albums.TryGetValue(key, out var shape)
                    ? shape
                    : (1, 1);

                string? albumArtist = reader.IsDBNull(10) ? null : reader.GetString(10);

                // No compilation flag exists in the schema, so an album with
                // no album artist but several track artists is treated as one.
                if (albumArtist is null && artists > 1)
                {
                    albumArtist = VariousArtists;
                }

                rows[id] = new TrackLayoutInfo(
                    id,
                    path,
                    reader.GetInt64(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : (byte[])reader[4],
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetInt64(7) != 0,
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    albumArtist,
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    albumId,
                    reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : reader.GetInt32(14),
                    discs > 1,
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetInt32(16),
                    RelativeToWatchFolder(path, watchFolders));
            }
        }

        return rows;
    }

    private Dictionary<long, (int Discs, int Artists)> LoadAlbumShape(List<long> trackIds)
    {
        var shape = new Dictionary<long, (int, int)>();
        foreach (long[] chunk in trackIds.Chunk(512))
        {
            string placeholders = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using SqliteCommand cmd = db.Command($"""
                SELECT album_id,
                       COUNT(DISTINCT COALESCE(disc_no, 1)),
                       COUNT(DISTINCT artist_id)
                FROM track
                WHERE album_id IN (SELECT album_id FROM track WHERE id IN ({placeholders}))
                  AND album_id IS NOT NULL
                GROUP BY album_id
                """);
            for (int i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                shape[reader.GetInt64(0)] = (reader.GetInt32(1), reader.GetInt32(2));
            }
        }

        return shape;
    }

    private List<string> LoadWatchFolders()
    {
        var folders = new List<string>();
        using SqliteCommand cmd = db.Command("SELECT path FROM watch_folder");
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            folders.Add(Path.GetFullPath(reader.GetString(0)).TrimEnd(Path.DirectorySeparatorChar));
        }

        // Longest first, so a nested watch folder wins over its parent.
        folders.Sort((a, b) => b.Length.CompareTo(a.Length));
        return folders;
    }

    private static string? RelativeToWatchFolder(string path, List<string> watchFolders)
    {
        foreach (string folder in watchFolders)
        {
            if (path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return path[(folder.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>Formats a byte count for preflight display.</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(
            CultureInfo.CurrentCulture,
            $"{value:0.#} {units[unit]}");
    }
}
