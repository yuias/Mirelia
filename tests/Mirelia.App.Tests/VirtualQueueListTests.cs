using Mirelia.App.Models;
using Mirelia.App.Services;
using Mirelia.Library.Querying;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class VirtualQueueListTests
{
    [Fact]
    public void RowsStartAsPlaceholdersAndNothingIsHydratedUpFront()
    {
        VirtualQueueList list = Make(500, out List<int> requested);

        // The whole point: queuing a big view must not query for all of it.
        Assert.Equal(500, list.Count);
        Assert.Empty(requested);
        Assert.Equal("…", list[0].Title);
        Assert.Equal(string.Empty, list[0].Artist);
    }

    [Fact]
    public void RealizingARowHydratesItsPageOnce()
    {
        VirtualQueueList list = Make(500, out List<int> requested);

        list.NotifyVisible(0);
        list.NotifyVisible(5);
        list.NotifyVisible(VirtualQueueList.PageSize);

        // Two distinct pages, and the in-flight page is not requested twice.
        Assert.Equal([0, 1], requested);
    }

    [Fact]
    public void ApplyingAPageFillsExactlyThatPage()
    {
        VirtualQueueList list = Make(300, out _);
        list.NotifyVisible(0);

        list.ApplyPage(0, Hydrated(1, 2));

        Assert.Equal("Track 1", list[0].Title);
        Assert.Equal("Artist 1", list[0].Artist);
        Assert.Equal("Track 2", list[1].Title);
        // A row the page did not cover stays a placeholder.
        Assert.Equal("…", list[VirtualQueueList.PageSize].Title);
    }

    [Fact]
    public void MissingTracksReadAsUnknownRatherThanStayingPlaceholders()
    {
        VirtualQueueList list = Make(3, out _);
        list.NotifyVisible(0);

        list.ApplyPage(0, Hydrated(1));

        Assert.Equal("Track 1", list[0].Title);
        Assert.Equal("(unknown)", list[1].Title);
    }

    [Fact]
    public void SetCurrentMovesTheMarkAndHydratesTheAudibleRow()
    {
        VirtualQueueList list = Make(500, out List<int> requested);

        QueueRowViewModel? first = list.SetCurrent(EntryId(0));
        Assert.NotNull(first);
        Assert.True(list[0].IsCurrent);

        QueueRowViewModel? later = list.SetCurrent(EntryId(300));
        Assert.NotNull(later);
        Assert.False(list[0].IsCurrent);
        Assert.True(list[300].IsCurrent);

        // The pane scrolls to the audible row, so its page is worth fetching
        // even when it was never realized.
        Assert.Equal([0, 2], requested);
    }

    [Fact]
    public void SetCurrentClearsTheMarkWhenNothingIsPlaying()
    {
        VirtualQueueList list = Make(10, out _);
        list.SetCurrent(EntryId(3));

        Assert.Null(list.SetCurrent(null));
        Assert.False(list[3].IsCurrent);
    }

    [Fact]
    public void AnUnqueuedEntryIdMarksNothing()
    {
        VirtualQueueList list = Make(10, out _);

        Assert.Null(list.SetCurrent(9_999));
        Assert.All(list, row => Assert.False(row.IsCurrent));
    }

    [Fact]
    public void HydratedPagesAreEvictedBackToPlaceholders()
    {
        int pages = 12; // above the list's hydrated-page window
        VirtualQueueList list = Make(VirtualQueueList.PageSize * pages, out _);

        for (int page = 0; page < pages; page++)
        {
            int row = page * VirtualQueueList.PageSize;
            list.NotifyVisible(row);
            list.ApplyPage(page, Hydrated(list[row].TrackId));
        }

        // The oldest page's text is released; the newest is still there.
        Assert.Equal("…", list[0].Title);
        Assert.NotEqual("…", list[(pages - 1) * VirtualQueueList.PageSize].Title);
    }

    [Fact]
    public void BandingAlternatesDownTheQueue()
    {
        VirtualQueueList list = Make(4, out _);

        Assert.Equal(
            [false, true, false, true],
            list.Select(r => r.IsAlternate));
    }

    private static long EntryId(int index) => 1000 + index;

    private static VirtualQueueList Make(int count, out List<int> requestedPages)
    {
        var pages = new List<int>();
        requestedPages = pages;
        QueueEntry[] entries =
        [
            .. Enumerable.Range(0, count).Select(i => new QueueEntry(EntryId(i), i + 1)),
        ];
        return new VirtualQueueList(entries, (page, _) => pages.Add(page));
    }

    private static Dictionary<long, TrackPageRow> Hydrated(params long[] trackIds)
        => trackIds.ToDictionary(
            id => id,
            id => new TrackPageRow(
                id, $"Track {id}", $"Artist {id}", null, null, null,
                null, null, null, 0, "flac", null, 0, null));
}
