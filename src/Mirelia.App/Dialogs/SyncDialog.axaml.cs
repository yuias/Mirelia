using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Mirelia.App.Services;
using Mirelia.App.ViewModels;
using Mirelia.Library.Playlists;
using Mirelia.Library.Sync;

namespace Mirelia.App.Dialogs;

/// <summary>
/// Configures folder sync targets and runs them. Settings are held in memory
/// and written when the user previews, syncs, switches target, or closes, so
/// typing a path does not produce a stream of database writes.
/// </summary>
public partial class SyncDialog : Window
{
    private sealed record Choice<T>(T Value, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>Illustrative track for the layout and playlist previews.</summary>
    private static readonly TrackLayoutInfo SampleTrack = new(
        TrackId: 1,
        SourcePath: @"X:\Music\Fixture Album\03 Fixture Song.flac",
        SizeBytes: 0,
        MTimeUnix: 0,
        ContentHash: null,
        DurationMs: 240000,
        Title: "Fixture Song",
        Artist: "Fixture Artist",
        AlbumArtist: "Fixture Artist",
        Album: "Fixture Album",
        AlbumId: 1,
        TrackNo: 3,
        DiscNo: 1,
        MultiDisc: false,
        Genre: "Rock",
        Year: 2020,
        SourceRelative: @"Fixture Album\03 Fixture Song.flac");

    private readonly LibraryService? _library;
    private readonly ObservableCollection<SyncPlaylistChoice> _playlistChoices = [];
    private List<SyncTarget> _targets = [];
    private SyncTarget? _current;
    private SyncPlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _loading;

    /// <summary>XAML-loader constructor.</summary>
    public SyncDialog()
    {
        InitializeComponent();
    }

    /// <summary>Creates the dialog over the library service.</summary>
    public SyncDialog(LibraryService library)
        : this()
    {
        _library = library;

        PresetBox.ItemsSource = new[]
        {
            new Choice<string>(PathTemplate.PresetArtistAlbum, "Artist / Album"),
            new Choice<string>(PathTemplate.PresetFlatAlbum, "Flat album folder"),
            new Choice<string>(PathTemplate.PresetMirrorSource, "Mirror source layout"),
        };
        ReservedBox.ItemsSource = new[]
        {
            new Choice<ReservedCharMode>(ReservedCharMode.Underscore, "Underscore (AC_DC)"),
            new Choice<ReservedCharMode>(ReservedCharMode.FullWidth, "Full-width (AC／DC)"),
        };
        FormatBox.ItemsSource = new[]
        {
            new Choice<string>("m3u8", ".m3u8 (UTF-8)"),
            new Choice<string>("m3u", ".m3u (legacy code page)"),
        };
        SeparatorBox.ItemsSource = new[]
        {
            new Choice<string>("\\", @"Backslash  \"),
            new Choice<string>("/", "Forward slash  /"),
        };
        FolderModeBox.ItemsSource = new[]
        {
            new Choice<PlaylistFolderMode>(PlaylistFolderMode.Flatten, "Flatten into one folder"),
            new Choice<PlaylistFolderMode>(PlaylistFolderMode.Mirror, "Mirror the playlist tree"),
        };

        PlaylistPicker.ItemsSource = _playlistChoices;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_library is null)
        {
            return;
        }

        foreach (PlaylistInfo playlist in await _library.GetPlaylistsAsync())
        {
            _playlistChoices.Add(new SyncPlaylistChoice(
                playlist.Id,
                playlist.Kind == PlaylistKind.Folder ? $"{playlist.Name}  (folder)" : playlist.Name,
                selected: false));
        }

        await ReloadTargetsAsync(null);
    }

    private async Task ReloadTargetsAsync(long? selectId)
    {
        if (_library is null)
        {
            return;
        }

        _targets = await _library.GetSyncTargetsAsync();
        _loading = true;
        TargetList.ItemsSource = _targets.Select(t => t.Name).ToList();
        _loading = false;

        int index = selectId is { } id ? _targets.FindIndex(t => t.Id == id) : 0;
        TargetList.SelectedIndex = _targets.Count == 0 ? -1 : Math.Max(index, 0);
        if (_targets.Count == 0)
        {
            _current = null;
            DetailPanel.IsEnabled = false;
            RemoveButton.IsEnabled = false;
        }
    }

    // ---- target list ---------------------------------------------------

    private async void OnAddTarget(object? sender, RoutedEventArgs e)
    {
        if (_library is null)
        {
            return;
        }

        await SaveCurrentAsync();
        string name = UniqueName("New target");
        SyncTarget created = await _library.CreateSyncTargetAsync(name, new SyncTargetConfig());
        await ReloadTargetsAsync(created.Id);
    }

    private string UniqueName(string basis)
    {
        if (_targets.All(t => t.Name != basis))
        {
            return basis;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{basis} {i}";
            if (_targets.All(t => t.Name != candidate))
            {
                return candidate;
            }
        }
    }

    private async void OnRemoveTarget(object? sender, RoutedEventArgs e)
    {
        if (_library is null || _current is null)
        {
            return;
        }

        // Only the record of the target goes; nothing on the device is touched.
        await _library.DeleteSyncTargetAsync(_current.Id);
        _current = null;
        InvalidatePlan();
        await ReloadTargetsAsync(null);
    }

    private async void OnTargetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _library is null)
        {
            return;
        }

        await SaveCurrentAsync();

        int index = TargetList.SelectedIndex;
        if (index < 0 || index >= _targets.Count)
        {
            _current = null;
            DetailPanel.IsEnabled = false;
            RemoveButton.IsEnabled = false;
            return;
        }

        _current = _targets[index];
        List<long> scope = await _library.GetSyncScopeAsync(_current.Id);
        ApplyToUi(_current, scope);
        DetailPanel.IsEnabled = true;
        RemoveButton.IsEnabled = true;
        InvalidatePlan();
    }

    // ---- binding between controls and config ---------------------------

    private void ApplyToUi(SyncTarget target, List<long> scope)
    {
        _loading = true;
        try
        {
            SyncTargetConfig config = target.Config;
            NameBox.Text = target.Name;
            MusicRootBox.Text = config.MusicRoot;
            PlaylistDirBox.Text = config.PlaylistDir;

            ScopeAllMusic.IsChecked = config.Scope == SyncScopeKind.AllMusic;
            ScopeAllPlaylists.IsChecked = config.Scope == SyncScopeKind.AllPlaylists;
            ScopeSelected.IsChecked = config.Scope == SyncScopeKind.Selected;
            PlaylistPickerBox.IsVisible = config.Scope == SyncScopeKind.Selected;

            var selected = new HashSet<long>(scope);
            foreach (SyncPlaylistChoice choice in _playlistChoices)
            {
                choice.IsSelected = selected.Contains(choice.Id);
            }

            TemplateBox.Text = config.LayoutTemplate;
            Select(PresetBox, config.LayoutTemplate);
            Select(ReservedBox, config.ReservedChars);
            Select(FormatBox, config.PlaylistFormat);
            Select(SeparatorBox, config.Separator);
            Select(FolderModeBox, config.PlaylistFolders);
            CodepageBox.Value = config.Codepage;
            CodepageBox.IsVisible = config.PlaylistFormat == "m3u";

            PathRelative.IsChecked = config.PathStyle == SyncPathStyle.Relative;
            PathAbsolute.IsChecked = config.PathStyle == SyncPathStyle.Absolute;
            DeviceRootBox.Text = config.DeviceRoot ?? string.Empty;
            DeviceRootBox.IsEnabled = config.PathStyle == SyncPathStyle.Absolute;

            OrphanCheck.IsChecked = config.OrphanPolicy == OrphanPolicy.Delete;
        }
        finally
        {
            _loading = false;
        }

        UpdatePreviews();
    }

    private static void Select<T>(ComboBox box, T value)
    {
        box.SelectedItem = box.ItemsSource?
            .OfType<Choice<T>>()
            .FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value));
    }

    private static T Value<T>(ComboBox box, T fallback) =>
        box.SelectedItem is Choice<T> choice ? choice.Value : fallback;

    private SyncTargetConfig ReadConfig() => new()
    {
        MusicRoot = MusicRootBox.Text ?? string.Empty,
        PlaylistDir = PlaylistDirBox.Text ?? string.Empty,
        Scope = ScopeAllMusic.IsChecked == true ? SyncScopeKind.AllMusic
            : ScopeSelected.IsChecked == true ? SyncScopeKind.Selected
            : SyncScopeKind.AllPlaylists,
        PathStyle = PathAbsolute.IsChecked == true ? SyncPathStyle.Absolute : SyncPathStyle.Relative,
        DeviceRoot = string.IsNullOrWhiteSpace(DeviceRootBox.Text) ? null : DeviceRootBox.Text,
        Separator = Value(SeparatorBox, "\\"),
        PlaylistFormat = Value(FormatBox, "m3u8"),
        Codepage = (int?)CodepageBox.Value ?? 1252,
        PlaylistFolders = Value(FolderModeBox, PlaylistFolderMode.Flatten),
        LayoutTemplate = string.IsNullOrWhiteSpace(TemplateBox.Text)
            ? PathTemplate.Default
            : TemplateBox.Text,
        ReservedChars = Value(ReservedBox, ReservedCharMode.Underscore),
        OrphanPolicy = OrphanCheck.IsChecked == true ? OrphanPolicy.Delete : OrphanPolicy.Keep,
    };

    private async Task SaveCurrentAsync()
    {
        if (_library is null || _current is null)
        {
            return;
        }

        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? _current.Name : NameBox.Text.Trim();
        SyncTargetConfig config = ReadConfig();
        long[] scope = [.. _playlistChoices.Where(c => c.IsSelected).Select(c => c.Id)];

        await _library.SaveSyncTargetAsync(_current.Id, name, config, scope);
        _current = _current with { Name = name, Config = config };
    }

    // ---- change handlers -----------------------------------------------

    // Avalonia's XAML compiler matches handlers by exact delegate signature,
    // so each event needs its own entry point into the shared logic.
    private void OnTextChanged(object? sender, TextChangedEventArgs e) => OnSettingChanged();

    private void OnComboChanged(object? sender, SelectionChangedEventArgs e) => OnSettingChanged();

    private void OnNumericChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        OnSettingChanged();

    private void OnCheckChanged(object? sender, RoutedEventArgs e) => OnSettingChanged();

    private void OnSettingChanged()
    {
        if (_loading)
        {
            return;
        }

        InvalidatePlan();
        UpdatePreviews();
    }

    private void OnScopeChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        PlaylistPickerBox.IsVisible = ScopeSelected.IsChecked == true;
        InvalidatePlan();
    }

    private void OnPathStyleChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        DeviceRootBox.IsEnabled = PathAbsolute.IsChecked == true;
        InvalidatePlan();
        UpdatePreviews();
    }

    private void OnFormatChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        CodepageBox.IsVisible = Value(FormatBox, "m3u8") == "m3u";
        InvalidatePlan();
        UpdatePreviews();
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetBox.SelectedItem is not Choice<string> preset)
        {
            return;
        }

        TemplateBox.Text = preset.Value;
    }

    private void InvalidatePlan()
    {
        _plan = null;
        SyncButton.IsEnabled = false;
    }

    private void UpdatePreviews()
    {
        SyncTargetConfig config = ReadConfig();

        string? error = PathTemplate.Validate(config.LayoutTemplate);
        if (error is not null)
        {
            LayoutPreview.Text = error;
            PlaylistPreview.Text = string.Empty;
            return;
        }

        string relPath = PathTemplate.Render(config.LayoutTemplate, SampleTrack, config.ReservedChars);
        string onDisk = Path.Combine(
            string.IsNullOrWhiteSpace(config.MusicRoot) ? "…" : config.MusicRoot,
            relPath.Replace('/', Path.DirectorySeparatorChar));
        LayoutPreview.Text = $"Example:  {onDisk}";

        string playlistName = "Favourites" + (config.PlaylistFormat == "m3u" ? ".m3u" : ".m3u8");
        PlaylistPreview.Text =
            $"Written into {playlistName}:  {SyncPaths.Reference(relPath, playlistName, config)}";
    }

    // ---- folder pickers ------------------------------------------------

    private async void OnBrowseMusicRoot(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Music folder on the device") is { } path)
        {
            MusicRootBox.Text = path;
        }
    }

    private async void OnBrowsePlaylistDir(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Playlist folder on the device") is { } path)
        {
            PlaylistDirBox.Text = path;
        }
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 1 ? folders[0].TryGetLocalPath() : null;
    }

    // ---- preflight and run ---------------------------------------------

    private async void OnPreview(object? sender, RoutedEventArgs e)
    {
        await PreviewAsync();
    }

    private async Task<SyncPlan?> PreviewAsync()
    {
        if (_library is null || _current is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(MusicRootBox.Text))
        {
            SummaryText.Text = "Choose a music folder first.";
            return null;
        }

        await SaveCurrentAsync();
        PreviewButton.IsEnabled = false;
        try
        {
            _plan = await _library.PlanSyncAsync(_current);
            ShowPlan(_plan);
            SyncButton.IsEnabled = !_plan.IsEmpty && _plan.FitsInFreeSpace;
            return _plan;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SummaryText.Text = $"Could not read the destination: {ex.Message}";
            return null;
        }
        finally
        {
            PreviewButton.IsEnabled = true;
        }
    }

    private void ShowPlan(SyncPlan plan)
    {
        string free = plan.FreeSpaceBytes is { } bytes
            ? $"free space {SyncPlanner.FormatBytes(bytes)}"
            : "free space unknown";

        SummaryText.Text = string.Create(
            CultureInfo.CurrentCulture,
            $"Copy {plan.Copies.Count} files ({SyncPlanner.FormatBytes(plan.BytesToCopy)})  ·  "
            + $"delete {plan.Deletes.Count}  ·  {plan.Playlists.Count} playlists  ·  "
            + $"{plan.UnchangedCount} already current  ·  {free}");

        var warnings = new List<string>(plan.Warnings);
        if (!plan.FitsInFreeSpace)
        {
            warnings.Insert(0, "Not enough free space on the destination.");
        }

        if (plan.MissingSourceCount > 0)
        {
            warnings.Add($"{plan.MissingSourceCount} tracks were skipped; their files are missing.");
        }

        WarningText.IsVisible = warnings.Count > 0;
        WarningText.Text = string.Join(Environment.NewLine, warnings);
    }

    private async void OnSync(object? sender, RoutedEventArgs e)
    {
        if (_library is null || _current is null)
        {
            return;
        }

        // Re-plan rather than trusting the previewed one: the library or the
        // device may have moved since the user looked at it.
        SyncPlan? plan = await PreviewAsync();
        if (plan is null || plan.IsEmpty)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        SetRunning(true);
        var progress = new Progress<SyncProgress>(p =>
        {
            Progress.Maximum = Math.Max(p.Total, 1);
            Progress.Value = p.Completed;
            SummaryText.Text = p.Current.Length > 0
                ? $"{p.Completed}/{p.Total}  {p.Current}"
                : $"{p.Completed}/{p.Total}";
        });

        try
        {
            SyncResult result = await _library.RunSyncAsync(plan, progress, _cts.Token);
            SummaryText.Text = string.Create(
                CultureInfo.CurrentCulture,
                $"Copied {result.Copied} ({SyncPlanner.FormatBytes(result.BytesCopied)}), "
                + $"deleted {result.Deleted}, wrote {result.PlaylistsWritten} playlists.");
            WarningText.IsVisible = result.Failures.Count > 0;
            WarningText.Text = string.Join(Environment.NewLine, result.Failures.Take(10));
        }
        catch (OperationCanceledException)
        {
            SummaryText.Text = "Sync stopped. Files already copied are recorded and will not be redone.";
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
            InvalidatePlan();
        }
    }

    private void SetRunning(bool running)
    {
        Progress.IsVisible = running;
        CancelButton.IsVisible = running;
        SyncButton.IsEnabled = false;
        PreviewButton.IsEnabled = !running;
        DetailPanel.IsEnabled = !running;
        TargetList.IsEnabled = !running;
    }

    private void OnStop(object? sender, RoutedEventArgs e) => _cts?.Cancel();

    private async void OnClose(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        await SaveCurrentAsync();
        Close();
    }
}
