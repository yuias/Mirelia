using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Mirelia.App.Services;
using Mirelia.App.ViewModels;
using Mirelia.Library;

namespace Mirelia.App;

/// <summary>Application entry: theme, composition root, main window.</summary>
public class App : Application
{
    private LibraryService? _library;
    private AudioPlayerService? _player;

    /// <summary>The settings service, available app-wide after startup.</summary>
    public static SettingsService? Settings { get; private set; }

    /// <summary>
    /// The library service, available app-wide after startup. Lets code-behind
    /// (e.g. the grid's rating cell) issue direct writes without routing
    /// through the view-model.
    /// </summary>
    public static LibraryService? Library { get; private set; }

    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Settings = new SettingsService(LibraryPaths.SettingsFilePath);

            // Reopen the library last chosen with File ▸ Open Library…, unless
            // it has since gone — a missing file would otherwise be recreated
            // empty and read as a lost library. MIRELIA_DB still outranks it,
            // so the smoke run and tests are never pointed at a saved library.
            string libraryPath = LibraryPaths.DatabaseOverride
                ?? (Settings.Current.LibraryPath is { } saved && File.Exists(saved)
                    ? saved
                    : LibraryPaths.DefaultDatabasePath);
            _library = new LibraryService(libraryPath);
            Library = _library;

            // Applied before the main window is created so there is no
            // flash of the wrong theme; re-applied on every settings save
            // so Preferences changes take effect immediately.
            RequestedThemeVariant = ThemeVariantFor(Settings.Current.Theme);
            Settings.Changed += s => RequestedThemeVariant = ThemeVariantFor(s.Theme);

            // AppFontFamily/AppFontSize back the Window/DataGrid styles
            // above; re-set on save so a font change also applies without
            // restart (DynamicResource observers re-evaluate immediately).
            ApplyFontResources(Settings.Current);
            Settings.Changed += s => ApplyFontResources(s);

            _player = new AudioPlayerService(_library, Settings);
            var viewModel = new MainViewModel(_library, _player, Settings);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                _player.Dispose();
                _library.Dispose();
            };

            _ = viewModel.InitializeAsync();

            // Headless-ish smoke run for CI and scripted checks: open, render,
            // pump each layout's virtualization briefly, then exit cleanly.
            if (desktop.Args?.Contains("--smoke") == true)
            {
                _ = SmokeAsync(desktop, viewModel);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Exercises every layout's virtualization and reports how much of the
    /// view each one realized. Both layouts are checked because they realize
    /// through entirely different container plumbing: the album layout once
    /// realized every block, and nothing caught it because the smoke run only
    /// ever opened the flat grid.
    /// </summary>
    private static async Task SmokeAsync(
        IClassicDesktopStyleApplicationLifetime desktop, MainViewModel viewModel)
    {
        // Long enough for the view to load and the containers to realize.
        await Task.Delay(TimeSpan.FromSeconds(2));
        bool healthy = Report(viewModel, Models.TrackLayouts.Tracks);

        await Dispatcher.UIThread.InvokeAsync(
            () => viewModel.SelectedLayout = Models.TrackLayouts.Albums);
        await Task.Delay(TimeSpan.FromSeconds(2));
        healthy &= Report(viewModel, Models.TrackLayouts.Albums);

        // The layout is persisted per view, so put it back rather than leaving
        // the library this ran against switched over.
        await Dispatcher.UIThread.InvokeAsync(
            () => viewModel.SelectedLayout = Models.TrackLayouts.Tracks);
        await Task.Delay(TimeSpan.FromSeconds(1));

        // The UI-thread hook marks exceptions handled so a failed click cannot
        // kill the window; without this the smoke run would exit 0 over a
        // crash it had swallowed.
        if (AppLog.ErrorCount > 0)
        {
            Console.WriteLine($"smoke: {AppLog.ErrorCount} error(s) logged, see {AppLog.Path}");
            healthy = false;
        }

        await Dispatcher.UIThread.InvokeAsync(() => desktop.Shutdown(healthy ? 0 : 1));
    }

    // A view is virtualized when it hydrates a handful of pages whatever its
    // size; realizing the whole thing shows up here as one page per 256 rows.
    private const int SmokeMaxPages = 4;

    private static bool Report(MainViewModel viewModel, Models.LayoutSpec layout)
    {
        int tracks = viewModel.Tracks?.Count ?? 0;
        int pages = viewModel.Tracks?.PagesRequested ?? 0;
        bool bigEnoughToJudge = tracks > Models.VirtualTrackList.PageSize * SmokeMaxPages;
        bool healthy = !bigEnoughToJudge || pages <= SmokeMaxPages;

        Console.WriteLine(
            $"smoke: layout={layout.Key} tracks={tracks} pages_hydrated={pages} "
            + $"blocks={viewModel.GroupedTracks?.Count ?? 0} "
            + (healthy ? "ok" : "NOT VIRTUALIZED"));
        return healthy;
    }

    private static ThemeVariant ThemeVariantFor(AppTheme theme) => theme switch
    {
        AppTheme.Light => ThemeVariant.Light,
        AppTheme.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    private void ApplyFontResources(AppSettings settings)
    {
        Resources["AppFontFamily"] = string.IsNullOrEmpty(settings.FontFamily)
            ? FontManager.Current.DefaultFontFamily
            : new FontFamily(settings.FontFamily);
        Resources["AppFontSize"] = settings.FontSize;
    }
}
