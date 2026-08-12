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

    /// <summary>Separator written into the file; null keeps whatever the paths already use.</summary>
    public char? Separator { get; init; }

    /// <summary>Text encoding; defaults to UTF-8 without BOM (the <c>.m3u8</c> contract).</summary>
    public Encoding? Encoding { get; init; }
}

/// <summary>One playlist line: its path reference and the <c>#EXTINF</c> data.</summary>
/// <param name="Reference">Path as written into the file.</param>
/// <param name="Display">Text after the duration, conventionally "artist - title".</param>
/// <param name="Seconds">Duration in whole seconds; -1 when unknown.</param>
public sealed record M3uEntry(string Reference, string Display, int Seconds);

/// <summary>Outcome counters of one export.</summary>
/// <param name="Written">Entries written to the playlist.</param>
/// <param name="Missing">Tracks skipped because the source file is gone.</param>
/// <param name="Copied">Files copied (only with <see cref="M3u8Options.CopyFiles"/>).</param>
public sealed record ExportResult(int Written, int Missing, int Copied);

/// <summary>
/// Writes Extended M3U playlists. UTF-8 without BOM by default (<c>.m3u8</c>);
/// legacy code pages are available for devices that need them.
/// </summary>
public static class M3u8Exporter
{
    static M3u8Exporter()
    {
        // Shift_JIS and the windows-125x family ship in this provider, not in-box.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>UTF-8 without a byte order mark: more players break on one than need it.</summary>
    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Encoding for a code page, falling back to UTF-8 when the platform does
    /// not know it.
    /// </summary>
    public static Encoding EncodingForCodepage(int codepage)
    {
        try
        {
            // Unrepresentable characters become '?' rather than throwing: a
            // playlist with a mangled title still plays, an exception does not.
            return Encoding.GetEncoding(
                codepage,
                EncoderFallback.ReplacementFallback,
                DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Utf8NoBom;
        }
    }

    /// <summary>Renders entries to the bytes of an Extended M3U file.</summary>
    /// <remarks>
    /// CRLF throughout: some devices treat a bare LF as part of the path and
    /// then cannot find the file.
    /// </remarks>
    public static byte[] Render(IReadOnlyList<M3uEntry> entries, Encoding? encoding = null)
    {
        var sb = new StringBuilder();
        sb.Append("#EXTM3U\r\n");
        foreach (M3uEntry entry in entries)
        {
            sb.Append("#EXTINF:").Append(entry.Seconds).Append(',').Append(entry.Display).Append("\r\n");
            sb.Append(entry.Reference).Append("\r\n");
        }

        return (encoding ?? Utf8NoBom).GetBytes(sb.ToString());
    }

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

        Dictionary<long, TrackLine> rows = FetchRows(db, trackIds);

        var entries = new List<M3uEntry>(trackIds.Count);
        int missing = 0, copied = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (long id in trackIds)
        {
            if (!rows.TryGetValue(id, out TrackLine row) || !File.Exists(row.Path))
            {
                missing++;
                continue;
            }

            string reference;
            if (options.CopyFiles)
            {
                string name = UniqueName(usedNames, Path.GetFileName(row.Path));
                File.Copy(row.Path, Path.Combine(outDir, name), overwrite: true);
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

            if (options.Separator is { } separator)
            {
                reference = reference.Replace('\\', separator).Replace('/', separator);
            }

            entries.Add(new M3uEntry(reference, Describe(row), (int)Math.Round(row.DurationMs / 1000.0)));
        }

        File.WriteAllBytes(outPath, Render(entries, options.Encoding));
        return new ExportResult(entries.Count, missing, copied);
    }

    private static string Describe(TrackLine row) =>
        string.IsNullOrEmpty(row.Artist)
            ? row.Title ?? Path.GetFileNameWithoutExtension(row.Path)
            : $"{row.Artist} - {row.Title}";

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

    private readonly record struct TrackLine(string Path, string? Title, string? Artist, long DurationMs);

    private static Dictionary<long, TrackLine> FetchRows(
        LibraryDatabase db, IReadOnlyList<long> trackIds)
    {
        var rows = new Dictionary<long, TrackLine>(trackIds.Count);
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
                rows[reader.GetInt64(0)] = new TrackLine(
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4));
            }
        }

        return rows;
    }
}
