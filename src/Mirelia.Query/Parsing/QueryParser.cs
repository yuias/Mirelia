using System.Globalization;
using Mirelia.Query.Ast;

namespace Mirelia.Query.Parsing;

/// <summary>
/// Recursive-descent parser for the query DSL (grammar: docs/query-dsl.md).
/// The parser is vocabulary-agnostic: field names are validated by the SQL
/// compiler, not here, so the grammar and the field table can evolve
/// independently.
/// </summary>
public static class QueryParser
{
    private static readonly string[] DurationUnits = ["s", "m", "h"];
    private static readonly string[] RelativeUnits = ["s", "m", "h", "d", "w", "mo", "y"];

    /// <summary>Parses query text into an AST.</summary>
    /// <exception cref="QueryParseException">On malformed input.</exception>
    public static ParsedQuery Parse(string text)
    {
        var state = new State(Lexer.Tokenize(text));
        QueryExpr where = ParseOr(state);
        IReadOnlyList<OrderTerm> orderBy = ParseOrderClause(state);
        LimitClause? limit = ParseLimitClause(state);
        state.Expect(TokenType.End, "end of query");
        return new ParsedQuery(where, orderBy, limit);
    }

    private sealed class State(List<Token> tokens)
    {
        private int _index;

        public Token Current => tokens[_index];

        public Token Advance() => tokens[_index++];

        public bool IsKeyword(string keyword)
            => Current.Type == TokenType.Ident
               && Current.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

        public bool TakeKeyword(string keyword)
        {
            if (!IsKeyword(keyword))
            {
                return false;
            }

            Advance();
            return true;
        }

        public Token Expect(TokenType type, string what)
        {
            return Current.Type == type
                ? Advance()
                : throw new QueryParseException($"Expected {what}, found '{Current.Text}'", Current.Position);
        }

        public void ExpectKeyword(string keyword)
        {
            if (!TakeKeyword(keyword))
            {
                throw new QueryParseException($"Expected '{keyword}', found '{Current.Text}'", Current.Position);
            }
        }
    }

    private static QueryExpr ParseOr(State s)
    {
        var operands = new List<QueryExpr> { ParseAnd(s) };
        while (s.Current.Type == TokenType.OrOr || s.IsKeyword("or"))
        {
            s.Advance();
            operands.Add(ParseAnd(s));
        }

        return operands.Count == 1 ? operands[0] : new OrExpr(operands);
    }

    private static QueryExpr ParseAnd(State s)
    {
        var operands = new List<QueryExpr> { ParseUnary(s) };
        while (s.Current.Type == TokenType.AndAnd || s.IsKeyword("and"))
        {
            s.Advance();
            operands.Add(ParseUnary(s));
        }

        return operands.Count == 1 ? operands[0] : new AndExpr(operands);
    }

    private static QueryExpr ParseUnary(State s)
    {
        if (s.Current.Type == TokenType.Bang || s.IsKeyword("not"))
        {
            s.Advance();
            return new NotExpr(ParseUnary(s));
        }

        return ParsePrimary(s);
    }

    private static QueryExpr ParsePrimary(State s)
    {
        if (s.Current.Type == TokenType.LParen)
        {
            s.Advance();
            QueryExpr inner = ParseOr(s);
            s.Expect(TokenType.RParen, "')'");
            return inner;
        }

        if (s.IsKeyword("any"))
        {
            s.Advance();
            s.Expect(TokenType.LParen, "'(' after any");
            // v1 restricts any() to a quoted string (see docs/query-dsl.md).
            Token text = s.Expect(TokenType.String, "quoted text inside any()");
            s.Expect(TokenType.RParen, "')'");
            return new FreeText(text.Text);
        }

        if (s.IsKeyword("playlist"))
        {
            s.Advance();
            Token op = s.Expect(TokenType.Operator, "'=' or '!=' after playlist");
            if (op.Text is not ("=" or "!="))
            {
                throw new QueryParseException("playlist supports only '=' and '!='", op.Position);
            }

            Token name = s.Expect(TokenType.String, "playlist name string");
            return new PlaylistMembership(op.Text == "!=", name.Text);
        }

        Token field = s.Expect(TokenType.Ident, "field name");
        string canonical = field.Text.ToLowerInvariant();

        if (s.TakeKeyword("in"))
        {
            s.Expect(TokenType.LParen, "'(' after IN");
            var values = new List<QueryValue> { ParseValue(s) };
            while (s.Current.Type == TokenType.Comma)
            {
                s.Advance();
                values.Add(ParseValue(s));
            }

            s.Expect(TokenType.RParen, "')'");
            return new InList(canonical, values);
        }

        if (s.TakeKeyword("between"))
        {
            QueryValue low = ParseValue(s);
            s.ExpectKeyword("and");
            QueryValue high = ParseValue(s);
            return new Between(canonical, low, high);
        }

        if (s.Current.Type == TokenType.Operator)
        {
            Token op = s.Advance();
            return new Comparison(canonical, ToCompareOp(op), ParseValue(s));
        }

        // Bare boolean field, e.g. `is_lossless`.
        return new BareField(canonical);
    }

    private static CompareOp ToCompareOp(Token op) => op.Text switch
    {
        "=" => CompareOp.Eq,
        "!=" => CompareOp.Ne,
        "<" => CompareOp.Lt,
        "<=" => CompareOp.Le,
        ">" => CompareOp.Gt,
        ">=" => CompareOp.Ge,
        "=~" => CompareOp.Contains,
        "!~" => CompareOp.NotContains,
        "^=" => CompareOp.StartsWith,
        "$=" => CompareOp.EndsWith,
        "~=" => CompareOp.Regex,
        _ => throw new QueryParseException($"Unknown operator '{op.Text}'", op.Position),
    };

    private static QueryValue ParseValue(State s)
    {
        Token t = s.Current;
        switch (t.Type)
        {
            case TokenType.String:
                s.Advance();
                return new StringValue(t.Text);

            case TokenType.Number:
                s.Advance();
                return new NumberValue(t.Number);

            case TokenType.UnitNumber:
                s.Advance();
                (decimal amount, string unit) = SplitUnitNumber(t);
                return DurationUnits.Contains(unit)
                    ? new DurationValue(amount, unit)
                    : throw new QueryParseException(
                        $"'{unit}' is not a duration unit (use s, m, or h)", t.Position);

            case TokenType.Date:
                s.Advance();
                var parsed = DateTimeOffset.Parse(
                    t.Text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);
                return new DateTimeValue(parsed, t.Text);

            case TokenType.AtNow:
                s.Advance();
                return ParseRelativeDate(s);

            case TokenType.Ident when t.Text.Equals("null", StringComparison.OrdinalIgnoreCase):
                s.Advance();
                return new NullValue();

            default:
                throw new QueryParseException($"Expected a value, found '{t.Text}'", t.Position);
        }
    }

    private static QueryValue ParseRelativeDate(State s)
    {
        int sign = s.Current.Type switch
        {
            TokenType.Minus => -1,
            TokenType.Plus => 1,
            _ => 0,
        };
        if (sign == 0)
        {
            return new RelativeDate(0, 0, string.Empty);
        }

        s.Advance();
        Token offset = s.Current.Type is TokenType.UnitNumber
            ? s.Advance()
            : throw new QueryParseException(
                $"Expected an offset like 30d after '@now {(sign < 0 ? "-" : "+")}'",
                s.Current.Position);
        (decimal amount, string unit) = SplitUnitNumber(offset);
        return RelativeUnits.Contains(unit)
            ? new RelativeDate(sign, amount, unit)
            : throw new QueryParseException(
                $"'{unit}' is not a time unit (use s, m, h, d, w, mo, or y)", offset.Position);
    }

    private static (decimal Amount, string Unit) SplitUnitNumber(Token t)
    {
        int split = 0;
        while (split < t.Text.Length && (char.IsAsciiDigit(t.Text[split]) || t.Text[split] == '.'))
        {
            split++;
        }

        return (
            decimal.Parse(t.Text[..split], CultureInfo.InvariantCulture),
            t.Text[split..].ToLowerInvariant());
    }

    private static IReadOnlyList<OrderTerm> ParseOrderClause(State s)
    {
        if (!s.TakeKeyword("order"))
        {
            return [];
        }

        s.ExpectKeyword("by");
        var terms = new List<OrderTerm> { ParseOrderTerm(s) };
        while (s.Current.Type == TokenType.Comma)
        {
            s.Advance();
            terms.Add(ParseOrderTerm(s));
        }

        return terms;
    }

    private static OrderTerm ParseOrderTerm(State s)
    {
        if (s.TakeKeyword("random"))
        {
            return new OrderTerm(string.Empty, Descending: false, IsRandom: true);
        }

        Token field = s.Expect(TokenType.Ident, "order field");
        bool descending = false;
        if (s.TakeKeyword("desc"))
        {
            descending = true;
        }
        else
        {
            s.TakeKeyword("asc");
        }

        return new OrderTerm(field.Text.ToLowerInvariant(), descending);
    }

    private static LimitClause? ParseLimitClause(State s)
    {
        if (!s.TakeKeyword("limit"))
        {
            return null;
        }

        Token count = s.Expect(TokenType.Number, "limit count");
        string? byField = null;
        if (s.TakeKeyword("by"))
        {
            byField = s.Expect(TokenType.Ident, "grouping field after BY").Text.ToLowerInvariant();
        }

        int n = (int)count.Number;
        if (n <= 0)
        {
            throw new QueryParseException("LIMIT must be positive", count.Position);
        }

        return new LimitClause(n, byField);
    }
}
