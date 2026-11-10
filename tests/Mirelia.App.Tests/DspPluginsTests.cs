using Mirelia.App.Services;
using Mirelia.AudioBridge;
using Xunit;

namespace Mirelia.App.Tests;

// Tests in one class run serially, and no other test class reads
// MIRELIA_DSP_PLUGIN_DIR, so changing it here cannot race.
public sealed class DspPluginsTests : IDisposable
{
    private const string DirVariable = "MIRELIA_DSP_PLUGIN_DIR";

    private readonly string? _previous = Environment.GetEnvironmentVariable(DirVariable);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mirelia-dsp-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DirVariable, _previous);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void DirectoryHonoursTheEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(DirVariable, _root);

        Assert.Equal(_root, DspPlugins.Directory);
    }

    [Fact]
    public void DirectoryDefaultsToPluginsDspBesideTheApp()
    {
        Environment.SetEnvironmentVariable(DirVariable, null);

        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "plugins", "dsp"), DspPlugins.Directory);
    }

    [Fact]
    public void RegisterAllOnAMissingDirectoryReturnsNoFailures()
    {
        Environment.SetEnvironmentVariable(DirVariable, _root);
        using var engine = AudioEngine.Create();

        Assert.Empty(DspPlugins.RegisterAll(engine));
    }

    [Fact]
    public void RegisterAllReportsAFileThatIsNotAPlugin()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "garbage.dll"), "not a library");
        Environment.SetEnvironmentVariable(DirVariable, _root);
        using var engine = AudioEngine.Create();

        IReadOnlyList<string> failures = DspPlugins.RegisterAll(engine);

        string failure = Assert.Single(failures);
        Assert.StartsWith("garbage.dll: ", failure);
        Assert.Empty(engine.EnumerateDsp());
    }
}
