namespace Mirelia.App.Models;

/// <summary>One available grid column.</summary>
/// <param name="Key">Stable key persisted in view settings.</param>
/// <param name="Header">Column header text.</param>
/// <param name="Property">The <see cref="TrackRow"/> property it binds.</param>
/// <param name="Width">Star factor or pixel width.</param>
/// <param name="IsStar">True for proportional width.</param>
public sealed record ColumnSpec(
    string Key, string Header, string Property, double Width, bool IsStar);

/// <summary>The fixed column registry; view settings reference keys.</summary>
public static class TrackColumns
{
    /// <summary>All available columns, in canonical order.</summary>
    public static readonly IReadOnlyList<ColumnSpec> All =
    [
        new("trackno", "#", nameof(TrackRow.TrackNo), 44, false),
        new("title", "Title", nameof(TrackRow.Title), 2, true),
        new("artist", "Artist", nameof(TrackRow.Artist), 1.2, true),
        new("album", "Album", nameof(TrackRow.Album), 1.2, true),
        new("albumartist", "Album Artist", nameof(TrackRow.AlbumArtist), 1.0, true),
        new("genre", "Genre", nameof(TrackRow.Genre), 100, false),
        new("time", "Time", nameof(TrackRow.Duration), 64, false),
        new("date", "Date", nameof(TrackRow.Date), 100, false),
        new("codec", "Codec", nameof(TrackRow.Codec), 64, false),
        new("bitrate", "kbps", nameof(TrackRow.Bitrate), 60, false),
        new("added", "Added", nameof(TrackRow.Added), 90, false),
    ];

    /// <summary>Columns shown when a view has no stored layout.</summary>
    public static readonly IReadOnlyList<string> DefaultKeys =
        ["trackno", "title", "artist", "album", "time", "date", "codec"];

    /// <summary>Looks up a column by key.</summary>
    public static ColumnSpec? Find(string key) => All.FirstOrDefault(c => c.Key == key);
}
