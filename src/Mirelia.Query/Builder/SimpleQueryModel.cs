using System.Globalization;
using System.Text;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

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

/// <summary>Base of the builder's condition tree nodes: a leaf or a nested group.</summary>
public abstract record ConditionNode;

/// <summary>A single condition row.</summary>
/// <param name="Condition">The condition it holds.</param>
public sealed record ConditionLeaf(SimpleCondition Condition) : ConditionNode;

/// <summary>
/// A group of child nodes joined by AND (<see cref="MatchAll"/> true) or OR.
/// Children may themselves be groups, so the tree nests to any depth; the
/// builder's whole query is the root group.
/// </summary>
/// <param name="MatchAll">True joins children with AND, false with OR.</param>
/// <param name="Children">The child conditions/subgroups, in order.</param>
public sealed record ConditionGroup(bool MatchAll, IReadOnlyList<ConditionNode> Children) : ConditionNode
{
    // The compiler-synthesized record equality would compare Children by
    // reference (List<T> does not have value equality); groups with the
    // same shape must compare equal for round-trip tests to be meaningful.
    /// <inheritdoc/>
    public bool Equals(ConditionGroup? other)
        => other is not null && MatchAll == other.MatchAll && Children.SequenceEqual(other.Children);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MatchAll);
        foreach (ConditionNode child in Children)
        {
            hash.Add(child);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// The GUI condition builder's model: a recursive tree of AND/OR groups and
/// conditions, plus optional order and limit. The builder and the advanced
/// text editor are two views of one thing (docs/query-dsl.md) and round-trip
/// through the canonical DSL: <see cref="ToDsl"/> renders this model, and
/// <see cref="TryFrom"/> re-derives it from any query simple enough to
/// display. IN/BETWEEN, playlist refs, free text, and NOT over anything but
/// a bare bool field stay text-editor-only.
/// </summary>
/// <param name="Root">The root group; may be empty (nothing to save yet).</param>
/// <param name="OrderBy">Optional single order term.</param>
/// <param name="Limit">Optional plain row limit (no BY grouping).</param>
public sealed record SimpleQueryModel(
    ConditionGroup Root,
    OrderTerm? OrderBy,
    int? Limit)
{
    /// <summary>Renders the model as DSL text (not necessarily canonical).</summary>
    /// <exception cref="InvalidOperationException">No conditions or unknown field.</exception>
    /// <exception cref="QueryParseException">A condition's value text is not a valid literal.</exception>
    public string ToDsl()
    {
        QueryExpr? where = BuildExpr(Root);
        if (where is null)
        {
            throw new InvalidOperationException("The builder needs at least one condition.");
        }

        var query = new ParsedQuery(
            where,
            OrderBy is { } order ? [order] : [],
            Limit is { } limit ? new LimitClause(limit, ByField: null) : null);
        return DslPrinter.Print(query);
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

        if (!TryGroup(query.Where, out ConditionGroup root))
        {
            return false;
        }

        model = new SimpleQueryModel(
            root,
            query.OrderBy.Count == 1 ? query.OrderBy[0] : null,
            query.Limit?.Count);
        return true;
    }

    /// <summary>
    /// Builds the AND/OR/comparison AST for a group, auto-pruning empty
    /// subgroups and collapsing single-child groups to their one operand
    /// (an AND or OR of one thing is just that thing). Returns null when
    /// the group has nothing left after pruning.
    /// </summary>
    private static QueryExpr? BuildExpr(ConditionGroup group)
    {
        var operands = new List<QueryExpr>(group.Children.Count);
        foreach (ConditionNode child in group.Children)
        {
            QueryExpr? expr = child switch
            {
                ConditionLeaf leaf => BuildCondition(leaf.Condition),
                ConditionGroup nested => BuildExpr(nested),
                _ => throw new InvalidOperationException($"Unknown node {child.GetType().Name}"),
            };
            if (expr is not null)
            {
                operands.Add(expr);
            }
        }

        return operands.Count switch
        {
            0 => null,
            1 => operands[0],
            _ => group.MatchAll ? new AndExpr(operands) : new OrExpr(operands),
        };
    }

    private static QueryExpr BuildCondition(SimpleCondition condition)
    {
        if (!FieldTable.TryGet(condition.Field, out FieldDef field))
        {
            throw new InvalidOperationException($"Unknown field '{condition.Field}'.");
        }

        switch (condition.Op)
        {
            case ConditionOp.IsTrue:
                return new BareField(condition.Field);
            case ConditionOp.IsFalse:
                return new NotExpr(new BareField(condition.Field));
        }

        // Reuse the DSL lexer/parser to turn the raw value text into a
        // QueryValue, instead of duplicating literal syntax (numbers,
        // durations, dates, @now offsets, null) here.
        ParsedQuery fragment = QueryParser.Parse(ConditionText(condition, field));
        return fragment.Where;
    }

    private static bool TryGroup(QueryExpr expr, out ConditionGroup group)
    {
        group = null!;
        bool matchAll;
        IReadOnlyList<QueryExpr> operands;
        switch (expr)
        {
            case AndExpr and:
                operands = and.Operands;
                matchAll = true;
                break;
            case OrExpr or_:
                operands = or_.Operands;
                matchAll = false;
                break;
            default:
                operands = [expr];
                matchAll = true;
                break;
        }

        var children = new List<ConditionNode>(operands.Count);
        foreach (QueryExpr operand in operands)
        {
            if (!TryNode(operand, out ConditionNode node))
            {
                return false;
            }

            children.Add(node);
        }

        group = new ConditionGroup(matchAll, children);
        return true;
    }

    private static bool TryNode(QueryExpr expr, out ConditionNode node)
    {
        node = null!;
        if (expr is AndExpr or OrExpr)
        {
            if (!TryGroup(expr, out ConditionGroup nested))
            {
                return false;
            }

            node = nested;
            return true;
        }

        if (!TryCondition(expr, out SimpleCondition condition))
        {
            return false;
        }

        node = new ConditionLeaf(condition);
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

    /// <summary>Renders one <c>field op value</c> fragment for the DSL parser to re-parse.</summary>
    private static string ConditionText(SimpleCondition condition, FieldDef field)
    {
        var sb = new StringBuilder();
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

        return sb.ToString();
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
