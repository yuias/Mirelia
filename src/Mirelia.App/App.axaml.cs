using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            _library = new LibraryService(LibraryPaths.DefaultDatabasePath);
            Settings = new SettingsService(
                Path.Combine(
                    Path.GetDirectoryName(Path.GetFullPath(LibraryPaths.DefaultDatabasePath))!,
                    "settings.json"));
            _player = new AudioPlayerService(_library, Settings);
            var viewModel = new MainViewModel(_library, _player);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) =>
            {
                _player.Dispose();
                _library.Dispose();
            };

            _ = viewModel.InitializeAsync();

            // Headless-ish smoke run for CI and scripted checks: open, render,
            // pump the virtualized grid briefly, then exit cleanly.
            if (desktop.Args?.Contains("--smoke") == true)
            {
                DispatcherTimer.RunOnce(
                    () =>
                    {
                        // Virtualization health check: a full-list enumeration
                        // would have hydrated every page, not a handful.
                        Console.WriteLine(
                            $"smoke: tracks={viewModel.Tracks?.Count ?? 0}, " +
                            $"pages_hydrated={viewModel.Tracks?.PagesRequested ?? 0}");
                        desktop.Shutdown();
                    },
                    TimeSpan.FromSeconds(2));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
