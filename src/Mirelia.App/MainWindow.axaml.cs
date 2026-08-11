using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mirelia.App.Dialogs;
using Mirelia.App.Models;
using Mirelia.App.ViewModels;
using Mirelia.Library.Playlists;

namespace Mirelia.App;

/// <summary>Main shell window; UI-only glue lives here, state in the view-model.</summary>
public partial class MainWindow : Window
{
    private static readonly DataFormat<long[]> TrackIdsFormat =
        DataFormat.CreateInProcessFormat<long[]>("mirelia/track-ids");

    // 6% gray banding reads correctly on both light and dark themes.
    private static readonly Avalonia.Media.IBrush BandingBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x0F, 0x80, 0x80, 0x80));

    private Point _dragOrigin;
    private PointerPressedEventArgs? _dragPress;

    // Last known bounds while WindowState was Normal, so a maximized (or
    // minimized) window's transient bounds never get saved in place of the
    // real normal-state geometry to restore next launch.
    private PixelPoint? _normalPosition;
    private Size? _normalClientSize;

    // DataGrid exposes no resize-committed event, so column widths are
    // captured off LayoutUpdated (which fires on every resize drag frame)
    // and debounced: only the settled width, once layout goes quiet, is
    // written through to view settings.
    private readonly DispatcherTimer _columnWidthDebounce;

    // RowHeight is not an inherited property, unlike FontFamily/FontSize, so
    // it needs to be recomputed and pushed explicitly whenever the font size
    // setting changes.
    private const double BaseFontSize = 14;
    private const double BaseRowHeight = 26;

    /// <summary>Initializes the window.</summary>
    public MainWindow()
    {
        InitializeComponent();
        RestoreWindowPlacement();

        ApplyFontScaledRowHeight(App.Settings?.Current.FontSize ?? BaseFontSize);
        if (App.Settings is { } settings)
        {
            settings.Changed += s => ApplyFontScaledRowHeight(s.FontSize);
        }

        _columnWidthDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _columnWidthDebounce.Tick += async (_, _) =>
        {
            _columnWidthDebounce.Stop();
            await PersistColumnWidthsAsync();
        };
        Grid.LayoutUpdated += (_, _) =>
        {
            _columnWidthDebounce.Stop();
            _columnWidthDebounce.Start();
        };

        // Track normal-state geometry as the window moves/resizes, and
        // persist it (plus maximized state) on close.
        PositionChanged += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalPosition = e.Point;
            }
        };
        Resized += (_, e) =>
        {
            if (WindowState == WindowState.Normal)
            {
                _normalClientSize = e.ClientSize;
            }
        };
        Closing += OnWindowClosing;

        // Visibility-driven hydration: rows realized by the grid pull their
        // page from the database (see VirtualTrackList). The same hook does
        // row banding by data index — reliable under row recycling, where
        // positional CSS selectors are not.
        Grid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is TrackRow row)
            {
                ViewModel?.Tracks?.NotifyVisible(row.Index);
                e.Row.Background = row.Index % 2 == 1 ? BandingBrush : null;
            }
        };

        // Drag source: selected grid rows. The DataGrid marks pointer events
        // handled for its own selection gestures, so observe them anyway.
        Grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        Grid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel);

        // Drop targets: playlists take track ids, the window takes files.
        PlaylistTreeView.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        PlaylistTreeView.AddHandler(DragDrop.DropEvent, OnPlaylistDrop);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);

        // Tree context menus are built in code: the "Move to" submenu is
        // per-node and data-driven, which XAML templates handle poorly.
        PlaylistTreeView.AddHandler(ContextRequestedEvent, OnTreeContextRequested);

        // Columns are data-driven from per-view settings.
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                RebuildColumns();
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MainViewModel.CurrentColumns))
                    {
                        RebuildColumns();
                    }
                    else if (args.PropertyName is nameof(MainViewModel.SortColumnKey)
                        or nameof(MainViewModel.SortDescending))
                    {
                        UpdateSortGlyphs();
                    }
                };
            }
        };
        Grid.ColumnReordered += async (_, _) => await PersistColumnOrderAsync();
    }

    /// <summary>
    /// Scales the grid row height with the font size so larger text does not
    /// clip, keeping the 26px row height at the default 14pt font size.
    /// </summary>
    private void ApplyFontScaledRowHeight(double fontSize)
        => Grid.RowHeight = Math.Max(BaseRowHeight, Math.Round(BaseRowHeight * fontSize / BaseFontSize));

    // ---- window placement persistence -----------------------------------

    private const double MinNormalWidth = 400;
    private const double MinNormalHeight = 300;

    /// <summary>
    /// Applies saved window geometry over the XAML defaults (first-run
    /// values). Saved bounds are only trusted if they still land on a
    /// connected screen, so an unplugged monitor cannot strand the window
    /// off-screen.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        if (App.Settings?.Current is not
            {
                WindowX: int x, WindowY: int y,
                WindowWidth: double width, WindowHeight: double height,
            } settings)
        {
            return;
        }

        width = Math.Max(width, MinNormalWidth);
        height = Math.Max(height, MinNormalHeight);
        var position = new PixelPoint(x, y);

        if (FitsOnAnyScreen(position, width, height))
        {
            Position = position;
            Width = width;
            Height = height;
            _normalPosition = position;
            _normalClientSize = new Size(width, height);
        }

        // Maximize on top of the restored (or default) normal bounds so
        // un-maximizing later lands on a sane rectangle.
        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private bool FitsOnAnyScreen(PixelPoint position, double width, double height)
    {
        IReadOnlyList<Screen>? screens = Screens?.All;
        if (screens is null || screens.Count == 0)
        {
            return true; // No screen info available yet; trust the saved value.
        }

        return screens.Any(screen =>
        {
            var size = new PixelSize(
                (int)Math.Round(width * screen.Scaling),
                (int)Math.Round(height * screen.Scaling));
            return screen.WorkingArea.Intersects(new PixelRect(position, size));
        });
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Flush any width resized within the last debounce window; best
        // effort since the process may exit before this write lands.
        _columnWidthDebounce.Stop();
        _ = PersistColumnWidthsAsync();

        if (App.Settings is not { } settings)
        {
            return;
        }

        // Fall back to the current bounds if the window never reported a
        // Normal-state PositionChanged/Resized (e.g. it launched maximized).
        PixelPoint position = _normalPosition ?? Position;
        Size size = _normalClientSize ?? ClientSize;

        settings.Save(settings.Current with
        {
            WindowX = position.X,
            WindowY = position.Y,
            WindowWidth = size.Width,
            WindowHeight = size.Height,
            WindowMaximized = WindowState == WindowState.Maximized,
        });
    }

    private bool _rebuildingColumns;

    private void RebuildColumns()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        IReadOnlyList<string> keys = vm.CurrentColumns;

        // Skip no-op rebuilds so a reorder-persist round trip does not reset
        // the grid's column state mid-drag.
        var currentKeys = Grid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(c => c.Tag as string)
            .ToList();
        if (currentKeys.SequenceEqual(keys))
        {
            return;
        }

        IReadOnlyDictionary<string, double> widths = vm.CurrentColumnWidths;

        _rebuildingColumns = true;
        try
        {
            Grid.Columns.Clear();
            foreach (string key in keys)
            {
                if (Models.TrackColumns.Find(key) is not { } spec)
                {
                    continue;
                }

                // Once a column has been user-resized it is pinned to that
                // pixel width, even if the registry defines it as star-sized;
                // untouched columns keep following the registry.
                bool resized = widths.TryGetValue(key, out double savedWidth);
                double width = resized ? savedWidth : spec.Width;
                bool isStar = spec.IsStar && !resized;
                DataGridLength gridWidth = isStar
                    ? new DataGridLength(width, DataGridLengthUnitType.Star)
                    : new DataGridLength(width);

                if (spec.Key == "rating")
                {
                    Grid.Columns.Add(new DataGridTemplateColumn
                    {
                        Header = spec.Header,
                        Width = gridWidth,
                        Tag = spec.Key,
                        CellTemplate = RatingCellTemplate,
                    });
                    continue;
                }

                Grid.Columns.Add(new DataGridTextColumn
                {
                    Header = spec.Header,
                    Binding = new Avalonia.Data.Binding(spec.Property),
                    Width = gridWidth,
                    Tag = spec.Key,
                });
            }
        }
        finally
        {
            _rebuildingColumns = false;
        }

        UpdateSortGlyphs();
    }

    // Built once and shared by every rating cell: each star's glyph is a
    // property binding (not a value captured at build time), so recycled
    // cells stay correct across virtualization and an in-place rating edit
    // repaints through TrackRow's own INPC rather than needing a rebuild.
    private static readonly IDataTemplate RatingCellTemplate = BuildRatingCellTemplate();

    private static IDataTemplate BuildRatingCellTemplate()
    {
        return new FuncDataTemplate<TrackRow>((_, _) =>
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            for (int star = 1; star <= 5; star++)
            {
                var glyph = new TextBlock
                {
                    FontSize = 13,
                    Tag = star,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };
                glyph.Bind(TextBlock.TextProperty, new Binding(nameof(TrackRow.Rating))
                {
                    Converter = RatingStarConverter.Instance,
                    ConverterParameter = star,
                });
                glyph.PointerPressed += OnRatingStarPressed;
                panel.Children.Add(glyph);
            }

            return panel;
        });
    }

    /// <summary>
    /// Click on star N sets the row's rating to N stars; clicking the
    /// currently-set star again clears it to unrated. Writes go straight to
    /// <see cref="App.Library"/> from here (not through the view-model) per
    /// the "UI-only glue in code-behind" rule at the top of this class.
    /// </summary>
    private static async void OnRatingStarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: TrackRow row, Tag: int clicked }
            || App.Library is not { } library)
        {
            return;
        }

        // Consume the press so it neither arms the grid's row-drag gesture
        // nor reaches the DataGrid's own row-selection handling.
        e.Handled = true;

        int newStars = row.Rating == clicked ? 0 : clicked;

        // Optimistic: TrackRow.Rating is an [ObservableProperty], so this
        // repaints the bound star glyphs immediately, in place, without
        // waiting on the write or reloading the page.
        row.Rating = newStars;
        await library.SetTrackRatingAsync(row.Id, newStars == 0 ? null : newStars * 20);
    }

    /// <summary>
    /// Header click: sorting is routed through SQL (§7.2), never the
    /// grid's collection view — Handled suppresses the in-memory sort.
    /// </summary>
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        if (e.Column.Tag is string key && ViewModel is { } vm)
        {
            _ = vm.CycleColumnSortAsync(key);
        }
    }

    private void UpdateSortGlyphs()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        foreach (DataGridColumn column in Grid.Columns)
        {
            if (column.Tag is not string key || Models.TrackColumns.Find(key) is not { } spec)
            {
                continue;
            }

            column.Header = key == vm.SortColumnKey
                ? spec.Header + (vm.SortDescending ? " ▼" : " ▲")
                : spec.Header;
        }
    }

    private async Task PersistColumnOrderAsync()
    {
        if (_rebuildingColumns || ViewModel is not { } vm)
        {
            return;
        }

        var keys = Grid.Columns
            .OrderBy(c => c.DisplayIndex)
            .Select(c => c.Tag as string)
            .OfType<string>()
            .ToList();
        if (keys.Count > 0)
        {
            await vm.SetColumnsAsync(keys);
        }
    }

    /// <summary>
    /// Reads each column's settled <c>ActualWidth</c> and persists it if it
    /// differs from what is stored — the debounced tail end of a resize
    /// drag, or the final flush on window close.
    /// </summary>
    private async Task PersistColumnWidthsAsync()
    {
        if (_rebuildingColumns || ViewModel is not { } vm)
        {
            return;
        }

        // A user resize turns a column's Width into a pixel DataGridLength,
        // so still-star columns are ones the user never touched: skip them,
        // otherwise the first settle would pin them at the current window
        // width and they would stop stretching.
        Dictionary<string, double> widths = Grid.Columns
            .Where(c => c.Tag is string && c.ActualWidth > 0
                && c.Width.UnitType != DataGridLengthUnitType.Star)
            .ToDictionary(c => (string)c.Tag!, c => c.ActualWidth);

        if (widths.Count > 0 && !WidthsEqual(widths, vm.CurrentColumnWidths))
        {
            await vm.SetColumnWidthsAsync(widths);
        }
    }

    private static bool WidthsEqual(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        // Sub-pixel drift from layout rounding should not trigger a write.
        const double tolerance = 0.5;
        return a.All(pair => b.TryGetValue(pair.Key, out double other)
            && Math.Abs(pair.Value - other) < tolerance);
    }

    private void OnGridMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        ColumnsMenu.Items.Clear();
        foreach (Models.ColumnSpec spec in Models.TrackColumns.All)
        {
            bool visible = vm.CurrentColumns.Contains(spec.Key);
            var item = new MenuItem
            {
                Header = spec.Header,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = visible,
                StaysOpenOnClick = true,
            };
            string key = spec.Key;
            item.Click += async (_, _) =>
            {
                List<string> keys = [.. vm.CurrentColumns];
                if (!keys.Remove(key))
                {
                    keys.Add(key); // new columns append at the end
                }

                await vm.SetColumnsAsync(keys);
                item.IsChecked = vm.CurrentColumns.Contains(key);
            };
            ColumnsMenu.Items.Add(item);
        }
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private long[] SelectedTrackIds()
        => Grid.SelectedItems is { } items
            ? [.. items.OfType<TrackRow>().OrderBy(r => r.Index).Select(r => r.Id)]
            : [];

    // ---- playback ------------------------------------------------------

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && Grid.SelectedIndex >= 0)
        {
            await vm.PlayRowAsync(Grid.SelectedIndex);
        }
    }

    private async void OnPlaySelectedClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && Grid.SelectedIndex >= 0)
        {
            await vm.PlayRowAsync(Grid.SelectedIndex);
        }
    }

    // ---- library scan --------------------------------------------------

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Add music folder",
                AllowMultiple = false,
            });
        if (folders.Count == 1 && folders[0].TryGetLocalPath() is { } path)
        {
            await vm.AddFolderAsync(path);
        }
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not null)
        {
            e.DragEffects = DragDropEffects.Copy;
        }
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm && e.DataTransfer.TryGetFiles() is { } files)
        {
            string[] paths = [.. files.Select(f => f.TryGetLocalPath()).OfType<string>()];
            if (paths.Length > 0)
            {
                await vm.AddDroppedPathsAsync(paths);
            }
        }
    }

    // ---- grid → playlist drag & drop -----------------------------------

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Arm row-dragging only for presses that land on an actual row.
        // Without this check, scrollbar-thumb and header drags get hijacked
        // into a drag&drop gesture and the grid stops scrolling.
        bool onRow = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<DataGridRow>()
            .Any() == true;

        _dragOrigin = e.GetPosition(this);
        _dragPress = onRow && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e : null;
    }

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPress is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point delta = e.GetPosition(this) - _dragOrigin;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        long[] ids = SelectedTrackIds();
        if (ids.Length == 0)
        {
            return;
        }

        PointerPressedEventArgs press = _dragPress;
        _dragPress = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(TrackIdsFormat, ids));
        await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Copy);
    }

    private static PlaylistInfo? PlaylistUnder(RoutedEventArgs e)
    {
        var node = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<TreeViewItem>()
            .FirstOrDefault()?
            .DataContext as PlaylistNode;
        return node?.Info;
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        bool ok = e.DataTransfer.Contains(TrackIdsFormat)
            && PlaylistUnder(e) is { Kind: PlaylistKind.Static };
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is { } vm
            && e.DataTransfer.TryGetValue(TrackIdsFormat) is { } ids
            && PlaylistUnder(e) is { Kind: PlaylistKind.Static } playlist)
        {
            e.Handled = true;
            await vm.AddTracksToPlaylistAsync(playlist, ids);
        }
    }

    // ---- playlist management -------------------------------------------

    private async void OnNewPlaylistClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await new NewPlaylistDialog(vm).ShowDialog<bool>(this);
        }
    }

    private async void OnAddToPlaylistClick(object? sender, RoutedEventArgs e)
    {
        // Click bubbles from the generated child item; its DataContext is
        // the target playlist. Clicks on the parent header carry the VM.
        if (ViewModel is { } vm
            && e.Source is MenuItem { DataContext: PlaylistInfo playlist })
        {
            long[] ids = SelectedTrackIds();
            await vm.AddTracksToPlaylistAsync(playlist, ids);
        }
    }

    private async void OnSaveSearchAsPlaylistClick(object? sender, RoutedEventArgs e)
    {
        // Promote the query-bar DSL to a saved smart playlist (§7.3).
        if (ViewModel is { CanPromoteSearch: true } vm)
        {
            string query = vm.SearchText.Trim();
            var dialog = new InputDialog(
                "Save as Smart Playlist", "Playlist name", string.Empty,
                name => vm.CreatePlaylistAsync(name, query));
            await dialog.ShowDialog<string?>(this);
        }
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnRescanClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.RescanLibraryAsync();
        }
    }

    private async void OnRgScanClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.ScanReplayGainAsync();
        }
    }

    private async void OnPreferencesClick(object? sender, RoutedEventArgs e)
    {
        if (App.Settings is { } settings)
        {
            await new PreferencesDialog(settings).ShowDialog<bool>(this);
        }
    }

    private async void OnNewFolderClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            var dialog = new InputDialog(
                "New Folder", "Folder name", string.Empty, vm.CreateFolderAsync);
            await dialog.ShowDialog<string?>(this);
        }
    }

    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || PlaylistUnder(e) is not { } playlist)
        {
            return;
        }

        e.Handled = true;
        var menu = new ContextMenu();

        var rename = new MenuItem { Header = "Rename…" };
        rename.Click += async (_, _) =>
        {
            var dialog = new InputDialog(
                "Rename", "New name", playlist.Name,
                value => vm.RenamePlaylistAsync(playlist, value));
            await dialog.ShowDialog<string?>(this);
        };
        menu.Items.Add(rename);

        if (playlist.Kind == PlaylistKind.Smart)
        {
            var editQuery = new MenuItem { Header = "Edit Query…" };
            editQuery.Click += async (_, _) =>
            {
                var editor = new SmartPlaylistEditorDialog(
                    playlist.QueryText, vm.CountQueryMatchesAsync);
                if (await editor.ShowDialog<string?>(this) is { } dsl
                    && await vm.UpdateSmartQueryAsync(playlist, dsl) is { } error)
                {
                    // The editor validated parse/compile; what lands here is
                    // store-side (e.g. a playlist-reference cycle).
                    vm.StatusText = error;
                }
            };
            menu.Items.Add(editQuery);
        }

        if (playlist.Kind != PlaylistKind.Folder)
        {
            var export = new MenuItem { Header = "Export as .m3u8…" };
            export.Click += async (_, _) => await ExportPlaylistAsync(vm, playlist);
            menu.Items.Add(export);
        }

        var moveTo = new MenuItem { Header = "Move to" };
        var topLevel = new MenuItem { Header = "(Top level)" };
        topLevel.Click += async (_, _) => await vm.MovePlaylistAsync(playlist, null);
        moveTo.Items.Add(topLevel);
        foreach (Library.Playlists.PlaylistInfo folder in vm.Folders)
        {
            if (folder.Id == playlist.Id)
            {
                continue;
            }

            var item = new MenuItem { Header = folder.Name };
            long target = folder.Id;
            item.Click += async (_, _) => await vm.MovePlaylistAsync(playlist, target);
            moveTo.Items.Add(item);
        }

        menu.Items.Add(moveTo);
        menu.Items.Add(new Separator());

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += async (_, _) => await vm.DeletePlaylistAsync(playlist);
        menu.Items.Add(delete);

        menu.Open(PlaylistTreeView);
    }

    private async Task ExportPlaylistAsync(MainViewModel vm, PlaylistInfo playlist)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export playlist",
            SuggestedFileName = playlist.Name + ".m3u8",
            FileTypeChoices =
            [
                new FilePickerFileType("M3U8 playlist") { Patterns = ["*.m3u8"] },
            ],
        });
        if (file?.TryGetLocalPath() is { } path)
        {
            await vm.ExportPlaylistAsync(playlist, path);
        }
    }
}
