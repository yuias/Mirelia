using Avalonia.Controls;
using Mirelia.AudioBridge;

namespace Mirelia.App;

/// <summary>
/// M0 shell window. The three-pane layout (§7 of the kickoff) lands in M3;
/// for now this only proves the native backend loads inside the UI process.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Initializes the window and probes the native backend.</summary>
    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = ProbeNativeBackend();
    }

    private static string ProbeNativeBackend()
    {
        try
        {
            using var engine = AudioEngine.Create();
            return $"M0 scaffold — audio core loaded, state: {engine.GetPosition().State}";
        }
        catch (Exception ex) when (ex is AudioEngineException or DllNotFoundException)
        {
            return $"M0 scaffold — audio core unavailable: {ex.Message}";
        }
    }
}
