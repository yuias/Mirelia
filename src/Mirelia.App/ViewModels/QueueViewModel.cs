using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.App.Models;
using Mirelia.App.Services;
using Mirelia.Library.Querying;

namespace Mirelia.App.ViewModels;

/// <summary>
/// Queue pane state. The queue itself lives in <see cref="AudioPlayerService"/>
/// (single source of truth); this view-model mirrors it into a virtualized
/// row list the pane binds to.
/// </summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly AudioPlayerService _player;
    private readonly SettingsService _settings;

    /// <summary>
    /// Rows shown in the queue pane, in play order. Replaced wholesale on
    /// every queue change rather than mutated: a queue can hold the whole
    /// library, and an observable collection would raise one change
    /// notification per entry.
    /// </summary>
    [ObservableProperty]
    private VirtualQueueList? _rows;

    /// <summary>Whether the queue pane is shown; persisted across launches.</summary>
    [ObservableProperty]
    private bool _isOpen;

    /// <summary>
    /// The row for the audible entry, or null. The pane auto-scrolls to it;
    /// note a queue refresh rebuilds the row objects, so consumers must key
    /// off <see cref="QueueRowViewModel.EntryId"/> to tell an actual track
    /// change from a rebuild.
    /// </summary>
    [ObservableProperty]
    private QueueRowViewModel? _currentRow;

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
        => CurrentRow = Rows?.SetCurrent(entryId);

    private Task RefreshAsync()
    {
        // No hydration here at all: rows arrive as placeholders and fill in as
        // the pane realizes them. Queuing a 100k-track view used to run a
        // 100k-row query and build 100k rows before the pane could paint.
        var list = new VirtualQueueList(_player.QueueEntries, RequestPage);
        Rows = list;
        CurrentRow = list.SetCurrent(_player.CurrentEntryId);
        return Task.CompletedTask;
    }

    private void RequestPage(int page, long[] trackIds)
    {
        VirtualQueueList? target = Rows;
        _ = HydratePageAsync(page, trackIds, target);
    }

    private async Task HydratePageAsync(int page, long[] trackIds, VirtualQueueList? target)
    {
        // HydrateAsync already does its work off the UI thread; the
        // continuation lands back on it, which is where ApplyPage belongs.
        Dictionary<long, TrackPageRow> data = await _library.HydrateAsync(trackIds);

        // Ignore pages for a list that was replaced meanwhile.
        if (target is not null && ReferenceEquals(target, Rows))
        {
            target.ApplyPage(page, data);
        }
    }
}
