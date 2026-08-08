using Avalonia.Threading;
using Mirelia.AudioBridge;

namespace Mirelia.App.Services;

/// <summary>
/// Plays a queue of library tracks through the native engine. Owns the
/// engine instance, a 200 ms poll timer for position/events, and the
/// next-slot preloading that makes track changes seamless.
/// All members run on the UI thread.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer;

    private AudioEngine? _engine;
    private bool _deviceOpen;
    private long[] _queue = [];
    private int _index = -1;
    private bool _nextPreloaded;

    /// <summary>Fires on every poll with the current position.</summary>
    public event Action<PlaybackPosition>? PositionChanged;

    /// <summary>Fires when a different track becomes audible (track id).</summary>
    public event Action<long>? NowPlayingChanged;

    /// <summary>Fires on asynchronous playback errors, with detail text.</summary>
    public event Action<string>? PlaybackError;

    /// <summary>Creates the service; the engine itself is created lazily.</summary>
    public AudioPlayerService(LibraryService library, SettingsService settings)
    {
        _library = library;
        _settings = settings;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Poll());

        // Device/mode changes take effect on the next play; live playback
        // stops so the old stream does not linger on the old device.
        _settings.Changed += _ =>
        {
            try
            {
                _engine?.Stop();
                _engine?.CloseDevice();
            }
            catch (AudioEngineException)
            {
                // Old device may already be gone; the reopen handles it.
            }

            _deviceOpen = false;
            _index = -1;
        };
    }

    /// <summary>True when a queue is loaded (playing or paused).</summary>
    public bool HasTrack => _index >= 0;

    /// <summary>Replaces the queue and starts playing at <paramref name="index"/>.</summary>
    public async Task PlayQueueAsync(long[] queue, int index)
    {
        _queue = queue;
        await StartAtAsync(index);
    }

    /// <summary>Toggles pause/resume; no-op when nothing is loaded.</summary>
    public void TogglePause()
    {
        if (_engine is null || !HasTrack)
        {
            return;
        }

        try
        {
            PlaybackPosition pos = _engine.GetPosition();
            if (pos.State == PlaybackState.Playing || pos.State == PlaybackState.Stalled)
            {
                _engine.Pause();
            }
            else
            {
                _engine.Play();
            }
        }
        catch (AudioEngineException ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    /// <summary>Skips to the next queue entry.</summary>
    public Task NextAsync()
        => _index + 1 < _queue.Length ? StartAtAsync(_index + 1) : Task.CompletedTask;

    /// <summary>Returns to the previous queue entry.</summary>
    public Task PreviousAsync()
        => _index > 0 ? StartAtAsync(_index - 1) : Task.CompletedTask;

    /// <summary>Seeks within the current track, in seconds.</summary>
    public void Seek(double seconds)
    {
        if (_engine is null || !HasTrack)
        {
            return;
        }

        try
        {
            PlaybackPosition pos = _engine.GetPosition();
            if (pos.SampleRate > 0)
            {
                _engine.Seek((long)(seconds * pos.SampleRate));
            }
        }
        catch (AudioEngineException ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    /// <summary>Sets linear volume, 0..1.</summary>
    public void SetVolume(double volume)
    {
        try
        {
            _engine?.SetVolume((float)Math.Clamp(volume, 0.0, 1.0));
        }
        catch (AudioEngineException ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        _engine?.Dispose();
    }

    private async Task StartAtAsync(int index)
    {
        if (index < 0 || index >= _queue.Length)
        {
            return;
        }

        long id = _queue[index];
        string? path = await _library.GetPathAsync(id);
        if (path is null || !File.Exists(path))
        {
            PlaybackError?.Invoke($"file missing for track {id}");
            return;
        }

        try
        {
            EnsureEngine();
            _index = index;
            _nextPreloaded = false;
            _engine!.Load(AudioSlot.Current, path, (ulong)id, await GainForAsync(id));
            _engine.Play();
            _timer.Start();
            NowPlayingChanged?.Invoke(id);
            await PreloadNextAsync();
        }
        catch (AudioEngineException ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    private async Task<float> GainForAsync(long id)
    {
        if (_settings.Current.ReplayGain != ReplayGainMode.Track)
        {
            return 0f;
        }

        double? gain = await _library.GetTrackGainAsync(id);
        return (float)(gain ?? 0);
    }

    private async Task PreloadNextAsync()
    {
        if (_nextPreloaded || _engine is null || _index + 1 >= _queue.Length)
        {
            return;
        }

        long nextId = _queue[_index + 1];
        string? path = await _library.GetPathAsync(nextId);
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            _engine.Load(AudioSlot.Next, path, (ulong)nextId, await GainForAsync(nextId));
            _nextPreloaded = true;
        }
        catch (AudioEngineException)
        {
            // Format change: the engine refuses gapless across formats until
            // M5. QueueEmpty will arrive at EOF and we reload as Current.
        }
    }

    private void Poll()
    {
        if (_engine is null)
        {
            return;
        }

        try
        {
            foreach (AudioEvent ev in _engine.PollEvents())
            {
                switch (ev.Type)
                {
                    case AudioEventType.TrackStarted:
                        OnTrackStarted((long)ev.UserToken);
                        break;

                    case AudioEventType.QueueEmpty:
                        // Auto-advance path for format changes (see above).
                        if (!_nextPreloaded && _index + 1 < _queue.Length)
                        {
                            _ = StartAtAsync(_index + 1);
                        }

                        break;

                    case AudioEventType.Error:
                        PlaybackError?.Invoke($"engine error: {ev.Code}");
                        break;
                }
            }

            PositionChanged?.Invoke(_engine.GetPosition());
        }
        catch (AudioEngineException ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    private void OnTrackStarted(long id)
    {
        int index = Array.IndexOf(_queue, id);
        if (index >= 0 && index != _index)
        {
            // The engine advanced into the preloaded next slot on its own.
            _index = index;
            _nextPreloaded = false;
            _ = PreloadNextAsync();
        }

        NowPlayingChanged?.Invoke(id);
    }

    private void EnsureEngine()
    {
        _engine ??= AudioEngine.Create();
        if (_deviceOpen)
        {
            return;
        }

        AppSettings settings = _settings.Current;
        try
        {
            _engine.OpenDevice(settings.DeviceId, settings.Exclusive);
        }
        catch (AudioEngineException ex) when (settings.Exclusive
            && ex.Result is KnResult.DeviceBusy or KnResult.Format or KnResult.Unsupported)
        {
            // Bit-perfect denied (device busy or format not offered):
            // shared mode beats silence.
            _engine.OpenDevice(settings.DeviceId, exclusive: false);
            PlaybackError?.Invoke($"exclusive mode unavailable, using shared ({ex.Result})");
        }

        _deviceOpen = true;
    }
}
