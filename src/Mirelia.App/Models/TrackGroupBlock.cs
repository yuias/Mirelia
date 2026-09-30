using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Mirelia.App.Services;
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
public sealed partial class TrackGroupBlock : ObservableObject
{
    private readonly VirtualTrackList _tracks;
    private readonly Action<TrackGroupBlock> _requestCover;
    private bool _coverRequested;

    /// <summary>
    /// The block's cover once it has been loaded. Null both before the load
    /// and when the group has no artwork at all; the placeholder glyph behind
    /// it covers either case.
    ///
    /// The block owns this bitmap outright — nothing else holds it, and
    /// <see cref="ReleaseCover"/> disposes it. Sharing decoded bitmaps through
    /// a cache is what made cover memory unbounded before: Skia's pixel
    /// buffers are native and invisible to the GC, so an owner has to free
    /// them explicitly, which is only safe when there is exactly one owner.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _cover;

    internal TrackGroupBlock(
        VirtualTrackList tracks,
        TrackGroupRun run,
        LayoutSpec layout,
        Action<TrackGroupBlock> requestCover)
    {
        _tracks = tracks;
        _requestCover = requestCover;
        Run = run;

        // The cover belongs to the group, not to the track it happens to be
        // read from — one album keeps one cached cover however many of its
        // tracks are on screen. A run with no group of its own falls back to
        // its cover track, which is the only identity it has.
        (CoverScope, CoverId) = run.GroupId == TrackGrouper.NoGroup
            ? (CoverScope.Track, run.CoverTrackId)
            : (layout.GroupField switch
            {
                TrackGroupField.AlbumArtist => CoverScope.AlbumArtist,
                _ => CoverScope.Album,
            }, run.GroupId);
        Title = FormatTitle(run, layout.UnknownLabel);
        Subtitle = FormatSubtitle(run);
        DurationText = FormatDuration(run.DurationMs);
        Discs =
        [
            .. run.Discs.Select(disc => new DiscSection(
                disc.DiscNo,
                Slice(tracks, disc.StartIndex, disc.Count, run.StartIndex))),
        ];
    }

    /// <summary>The run this block renders.</summary>
    public TrackGroupRun Run { get; }

    /// <summary>What the block's cover is cached under.</summary>
    public CoverScope CoverScope { get; }

    /// <summary>Id of the group (or track) the cover is cached under.</summary>
    public long CoverId { get; }

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
    /// Identifies the current realization. Bumped by
    /// <see cref="ReleaseCover"/> so a decode still in flight when the block
    /// scrolls away cannot hand its bitmap to a block nobody is showing.
    /// </summary>
    public int CoverToken { get; private set; }

    /// <summary>
    /// Reports that the block was realized on screen, so its rows hydrate and
    /// its cover loads. A block can span more than one hydration page, so
    /// every page it touches is announced, not just its first row.
    /// </summary>
    public void NotifyVisible()
    {
        // One request per realization: scrolling within a realized block must
        // not re-decode, and scrolling back to it must.
        if (!_coverRequested)
        {
            _coverRequested = true;
            _requestCover(this);
        }

        int end = Run.StartIndex + Run.Count - 1;
        for (int i = Run.StartIndex; i < end; i += VirtualTrackList.PageSize)
        {
            _tracks.NotifyVisible(i);
        }

        _tracks.NotifyVisible(end);
    }

    /// <summary>
    /// Takes a freshly decoded cover, unless the block has been recycled since
    /// it was asked for — in which case the bitmap is disposed here rather
    /// than being attached to a block nobody is showing.
    /// </summary>
    public void TrySetCover(Bitmap? cover, int token)
    {
        if (token != CoverToken || Cover is not null)
        {
            cover?.Dispose();
            return;
        }

        Cover = cover;
    }

    /// <summary>
    /// Frees the cover when the block scrolls out of view. Called from the
    /// list's container-recycling hook: without it, every block ever shown
    /// would keep its pixels for the lifetime of the view.
    /// </summary>
    public void ReleaseCover()
    {
        CoverToken++;
        _coverRequested = false;
        Bitmap? cover = Cover;
        Cover = null;
        cover?.Dispose();
    }

    private static TrackRow[] Slice(
        VirtualTrackList tracks, int start, int count, int runStart)
    {
        var rows = new TrackRow[count];
        for (int i = 0; i < count; i++)
        {
            TrackRow row = tracks[start + i];
            // Banding runs through the whole block, so it does not restart
            // mid-album at a disc boundary.
            row.IsAlternate = (start + i - runStart) % 2 == 1;
            rows[i] = row;
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
    /// <param name="requestCover">
    /// Called once per block when it first becomes visible; the callee
    /// eventually sets <see cref="TrackGroupBlock.Cover"/>.
    /// </param>
    public static List<TrackGroupBlock> Build(
        VirtualTrackList tracks,
        IReadOnlyList<TrackGroupRun> runs,
        LayoutSpec layout,
        Action<TrackGroupBlock> requestCover)
    {
        var blocks = new List<TrackGroupBlock>(runs.Count);
        foreach (TrackGroupRun run in runs)
        {
            blocks.Add(new TrackGroupBlock(tracks, run, layout, requestCover));
        }

        return blocks;
    }

    /// <summary>
    /// Index of the block whose [StartIndex, StartIndex + Count) contains
    /// <paramref name="rowIndex"/>; -1 if none.
    /// </summary>
    public static int IndexOfBlockContaining(IReadOnlyList<TrackGroupBlock> blocks, int rowIndex)
    {
        // Last block starting at or before the row.
        int low = 0;
        int high = blocks.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            if (blocks[mid].StartIndex <= rowIndex)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (found < 0 || rowIndex < 0)
        {
            return -1;
        }

        TrackGroupBlock block = blocks[found];
        return rowIndex < block.StartIndex + block.Count ? found : -1;
    }
}
