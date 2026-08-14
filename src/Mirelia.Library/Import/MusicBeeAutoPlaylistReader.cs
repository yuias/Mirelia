using System.Globalization;
using System.Xml.Linq;

namespace Mirelia.Library.Import;

/// <summary>One rule of a MusicBee auto-playlist.</summary>
/// <param name="Field">MusicBee field name, e.g. <c>FileDateAdded</c>.</param>
/// <param name="Comparison">MusicBee comparison name, e.g. <c>InTheLast</c>.</param>
/// <param name="Values">
/// Operands. Most comparisons take one; <c>InRange</c> takes two, and the
/// <c>In</c> family takes as many as the rule lists.
/// </param>
public sealed record MusicBeeCondition(
    string Field, string Comparison, IReadOnlyList<string> Values);

/// <summary>A MusicBee auto-playlist, as one <c>.xautopf</c> file describes it.</summary>
/// <param name="Reference">
/// Path below the playlist folder without the extension, <c>/</c>-separated —
/// <c>Tmp/Filters/Picks.ins</c>. This is both how the file is located and
/// how other playlists refer to it.
/// </param>
/// <param name="SourcePlaylists">
/// Playlists the rules draw from, as references. Empty means the whole library.
/// </param>
/// <param name="MatchAll">True for <c>CombineMethod="All"</c>, false for <c>Any</c>.</param>
/// <param name="Conditions">The rules themselves.</param>
/// <param name="Limit">Row cap, or null when the limit is switched off.</param>
public sealed record MusicBeeAutoPlaylist(
    string Reference,
    IReadOnlyList<string> SourcePlaylists,
    bool MatchAll,
    IReadOnlyList<MusicBeeCondition> Conditions,
    int? Limit);

/// <summary>
/// Reads the <c>.xautopf</c> files under a MusicBee playlist folder. Unlike
/// the library itself, auto-playlist rules are plain XML.
/// </summary>
public static class MusicBeeAutoPlaylistReader
{
    /// <summary>Extension MusicBee gives an auto-playlist.</summary>
    public const string Extension = ".xautopf";

    /// <summary>Extensions a playlist reference may resolve to on disk.</summary>
    private static readonly string[] PlaylistExtensions = [".mbp", ".xautopf", ".m3u", ".m3u8"];

    /// <summary>
    /// Reads every auto-playlist below <paramref name="playlistFolder"/>,
    /// in path order so the result is reproducible.
    /// </summary>
    public static List<MusicBeeAutoPlaylist> ReadFolder(string playlistFolder)
    {
        if (!Directory.Exists(playlistFolder))
        {
            return [];
        }

        var found = new List<MusicBeeAutoPlaylist>();
        IEnumerable<string> files = Directory
            .EnumerateFiles(playlistFolder, "*" + Extension, SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            found.Add(Read(file, playlistFolder));
        }

        return found;
    }

    /// <summary>Reads one auto-playlist file.</summary>
    /// <param name="file">The <c>.xautopf</c> file.</param>
    /// <param name="playlistFolder">Root the reference is expressed against.</param>
    public static MusicBeeAutoPlaylist Read(string file, string playlistFolder)
    {
        XElement root = XDocument.Load(file).Root
            ?? throw new InvalidDataException($"'{file}' is empty.");

        // Several <Source> elements would be several playlists in one file,
        // which MusicBee does not write; the first is the playlist.
        XElement? source = root.Element("Source");
        XElement? conditions = source?.Element("Conditions");

        var sources = source?
            .Elements("SourcePath")
            .Select(e => (string?)e.Attribute("Location"))
            .OfType<string>()
            .Select(location => Reference(location, playlistFolder))
            .ToList() ?? [];

        var rules = conditions?
            .Elements("Condition")
            .Select(ToCondition)
            .ToList() ?? [];

        return new MusicBeeAutoPlaylist(
            Reference(file, playlistFolder),
            sources,
            (string?)conditions?.Attribute("CombineMethod") != "Any",
            rules,
            ReadLimit(source?.Element("Limit")));
    }

    private static MusicBeeCondition ToCondition(XElement element)
    {
        // A single operand is "Value"; several are "Value1", "Value2", …
        var values = new List<string>();
        if ((string?)element.Attribute("Value") is { } single)
        {
            values.Add(single);
        }

        for (int i = 1; (string?)element.Attribute($"Value{i}") is { } value; i++)
        {
            values.Add(value);
        }

        return new MusicBeeCondition(
            (string?)element.Attribute("Field") ?? string.Empty,
            (string?)element.Attribute("Comparison") ?? string.Empty,
            values);
    }

    private static int? ReadLimit(XElement? limit)
    {
        if ((string?)limit?.Attribute("Enabled") != "True"
            || !int.TryParse(
                (string?)limit.Attribute("Count"), CultureInfo.InvariantCulture, out int count)
            || count <= 0)
        {
            return null;
        }

        return count;
    }

    /// <summary>
    /// Turns a path into the reference form other playlists use: relative to
    /// the playlist folder, extension dropped, <c>/</c>-separated.
    /// </summary>
    /// <remarks>
    /// A trailing separator survives, because MusicBee writes one to mean
    /// "every playlist in this folder".
    /// </remarks>
    public static string Reference(string path, string playlistFolder)
    {
        string relative = Path.IsPathRooted(path)
            ? Path.GetRelativePath(playlistFolder, path)
            : path;
        relative = relative.Replace('\\', '/');

        if (relative.EndsWith('/'))
        {
            return relative;
        }

        foreach (string extension in PlaylistExtensions)
        {
            if (relative.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return relative[..^extension.Length];
            }
        }

        return relative;
    }
}
