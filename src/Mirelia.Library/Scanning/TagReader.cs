namespace Mirelia.Library.Scanning;

/// <summary>Reads tags and audio properties via TagLib#.</summary>
public static class TagReader
{
    /// <summary>
    /// Reads metadata from one file.
    /// </summary>
    /// <exception cref="NotSupportedException">The codec is outside the v1 set.</exception>
    /// <exception cref="TagLib.CorruptFileException">The file is unreadable as audio.</exception>
    public static TrackMetadata Read(string path)
    {
        using TagLib.File file = TagLib.File.Create(path);
        TagLib.Tag tag = file.Tag;
        TagLib.Properties props = file.Properties;

        (string codec, bool lossless) = DetectCodec(file, Path.GetExtension(path).ToLowerInvariant());

        return new TrackMetadata
        {
            Title = Clean(tag.Title),
            Artist = Join(tag.Performers),
            AlbumArtist = Join(tag.AlbumArtists),
            Composer = Join(tag.Composers),
            Album = Clean(tag.Album),
            Genre = Clean(tag.FirstGenre),
            TitleSort = Clean(tag.TitleSort),
            ArtistSort = Join(tag.PerformersSort),
            AlbumArtistSort = Join(tag.AlbumArtistsSort),
            ComposerSort = Join(tag.ComposersSort),
            AlbumSort = Clean(tag.AlbumSort),
            TrackNo = tag.Track > 0 ? (int)tag.Track : null,
            DiscNo = tag.Disc > 0 ? (int)tag.Disc : null,
            Year = tag.Year > 0 ? (int)tag.Year : null,
            DurationMs = (long)props.Duration.TotalMilliseconds,
            SampleRate = props.AudioSampleRate,
            Channels = props.AudioChannels,
            BitDepth = lossless && props.BitsPerSample > 0 ? props.BitsPerSample : null,
            BitrateKbps = props.AudioBitrate > 0 ? props.AudioBitrate : null,
            Codec = codec,
            IsLossless = lossless,
        };
    }

    private static (string Codec, bool Lossless) DetectCodec(TagLib.File file, string extension)
    {
        switch (extension)
        {
            case ".mp3":
                return ("mp3", false);
            case ".flac":
                return ("flac", true);
            case ".wav":
                return ("pcm", true);
            case ".m4a" or ".mp4":
                // ALAC and AAC share the MP4 container; the codec description
                // is the only reliable discriminator TagLib# exposes.
                return HasCodecDescription(file, "alac") ? ("alac", true) : ("aac", false);
            case ".ogg" or ".oga":
                return HasCodecDescription(file, "flac")
                    ? ("flac", true)
                    : throw new NotSupportedException(
                        "Only FLAC is supported in Ogg containers in v1.");
            default:
                throw new NotSupportedException($"Unsupported extension '{extension}'.");
        }
    }

    private static bool HasCodecDescription(TagLib.File file, string needle)
    {
        return file.Properties.Codecs.Any(c =>
            c?.Description?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static string? Clean(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Join(string[]? values)
    {
        if (values is null)
        {
            return null;
        }

        string joined = string.Join("; ", values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim()));
        return joined.Length > 0 ? joined : null;
    }
}
