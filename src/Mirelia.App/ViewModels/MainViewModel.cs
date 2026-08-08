using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.App.Models;
using Mirelia.App.Services;
using Mirelia.Library.Playlists;
using Mirelia.Library.Querying;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

namespace Mirelia.App.ViewModels;

/// <summary>A left-pane source entry.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Kind">What it browses.</param>
public sealed record SourceItem(string Name, SourceKind Kind)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>The browsable source kinds of M3.</summary>
public enum SourceKind
{
    /// <summary>The whole library.</summary>
    Music,

    /// <summary>Most recently added tracks.</summary>
    RecentlyAdded,
}

/// <summary>Main window state: sources, search, the virtualized track grid.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly AudioPlayerService _player;
    private readonly DispatcherTimer _searchDebounce;

    private int _loadGeneration;

    [ObservableProperty]
    private VirtualTrackList? _tracks;

    [ObservableProperty]
    private SourceItem? _selectedSource;

    [ObservableProperty]
    private PlaylistInfo? _selectedPlaylist;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private TrackSortOrder _sortOrder = TrackSortOrder.Artist;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isLibraryEmpty;

    /// <summary>The transport bar view-model.</summary>
    public TransportViewModel Transport { get; }

    /// <summary>Left-pane sources.</summary>
    public IReadOnlyList<SourceItem> Sources { get; } =
    [
        new("Music", SourceKind.Music),
        new("Recently Added", SourceKind.RecentlyAdded),
    ];

    /// <summary>Left-pane playlists.</summary>
    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

    /// <summary>Static playlists only — valid targets for adding tracks.</summary>
    public IEnumerable<PlaylistInfo> StaticPlaylists
        => Playlists.Where(p => p.Kind == PlaylistKind.Static);

    /// <summary>Sort choices for the toolbar selector.</summary>
    public IReadOnlyList<TrackSortOrder> SortOrders { get; } =
        [TrackSortOrder.Artist, TrackSortOrder.Album, TrackSortOrder.Title, TrackSortOrder.RecentlyAdded];

    /// <summary>Creates the view-model; call <see cref="InitializeAsync"/> after.</summary>
    public MainViewModel(LibraryService library, AudioPlayerService player)
    {
        _library = library;
        _player = player;
        Transport = new TransportViewModel(player);
        _searchDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = ReloadAsync();
        };

        _player.NowPlayingChanged += id => _ = ShowNowPlayingAsync(id);
        _player.PlaybackError += msg => StatusText = $"Playback error: {msg}";
        _selectedSource = Sources[0];
    }

    /// <summary>Loads the initial view.</summary>
    public async Task InitializeAsync()
    {
        await RefreshPlaylistsAsync();
        await ReloadAsync();
    }

    /// <summary>Re-reads playlists from the store.</summary>
    public async Task RefreshPlaylistsAsync()
    {
        List<PlaylistInfo> playlists = await _library.GetPlaylistsAsync();
        long? selectedId = SelectedPlaylist?.Id;
        Playlists.Clear();
        foreach (PlaylistInfo p in playlists)
        {
            Playlists.Add(p);
        }

        SelectedPlaylist = playlists.FirstOrDefault(p => p.Id == selectedId);
        OnPropertyChanged(nameof(StaticPlaylists));
    }

    /// <summary>
    /// Creates a playlist. Returns null on success, or the validation error
    /// text for the dialog to show.
    /// </summary>
    public async Task<string?> CreatePlaylistAsync(string name, string? smartQuery)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(smartQuery))
            {
                await _library.CreateStaticPlaylistAsync(name.Trim());
            }
            else
            {
                await _library.CreateSmartPlaylistAsync(name.Trim(), smartQuery);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or QueryParseException
            or QueryCompileException)
        {
            return ex.Message;
        }

        await RefreshPlaylistsAsync();
        return null;
    }

    /// <summary>Renames a playlist; returns error text or null.</summary>
    public async Task<string?> RenamePlaylistAsync(PlaylistInfo playlist, string newName)
    {
        try
        {
            await _library.RenamePlaylistAsync(playlist.Id, newName.Trim());
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        await RefreshPlaylistsAsync();
        return null;
    }

    /// <summary>Deletes a playlist.</summary>
    public async Task DeletePlaylistAsync(PlaylistInfo playlist)
    {
        await _library.DeletePlaylistAsync(playlist.Id);
        if (SelectedPlaylist?.Id == playlist.Id)
        {
            SelectedPlaylist = null;
            SelectedSource = Sources[0];
        }

        await RefreshPlaylistsAsync();
        await ReloadAsync();
    }

    /// <summary>Appends tracks to a static playlist and reports in the status line.</summary>
    public async Task AddTracksToPlaylistAsync(PlaylistInfo playlist, IReadOnlyList<long> trackIds)
    {
        if (playlist.Kind != PlaylistKind.Static || trackIds.Count == 0)
        {
            return;
        }

        await _library.AddToPlaylistAsync(playlist.Id, trackIds);
        StatusText = $"Added {trackIds.Count} track(s) to '{playlist.Name}'";
        if (SelectedPlaylist?.Id == playlist.Id)
        {
            await ReloadAsync();
        }
    }

    /// <summary>Exports a playlist to the chosen file, absolute paths.</summary>
    public async Task ExportPlaylistAsync(PlaylistInfo playlist, string outPath)
    {
        try
        {
            Library.Export.ExportResult result =
                await _library.ExportPlaylistAsync(playlist, outPath);
            StatusText =
                $"Exported {result.Written} track(s) to {Path.GetFileName(outPath)}" +
                (result.Missing > 0 ? $" ({result.Missing} missing)" : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = $"Export failed: {ex.Message}";
        }
    }

    /// <summary>Scans dropped files/folders into the library.</summary>
    public async Task AddDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        StatusText = "Scanning dropped items…";
        Library.Scanning.ScanResult result = await _library.ScanPathsAsync(paths, progress: null);
        StatusText =
            $"Scan finished: {result.Added} added, {result.Updated} updated, " +
            $"{result.Unchanged} unchanged, {result.Skipped} skipped";
        await ReloadAsync();
    }

    /// <summary>Plays the grid row at <paramref name="rowIndex"/> with the current view as queue.</summary>
    public async Task PlayRowAsync(int rowIndex)
    {
        if (Tracks is null || rowIndex < 0 || rowIndex >= Tracks.Count)
        {
            return;
        }

        await _player.PlayQueueAsync(Tracks.Ids, rowIndex);
    }

    /// <summary>Scans a folder into the library, then reloads the view.</summary>
    public async Task AddFolderAsync(string directory)
    {
        StatusText = $"Scanning {directory}…";
        var progress = new Progress<int>(n =>
        {
            if (n % 500 == 0)
            {
                StatusText = $"Scanning {directory}… {n} files";
            }
        });
        Library.Scanning.ScanResult result = await _library.ScanAsync(directory, progress);
        StatusText =
            $"Scan finished: {result.Added} added, {result.Updated} updated, " +
            $"{result.Unchanged} unchanged, {result.Skipped} skipped";
        await ReloadAsync();
    }

    partial void OnSelectedSourceChanged(SourceItem? value)
    {
        if (value is not null)
        {
            SelectedPlaylist = null;
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedPlaylistChanged(PlaylistInfo? value)
    {
        if (value is not null)
        {
            SelectedSource = null;
            _ = ReloadAsync();
        }
    }

    partial void OnSortOrderChanged(TrackSortOrder value) => _ = ReloadAsync();

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async Task ReloadAsync()
    {
        int generation = ++_loadGeneration;

        long[] ids;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            ids = await _library.SearchAsync(SearchText.Trim());
        }
        else if (SelectedPlaylist is { } playlist)
        {
            ids = await _library.EvaluatePlaylistAsync(playlist);
        }
        else
        {
            ids = SelectedSource?.Kind == SourceKind.RecentlyAdded
                ? await _library.BrowseRecentlyAddedAsync()
                : await _library.BrowseAllAsync(SortOrder);
        }

        if (generation != _loadGeneration)
        {
            return; // a newer reload superseded this one
        }

        Tracks = new VirtualTrackList(ids, RequestPage);
        IsLibraryEmpty = ids.Length == 0 && string.IsNullOrWhiteSpace(SearchText);
        StatusText = $"{ids.Length:N0} track(s)";
    }

    private void RequestPage(int page, long[] pageIds)
    {
        VirtualTrackList? target = Tracks;
        _ = Task.Run(async () =>
        {
            Dictionary<long, TrackPageRow> data = await _library.HydrateAsync(pageIds);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Ignore pages for lists that were replaced meanwhile.
                if (ReferenceEquals(target, Tracks))
                {
                    target!.ApplyPage(page, data);
                }
            });
        });
    }

    private async Task ShowNowPlayingAsync(long id)
    {
        Dictionary<long, TrackPageRow> rows = await _library.HydrateAsync([id]);
        if (rows.TryGetValue(id, out TrackPageRow? row))
        {
            Transport.SetNowPlaying(row.Title, row.Artist);
        }
    }

}
