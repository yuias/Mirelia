using Mirelia.App.Services;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"mirelia-settings-{Guid.NewGuid():N}");

    public SettingsServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void OutputSampleRateDefaultsToSameAsSource()
    {
        var service = new SettingsService(Path.Combine(_root, "settings.json"));

        Assert.Null(service.Current.OutputSampleRate);
    }

    [Theory]
    [InlineData(44100u)]
    [InlineData(192000u)]
    public void ASavedOutputSampleRateSurvivesAReload(uint rate)
    {
        string path = Path.Combine(_root, "settings.json");
        new SettingsService(path).Save(new AppSettings { OutputSampleRate = rate });

        Assert.Equal(rate, new SettingsService(path).Current.OutputSampleRate);
    }

    [Fact]
    public void DitherResampledDefaultsToOff()
    {
        var service = new SettingsService(Path.Combine(_root, "settings.json"));

        Assert.False(service.Current.DitherResampled);
    }

    [Fact]
    public void ADitherResampledChoiceSurvivesAReload()
    {
        string path = Path.Combine(_root, "settings.json");
        new SettingsService(path).Save(new AppSettings { DitherResampled = true });

        Assert.True(new SettingsService(path).Current.DitherResampled);
    }

    [Fact]
    public void AnUnsupportedOutputSampleRateLoadsAsSameAsSource()
    {
        string path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{ "OutputSampleRate": 12345, "Exclusive": true }""");

        SettingsService service = new(path);

        Assert.Null(service.Current.OutputSampleRate);
        Assert.True(service.Current.Exclusive);
    }
}
