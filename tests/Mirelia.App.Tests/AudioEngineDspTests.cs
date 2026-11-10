using Mirelia.AudioBridge;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class AudioEngineDspTests : IDisposable
{
    private readonly AudioEngine _engine = AudioEngine.Create();

    public void Dispose() => _engine.Dispose();

    [Fact]
    public void AFreshEngineHasNoDspPlugins()
    {
        Assert.Empty(_engine.EnumerateDsp());
    }

    [Fact]
    public void SelectingTheBuiltInResamplerSucceeds()
    {
        _engine.SetResampler(null);
    }

    [Fact]
    public void SelectingAnUnregisteredResamplerIsRejected()
    {
        var ex = Assert.Throws<AudioEngineException>(() => _engine.SetResampler("no-such-plugin"));

        Assert.Equal(MireliaResult.InvalidArg, ex.Result);
    }

    [Fact]
    public void RegisteringAMissingFileReportsIo()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string missing = Path.Combine(Path.GetTempPath(), $"mirelia-missing-{Guid.NewGuid():N}.dll");

        var ex = Assert.Throws<AudioEngineException>(() => _engine.RegisterDspLibrary(missing));

        Assert.Equal(MireliaResult.Io, ex.Result);
    }
}
