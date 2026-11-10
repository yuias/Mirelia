namespace Mirelia.AudioBridge;

/// <summary>
/// Kind of a DSP plugin capability. Mirrors <c>MireliaDspKind</c> in
/// <c>audio_core.h</c>.
/// </summary>
public enum DspKind
{
    /// <summary>Sample-rate conversion.</summary>
    Resampler = 1,
}

/// <summary>One registered DSP plugin capability from <see cref="AudioEngine.EnumerateDsp"/>.</summary>
/// <param name="Id">Stable plugin id, stored in settings.</param>
/// <param name="Name">Display name.</param>
/// <param name="Kind">What the capability does.</param>
public readonly record struct DspPluginInfo(string Id, string Name, DspKind Kind);
