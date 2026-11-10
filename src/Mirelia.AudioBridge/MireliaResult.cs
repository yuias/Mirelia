namespace Mirelia.AudioBridge;

/// <summary>
/// Result codes returned by the native audio core. Mirrors <c>MireliaResult</c>
/// in <c>audio_core.h</c>; the header is authoritative.
/// </summary>
public enum MireliaResult
{
    /// <summary>Success.</summary>
    Ok = 0,

    /// <summary>An argument was null or out of range.</summary>
    InvalidArg = -1,

    /// <summary>The operation is not supported by this backend.</summary>
    Unsupported = -2,

    /// <summary>The audio device disappeared.</summary>
    DeviceLost = -3,

    /// <summary>Exclusive mode was denied.</summary>
    DeviceBusy = -4,

    /// <summary>Decoding failed.</summary>
    Decode = -5,

    /// <summary>File or stream I/O failed.</summary>
    Io = -6,

    /// <summary>The stream format is not usable.</summary>
    Format = -7,

    /// <summary>The call is invalid in the current engine state.</summary>
    State = -8,

    /// <summary>Native allocation failed.</summary>
    OutOfMemory = -9,

    /// <summary>The backend does not implement the requested ABI version.</summary>
    AbiMismatch = -10,

    /// <summary>A DSP plugin failed; playback continued without it.</summary>
    Plugin = -11,

    /// <summary>Unspecified internal failure.</summary>
    Internal = -99,
}
