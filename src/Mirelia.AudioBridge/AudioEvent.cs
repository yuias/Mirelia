namespace Mirelia.AudioBridge;

/// <summary>Event kinds. Mirrors <c>kn_event_type</c> in <c>audio_core.h</c>.</summary>
public enum AudioEventType
{
    /// <summary>Transport state changed asynchronously.</summary>
    StateChanged = 1,

    /// <summary>A (new) track became audible.</summary>
    TrackStarted = 2,

    /// <summary>The audible track finished.</summary>
    TrackEnded = 3,

    /// <summary>The engine needs a next slot now.</summary>
    QueueEmpty = 4,

    /// <summary>The default device switched.</summary>
    DeviceChanged = 5,

    /// <summary>The device disappeared.</summary>
    DeviceLost = 6,

    /// <summary>An asynchronous error; see <see cref="AudioEvent.Code"/>.</summary>
    Error = 7,
}

/// <summary>One engine event drained by <see cref="AudioEngine.PollEvents"/>.</summary>
/// <param name="Type">What happened.</param>
/// <param name="UserToken">The host token passed at load time.</param>
/// <param name="Code">A <see cref="KnResult"/> for errors; 0 otherwise.</param>
/// <param name="HostTimeNs">Monotonic timestamp of the event.</param>
public readonly record struct AudioEvent(
    AudioEventType Type,
    ulong UserToken,
    KnResult Code,
    ulong HostTimeNs);

/// <summary>The two source slots. Mirrors <c>kn_slot</c>.</summary>
public enum AudioSlot
{
    /// <summary>What is playing (or will play).</summary>
    Current = 0,

    /// <summary>What plays after the current source, for gapless advance.</summary>
    Next = 1,
}

/// <summary>One playback device from <see cref="AudioEngine.EnumerateDevices"/>.</summary>
/// <param name="Id">Stable, backend-scoped identifier.</param>
/// <param name="Name">Human-readable name.</param>
/// <param name="IsDefault">True for the system default render device.</param>
/// <param name="NativeRate">Current mix/native sample rate.</param>
/// <param name="NativeChannels">Current channel count.</param>
public readonly record struct AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault,
    uint NativeRate,
    ushort NativeChannels);

/// <summary>Stream metadata from <see cref="AudioEngine.Probe"/>.</summary>
/// <param name="SampleRate">Sample rate in Hz.</param>
/// <param name="Channels">Channel count.</param>
/// <param name="BitDepth">Source bit depth; null for lossy codecs.</param>
/// <param name="TotalFrames">Total frames; null when unknown.</param>
/// <param name="BitrateKbps">Average bitrate; null when unknown.</param>
/// <param name="Codec">Canonical codec id (mp3, aac, alac, flac, pcm).</param>
public readonly record struct AudioProbeResult(
    uint SampleRate,
    ushort Channels,
    ushort? BitDepth,
    long? TotalFrames,
    uint? BitrateKbps,
    string Codec);
