using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Mirelia.App.ViewModels;

namespace Mirelia.App;

/// <summary>Main shell window; UI-only glue lives here, state in the view-model.</summary>
public partial class MainWindow : Window
{
    /// <summary>Initializes the window.</summary>
    public MainWindow()
    {
        InitializeComponent();

        // Visibility-driven hydration: rows realized by the grid pull their
        // page from the database (see VirtualTrackList).
        Grid.LoadingRow += (_, e) =>
        {
            if (e.Row.DataContext is Models.TrackRow row)
            {
                ViewModel?.Tracks?.NotifyVisible(row.Index);
            }
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private async void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ViewModel is { } vm && Grid.SelectedIndex >= 0)
        {
            await vm.PlayRowAsync(Grid.SelectedIndex);
        }
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
}
