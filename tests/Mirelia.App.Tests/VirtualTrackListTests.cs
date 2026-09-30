using Mirelia.App.Models;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class VirtualTrackListTests
{
    [Fact]
    public void SetNowPlayingMarksEveryOccurrenceOfTheTrack()
    {
        VirtualTrackList tracks = MakeList(1, 2, 3, 2);

        tracks.SetNowPlaying(2, isPlaying: true);

        Assert.Equal(2L, tracks.NowPlayingId);
        Assert.Equal(
            [
                NowPlayingState.None,
                NowPlayingState.Playing,
                NowPlayingState.None,
                NowPlayingState.Playing,
            ],
            States(tracks));
    }

    [Fact]
    public void SetNowPlayingClearsThePreviouslyMarkedRows()
    {
        VirtualTrackList tracks = MakeList(1, 2, 3, 2);
        tracks.SetNowPlaying(2, isPlaying: true);

        tracks.SetNowPlaying(3, isPlaying: true);

        Assert.Equal(
            [
                NowPlayingState.None,
                NowPlayingState.None,
                NowPlayingState.Playing,
                NowPlayingState.None,
            ],
            States(tracks));
    }

    [Fact]
    public void SetNowPlayingWithTheSameIdOnlySwitchesPlayAndPause()
    {
        VirtualTrackList tracks = MakeList(1, 2, 3, 2);
        tracks.SetNowPlaying(2, isPlaying: true);

        tracks.SetNowPlaying(2, isPlaying: false);

        Assert.Equal(
            [
                NowPlayingState.None,
                NowPlayingState.Paused,
                NowPlayingState.None,
                NowPlayingState.Paused,
            ],
            States(tracks));

        tracks.SetNowPlaying(2, isPlaying: true);

        Assert.Equal(NowPlayingState.Playing, tracks[1].NowPlaying);
        Assert.Equal(NowPlayingState.Playing, tracks[3].NowPlaying);
    }

    [Fact]
    public void SetNowPlayingNullClearsTheMarker()
    {
        VirtualTrackList tracks = MakeList(1, 2, 3);
        tracks.SetNowPlaying(2, isPlaying: true);

        tracks.SetNowPlaying(null, isPlaying: false);

        Assert.Null(tracks.NowPlayingId);
        Assert.All(States(tracks), state => Assert.Equal(NowPlayingState.None, state));
    }

    [Fact]
    public void SetNowPlayingForATrackNotInTheViewMarksNothing()
    {
        VirtualTrackList tracks = MakeList(1, 2, 3);

        tracks.SetNowPlaying(99, isPlaying: true);

        Assert.Equal(99L, tracks.NowPlayingId);
        Assert.All(States(tracks), state => Assert.Equal(NowPlayingState.None, state));
    }

    [Fact]
    public void ResetKeepsTheNowPlayingMarker()
    {
        VirtualTrackList tracks = MakeList(1, 2, 3);
        tracks.SetNowPlaying(2, isPlaying: true);

        // Eviction resets a row's text, not the fact that its track is playing.
        tracks[1].Reset();

        Assert.Equal(NowPlayingState.Playing, tracks[1].NowPlaying);
        Assert.True(tracks[1].IsNowPlaying);
    }

    [Fact]
    public void IndexOfFindsTheFirstOccurrenceAtOrAfterTheStart()
    {
        VirtualTrackList tracks = MakeList(5, 6, 7, 6, 8);

        Assert.Equal(1, tracks.IndexOf(6));
        Assert.Equal(1, tracks.IndexOf(6, 1));
        Assert.Equal(3, tracks.IndexOf(6, 2));
        Assert.Equal(3, tracks.IndexOf(6, 3));
    }

    [Fact]
    public void IndexOfWrapsToTheStartOfTheList()
    {
        VirtualTrackList tracks = MakeList(5, 6, 7, 6, 8);

        Assert.Equal(0, tracks.IndexOf(5, 2));
        Assert.Equal(1, tracks.IndexOf(6, 4));
    }

    [Fact]
    public void IndexOfReturnsMinusOneForAMissingTrack()
    {
        VirtualTrackList tracks = MakeList(5, 6, 7);

        Assert.Equal(-1, tracks.IndexOf(99));
        Assert.Equal(-1, tracks.IndexOf(99, 2));
        Assert.Equal(-1, MakeList().IndexOf(1));
    }

    [Fact]
    public void IndexOfClampsAnOutOfRangeStart()
    {
        VirtualTrackList tracks = MakeList(5, 6, 7);

        Assert.Equal(2, tracks.IndexOf(7, -10));
        Assert.Equal(0, tracks.IndexOf(5, 3));
        Assert.Equal(0, tracks.IndexOf(5, 100));
        Assert.Equal(-1, tracks.IndexOf(99, 100));
    }

    private static NowPlayingState[] States(VirtualTrackList tracks)
        => [.. tracks.Select(row => row.NowPlaying)];

    private static VirtualTrackList MakeList(params long[] ids)
        => new(ids, (_, _) => { });
}
