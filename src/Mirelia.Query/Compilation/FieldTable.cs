namespace Mirelia.Query.Compilation;

/// <summary>Value type a field carries, driving literal interpretation.</summary>
public enum FieldKind
{
    /// <summary>Free text; supports =~ family via a normalized shadow column.</summary>
    Text,

    /// <summary>Plain number.</summary>
    Number,

    /// <summary>0/1 flag; usable bare as a predicate.</summary>
    Bool,

    /// <summary>Unix seconds; accepts date literals and @now arithmetic.</summary>
    DateTime,

    /// <summary>
    /// yyyymmdd integer date key. Partial dates (bare YYYY number, YYYY-MM
    /// literal) cover their whole period in comparisons.
    /// </summary>
    DateKey,

    /// <summary>Milliseconds; accepts duration literals (bare numbers = seconds).</summary>
    Duration,
}

/// <summary>One entry of the field vocabulary (docs/query-dsl.md).</summary>
/// <param name="Name">Canonical DSL name.</param>
/// <param name="Kind">Value type.</param>
/// <param name="Column">SQL expression for the raw value, using the fixed aliases.</param>
/// <param name="SearchColumn">Normalized shadow column for =~ family; null = fall back to lower(raw).</param>
/// <param name="OrderColumn">SQL expression used in ORDER BY; defaults to <paramref name="Column"/>.</param>
public sealed record FieldDef(
    string Name,
    FieldKind Kind,
    string Column,
    string? SearchColumn = null,
    string? OrderColumn = null)
{
    /// <summary>The expression ORDER BY should use for this field.</summary>
    public string EffectiveOrderColumn => OrderColumn ?? Column;
}

/// <summary>
/// The fixed field vocabulary, mapped to columns of the schema in
/// docs/schema.md. Adding a field is one line here plus a doc row.
/// Aliases: t=track, ar=artist, aa=album artist, comp=composer, al=album, g=genre.
/// </summary>
public static class FieldTable
{
    private static readonly Dictionary<string, FieldDef> Fields = new[]
    {
        new FieldDef("title", FieldKind.Text, "t.title", "t.title_search", "t.title_sort"),
        new FieldDef("artist", FieldKind.Text, "ar.name", "ar.name_search", "ar.name_sort"),
        new FieldDef("albumartist", FieldKind.Text, "aa.name", "aa.name_search", "aa.name_sort"),
        new FieldDef("composer", FieldKind.Text, "comp.name", "comp.name_search", "comp.name_sort"),
        new FieldDef("album", FieldKind.Text, "al.name", "al.name_search", "al.name_sort"),
        new FieldDef("genre", FieldKind.Text, "g.name", "g.name_search", "g.name_sort"),
        new FieldDef("year", FieldKind.Number, "t.year"),
        new FieldDef("date", FieldKind.DateKey, "t.date_key"),
        new FieldDef("rating", FieldKind.Number, "t.rating"),
        new FieldDef("playcount", FieldKind.Number, "t.play_count"),
        new FieldDef("skipcount", FieldKind.Number, "t.skip_count"),
        new FieldDef("lastplayed", FieldKind.DateTime, "t.last_played_unix"),
        new FieldDef("added", FieldKind.DateTime, "t.added_unix"),
        new FieldDef("modified", FieldKind.DateTime, "t.modified_unix"),
        new FieldDef("duration", FieldKind.Duration, "t.duration_ms"),
        new FieldDef("samplerate", FieldKind.Number, "t.sample_rate"),
        new FieldDef("bitdepth", FieldKind.Number, "t.bit_depth"),
        new FieldDef("bitrate", FieldKind.Number, "t.bitrate_kbps"),
        new FieldDef("codec", FieldKind.Text, "t.codec"),
        new FieldDef("is_lossless", FieldKind.Bool, "t.is_lossless"),
        new FieldDef("trackno", FieldKind.Number, "t.track_no"),
        new FieldDef("discno", FieldKind.Number, "t.disc_no"),
        new FieldDef("path", FieldKind.Text, "t.path", "t.path_lower"),
    }.ToDictionary(f => f.Name);

    /// <summary>All fields, for editors and documentation tooling.</summary>
    public static IReadOnlyCollection<FieldDef> All => Fields.Values;

    /// <summary>Looks up a field by canonical name.</summary>
    public static bool TryGet(string name, out FieldDef field)
        => Fields.TryGetValue(name, out field!);
}
