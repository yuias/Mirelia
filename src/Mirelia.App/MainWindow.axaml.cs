using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
using Mirelia.App.Services;
using Mirelia.App.ViewModels;
using Mirelia.Library;
using Mirelia.Library.Playlists;

namespace Mirelia.App;

/// <summary>Main shell window; UI-only glue lives here, state in the view-model.</summary>
public partial class MainWindow : Window
{
    private static readonly DataFormat<long[]> TrackIdsFormat =
        DataFormat.CreateInProcessFormat<long[]>("mirelia/track-ids");

    // In-process formats require a reference type; a single-element array
    // carries the one entry id being dragged (mirrors TrackIdsFormat).
    private static readonly DataFormat<long[]> QueueEntryIdFormat =
        DataFormat.CreateInProcessFormat<long[]>("mirelia/queue-entry-id");

    // 6% gray banding reads correctly on both light and dark themes.
    private static readonly Avalonia.Media.IBrush BandingBrush =
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x0F, 0x80, 0x80, 0x80));

    private Point _dragOrigin;
    private PointerPressedEventArgs? _dragPress;

    // Row under the initial press, and its index once a reorder-eligible
    // drag is armed (null otherwise, including once a drop consumes it).
    private TrackRow? _dragSourceRow;
    private int? _dragSourceIndex;

    private Point _queueDragOrigin;
    private PointerPressedEventArgs? _queueDragPress;
    private long? _queueDragEntryId;

    // Entry the queue pane last auto-scrolled to. Refreshes rebuild the row
    // objects, so this keys off the entry id to scroll only when playback
    // actually moves — a manual reorder must not yank the scroll position.
    private long? _autoScrolledEntryId;

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

    // The grid's columns lag the view-model during a view switch (settings
    // load asynchronously), so the scope whose widths the grid currently
    // shows is tracked separately: captures are taken only while grid and
    // view-model agree on the scope, and a capture still pending when the
    // scope flips is flushed to the scope it was taken under — never the
    // one just navigated to.
    private string? _gridScope;
    private IReadOnlyDictionary<string, double> _gridStoredWidths = new Dictionary<string, double>();
    private Dictionary<string, double>? _pendingWidths;

    // The queue pane's column collapses to zero width while the pane is
    // hidden, so the width the user dragged it to is remembered here to be
    // restored on reopen and persisted at exit.
    private double _queuePaneWidth;

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
        RestorePaneWidths();

        ApplyFontScaledRowHeight(App.Settings?.Current.FontSize ?? BaseFontSize);
        if (App.Settings is { } settings)
        {
            settings.Changed += s => ApplyFontScaledRowHeight(s.FontSize);
        }

        _columnWidthDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _columnWidthDebounce.Tick += async (_, _) => await FlushPendingWidthsAsync();
        Grid.LayoutUpdated += (_, _) =>
        {
            // No capture while the grid does not represent the view-model's
            // scope (mid view-switch); OnViewSettingsApplied resumes it.
            if (_rebuildingColumns || ViewModel is not { } vm || _gridScope != vm.CurrentScopeKey)
            {
                return;
            }

            _pendingWidths = CaptureColumnWidths();
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

        // The grid is also a drop target on itself: a static playlist in
        // "Playlist Order" accepts its own drag-selected row back to
        // reorder it (see OnGridPointerMoved for the gate).
        Grid.AddHandler(DragDrop.DragOverEvent, OnGridDragOver);
        Grid.AddHandler(DragDrop.DropEvent, OnGridDrop);

        // Queue pane: drag source and drop target are the same list, for
        // in-place reordering.
        QueueList.AddHandler(PointerPressedEvent, OnQueuePointerPressed, RoutingStrategies.Tunnel);
        QueueList.AddHandler(PointerMovedEvent, OnQueuePointerMoved, RoutingStrategies.Tunnel);
        QueueList.AddHandler(DragDrop.DragOverEvent, OnQueueDragOver);
        QueueList.AddHandler(DragDrop.DropEvent, OnQueueDrop);

        // Tree context menus are built in code: the "Move to" submenu is
        // per-node and data-driven, which XAML templates handle poorly.
        PlaylistTreeView.AddHandler(ContextRequestedEvent, OnTreeContextRequested);

        UpdateLibraryTitle();

        // Columns are data-driven from per-view settings.
        DataContextChanged += (_, _) =>
        {
            if (ViewModel is { } vm)
            {
                RebuildColumns();
                vm.ViewSettingsApplied += OnViewSettingsApplied;
                ApplyQueuePaneWidth();
                vm.Queue.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(QueueViewModel.CurrentRow))
                    {
                        ScrollQueueToCurrent(onlyIfNewEntry: true);
                    }
                    else if (args.PropertyName == nameof(QueueViewModel.IsOpen))
                    {
                        CaptureQueuePaneWidth();
                        ApplyQueuePaneWidth();
                        if (vm.Queue.IsOpen)
                        {
                            ScrollQueueToCurrent(onlyIfNewEntry: false);
                        }
                    }
                };
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

    // ---- pane widths -----------------------------------------------------

    private const double MinPaneWidth = 150;

    // x:Name on a ColumnDefinition generates no field, so the two resizable
    // pane columns are reached by index (see the note in MainWindow.axaml).
    private ColumnDefinition LeftPaneColumn => PanesGrid.ColumnDefinitions[0];

    private ColumnDefinition QueuePaneColumn => PanesGrid.ColumnDefinitions[4];

    /// <summary>Applies the saved pane widths over the XAML defaults.</summary>
    private void RestorePaneWidths()
    {
        AppSettings settings = App.Settings?.Current ?? new AppSettings();
        LeftPaneColumn.Width = new GridLength(Math.Max(settings.LeftPaneWidth, MinPaneWidth));
        _queuePaneWidth = Math.Max(settings.QueuePaneWidth, MinPaneWidth);
    }

    /// <summary>
    /// Gives the queue column its width while the pane is shown and collapses
    /// it to nothing while it is hidden — a hidden pane must not leave a gap.
    /// The minimum goes with it, since a floored column cannot collapse.
    /// </summary>
    private void ApplyQueuePaneWidth()
    {
        bool open = ViewModel?.Queue.IsOpen ?? false;
        QueuePaneColumn.MinWidth = open ? MinPaneWidth : 0;
        QueuePaneColumn.Width = new GridLength(open ? _queuePaneWidth : 0);
    }

    // Remembers the current queue-pane width, unless the pane is collapsed
    // (its column is zero then, which is not a width worth keeping).
    private void CaptureQueuePaneWidth()
    {
        if (QueuePaneColumn.Width is { IsAbsolute: true, Value: > 0 } width)
        {
            _queuePaneWidth = width.Value;
        }
    }

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
        _ = FlushPendingWidthsAsync();

        if (App.Settings is not { } settings)
        {
            return;
        }

        // A volume change still inside its debounce window would otherwise be
        // overwritten by the geometry save below with the stale level.
        ViewModel?.Transport.FlushVolume();
        CaptureQueuePaneWidth();

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
            LeftPaneWidth = LeftPaneColumn.Width.IsAbsolute
                ? LeftPaneColumn.Width.Value
                : settings.Current.LeftPaneWidth,
            QueuePaneWidth = _queuePaneWidth,
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

    /// <summary>
    /// The rating cell template, exposed so the grouped layout's row template
    /// can reuse it instead of growing a second star cell that drifts from
    /// this one.
    /// </summary>
    public static IDataTemplate RatingCell => RatingCellTemplate;

    // Unrated cells render as empty text, so the glyphs carry a fixed width
    // to keep both the hover target and the column alignment.
    private const double RatingStarWidth = 14;

    private static IDataTemplate BuildRatingCellTemplate()
    {
        return new FuncDataTemplate<TrackRow>((_, _) =>
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,

                // A panel without a background is not hit-testable, and the
                // cell's hover state is what reveals an unrated row's stars.
                Background = Avalonia.Media.Brushes.Transparent,
            };
            for (int star = 1; star <= 5; star++)
            {
                var glyph = new TextBlock
                {
                    FontSize = 13,
                    Width = RatingStarWidth,
                    TextAlignment = Avalonia.Media.TextAlignment.Center,
                    Tag = star,
                    Cursor = new Cursor(StandardCursorType.Hand),
                };

                // The cell's own hover state is a second input: it is what
                // reveals the stars of an unrated row (see the converter).
                var text = new MultiBinding
                {
                    Converter = RatingStarConverter.Instance,
                    ConverterParameter = star,
                    Bindings =
                    {
                        new Binding(nameof(TrackRow.Rating)),
                        new Binding(nameof(InputElement.IsPointerOver)) { Source = panel },
                    },
                };
                glyph.Bind(TextBlock.TextProperty, text);
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
    /// A group block was realized: hydrate its rows. This is the grouped
    /// layout's counterpart to the grid's LoadingRow hook — recycled
    /// containers get a fresh block here, which an attach event would miss.
    /// </summary>
    private void OnBlockPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (ViewModel?.GroupedTracks is { } blocks && e.Index < blocks.Count)
        {
            blocks[e.Index].NotifyVisible();
        }
    }

    /// <summary>
    /// A block scrolled out and its container is being recycled: free the
    /// cover. Skia's pixel buffers are native memory the GC cannot size, so
    /// without this every block ever shown keeps its pixels for the life of
    /// the view.
    /// </summary>
    private void OnBlockClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container.DataContext is TrackGroupBlock block)
        {
            block.ReleaseCover();
        }
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

    // A user resize turns a column's Width into a pixel DataGridLength, so
    // still-star columns are ones the user never touched: skip them,
    // otherwise the first settle would pin them at the current window width
    // and they would stop stretching.
    private Dictionary<string, double> CaptureColumnWidths()
        => Grid.Columns
            .Where(c => c.Tag is string && c.ActualWidth > 0
                && c.Width.UnitType != DataGridLengthUnitType.Star)
            .ToDictionary(c => (string)c.Tag!, c => c.ActualWidth);

    /// <summary>
    /// Persists the pending width capture under the scope it was taken for,
    /// merged over that scope's stored widths so columns hidden from the
    /// current view keep their saved width.
    /// </summary>
    private async Task FlushPendingWidthsAsync()
    {
        _columnWidthDebounce.Stop();
        if (_pendingWidths is not { } pending)
        {
            return;
        }

        _pendingWidths = null;
        if (_gridScope is not { } scope || ViewModel is not { } vm)
        {
            return;
        }

        var merged = new Dictionary<string, double>(_gridStoredWidths);
        foreach ((string key, double width) in pending)
        {
            merged[key] = width;
        }

        if (!WidthsEqual(merged, _gridStoredWidths))
        {
            _gridStoredWidths = merged;
            await vm.SaveColumnWidthsAsync(scope, merged);
        }
    }

    /// <summary>
    /// Re-syncs grid column state after a view's settings were applied:
    /// flushes any capture still owed to the previous view, rebuilds or
    /// re-widths the columns, and re-arms capture for the new scope.
    /// </summary>
    private async void OnViewSettingsApplied()
    {
        await FlushPendingWidthsAsync();
        RebuildColumns();
        ApplyColumnWidths();
        if (ViewModel is { } vm)
        {
            _gridScope = vm.CurrentScopeKey;
            _gridStoredWidths = vm.CurrentColumnWidths;
        }
    }

    /// <summary>
    /// Applies the view-model's widths to the existing columns without a
    /// rebuild: saved widths pin columns to pixels, unsaved columns return
    /// to registry sizing. Needed on view switches where the column set is
    /// unchanged and <see cref="RebuildColumns"/> short-circuits.
    /// </summary>
    private void ApplyColumnWidths()
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        IReadOnlyDictionary<string, double> widths = vm.CurrentColumnWidths;
        _rebuildingColumns = true;
        try
        {
            foreach (DataGridColumn column in Grid.Columns)
            {
                if (column.Tag is not string key || Models.TrackColumns.Find(key) is not { } spec)
                {
                    continue;
                }

                bool resized = widths.TryGetValue(key, out double saved);
                DataGridLength target = resized || !spec.IsStar
                    ? new DataGridLength(resized ? saved : spec.Width)
                    : new DataGridLength(spec.Width, DataGridLengthUnitType.Star);
                if (column.Width.UnitType != target.UnitType
                    || Math.Abs(column.Width.Value - target.Value) > 0.5)
                {
                    column.Width = target;
                }
            }
        }
        finally
        {
            _rebuildingColumns = false;
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

        // "Default" is the Music view's state (the built-in registry for
        // the Music view itself); resetting clears this view's saved
        // columns and widths so it falls back to that. Only this view —
        // View ▸ Columns has the all-views reset.
        ColumnsMenu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset This View to Default" };
        reset.Click += async (_, _) =>
        {
            DiscardPendingWidths();
            await vm.ResetColumnsToDefaultAsync();
        };
        ColumnsMenu.Items.Add(reset);
    }

    private async void OnClearCoverCacheClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.DiscardCoverCacheAsync();
        }
    }

    private async void OnResetAllColumnsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            DiscardPendingWidths();
            await vm.ResetAllColumnsToDefaultAsync();
        }
    }

    private async void OnApplyMusicColumnsClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            DiscardPendingWidths();
            await vm.ApplyMusicColumnsEverywhereAsync();
        }
    }

    // A capture still pending from a just-made resize would be re-persisted
    // right after a reset; the reset supersedes it.
    private void DiscardPendingWidths()
    {
        _pendingWidths = null;
        _columnWidthDebounce.Stop();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    // Both layouts answer "what is selected" through these two, so the
    // context-menu handlers below work in either without knowing which is up.
    private long[] SelectedTrackIds()
        => ViewModel is { IsFlatLayout: false } grouped
            ? grouped.BlockSelectedIds
            : Grid.SelectedItems is { } items
                ? [.. items.OfType<TrackRow>().OrderBy(r => r.Index).Select(r => r.Id)]
                : [];

    private int SelectedRowIndex()
        => ViewModel is { IsFlatLayout: false } grouped
            ? grouped.BlockSelectedIndex
            : Grid.SelectedIndex;

    // ---- playback ------------------------------------------------------

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        // A double-click on a column header is never a "play" gesture; on a
        // resize edge it auto-fits the adjacent column instead.
        if (e.Source is Visual source
            && source.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is { } header)
        {
            e.Handled = true;
            AutoFitColumnFromHeaderEdge(header, e.GetPosition(header).X);
            return;
        }

        if (ViewModel is { } vm && Grid.SelectedIndex >= 0)
        {
            await vm.PlayRowAsync(Grid.SelectedIndex);
        }
    }

    /// <summary>
    /// Explorer-style auto-fit: a double-click near a header's right edge
    /// (or the left edge, meaning the previous column's right edge) sizes
    /// that column to its realized content and pins the result as a pixel
    /// width, which the normal capture path then persists.
    /// </summary>
    private void AutoFitColumnFromHeaderEdge(DataGridColumnHeader header, double x)
    {
        // Match the header's resize hot zone so the same spot that starts a
        // resize drag also accepts the double-click.
        const double edgeSlop = 6;

        var ordered = Grid.Columns.OrderBy(c => c.DisplayIndex).ToList();
        int index = ordered.FindIndex(c => Equals(c.Header, header.Content));
        DataGridColumn? column = index < 0
            ? null
            : x >= header.Bounds.Width - edgeSlop
                ? ordered[index]
                : x <= edgeSlop && index > 0
                    ? ordered[index - 1]
                    : null;
        if (column is null)
        {
            return;
        }

        // Let Auto sizing measure the realized cells, then freeze the result
        // as a pixel width so virtualization cannot re-grow it later.
        column.Width = DataGridLength.Auto;
        Grid.UpdateLayout();
        column.Width = new DataGridLength(Math.Max(column.ActualWidth, 24));
    }

    private async void OnPlaySelectedClick(object? sender, RoutedEventArgs e)
    {
        int index = SelectedRowIndex();
        if (ViewModel is { } vm && index >= 0)
        {
            await vm.PlayRowAsync(index);
        }
    }

    // ---- grouped layout --------------------------------------------------

    private void OnBlockRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is not { } vm || sender is not Control { DataContext: TrackRow row } control)
        {
            return;
        }

        // A right-click inside an existing selection opens the menu for all
        // of it rather than collapsing it to the row under the pointer.
        if (e.GetCurrentPoint(control).Properties.IsRightButtonPressed
            && vm.IsBlockRowSelected(row.Index))
        {
            return;
        }

        vm.SelectBlockRow(
            row.Index,
            e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    private async void OnBlockRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Control { DataContext: TrackRow row })
        {
            e.Handled = true;
            await vm.PlayRowAsync(row.Index);
        }
    }

    private void OnBlockHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Control { DataContext: TrackGroupBlock block })
        {
            vm.SelectBlockRange(block.StartIndex, block.Count);
        }
    }

    private async void OnBlockHeaderDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && sender is Control { DataContext: TrackGroupBlock block })
        {
            e.Handled = true;
            await vm.PlayRowAsync(block.StartIndex);
        }
    }

    private async void OnPlayNextClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.PlayNextAsync(SelectedTrackIds());
        }
    }

    private async void OnAddToQueueClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.AddToQueueAsync(SelectedTrackIds());
        }
    }

    // ---- queue pane ------------------------------------------------------

    private async void OnClearQueueClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await vm.Queue.ClearAsync();
        }
    }

    /// <summary>
    /// Keeps the audible entry visible in the queue pane. With
    /// <paramref name="onlyIfNewEntry"/>, refreshes that merely rebuild the
    /// rows (reorder, remove) do not yank the user's scroll position.
    /// </summary>
    private void ScrollQueueToCurrent(bool onlyIfNewEntry)
    {
        if (ViewModel?.Queue is not { IsOpen: true, CurrentRow: { } row })
        {
            return;
        }

        if (onlyIfNewEntry && _autoScrolledEntryId == row.EntryId)
        {
            return;
        }

        _autoScrolledEntryId = row.EntryId;

        // The rows were only just added (or the pane only just shown), so the
        // list has no realized containers to scroll to yet — scrolling now is
        // silently dropped when the pane was empty beforehand. Background
        // priority runs after the pending layout pass.
        Dispatcher.UIThread.Post(
            () =>
            {
                // Playback may have moved on again while this was queued.
                if (ViewModel?.Queue is { IsOpen: true, CurrentRow: { } target }
                    && target.EntryId == row.EntryId)
                {
                    QueueList.ScrollIntoView(target);
                }
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// A queue row was realized: hydrate its page. The queue pane virtualizes
    /// the same way the track grid does, because a queue is as long as the
    /// view it was started from.
    /// </summary>
    private void OnQueueRowPrepared(object? sender, ContainerPreparedEventArgs e)
        => ViewModel?.Queue.Rows?.NotifyVisible(e.Index);

    private async void OnQueueDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && QueueList.SelectedItem is QueueRowViewModel row)
        {
            await vm.Queue.PlayAsync(row.EntryId);
        }
    }

    private async void OnQueuePlayClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm && QueueList.SelectedItem is QueueRowViewModel row)
        {
            await vm.Queue.PlayAsync(row.EntryId);
        }
    }

    private async void OnQueueRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || QueueList.SelectedItems is not { } items)
        {
            return;
        }

        foreach (QueueRowViewModel row in items.OfType<QueueRowViewModel>().ToList())
        {
            await vm.Queue.RemoveAsync(row.EntryId);
        }
    }

    private async void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || ViewModel is not { } vm || QueueList.SelectedItems is not { } items)
        {
            return;
        }

        e.Handled = true;
        foreach (QueueRowViewModel row in items.OfType<QueueRowViewModel>().ToList())
        {
            await vm.Queue.RemoveAsync(row.EntryId);
        }
    }

    // Drag source: the row under the initial press, identified by its
    // stable entry id (not its position, which is about to move).
    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        QueueRowViewModel? row = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>()
            .FirstOrDefault()?
            .DataContext as QueueRowViewModel;

        _queueDragOrigin = e.GetPosition(this);
        _queueDragEntryId = row?.EntryId;
        _queueDragPress = row is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? e
            : null;
    }

    private async void OnQueuePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_queueDragPress is null || _queueDragEntryId is not { } entryId
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point delta = e.GetPosition(this) - _queueDragOrigin;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
        {
            return;
        }

        PointerPressedEventArgs press = _queueDragPress;
        _queueDragPress = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(QueueEntryIdFormat, [entryId]));
        await DragDrop.DoDragDropAsync(press, transfer, DragDropEffects.Move);
    }

    private void OnQueueDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(QueueEntryIdFormat)
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnQueueDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm
            || e.DataTransfer.TryGetValue(QueueEntryIdFormat) is not { Length: 1 } ids)
        {
            return;
        }

        e.Handled = true;
        await vm.Queue.MoveAsync(ids[0], QueueDropIndex(e, vm));
    }

    // Drops on the top half of a row insert before it, the bottom half
    // insert after; drops past the last row (or on empty space) append.
    private int QueueDropIndex(DragEventArgs e, MainViewModel vm)
    {
        var item = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>()
            .FirstOrDefault();
        if (item?.DataContext is not QueueRowViewModel row)
        {
            return (vm.Queue.Rows?.Count ?? 0) - 1;
        }

        bool before = e.GetPosition(item).Y < item.Bounds.Height / 2;
        return before ? row.Index : row.Index + 1;
    }

    // ---- library scan --------------------------------------------------

    private async void OnOpenLibraryClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open library",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Mirelia library") { Patterns = ["*.db"] },
                ],
            });
        if (files.Count != 1 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }

        if (await vm.OpenLibraryAsync(path) is { } problem)
        {
            vm.StatusText = problem;
            return;
        }

        UpdateLibraryTitle();
        if (App.Settings is { } settings)
        {
            settings.Save(settings.Current with { LibraryPath = path });
        }
    }

    /// <summary>
    /// Names the open library in the title bar. The default library stays
    /// anonymous — its name only matters once there is more than one.
    /// </summary>
    private void UpdateLibraryTitle()
    {
        string? path = App.Library?.DatabasePath;
        Title = path is null
            || string.Equals(
                Path.GetFullPath(path),
                Path.GetFullPath(LibraryPaths.DefaultDatabasePath),
                StringComparison.OrdinalIgnoreCase)
            ? "Mirelia"
            : $"Mirelia — {Path.GetFileNameWithoutExtension(path)}";
    }

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
        _dragSourceRow = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<DataGridRow>()
            .FirstOrDefault()?
            .DataContext as TrackRow;

        _dragOrigin = e.GetPosition(this);
        _dragPress = _dragSourceRow is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            ? e
            : null;
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

        // Reordering only ever applies to a lone dragged row (queue-pane
        // parity, kept simple for v1), in a view where grid rows are
        // playlist positions, and only when the press actually landed on
        // the row that ended up selected — guards against a stale
        // _dragSourceRow if selection changed without a fresh press.
        bool reorderEligible = ids.Length == 1
            && ViewModel is { CanReorderStaticPlaylist: true }
            && _dragSourceRow?.Id == ids[0];
        _dragSourceIndex = reorderEligible ? _dragSourceRow!.Index : null;

        PointerPressedEventArgs press = _dragPress;
        _dragPress = null;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(TrackIdsFormat, ids));
        DragDropEffects allowed = DragDropEffects.Copy
            | (reorderEligible ? DragDropEffects.Move : DragDropEffects.None);
        await DragDrop.DoDragDropAsync(press, transfer, allowed);
    }

    private void OnGridDragOver(object? sender, DragEventArgs e)
    {
        bool ok = _dragSourceIndex is not null
            && ViewModel is { CanReorderStaticPlaylist: true }
            && e.DataTransfer.Contains(TrackIdsFormat);
        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnGridDrop(object? sender, DragEventArgs e)
    {
        if (ViewModel is not { } vm
            || _dragSourceIndex is not { } from
            || !vm.CanReorderStaticPlaylist
            || vm.Tracks is not { } tracks
            || !e.DataTransfer.Contains(TrackIdsFormat))
        {
            return;
        }

        e.Handled = true;
        _dragSourceIndex = null;

        int to = GridDropIndex(e, from, tracks.Count);
        if (to == from)
        {
            return; // dropped back where it started
        }

        if (await vm.MoveStaticPlaylistEntryAsync(from, to))
        {
            Grid.SelectedIndex = to;
        }
    }

    // Drops on the top half of a row insert before it, the bottom half
    // insert after; drops past the last row (or on empty space) append.
    // The result is converted from that pre-drag position to
    // PlaylistStore.Move's post-removal index convention — the same
    // adjustment PlayQueue.Move applies internally for the queue pane.
    private static int GridDropIndex(DragEventArgs e, int from, int rowCount)
    {
        var row = (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<DataGridRow>()
            .FirstOrDefault();
        int preDrag = row?.DataContext is TrackRow track
            ? (e.GetPosition(row).Y < row.Bounds.Height / 2 ? track.Index : track.Index + 1)
            : rowCount;

        return preDrag > from ? preDrag - 1 : preDrag;
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

    private async void OnPinSearchAsTemporaryClick(object? sender, RoutedEventArgs e)
    {
        // Pin the query-bar DSL as an in-memory playlist that lives until
        // exit — the ephemeral counterpart of OnSaveSearchAsPlaylistClick.
        if (ViewModel is { CanPromoteSearch: true } vm)
        {
            string query = vm.SearchText.Trim();
            var dialog = new InputDialog(
                "Pin as Temporary Playlist", "Playlist name", string.Empty,
                name =>
                {
                    vm.PinSearchAsTemporaryPlaylist(name, query);
                    return Task.FromResult<string?>(null);
                });
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

    private async void OnSyncClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            await new SyncDialog(vm.Library).ShowDialog(this);
        }
    }

    private async void OnPreferencesClick(object? sender, RoutedEventArgs e)
    {
        if (App.Settings is { } settings)
        {
            await new PreferencesDialog(settings).ShowDialog<bool>(this);
        }
    }

    private async void OnAboutClick(object? sender, RoutedEventArgs e)
    {
        await new AboutDialog().ShowDialog(this);
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

        if (playlist.Id < 0)
        {
            // Ephemeral search playlists have no store row: no rename, edit,
            // move, or export — the only action is dropping it from memory.
            var remove = new MenuItem { Header = "Remove" };
            remove.Click += async (_, _) => await vm.RemoveEphemeralPlaylistAsync(playlist);
            menu.Items.Add(remove);
            menu.Open(PlaylistTreeView);
            return;
        }

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
