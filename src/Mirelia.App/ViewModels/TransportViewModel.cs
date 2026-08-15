using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirelia.App.Services;
using Mirelia.AudioBridge;

namespace Mirelia.App.ViewModels;

/// <summary>Transport bar state: now playing, position slider, volume.</summary>
public sealed partial class TransportViewModel : ObservableObject
{
    private readonly AudioPlayerService _player;
    private readonly SettingsService _settings;

    // Dragging the slider fires a change per pixel; only the settled level
    // is written to disk.
    private readonly DispatcherTimer _volumeSaveDebounce;

    private bool _updatingFromPlayback;

    [ObservableProperty]
    private string _nowPlayingTitle = string.Empty;

    [ObservableProperty]
    private string _nowPlayingArtist = string.Empty;

    [ObservableProperty]
    private string _nowPlayingAlbum = string.Empty;

    [ObservableProperty]
    private Avalonia.Media.Imaging.Bitmap? _artwork;

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _totalText = "0:00";

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _totalSeconds = 1;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private double _volume = 1.0;

    /// <summary>Creates the view-model and subscribes to player updates.</summary>
    public TransportViewModel(AudioPlayerService player, SettingsService settings)
    {
        _player = player;
        _settings = settings;

        // Direct field assignment: the restored level must not immediately
        // write itself back out through OnVolumeChanged.
        _volume = Math.Clamp(settings.Current.Volume, 0.0, 1.0);

        _volumeSaveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _volumeSaveDebounce.Tick += (_, _) => FlushVolume();

        _player.PositionChanged += OnPosition;
    }

    /// <summary>
    /// Writes a volume change still inside the debounce window, so closing
    /// the app right after moving the slider does not lose the new level.
    /// </summary>
    public void FlushVolume()
    {
        _volumeSaveDebounce.Stop();
        if (Math.Abs(_settings.Current.Volume - Volume) > 0.0001)
        {
            _settings.Save(_settings.Current with { Volume = Volume });
        }
    }

    /// <summary>Shows the audible track's metadata and artwork.</summary>
    public void SetNowPlaying(
        string? title, string? artist, string? album, Avalonia.Media.Imaging.Bitmap? artwork)
    {
        NowPlayingTitle = title ?? string.Empty;
        NowPlayingArtist = artist ?? string.Empty;
        NowPlayingAlbum = album ?? string.Empty;

        // Not disposed on replacement: the artwork cache owns these bitmaps
        // and may still be handing the same one to an album block.
        Artwork = artwork;
    }

    [RelayCommand]
    private void PlayPause() => _player.TogglePause();

    [RelayCommand]
    private Task Next() => _player.NextAsync();

    [RelayCommand]
    private Task Previous() => _player.PreviousAsync();

    partial void OnPositionSecondsChanged(double value)
    {
        if (!_updatingFromPlayback)
        {
            _player.Seek(value);
        }
    }

    partial void OnVolumeChanged(double value)
    {
        _player.SetVolume(value);
        _volumeSaveDebounce.Stop();
        _volumeSaveDebounce.Start();
    }

    private void OnPosition(PlaybackPosition pos)
    {
        if (pos.SampleRate == 0)
        {
            return;
        }

        _updatingFromPlayback = true;
        try
        {
            double seconds = (double)pos.Frame / pos.SampleRate;
            PositionSeconds = seconds;
            PositionText = Format(seconds);
            if (pos.TotalFrames > 0)
            {
                TotalSeconds = (double)pos.TotalFrames / pos.SampleRate;
                TotalText = Format(TotalSeconds);
            }

            IsPlaying = pos.State is PlaybackState.Playing or PlaybackState.Stalled;
        }
        finally
        {
            _updatingFromPlayback = false;
        }
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }
}
