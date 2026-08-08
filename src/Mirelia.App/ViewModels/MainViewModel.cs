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

/// <summary>A node of the playlist tree (folders contain children).</summary>
public sealed class PlaylistNode
{
    /// <summary>The underlying playlist row.</summary>
    public required PlaylistInfo Info { get; init; }

    /// <summary>Child nodes; empty for non-folders.</summary>
    public ObservableCollection<PlaylistNode> Children { get; } = [];

    /// <summary>True for folder nodes.</summary>
    public bool IsFolder => Info.Kind == PlaylistKind.Folder;

    /// <summary>Inverse of <see cref="IsFolder"/>, for icon visibility bindings.</summary>
    public bool IsPlaylist => !IsFolder;

    /// <summary>Display name.</summary>
    public string Name => Info.Name;
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
    private PlaylistNode? _selectedNode;

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

    /// <summary>All playlists, flat (menus and lookups).</summary>
    public ObservableCollection<PlaylistInfo> Playlists { get; } = [];

    /// <summary>The playlist tree shown in the left pane.</summary>
    public ObservableCollection<PlaylistNode> PlaylistTree { get; } = [];

    /// <summary>Static playlists only — valid targets for adding tracks.</summary>
    public IEnumerable<PlaylistInfo> StaticPlaylists
        => Playlists.Where(p => p.Kind == PlaylistKind.Static);

    /// <summary>Folders — valid "move to" targets.</summary>
    public IEnumerable<PlaylistInfo> Folders
        => Playlists.Where(p => p.Kind == PlaylistKind.Folder);

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

    /// <summary>Re-reads playlists from the store and rebuilds the tree.</summary>
    public async Task RefreshPlaylistsAsync()
    {
        List<PlaylistInfo> playlists = await _library.GetPlaylistsAsync();
        long? selectedId = SelectedPlaylist?.Id;
        Playlists.Clear();
        foreach (PlaylistInfo p in playlists)
        {
            Playlists.Add(p);
        }

        var nodes = playlists.ToDictionary(p => p.Id, p => new PlaylistNode { Info = p });
        PlaylistTree.Clear();
        foreach (PlaylistInfo p in playlists)
        {
            PlaylistNode node = nodes[p.Id];
            if (p.ParentId is { } parentId && nodes.TryGetValue(parentId, out PlaylistNode? parent))
            {
                parent.Children.Add(node);
            }
            else
            {
                PlaylistTree.Add(node);
            }
        }

        SelectedPlaylist = playlists.FirstOrDefault(p => p.Id == selectedId);
        OnPropertyChanged(nameof(StaticPlaylists));
        OnPropertyChanged(nameof(Folders));
    }

    /// <summary>Creates a playlist folder; returns error text or null.</summary>
    public async Task<string?> CreateFolderAsync(string name)
    {
        try
        {
            await _library.CreateFolderAsync(name.Trim());
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        await RefreshPlaylistsAsync();
        return null;
    }

    /// <summary>Moves a playlist/folder into a folder (null = top level).</summary>
    public async Task MovePlaylistAsync(PlaylistInfo playlist, long? parentId)
    {
        try
        {
            await _library.SetPlaylistParentAsync(playlist.Id, parentId);
        }
        catch (InvalidOperationException ex)
        {
            StatusText = ex.Message;
            return;
        }

        await RefreshPlaylistsAsync();
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

    /// <summary>
    /// Scans ReplayGain for every track that has no values yet, on a worker
    /// thread with its own engine. Reports progress in the status line.
    /// </summary>
    public async Task ScanReplayGainAsync()
    {
        List<(long Id, string Path)> pending = await _library.GetTracksMissingReplayGainAsync();
        if (pending.Count == 0)
        {
            StatusText = "ReplayGain: nothing to scan";
            return;
        }

        StatusText = $"ReplayGain: scanning {pending.Count} track(s)…";
        var progress = new Progress<int>(n => StatusText = $"ReplayGain: {n}/{pending.Count}…");
        List<(long Id, float Gain, float Peak)> results = await Task.Run(() =>
        {
            var scanned = new List<(long, float, float)>(pending.Count);
            try
            {
                using var engine = AudioBridge.AudioEngine.Create();
                int processed = 0;
                foreach ((long id, string path) in pending)
                {
                    try
                    {
                        (float gain, float peak) = engine.ScanLoudness(path);
                        scanned.Add((id, gain, peak));
                    }
                    catch (AudioBridge.AudioEngineException)
                    {
                        // Missing or unreadable file; skip it.
                    }

                    if (++processed % 20 == 0)
                    {
                        ((IProgress<int>)progress).Report(processed);
                    }
                }
            }
            catch (DllNotFoundException)
            {
                // No backend; scanned stays empty.
            }

            return scanned;
        });

        foreach ((long id, float gain, float peak) in results)
        {
            await _library.SetTrackReplayGainAsync(id, gain, peak);
        }

        int failed = pending.Count - results.Count;
        StatusText = $"ReplayGain: {results.Count} scanned" +
            (failed > 0 ? $", {failed} failed" : string.Empty);
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
            SelectedNode = null;
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedNodeChanged(PlaylistNode? value)
    {
        // Folder selection keeps the current view; playlists load.
        if (value is { IsPlaylist: true })
        {
            SelectedPlaylist = value.Info;
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
        if (!rows.TryGetValue(id, out TrackPageRow? row))
        {
            return;
        }

        Avalonia.Media.Imaging.Bitmap? artwork = null;
        byte[]? bytes = await _library.GetArtworkAsync(id);
        if (bytes is not null)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                // Panel width is ~180 logical px; decode small to keep memory flat.
                artwork = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 400);
            }
            catch (Exception)
            {
                // Broken embedded images are not worth surfacing.
            }
        }

        Transport.SetNowPlaying(row.Title, row.Artist, row.Album, artwork);
    }

}
