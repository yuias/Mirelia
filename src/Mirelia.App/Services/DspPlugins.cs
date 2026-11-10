using Mirelia.AudioBridge;

namespace Mirelia.App.Services;

/// <summary>Finds and registers DSP plugin libraries with an engine.</summary>
public static class DspPlugins
{
    /// <summary>
    /// The directory scanned for plugin DLLs: <c>MIRELIA_DSP_PLUGIN_DIR</c>
    /// when set (development and tests), otherwise <c>plugins/dsp</c> beside
    /// the app.
    /// </summary>
    public static string Directory
    {
        get
        {
            string? overridden = Environment.GetEnvironmentVariable("MIRELIA_DSP_PLUGIN_DIR");
            return string.IsNullOrEmpty(overridden)
                ? Path.Combine(AppContext.BaseDirectory, "plugins", "dsp")
                : overridden;
        }
    }

    /// <summary>
    /// Registers every <c>*.dll</c> in <see cref="Directory"/> with the engine.
    /// Returns a description of each file that failed; a missing directory
    /// yields an empty list.
    /// </summary>
    public static IReadOnlyList<string> RegisterAll(AudioEngine engine)
    {
        List<string> failures = [];
        string directory = Directory;
        if (!System.IO.Directory.Exists(directory))
        {
            return failures;
        }

        foreach (string file in System.IO.Directory.EnumerateFiles(directory, "*.dll"))
        {
            try
            {
                engine.RegisterDspLibrary(Path.GetFullPath(file));
            }
            catch (AudioEngineException ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return failures;
    }
}
