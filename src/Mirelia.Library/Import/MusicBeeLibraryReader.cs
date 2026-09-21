using System.Globalization;
using System.Xml;

namespace Mirelia.Library.Import;

/// <summary>One track as MusicBee's export describes it.</summary>
/// <param name="Id">Track id, the key playlists reference.</param>
/// <param name="Path">Absolute path, decoded from the export's file URL.</param>
/// <param name="Title">Title, for reporting a track that could not be matched.</param>
/// <param name="Rating">0–100, or null when unrated. The scale Mirelia stores.</param>
/// <param name="PlayCount">Times played; zero when the export omits it.</param>
/// <param name="LastPlayedUnix">Last play, or null when never played.</param>
/// <param name="AddedUnix">When MusicBee first saw the file.</param>
public sealed record MusicBeeTrack(
    int Id,
    string Path,
    string? Title,
    int? Rating,
    int PlayCount,
    long? LastPlayedUnix,
    long? AddedUnix);

/// <summary>One playlist or playlist folder from the export.</summary>
/// <param name="PersistentId">
/// Stable id; what children name as their parent. Falls back to the name when
/// the export carries no id, so nesting still resolves — see
/// <paramref name="ExportId"/> before storing this anywhere.
/// </param>
/// <param name="ParentPersistentId">Containing folder, or null at the top level.</param>
/// <param name="Name">Name within its folder; not unique across the library.</param>
/// <param name="IsFolder">Whether it holds playlists rather than tracks.</param>
/// <param name="TrackIds">Members in playlist order; empty for a folder.</param>
/// <param name="ExportId">
/// The id the export actually carried, or null when it carried none. Only this
/// is worth persisting: <paramref name="PersistentId"/>'s fallback is a name,
/// which would be recorded as an id and then go stale on the next rename.
/// </param>
public sealed record MusicBeePlaylist(
    string PersistentId,
    string? ParentPersistentId,
    string Name,
    bool IsFolder,
    IReadOnlyList<int> TrackIds,
    string? ExportId = null);

/// <summary>Everything one export file holds.</summary>
/// <param name="Tracks">Every file-backed track, in export order.</param>
/// <param name="Playlists">Playlists and folders, in export order.</param>
public sealed record MusicBeeLibrary(
    IReadOnlyList<MusicBeeTrack> Tracks,
    IReadOnlyList<MusicBeePlaylist> Playlists);

/// <summary>
/// Reads <c>iTunes Music Library.xml</c>, the iTunes-compatible export
/// MusicBee keeps beside its own library.
/// </summary>
/// <remarks>
/// Streamed rather than loaded: the file runs to tens of megabytes on a real
/// library, and nothing here needs random access. Only the keys the import
/// uses are decoded; the rest of each record is skipped in place.
/// </remarks>
public static class MusicBeeLibraryReader
{
    /// <summary>File name MusicBee gives the export, inside the library folder.</summary>
    public const string FileName = "iTunes Music Library.xml";

    /// <summary>Reads an export file.</summary>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="XmlException">The file is not the expected export.</exception>
    public static MusicBeeLibrary Read(string path)
    {
        var settings = new XmlReaderSettings
        {
            // The export declares Apple's DTD by URL. Resolving it would mean
            // a network fetch to parse a local file.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreWhitespace = true,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        using XmlReader reader = XmlReader.Create(path, settings);
        if (!reader.ReadToFollowing("plist") || !reader.ReadToDescendant("dict"))
        {
            throw new XmlException($"'{path}' is not an iTunes library export.");
        }

        return ReadRoot(reader);
    }

    private static MusicBeeLibrary ReadRoot(XmlReader reader)
    {
        var tracks = new List<MusicBeeTrack>();
        var playlists = new List<MusicBeePlaylist>();

        foreach (string key in DictKeys(reader))
        {
            switch (key)
            {
                case "Tracks":
                    ReadTracks(reader, tracks);
                    break;
                case "Playlists":
                    ReadPlaylists(reader, playlists);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        return new MusicBeeLibrary(tracks, playlists);
    }

    /// <summary>
    /// Walks a <c>&lt;dict&gt;</c>, yielding each key with the reader left on
    /// its value. The caller must consume or skip that value before the next
    /// step, which is what makes streaming possible at all.
    /// </summary>
    private static IEnumerable<string> DictKeys(XmlReader reader)
    {
        if (reader.IsEmptyElement)
        {
            reader.Skip();
            yield break;
        }

        reader.ReadStartElement();
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            yield return reader.ReadElementContentAsString("key", string.Empty);
        }

        reader.ReadEndElement();
    }

    private static void ReadTracks(XmlReader reader, List<MusicBeeTrack> into)
    {
        // Keyed by track id, which the record repeats, so the key is dropped.
        foreach (string _ in DictKeys(reader))
        {
            if (ToTrack(ReadScalars(reader)) is { } track)
            {
                into.Add(track);
            }
        }
    }

    private static void ReadPlaylists(XmlReader reader, List<MusicBeePlaylist> into)
    {
        if (reader.IsEmptyElement)
        {
            reader.Skip();
            return;
        }

        reader.ReadStartElement("array");
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            if (ReadPlaylist(reader) is { } playlist)
            {
                into.Add(playlist);
            }
        }

        reader.ReadEndElement();
    }

    private static MusicBeePlaylist? ReadPlaylist(XmlReader reader)
    {
        string? persistentId = null;
        string? parentId = null;
        string? name = null;
        bool folder = false;
        var items = new List<int>();

        foreach (string key in DictKeys(reader))
        {
            switch (key)
            {
                case "Playlist Persistent ID":
                    persistentId = reader.ReadElementContentAsString();
                    break;
                case "Parent Persistent ID":
                    parentId = reader.ReadElementContentAsString();
                    break;
                case "Name":
                    name = reader.ReadElementContentAsString();
                    break;
                case "Folder":
                    folder = reader.Name == "true";
                    reader.Skip();
                    break;
                case "Playlist Items":
                    ReadPlaylistItems(reader, items);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        // A playlist with no name cannot be created and cannot be referenced.
        return name is null
            ? null
            : new MusicBeePlaylist(
                persistentId ?? name, parentId, name, folder, items, persistentId);
    }

    private static void ReadPlaylistItems(XmlReader reader, List<int> into)
    {
        if (reader.IsEmptyElement)
        {
            reader.Skip();
            return;
        }

        reader.ReadStartElement("array");
        while (reader.NodeType != XmlNodeType.EndElement)
        {
            Dictionary<string, string> item = ReadScalars(reader);
            if (item.TryGetValue("Track ID", out string? id)
                && int.TryParse(id, CultureInfo.InvariantCulture, out int trackId))
            {
                into.Add(trackId);
            }
        }

        reader.ReadEndElement();
    }

    /// <summary>Reads a dict of scalar values, skipping any nested structure.</summary>
    private static Dictionary<string, string> ReadScalars(XmlReader reader)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in DictKeys(reader))
        {
            if (reader.IsEmptyElement)
            {
                // <true/> and <false/> carry their value in the element name.
                fields[key] = reader.Name;
                reader.Skip();
            }
            else if (reader.Name is "dict" or "array")
            {
                reader.Skip();
            }
            else
            {
                fields[key] = reader.ReadElementContentAsString();
            }
        }

        return fields;
    }

    private static MusicBeeTrack? ToTrack(Dictionary<string, string> fields)
    {
        // Streams and other non-file entries have no path to match on.
        if (!fields.TryGetValue("Track Type", out string? kind) || kind != "File")
        {
            return null;
        }

        if (!fields.TryGetValue("Track ID", out string? rawId)
            || !int.TryParse(rawId, CultureInfo.InvariantCulture, out int id)
            || !fields.TryGetValue("Location", out string? location)
            || ToPath(location) is not { } path)
        {
            return null;
        }

        return new MusicBeeTrack(
            id,
            path,
            fields.GetValueOrDefault("Name"),
            Number(fields, "Rating"),
            Number(fields, "Play Count") ?? 0,
            Timestamp(fields, "Play Date UTC"),
            Timestamp(fields, "Date Added"));
    }

    /// <summary>Turns an export file URL into an absolute path.</summary>
    private static string? ToPath(string location)
    {
        string[] prefixes = ["file://localhost/", "file:///"];
        foreach (string prefix in prefixes)
        {
            if (!location.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string decoded = Uri.UnescapeDataString(location[prefix.Length..]);
            return decoded.Replace('/', Path.DirectorySeparatorChar);
        }

        return null;
    }

    private static int? Number(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out string? raw)
            && int.TryParse(raw, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private static long? Timestamp(Dictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out string? raw)
            && DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset value)
            ? value.ToUnixTimeSeconds()
            : null;
}
