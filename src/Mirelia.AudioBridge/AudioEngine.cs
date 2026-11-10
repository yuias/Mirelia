namespace Mirelia.AudioBridge;

/// <summary>
/// Safe wrapper around a native <c>MireliaEngine</c> instance. All members must
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
    /// <see cref="MireliaResult.AbiMismatch"/> if the backend is stale, or with
    /// <see cref="MireliaResult.Unsupported"/> if the backend library is missing
    /// altogether.
    /// </summary>
    public static AudioEngine Create(AudioEngineConfig config = default)
    {
        var native = new NativeMethods.MireliaEngineConfig
        {
            AbiVersion = NativeMethods.AbiVersion,
            TargetLatencyMs = config.TargetLatencyMs,
            DecodeAheadMs = config.DecodeAheadMs,
            AllowResample = config.AllowResample ? 1 : 0,
            DitherToInt = (int)config.Dither,
        };

        MireliaResult result;
        nint handle;
        try
        {
            result = NativeMethods.mireliaEngineCreate(in native, out handle);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
        {
            // This is the first call into the backend, so a broken or absent
            // library surfaces here and nowhere else. Callers already handle
            // AudioEngineException everywhere; a raw loader exception from a
            // WinExe is a silent exit instead.
            throw new AudioEngineException(
                MireliaResult.Unsupported,
                $"The audio backend library could not be loaded. {ex.Message}",
                ex);
        }

        return result == MireliaResult.Ok
            ? new AudioEngine(handle)
            : throw new AudioEngineException(result, NativeMethods.LastError(nint.Zero));
    }

    /// <summary>Snapshot of the current playback position.</summary>
    public PlaybackPosition GetPosition()
    {
        ThrowIfDisposed();
        MireliaResult result = NativeMethods.mireliaGetPosition(
            _handle,
            out NativeMethods.MireliaPosition p);
        if (result != MireliaResult.Ok)
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
    /// <see cref="MireliaResult.DeviceBusy"/> or <see cref="MireliaResult.Format"/>.
    /// A non-null <paramref name="outputSampleRate"/> makes the engine convert
    /// every track to that rate and keep the channel count of the source;
    /// null plays each track at its own rate.
    /// </summary>
    public unsafe (uint SampleRate, ushort Channels) OpenDevice(
        string? deviceId = null, bool exclusive = false, uint? outputSampleRate = null)
    {
        ThrowIfDisposed();

        // Channels 0 means "follow the source's channels"; Format 3 is f32.
        NativeMethods.MireliaStreamFormat desired = new()
        {
            SampleRate = outputSampleRate.GetValueOrDefault(),
            Channels = 0,
            Format = 3,
            ChannelMask = 0,
        };
        Check(NativeMethods.mireliaEngineOpenDevice(
            _handle,
            deviceId,
            mode: exclusive ? 1 : 0,
            desired: outputSampleRate is null ? nint.Zero : (nint)(&desired),
            out NativeMethods.MireliaStreamFormat actual));
        return (actual.SampleRate, actual.Channels);
    }

    /// <summary>Closes the output device, stopping playback.</summary>
    public void CloseDevice()
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaEngineCloseDevice(_handle));
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
            var native = new NativeMethods.MireliaLoadParams
            {
                Path = (nint)p,
                StartFrame = startFrame,
                EndFrame = endFrame,
                GainDb = gainDb,
                UserToken = userToken,
            };
            Check(NativeMethods.mireliaLoad(_handle, (int)slot, in native));
        }
    }

    /// <summary>Clears a slot; clearing Current unloads playback entirely.</summary>
    public void Clear(AudioSlot slot)
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaClear(_handle, (int)slot));
    }

    /// <summary>Starts or resumes playback.</summary>
    public void Play()
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaPlay(_handle));
    }

    /// <summary>Pauses playback, keeping the position.</summary>
    public void Pause()
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaPause(_handle));
    }

    /// <summary>Stops playback and clears both slots.</summary>
    public void Stop()
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaStop(_handle));
    }

    /// <summary>Seeks within the current source, in frames.</summary>
    public void Seek(long frame)
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaSeek(_handle, frame));
    }

    /// <summary>Jumps to the Next slot immediately (not gapless).</summary>
    public void SkipToNext()
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaSkipToNext(_handle));
    }

    /// <summary>Sets linear output gain, 0.0 to 4.0. Smoothed internally.</summary>
    public void SetVolume(float gain)
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaSetVolume(_handle, gain));
    }

    /// <summary>Drains pending engine events (up to 32 per call).</summary>
    public IReadOnlyList<AudioEvent> PollEvents()
    {
        ThrowIfDisposed();
        var buffer = new NativeMethods.MireliaEvent[32];
        nuint n = NativeMethods.mireliaEnginePollEvent(_handle, buffer, (nuint)buffer.Length);
        var result = new List<AudioEvent>((int)n);
        for (nuint i = 0; i < n; i++)
        {
            NativeMethods.MireliaEvent e = buffer[i];
            result.Add(new AudioEvent(
                (AudioEventType)e.Type, e.UserToken, (MireliaResult)e.Code, e.HostTimeNs));
        }

        return result;
    }

    /// <summary>Lists playback devices.</summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateDevices()
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaDevicesEnumerate(_handle, out nint devices, out nuint count));
        try
        {
            var result = new List<AudioDeviceInfo>((int)count);
            int size = System.Runtime.InteropServices.Marshal
                .SizeOf<NativeMethods.MireliaDeviceInfo>();
            for (nuint i = 0; i < count; i++)
            {
                NativeMethods.MireliaDeviceInfo native = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<NativeMethods.MireliaDeviceInfo>(devices + (nint)i * size);
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
            NativeMethods.mireliaDevicesFree(_handle);
        }
    }

    /// <summary>
    /// Loads and validates a DSP plugin library. Registering does not select
    /// it; see <see cref="SetResampler"/>. <paramref name="path"/> must be
    /// absolute. Throws <see cref="AudioEngineException"/> with
    /// <see cref="MireliaResult.Unsupported"/> where plugins are not implemented.
    /// </summary>
    public void RegisterDspLibrary(string path)
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaRegisterDspLibrary(_handle, path));
    }

    /// <summary>Lists the registered DSP plugin capabilities, one entry each.</summary>
    public IReadOnlyList<DspPluginInfo> EnumerateDsp()
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaDspEnumerate(_handle, out nint infos, out nuint count));
        try
        {
            var result = new List<DspPluginInfo>((int)count);
            int size = System.Runtime.InteropServices.Marshal
                .SizeOf<NativeMethods.MireliaDspInfo>();
            for (nuint i = 0; i < count; i++)
            {
                NativeMethods.MireliaDspInfo native = System.Runtime.InteropServices.Marshal
                    .PtrToStructure<NativeMethods.MireliaDspInfo>(infos + (nint)i * size);
                result.Add(new DspPluginInfo(
                    System.Runtime.InteropServices.Marshal.PtrToStringUTF8(native.Id) ?? string.Empty,
                    System.Runtime.InteropServices.Marshal.PtrToStringUTF8(native.Name) ?? string.Empty,
                    (DspKind)native.Kind));
            }

            return result;
        }
        finally
        {
            NativeMethods.mireliaDspFree(_handle);
        }
    }

    /// <summary>
    /// Selects the resampler plugin by id; null selects the built-in resampler.
    /// Takes effect at the next load into <see cref="AudioSlot.Current"/>.
    /// Throws <see cref="AudioEngineException"/> with
    /// <see cref="MireliaResult.InvalidArg"/> for an id that is not registered.
    /// </summary>
    public void SetResampler(string? pluginId)
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaEngineSetResampler(_handle, pluginId));
    }

    /// <summary>
    /// EBU R128 scan: ReplayGain 2 track gain (dB) and true peak (linear).
    /// Blocking — call from a worker thread.
    /// </summary>
    public (float GainDb, float Peak) ScanLoudness(string path)
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaScanLoudness(_handle, path, out float gain, out float peak));
        return (gain, peak);
    }

    /// <summary>Reads stream metadata without playing.</summary>
    public unsafe AudioProbeResult Probe(string path)
    {
        ThrowIfDisposed();
        Check(NativeMethods.mireliaProbe(_handle, path, out NativeMethods.MireliaProbeResult p));
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
            NativeMethods.mireliaEngineDestroy(_handle);
            _handle = nint.Zero;
        }
    }

    private void Check(MireliaResult result)
    {
        if (result != MireliaResult.Ok)
        {
            throw new AudioEngineException(result, NativeMethods.LastError(_handle));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);
    }
}
