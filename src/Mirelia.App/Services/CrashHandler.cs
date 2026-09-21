using System.Reflection;
using Avalonia.Threading;
using Mirelia.Library;

namespace Mirelia.App.Services;

/// <summary>
/// Installs the process-wide exception hooks and opens the log. A WinExe has
/// no console, so without these an unhandled exception ends the process with
/// no visible cause and nothing on disk to report.
/// </summary>
internal static class CrashHandler
{
    private static bool _installed;

    /// <summary>Installs the hooks and opens the log. Idempotent.</summary>
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;

        AppLog.Open(LibraryPaths.LogFilePath, StartupLine());

        // Fatal: the CLR is already tearing the process down, so this only
        // gets the reason on disk before it goes.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Error("unhandled exception", e.ExceptionObject as Exception);

        // Faulted tasks nobody awaited. Observing them keeps the finalizer
        // from escalating to a crash, and the log records what was dropped.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("unobserved task exception", e.Exception);
            e.SetObserved();
        };

        // The one hook that catches the `async void` event handlers: their
        // exceptions are posted back to the dispatcher. Marking them handled
        // keeps a failed click from killing the window.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AppLog.Error("unhandled exception on the UI thread", e.Exception);
            e.Handled = true;
        };
    }

    private static string StartupLine()
    {
        string version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? typeof(CrashHandler).Assembly.GetName().Version?.ToString()
            ?? "unknown";

        return $"Mirelia {version} on {Environment.OSVersion}";
    }
}
