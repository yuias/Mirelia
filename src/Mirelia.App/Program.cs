using Avalonia;
using Avalonia.Media;
using Mirelia.App.Services;
using Mirelia.Library;

namespace Mirelia.App;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new FontManagerOptions
            {
                // CJK titles must not render as tofu; Avalonia's default
                // fallback chain does not always reach a CJK face.
                FontFallbacks =
                [
                    new FontFallback { FontFamily = new FontFamily("Yu Gothic UI") },
                    new FontFallback { FontFamily = new FontFamily("Meiryo") },
                ],
            })
            .LogToTrace();

    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything else, so a failure during Avalonia setup is logged
        // too rather than ending the process silently.
        CrashHandler.Install();

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLog.Error("startup failed", ex);
            throw;
        }
    }
}
