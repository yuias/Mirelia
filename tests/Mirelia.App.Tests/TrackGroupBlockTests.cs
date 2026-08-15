using Mirelia.App.Models;
using Mirelia.Library.Querying;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class TrackGroupBlockTests
{
    [Fact]
    public void BlocksSliceTheTrackListByRunAndDisc()
    {
        VirtualTrackList tracks = MakeList(5, out _);
        TrackGroupRun run = Run(startIndex: 1, count: 3, discs:
        [
            new DiscSpan(1, 1, 2),
            new DiscSpan(2, 3, 1),
        ]);

        TrackGroupBlock block = Assert.Single(
            Blocks(tracks, run));

        Assert.Equal(1, block.StartIndex);
        Assert.Equal(3, block.Count);
        Assert.Equal(2, block.Discs.Count);
        // The block holds the list's own row objects, not copies, so
        // hydration reaches what is on screen.
        Assert.Same(tracks[1], block.Discs[0].Rows[0]);
        Assert.Same(tracks[2], block.Discs[0].Rows[1]);
        Assert.Same(tracks[3], block.Discs[1].Rows[0]);
        Assert.Equal("Disc 1", block.Discs[0].Caption);
        Assert.True(block.Discs[0].HasCaption);
    }

    [Fact]
    public void UntaggedDiscsShowNoCaption()
    {
        VirtualTrackList tracks = MakeList(2, out _);
        TrackGroupRun run = Run(0, 2, [new DiscSpan(null, 0, 2)]);

        DiscSection disc = Assert.Single(
            Blocks(tracks, run)[0].Discs);

        Assert.False(disc.HasCaption);
        Assert.Equal(string.Empty, disc.Caption);
    }

    [Fact]
    public void NotifyVisibleHydratesEveryPageTheBlockTouches()
    {
        int size = (VirtualTrackList.PageSize * 2) + 10;
        VirtualTrackList tracks = MakeList(size, out List<int> requested);
        TrackGroupRun run = Run(0, size, [new DiscSpan(1, 0, size)]);

        Blocks(tracks, run)[0].NotifyVisible();

        // An album longer than one hydration page must not leave its tail as
        // placeholders, so every page it spans is announced.
        Assert.Equal([0, 1, 2], requested);
    }

    [Fact]
    public void NotifyVisibleHandlesASingleRowBlock()
    {
        VirtualTrackList tracks = MakeList(1, out List<int> requested);
        TrackGroupRun run = Run(0, 1, [new DiscSpan(null, 0, 1)]);

        Blocks(tracks, run)[0].NotifyVisible();

        Assert.Equal([0], requested);
    }

    [Theory]
    [InlineData("Genshin", "Vol.5", "Genshin - Vol.5")]
    [InlineData(null, "Vol.5", "Vol.5")]
    [InlineData("Genshin", null, "Genshin")]
    [InlineData(null, null, "Unknown Album")]
    public void TheHeaderNamesTheGroupOrSaysItIsUnknown(
        string? albumArtist, string? album, string expected)
    {
        VirtualTrackList tracks = MakeList(1, out _);
        TrackGroupRun run = Run(0, 1, [new DiscSpan(null, 0, 1)]) with
        {
            Title = album,
            Subtitle = albumArtist,
        };

        Assert.Equal(
            expected,
            Blocks(tracks, run)[0].Title);
    }

    [Fact]
    public void TheSecondHeaderLineSkipsUntaggedFields()
    {
        VirtualTrackList tracks = MakeList(1, out _);
        TrackGroupRun full = Run(0, 1, [new DiscSpan(null, 0, 1)]) with
        {
            Year = 2026,
            Genre = "Game",
            Artist = "HOYO-MiX",
        };
        TrackGroupRun sparse = full with { Genre = null, Artist = null };
        TrackGroupRun bare = full with { Year = null, Genre = null, Artist = null };

        Assert.Equal(
            "2026 · Game · HOYO-MiX",
            Blocks(tracks, full)[0].Subtitle);
        Assert.Equal(
            "2026",
            Blocks(tracks, sparse)[0].Subtitle);
        Assert.Equal(
            string.Empty,
            Blocks(tracks, bare)[0].Subtitle);
    }

    [Theory]
    [InlineData(0, "0 mins")]
    [InlineData(60_000, "1 min")]
    [InlineData(1_860_000, "31 mins")]
    public void TheHeaderShowsWholeMinutes(long durationMs, string expected)
    {
        VirtualTrackList tracks = MakeList(1, out _);
        TrackGroupRun run = Run(0, 1, [new DiscSpan(null, 0, 1)]) with
        {
            DurationMs = durationMs,
        };

        Assert.Equal(
            expected,
            Blocks(tracks, run)[0].DurationText);
    }

    [Fact]
    public void BandingAlternatesThroughTheWholeBlock()
    {
        VirtualTrackList tracks = MakeList(5, out _);
        TrackGroupRun run = Run(0, 5, [new DiscSpan(1, 0, 3), new DiscSpan(2, 3, 2)]);

        Blocks(tracks, run);

        // Banding is continuous across the disc boundary: restarting it there
        // would put two shaded rows next to each other.
        Assert.Equal(
            [false, true, false, true, false],
            Enumerable.Range(0, 5).Select(i => tracks[i].IsAlternate));
    }

    [Fact]
    public void ACoverIsRequestedOnceHoweverOftenTheBlockIsRealized()
    {
        VirtualTrackList tracks = MakeList(1, out _);
        var requested = new List<long>();
        TrackGroupBlock block = TrackGroupBlocks.Build(
            tracks,
            [Run(0, 1, [new DiscSpan(null, 0, 1)])],
            TrackLayouts.Albums,
            b => requested.Add(b.Run.CoverTrackId))[0];

        block.NotifyVisible();
        block.NotifyVisible();

        // Scrolling re-realizes a block constantly; each re-realization must
        // not start another file read.
        Assert.Equal([1L], requested);
    }

    private static List<TrackGroupBlock> Blocks(VirtualTrackList tracks, TrackGroupRun run)
        => TrackGroupBlocks.Build(tracks, [run], TrackLayouts.Albums, _ => { });

    private static VirtualTrackList MakeList(int count, out List<int> requestedPages)
    {
        var pages = new List<int>();
        requestedPages = pages;
        long[] ids = [.. Enumerable.Range(1, count).Select(i => (long)i)];
        return new VirtualTrackList(ids, (page, _) => pages.Add(page));
    }

    private static TrackGroupRun Run(int startIndex, int count, DiscSpan[] discs)
        => new(
            GroupId: 1,
            Title: "Album",
            Subtitle: null,
            Artist: null,
            Genre: null,
            Year: null,
            StartIndex: startIndex,
            Count: count,
            DurationMs: 0,
            MaxAddedUnix: 0,
            CoverTrackId: 1,
            Discs: discs);
}
