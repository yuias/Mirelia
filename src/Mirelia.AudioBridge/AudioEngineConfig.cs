namespace Mirelia.AudioBridge;

/// <summary>Engine creation options. Mirrors <c>kn_engine_config</c>.</summary>
/// <param name="TargetLatencyMs">Output latency hint in milliseconds; 0 = backend default.</param>
/// <param name="DecodeAheadMs">Decode-ahead ring buffer depth in milliseconds; 0 = backend default.</param>
/// <param name="AllowResample">When false, playback fails rather than resamples.</param>
/// <param name="DitherToInt">Apply TPDF dither on float-to-int conversion.</param>
public readonly record struct AudioEngineConfig(
    uint TargetLatencyMs = 0,
    uint DecodeAheadMs = 0,
    bool AllowResample = true,
    bool DitherToInt = true);
