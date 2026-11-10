namespace Mirelia.AudioBridge;

/// <summary>Transport state. Mirrors <c>MireliaState</c> in <c>audio_core.h</c>.</summary>
public enum PlaybackState
{
    /// <summary>No source loaded.</summary>
    Idle = 0,

    /// <summary>Audible.</summary>
    Playing = 1,

    /// <summary>Loaded, clock stopped.</summary>
    Paused = 2,

    /// <summary>Playing but the decode buffer ran dry.</summary>
    Stalled = 3,
}
