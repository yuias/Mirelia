namespace Mirelia.AudioBridge;

/// <summary>When the engine applies TPDF dither on float-to-integer conversion.</summary>
public enum DitherMode
{
    /// <summary>Never.</summary>
    Off = 0,

    /// <summary>On every track.</summary>
    Always = 1,

    /// <summary>
    /// Only on tracks the engine resampled. A track that plays at its own rate
    /// is never dithered, so it stays bit-perfect.
    /// </summary>
    Resampled = 2,
}

/// <summary>Engine creation options. Mirrors <c>kn_engine_config</c>.</summary>
/// <param name="TargetLatencyMs">Output latency hint in milliseconds; 0 = backend default.</param>
/// <param name="DecodeAheadMs">Decode-ahead ring buffer depth in milliseconds; 0 = backend default.</param>
/// <param name="AllowResample">When false, playback fails rather than resamples.</param>
/// <param name="Dither">When to apply TPDF dither on float-to-integer conversion.</param>
public readonly record struct AudioEngineConfig(
    uint TargetLatencyMs = 0,
    uint DecodeAheadMs = 0,
    bool AllowResample = true,
    DitherMode Dither = DitherMode.Always);
