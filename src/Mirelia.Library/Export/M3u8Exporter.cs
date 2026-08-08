using System.Text;
using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;

namespace Mirelia.Library.Export;

/// <summary>Path handling and copy options for an export.</summary>
public sealed record M3u8Options
{
    /// <summary>Prefix rewrite applied to every path (e.g. drive remapping for NAS shares).</summary>
    public (string From, string To)? PathRewrite { get; init; }

    /// <summary>Write paths relative to the playlist file's directory.</summary>
    public bool RelativePaths { get; init; }

    /// <summary>
    /// Copy the audio files next to the playlist and reference the copies
    /// (relative). Overrides <see cref="PathRewrite"/> and
    /// <see cref="RelativePaths"/> since the copies define the paths.
    /// </summary>
    public bool CopyFiles { get; init; }
}

/// <summary>Outcome counters of one export.</summary>
/// <param name="Written">Entries written to the playlist.</param>
/// <param name="Missing">Tracks skipped because the source file is gone.</param>
/// <param name="Copied">Files copied (only with <see cref="M3u8Options.CopyFiles"/>).</param>
public sealed record ExportResult(int Written, int Missing, int Copied);

/// <summary>Writes Extended M3U (#EXTM3U) playlists as UTF-8 (.m3u8).</summary>
public static class M3u8Exporter
{
    /// <summary>Exports <paramref name="trackIds"/> in order to <paramref name="outPath"/>.</summary>
    public static ExportResult Export(
        LibraryDatabase db,
        IReadOnlyList<long> trackIds,
        string outPath,
        M3u8Options? options = null)
    {
        options ??= new M3u8Options();
        string outDir = Path.GetDirectoryName(Path.GetFullPath(outPath))!;
        Directory.CreateDirectory(outDir);

        Dictionary<long, (string Path, string? Title, string? Artist, long DurationMs)> rows =
            FetchRows(db, trackIds);

        var sb = new StringBuilder();
        sb.Append("#EXTM3U\n");
        int written = 0, missing = 0, copied = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (long id in trackIds)
        {
            if (!rows.TryGetValue(id, out (string Path, string? Title, string? Artist, long DurationMs) row)
                || !File.Exists(row.Path))
            {
                missing++;
                continue;
            }

            string reference;
            if (options.CopyFiles)
            {
                string name = UniqueName(usedNames, Path.GetFileName(row.Path));
                string dest = Path.Combine(outDir, name);
                File.Copy(row.Path, dest, overwrite: true);
                copied++;
                reference = name;
            }
            else
            {
                reference = row.Path;
                if (options.PathRewrite is { } rewrite
                    && reference.StartsWith(rewrite.From, StringComparison.OrdinalIgnoreCase))
                {
                    reference = rewrite.To + reference[rewrite.From.Length..];
                }

                if (options.RelativePaths)
                {
                    reference = Path.GetRelativePath(outDir, reference);
                }
            }

            int seconds = (int)Math.Round(row.DurationMs / 1000.0);
            string display = string.IsNullOrEmpty(row.Artist)
                ? row.Title ?? Path.GetFileNameWithoutExtension(row.Path)
                : $"{row.Artist} - {row.Title}";
            sb.Append($"#EXTINF:{seconds},{display}\n");
            sb.Append(reference).Append('\n');
            written++;
        }

        // UTF-8 without BOM is what .m3u8 consumers expect.
        File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));
        return new ExportResult(written, missing, copied);
    }

    private static string UniqueName(HashSet<string> used, string fileName)
    {
        if (used.Add(fileName))
        {
            return fileName;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            string candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static Dictionary<long, (string, string?, string?, long)> FetchRows(
        LibraryDatabase db, IReadOnlyList<long> trackIds)
    {
        var rows = new Dictionary<long, (string, string?, string?, long)>(trackIds.Count);
        foreach (long[] chunk in trackIds.Distinct().Chunk(512))
        {
            string placeholders = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using SqliteCommand cmd = db.Command($"""
                SELECT t.id, t.path, t.title, ar.name, t.duration_ms
                FROM track t
                LEFT JOIN artist ar ON ar.id = t.artist_id
                WHERE t.id IN ({placeholders})
                """);
            for (int i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                rows[reader.GetInt64(0)] = (
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4));
            }
        }

        return rows;
    }
}
