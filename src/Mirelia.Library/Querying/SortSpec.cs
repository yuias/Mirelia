namespace Mirelia.Library.Querying;

/// <summary>One term of a sort order: a field, and which way it runs.</summary>
/// <param name="Field">A key from <see cref="SortFields"/>.</param>
/// <param name="Descending">True for descending.</param>
public sealed record SortTerm(string Field, bool Descending);

/// <summary>
/// An ordered list of sort terms. Presets, header-click sorts and
/// user-defined orders are all this one shape, so they compile through one
/// path instead of three.
/// </summary>
/// <param name="Terms">The terms, most significant first.</param>
public sealed record SortSpec(IReadOnlyList<SortTerm> Terms)
{
    /// <summary>Builds a single-term spec.</summary>
    public static SortSpec Of(string field, bool descending = false)
        => new([new SortTerm(field, descending)]);
}

/// <summary>
/// A field a sort term may name, and how it reaches SQL.
/// </summary>
/// <param name="Key">Stable key, persisted in sort definitions.</param>
/// <param name="Label">Shown in the sort-order editor.</param>
/// <param name="Sql">The expression to order by.</param>
/// <param name="Nullable">
/// True when the value may be missing, so valueless rows need pinning last in
/// both directions rather than clumping at whichever end NULL happens to sort.
/// </param>
/// <param name="AlbumWide">
/// True for fields that describe the album rather than the track — they
/// aggregate over the album even in an ungrouped view, which is what makes
/// "albums by release date" differ from "tracks by release date".
/// </param>
public sealed record SortField(
    string Key, string Label, string Sql, bool Nullable, bool AlbumWide = false);

/// <summary>
/// The sortable-field registry. Sort definitions reference keys, so adding a
/// field is one entry here and nothing else.
/// </summary>
public static class SortFields
{
    /// <summary>All sortable fields, in display order.</summary>
    public static readonly IReadOnlyList<SortField> All =
    [
        new("title", "Title", "t.title_sort", false),
        new("artist", "Artist", "ar.name_sort", true),
        new("albumartist", "Album Artist", "aa.name_sort", true),
        new("album", "Album", "al.name_sort", true),
        new("genre", "Genre", "g.name_sort", true),
        new("date", "Release Date", "t.date_key", true),
        new("albumdate", "Release Date (Album)", "t.date_key", true, AlbumWide: true),
        new("added", "Date Added", "t.added_unix", false),
        new("albumadded", "Date Added (Album)", "t.added_unix", true, AlbumWide: true),
        new("discno", "Disc Number", "t.disc_no", true),
        new("trackno", "Track Number", "t.track_no", true),
        new("time", "Time", "t.duration_ms", false),
        new("rating", "Rating", "t.rating", true),
        new("bitrate", "Bitrate", "t.bitrate_kbps", true),
        new("codec", "Codec", "t.codec", false),
    ];

    /// <summary>Looks up a field by key, or null when the key is unknown.</summary>
    public static SortField? Find(string key) => All.FirstOrDefault(f => f.Key == key);
}

/// <summary>
/// Turns a <see cref="SortSpec"/> into a SQL ORDER BY clause. Sorting always
/// happens in SQL (kickoff §4.3), so this is the only place a sort order
/// becomes an ordering.
/// </summary>
public static class SortCompiler
{
    // Appended after the spec's own terms so rows the spec cannot separate
    // still land in a stable, listener-sensible order. In a grouped layout
    // these order the rows inside a group, which is exactly what a block wants.
    private static readonly string[] Tail =
        ["al.name_sort", "t.disc_no", "t.track_no", "t.title_sort", "t.id"];

    /// <summary>
    /// Compiles a spec, optionally for a grouped layout.
    /// </summary>
    /// <param name="spec">The sort order.</param>
    /// <param name="group">
    /// The layout's group field, or null for the flat grid. A grouped layout
    /// can only render group-contiguous orders, so every term is aggregated
    /// over the group: whatever the term says, all of a group's rows get the
    /// same key and the group stays in one piece.
    /// </param>
    public static string Compile(SortSpec spec, TrackGroupField? group = null)
    {
        string? groupPartition = group switch
        {
            TrackGroupField.Album => "t.album_id",
            TrackGroupField.AlbumArtist => "COALESCE(t.album_artist_id, t.artist_id)",
            _ => null,
        };

        var parts = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (SortTerm term in spec.Terms)
        {
            if (SortFields.Find(term.Field) is not { } field)
            {
                continue; // A field this build does not have: skip, do not fail the view.
            }

            // An album-wide field aggregates over the album when nothing else
            // groups the view; a grouped layout's own partition wins, so the
            // two never nest (SQLite forbids a window function inside one).
            string? partition = groupPartition ?? (field.AlbumWide ? "t.album_id" : null);
            string expression = partition is null
                ? field.Sql
                : $"MAX({field.Sql}) OVER (PARTITION BY {partition})";

            if (!used.Add(expression))
            {
                continue; // Already ordered by this; repeating it changes nothing.
            }

            if (field.Nullable)
            {
                parts.Add($"{expression} IS NULL");
            }

            parts.Add(term.Descending ? $"{expression} DESC" : expression);
        }

        // Two groups can share every key (same release date, same import
        // second, same name); without this the tail would interleave their
        // rows and cut both into pieces.
        if (groupPartition is not null)
        {
            parts.Add(groupPartition);
        }

        foreach (string tie in Tail)
        {
            if (used.Add(tie))
            {
                parts.Add(tie);
            }
        }

        return string.Join(", ", parts);
    }
}

/// <summary>
/// The built-in sort orders. These are the ones the user cannot edit or
/// delete, so they live in code rather than in the library database.
/// </summary>
public static class SortPresets
{
    /// <summary>
    /// The spec behind a preset, or null for
    /// <see cref="TrackSortOrder.PlaylistOrder"/>, which is not an ordering
    /// over fields at all but the view's own input order.
    /// </summary>
    public static SortSpec? SpecFor(TrackSortOrder order) => order switch
    {
        TrackSortOrder.Artist => SortSpec.Of("artist"),
        TrackSortOrder.Album => SortSpec.Of("album"),
        TrackSortOrder.Title => SortSpec.Of("title"),
        TrackSortOrder.RecentlyAdded => SortSpec.Of("added", descending: true),
        TrackSortOrder.Year => SortSpec.Of("date"),
        TrackSortOrder.AlbumByYear => SortSpec.Of("albumdate"),
        TrackSortOrder.AlbumByRecentlyAdded => SortSpec.Of("albumadded", descending: true),
        TrackSortOrder.AlbumArtist => SortSpec.Of("albumartist"),
        _ => null,
    };
}
