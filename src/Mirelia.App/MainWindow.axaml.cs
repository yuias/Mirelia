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
        PlaylistList.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        PlaylistList.AddHandler(DragDrop.DropEvent, OnPlaylistDrop);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
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
        _dragOrigin = e.GetPosition(this);
        _dragPress = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? e : null;
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

    private static PlaylistInfo? PlaylistUnder(DragEventArgs e)
    {
        return (e.Source as Visual)?
            .GetSelfAndVisualAncestors()
            .OfType<ListBoxItem>()
            .FirstOrDefault()?
            .DataContext as PlaylistInfo;
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

    private async void OnRenamePlaylist(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm
            || (sender as MenuItem)?.DataContext is not PlaylistInfo playlist)
        {
            return;
        }

        var dialog = new InputDialog(
            "Rename Playlist",
            "New name",
            playlist.Name,
            value => vm.RenamePlaylistAsync(playlist, value));
        await dialog.ShowDialog<string?>(this);
    }

    private async void OnDeletePlaylist(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is { } vm
            && (sender as MenuItem)?.DataContext is PlaylistInfo playlist)
        {
            await vm.DeletePlaylistAsync(playlist);
        }
    }

    private async void OnExportPlaylist(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm
            || (sender as MenuItem)?.DataContext is not PlaylistInfo playlist)
        {
            return;
        }

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
