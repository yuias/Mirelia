using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

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

    [ObservableProperty]
    private string _trackNo = string.Empty;

    [ObservableProperty]
    private string _title = "…";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _album = string.Empty;

    [ObservableProperty]
    private string _duration = string.Empty;

    [ObservableProperty]
    private string _year = string.Empty;

    [ObservableProperty]
    private string _codec = string.Empty;

    /// <summary>Returns the row to its placeholder state after LRU eviction.</summary>
    public void Reset()
    {
        TrackNo = string.Empty;
        Title = "…";
        Artist = string.Empty;
        Album = string.Empty;
        Duration = string.Empty;
        Year = string.Empty;
        Codec = string.Empty;
    }

    /// <summary>Fills the row from a hydrated page entry. UI thread only.</summary>
    public void Apply(TrackPageRow row)
    {
        TrackNo = row.TrackNo?.ToString() ?? string.Empty;
        Title = row.Title ?? string.Empty;
        Artist = row.Artist ?? string.Empty;
        Album = row.Album ?? string.Empty;
        var t = TimeSpan.FromMilliseconds(row.DurationMs);
        Duration = $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        Year = row.Year?.ToString() ?? string.Empty;
        Codec = row.Codec;
    }
}
