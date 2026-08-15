using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Data.Sqlite;
using Mirelia.App.Models;
using Mirelia.App.Services;
using Mirelia.Library.Database;
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

    /// <summary>
    /// True for in-memory search playlists that are never written to the
    /// store and vanish at app exit. The negative id sentinel (see
    /// <see cref="MainViewModel.PinSearchAsTemporaryPlaylist"/>) can never
    /// collide with a real (positive, autoincrement) playlist id.
    /// </summary>
    public bool IsEphemeral => Info.Id < 0;

    /// <summary>Italicizes ephemeral entries so they read as temporary at a glance.</summary>
    public FontStyle NameStyle => IsEphemeral ? FontStyle.Italic : FontStyle.Normal;

    /// <summary>Explains the ephemeral lifetime; null (no tooltip) for ordinary entries.</summary>
    public string? Tooltip => IsEphemeral ? "Temporary playlist — cleared when Mirelia exits" : null;
}

/// <summary>Main window state: sources, search, the virtualized track grid.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private static readonly SortChoice[] BrowseSorts =
    [
        new(TrackSortOrder.Artist, "Artist"),
        new(TrackSortOrder.Album, "Album"),
        new(TrackSortOrder.Title, "Title"),
        new(TrackSortOrder.Year, "Year"),
        new(TrackSortOrder.AlbumByYear, "Album (by Year)"),
        new(TrackSortOrder.AlbumByRecentlyAdded, "Album (by Added)"),
        new(TrackSortOrder.RecentlyAdded, "Recently Added"),
    ];

    private static readonly SortChoice[] PlaylistSorts =
    [
        new(TrackSortOrder.PlaylistOrder, "Playlist Order"),
        .. BrowseSorts,
    ];

    private readonly LibraryService _library;
    private readonly AudioPlayerService _player;
    private readonly ArtworkCache _artwork;
    private readonly DispatcherTimer _searchDebounce;

    // Grouped-layout selection only; the flat grid keeps the DataGrid's own.
    private readonly BlockSelection _blockSelection = new();

    // Ephemeral ("pin as temporary playlist") state: pure in-memory, never
    // written through _library. Ids count down from -1 so they can never
    // collide with a real (positive) playlist id.
    private readonly List<PlaylistInfo> _ephemeralPlaylists = [];
    private long _nextEphemeralId = -1;

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

    /// <summary>True when the search box takes raw DSL instead of free text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchPlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanPromoteSearch))]
    private bool _isDslSearch;

    /// <summary>Search-box placeholder for the active mode.</summary>
    public string SearchPlaceholder
        => IsDslSearch ? "Query (DSL)…" : "Search (full-text)…";

    /// <summary>True when the current DSL query can be saved as a smart playlist.</summary>
    public bool CanPromoteSearch
        => IsDslSearch && !string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Layout styles offered for the current view.</summary>
    [ObservableProperty]
    private IReadOnlyList<LayoutSpec> _availableLayouts = TrackLayouts.All;

    /// <summary>The active layout style.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFlatLayout))]
    private LayoutSpec _selectedLayout = TrackLayouts.Tracks;

    /// <summary>
    /// The grouped layout's blocks over <see cref="Tracks"/>, or null while a
    /// flat layout is active.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<TrackGroupBlock>? _groupedTracks;

    /// <summary>True while the flat track grid is the active layout.</summary>
    public bool IsFlatLayout => !SelectedLayout.IsGrouped;

    [ObservableProperty]
    private IReadOnlyList<SortChoice> _availableSorts = BrowseSorts;

    [ObservableProperty]
    private SortChoice? _selectedSort = BrowseSorts[0];

    /// <summary>Column key of the active header-click sort, or null.</summary>
    [ObservableProperty]
    private string? _sortColumnKey;

    /// <summary>Direction of the header-click sort.</summary>
    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private IReadOnlyList<string> _currentColumns = TrackColumns.DefaultKeys;

    /// <summary>User-resized column widths for the current view, keyed by column key.</summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, double> _currentColumnWidths = EmptyColumnWidths;

    private static readonly IReadOnlyDictionary<string, double> EmptyColumnWidths =
        new Dictionary<string, double>();

    /// <summary>True while view settings are being applied programmatically.</summary>
    private bool _applyingViewSettings;

    // Whether the current view's columns/widths were inherited from the
    // Music view rather than saved under this scope. Inherited fields
    // persist as null so the view keeps following Music until the user
    // customizes it here.
    private bool _columnsInherited;
    private bool _widthsInherited;

    /// <summary>
    /// Raised after <see cref="ReloadAsync"/> has applied a view's saved
    /// settings to the bound properties — even when the values are unchanged
    /// and no PropertyChanged fires — so the window can re-sync grid column
    /// state to the (possibly new) view scope.
    /// </summary>
    public event Action? ViewSettingsApplied;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isLibraryEmpty;

    /// <summary>The transport bar view-model.</summary>
    public TransportViewModel Transport { get; }

    /// <summary>The playing-queue pane view-model.</summary>
    public QueueViewModel Queue { get; }

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

    /// <summary>
    /// True when the grid's row order equals playlist entry position, so a
    /// drag-drop within the grid can reorder the playlist: a static playlist
    /// is selected, no header-click column sort overrides it, the "Playlist
    /// Order" preset is active, and no search filter is narrowing the view.
    /// </summary>
    public bool CanReorderStaticPlaylist
        => SelectedPlaylist is { Kind: PlaylistKind.Static }
            && SortColumnKey is null
            && SelectedSort?.Order == TrackSortOrder.PlaylistOrder
            && string.IsNullOrWhiteSpace(SearchText);


    /// <summary>The library facade, for dialogs that talk to it directly.</summary>
    public LibraryService Library => _library;

    /// <summary>Creates the view-model; call <see cref="InitializeAsync"/> after.</summary>
    public MainViewModel(LibraryService library, AudioPlayerService player, SettingsService settings)
    {
        _library = library;
        _player = player;
        _artwork = new ArtworkCache(library);
        _artwork.UseCurrentLibrary();
        Transport = new TransportViewModel(player, settings);
        Queue = new QueueViewModel(library, player, settings);
        _searchDebounce = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = ReloadAsync();
        };

        _blockSelection.Changed += (index, selected) =>
        {
            if (Tracks is { } tracks && index < tracks.Count)
            {
                tracks[index].IsSelected = selected;
            }
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
        await Queue.InitializeAsync();
    }

    /// <summary>Re-reads playlists from the store and rebuilds the tree.</summary>
    public async Task RefreshPlaylistsAsync()
    {
        List<PlaylistInfo> playlists = await _library.GetPlaylistsAsync();
        long? selectedId = SelectedPlaylist?.Id;
        long? selectedNodeId = SelectedNode?.Info.Id;
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

        // Ephemeral playlists never come back from GetPlaylistsAsync (they
        // are not store rows), so they are re-attached at the tree root
        // after every rebuild instead of being silently dropped.
        var ephemeralNodes = new Dictionary<long, PlaylistNode>();
        foreach (PlaylistInfo ephemeral in _ephemeralPlaylists)
        {
            var node = new PlaylistNode { Info = ephemeral };
            ephemeralNodes[ephemeral.Id] = node;
            PlaylistTree.Add(node);
        }

        SelectedPlaylist = playlists.FirstOrDefault(p => p.Id == selectedId)
            ?? (selectedId is { } sid && ephemeralNodes.TryGetValue(sid, out PlaylistNode? sp) ? sp.Info : null);

        // The rebuild replaced every node instance, so the tree's bound
        // selection must be re-pointed at the new node or the visual
        // highlight silently disappears after any playlist-store mutation.
        SelectedNode = selectedNodeId is { } nodeId
            ? nodes.TryGetValue(nodeId, out PlaylistNode? selected)
                ? selected
                : ephemeralNodes.GetValueOrDefault(nodeId)
            : null;

        OnPropertyChanged(nameof(StaticPlaylists));
        OnPropertyChanged(nameof(Folders));
    }

    /// <summary>
    /// Promotes the current query-bar search to an in-memory playlist that
    /// lives only until the app exits — unlike <see cref="CreatePlaylistAsync"/>,
    /// nothing is written to the store. Re-evaluated live like a smart
    /// playlist (same DSL evaluation path), so it tracks library changes.
    /// </summary>
    public void PinSearchAsTemporaryPlaylist(string name, string query)
    {
        var info = new PlaylistInfo(_nextEphemeralId--, name.Trim(), PlaylistKind.Smart, ParentId: null, query);
        _ephemeralPlaylists.Add(info);
        var node = new PlaylistNode { Info = info };
        PlaylistTree.Add(node);
        SelectedNode = node;
    }

    /// <summary>Drops an ephemeral search playlist from memory; no store write.</summary>
    public async Task RemoveEphemeralPlaylistAsync(PlaylistInfo playlist)
    {
        _ephemeralPlaylists.RemoveAll(p => p.Id == playlist.Id);
        if (PlaylistTree.FirstOrDefault(n => n.Info.Id == playlist.Id) is { } node)
        {
            PlaylistTree.Remove(node);
        }

        if (SelectedPlaylist?.Id != playlist.Id)
        {
            return;
        }

        SelectedNode = null;
        SelectedPlaylist = null;
        SelectedSource = Sources[0];
        await ReloadAsync();
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
        if (playlist.Id < 0)
        {
            // Ephemeral playlists have no store row and no folder membership;
            // the tree UI never offers this action for them, but guard
            // anyway since the id would otherwise be sent to SQLite.
            StatusText = "Temporary playlists can't be moved into a folder.";
            return;
        }

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

    /// <summary>Number of tracks matching a DSL query (editor live preview).</summary>
    public async Task<int> CountQueryMatchesAsync(string queryText)
        => (await _library.QueryDslAsync(queryText)).Ids.Length;

    /// <summary>Replaces a smart playlist's query; returns error text or null.</summary>
    public async Task<string?> UpdateSmartQueryAsync(PlaylistInfo playlist, string queryText)
    {
        if (playlist.Id < 0)
        {
            return "Temporary playlists can't be edited; pin a new search instead.";
        }

        try
        {
            await _library.UpdateSmartQueryAsync(playlist.Id, queryText);
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or QueryParseException
            or QueryCompileException)
        {
            return ex.Message;
        }

        await RefreshPlaylistsAsync();
        if (SelectedPlaylist?.Id == playlist.Id)
        {
            await ReloadAsync();
        }

        return null;
    }

    /// <summary>Renames a playlist; returns error text or null.</summary>
    public async Task<string?> RenamePlaylistAsync(PlaylistInfo playlist, string newName)
    {
        if (playlist.Id < 0)
        {
            return "Temporary playlists can't be renamed.";
        }

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
        if (playlist.Id < 0)
        {
            // The tree UI routes ephemeral entries to RemoveEphemeralPlaylistAsync
            // directly (a "Remove" item, not "Delete"); redirect here too in
            // case this is ever reached for one.
            await RemoveEphemeralPlaylistAsync(playlist);
            return;
        }

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

    /// <summary>
    /// Reorders one entry within the currently viewed static playlist (grid
    /// drag-drop). <paramref name="to"/> is already converted to
    /// <see cref="Library.Playlists.PlaylistStore.Move"/>'s post-removal
    /// index convention. The gate is re-checked here, defensively, since the
    /// view could in principle change between drag start and drop.
    /// </summary>
    public async Task<bool> MoveStaticPlaylistEntryAsync(int from, int to)
    {
        if (!CanReorderStaticPlaylist || SelectedPlaylist is not { } playlist)
        {
            return false;
        }

        await _library.MovePlaylistEntryAsync(playlist.Id, from, to);
        await ReloadAsync();
        return true;
    }

    /// <summary>Exports a playlist to the chosen file, absolute paths.</summary>
    public async Task ExportPlaylistAsync(PlaylistInfo playlist, string outPath)
    {
        if (playlist.Id < 0)
        {
            StatusText = "Temporary playlists can't be exported.";
            return;
        }

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

    /// <summary>Force re-scans all watch folders (tag refresh).</summary>
    public async Task RescanLibraryAsync()
    {
        StatusText = "Rescanning library…";
        var progress = new Progress<int>(n =>
        {
            if (n % 500 == 0)
            {
                StatusText = $"Rescanning library… {n} files";
            }
        });
        Library.Scanning.ScanResult result = await _library.RescanAllAsync(progress);
        StatusText =
            $"Rescan finished: {result.Added} added, {result.Updated} updated, " +
            $"{result.Skipped} skipped";
        await ReloadAsync();
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

    /// <summary>
    /// Switches the window to a different library file. Returns null on
    /// success, otherwise why the file was refused. Everything on screen is
    /// keyed by track and playlist ids, and none of those survive the move,
    /// so playback, the queue, and every cached view are dropped first.
    /// </summary>
    public async Task<string?> OpenLibraryAsync(string dbPath)
    {
        if (LibraryDatabase.DescribeLibraryProblem(dbPath) is { } problem)
        {
            return problem;
        }

        // Checked before anything is torn down; SwitchToAsync checks it again
        // to close the race, but by then the queue would already be gone.
        if (_library.IsBusy)
        {
            return "A scan or sync is still running against the current library.";
        }

        _player.StopAndClear();
        _artwork.UseCurrentLibrary();
        _ephemeralPlaylists.Clear();
        SelectedPlaylist = null;
        SelectedNode = null;
        SearchText = string.Empty;
        SortColumnKey = null;
        SortDescending = false;
        SelectedSource = Sources[0];
        StatusText = "Opening library…";

        try
        {
            await _library.SwitchToAsync(dbPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
        {
            StatusText = "Library unchanged.";
            return ex.Message;
        }

        await RefreshPlaylistsAsync();
        await ReloadAsync();
        await Queue.InitializeAsync();
        return null;
    }

    /// <summary>
    /// Throws away every cached cover and reloads, so re-tagged artwork is
    /// picked up. Nothing validates a stored thumbnail against its file, so
    /// this is the way back from a stale one.
    /// </summary>
    public async Task DiscardCoverCacheAsync()
    {
        _artwork.Discard();
        await ReloadAsync();
        StatusText = "Cover cache cleared.";
    }

    /// <summary>Track ids selected in the grouped layout, in view order.</summary>
    public long[] BlockSelectedIds
        => Tracks is { } tracks ? [.. _blockSelection.Indices.Select(tracks.IdAt)] : [];

    /// <summary>Lowest selected row index in the grouped layout, or -1 for none.</summary>
    public int BlockSelectedIndex => _blockSelection.FirstIndex;

    /// <summary>Whether a grouped-layout row is currently selected.</summary>
    public bool IsBlockRowSelected(int index) => _blockSelection.Contains(index);

    /// <summary>Click on a grouped-layout row; Ctrl toggles, Shift extends.</summary>
    public void SelectBlockRow(int index, bool toggle, bool extend)
        => _blockSelection.Select(index, toggle, extend, Tracks?.Count ?? 0);

    /// <summary>Selects a whole group block — its header was clicked.</summary>
    public void SelectBlockRange(int start, int count)
        => _blockSelection.SelectRange(start, count);

    /// <summary>Plays the grid row at <paramref name="rowIndex"/> with the current view as queue.</summary>
    public async Task PlayRowAsync(int rowIndex)
    {
        if (Tracks is null || rowIndex < 0 || rowIndex >= Tracks.Count)
        {
            return;
        }

        await _player.PlayQueueAsync(Tracks.Ids, rowIndex);
    }

    /// <summary>Inserts tracks right after whatever is currently playing.</summary>
    public Task PlayNextAsync(IReadOnlyList<long> trackIds) => _player.EnqueueNextAsync(trackIds);

    /// <summary>Appends tracks to the end of the playing queue.</summary>
    public Task AddToQueueAsync(IReadOnlyList<long> trackIds) => _player.EnqueueEndAsync(trackIds);

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
        if (value is { IsPlaylist: true })
        {
            SelectedPlaylist = value.Info;
        }
        else if (value is { IsFolder: true })
        {
            // Folder selection shows the union of its descendant playlists.
            SelectedPlaylist = null;
            SelectedSource = null;
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

    partial void OnSelectedLayoutChanged(LayoutSpec value)
    {
        if (!_applyingViewSettings)
        {
            // The new layout dictates its own sort list, so let the reload
            // re-derive the sort instead of carrying an incompatible one over.
            _ = PersistViewSettingsAsync();
            _ = ReloadAsync();
        }
    }

    partial void OnSelectedSortChanged(SortChoice? value)
    {
        if (!_applyingViewSettings && value is not null)
        {
            // Choosing a preset dismisses any header-click override.
            SortColumnKey = null;
            SortDescending = false;
            _ = PersistViewSettingsAsync();
            _ = ReloadAsync();
        }
    }

    /// <summary>
    /// Header click on a column: cycles that column Asc → Desc → back to
    /// the view's sort preset (backlog: 3-state header sorting).
    /// </summary>
    public async Task CycleColumnSortAsync(string columnKey)
    {
        if (!TrackPager.IsSortableColumn(columnKey))
        {
            return;
        }

        if (SortColumnKey != columnKey)
        {
            SortColumnKey = columnKey;
            SortDescending = false;
        }
        else if (!SortDescending)
        {
            SortDescending = true;
        }
        else
        {
            SortColumnKey = null;
            SortDescending = false;
        }

        await PersistViewSettingsAsync();
        await ReloadAsync();
    }

    private const string MusicScopeKey = "source:music";

    // Ephemeral playlists share one view-settings scope rather than each
    // getting "playlist:{id}": their ids are freshly minted every session,
    // so a per-id scope would just accumulate orphaned rows in the store.

    /// <summary>View-settings scope key for the currently selected view.</summary>
    public string CurrentScopeKey => SelectedPlaylist is { Id: < 0 }
        ? "ephemeral"
        : SelectedPlaylist is { } playlist
            ? $"playlist:{playlist.Id}"
            : SelectedNode is { IsFolder: true } folder
                ? $"folder:{folder.Info.Id}"
                : SelectedSource?.Kind == SourceKind.RecentlyAdded
                    ? "source:recent"
                    : MusicScopeKey;

    private Task PersistViewSettingsAsync()
    {
        // Inherited column state stays null in the store so this view keeps
        // following the Music view until the user customizes it here.
        return _library.SetViewSettingsAsync(CurrentScopeKey, new Library.Database.ViewSettings
        {
            Columns = _columnsInherited ? null : [.. CurrentColumns],
            // Null is "the flat grid", so an unset view and one explicitly
            // switched back to it read the same.
            Layout = SelectedLayout.IsGrouped ? SelectedLayout.Key : null,
            Sort = SelectedSort?.Order,
            SortColumn = SortColumnKey,
            SortDescending = SortDescending,
            ColumnWidths = !_widthsInherited && CurrentColumnWidths.Count > 0
                ? new Dictionary<string, double>(CurrentColumnWidths)
                : null,
        });
    }

    /// <summary>Applies a new column layout and persists it for this view.</summary>
    public async Task SetColumnsAsync(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            return; // never allow an empty grid
        }

        CurrentColumns = keys;
        _columnsInherited = false;
        await PersistViewSettingsAsync();
    }

    /// <summary>
    /// Persists captured column widths for a scope — usually the current
    /// view, but possibly the one just navigated away from when a resize
    /// settles across a view switch. The stale-scope path is a
    /// read-modify-write on that scope's stored settings, never a snapshot
    /// of current view state.
    /// </summary>
    public async Task SaveColumnWidthsAsync(string scopeKey, IReadOnlyDictionary<string, double> widths)
    {
        if (scopeKey == CurrentScopeKey)
        {
            CurrentColumnWidths = widths;
            _widthsInherited = false;
            await PersistViewSettingsAsync();
            return;
        }

        Library.Database.ViewSettings stored = await _library.GetViewSettingsAsync(scopeKey);
        await _library.SetViewSettingsAsync(scopeKey, stored with
        {
            ColumnWidths = widths.Count > 0 ? new Dictionary<string, double>(widths) : null,
        });
    }

    /// <summary>
    /// Clears this view's saved column layout and widths — only this one —
    /// so it falls back to the default: the Music view's state, or the
    /// built-in registry for the Music view itself.
    /// </summary>
    public async Task ResetColumnsToDefaultAsync()
    {
        Library.Database.ViewSettings stored =
            await _library.GetViewSettingsAsync(CurrentScopeKey);
        await _library.SetViewSettingsAsync(CurrentScopeKey, stored with
        {
            Columns = null,
            ColumnWidths = null,
        });
        await ReloadAsync();
    }

    /// <summary>
    /// Clears the saved column layout and widths of every view at once, the
    /// Music view included, so all of them fall back to the registry
    /// defaults. Sort state is left untouched.
    /// </summary>
    public async Task ResetAllColumnsToDefaultAsync()
    {
        await _library.ClearAllViewColumnsAsync();
        await ReloadAsync();
        StatusText = "Column layout reset in all views.";
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanPromoteSearch));
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    partial void OnIsDslSearchChanged(bool value)
    {
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            _ = ReloadAsync();
        }
    }

    // The reload in flight, so a newer one can abandon it. Superseding is not
    // enough on its own: without cancellation the abandoned query keeps a core
    // busy to completion, and every later reload queues behind it on the
    // library's gate.
    private CancellationTokenSource? _reloadCts;

    private async Task ReloadAsync()
    {
        var cts = new CancellationTokenSource();
        CancellationTokenSource? superseded = Interlocked.Exchange(ref _reloadCts, cts);
        if (superseded is not null)
        {
            superseded.Cancel();
            superseded.Dispose();
        }

        try
        {
            await ReloadAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer reload took over; its result is the one that matters.
        }
        finally
        {
            if (Interlocked.CompareExchange(ref _reloadCts, null, cts) == cts)
            {
                cts.Dispose();
            }
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        int generation = ++_loadGeneration;
        bool isPlaylist = SelectedPlaylist is not null;
        bool isFolder = SelectedNode is { IsFolder: true };
        bool isRecent = SelectedSource?.Kind == SourceKind.RecentlyAdded;

        // Per-view settings decide sort and columns before the query runs.
        Library.Database.ViewSettings viewSettings =
            await _library.GetViewSettingsAsync(CurrentScopeKey, cancellationToken);

        // Views with no saved column state follow the Music view: "default"
        // means whatever Music currently looks like, until the user
        // customizes columns or widths here.
        _columnsInherited = false;
        _widthsInherited = false;
        if (CurrentScopeKey != MusicScopeKey)
        {
            bool noColumns = viewSettings.Columns is not { Count: > 0 };
            bool noWidths = viewSettings.ColumnWidths is null;
            if (noColumns || noWidths)
            {
                Library.Database.ViewSettings music =
                    await _library.GetViewSettingsAsync(MusicScopeKey, cancellationToken);
                if (noColumns)
                {
                    viewSettings = viewSettings with { Columns = music.Columns };
                    _columnsInherited = true;
                }

                if (noWidths)
                {
                    viewSettings = viewSettings with { ColumnWidths = music.ColumnWidths };
                    _widthsInherited = true;
                }
            }
        }

        // A newer reload may have started while settings were loading; let
        // it win instead of applying stale view state over its result.
        if (generation != _loadGeneration)
        {
            return;
        }

        // A grouped layout brings its own sort list; only a group-contiguous
        // order can be rendered as blocks.
        LayoutSpec layout = TrackLayouts.Find(viewSettings.Layout);
        IReadOnlyList<SortChoice> available = layout.Sorts
            ?? (isPlaylist || isFolder ? PlaylistSorts : BrowseSorts);
        TrackSortOrder defaultSort = layout.IsGrouped
            ? layout.DefaultSort
            : isPlaylist || isFolder
                ? TrackSortOrder.PlaylistOrder
                : isRecent ? TrackSortOrder.RecentlyAdded : TrackSortOrder.Artist;
        TrackSortOrder sort = viewSettings.Sort ?? defaultSort;

        _applyingViewSettings = true;
        try
        {
            SelectedLayout = layout;
            AvailableSorts = available;
            // A sort the layout does not offer falls back to the layout's own
            // default rather than to whatever happens to head its list.
            SelectedSort = available.FirstOrDefault(c => c.Order == sort)
                ?? available.FirstOrDefault(c => c.Order == defaultSort)
                ?? available[0];
            // Header-click sorts belong to the grid; a grouped layout has no
            // headers to click and could not stay group-contiguous under one.
            SortColumnKey = !layout.IsGrouped && viewSettings.SortColumn is { } column
                && TrackPager.IsSortableColumn(column) ? column : null;
            SortDescending = SortColumnKey is not null && viewSettings.SortDescending;
            CurrentColumns = viewSettings.Columns is { Count: > 0 }
                ? viewSettings.Columns
                : TrackColumns.DefaultKeys;
            CurrentColumnWidths = viewSettings.ColumnWidths is { Count: > 0 }
                ? viewSettings.ColumnWidths
                : EmptyColumnWidths;
        }
        finally
        {
            _applyingViewSettings = false;
        }

        ViewSettingsApplied?.Invoke();

        sort = SelectedSort?.Order ?? defaultSort;

        // A header-click sort overrides the preset for any id source.
        ColumnSort? columnSort = SortColumnKey is { } sortKey
            ? new ColumnSort(sortKey, SortDescending)
            : null;

        // A grouped layout orders and groups in one query further down, so
        // the acquisition step here must not sort as well — that second walk
        // over the view was most of the cost of loading an album view.
        bool deferSort = layout.GroupField is not null;
        bool keepSourceOrder = false;

        long[]? ids = null;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            bool applyViewSort = true;
            if (IsDslSearch)
            {
                try
                {
                    (ids, bool ordered) = await _library.QueryDslAsync(SearchText.Trim(), cancellationToken);
                    // A query carrying its own ORDER BY keeps it (§7.3).
                    applyViewSort = !ordered;
                }
                catch (Exception ex) when (ex is QueryParseException or QueryCompileException)
                {
                    // Mid-typing parse errors are routine; keep the current
                    // view and report in the status line.
                    StatusText = $"Query error: {ex.Message}";
                    return;
                }
            }
            else
            {
                ids = await _library.SearchAsync(SearchText.Trim(), cancellationToken);
            }

            keepSourceOrder = !applyViewSort;
            if (applyViewSort && !deferSort)
            {
                ids = columnSort is not null
                    ? await _library.SortIdsAsync(ids, columnSort, cancellationToken)
                    : sort != TrackSortOrder.PlaylistOrder
                        ? await _library.SortIdsAsync(ids, sort, cancellationToken)
                        : ids;
            }
        }
        else if (SelectedPlaylist is { } playlist)
        {
            ids = await _library.EvaluatePlaylistAsync(playlist, cancellationToken);
            if (!deferSort)
            {
                ids = columnSort is not null
                    ? await _library.SortIdsAsync(ids, columnSort, cancellationToken)
                    : sort != TrackSortOrder.PlaylistOrder
                        ? await _library.SortIdsAsync(ids, sort, cancellationToken)
                        : ids;
            }
        }
        else if (SelectedNode is { IsFolder: true } folderNode)
        {
            ids = await EvaluateFolderAsync(folderNode, cancellationToken);
            if (!deferSort)
            {
                ids = columnSort is not null
                    ? await _library.SortIdsAsync(ids, columnSort, cancellationToken)
                    : sort != TrackSortOrder.PlaylistOrder
                        ? await _library.SortIdsAsync(ids, sort, cancellationToken)
                        : ids;
            }
        }
        else if (isRecent)
        {
            ids = await _library.BrowseRecentlyAddedAsync(cancellationToken);
            if (!deferSort)
            {
                ids = columnSort is not null
                    ? await _library.SortIdsAsync(ids, columnSort, cancellationToken)
                    : sort != TrackSortOrder.RecentlyAdded
                        ? await _library.SortIdsAsync(ids, sort, cancellationToken)
                        : ids;
            }
        }
        else if (columnSort is not null)
        {
            ids = await _library.BrowseAllAsync(columnSort, cancellationToken);
        }
        else if (!deferSort)
        {
            ids = await _library.BrowseAllAsync(
                sort == TrackSortOrder.PlaylistOrder ? TrackSortOrder.Artist : sort,
                cancellationToken);
        }

        if (generation != _loadGeneration)
        {
            return; // a newer reload superseded this one
        }

        // ids is still null only when browsing the whole library grouped, and
        // that is deliberate: the combined query reads the library directly,
        // so no id array is built, shipped and re-parsed just to be handed
        // straight back.
        List<TrackGroupRun>? runs = null;
        if (layout.GroupField is { } groupField)
        {
            (ids, runs) = await _library.LoadOrderedGroupRunsAsync(
                ids,
                keepSourceOrder ? TrackSortOrder.PlaylistOrder : sort,
                groupField,
                cancellationToken);
            if (generation != _loadGeneration)
            {
                return;
            }
        }

        long[] viewIds = ids ?? [];

        // A row object per id, and a block per group: O(view size) allocation
        // that has no business on the UI thread, where it would hold the
        // repaint until the whole list exists. Nothing built here is bound
        // yet, so it is safe to build off-thread and hand over below.
        (VirtualTrackList list, List<TrackGroupBlock>? blocks) = await Task.Run(
            () =>
            {
                var built = new VirtualTrackList(viewIds, RequestPage);
                return (
                    built,
                    runs is null
                        ? null
                        : TrackGroupBlocks.Build(built, runs, layout, RequestCover));
            },
            cancellationToken);

        if (generation != _loadGeneration)
        {
            return;
        }

        // Selection indexes the list being replaced, so it cannot survive it.
        _blockSelection.Reset();

        // Container recycling frees a block's cover when it scrolls away, but
        // replacing the whole list bypasses that; free them here rather than
        // waiting on a finalizer for native pixels.
        if (GroupedTracks is { } replaced)
        {
            foreach (TrackGroupBlock block in replaced)
            {
                block.ReleaseCover();
            }
        }

        Tracks = list;
        GroupedTracks = blocks;
        IsLibraryEmpty = viewIds.Length == 0 && string.IsNullOrWhiteSpace(SearchText);
        StatusText = blocks is null
            ? $"{viewIds.Length:N0} track(s)"
            : $"{viewIds.Length:N0} track(s) / {blocks.Count:N0} {layout.Label.ToLowerInvariant()}";
    }

    /// <summary>
    /// Union of every descendant playlist's tracks under a folder, deduped
    /// keeping each id's first occurrence in tree order.
    /// </summary>
    private async Task<long[]> EvaluateFolderAsync(
        PlaylistNode folder, CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        var union = new List<long>();
        foreach (PlaylistInfo playlist in DescendantPlaylists(folder))
        {
            long[] ids = await _library.EvaluatePlaylistAsync(playlist, cancellationToken);
            foreach (long id in ids)
            {
                if (seen.Add(id))
                {
                    union.Add(id);
                }
            }
        }

        return [.. union];
    }

    /// <summary>Depth-first walk yielding every playlist (not folder) under <paramref name="folder"/>.</summary>
    private static IEnumerable<PlaylistInfo> DescendantPlaylists(PlaylistNode folder)
    {
        foreach (PlaylistNode child in folder.Children)
        {
            if (child.IsFolder)
            {
                foreach (PlaylistInfo descendant in DescendantPlaylists(child))
                {
                    yield return descendant;
                }
            }
            else
            {
                yield return child.Info;
            }
        }
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

    // Decode widths. The cache stores one 512 px thumbnail per cover; each
    // consumer decodes it at the size it actually draws, so an album block
    // holds a quarter of the pixels the Now Playing panel does.
    private const int NowPlayingArtworkWidth = 400;
    private const int BlockCoverWidth = 256;

    /// <summary>Loads a block's cover once it has scrolled into view.</summary>
    private async void RequestCover(TrackGroupBlock block)
    {
        IReadOnlyList<TrackGroupBlock>? target = GroupedTracks;
        int token = block.CoverToken;
        Avalonia.Media.Imaging.Bitmap? cover =
            await DecodeArtworkAsync(block.Run.CoverTrackId, BlockCoverWidth);

        // Ignore covers for a block list that was replaced meanwhile; the
        // block itself rejects (and frees) one it no longer wants.
        if (!ReferenceEquals(target, GroupedTracks))
        {
            cover?.Dispose();
            return;
        }

        block.TrySetCover(cover, token);
    }

    /// <summary>
    /// A track's cover decoded to <paramref name="width"/>. The caller owns
    /// the bitmap; only the encoded thumbnail behind it is shared.
    /// </summary>
    private async Task<Avalonia.Media.Imaging.Bitmap?> DecodeArtworkAsync(
        long trackId, int width)
    {
        if (await _artwork.GetThumbnailAsync(trackId) is not { } thumbnail)
        {
            return null;
        }

        return await Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(thumbnail);
                return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, width);
            }
            catch (Exception)
            {
                // A thumbnail that will not decode is no different from none.
                return null;
            }
        });
    }

    private async Task ShowNowPlayingAsync(long id)
    {
        Dictionary<long, TrackPageRow> rows = await _library.HydrateAsync([id]);
        if (!rows.TryGetValue(id, out TrackPageRow? row))
        {
            return;
        }

        Avalonia.Media.Imaging.Bitmap? artwork =
            await DecodeArtworkAsync(id, NowPlayingArtworkWidth);
        Transport.SetNowPlaying(row.Title, row.Artist, row.Album, artwork);
    }

}
