using System.Globalization;
using System.Text;

namespace Mirelia.Library.Sync;

/// <summary>Raised when a layout template cannot be parsed or uses an unknown token.</summary>
public sealed class PathTemplateException(string message) : Exception(message);

/// <summary>Everything the layout template and the delta need about one track.</summary>
/// <param name="TrackId">Track id.</param>
/// <param name="SourcePath">Absolute path of the file on this PC.</param>
/// <param name="SizeBytes">Source size, for delta comparison.</param>
/// <param name="MTimeUnix">Source modification time, for delta comparison.</param>
/// <param name="ContentHash">Source content hash when one has been computed.</param>
/// <param name="DurationMs">Playing time, for the playlist's <c>#EXTINF</c> line.</param>
/// <param name="Title">Track title.</param>
/// <param name="Artist">Track artist.</param>
/// <param name="AlbumArtist">Album artist, already resolved to "Various Artists" for compilations.</param>
/// <param name="Album">Album name.</param>
/// <param name="AlbumId">Album id; the source of the collision suffix.</param>
/// <param name="TrackNo">Track number within the disc.</param>
/// <param name="DiscNo">Disc number.</param>
/// <param name="MultiDisc">Whether the album spans more than one disc.</param>
/// <param name="Genre">Genre name.</param>
/// <param name="Year">Release year.</param>
/// <param name="SourceRelative">Path relative to the containing watch folder, or null when outside one.</param>
public sealed record TrackLayoutInfo(
    long TrackId,
    string SourcePath,
    long SizeBytes,
    long MTimeUnix,
    byte[]? ContentHash,
    long DurationMs,
    string? Title,
    string? Artist,
    string? AlbumArtist,
    string? Album,
    long? AlbumId,
    int? TrackNo,
    int? DiscNo,
    bool MultiDisc,
    string? Genre,
    int? Year,
    string? SourceRelative);

/// <summary>
/// Expands a destination layout template into a relative path. Output uses
/// <c>/</c> separators regardless of host or device convention; sinks and the
/// playlist writer convert on the way out.
/// </summary>
public static class PathTemplate
{
    /// <summary>Template applied when a target does not choose another.</summary>
    public const string Default = "{AlbumArtist}/{Album}/{Disc?}{Track:00} {Title}";

    /// <summary>Everything under one directory per album artist and album.</summary>
    public const string PresetArtistAlbum = Default;

    /// <summary>One directory per album, named for both artist and album.</summary>
    public const string PresetFlatAlbum = "{AlbumArtist} - {Album}/{Disc?}{Track:00} {Title}";

    /// <summary>Reproduces the layout the files already have under their watch folder.</summary>
    public const string PresetMirrorSource = "{SourceRelative}";

    /// <summary>Substituted for an album artist that no tag supplies.</summary>
    public const string UnknownArtist = "Unknown Artist";

    /// <summary>Substituted for an album name that no tag supplies.</summary>
    public const string UnknownAlbum = "Unknown Album";

    /// <summary>
    /// Renders <paramref name="info"/> through <paramref name="template"/>,
    /// sanitizing each component. The source file's extension is appended
    /// unless the template produced one.
    /// </summary>
    /// <exception cref="PathTemplateException">Malformed or unknown token.</exception>
    public static string Render(string template, TrackLayoutInfo info, ReservedCharMode mode)
    {
        string extension = Path.GetExtension(info.SourcePath);

        // {SourceRelative} is already a sanitized, real path — re-running the
        // component rules over it would rewrite names that demonstrably work.
        if (template.Trim() == PresetMirrorSource && info.SourceRelative is { } relative)
        {
            return relative.Replace('\\', '/');
        }

        string expanded = Expand(template, info);
        string[] parts = expanded.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new PathTemplateException("Template expanded to an empty path.");
        }

        var components = new string[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            bool hasExtension = last
                && extension.Length > 0
                && parts[i].EndsWith(extension, StringComparison.OrdinalIgnoreCase);
            string raw = hasExtension ? parts[i][..^extension.Length] : parts[i];
            components[i] = NameSanitizer.Component(raw, mode, last ? extension : string.Empty);
        }

        return string.Join('/', components);
    }

    /// <summary>
    /// Parses <paramref name="template"/> without needing a track, for
    /// validating what the user typed. Returns the error text, or null when
    /// the template is usable.
    /// </summary>
    public static string? Validate(string template)
    {
        var probe = new TrackLayoutInfo(
            1, "probe.flac", 0, 0, null, 0, "Title", "Artist", "Album Artist", "Album",
            1, 1, 1, false, "Genre", 2000, "Artist/Album/probe.flac");
        try
        {
            Render(template, probe, ReservedCharMode.Underscore);
            return null;
        }
        catch (PathTemplateException ex)
        {
            return ex.Message;
        }
    }

    private static string Expand(string template, TrackLayoutInfo info)
    {
        var sb = new StringBuilder(template.Length + 32);
        int i = 0;
        while (i < template.Length)
        {
            char c = template[i];
            if (c != '{')
            {
                if (c == '}')
                {
                    throw new PathTemplateException($"Unmatched '}}' at position {i}.");
                }

                sb.Append(c);
                i++;
                continue;
            }

            int end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                throw new PathTemplateException($"Unmatched '{{' at position {i}.");
            }

            string token = template[(i + 1)..end];
            sb.Append(Resolve(token, info));
            i = end + 1;
        }

        return sb.ToString();
    }

    private static string Resolve(string token, TrackLayoutInfo info)
    {
        string name = token;
        string? format = null;
        int colon = token.IndexOf(':');
        if (colon >= 0)
        {
            name = token[..colon];
            format = token[(colon + 1)..];
        }

        return name switch
        {
            "AlbumArtist" => info.AlbumArtist ?? info.Artist ?? UnknownArtist,
            "Artist" => info.Artist ?? info.AlbumArtist ?? UnknownArtist,
            "Album" => info.Album ?? UnknownAlbum,
            "Title" => info.Title ?? Path.GetFileNameWithoutExtension(info.SourcePath),
            "Genre" => info.Genre ?? string.Empty,
            "Year" => info.Year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "Track" => Number(info.TrackNo, format ?? "0"),
            "Disc" => Number(info.DiscNo, format ?? "0"),

            // Disc prefix that disappears on single-disc albums, so the common
            // case stays clean without needing a second template.
            "Disc?" => info.MultiDisc && info.DiscNo is { } d
                ? d.ToString(CultureInfo.InvariantCulture) + "-"
                : string.Empty,

            "Ext" => Path.GetExtension(info.SourcePath).TrimStart('.'),
            "SourceRelative" => (info.SourceRelative ?? Path.GetFileName(info.SourcePath))
                .Replace('\\', '/'),
            _ => throw new PathTemplateException($"Unknown template token '{{{token}}}'."),
        };
    }

    private static string Number(int? value, string format) =>
        value is { } v ? v.ToString(format, CultureInfo.InvariantCulture) : string.Empty;
}
