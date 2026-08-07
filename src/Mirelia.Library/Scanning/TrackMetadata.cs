namespace Mirelia.Library.Scanning;

/// <summary>
/// Everything the scanner extracts from one audio file. Null string members
/// mean "tag absent"; explicit sort members are the file's own sort tags
/// (TSOT/sonm/TITLESORT etc.) and win over derived keys.
/// </summary>
public sealed record TrackMetadata
{
    /// <summary>Track title.</summary>
    public string? Title { get; init; }

    /// <summary>Performing artist(s), joined.</summary>
    public string? Artist { get; init; }

    /// <summary>Album artist(s), joined; falls back to <see cref="Artist"/> at intern time.</summary>
    public string? AlbumArtist { get; init; }

    /// <summary>Composer(s), joined.</summary>
    public string? Composer { get; init; }

    /// <summary>Album title.</summary>
    public string? Album { get; init; }

    /// <summary>Primary genre.</summary>
    public string? Genre { get; init; }

    /// <summary>Explicit title sort tag, if present.</summary>
    public string? TitleSort { get; init; }

    /// <summary>Explicit artist sort tag, if present.</summary>
    public string? ArtistSort { get; init; }

    /// <summary>Explicit album-artist sort tag, if present.</summary>
    public string? AlbumArtistSort { get; init; }

    /// <summary>Explicit composer sort tag, if present.</summary>
    public string? ComposerSort { get; init; }

    /// <summary>Explicit album sort tag, if present.</summary>
    public string? AlbumSort { get; init; }

    /// <summary>Track number; null if untagged.</summary>
    public int? TrackNo { get; init; }

    /// <summary>Disc number; null if untagged.</summary>
    public int? DiscNo { get; init; }

    /// <summary>Release year; null if untagged.</summary>
    public int? Year { get; init; }

    /// <summary>Duration in milliseconds.</summary>
    public required long DurationMs { get; init; }

    /// <summary>Sample rate in Hz.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Channel count.</summary>
    public required int Channels { get; init; }

    /// <summary>Bits per sample; null for lossy codecs.</summary>
    public int? BitDepth { get; init; }

    /// <summary>Average bitrate in kbps; null when unknown.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>Canonical codec id: mp3, aac, alac, flac, pcm.</summary>
    public required string Codec { get; init; }

    /// <summary>True for flac/alac/pcm.</summary>
    public required bool IsLossless { get; init; }
}
