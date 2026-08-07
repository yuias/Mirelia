namespace Mirelia.Query.Ast;

/// <summary>A parsed smart-playlist query: predicate, ordering, limit.</summary>
/// <param name="Where">The root predicate expression.</param>
/// <param name="OrderBy">Order terms; empty means unspecified.</param>
/// <param name="Limit">Optional limit clause.</param>
public sealed record ParsedQuery(
    QueryExpr Where,
    IReadOnlyList<OrderTerm> OrderBy,
    LimitClause? Limit);

/// <summary>One <c>ORDER BY</c> term.</summary>
/// <param name="Field">Canonical field name; ignored when <paramref name="IsRandom"/>.</param>
/// <param name="Descending">True for <c>DESC</c>.</param>
/// <param name="IsRandom">True for <c>ORDER BY random</c>.</param>
public sealed record OrderTerm(string Field, bool Descending, bool IsRandom = false);

/// <summary>A <c>LIMIT n [BY field]</c> clause; BY caps rows per group.</summary>
/// <param name="Count">Maximum rows (per group when <paramref name="ByField"/> is set).</param>
/// <param name="ByField">Grouping field for per-group limits, or null.</param>
public sealed record LimitClause(int Count, string? ByField);

/// <summary>Base of all predicate expressions.</summary>
public abstract record QueryExpr;

/// <summary>Logical OR over two or more operands.</summary>
/// <param name="Operands">The operands, in source order.</param>
public sealed record OrExpr(IReadOnlyList<QueryExpr> Operands) : QueryExpr;

/// <summary>Logical AND over two or more operands.</summary>
/// <param name="Operands">The operands, in source order.</param>
public sealed record AndExpr(IReadOnlyList<QueryExpr> Operands) : QueryExpr;

/// <summary>Logical negation.</summary>
/// <param name="Operand">The negated expression.</param>
public sealed record NotExpr(QueryExpr Operand) : QueryExpr;

/// <summary>Comparison operators of the DSL.</summary>
public enum CompareOp
{
    /// <summary><c>=</c> — exact match.</summary>
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

    /// <summary><c>=~</c> — contains, case/width/kana-insensitive.</summary>
    Contains,

    /// <summary><c>!~</c> — negated contains.</summary>
    NotContains,

    /// <summary><c>^=</c> — starts with (normalized).</summary>
    StartsWith,

    /// <summary><c>$=</c> — ends with (normalized).</summary>
    EndsWith,

    /// <summary><c>~=</c> — regular expression over the raw value.</summary>
    Regex,
}

/// <summary><c>field op value</c>.</summary>
/// <param name="Field">Canonical field name.</param>
/// <param name="Op">The operator.</param>
/// <param name="Value">The right-hand value.</param>
public sealed record Comparison(string Field, CompareOp Op, QueryValue Value) : QueryExpr;

/// <summary><c>field IN (v, v, ...)</c>.</summary>
/// <param name="Field">Canonical field name.</param>
/// <param name="Values">The candidate values.</param>
public sealed record InList(string Field, IReadOnlyList<QueryValue> Values) : QueryExpr;

/// <summary><c>field BETWEEN low AND high</c>, bounds inclusive.</summary>
/// <param name="Field">Canonical field name.</param>
/// <param name="Low">Lower bound.</param>
/// <param name="High">Upper bound.</param>
public sealed record Between(string Field, QueryValue Low, QueryValue High) : QueryExpr;

/// <summary><c>playlist = "name"</c> / <c>playlist != "name"</c> membership test.</summary>
/// <param name="Negated">True for <c>!=</c>.</param>
/// <param name="PlaylistName">The playlist name, matched exactly.</param>
public sealed record PlaylistMembership(bool Negated, string PlaylistName) : QueryExpr;

/// <summary><c>any("text")</c> — free text over the FTS index.</summary>
/// <param name="Text">The searched text.</param>
public sealed record FreeText(string Text) : QueryExpr;

/// <summary>A boolean field used bare as a predicate, e.g. <c>is_lossless</c>.</summary>
/// <param name="Field">Canonical field name.</param>
public sealed record BareField(string Field) : QueryExpr;

/// <summary>Base of all literal values.</summary>
public abstract record QueryValue;

/// <summary>A quoted string.</summary>
/// <param name="Value">The unescaped text.</param>
public sealed record StringValue(string Value) : QueryValue;

/// <summary>A number. Decimal keeps integer round-trips exact.</summary>
/// <param name="Value">The numeric value.</param>
public sealed record NumberValue(decimal Value) : QueryValue;

/// <summary>A duration literal like <c>90s</c>, <c>3m</c>, <c>1h</c>.</summary>
/// <param name="Amount">The scalar part.</param>
/// <param name="Unit">One of <c>s</c>, <c>m</c>, <c>h</c>.</param>
public sealed record DurationValue(decimal Amount, string Unit) : QueryValue;

/// <summary>A date/time literal, e.g. <c>2024-01-01</c>.</summary>
/// <param name="Value">The parsed instant (local midnight for bare dates).</param>
/// <param name="Source">Original text, kept for canonical printing.</param>
public sealed record DateTimeValue(DateTimeOffset Value, string Source) : QueryValue;

/// <summary><c>@now</c> optionally offset, e.g. <c>@now - 30d</c>.</summary>
/// <param name="Sign">-1, 0, or +1.</param>
/// <param name="Amount">Offset scalar; 0 when <paramref name="Sign"/> is 0.</param>
/// <param name="Unit">One of <c>s m h d w mo y</c>; empty when no offset.</param>
public sealed record RelativeDate(int Sign, decimal Amount, string Unit) : QueryValue;

/// <summary>The <c>null</c> literal.</summary>
public sealed record NullValue : QueryValue;
