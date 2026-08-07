namespace Mirelia.AudioBridge;

/// <summary>
/// A position snapshot from the engine. Positions are frames at
/// <see cref="SampleRate"/>, never milliseconds; use <see cref="HostTimeNs"/>
/// for client-side interpolation between polls.
/// </summary>
/// <param name="State">Transport state at snapshot time.</param>
/// <param name="UserToken">Host token of the audible source; 0 when idle.</param>
/// <param name="Frame">Frame position within the current source.</param>
/// <param name="TotalFrames">Total frames of the current source; -1 if unknown.</param>
/// <param name="SampleRate">Sample rate the position is expressed in.</param>
/// <param name="HostTimeNs">Monotonic host timestamp of the snapshot.</param>
/// <param name="BufferFill">Decode buffer fill ratio, 0..1.</param>
public readonly record struct PlaybackPosition(
    PlaybackState State,
    ulong UserToken,
    long Frame,
    long TotalFrames,
    uint SampleRate,
    ulong HostTimeNs,
    float BufferFill);
