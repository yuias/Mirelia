using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Mirelia.App.Services;
using Mirelia.App.ViewModels;
using Mirelia.Library.Playlists;
using Mirelia.Library.Sync;
using Mirelia.Sync.Mtp;

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

    /// <summary>
    /// A portable device the user can point a target at. A target keeps its
    /// device even while it is unplugged, so the list holds absent ones too.
    /// </summary>
    private sealed record DeviceChoice(string Id, string Name, bool Connected)
    {
        public override string ToString() => Connected ? Name : $"{Name}  (not connected)";
    }

    /// <summary>Illustrative track for the layout and playlist previews.</summary>
    private static readonly TrackLayoutInfo SampleTrack = new(
        TrackId: 1,
        SourcePath: @"X:\Music\Fixture Album\03 Fixture Song.flac",
        SizeBytes: 0,
        MTimeUnix: 0,
        ContentHash: null,
        DurationMs: 240000,
        Codec: "flac",
        IsLossless: true,
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

    // A second list over the same playlists: scope is what the target holds,
    // force is a one-shot instruction for the next run.
    private readonly ObservableCollection<SyncPlaylistChoice> _forceChoices = [];
    private readonly ObservableCollection<SyncCodecChoice> _codecChoices = [];
    private List<SyncTarget> _targets = [];
    private IReadOnlyList<MtpDeviceInfo> _devices = [];
    private SyncTarget? _current;
    private SyncPlan? _plan;
    private CancellationTokenSource? _cts;
    private bool _loading;

    /// <summary>XAML-loader constructor.</summary>
    public SyncDialog()
    {
        InitializeComponent();

        // Device sync goes through Windows Portable Devices; elsewhere the
        // button could only ever create a target that finds no device.
        if (!LibraryService.MtpSupported)
        {
            AddDeviceButton.IsEnabled = false;
            NoMtpNote.IsVisible = true;
        }
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
        ForcePicker.ItemsSource = _forceChoices;
        CodecPicker.ItemsSource = _codecChoices;
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
            string label = playlist.Kind == PlaylistKind.Folder
                ? $"{playlist.Name}  (folder)"
                : playlist.Name;
            _playlistChoices.Add(new SyncPlaylistChoice(playlist.Id, label, selected: false));
            _forceChoices.Add(new SyncPlaylistChoice(playlist.Id, label, selected: false));
        }

        foreach (string codec in await _library.GetCodecsAsync())
        {
            _codecChoices.Add(new SyncCodecChoice(codec, selected: true));
        }

        _devices = await LibraryService.GetMtpDevicesAsync();
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
        TargetList.ItemsSource = _targets.Select(TargetLabel).ToList();
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

    /// <summary>
    /// Names a target in the list. An MTP target says which device it is for,
    /// since its own name rarely does and the device may be unplugged.
    /// </summary>
    private string TargetLabel(SyncTarget target)
    {
        if (target.Kind != SyncTargetKind.Mtp)
        {
            return target.Name;
        }

        bool connected = target.Config.DeviceId is { } id
            && _devices.Any(d => d.DeviceId == id);
        string device = target.Config.DeviceLabel ?? "no device";
        return connected ? $"{target.Name}  ·  {device}" : $"{target.Name}  ·  {device} (not connected)";
    }

    private async void OnAddFolderTarget(object? sender, RoutedEventArgs e)
    {
        await AddTargetAsync(SyncTargetKind.Folder, new SyncTargetConfig());
    }

    private async void OnAddDeviceTarget(object? sender, RoutedEventArgs e)
    {
        // Android's own layout, so a fresh target is usable without editing.
        MtpDeviceInfo? device = _devices.Count > 0 ? _devices[0] : null;
        await AddTargetAsync(
            SyncTargetKind.Mtp,
            new SyncTargetConfig
            {
                DeviceId = device?.DeviceId,
                DeviceLabel = device?.Label,
                MusicRoot = "Internal shared storage/Music",
                PlaylistDir = "Internal shared storage/Playlists",
            });
    }

    private async Task AddTargetAsync(SyncTargetKind kind, SyncTargetConfig config)
    {
        if (_library is null)
        {
            return;
        }

        await SaveCurrentAsync();
        string name = UniqueName(kind == SyncTargetKind.Mtp ? "New device" : "New target");
        SyncTarget created = await _library.CreateSyncTargetAsync(name, kind, config);
        await ReloadTargetsAsync(created.Id);
    }

    private async void OnRefreshDevices(object? sender, RoutedEventArgs e)
    {
        _devices = await LibraryService.GetMtpDevicesAsync();

        _loading = true;
        try
        {
            int selected = TargetList.SelectedIndex;
            TargetList.ItemsSource = _targets.Select(TargetLabel).ToList();
            TargetList.SelectedIndex = selected;
            if (_current is not null)
            {
                FillDeviceBox(_current.Config);
            }
        }
        finally
        {
            _loading = false;
        }
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

            // MTP has no drive letter, so the OS folder picker cannot reach it;
            // the paths are typed as the device itself shows them.
            bool mtp = target.Kind == SyncTargetKind.Mtp;
            DevicePanel.IsVisible = mtp;
            BrowseMusicButton.IsVisible = !mtp;
            BrowsePlaylistButton.IsVisible = !mtp;
            FillDeviceBox(config);

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

            // A null profile and a profile listing every codec mean the same
            // thing to the policy; the checkbox shows the former.
            List<string>? supported = config.Device?.SupportedCodecs;
            AllCodecsCheck.IsChecked = supported is not { Count: > 0 };
            CodecPickerBox.IsEnabled = supported is { Count: > 0 };
            foreach (SyncCodecChoice choice in _codecChoices)
            {
                choice.IsSelected = supported is not { Count: > 0 }
                    || supported.Contains(choice.Codec, StringComparer.OrdinalIgnoreCase);
            }

            SkipLosslessCheck.IsChecked = config.Device?.SkipLossless == true;

            // Force is per-run, not per-target, so switching target clears it.
            ForceAllCheck.IsChecked = false;
            ForcePickerBox.IsEnabled = true;
            foreach (SyncPlaylistChoice choice in _forceChoices)
            {
                choice.IsSelected = false;
            }
        }
        finally
        {
            _loading = false;
        }

        UpdatePreviews();
    }

    /// <summary>
    /// Lists the connected devices plus, when it is not among them, the one
    /// this target already names — losing that on an unplugged phone would
    /// silently repoint the target.
    /// </summary>
    private void FillDeviceBox(SyncTargetConfig config)
    {
        var choices = _devices
            .Select(d => new DeviceChoice(d.DeviceId, d.Label, Connected: true))
            .ToList();

        if (config.DeviceId is { Length: > 0 } id && choices.All(c => c.Id != id))
        {
            choices.Insert(0, new DeviceChoice(id, config.DeviceLabel ?? id, Connected: false));
        }

        DeviceBox.ItemsSource = choices;
        DeviceBox.SelectedItem = choices.FirstOrDefault(c => c.Id == config.DeviceId);
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
        DeviceId = SelectedDevice?.Id,
        DeviceLabel = SelectedDevice?.Name,
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
        Device = ReadDeviceProfile(),
    };

    /// <summary>The device a target points at, or null when it is not an MTP one.</summary>
    private DeviceChoice? SelectedDevice => _current?.Kind == SyncTargetKind.Mtp
        ? DeviceBox.SelectedItem as DeviceChoice
        : null;

    private DeviceProfile? ReadDeviceProfile()
    {
        bool everyCodec = AllCodecsCheck.IsChecked == true;
        bool skipLossless = SkipLosslessCheck.IsChecked == true;
        if (everyCodec && !skipLossless)
        {
            return null;
        }

        return new DeviceProfile
        {
            SupportedCodecs = everyCodec
                ? null
                : [.. _codecChoices.Where(c => c.IsSelected).Select(c => c.Codec)],
            SkipLossless = skipLossless,
        };
    }

    private SyncPlanOptions ReadForceOptions() => new()
    {
        ForceAll = ForceAllCheck.IsChecked == true,
        ForcePlaylistIds = [.. _forceChoices.Where(c => c.IsSelected).Select(c => c.Id)],
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

    private void OnAllCodecsChanged(object? sender, RoutedEventArgs e)
    {
        CodecPickerBox.IsEnabled = AllCodecsCheck.IsChecked != true;
        OnSettingChanged();
    }

    private void OnForceAllChanged(object? sender, RoutedEventArgs e)
    {
        // Forcing everything makes the per-playlist choice meaningless.
        ForcePickerBox.IsEnabled = ForceAllCheck.IsChecked != true;
        OnSettingChanged();
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
        string root = string.IsNullOrWhiteSpace(config.MusicRoot) ? "…" : config.MusicRoot;
        string example = _current?.Kind == SyncTargetKind.Mtp
            ? $"{root.TrimEnd('/')}/{relPath}"
            : Path.Combine(root, relPath.Replace('/', Path.DirectorySeparatorChar));
        LayoutPreview.Text = $"Example:  {example}";

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

        if (_current.Kind == SyncTargetKind.Mtp && SelectedDevice is null)
        {
            SummaryText.Text = "Connect the device and choose it above first.";
            return null;
        }

        await SaveCurrentAsync();
        PreviewButton.IsEnabled = false;
        try
        {
            _plan = await _library.PlanSyncAsync(_current, ReadForceOptions());
            ShowPlan(_plan);
            SyncButton.IsEnabled = !_plan.IsEmpty && _plan.FitsInFreeSpace;
            return _plan;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or MtpDeviceLostException
            or PlatformNotSupportedException)
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

        foreach ((string reason, int count) in plan.SkippedByPolicy)
        {
            warnings.Add($"{count} tracks skipped: {reason}.");
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
            ClearForce();
        }
        catch (OperationCanceledException)
        {
            SummaryText.Text = "Sync stopped. Files already copied are recorded and will not be redone.";
        }
        catch (MtpDeviceLostException ex)
        {
            // One unwritable file is survivable and already reported per file;
            // a device that went away is not, so the run ended here.
            SummaryText.Text = $"The device disconnected: {ex.Message} "
                + "Files already copied are recorded and will not be redone.";
        }
        finally
        {
            SetRunning(false);
            _cts?.Dispose();
            _cts = null;
            InvalidatePlan();
        }
    }

    /// <summary>Drops the force selection once it has been acted on.</summary>
    private void ClearForce()
    {
        _loading = true;
        try
        {
            ForceAllCheck.IsChecked = false;
            ForcePickerBox.IsEnabled = true;
            foreach (SyncPlaylistChoice choice in _forceChoices)
            {
                choice.IsSelected = false;
            }
        }
        finally
        {
            _loading = false;
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
