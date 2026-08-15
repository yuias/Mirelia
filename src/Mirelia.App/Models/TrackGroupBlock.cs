using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

/// <summary>One disc's rows inside a block, with the caption above them.</summary>
public sealed class DiscSection
{
    internal DiscSection(int? discNo, IReadOnlyList<TrackRow> rows)
    {
        Caption = discNo is { } no ? $"Disc {no}" : string.Empty;
        Rows = rows;
    }

    /// <summary>"Disc 2", or empty when the tracks carry no disc number.</summary>
    public string Caption { get; }

    /// <summary>False for untagged discs, so the caption row stays out of the way.</summary>
    public bool HasCaption => Caption.Length > 0;

    /// <summary>The section's rows, in view order.</summary>
    public IReadOnlyList<TrackRow> Rows { get; }
}

/// <summary>
/// A <see cref="TrackGroupRun"/> presented as one block of the grouped
/// layout: a header, a cover, and the run's rows split by disc. The rows are
/// the owning <see cref="VirtualTrackList"/>'s own objects, so they hydrate
/// and evict exactly as the flat grid's do.
/// </summary>
public sealed class TrackGroupBlock
{
    private readonly VirtualTrackList _tracks;

    internal TrackGroupBlock(
        VirtualTrackList tracks, TrackGroupRun run, string unknownLabel)
    {
        _tracks = tracks;
        Run = run;
        Title = FormatTitle(run, unknownLabel);
        Subtitle = FormatSubtitle(run);
        DurationText = FormatDuration(run.DurationMs);
        Discs =
        [
            .. run.Discs.Select(disc => new DiscSection(
                disc.DiscNo,
                Slice(tracks, disc.StartIndex, disc.Count))),
        ];
    }

    /// <summary>The run this block renders.</summary>
    public TrackGroupRun Run { get; }

    /// <summary>Header line: "Album Artist - Album", or the group's own name.</summary>
    public string Title { get; }

    /// <summary>Second header line: release year, genre and artist.</summary>
    public string Subtitle { get; }

    /// <summary>Total playing time, as MusicBee shows it on an album header.</summary>
    public string DurationText { get; }

    /// <summary>The block's rows, split into disc sections.</summary>
    public IReadOnlyList<DiscSection> Discs { get; }

    /// <summary>Index of the block's first row in the owning track list.</summary>
    public int StartIndex => Run.StartIndex;

    /// <summary>Number of rows in the block.</summary>
    public int Count => Run.Count;

    /// <summary>
    /// Reports that the block was realized on screen, so its rows hydrate.
    /// A block can span more than one hydration page, so every page it
    /// touches is announced, not just its first row.
    /// </summary>
    public void NotifyVisible()
    {
        int end = Run.StartIndex + Run.Count - 1;
        for (int i = Run.StartIndex; i < end; i += VirtualTrackList.PageSize)
        {
            _tracks.NotifyVisible(i);
        }

        _tracks.NotifyVisible(end);
    }

    private static TrackRow[] Slice(VirtualTrackList tracks, int start, int count)
    {
        var rows = new TrackRow[count];
        for (int i = 0; i < count; i++)
        {
            rows[i] = tracks[start + i];
        }

        return rows;
    }

    private static string FormatTitle(TrackGroupRun run, string unknownLabel)
    {
        return (run.Subtitle, run.Title) switch
        {
            ({ } artist, { } title) => $"{artist} - {title}",
            (_, { } title) => title,
            ({ } artist, null) => artist,
            _ => unknownLabel,
        };
    }

    private static string FormatSubtitle(TrackGroupRun run)
    {
        string?[] parts = [run.Year?.ToString(), run.Genre, run.Artist];
        return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    private static string FormatDuration(long durationMs)
    {
        // Whole minutes throughout: an album header is a rough sense of
        // length, not a running time to read seconds off.
        long minutes = (long)Math.Round(durationMs / 60000.0);
        return minutes == 1 ? "1 min" : $"{minutes} mins";
    }
}

/// <summary>Builds the block list a grouped layout binds to.</summary>
public static class TrackGroupBlocks
{
    /// <summary>
    /// Wraps <paramref name="runs"/> around <paramref name="tracks"/>. The
    /// runs must have been built from the same id array, in the same order.
    /// </summary>
    public static List<TrackGroupBlock> Build(
        VirtualTrackList tracks, IReadOnlyList<TrackGroupRun> runs, LayoutSpec layout)
    {
        var blocks = new List<TrackGroupBlock>(runs.Count);
        foreach (TrackGroupRun run in runs)
        {
            blocks.Add(new TrackGroupBlock(tracks, run, layout.UnknownLabel));
        }

        return blocks;
    }
}
