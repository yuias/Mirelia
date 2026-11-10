using System.Text.Json;

namespace Mirelia.App.Services;

/// <summary>ReplayGain application modes (album gain is post-v1).</summary>
public enum ReplayGainMode
{
    /// <summary>No gain applied.</summary>
    Off,

    /// <summary>Apply per-track gain where scanned.</summary>
    Track,
}

/// <summary>How playback behaves when a track ends.</summary>
public enum RepeatMode
{
    /// <summary>Stop once the queue runs out.</summary>
    Off,

    /// <summary>Start the queue again from the top.</summary>
    All,

    /// <summary>Replay the current track indefinitely.</summary>
    One,
}

/// <summary>Color theme preference for the UI.</summary>
public enum AppTheme
{
    /// <summary>Follow the OS setting.</summary>
    Auto,

    /// <summary>Always use the light theme.</summary>
    Light,

    /// <summary>Always use the dark theme.</summary>
    Dark,
}

/// <summary>Persisted user settings.</summary>
public sealed record AppSettings
{
    /// <summary>Output device id, or null for the system default.</summary>
    public string? DeviceId { get; init; }

    /// <summary>Open the device in exclusive (bit-perfect) mode.</summary>
    public bool Exclusive { get; init; }

    /// <summary>How ReplayGain is applied during playback.</summary>
    public ReplayGainMode ReplayGain { get; init; } = ReplayGainMode.Off;

    /// <summary>The UI color theme.</summary>
    public AppTheme Theme { get; init; } = AppTheme.Auto;

    /// <summary>UI font family name, or null for the platform default.</summary>
    public string? FontFamily { get; init; }

    /// <summary>UI font size in points, applied app-wide.</summary>
    public double FontSize { get; init; } = 14;

    /// <summary>Main window X position (device pixels), or null before first save.</summary>
    public int? WindowX { get; init; }

    /// <summary>Main window Y position (device pixels), or null before first save.</summary>
    public int? WindowY { get; init; }

    /// <summary>Main window width (DIPs) in its normal (non-maximized) state, or null before first save.</summary>
    public double? WindowWidth { get; init; }

    /// <summary>Main window height (DIPs) in its normal (non-maximized) state, or null before first save.</summary>
    public double? WindowHeight { get; init; }

    /// <summary>Whether the main window was maximized when it last closed.</summary>
    public bool WindowMaximized { get; init; }

    /// <summary>Playback volume, 0-1 linear.</summary>
    public double Volume { get; init; } = 1.0;

    /// <summary>Whether the queue plays in a shuffled order.</summary>
    public bool Shuffle { get; init; }

    /// <summary>What happens when the queue or the current track ends.</summary>
    public RepeatMode Repeat { get; init; } = RepeatMode.Off;

    /// <summary>Whether the playing-queue pane is shown.</summary>
    public bool QueuePaneOpen { get; init; } = true;

    /// <summary>Width (DIPs) of the sources/playlists pane.</summary>
    public double LeftPaneWidth { get; init; } = 220;

    /// <summary>Width (DIPs) of the playing-queue pane while it is shown.</summary>
    public double QueuePaneWidth { get; init; } = 240;

    /// <summary>
    /// The library file to reopen at startup, or null for the default one.
    /// Set by "Open Library…"; falls back to the default when the file has
    /// since gone.
    /// </summary>
    public string? LibraryPath { get; init; }

    /// <summary>
    /// The sample rates the output can be fixed to, in Hz. Anything else is
    /// treated as "same as source".
    /// </summary>
    public static IReadOnlyList<uint> SupportedOutputSampleRates { get; } =
        [44100, 48000, 88200, 96000, 176400, 192000];

    /// <summary>
    /// Fixed output sample rate in Hz, or null to play each track at its
    /// source rate. Must be one of <see cref="SupportedOutputSampleRates"/>.
    /// </summary>
    public uint? OutputSampleRate { get; init; }

    /// <summary>
    /// Applies TPDF dither to tracks Mirelia resamples, on integer devices in
    /// exclusive mode. Tracks played at their own rate are never dithered.
    /// </summary>
    public bool DitherResampled { get; init; }

    /// <summary>
    /// Id of the resampler plugin used for tracks Mirelia resamples, or null
    /// for the built-in one. A string because the available plugins are not
    /// known at compile time; the engine decides whether the id is valid.
    /// </summary>
    public string? ResamplerPluginId { get; init; }
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON in the app's own data
/// folder — not the open library's — so preferences do not fork per library.
/// Unreadable files fall back to defaults rather than blocking startup.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private readonly string _path;

    /// <summary>Loads settings from the given file (created on first save).</summary>
    public SettingsService(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
            {
                Current = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(path), Options) ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Current = new AppSettings();
        }

        // The file is the one place a rate can arrive that the UI never offered.
        if (Current.OutputSampleRate is { } rate
            && !AppSettings.SupportedOutputSampleRates.Contains(rate))
        {
            Current = Current with { OutputSampleRate = null };
        }
    }

    /// <summary>The active settings.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Raised after <see cref="Save"/> with the new settings.</summary>
    public event Action<AppSettings>? Changed;

    /// <summary>Persists and applies new settings.</summary>
    public void Save(AppSettings settings)
    {
        Current = settings;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
        Changed?.Invoke(settings);
    }
}
