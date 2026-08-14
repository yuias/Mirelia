using System.Globalization;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;

namespace Mirelia.Library.Import;

/// <summary>The outcome of translating one auto-playlist.</summary>
/// <param name="Query">The translated query, or null when anything failed.</param>
/// <param name="Problems">
/// Why it failed, one line per cause. Empty exactly when
/// <paramref name="Query"/> is set.
/// </param>
public sealed record AutoPlaylistTranslation(
    ParsedQuery? Query, IReadOnlyList<string> Problems);

/// <summary>
/// Turns MusicBee auto-playlist rules into the query DSL.
/// </summary>
/// <remarks>
/// A rule that only partly translates is not translated at all. Half a
/// predicate silently selects the wrong tracks, which is worse than importing
/// what the playlist held at export time and saying so.
/// </remarks>
public static class AutoPlaylistTranslator
{
    /// <summary>MusicBee field names to the DSL vocabulary.</summary>
    private static readonly Dictionary<string, string> FieldMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Title"] = "title",
            ["TrackTitle"] = "title",
            ["Artist"] = "artist",
            ["AlbumArtist"] = "albumartist",
            ["Album"] = "album",
            ["Composer"] = "composer",
            ["Genre"] = "genre",
            ["Year"] = "year",
            ["Rating"] = "rating",
            ["PlayCount"] = "playcount",
            ["SkipCount"] = "skipcount",
            ["LastPlayed"] = "lastplayed",
            ["FileDateAdded"] = "added",
            ["FileDateModified"] = "modified",
            ["TrackNo"] = "trackno",
            ["DiscNo"] = "discno",
            ["Bitrate"] = "bitrate",
            ["SampleRate"] = "samplerate",
            ["Kind"] = "codec",
            ["FileType"] = "codec",
            ["FilePath"] = "path",
            ["Filename"] = "path",
            ["Time"] = "duration",
            ["Duration"] = "duration",
        };

    /// <summary>MusicBee comparison names to DSL operators.</summary>
    private static readonly Dictionary<string, CompareOp> OperatorMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Is"] = CompareOp.Eq,
            ["IsNot"] = CompareOp.Ne,
            ["Contains"] = CompareOp.Contains,
            ["DoesNotContain"] = CompareOp.NotContains,
            ["StartsWith"] = CompareOp.StartsWith,
            ["EndsWith"] = CompareOp.EndsWith,
            ["MatchesRegEx"] = CompareOp.Regex,
            ["MatchesRegExIgnoreCase"] = CompareOp.Regex,
            ["GreaterThan"] = CompareOp.Gt,
            ["IsGreaterThan"] = CompareOp.Gt,
            ["LessThan"] = CompareOp.Lt,
            ["IsLessThan"] = CompareOp.Lt,
            ["IsGreaterOrEqual"] = CompareOp.Ge,
            ["IsLessOrEqual"] = CompareOp.Le,
        };

    /// <summary>
    /// MusicBee's relative-time units to the DSL's. Only the month unit
    /// differs, and dangerously so: <c>m</c> means months to MusicBee and
    /// minutes to the DSL.
    /// </summary>
    private static readonly Dictionary<string, string> UnitMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["d"] = "d",
            ["w"] = "w",
            ["m"] = "mo",
            ["y"] = "y",
        };

    /// <summary>Translates one auto-playlist.</summary>
    /// <param name="source">The rules, as read from the <c>.xautopf</c> file.</param>
    /// <param name="resolve">
    /// Expands a MusicBee playlist reference — which may name a folder — into
    /// the Mirelia playlist names it covers. An unknown reference resolves to
    /// nothing, and the translation fails.
    /// </param>
    public static AutoPlaylistTranslation Translate(
        MusicBeeAutoPlaylist source, Func<string, IReadOnlyList<string>> resolve)
    {
        var problems = new List<string>();
        var terms = new List<QueryExpr>();

        // A source playlist narrows whatever the conditions select, so it
        // is ANDed on regardless of how the conditions combine.
        if (source.SourcePlaylists.Count > 0
            && Membership(source.SourcePlaylists, negated: false, resolve, problems) is { } sources)
        {
            terms.Add(sources);
        }

        var conditions = new List<QueryExpr>();
        foreach (MusicBeeCondition condition in source.Conditions)
        {
            if (ToExpr(condition, resolve, problems) is { } expr)
            {
                conditions.Add(expr);
            }
        }

        if (conditions.Count > 0)
        {
            terms.Add(source.MatchAll ? All(conditions) : Any(conditions));
        }

        if (terms.Count == 0 && problems.Count == 0)
        {
            problems.Add("the playlist has no conditions to translate");
        }

        if (problems.Count > 0)
        {
            return new AutoPlaylistTranslation(null, problems);
        }

        return new AutoPlaylistTranslation(
            new ParsedQuery(
                All(terms),
                [],
                source.Limit is { } limit ? new LimitClause(limit, null) : null),
            []);
    }

    private static QueryExpr All(List<QueryExpr> operands)
        => operands.Count == 1 ? operands[0] : new AndExpr(operands);

    private static QueryExpr Any(List<QueryExpr> operands)
        => operands.Count == 1 ? operands[0] : new OrExpr(operands);

    private static QueryExpr? ToExpr(
        MusicBeeCondition condition,
        Func<string, IReadOnlyList<string>> resolve,
        List<string> problems)
    {
        if (string.Equals(condition.Field, "Playlists", StringComparison.OrdinalIgnoreCase))
        {
            bool negated = condition.Comparison.Contains("Not", StringComparison.OrdinalIgnoreCase);
            return Membership(condition.Values, negated, resolve, problems);
        }

        if (!FieldMap.TryGetValue(condition.Field, out string? field))
        {
            problems.Add($"no field matches MusicBee's '{condition.Field}'");
            return null;
        }

        if (!FieldTable.TryGet(field, out FieldDef definition))
        {
            problems.Add($"the field '{field}' is not in the query vocabulary");
            return null;
        }

        switch (condition.Comparison)
        {
            case "IsNull":
                return new Comparison(field, CompareOp.Eq, new NullValue());
            case "IsNotNull":
                return new Comparison(field, CompareOp.Ne, new NullValue());

            case "InRange" when condition.Values.Count == 2:
                QueryValue? low = Literal(condition.Values[0], definition, problems);
                QueryValue? high = Literal(condition.Values[1], definition, problems);
                return low is null || high is null ? null : new Between(field, low, high);

            case "InTheLast":
            case "NotInTheLast":
                return Recent(field, condition, problems);
        }

        if (!OperatorMap.TryGetValue(condition.Comparison, out CompareOp op))
        {
            problems.Add($"no operator matches MusicBee's '{condition.Comparison}'");
            return null;
        }

        if (condition.Values.Count == 0)
        {
            problems.Add($"'{condition.Field} {condition.Comparison}' carries no value");
            return null;
        }

        if (condition.Values.Count == 1)
        {
            QueryValue? value = Literal(condition.Values[0], definition, problems);
            return value is null ? null : new Comparison(field, op, value);
        }

        // The "In" family: several operands against one field.
        var alternatives = new List<QueryExpr>();
        foreach (string raw in condition.Values)
        {
            if (Literal(raw, definition, problems) is not { } value)
            {
                return null;
            }

            alternatives.Add(new Comparison(field, op, value));
        }

        bool negatedList = op is CompareOp.Ne or CompareOp.NotContains;
        return negatedList ? All(alternatives) : Any(alternatives);
    }

    /// <summary>
    /// <c>InTheLast 100d</c> and its negation, as an offset from <c>@now</c>.
    /// </summary>
    private static QueryExpr? Recent(
        string field, MusicBeeCondition condition, List<string> problems)
    {
        if (condition.Values.Count != 1 || Offset(condition.Values[0]) is not { } offset)
        {
            problems.Add(
                $"'{condition.Field} {condition.Comparison}' has no readable period");
            return null;
        }

        bool negated = condition.Comparison.StartsWith("Not", StringComparison.Ordinal);
        return new Comparison(field, negated ? CompareOp.Lt : CompareOp.Ge, offset);
    }

    private static RelativeDate? Offset(string raw)
    {
        string trimmed = raw.Trim();
        int digits = 0;
        while (digits < trimmed.Length && char.IsAsciiDigit(trimmed[digits]))
        {
            digits++;
        }

        if (digits == 0
            || !decimal.TryParse(
                trimmed[..digits], CultureInfo.InvariantCulture, out decimal amount)
            || !UnitMap.TryGetValue(trimmed[digits..], out string? unit))
        {
            return null;
        }

        return new RelativeDate(-1, amount, unit);
    }

    private static QueryExpr? Membership(
        IReadOnlyList<string> references,
        bool negated,
        Func<string, IReadOnlyList<string>> resolve,
        List<string> problems)
    {
        var tests = new List<QueryExpr>();
        foreach (string reference in references)
        {
            IReadOnlyList<string> targets = resolve(reference);
            if (targets.Count == 0)
            {
                problems.Add($"no playlist matches the reference '{reference}'");
                return null;
            }

            foreach (string target in targets)
            {
                tests.Add(new PlaylistMembership(negated, target));
            }
        }

        if (tests.Count == 0)
        {
            return null;
        }

        // "In none of these" is every test holding at once; "in any of these"
        // is one of them.
        return negated ? All(tests) : Any(tests);
    }

    private static QueryValue? Literal(string raw, FieldDef field, List<string> problems)
    {
        switch (field.Kind)
        {
            case FieldKind.Text:
                return new StringValue(raw);

            case FieldKind.Number:
            case FieldKind.DateKey:
                if (decimal.TryParse(raw, CultureInfo.InvariantCulture, out decimal number))
                {
                    return new NumberValue(number);
                }

                break;

            case FieldKind.Duration:
                // MusicBee counts a track's length in seconds.
                if (decimal.TryParse(raw, CultureInfo.InvariantCulture, out decimal seconds))
                {
                    return new DurationValue(seconds, "s");
                }

                break;

            case FieldKind.DateTime:
                if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out DateTimeOffset moment))
                {
                    return new DateTimeValue(moment, moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }

                break;

            case FieldKind.Bool:
                return new NumberValue(
                    raw.Equals("True", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        }

        problems.Add($"'{raw}' is not a {field.Kind} the field '{field.Name}' accepts");
        return null;
    }
}
