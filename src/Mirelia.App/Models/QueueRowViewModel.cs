using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

/// <summary>
/// One row in the queue pane. Like <see cref="TrackRow"/>, rows exist as
/// placeholders for the whole queue and are filled in place when their page
/// arrives — a queue is as long as the view it was started from, which can be
/// the whole library.
/// </summary>
public sealed partial class QueueRowViewModel : ObservableObject
{
    // Matches MainWindow's grid banding brush — same 6% gray reads correctly
    // on both themes; a stronger tint marks the audible row.
    private static readonly IBrush BandingBrush =
        new SolidColorBrush(Color.FromArgb(0x0F, 0x80, 0x80, 0x80));
    private static readonly IBrush CurrentBrush =
        new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80));

    /// <summary>Stable identity of this queue slot (distinguishes duplicate track ids).</summary>
    public required long EntryId { get; init; }

    /// <summary>The library track this slot plays.</summary>
    public required long TrackId { get; init; }

    /// <summary>Position within the owning list; stable for the list's lifetime.</summary>
    public required int Index { get; init; }

    [ObservableProperty]
    private string _title = "…";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBrush))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    private bool _isCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RowBrush))]
    private bool _isAlternate;

    /// <summary>Row background: a stronger tint for the audible entry, subtle banding otherwise.</summary>
    public IBrush? RowBrush => IsCurrent ? CurrentBrush : IsAlternate ? BandingBrush : null;

    /// <summary>Title weight: emphasized for the audible entry.</summary>
    public FontWeight TitleWeight => IsCurrent ? FontWeight.SemiBold : FontWeight.Normal;

    /// <summary>Fills the row from a hydrated page entry. UI thread only.</summary>
    public void Apply(TrackPageRow? row)
    {
        Title = row?.Title ?? "(unknown)";
        Artist = row?.Artist ?? string.Empty;
    }

    /// <summary>Returns the row to its placeholder state after LRU eviction.</summary>
    public void Reset()
    {
        Title = "…";
        Artist = string.Empty;
    }
}
