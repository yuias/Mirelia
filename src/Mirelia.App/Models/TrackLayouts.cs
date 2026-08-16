using Mirelia.Library.Database;
using Mirelia.Library.Querying;

namespace Mirelia.App.Models;

/// <summary>
/// One entry of the sort selector: a built-in preset or a user-defined order.
/// </summary>
/// <param name="Label">Shown in the sort selector.</param>
/// <param name="Preset">The built-in preset, or null for a user-defined order.</param>
/// <param name="CustomId">The user-defined order's id, or null for a preset.</param>
/// <param name="Spec">
/// How it orders, or null to keep the view's own order (Playlist Order).
/// </param>
public sealed record SortChoice(
    string Label, TrackSortOrder? Preset, long? CustomId, SortSpec? Spec)
{
    /// <summary>Builds the choice for a built-in preset.</summary>
    public static SortChoice Of(TrackSortOrder preset, string label)
        => new(label, preset, null, SortPresets.SpecFor(preset));

    /// <summary>Builds the choice for a user-defined order.</summary>
    public static SortChoice Of(SortOrderInfo custom)
        => new(custom.Name, null, custom.Id, custom.Spec);

    /// <summary>True when this keeps the view's own order instead of imposing one.</summary>
    public bool IsSourceOrder => Spec is null;

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
/// The presets this layout offers, or null to let the view keep its own list.
/// A grouped layout names its own because only some presets say anything
/// about a block; contiguity itself is the compiler's job, not this list's,
/// so user-defined orders are appended whatever a layout names here.
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
            SortChoice.Of(TrackSortOrder.Album, "Album Title"),
            SortChoice.Of(TrackSortOrder.AlbumByYear, "Release"),
            SortChoice.Of(TrackSortOrder.AlbumByRecentlyAdded, "Added"),
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
