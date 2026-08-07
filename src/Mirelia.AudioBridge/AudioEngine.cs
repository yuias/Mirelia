namespace Mirelia.AudioBridge;

/// <summary>
/// Safe wrapper around a native <c>kn_engine</c> instance. All members must
/// be called from managed threads only; the native side never calls back.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private nint _handle;

    private AudioEngine(nint handle)
    {
        _handle = handle;
    }

    /// <summary>
    /// Creates an engine, negotiating ABI v1 with whichever backend the
    /// resolver loaded. Throws <see cref="AudioEngineException"/> with
    /// <see cref="KnResult.AbiMismatch"/> if the backend is stale.
    /// </summary>
    public static AudioEngine Create(AudioEngineConfig config = default)
    {
        var native = new NativeMethods.KnEngineConfig
        {
            AbiVersion = NativeMethods.AbiVersion,
            TargetLatencyMs = config.TargetLatencyMs,
            DecodeAheadMs = config.DecodeAheadMs,
            AllowResample = config.AllowResample ? 1 : 0,
            DitherToInt = config.DitherToInt ? 1 : 0,
        };
        KnResult result = NativeMethods.kn_engine_create(in native, out nint handle);
        return result == KnResult.Ok
            ? new AudioEngine(handle)
            : throw new AudioEngineException(result, NativeMethods.LastError(nint.Zero));
    }

    /// <summary>Snapshot of the current playback position.</summary>
    public PlaybackPosition GetPosition()
    {
        ThrowIfDisposed();
        KnResult result = NativeMethods.kn_get_position(_handle, out NativeMethods.KnPosition p);
        if (result != KnResult.Ok)
        {
            throw new AudioEngineException(result, NativeMethods.LastError(_handle));
        }

        return new PlaybackPosition(
            (PlaybackState)p.State,
            p.UserToken,
            p.Frame,
            p.TotalFrames,
            p.SampleRate,
            p.HostTimeNs,
            p.BufferFill);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle != nint.Zero)
        {
            NativeMethods.kn_engine_destroy(_handle);
            _handle = nint.Zero;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
    }
}
