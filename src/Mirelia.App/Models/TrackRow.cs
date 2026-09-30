using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

/// <summary>Whether a row is the audible track, and if so whether it is paused.</summary>
public enum NowPlayingState
{
    /// <summary>The row is not the audible track.</summary>
    None,

    /// <summary>The row is the audible track and playback is running.</summary>
    Playing,

    /// <summary>The row is the audible track and playback is paused.</summary>
    Paused,
}

/// <summary>
/// One grid row. Rows are created as placeholders showing only "…" and are
/// filled in place when their page arrives; the cells update through INPC.
/// </summary>
public sealed partial class TrackRow : ObservableObject
{
    /// <summary>Track id; exists before hydration.</summary>
    public required long Id { get; init; }

    /// <summary>Position within the owning list; stable for the list's lifetime.</summary>
    public required int Index { get; init; }

    /// <summary>
    /// True for every second row of the group block holding it, which is what
    /// draws the grouped layout's banding. Assigned when blocks are built,
    /// before the rows are shown; the flat grid ignores it.
    /// </summary>
    public bool IsAlternate { get; set; }

    /// <summary>
    /// True while the row is selected in the grouped layout. Deliberately
    /// outside <see cref="Reset"/>: LRU eviction clears a row's text, not the
    /// user's selection.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Whether this row holds the audible track. Deliberately outside
    /// <see cref="Reset"/>: LRU eviction clears a row's text, not the fact that
    /// its track is playing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNowPlaying))]
    [NotifyPropertyChangedFor(nameof(IsNowPlayingActive))]
    [NotifyPropertyChangedFor(nameof(IsNowPlayingPaused))]
    private NowPlayingState _nowPlaying;

    /// <summary>True while the row holds the audible track, playing or paused.</summary>
    public bool IsNowPlaying => NowPlaying != NowPlayingState.None;

    /// <summary>True while the row holds the audible track and it is playing.</summary>
    public bool IsNowPlayingActive => NowPlaying == NowPlayingState.Playing;

    /// <summary>True while the row holds the audible track and it is paused.</summary>
    public bool IsNowPlayingPaused => NowPlaying == NowPlayingState.Paused;

    [ObservableProperty]
    private string _trackNo = string.Empty;

    [ObservableProperty]
    private string _title = "…";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private string _albumArtist = string.Empty;

    [ObservableProperty]
    private string _genre = string.Empty;

    [ObservableProperty]
    private string _duration = string.Empty;

    [ObservableProperty]
    private string _date = string.Empty;

    [ObservableProperty]
    private string _codec = string.Empty;

    [ObservableProperty]
    private string _bitrate = string.Empty;

    [ObservableProperty]
    private string _added = string.Empty;

    /// <summary>
    /// Star rating, 0-5 (0 = unrated). Set directly by the grid's rating
    /// cell on edit, and by <see cref="Apply"/> on (re)hydration; the
    /// [ObservableProperty] notification is what lets an edit repaint the
    /// bound star glyphs without a page reload.
    /// </summary>
    [ObservableProperty]
    private int _rating;

    /// <summary>
    /// Returns the row to its placeholder state after LRU eviction. Leaves
    /// <see cref="IsSelected"/> and <see cref="NowPlaying"/> alone.
    /// </summary>
    public void Reset()
    {
        TrackNo = string.Empty;
        Title = "…";
        Artist = string.Empty;
        Album = string.Empty;
        AlbumArtist = string.Empty;
        Genre = string.Empty;
        Duration = string.Empty;
        Date = string.Empty;
        Codec = string.Empty;
        Bitrate = string.Empty;
        Added = string.Empty;
        Rating = 0;
    }

    /// <summary>Fills the row from a hydrated page entry. UI thread only.</summary>
    public void Apply(TrackPageRow row)
    {
        TrackNo = row.TrackNo?.ToString() ?? string.Empty;
        Title = row.Title ?? string.Empty;
        Artist = row.Artist ?? string.Empty;
        Album = row.Album ?? string.Empty;
        AlbumArtist = row.AlbumArtist ?? string.Empty;
        Genre = row.Genre ?? string.Empty;
        var t = TimeSpan.FromMilliseconds(row.DurationMs);
        Duration = $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        // Full release dates beat the bare year when the tag carried one.
        Date = row.DateText ?? row.Year?.ToString() ?? string.Empty;
        Codec = row.Codec;
        Bitrate = row.BitrateKbps is { } kbps ? $"{kbps}" : string.Empty;
        Added = DateTimeOffset.FromUnixTimeSeconds(row.AddedUnix)
            .ToLocalTime()
            .ToString("yyyy-MM-dd");
        // Stored rating is 0-100 in steps of 20; round to the nearest star
        // rather than truncate so hand-edited (non-multiple-of-20) values
        // in the DB still land on a sane star count instead of always low.
        Rating = row.Rating is { } raw ? Math.Clamp((raw + 10) / 20, 0, 5) : 0;
    }
}
