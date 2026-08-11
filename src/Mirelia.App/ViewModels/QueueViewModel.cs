using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.App.Services;
using Mirelia.Library.Querying;

namespace Mirelia.App.ViewModels;

/// <summary>One row in the queue pane: a queue entry plus hydrated display text.</summary>
public sealed partial class QueueRowViewModel : ObservableObject
{
    // Matches MainWindow's grid banding brush — same 6% gray reads correctly
    // on both themes; a stronger tint marks the audible row.
    private static readonly IBrush BandingBrush =
        new SolidColorBrush(Color.FromArgb(0x0F, 0x80, 0x80, 0x80));
    private static readonly IBrush CurrentBrush =
        new SolidColorBrush(Color.FromArgb(0x26, 0x80, 0x80, 0x80));

    /// <summary>Stable identity of this queue slot (distinguishes duplicate track ids).</summary>
    public long EntryId { get; }

    /// <summary>The library track this slot plays.</summary>
    public long TrackId { get; }

    [ObservableProperty]
    private string _title = string.Empty;

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

    /// <summary>Creates a row for the given queue entry.</summary>
    public QueueRowViewModel(long entryId, long trackId)
    {
        EntryId = entryId;
        TrackId = trackId;
    }
}

/// <summary>
/// Queue pane state. The queue itself lives in <see cref="AudioPlayerService"/>
/// (single source of truth); this view-model mirrors it into an
/// <see cref="ObservableCollection{T}"/> the pane binds to, re-hydrating
/// display text from the library on every change.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly AudioPlayerService _player;
    private readonly SettingsService _settings;

    /// <summary>Rows shown in the queue pane, in play order.</summary>
    public ObservableCollection<QueueRowViewModel> Rows { get; } = [];

    /// <summary>Whether the queue pane is shown; persisted across launches.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>Creates the view-model and subscribes to the player's queue.</summary>
    public QueueViewModel(LibraryService library, AudioPlayerService player, SettingsService settings)
    {
        _library = library;
        _player = player;
        _settings = settings;

        // Direct field assignment: the initial value from settings must not
        // immediately write itself back out via OnIsOpenChanged.
        _isOpen = settings.Current.QueuePaneOpen;

        _player.QueueChanged += () => _ = RefreshAsync();
        _player.CurrentEntryChanged += OnCurrentEntryChanged;
    }

    /// <summary>Loads the initial queue state.</summary>
    public Task InitializeAsync() => RefreshAsync();

    /// <summary>Jumps playback to a queue entry (double-click / context menu).</summary>
    public Task PlayAsync(long entryId) => _player.PlayEntryAsync(entryId);

    /// <summary>Removes an entry from the queue.</summary>
    public Task RemoveAsync(long entryId) => _player.RemoveEntryAsync(entryId);

    /// <summary>Moves an entry to a new position (drag reorder).</summary>
    public Task MoveAsync(long entryId, int newIndex) => _player.MoveEntryAsync(entryId, newIndex);

    /// <summary>Drops every queued-up entry except the one currently playing.</summary>
    public Task ClearAsync() => _player.ClearQueueAsync();

    partial void OnIsOpenChanged(bool value)
        => _settings.Save(_settings.Current with { QueuePaneOpen = value });

    private void OnCurrentEntryChanged(long? entryId)
    {
        foreach (QueueRowViewModel row in Rows)
        {
            row.IsCurrent = row.EntryId == entryId;
        }
    }

    private async Task RefreshAsync()
    {
        IReadOnlyList<QueueEntry> entries = _player.QueueEntries;
        long? currentId = _player.CurrentEntryId;

        long[] distinctTrackIds = [.. entries.Select(e => e.TrackId).Distinct()];
        Dictionary<long, TrackPageRow> hydrated = await _library.HydrateAsync(distinctTrackIds);

        Rows.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            QueueEntry entry = entries[i];
            hydrated.TryGetValue(entry.TrackId, out TrackPageRow? row);
            Rows.Add(new QueueRowViewModel(entry.EntryId, entry.TrackId)
            {
                Title = row?.Title ?? "(unknown)",
                Artist = row?.Artist ?? string.Empty,
                IsAlternate = i % 2 == 1,
                IsCurrent = entry.EntryId == currentId,
            });
        }
    }
}
