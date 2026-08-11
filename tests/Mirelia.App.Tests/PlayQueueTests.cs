using Mirelia.App.Services;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class PlayQueueTests
{
    [Fact]
    public void ReplaceSetsEntriesAndCurrent()
    {
        var queue = new PlayQueue();
        IReadOnlyList<QueueEntry> entries = queue.Replace([10, 20, 30], startIndex: 1);

        Assert.Equal(3, queue.Count);
        Assert.Equal([10L, 20L, 30L], entries.Select(e => e.TrackId));
        Assert.Equal(20L, queue.Current!.Value.TrackId);
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Fact]
    public void ReplaceClampsOutOfRangeIndex()
    {
        var queue = new PlayQueue();
        queue.Replace([10, 20], startIndex: 99);
        Assert.Equal(20L, queue.Current!.Value.TrackId);

        queue.Replace([10, 20], startIndex: -5);
        Assert.Equal(10L, queue.Current!.Value.TrackId);
    }

    [Fact]
    public void ReplaceWithEmptyQueueClearsCurrent()
    {
        var queue = new PlayQueue();
        queue.Replace([10], startIndex: 0);
        queue.Replace([], startIndex: 0);

        Assert.Equal(0, queue.Count);
        Assert.Null(queue.Current);
        Assert.Equal(-1, queue.CurrentIndex);
    }

    [Fact]
    public void EachEntryGetsAUniqueIdEvenForDuplicateTracks()
    {
        var queue = new PlayQueue();
        queue.Replace([7, 7, 7], startIndex: 0);

        long[] entryIds = [.. queue.Entries.Select(e => e.EntryId)];
        Assert.Equal(3, entryIds.Distinct().Count());
        Assert.All(queue.Entries, e => Assert.Equal(7L, e.TrackId));
    }

    [Fact]
    public void InsertNextInsertsImmediatelyAfterCurrentInOrder()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 0);

        IReadOnlyList<QueueEntry> inserted = queue.InsertNext([100, 101]);

        Assert.Equal([1L, 100L, 101L, 2L, 3L], queue.Entries.Select(e => e.TrackId));
        Assert.Equal(2, inserted.Count);
        // The current entry (track 1) is untouched by the insert.
        Assert.Equal(1L, queue.Current!.Value.TrackId);
    }

    [Fact]
    public void InsertNextWithNoCurrentInsertsAtFront()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2], startIndex: 0);
        queue.ClearCurrent();

        queue.InsertNext([9]);

        Assert.Equal([9L, 1L, 2L], queue.Entries.Select(e => e.TrackId));
    }

    [Fact]
    public void AppendAddsToTheEndWithoutTouchingCurrent()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2], startIndex: 1);

        queue.Append([50, 51]);

        Assert.Equal([1L, 2L, 50L, 51L], queue.Entries.Select(e => e.TrackId));
        Assert.Equal(2L, queue.Current!.Value.TrackId);
    }

    [Fact]
    public void RemoveOtherEntryLeavesCurrentPointerAlone()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 1);
        long removedId = queue.Entries[0].EntryId; // track 1, not current

        QueueRemoveResult result = queue.Remove(removedId);

        Assert.Equal(QueueRemoveResult.RemovedOther, result);
        Assert.Equal([2L, 3L], queue.Entries.Select(e => e.TrackId));
        Assert.Equal(2L, queue.Current!.Value.TrackId);
    }

    [Fact]
    public void RemoveCurrentPromotesTheFollowingEntry()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 1); // current = track 2
        long currentId = queue.Current!.Value.EntryId;

        QueueRemoveResult result = queue.Remove(currentId);

        Assert.Equal(QueueRemoveResult.RemovedCurrentHasNext, result);
        Assert.Equal([1L, 3L], queue.Entries.Select(e => e.TrackId));
        Assert.Equal(3L, queue.Current!.Value.TrackId);
    }

    [Fact]
    public void RemoveCurrentLastEntryEndsTheQueue()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2], startIndex: 1); // current = last entry
        long currentId = queue.Current!.Value.EntryId;

        QueueRemoveResult result = queue.Remove(currentId);

        Assert.Equal(QueueRemoveResult.RemovedCurrentQueueEnded, result);
        Assert.Equal([1L], queue.Entries.Select(e => e.TrackId));
        Assert.Null(queue.Current);
    }

    [Fact]
    public void RemoveUnknownEntryIsANoOp()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2], startIndex: 0);

        Assert.Equal(QueueRemoveResult.NotFound, queue.Remove(entryId: 999));
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void RemoveDisambiguatesDuplicateTracksByEntryId()
    {
        var queue = new PlayQueue();
        queue.Replace([5, 5], startIndex: 0); // two entries, same track id
        long firstEntryId = queue.Entries[0].EntryId;
        long secondEntryId = queue.Entries[1].EntryId;

        queue.Remove(firstEntryId);

        Assert.Single(queue.Entries);
        Assert.Equal(secondEntryId, queue.Entries[0].EntryId);
    }

    [Fact]
    public void MoveReordersRelativeToTheOriginalIndex()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3, 4], startIndex: 0);
        long trackTwoEntryId = queue.Entries[1].EntryId;

        bool moved = queue.Move(trackTwoEntryId, newIndex: 3);

        Assert.True(moved);
        // newIndex=3 is track 4's slot in the pre-move order [1,2,3,4]; track
        // 2 lands right after it once its own removal shifts things down.
        Assert.Equal([1L, 3L, 2L, 4L], queue.Entries.Select(e => e.TrackId));
    }

    [Fact]
    public void MoveKeepsTrackingTheSameEntryAsCurrentAcrossReorder()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 0); // current = track 1
        long currentEntryId = queue.Current!.Value.EntryId;

        queue.Move(currentEntryId, newIndex: 2);

        Assert.Equal([2L, 1L, 3L], queue.Entries.Select(e => e.TrackId));
        Assert.Equal(currentEntryId, queue.Current!.Value.EntryId);
        Assert.Equal(1, queue.CurrentIndex);
    }

    [Fact]
    public void MoveToSameIndexIsANoOp()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 0);
        long entryId = queue.Entries[1].EntryId;

        Assert.False(queue.Move(entryId, newIndex: 1));
    }

    [Fact]
    public void MoveUnknownEntryReturnsFalse()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2], startIndex: 0);

        Assert.False(queue.Move(entryId: 999, newIndex: 0));
    }

    [Fact]
    public void ClearKeepsOnlyTheCurrentEntry()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 1); // current = track 2

        queue.Clear();

        Assert.Single(queue.Entries);
        Assert.Equal(2L, queue.Current!.Value.TrackId);
        Assert.Equal(0, queue.CurrentIndex);
    }

    [Fact]
    public void ClearWithNoCurrentEmptiesTheQueue()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2], startIndex: 0);
        queue.ClearCurrent();

        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.Null(queue.Current);
    }

    [Fact]
    public void NextAndPreviousEntryReflectPositionAroundCurrent()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 1);

        Assert.Equal(1L, queue.PreviousEntry!.Value.TrackId);
        Assert.Equal(3L, queue.NextEntry!.Value.TrackId);

        queue.Replace([1, 2, 3], startIndex: 0);
        Assert.Null(queue.PreviousEntry);

        queue.Replace([1, 2, 3], startIndex: 2);
        Assert.Null(queue.NextEntry);
    }

    [Fact]
    public void SetCurrentMovesThePointerToAnExistingEntry()
    {
        var queue = new PlayQueue();
        queue.Replace([1, 2, 3], startIndex: 0);
        long thirdEntryId = queue.Entries[2].EntryId;

        Assert.True(queue.SetCurrent(thirdEntryId));
        Assert.Equal(3L, queue.Current!.Value.TrackId);
        Assert.False(queue.SetCurrent(999));
    }

    [Fact]
    public void FindReturnsNullForUnknownEntry()
    {
        var queue = new PlayQueue();
        queue.Replace([1], startIndex: 0);

        Assert.NotNull(queue.Find(queue.Entries[0].EntryId));
        Assert.Null(queue.Find(999));
    }
}
