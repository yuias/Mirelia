using Avalonia.Threading;
using Mirelia.AudioBridge;

namespace Mirelia.App.Services;

/// <summary>
/// Plays a queue of library tracks through the native engine. Owns the
/// engine instance, a 200 ms poll timer for position/events, the playback
/// queue itself, and the next-slot preloading that makes track changes
/// seamless. All members run on the UI thread.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _timer;
    private readonly PlayQueue _queue = new();

    private AudioEngine? _engine;
    private bool _deviceOpen;

    // Output settings the live device was opened for, and the ReplayGain
    // mode the loaded slots were decoded under. A settings save that touches
    // neither (volume, pane visibility, window geometry, fonts…) must leave
    // playback alone.
    private (string? DeviceId, bool Exclusive) _deviceConfig;
    private ReplayGainMode _replayGain;

    // Volume lives here, not on the engine, because the engine is created
    // lazily and reopened on device loss — both need the level re-applied.
    private float _volume;

    // Entry id currently loaded into the engine's Next slot, or null when
    // nothing is preloaded. The engine's user_token carries entry ids (not
    // track ids), so a duplicated track never resolves to the wrong slot.
    private long? _preloadedEntryId;
    private bool _recoveringDevice;

    // The entry StartAtEntryAsync just loaded into the Current slot. Its
    // TrackStarted is not an auto-advance and must not promote the cursor —
    // and under repeat-one both slots hold the same entry id, so the id
    // alone cannot tell the two apart.
    private long? _pendingStartEntryId;

    // Latched across polls: the engine pushes TrackEnded before it switches
    // its state to Idle, so one poll can see the event while the state still
    // reads Playing.
    private bool _trackEndedPending;

    private RepeatMode _repeat;

    /// <summary>Fires on every poll with the current position.</summary>
    public event Action<PlaybackPosition>? PositionChanged;

    /// <summary>Fires when a different track becomes audible (track id).</summary>
    public event Action<long>? NowPlayingChanged;

    /// <summary>Fires after any queue mutation (insert/remove/move/clear).</summary>
    public event Action? QueueChanged;

    /// <summary>Fires when the current queue entry changes, with its entry id (null when nothing is current).</summary>
    public event Action<long?>? CurrentEntryChanged;

    /// <summary>
    /// Fires when playback stops with nothing further to play: the queue ran out,
    /// the current entry was removed with nothing after it, the queue was cleared
    /// for a library switch, or the output device/mode changed.
    /// </summary>
    public event Action? PlaybackEnded;

    /// <summary>Fires on asynchronous playback errors, with detail text.</summary>
    public event Action<string>? PlaybackError;

    /// <summary>Creates the service; the engine itself is created lazily.</summary>
    public AudioPlayerService(LibraryService library, SettingsService settings)
    {
        _library = library;
        _settings = settings;
        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => Poll());

        AppSettings current = settings.Current;
        _deviceConfig = (current.DeviceId, current.Exclusive);
        _replayGain = current.ReplayGain;
        _volume = ClampVolume(current.Volume);
        _repeat = current.Repeat;
        _queue.WrapAround = _repeat == RepeatMode.All;
        _queue.Shuffle = current.Shuffle;
        _settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(AppSettings settings)
    {
        if (_replayGain != settings.ReplayGain)
        {
            // The preloaded slot was decoded under the old mode; reload it so
            // the next track is gained correctly. Whatever is already audible
            // keeps its gain until it ends.
            _replayGain = settings.ReplayGain;
            _preloadedEntryId = null;
            _ = ReconcilePreloadAsync();
        }

        if (_deviceConfig == (settings.DeviceId, settings.Exclusive))
        {
            return;
        }

        // Device/mode changes take effect on the next play; live playback
        // stops so the old stream does not linger on the old device. The
        // queue itself is left intact — only the "what's audible" pointer
        // resets, mirroring the previous _index = -1 behavior.
        _deviceConfig = (settings.DeviceId, settings.Exclusive);
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
        _preloadedEntryId = null;
        _trackEndedPending = false;
        ClearCurrentEntry();
        PlaybackEnded?.Invoke();
    }

    /// <summary>Whether the queue plays in a shuffled order.</summary>
    public bool Shuffle
    {
        get => _queue.Shuffle;
        set
        {
            if (_queue.Shuffle == value)
            {
                return;
            }

            _queue.Shuffle = value;
            _ = ReconcilePreloadAsync();
        }
    }

    /// <summary>What happens when the queue or the current track ends.</summary>
    public RepeatMode Repeat
    {
        get => _repeat;
        set
        {
            if (_repeat == value)
            {
                return;
            }

            _repeat = value;
            _queue.WrapAround = value == RepeatMode.All;
            _ = ReconcilePreloadAsync();
        }
    }

    /// <summary>
    /// The entry the engine's Next slot should hold. Repeat-one is expressed
    /// here rather than in the queue so that pressing Next still advances.
    /// </summary>
    private QueueEntry? PreloadTarget
        => _repeat == RepeatMode.One ? _queue.Current : _queue.NextEntry;

    /// <summary>True when a queue is loaded (playing or paused).</summary>
    public bool HasTrack => _queue.Current is not null;

    /// <summary>The queue's entries, in play order — a live view, not a snapshot.</summary>
    public IReadOnlyList<QueueEntry> QueueEntries => _queue.Entries;

    /// <summary>Entry id of the audible entry, or null when nothing is current.</summary>
    public long? CurrentEntryId => _queue.Current?.EntryId;

    /// <summary>Replaces the queue and starts playing at <paramref name="index"/> ("play now").</summary>
    public async Task PlayQueueAsync(long[] queue, int index)
    {
        IReadOnlyList<QueueEntry> entries = _queue.Replace(queue, index);
        QueueChanged?.Invoke();
        if (entries.Count > 0)
        {
            await StartAtEntryAsync(_queue.Current!.Value);
        }
    }

    /// <summary>
    /// Inserts tracks right after the current entry ("Play Next"). Starts
    /// playback immediately if nothing was playing.
    /// </summary>
    public async Task EnqueueNextAsync(IReadOnlyList<long> trackIds)
    {
        if (trackIds.Count == 0)
        {
            return;
        }

        bool wasEmpty = !HasTrack;
        IReadOnlyList<QueueEntry> inserted = _queue.InsertNext(trackIds);
        QueueChanged?.Invoke();
        if (wasEmpty)
        {
            await StartAtEntryAsync(inserted[0]);
        }
        else
        {
            await ReconcilePreloadAsync();
        }
    }

    /// <summary>
    /// Appends tracks to the end of the queue ("Add to Queue"). Starts
    /// playback immediately if nothing was playing.
    /// </summary>
    public async Task EnqueueEndAsync(IReadOnlyList<long> trackIds)
    {
        if (trackIds.Count == 0)
        {
            return;
        }

        bool wasEmpty = !HasTrack;
        IReadOnlyList<QueueEntry> appended = _queue.Append(trackIds);
        QueueChanged?.Invoke();
        if (wasEmpty)
        {
            await StartAtEntryAsync(appended[0]);
        }
        else
        {
            await ReconcilePreloadAsync();
        }
    }

    /// <summary>Removes one queue entry. If it was playing, advances to what took its place (or stops).</summary>
    public async Task RemoveEntryAsync(long entryId)
    {
        QueueRemoveResult result = _queue.Remove(entryId);
        switch (result)
        {
            case QueueRemoveResult.NotFound:
                return;

            case QueueRemoveResult.RemovedOther:
                QueueChanged?.Invoke();
                await ReconcilePreloadAsync();
                return;

            case QueueRemoveResult.RemovedCurrentHasNext:
                QueueChanged?.Invoke();
                await StartAtEntryAsync(_queue.Current!.Value);
                return;

            case QueueRemoveResult.RemovedCurrentQueueEnded:
                QueueChanged?.Invoke();
                StopForQueueEnded();
                return;
        }
    }

    /// <summary>Moves an entry to a new position (drag reorder).</summary>
    public async Task MoveEntryAsync(long entryId, int newIndex)
    {
        if (!_queue.Move(entryId, newIndex))
        {
            return;
        }

        QueueChanged?.Invoke();
        await ReconcilePreloadAsync();
    }

    /// <summary>Drops every queued-up entry except the one currently playing.</summary>
    public async Task ClearQueueAsync()
    {
        _queue.Clear();
        QueueChanged?.Invoke();
        await ReconcilePreloadAsync();
    }

    /// <summary>
    /// Stops playback and empties the queue outright, current entry included.
    /// Used when the library underneath changes: queued track ids belong to
    /// the database that is going away, and would resolve to the wrong files
    /// (or none) in the new one.
    /// </summary>
    public void StopAndClear()
    {
        _timer.Stop();
        try
        {
            _engine?.Stop();
        }
        catch (AudioEngineException)
        {
            // Device may already be gone; the queue still has to go.
        }

        _preloadedEntryId = null;
        _queue.RemoveAll();
        CurrentEntryChanged?.Invoke(null);
        PlaybackEnded?.Invoke();
        QueueChanged?.Invoke();
    }

    /// <summary>Jumps playback directly to a queue entry (double-click in the queue pane).</summary>
    public Task PlayEntryAsync(long entryId)
        => _queue.Find(entryId) is { } entry ? StartAtEntryAsync(entry) : Task.CompletedTask;

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
        => _queue.NextEntry is { } next ? StartAtEntryAsync(next) : Task.CompletedTask;

    /// <summary>Returns to the previous queue entry.</summary>
    public Task PreviousAsync()
        => _queue.PreviousEntry is { } previous ? StartAtEntryAsync(previous) : Task.CompletedTask;

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
        _volume = ClampVolume(volume);
        try
        {
            _engine?.SetVolume(_volume);
        }
        catch (AudioEngineException ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    private static float ClampVolume(double volume) => (float)Math.Clamp(volume, 0.0, 1.0);

    // A freshly opened device starts at unity gain, so the remembered level
    // has to be pushed back after every open (first play, device recovery).
    private void ApplyVolume()
    {
        try
        {
            _engine?.SetVolume(_volume);
        }
        catch (AudioEngineException)
        {
            // A device that cannot take a volume is still worth playing on.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer.Stop();
        _engine?.Dispose();
    }

    private async Task StartAtEntryAsync(QueueEntry entry)
    {
        string? path = await _library.GetPathAsync(entry.TrackId);
        if (path is null || !File.Exists(path))
        {
            PlaybackError?.Invoke($"file missing for track {entry.TrackId}");
            return;
        }

        try
        {
            EnsureEngine();

            // Awaited before the promote: a poll during the await would
            // otherwise see the old track's end with the new entry already
            // current and report playback as over.
            float gain = await GainForAsync(entry.TrackId);
            PromoteCurrent(entry);
            _pendingStartEntryId = entry.EntryId;
            _trackEndedPending = false;
            _preloadedEntryId = null;
            _engine!.Load(AudioSlot.Current, path, (ulong)entry.EntryId, gain);
            _engine.Play();
            _timer.Start();
            NowPlayingChanged?.Invoke(entry.TrackId);
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
        if (_preloadedEntryId is not null || _engine is null || PreloadTarget is not { } entry)
        {
            return;
        }

        string? path = await _library.GetPathAsync(entry.TrackId);
        if (path is null || !File.Exists(path))
        {
            return;
        }

        try
        {
            _engine.Load(AudioSlot.Next, path, (ulong)entry.EntryId, await GainForAsync(entry.TrackId));
            _preloadedEntryId = entry.EntryId;
        }
        catch (AudioEngineException)
        {
            // Format change: the engine refuses a gapless Next across
            // formats. QueueEmpty arrives at EOF and we reload as Current.
        }
    }

    /// <summary>
    /// Re-checks the Next slot against the queue after a mutation and only
    /// touches the engine when the entry that belongs there actually
    /// changed — mutations behind the current entry are otherwise free.
    /// </summary>
    private async Task ReconcilePreloadAsync()
    {
        if (PreloadTarget?.EntryId == _preloadedEntryId)
        {
            return;
        }

        _preloadedEntryId = null;
        if (_engine is not null)
        {
            try
            {
                _engine.Clear(AudioSlot.Next);
            }
            catch (AudioEngineException)
            {
                // Nothing to clear, or the device is already gone;
                // PreloadNextAsync below will surface a real failure.
            }
        }

        await PreloadNextAsync();
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

                    case AudioEventType.TrackEnded:
                        _trackEndedPending = true;
                        break;

                    case AudioEventType.QueueEmpty:
                        // Auto-advance path for format changes (see above).
                        if (_preloadedEntryId is null && PreloadTarget is { } resume)
                        {
                            _ = StartAtEntryAsync(resume);
                        }

                        break;

                    case AudioEventType.Error:
                        PlaybackError?.Invoke($"engine error: {ev.Code}");
                        break;

                    case AudioEventType.DeviceLost:
                        // The device disappeared under us; always try to
                        // recover, falling back to the system default.
                        _ = RecoverDeviceAsync();
                        break;

                    case AudioEventType.DeviceChanged:
                        // Only "system default" playback should follow the
                        // switch; a specific device stays put until it, too,
                        // is lost.
                        if (_settings.Current.DeviceId is null)
                        {
                            _ = RecoverDeviceAsync();
                        }

                        break;
                }
            }

            PlaybackPosition pos = _engine.GetPosition();
            PositionChanged?.Invoke(pos);

            // QueueEmpty is no end signal: it fires at decoder EOF, before the
            // buffered audio has played out, and a Next slot can still arrive
            // after it. The PreloadTarget check keeps the format-change restart
            // and the repeat modes from counting as an end.
            if (_trackEndedPending && pos.State == PlaybackState.Idle && PreloadTarget is null)
            {
                _trackEndedPending = false;
                PlaybackEnded?.Invoke();
            }
        }
        catch (AudioEngineException ex)
        {
            PlaybackError?.Invoke(ex.Message);
        }
    }

    private void OnTrackStarted(long entryId)
    {
        _trackEndedPending = false;

        QueueEntry? entry = _queue.Find(entryId);
        if (entry is not { } e)
        {
            // The entry that just became audible was removed from the queue
            // in the meantime (e.g. a preloaded Next slot outraced its own
            // invalidation); nothing sane to report against.
            return;
        }

        if (_pendingStartEntryId == entryId && _queue.Current?.EntryId == entryId)
        {
            // The start we asked for, now audible — the cursor is already
            // where it belongs and the Next slot stays armed.
            _pendingStartEntryId = null;
        }
        else
        {
            // The engine moved into the preloaded slot on its own. Under
            // repeat-one that slot holds the entry that was already current,
            // so this re-arms the loop rather than advancing anything.
            PromoteCurrent(e);
            _preloadedEntryId = null;
            _ = PreloadNextAsync();
        }

        NowPlayingChanged?.Invoke(e.TrackId);
    }

    private async Task RecoverDeviceAsync()
    {
        if (_engine is null || !HasTrack || _recoveringDevice)
        {
            return;
        }

        _recoveringDevice = true;
        try
        {
            // Snapshot before touching the device: reopening resets the
            // engine's transport state, so the last-known position must be
            // captured first.
            PlaybackPosition pos = _engine.GetPosition();
            QueueEntry? current = _queue.Find((long)pos.UserToken);
            if (current is not { } entry)
            {
                // The audible entry is no longer in the queue; there is
                // nothing sane left to resume onto.
                PlaybackError?.Invoke("output device lost; playback stopped");
                PlaybackEnded?.Invoke();
                return;
            }

            long resumeFrame = pos.Frame;
            bool wasPlaying = pos.State is PlaybackState.Playing or PlaybackState.Stalled;

            _deviceOpen = false;
            if (!TryReopenDevice())
            {
                try
                {
                    _engine.Stop();
                }
                catch (AudioEngineException)
                {
                    // The device is already gone; nothing left to stop cleanly.
                }

                PlaybackError?.Invoke("output device lost; playback stopped");
                PlaybackEnded?.Invoke();
                return;
            }

            string? path = await _library.GetPathAsync(entry.TrackId);
            if (path is null || !File.Exists(path))
            {
                PlaybackError?.Invoke($"file missing for track {entry.TrackId}");
                PlaybackEnded?.Invoke();
                return;
            }

            try
            {
                // Reopening dropped the decode worker along with the old
                // stream, so the track is reloaded from where it left off
                // rather than resumed in place.
                PromoteCurrent(entry);
                _preloadedEntryId = null;
                _engine.Load(
                    AudioSlot.Current, path, (ulong)entry.EntryId, await GainForAsync(entry.TrackId),
                    startFrame: resumeFrame);
                if (wasPlaying)
                {
                    _engine.Play();
                }

                await PreloadNextAsync();
            }
            catch (AudioEngineException ex)
            {
                PlaybackError?.Invoke(ex.Message);
            }
        }
        finally
        {
            _recoveringDevice = false;
        }
    }

    // Tries the configured device first, then the system default. Returns
    // false only when neither is reachable.
    private bool TryReopenDevice()
    {
        AppSettings settings = _settings.Current;
        if (TryOpenDevice(settings.DeviceId, settings.Exclusive))
        {
            return true;
        }

        return settings.DeviceId is not null && TryOpenDevice(deviceId: null, settings.Exclusive);
    }

    private bool TryOpenDevice(string? deviceId, bool exclusive)
    {
        try
        {
            _engine!.OpenDevice(deviceId, exclusive);
            _deviceOpen = true;
            ApplyVolume();
            return true;
        }
        catch (AudioEngineException)
        {
            return false;
        }
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
        ApplyVolume();
    }

    private void StopForQueueEnded()
    {
        _preloadedEntryId = null;
        _timer.Stop();
        try
        {
            _engine?.Stop();
        }
        catch (AudioEngineException)
        {
            // Nothing meaningful to stop if the device is already gone.
        }

        // The queue already cleared its own current pointer (Remove); this
        // only broadcasts that fact to subscribers (e.g. the queue pane).
        CurrentEntryChanged?.Invoke(null);
        PlaybackEnded?.Invoke();
    }

    private void PromoteCurrent(QueueEntry entry)
    {
        _queue.SetCurrent(entry.EntryId);
        CurrentEntryChanged?.Invoke(entry.EntryId);
    }

    private void ClearCurrentEntry()
    {
        _queue.ClearCurrent();
        CurrentEntryChanged?.Invoke(null);
    }
}
