using System.Globalization;
using System.Text;
using Mirelia.Query.Ast;
using Mirelia.Query.Text;

namespace Mirelia.Query.Compilation;

/// <summary>
/// Compiles a parsed query into one SQL statement selecting <c>track.id</c>.
/// Relative dates are resolved at compile time, so a compiled query is a
/// snapshot; recompile to re-evaluate <c>@now</c>.
/// </summary>
public static class SqlCompiler
{
    private const string FromClause =
        "FROM track t\n" +
        "LEFT JOIN artist ar ON ar.id = t.artist_id\n" +
        "LEFT JOIN artist aa ON aa.id = t.album_artist_id\n" +
        "LEFT JOIN artist comp ON comp.id = t.composer_id\n" +
        "LEFT JOIN album al ON al.id = t.album_id\n" +
        "LEFT JOIN genre g ON g.id = t.genre_id";

    private static readonly Dictionary<string, long> UnitSeconds = new()
    {
        ["s"] = 1,
        ["m"] = 60,
        ["h"] = 3600,
        ["d"] = 86_400,
        ["w"] = 604_800,
        ["mo"] = 2_592_000, // 30 days; calendar-aware arithmetic is post-v1
        ["y"] = 31_536_000, // 365 days
    };

    /// <summary>How deep <c>playlist =</c> may chase smart playlists.</summary>
    /// <remarks>
    /// Cycles are rejected when a playlist is saved, so this only catches a
    /// reference graph that got into the database some other way.
    /// </remarks>
    private const int MaxPlaylistDepth = 16;

    /// <summary>Compiles the query. <paramref name="now"/> anchors <c>@now</c>; defaults to the current time.</summary>
    /// <param name="query">The parsed query.</param>
    /// <param name="now">Anchor for <c>@now</c>.</param>
    /// <param name="smartPlaylists">
    /// Looks up a smart playlist's query by name, so <c>playlist =</c> can test
    /// membership of one. Without it only static playlists are visible, and a
    /// reference to a smart playlist matches nothing.
    /// </param>
    /// <exception cref="QueryCompileException">On unknown fields or type mismatches.</exception>
    public static CompiledQuery Compile(
        ParsedQuery query,
        DateTimeOffset? now = null,
        Func<string, ParsedQuery?>? smartPlaylists = null)
    {
        var ctx = new Context(now ?? DateTimeOffset.Now, smartPlaylists);
        string where = ctx.CompileExpr(query.Where);
        List<(string Expr, bool Desc)> order = CompileOrder(query.OrderBy);

        string sql = query.Limit is { ByField: not null } groupLimit
            ? BuildGroupLimited(ctx, where, order, groupLimit)
            : BuildPlain(where, order, query.Limit);

        return new CompiledQuery(sql, ctx.Parameters);
    }

    private static string BuildPlain(
        string where, List<(string Expr, bool Desc)> order, LimitClause? limit)
    {
        var sb = new StringBuilder();
        sb.Append("SELECT t.id\n").Append(FromClause).Append("\nWHERE ").Append(where);
        if (order.Count > 0)
        {
            sb.Append("\nORDER BY ").Append(string.Join(", ",
                order.Select(o => o.Expr + (o.Desc ? " DESC" : string.Empty))));
        }

        if (limit is not null)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\nLIMIT {limit.Count}");
        }

        return sb.ToString();
    }

    private static string BuildGroupLimited(
        Context ctx, string where, List<(string Expr, bool Desc)> order, LimitClause limit)
    {
        if (!FieldTable.TryGet(limit.ByField!, out FieldDef groupField))
        {
            throw new QueryCompileException($"Unknown field '{limit.ByField}' in LIMIT BY");
        }

        // Three levels so per-row expressions (random()) are evaluated once:
        // level 1 materializes order keys, level 2 ranks, level 3 filters.
        List<(string Expr, bool Desc)> effectiveOrder = order.Count > 0 ? order : [("t.id", false)];
        var level1 = new StringBuilder();
        level1.Append("SELECT t.id AS id, ").Append(groupField.EffectiveOrderColumn).Append(" AS grp");
        for (int i = 0; i < effectiveOrder.Count; i++)
        {
            level1.Append(CultureInfo.InvariantCulture, $", {effectiveOrder[i].Expr} AS o{i}");
        }

        level1.Append('\n').Append(FromClause).Append("\nWHERE ").Append(where);

        string rankOrder = string.Join(", ", effectiveOrder.Select(
            (o, i) => $"o{i}" + (o.Desc ? " DESC" : string.Empty)));

        return
            $"WITH matched AS (\n{level1}\n),\n" +
            $"ranked AS (\n" +
            $"SELECT id, {string.Join(", ", effectiveOrder.Select((_, i) => $"o{i}"))}, " +
            $"row_number() OVER (PARTITION BY grp ORDER BY {rankOrder}) AS rn\n" +
            $"FROM matched\n)\n" +
            $"SELECT id FROM ranked WHERE rn <= {limit.Count.ToString(CultureInfo.InvariantCulture)}\n" +
            $"ORDER BY {rankOrder}";
    }

    private static List<(string Expr, bool Desc)> CompileOrder(IReadOnlyList<OrderTerm> terms)
    {
        var result = new List<(string, bool)>();
        foreach (OrderTerm term in terms)
        {
            if (term.IsRandom)
            {
                result.Add(("random()", false));
                continue;
            }

            if (!FieldTable.TryGet(term.Field, out FieldDef field))
            {
                throw new QueryCompileException($"Unknown field '{term.Field}' in ORDER BY");
            }

            result.Add((field.EffectiveOrderColumn, term.Descending));
        }

        return result;
    }

    private sealed class Context(DateTimeOffset now, Func<string, ParsedQuery?>? smartPlaylists)
    {
        private readonly List<KeyValuePair<string, object>> _parameters = [];
        private int _playlistDepth;

        public IReadOnlyList<KeyValuePair<string, object>> Parameters => _parameters;

        private string AddParameter(object value)
        {
            string name = $"$p{_parameters.Count}";
            _parameters.Add(new KeyValuePair<string, object>(name, value));
            return name;
        }

        public string CompileExpr(QueryExpr expr) => expr switch
        {
            OrExpr or_ => "(" + string.Join(" OR ", or_.Operands.Select(CompileExpr)) + ")",
            AndExpr and => "(" + string.Join(" AND ", and.Operands.Select(CompileExpr)) + ")",
            NotExpr not => "NOT " + CompileExpr(not.Operand),
            Comparison c => CompileComparison(c),
            InList inList => CompileIn(inList),
            Between b => CompileBetween(b),
            PlaylistMembership p => CompilePlaylist(p),
            FreeText f => CompileFreeText(f),
            BareField bare => CompileBareField(bare),
            _ => throw new QueryCompileException($"Unknown expression node {expr.GetType().Name}"),
        };

        private static FieldDef Resolve(string name)
        {
            return FieldTable.TryGet(name, out FieldDef field)
                ? field
                : throw new QueryCompileException($"Unknown field '{name}'");
        }

        private string CompileComparison(Comparison c)
        {
            FieldDef field = Resolve(c.Field);

            if (c.Value is NullValue)
            {
                return c.Op switch
                {
                    CompareOp.Eq => $"{field.Column} IS NULL",
                    CompareOp.Ne => $"{field.Column} IS NOT NULL",
                    _ => throw new QueryCompileException(
                        $"null supports only '=' and '!=' (field '{field.Name}')"),
                };
            }

            if (field.Kind == FieldKind.DateKey
                && c.Op is CompareOp.Eq or CompareOp.Ne or CompareOp.Lt or CompareOp.Le
                    or CompareOp.Gt or CompareOp.Ge
                && TryDateKeyRange(c.Value, out long lo, out long hi))
            {
                // A partial date means its whole period: '=' is containment,
                // >=/< anchor at the period start, <=/> at its end.
                return c.Op switch
                {
                    CompareOp.Eq => lo == hi
                        ? $"{field.Column} = {AddParameter(lo)}"
                        : $"{field.Column} BETWEEN {AddParameter(lo)} AND {AddParameter(hi)}",
                    CompareOp.Ne => lo == hi
                        ? $"({field.Column} IS NULL OR {field.Column} != {AddParameter(lo)})"
                        : $"({field.Column} IS NULL OR {field.Column} NOT BETWEEN {AddParameter(lo)} AND {AddParameter(hi)})",
                    CompareOp.Lt => $"{field.Column} < {AddParameter(lo)}",
                    CompareOp.Ge => $"{field.Column} >= {AddParameter(lo)}",
                    CompareOp.Le => $"{field.Column} <= {AddParameter(hi)}",
                    _ => $"{field.Column} > {AddParameter(hi)}",
                };
            }

            switch (c.Op)
            {
                case CompareOp.Contains:
                case CompareOp.NotContains:
                case CompareOp.StartsWith:
                case CompareOp.EndsWith:
                    return CompileTextMatch(field, c.Op, c.Value);

                case CompareOp.Regex:
                    if (field.Kind != FieldKind.Text || c.Value is not StringValue pattern)
                    {
                        throw new QueryCompileException($"'~=' needs a text field and a string pattern");
                    }

                    return $"COALESCE({field.Column}, '') REGEXP {AddParameter(pattern.Value)}";

                case CompareOp.Eq:
                    return $"{field.Column} = {AddParameter(ToSqlValue(field, c.Value))}";

                case CompareOp.Ne:
                    // NULL columns should count as "not equal", matching user intuition.
                    string p = AddParameter(ToSqlValue(field, c.Value));
                    return $"({field.Column} IS NULL OR {field.Column} != {p})";

                case CompareOp.Lt:
                case CompareOp.Le:
                case CompareOp.Gt:
                case CompareOp.Ge:
                    string op = c.Op switch
                    {
                        CompareOp.Lt => "<",
                        CompareOp.Le => "<=",
                        CompareOp.Gt => ">",
                        _ => ">=",
                    };
                    return $"{field.Column} {op} {AddParameter(ToSqlValue(field, c.Value))}";

                default:
                    throw new QueryCompileException($"Unsupported operator {c.Op}");
            }
        }

        private string CompileTextMatch(FieldDef field, CompareOp op, QueryValue value)
        {
            if (field.Kind != FieldKind.Text || value is not StringValue s)
            {
                throw new QueryCompileException(
                    $"'{field.Name}' does not support text matching, or the value is not a string");
            }

            string search = field.SearchColumn ?? $"lower({field.Column})";
            string needle = EscapeLike(TextNormalizer.NormalizeSearch(s.Value));
            string pattern = op switch
            {
                CompareOp.StartsWith => needle + "%",
                CompareOp.EndsWith => "%" + needle,
                _ => "%" + needle + "%",
            };
            string param = AddParameter(pattern);
            string like = $"COALESCE({search}, '') LIKE {param} ESCAPE '\\'";
            return op == CompareOp.NotContains ? "NOT " + like : like;
        }

        private string CompileIn(InList inList)
        {
            FieldDef field = Resolve(inList.Field);
            if (field.Kind == FieldKind.DateKey)
            {
                // Each element covers its period, so IN is an OR of ranges.
                IEnumerable<string> terms = inList.Values.Select(v =>
                    TryDateKeyRange(v, out long lo, out long hi)
                        ? lo == hi
                            ? $"{field.Column} = {AddParameter(lo)}"
                            : $"{field.Column} BETWEEN {AddParameter(lo)} AND {AddParameter(hi)}"
                        : throw new QueryCompileException(
                            $"Field '{field.Name}' takes YYYY, YYYY-MM, or YYYY-MM-DD values"));
                return "(" + string.Join(" OR ", terms) + ")";
            }

            IEnumerable<string> parameters =
                inList.Values.Select(v => AddParameter(ToSqlValue(field, v)));
            return $"{field.Column} IN ({string.Join(", ", parameters)})";
        }

        private string CompileBetween(Between b)
        {
            FieldDef field = Resolve(b.Field);
            if (field.Kind == FieldKind.DateKey
                && TryDateKeyRange(b.Low, out long dateLow, out _)
                && TryDateKeyRange(b.High, out _, out long dateHigh))
            {
                // Bounds are inclusive periods: BETWEEN 2024 AND 2025-06
                // runs from 2024-01-01 through 2025-06-30.
                return $"{field.Column} BETWEEN {AddParameter(dateLow)} AND {AddParameter(dateHigh)}";
            }

            string low = AddParameter(ToSqlValue(field, b.Low));
            string high = AddParameter(ToSqlValue(field, b.High));
            return $"{field.Column} BETWEEN {low} AND {high}";
        }

        /// <summary>
        /// The yyyymmdd period a literal covers on a date-key field: a bare
        /// year number is the whole year, YYYY-MM the whole month, a full
        /// date a single day. Uniform "-31" month ends are safe because
        /// keys come from real dates — nothing falls between the 28th/30th
        /// and the 31st.
        /// </summary>
        private static bool TryDateKeyRange(QueryValue value, out long start, out long end)
        {
            switch (value)
            {
                case NumberValue n when decimal.IsInteger(n.Value) && n.Value is > 0 and < 10000:
                    long year = (long)n.Value;
                    start = (year * 100 + 1) * 100 + 1;
                    end = (year * 100 + 12) * 100 + 31;
                    return true;

                case DateTimeValue dt:
                    DateTimeOffset d = dt.Value;
                    // The source spelling carries the precision: one dash
                    // is YYYY-MM, more is a full date (time uses ':').
                    if (dt.Source.Count(ch => ch == '-') == 1)
                    {
                        start = (d.Year * 100L + d.Month) * 100 + 1;
                        end = (d.Year * 100L + d.Month) * 100 + 31;
                    }
                    else
                    {
                        start = end = (d.Year * 100L + d.Month) * 100 + d.Day;
                    }

                    return true;

                default:
                    start = end = 0;
                    return false;
            }
        }

        private string CompilePlaylist(PlaylistMembership p)
        {
            if (smartPlaylists?.Invoke(p.PlaylistName) is { } referenced)
            {
                return CompileSmartPlaylist(p, referenced);
            }

            string param = AddParameter(p.PlaylistName);
            string exists =
                "EXISTS (SELECT 1 FROM playlist_entry pe " +
                "JOIN playlist pl ON pl.id = pe.playlist_id " +
                $"WHERE pe.track_id = t.id AND pl.name = {param})";
            return p.Negated ? "NOT " + exists : exists;
        }

        /// <summary>
        /// A smart playlist keeps no entries, so its membership is its own
        /// predicate, inlined here over the same row.
        /// </summary>
        private string CompileSmartPlaylist(PlaylistMembership p, ParsedQuery referenced)
        {
            // A limit picks rows out of an ordered result. There is no row set
            // to order inside a predicate about one track, so pretending
            // otherwise would quietly widen the reference.
            if (referenced.Limit is not null)
            {
                throw new QueryCompileException(
                    $"Playlist '{p.PlaylistName}' is limited and cannot be referenced.");
            }

            if (++_playlistDepth > MaxPlaylistDepth)
            {
                throw new QueryCompileException(
                    $"Playlist references nest more than {MaxPlaylistDepth} deep at '{p.PlaylistName}'.");
            }

            try
            {
                string inner = CompileExpr(referenced.Where);
                return p.Negated ? $"NOT ({inner})" : $"({inner})";
            }
            finally
            {
                _playlistDepth--;
            }
        }

        private string CompileFreeText(FreeText f)
        {
            // Phrase-quote so FTS operators in user text are inert.
            string phrase = "\"" + f.Text.Replace("\"", "\"\"") + "\"";
            string param = AddParameter(phrase);
            return $"t.id IN (SELECT rowid FROM track_fts WHERE track_fts MATCH {param})";
        }

        private static string CompileBareField(BareField bare)
        {
            FieldDef field = Resolve(bare.Field);
            return field.Kind == FieldKind.Bool
                ? $"{field.Column} != 0"
                : throw new QueryCompileException(
                    $"Field '{field.Name}' is not boolean and cannot stand alone");
        }

        private object ToSqlValue(FieldDef field, QueryValue value)
        {
            switch (value)
            {
                case StringValue s:
                    return field.Kind == FieldKind.Text
                        ? s.Value
                        : throw new QueryCompileException(
                            $"Field '{field.Name}' is not text; got a string");

                case NumberValue n:
                    return field.Kind switch
                    {
                        // Bare numbers on duration fields mean seconds.
                        FieldKind.Duration => (object)(long)(n.Value * 1000m),
                        FieldKind.Text => throw new QueryCompileException(
                            $"Field '{field.Name}' is text; got a number"),
                        // Reached when TryDateKeyRange rejected the number
                        // (8-digit yyyymmdd, fractions, out-of-range years).
                        FieldKind.DateKey => throw new QueryCompileException(
                            $"Field '{field.Name}' takes YYYY, YYYY-MM, or YYYY-MM-DD values"),
                        // Box explicitly so integers stay long instead of
                        // being unified to double by the conditional.
                        _ => decimal.IsInteger(n.Value)
                            ? (object)(long)n.Value
                            : (object)(double)n.Value,
                    };

                case DurationValue d:
                    return field.Kind == FieldKind.Duration
                        ? (long)(d.Amount * UnitSeconds[d.Unit] * 1000)
                        : throw new QueryCompileException(
                            $"Field '{field.Name}' does not accept durations");

                case DateTimeValue dt:
                    return field.Kind == FieldKind.DateTime
                        ? dt.Value.ToUnixTimeSeconds()
                        : throw new QueryCompileException(
                            $"Field '{field.Name}' does not accept dates");

                case RelativeDate r:
                    if (field.Kind != FieldKind.DateTime)
                    {
                        throw new QueryCompileException(
                            $"Field '{field.Name}' does not accept @now arithmetic");
                    }

                    long offset = r.Sign == 0 ? 0 : r.Sign * (long)(r.Amount * UnitSeconds[r.Unit]);
                    return now.ToUnixTimeSeconds() + offset;

                default:
                    throw new QueryCompileException($"Unsupported value {value.GetType().Name}");
            }
        }

        private static string EscapeLike(string s)
            => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
