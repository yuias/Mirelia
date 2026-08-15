using Mirelia.App.Models;
using Xunit;

namespace Mirelia.App.Tests;

public sealed class BlockSelectionTests
{
    private const int Count = 10;

    [Fact]
    public void APlainClickReplacesTheSelection()
    {
        var selection = new BlockSelection();

        selection.Select(3, toggle: false, extend: false, Count);
        selection.Select(5, toggle: false, extend: false, Count);

        Assert.Equal([5], selection.Indices);
        Assert.Equal(5, selection.FirstIndex);
        Assert.False(selection.Contains(3));
    }

    [Fact]
    public void CtrlClickAddsAndRemovesOneRow()
    {
        var selection = new BlockSelection();

        selection.Select(3, toggle: false, extend: false, Count);
        selection.Select(6, toggle: true, extend: false, Count);
        Assert.Equal([3, 6], selection.Indices);

        selection.Select(3, toggle: true, extend: false, Count);
        Assert.Equal([6], selection.Indices);
    }

    [Fact]
    public void ShiftClickSpansFromTheAnchorInEitherDirection()
    {
        var selection = new BlockSelection();

        selection.Select(4, toggle: false, extend: false, Count);
        selection.Select(7, toggle: false, extend: true, Count);
        Assert.Equal([4, 5, 6, 7], selection.Indices);

        selection.Select(2, toggle: false, extend: true, Count);
        Assert.Equal([2, 3, 4], selection.Indices);
    }

    [Fact]
    public void ShiftClickKeepsTheAnchorPut()
    {
        var selection = new BlockSelection();

        selection.Select(4, toggle: false, extend: false, Count);
        selection.Select(6, toggle: false, extend: true, Count);
        selection.Select(8, toggle: false, extend: true, Count);

        // Re-spanning from the original anchor, not walking it forward one
        // Shift-click at a time.
        Assert.Equal([4, 5, 6, 7, 8], selection.Indices);
    }

    [Fact]
    public void ShiftClickWithoutAnAnchorBehavesLikeAPlainClick()
    {
        var selection = new BlockSelection();

        selection.Select(3, toggle: false, extend: true, Count);

        Assert.Equal([3], selection.Indices);
    }

    [Fact]
    public void OutOfRangeClicksAreIgnored()
    {
        var selection = new BlockSelection();

        selection.Select(2, toggle: false, extend: false, Count);
        selection.Select(-1, toggle: false, extend: false, Count);
        selection.Select(Count, toggle: false, extend: false, Count);

        Assert.Equal([2], selection.Indices);
    }

    [Fact]
    public void SelectRangeTakesAWholeBlockAndAnchorsAtItsStart()
    {
        var selection = new BlockSelection();

        selection.SelectRange(2, 3);
        Assert.Equal([2, 3, 4], selection.Indices);

        selection.Select(6, toggle: false, extend: true, Count);
        Assert.Equal([2, 3, 4, 5, 6], selection.Indices);
    }

    [Fact]
    public void ChangedReportsOnlyRowsWhoseStateActuallyMoved()
    {
        var selection = new BlockSelection();
        var changes = new List<(int Index, bool Selected)>();

        selection.SelectRange(0, 3);
        selection.Changed += (index, selected) => changes.Add((index, selected));
        selection.SelectRange(2, 3);

        // Row 2 stays selected across the two ranges, so it must not be
        // repainted — and above all must not be reported as deselected.
        Assert.Equal([(0, false), (1, false), (3, true), (4, true)], changes);
    }

    [Fact]
    public void ResetDropsEverythingSilently()
    {
        var selection = new BlockSelection();
        var changes = new List<int>();

        selection.SelectRange(0, 3);
        selection.Changed += (index, _) => changes.Add(index);
        selection.Reset();

        // The list those indices addressed is being replaced, so there is no
        // row left to repaint.
        Assert.Empty(selection.Indices);
        Assert.Equal(-1, selection.FirstIndex);
        Assert.Empty(changes);
    }
}
