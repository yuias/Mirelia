using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

/// <summary>A sort preset with a display label.</summary>
/// <param name="Order">The preset.</param>
/// <param name="Label">Shown in the sort selector.</param>
public sealed record SortChoice(TrackSortOrder Order, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// One selectable layout style for a track view.
/// </summary>
/// <param name="Key">Stable key persisted in view settings.</param>
/// <param name="Label">Shown in the layout selector.</param>
/// <param name="GroupField">
/// What the layout groups by, or null for the flat track grid.
/// </param>
/// <param name="Sorts">
/// The sorts this layout allows, or null to let the view keep its own list.
/// A grouped layout names its own so the order stays group-contiguous.
/// </param>
/// <param name="DefaultSort">Sort applied when the view's stored one does not fit.</param>
/// <param name="UnknownLabel">Header text for a group whose field is untagged.</param>
public sealed record LayoutSpec(
    string Key,
    string Label,
    TrackGroupField? GroupField,
    IReadOnlyList<SortChoice>? Sorts,
    TrackSortOrder DefaultSort,
    string UnknownLabel = "")
{
    /// <summary>True for layouts that render group blocks instead of rows.</summary>
    public bool IsGrouped => GroupField is not null;

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The layout registry; view settings reference keys. Adding a style is one
/// entry here plus, when it groups by something new, one
/// <see cref="TrackGroupField"/> member with its SQL mapping — the block
/// rendering itself is shared.
/// </summary>
public static class TrackLayouts
{
    /// <summary>The flat, column-based track grid: every view's default.</summary>
    public static readonly LayoutSpec Tracks =
        new("tracks", "Tracks", null, null, TrackSortOrder.Artist);

    /// <summary>Album blocks: cover, album header, then the album's tracks.</summary>
    public static readonly LayoutSpec Albums = new(
        "albums",
        "Albums",
        TrackGroupField.Album,
        [
            new(TrackSortOrder.Album, "Album Title"),
            new(TrackSortOrder.AlbumByYear, "Release"),
            new(TrackSortOrder.AlbumByRecentlyAdded, "Added"),
        ],
        TrackSortOrder.AlbumByYear,
        "Unknown Album");

    /// <summary>All layouts, in display order.</summary>
    public static readonly IReadOnlyList<LayoutSpec> All = [Tracks, Albums];

    /// <summary>
    /// Looks up a layout by key, falling back to <see cref="Tracks"/> — a key
    /// stored by a build that had a style this one does not must not leave the
    /// view unable to render.
    /// </summary>
    public static LayoutSpec Find(string? key)
        => All.FirstOrDefault(l => l.Key == key) ?? Tracks;
}
