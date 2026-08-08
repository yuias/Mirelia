using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

    /// <summary>Initializes the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

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
                RebuildColumns(vm.CurrentColumns);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(MainViewModel.CurrentColumns))
                    {
                        RebuildColumns(vm.CurrentColumns);
                    }
                };
            }
        };
        Grid.ColumnReordered += async (_, _) => await PersistColumnOrderAsync();
    }

    private bool _rebuildingColumns;

    private void RebuildColumns(IReadOnlyList<string> keys)
    {
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

                Grid.Columns.Add(new DataGridTextColumn
                {
                    Header = spec.Header,
                    Binding = new Avalonia.Data.Binding(spec.Property),
                    Width = spec.IsStar
                        ? new DataGridLength(spec.Width, DataGridLengthUnitType.Star)
                        : new DataGridLength(spec.Width),
                    Tag = spec.Key,
                });
            }
        }
        finally
        {
            _rebuildingColumns = false;
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
