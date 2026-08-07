using System.Globalization;
using System.Text;
using Mirelia.Query.Ast;

namespace Mirelia.Query;

/// <summary>
/// Prints an AST back to canonical DSL text. The canonical form is what gets
/// persisted in <c>playlist.query_text</c>; parse → print → parse must be a
/// fixed point (the round-trip test suite enforces this).
/// </summary>
public static class DslPrinter
{
    /// <summary>Renders the query in canonical form.</summary>
    public static string Print(ParsedQuery query)
    {
        var sb = new StringBuilder();
        PrintExpr(sb, query.Where, parentPrecedence: 0);

        if (query.OrderBy.Count > 0)
        {
            sb.Append(" ORDER BY ");
            sb.Append(string.Join(", ", query.OrderBy.Select(PrintOrderTerm)));
        }

        if (query.Limit is { } limit)
        {
            sb.Append(CultureInfo.InvariantCulture, $" LIMIT {limit.Count}");
            if (limit.ByField is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $" BY {limit.ByField}");
            }
        }

        return sb.ToString();
    }

    private static string PrintOrderTerm(OrderTerm term)
    {
        if (term.IsRandom)
        {
            return "random";
        }

        return term.Descending ? term.Field + " DESC" : term.Field;
    }

    // Precedence: OR = 1, AND = 2, NOT = 3. A child at lower precedence than
    // its context needs parentheses.
    private static void PrintExpr(StringBuilder sb, QueryExpr expr, int parentPrecedence)
    {
        switch (expr)
        {
            case OrExpr or_:
                Parenthesize(sb, parentPrecedence > 1, () =>
                    JoinOperands(sb, or_.Operands, " OR ", 1));
                break;

            case AndExpr and:
                Parenthesize(sb, parentPrecedence > 2, () =>
                    JoinOperands(sb, and.Operands, " AND ", 2));
                break;

            case NotExpr not:
                sb.Append("NOT ");
                PrintExpr(sb, not.Operand, 3);
                break;

            case Comparison c:
                sb.Append(c.Field).Append(' ').Append(OpText(c.Op)).Append(' ');
                PrintValue(sb, c.Value);
                break;

            case InList inList:
                sb.Append(inList.Field).Append(" IN (");
                for (int i = 0; i < inList.Values.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    PrintValue(sb, inList.Values[i]);
                }

                sb.Append(')');
                break;

            case Between b:
                sb.Append(b.Field).Append(" BETWEEN ");
                PrintValue(sb, b.Low);
                sb.Append(" AND ");
                PrintValue(sb, b.High);
                break;

            case PlaylistMembership p:
                sb.Append("playlist ").Append(p.Negated ? "!=" : "=").Append(' ');
                PrintString(sb, p.PlaylistName);
                break;

            case FreeText f:
                sb.Append("any(");
                PrintString(sb, f.Text);
                sb.Append(')');
                break;

            case BareField bare:
                sb.Append(bare.Field);
                break;

            default:
                throw new InvalidOperationException($"Unknown expression node {expr.GetType().Name}");
        }
    }

    private static void JoinOperands(
        StringBuilder sb, IReadOnlyList<QueryExpr> operands, string separator, int precedence)
    {
        for (int i = 0; i < operands.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(separator);
            }

            PrintExpr(sb, operands[i], precedence);
        }
    }

    private static void Parenthesize(StringBuilder sb, bool needed, Action print)
    {
        if (needed)
        {
            sb.Append('(');
        }

        print();
        if (needed)
        {
            sb.Append(')');
        }
    }

    private static string OpText(CompareOp op) => op switch
    {
        CompareOp.Eq => "=",
        CompareOp.Ne => "!=",
        CompareOp.Lt => "<",
        CompareOp.Le => "<=",
        CompareOp.Gt => ">",
        CompareOp.Ge => ">=",
        CompareOp.Contains => "=~",
        CompareOp.NotContains => "!~",
        CompareOp.StartsWith => "^=",
        CompareOp.EndsWith => "$=",
        CompareOp.Regex => "~=",
        _ => throw new InvalidOperationException($"Unknown operator {op}"),
    };

    private static void PrintValue(StringBuilder sb, QueryValue value)
    {
        switch (value)
        {
            case StringValue s:
                PrintString(sb, s.Value);
                break;

            case NumberValue n:
                sb.Append(n.Value.ToString(CultureInfo.InvariantCulture));
                break;

            case DurationValue d:
                sb.Append(d.Amount.ToString(CultureInfo.InvariantCulture)).Append(d.Unit);
                break;

            case DateTimeValue dt:
                sb.Append(dt.Source);
                break;

            case RelativeDate r:
                sb.Append("@now");
                if (r.Sign != 0)
                {
                    sb.Append(r.Sign < 0 ? " - " : " + ");
                    sb.Append(r.Amount.ToString(CultureInfo.InvariantCulture)).Append(r.Unit);
                }

                break;

            case NullValue:
                sb.Append("null");
                break;

            default:
                throw new InvalidOperationException($"Unknown value node {value.GetType().Name}");
        }
    }

    private static void PrintString(StringBuilder sb, string value)
    {
        sb.Append('"');
        sb.Append(value.Replace("\\", "\\\\").Replace("\"", "\\\""));
        sb.Append('"');
    }
}
