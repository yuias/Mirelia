using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mirelia.App.Services;
using Mirelia.AudioBridge;

namespace Mirelia.App.ViewModels;

/// <summary>Transport bar state: now playing, position slider, volume.</summary>
public sealed partial class TransportViewModel : ObservableObject
{
    private readonly AudioPlayerService _player;
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
    public TransportViewModel(AudioPlayerService player)
    {
        _player = player;
        _player.PositionChanged += OnPosition;
    }

    /// <summary>Shows the audible track's metadata and artwork.</summary>
    public void SetNowPlaying(
        string? title, string? artist, string? album, Avalonia.Media.Imaging.Bitmap? artwork)
    {
        NowPlayingTitle = title ?? string.Empty;
        NowPlayingArtist = artist ?? string.Empty;
        NowPlayingAlbum = album ?? string.Empty;

        Avalonia.Media.Imaging.Bitmap? previous = Artwork;
        Artwork = artwork;
        previous?.Dispose();
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

    partial void OnVolumeChanged(double value) => _player.SetVolume(value);

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
