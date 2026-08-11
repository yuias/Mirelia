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
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON next to the library
/// database. Unreadable files fall back to defaults rather than blocking
/// startup.
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
