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
    /// <see cref="KnResult.AbiMismatch"/> if the backend is stale, or with
    /// <see cref="KnResult.Unsupported"/> if the backend library is missing
    /// altogether.
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

        KnResult result;
        nint handle;
        try
        {
            result = NativeMethods.kn_engine_create(in native, out handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            // This is the first call into the backend, so a broken or absent
            // library surfaces here and nowhere else. Callers already handle
            // AudioEngineException everywhere; a raw loader exception from a
            // WinExe is a silent exit instead.
            throw new AudioEngineException(
                KnResult.Unsupported,
                $"The audio backend library could not be loaded. {ex.Message}",
                ex);
        }

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

    /// <summary>
    /// Opens an output device (null = system default) and returns the
    /// negotiated sample rate and channel count. Exclusive mode is
    /// bit-perfect (no OS mixer) but can fail with
    /// <see cref="KnResult.DeviceBusy"/> or <see cref="KnResult.Format"/>.
    /// </summary>
    public (uint SampleRate, ushort Channels) OpenDevice(
        string? deviceId = null, bool exclusive = false)
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_engine_open_device(
            _handle,
            deviceId,
            mode: exclusive ? 1 : 0,
            desired: nint.Zero,
            out NativeMethods.KnStreamFormat actual));
        return (actual.SampleRate, actual.Channels);
    }

    /// <summary>Closes the output device, stopping playback.</summary>
    public void CloseDevice()
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_engine_close_device(_handle));
    }

    /// <summary>
    /// Loads a file into a slot. Loading <see cref="AudioSlot.Next"/> is what
    /// makes gapless advance work; the engine switches when the current
    /// source ends.
    /// </summary>
    public unsafe void Load(
        AudioSlot slot,
        string path,
        ulong userToken,
        float gainDb = 0,
        long startFrame = 0,
        long endFrame = -1)
    {
        ThrowIfDisposed();
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        fixed (byte* p = utf8)
        {
            var native = new NativeMethods.KnLoadParams
            {
                Path = (nint)p,
                StartFrame = startFrame,
                EndFrame = endFrame,
                GainDb = gainDb,
                UserToken = userToken,
            };
            Check(NativeMethods.kn_load(_handle, (int)slot, in native));
        }
    }

    /// <summary>Clears a slot; clearing Current unloads playback entirely.</summary>
    public void Clear(AudioSlot slot)
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_clear(_handle, (int)slot));
    }

    /// <summary>Starts or resumes playback.</summary>
    public void Play()
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_play(_handle));
    }

    /// <summary>Pauses playback, keeping the position.</summary>
    public void Pause()
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_pause(_handle));
    }

    /// <summary>Stops playback and clears both slots.</summary>
    public void Stop()
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_stop(_handle));
    }

    /// <summary>Seeks within the current source, in frames.</summary>
    public void Seek(long frame)
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_seek(_handle, frame));
    }

    /// <summary>Jumps to the Next slot immediately (not gapless).</summary>
    public void SkipToNext()
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_skip_to_next(_handle));
    }

    /// <summary>Sets linear output gain, 0.0 to 4.0. Smoothed internally.</summary>
    public void SetVolume(float gain)
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_set_volume(_handle, gain));
    }

    /// <summary>Drains pending engine events (up to 32 per call).</summary>
    public IReadOnlyList<AudioEvent> PollEvents()
    {
        ThrowIfDisposed();
        var buffer = new NativeMethods.KnEvent[32];
        nuint n = NativeMethods.kn_engine_poll_event(_handle, buffer, (nuint)buffer.Length);
        var result = new List<AudioEvent>((int)n);
        for (nuint i = 0; i < n; i++)
        {
            NativeMethods.KnEvent e = buffer[i];
            result.Add(new AudioEvent(
                (AudioEventType)e.Type, e.UserToken, (KnResult)e.Code, e.HostTimeNs));
        }

        return result;
    }

    /// <summary>Lists playback devices.</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_devices_enumerate(_handle, out nint devices, out nuint count));
        try
        {
            var result = new List<AudioDeviceInfo>((int)count);
            int size = System.Runtime.InteropServices.Marshal
                .SizeOf<NativeMethods.KnDeviceInfo>();
            for (nuint i = 0; i < count; i++)
            {
                NativeMethods.KnDeviceInfo native = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<NativeMethods.KnDeviceInfo>(devices + (nint)i * size);
                result.Add(new AudioDeviceInfo(
                    System.Runtime.InteropServices.Marshal.PtrToStringUTF8(native.Id) ?? string.Empty,
                    System.Runtime.InteropServices.Marshal.PtrToStringUTF8(native.Name) ?? "(unknown)",
                    native.IsDefault != 0,
                    native.NativeRate,
                    native.NativeChannels));
            }

            return result;
        }
        finally
        {
            NativeMethods.kn_devices_free(_handle);
        }
    }

    /// <summary>
    /// EBU R128 scan: ReplayGain 2 track gain (dB) and true peak (linear).
    /// Blocking — call from a worker thread.
    /// </summary>
    public (float GainDb, float Peak) ScanLoudness(string path)
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_scan_loudness(_handle, path, out float gain, out float peak));
        return (gain, peak);
    }

    /// <summary>Reads stream metadata without playing.</summary>
    public unsafe AudioProbeResult Probe(string path)
    {
        ThrowIfDisposed();
        Check(NativeMethods.kn_probe(_handle, path, out NativeMethods.KnProbeResult p));
        string codec = System.Text.Encoding.ASCII
            .GetString(p.Codec, 16)
            .TrimEnd('\0');
        return new AudioProbeResult(
            p.SampleRate,
            p.Channels,
            p.BitDepth == 0 ? null : p.BitDepth,
            p.TotalFrames < 0 ? null : p.TotalFrames,
            p.BitrateKbps == 0 ? null : p.BitrateKbps,
            codec);
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

    private void Check(KnResult result)
    {
        if (result != KnResult.Ok)
        {
            throw new AudioEngineException(result, NativeMethods.LastError(_handle));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
    }
}
