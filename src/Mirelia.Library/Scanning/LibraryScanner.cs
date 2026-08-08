using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Mirelia.Query.Text;

namespace Mirelia.Library.Scanning;

/// <summary>
/// Walks watch folders and mirrors audio files into the database. Unchanged
/// files (same size and mtime) are skipped without opening them; user state
/// (rating, play counts) is never touched by a rescan.
/// </summary>
public sealed class LibraryScanner(LibraryDatabase db)
{
    private const int BatchSize = 500;

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".mp4", ".flac", ".wav", ".ogg", ".oga",
    };

    // Per-scan intern caches; names repeat heavily within one library.
    private readonly Dictionary<string, long> _artistCache = [];
    private readonly Dictionary<(string Name, long? ArtistId), long> _albumCache = [];
    private readonly Dictionary<string, long> _genreCache = [];

    /// <summary>
    /// Scans an explicit set of files and/or directories (recursive), e.g.
    /// from an Explorer drag-drop. Non-audio entries are ignored.
    /// </summary>
    public ScanResult ScanPaths(IEnumerable<string> paths, IProgress<int>? progress = null)
    {
        int added = 0, updated = 0, unchanged = 0, skipped = 0, processed = 0;
        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();

        foreach (string path in EnumeratePaths(paths))
        {
            switch (ScanOne(path))
            {
                case FileOutcome.Added:
                    added++;
                    break;
                case FileOutcome.Updated:
                    updated++;
                    break;
                case FileOutcome.Unchanged:
                    unchanged++;
                    break;
                case FileOutcome.Skipped:
                    skipped++;
                    break;
            }

            progress?.Report(++processed);
        }

        if (added + updated > 0)
        {
            db.BumpLibraryGeneration();
        }

        tx.Commit();
        return new ScanResult(added, updated, unchanged, skipped);
    }

    private static IEnumerable<string> EnumeratePaths(IEnumerable<string> paths)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", options))
                {
                    if (Extensions.Contains(Path.GetExtension(file)))
                    {
                        yield return file;
                    }
                }
            }
            else if (File.Exists(path) && Extensions.Contains(Path.GetExtension(path)))
            {
                yield return path;
            }
        }
    }

    /// <summary>Scans <paramref name="root"/> recursively.</summary>
    public ScanResult Scan(string root, IProgress<int>? progress = null)
    {
        int added = 0, updated = 0, unchanged = 0, skipped = 0, processed = 0;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
        };

        LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        try
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                if (!Extensions.Contains(Path.GetExtension(path)))
                {
                    continue;
                }

                switch (ScanOne(path))
                {
                    case FileOutcome.Added:
                        added++;
                        break;
                    case FileOutcome.Updated:
                        updated++;
                        break;
                    case FileOutcome.Unchanged:
                        unchanged++;
                        break;
                    case FileOutcome.Skipped:
                        skipped++;
                        break;
                }

                processed++;
                progress?.Report(processed);

                if (processed % BatchSize == 0)
                {
                    tx.Commit();
                    tx.Dispose();
                    tx = db.BeginTransaction();
                }
            }

            if (added + updated > 0)
            {
                db.BumpLibraryGeneration();
            }

            tx.Commit();
        }
        finally
        {
            tx.Dispose();
        }

        return new ScanResult(added, updated, unchanged, skipped);
    }

    private enum FileOutcome
    {
        Added,
        Updated,
        Unchanged,
        Skipped,
    }

    private FileOutcome ScanOne(string path)
    {
        var info = new FileInfo(path);
        string fullPath = info.FullName;
        string pathLower = fullPath.ToLowerInvariant();
        long mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();

        long? existingId = null;
        using (SqliteCommand cmd = db.Command(
            "SELECT id, size_bytes, mtime_unix FROM track WHERE path_lower = $p",
            ("$p", pathLower)))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            if (reader.Read())
            {
                existingId = reader.GetInt64(0);
                if (reader.GetInt64(1) == info.Length && reader.GetInt64(2) == mtime)
                {
                    return FileOutcome.Unchanged;
                }
            }
        }

        TrackMetadata meta;
        try
        {
            meta = TagReader.Read(fullPath);
        }
        catch (Exception ex) when (ex is NotSupportedException
            or TagLib.CorruptFileException
            or TagLib.UnsupportedFormatException
            or IOException)
        {
            return FileOutcome.Skipped;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string displayTitle = meta.Title ?? Path.GetFileNameWithoutExtension(fullPath);
        string titleSort = meta.TitleSort is { } explicitSort
            ? TextNormalizer.NormalizeSearch(explicitSort)
            : TextNormalizer.DeriveSortKey(displayTitle);

        // Containers disagree on where "the artist" lives (RIFF INFO's IART
        // surfaces as album artist in TagLib#), so each side backfills the other.
        string? effectiveArtist = meta.Artist ?? meta.AlbumArtist;
        string? effectiveArtistSort = meta.ArtistSort ?? meta.AlbumArtistSort;
        long? artistId = InternArtist(effectiveArtist, effectiveArtistSort);
        long? albumArtistId = InternArtist(meta.AlbumArtist ?? meta.Artist, meta.AlbumArtistSort ?? meta.ArtistSort);
        long? composerId = InternArtist(meta.Composer, meta.ComposerSort);
        long? genreId = InternGenre(meta.Genre);
        long? albumId = InternAlbum(meta.Album, meta.AlbumSort, albumArtistId);

        long trackId;
        using (SqliteCommand cmd = db.Command("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix,
                title, album_id, artist_id, album_artist_id, composer_id, genre_id,
                track_no, disc_no, year,
                title_sort, title_search,
                duration_ms, sample_rate, channels, bit_depth, bitrate_kbps,
                codec, is_lossless,
                added_unix, modified_unix)
            VALUES (
                $path, $path_lower, $size, $mtime,
                $title, $album_id, $artist_id, $album_artist_id, $composer_id, $genre_id,
                $track_no, $disc_no, $year,
                $title_sort, $title_search,
                $duration_ms, $sample_rate, $channels, $bit_depth, $bitrate_kbps,
                $codec, $is_lossless,
                $now, $now)
            ON CONFLICT(path_lower) DO UPDATE SET
                path = excluded.path,
                size_bytes = excluded.size_bytes,
                mtime_unix = excluded.mtime_unix,
                title = excluded.title,
                album_id = excluded.album_id,
                artist_id = excluded.artist_id,
                album_artist_id = excluded.album_artist_id,
                composer_id = excluded.composer_id,
                genre_id = excluded.genre_id,
                track_no = excluded.track_no,
                disc_no = excluded.disc_no,
                year = excluded.year,
                title_sort = excluded.title_sort,
                title_search = excluded.title_search,
                duration_ms = excluded.duration_ms,
                sample_rate = excluded.sample_rate,
                channels = excluded.channels,
                bit_depth = excluded.bit_depth,
                bitrate_kbps = excluded.bitrate_kbps,
                codec = excluded.codec,
                is_lossless = excluded.is_lossless,
                modified_unix = excluded.modified_unix
            RETURNING id
            """,
            ("$path", fullPath),
            ("$path_lower", pathLower),
            ("$size", info.Length),
            ("$mtime", mtime),
            ("$title", meta.Title),
            ("$album_id", albumId),
            ("$artist_id", artistId),
            ("$album_artist_id", albumArtistId),
            ("$composer_id", composerId),
            ("$genre_id", genreId),
            ("$track_no", meta.TrackNo),
            ("$disc_no", meta.DiscNo),
            ("$year", meta.Year),
            ("$title_sort", titleSort),
            ("$title_search", TextNormalizer.NormalizeSearch(displayTitle)),
            ("$duration_ms", meta.DurationMs),
            ("$sample_rate", meta.SampleRate),
            ("$channels", meta.Channels),
            ("$bit_depth", meta.BitDepth),
            ("$bitrate_kbps", meta.BitrateKbps),
            ("$codec", meta.Codec),
            ("$is_lossless", meta.IsLossless ? 1 : 0),
            ("$now", now)))
        {
            trackId = (long)cmd.ExecuteScalar()!;
        }

        UpdateFts(trackId, isNew: existingId is null, meta, displayTitle);
        return existingId is null ? FileOutcome.Added : FileOutcome.Updated;
    }

    private void UpdateFts(long trackId, bool isNew, TrackMetadata meta, string displayTitle)
    {
        if (!isNew)
        {
            db.Execute("DELETE FROM track_fts WHERE rowid = $id", ("$id", trackId));
        }

        db.Execute("""
            INSERT INTO track_fts (rowid, title, artist, album, album_artist, composer, genre)
            VALUES ($id, $title, $artist, $album, $album_artist, $composer, $genre)
            """,
            ("$id", trackId),
            ("$title", displayTitle),
            ("$artist", meta.Artist ?? meta.AlbumArtist ?? string.Empty),
            ("$album", meta.Album ?? string.Empty),
            ("$album_artist", meta.AlbumArtist ?? meta.Artist ?? string.Empty),
            ("$composer", meta.Composer ?? string.Empty),
            ("$genre", meta.Genre ?? string.Empty));
    }

    private long? InternArtist(string? name, string? explicitSort)
    {
        return Intern(_artistCache, "artist", name, explicitSort);
    }

    private long? InternGenre(string? name)
    {
        return Intern(_genreCache, "genre", name, explicitSort: null);
    }

    private long? Intern(
        Dictionary<string, long> cache, string table, string? name, string? explicitSort)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();
        if (cache.TryGetValue(name, out long cached))
        {
            return cached;
        }

        // Table names come from the two literal call sites above, never user input.
        object? found = db.Scalar($"SELECT id FROM {table} WHERE name = $n", ("$n", name));
        long id = found is long f
            ? f
            : (long)db.Scalar(
                $"INSERT INTO {table} (name, name_sort, name_search) VALUES ($n, $sort, $search) RETURNING id",
                ("$n", name),
                ("$sort", explicitSort is not null
                    ? TextNormalizer.NormalizeSearch(explicitSort)
                    : TextNormalizer.DeriveSortKey(name)),
                ("$search", TextNormalizer.NormalizeSearch(name)))!;
        cache[name] = id;
        return id;
    }

    private long? InternAlbum(string? name, string? explicitSort, long? albumArtistId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();
        (string, long?) key = (name, albumArtistId);
        if (_albumCache.TryGetValue(key, out long cached))
        {
            return cached;
        }

        object? found = db.Scalar(
            "SELECT id FROM album WHERE name = $n AND album_artist_id IS $a",
            ("$n", name),
            ("$a", albumArtistId));
        long id = found is long f
            ? f
            : (long)db.Scalar("""
                INSERT INTO album (name, name_sort, name_search, album_artist_id)
                VALUES ($n, $sort, $search, $a) RETURNING id
                """,
                ("$n", name),
                ("$sort", explicitSort is not null
                    ? TextNormalizer.NormalizeSearch(explicitSort)
                    : TextNormalizer.DeriveSortKey(name)),
                ("$search", TextNormalizer.NormalizeSearch(name)),
                ("$a", albumArtistId))!;
        _albumCache[key] = id;
        return id;
    }
}
