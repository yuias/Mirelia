using System.Globalization;
using System.Text;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;

namespace Mirelia.Query.Builder;

/// <summary>Operators the condition builder offers, including bool tests.</summary>
public enum ConditionOp
{
    /// <summary><c>=</c>.</summary>
    Eq,

    /// <summary><c>!=</c>.</summary>
    Ne,

    /// <summary><c>&lt;</c>.</summary>
    Lt,

    /// <summary><c>&lt;=</c>.</summary>
    Le,

    /// <summary><c>&gt;</c>.</summary>
    Gt,

    /// <summary><c>&gt;=</c>.</summary>
    Ge,

    /// <summary><c>=~</c> — contains.</summary>
    Contains,

    /// <summary><c>!~</c> — does not contain.</summary>
    NotContains,

    /// <summary><c>^=</c> — starts with.</summary>
    StartsWith,

    /// <summary><c>$=</c> — ends with.</summary>
    EndsWith,

    /// <summary><c>~=</c> — regex match.</summary>
    Regex,

    /// <summary>Bare bool field (<c>is_lossless</c>); no value.</summary>
    IsTrue,

    /// <summary>Negated bare bool field (<c>NOT is_lossless</c>); no value.</summary>
    IsFalse,
}

/// <summary>One builder row: field, operator, value text as typed.</summary>
/// <param name="Field">Canonical field name.</param>
/// <param name="Op">The operator.</param>
/// <param name="ValueText">
/// Raw value text. Text-kind fields take it literally (quoted on print);
/// other kinds take a DSL literal (<c>90s</c>, <c>@now - 30d</c>,
/// <c>null</c>, …). Ignored for the bool operators.
/// </param>
public sealed record SimpleCondition(string Field, ConditionOp Op, string ValueText);

/// <summary>
/// The GUI condition builder's flat model: conditions joined by all/any,
/// plus optional order and limit. The builder and the advanced text editor
/// are two views of one thing (docs/query-dsl.md) and round-trip through
/// the canonical DSL: <see cref="ToDsl"/> renders this model, and
/// <see cref="TryFrom"/> re-derives it from any query simple enough to
/// display. Nesting, IN/BETWEEN, playlist refs, and free text stay
/// text-editor-only in v1.
/// </summary>
/// <param name="MatchAll">True joins conditions with AND, false with OR.</param>
/// <param name="Conditions">The rows; at least one.</param>
/// <param name="OrderBy">Optional single order term.</param>
/// <param name="Limit">Optional plain row limit (no BY grouping).</param>
public sealed record SimpleQueryModel(
    bool MatchAll,
    IReadOnlyList<SimpleCondition> Conditions,
    OrderTerm? OrderBy,
    int? Limit)
{
    /// <summary>Renders the model as DSL text (not necessarily canonical).</summary>
    /// <exception cref="InvalidOperationException">No conditions or unknown field.</exception>
    public string ToDsl()
    {
        if (Conditions.Count == 0)
        {
            throw new InvalidOperationException("The builder needs at least one condition.");
        }

        var sb = new StringBuilder();
        string joiner = MatchAll ? " AND " : " OR ";
        for (int i = 0; i < Conditions.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(joiner);
            }

            AppendCondition(sb, Conditions[i]);
        }

        if (OrderBy is { } order)
        {
            sb.Append(" ORDER BY ");
            sb.Append(order.IsRandom ? "random" : order.Field);
            if (!order.IsRandom && order.Descending)
            {
                sb.Append(" DESC");
            }
        }

        if (Limit is { } limit)
        {
            sb.Append(CultureInfo.InvariantCulture, $" LIMIT {limit}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Derives a builder model from a parsed query, or reports false when
    /// the query uses constructs the builder cannot display.
    /// </summary>
    public static bool TryFrom(ParsedQuery query, out SimpleQueryModel model)
    {
        model = null!;
        if (query.Limit is { ByField: not null } || query.OrderBy.Count > 1)
        {
            return false;
        }

        bool matchAll = true;
        IReadOnlyList<QueryExpr> operands;
        switch (query.Where)
        {
            case AndExpr and:
                operands = and.Operands;
                break;
            case OrExpr or_:
                operands = or_.Operands;
                matchAll = false;
                break;
            default:
                operands = [query.Where];
                break;
        }

        var conditions = new List<SimpleCondition>(operands.Count);
        foreach (QueryExpr expr in operands)
        {
            if (!TryCondition(expr, out SimpleCondition condition))
            {
                return false;
            }

            conditions.Add(condition);
        }

        model = new SimpleQueryModel(
            matchAll,
            conditions,
            query.OrderBy.Count == 1 ? query.OrderBy[0] : null,
            query.Limit?.Count);
        return true;
    }

    private static bool TryCondition(QueryExpr expr, out SimpleCondition condition)
    {
        condition = null!;
        switch (expr)
        {
            case BareField bare:
                condition = new SimpleCondition(bare.Field, ConditionOp.IsTrue, string.Empty);
                return true;

            case NotExpr { Operand: BareField bare }:
                condition = new SimpleCondition(bare.Field, ConditionOp.IsFalse, string.Empty);
                return true;

            case Comparison c:
                // `title = null` would print back as the string "null";
                // null tests on text fields stay text-editor-only.
                if (c.Value is NullValue
                    && FieldTable.TryGet(c.Field, out FieldDef def)
                    && def.Kind == FieldKind.Text)
                {
                    return false;
                }

                condition = new SimpleCondition(c.Field, FromCompareOp(c.Op), ValueText(c.Value));
                return true;

            default:
                return false;
        }
    }

    private static void AppendCondition(StringBuilder sb, SimpleCondition condition)
    {
        if (!FieldTable.TryGet(condition.Field, out FieldDef field))
        {
            throw new InvalidOperationException($"Unknown field '{condition.Field}'.");
        }

        switch (condition.Op)
        {
            case ConditionOp.IsTrue:
                sb.Append(condition.Field);
                return;
            case ConditionOp.IsFalse:
                sb.Append("NOT ").Append(condition.Field);
                return;
        }

        sb.Append(condition.Field).Append(' ').Append(OpText(condition.Op)).Append(' ');
        string value = condition.ValueText.Trim();
        if (field.Kind == FieldKind.Text)
        {
            sb.Append('"');
            sb.Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""));
            sb.Append('"');
        }
        else
        {
            // Non-text kinds take DSL literals verbatim (90s, @now - 30d,
            // null, 2024-01-01); the parser validates them.
            sb.Append(value);
        }
    }

    private static ConditionOp FromCompareOp(CompareOp op) => op switch
    {
        CompareOp.Eq => ConditionOp.Eq,
        CompareOp.Ne => ConditionOp.Ne,
        CompareOp.Lt => ConditionOp.Lt,
        CompareOp.Le => ConditionOp.Le,
        CompareOp.Gt => ConditionOp.Gt,
        CompareOp.Ge => ConditionOp.Ge,
        CompareOp.Contains => ConditionOp.Contains,
        CompareOp.NotContains => ConditionOp.NotContains,
        CompareOp.StartsWith => ConditionOp.StartsWith,
        CompareOp.EndsWith => ConditionOp.EndsWith,
        CompareOp.Regex => ConditionOp.Regex,
        _ => throw new InvalidOperationException($"Unknown operator {op}"),
    };

    private static string OpText(ConditionOp op) => op switch
    {
        ConditionOp.Eq => "=",
        ConditionOp.Ne => "!=",
        ConditionOp.Lt => "<",
        ConditionOp.Le => "<=",
        ConditionOp.Gt => ">",
        ConditionOp.Ge => ">=",
        ConditionOp.Contains => "=~",
        ConditionOp.NotContains => "!~",
        ConditionOp.StartsWith => "^=",
        ConditionOp.EndsWith => "$=",
        ConditionOp.Regex => "~=",
        _ => throw new InvalidOperationException($"Operator {op} takes no value."),
    };

    private static string ValueText(QueryValue value) => value switch
    {
        StringValue s => s.Value,
        NumberValue n => n.Value.ToString(CultureInfo.InvariantCulture),
        DurationValue d => d.Amount.ToString(CultureInfo.InvariantCulture) + d.Unit,
        DateTimeValue dt => dt.Source,
        RelativeDate { Sign: 0 } => "@now",
        RelativeDate r => "@now " + (r.Sign < 0 ? "- " : "+ ")
            + r.Amount.ToString(CultureInfo.InvariantCulture) + r.Unit,
        NullValue => "null",
        _ => throw new InvalidOperationException($"Unknown value node {value.GetType().Name}"),
    };
}
