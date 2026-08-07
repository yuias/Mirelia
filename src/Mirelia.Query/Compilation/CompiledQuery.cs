namespace Mirelia.Query.Compilation;

/// <summary>
/// A query compiled to SQL. The statement selects track ids only; hydration
/// is the caller's concern (the UI keeps ID arrays, per kickoff §4.3).
/// </summary>
/// <param name="Sql">The SELECT statement, with $-prefixed parameters.</param>
/// <param name="Parameters">Parameter names and values, in declaration order.</param>
public sealed record CompiledQuery(
    string Sql,
    IReadOnlyList<KeyValuePair<string, object>> Parameters);
