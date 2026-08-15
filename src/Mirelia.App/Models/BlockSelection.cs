namespace Mirelia.App.Models;

/// <summary>
/// The grouped layout's row selection: an index set over the current track
/// list plus the anchor a Shift-click spans from.
///
/// This exists only for the grouped layout — the flat grid keeps the
/// DataGrid's own selection, which already implements the platform's
/// gestures. Row objects are not touched here; <see cref="Changed"/> reports
/// which index changed so the owner can repaint it.
/// </summary>
public sealed class BlockSelection
{
    private readonly SortedSet<int> _indices = [];
    private int _anchor = -1;

    /// <summary>Raised per index whose selected state changed.</summary>
    public event Action<int, bool>? Changed;

    /// <summary>Selected row indices, ascending.</summary>
    public IReadOnlyCollection<int> Indices => _indices;

    /// <summary>Lowest selected index, or -1 when nothing is selected.</summary>
    public int FirstIndex => _indices.Count > 0 ? _indices.Min : -1;

    /// <summary>Whether a row is selected.</summary>
    public bool Contains(int index) => _indices.Contains(index);

    /// <summary>
    /// Click on a row of a list of <paramref name="count"/> rows:
    /// <paramref name="toggle"/> is Ctrl (add or remove that one row),
    /// <paramref name="extend"/> is Shift (span from the anchor).
    /// </summary>
    public void Select(int index, bool toggle, bool extend, int count)
    {
        if (index < 0 || index >= count)
        {
            return;
        }

        if (extend && _anchor >= 0)
        {
            int from = Math.Min(_anchor, index);
            int to = Math.Max(_anchor, index);
            // The anchor stays put, so a second Shift-click re-spans from it
            // instead of walking away one click at a time.
            Replace(Enumerable.Range(from, to - from + 1));
            return;
        }

        if (toggle)
        {
            if (_indices.Remove(index))
            {
                Changed?.Invoke(index, false);
            }
            else if (_indices.Add(index))
            {
                Changed?.Invoke(index, true);
            }
        }
        else
        {
            Replace([index]);
        }

        _anchor = index;
    }

    /// <summary>Selects a whole run of rows — a group header was clicked.</summary>
    public void SelectRange(int start, int count)
    {
        Replace(Enumerable.Range(start, count));
        _anchor = start;
    }

    /// <summary>
    /// Drops everything without reporting changes, for when the list the
    /// indices address is being replaced anyway.
    /// </summary>
    public void Reset()
    {
        _indices.Clear();
        _anchor = -1;
    }

    private void Replace(IEnumerable<int> indices)
    {
        // A set, not a list: selecting a whole album of a long view diffs
        // against everything previously selected.
        HashSet<int> previous = [.. _indices];
        _indices.Clear();
        _indices.UnionWith(indices);

        foreach (int index in previous)
        {
            if (!_indices.Contains(index))
            {
                Changed?.Invoke(index, false);
            }
        }

        foreach (int index in _indices)
        {
            if (!previous.Contains(index))
            {
                Changed?.Invoke(index, true);
            }
        }
    }
}
