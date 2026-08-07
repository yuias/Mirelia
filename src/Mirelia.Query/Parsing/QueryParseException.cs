namespace Mirelia.Query.Parsing;

/// <summary>Thrown when query text cannot be tokenized or parsed.</summary>
public sealed class QueryParseException : Exception
{
    /// <summary>Creates a parse error at a character offset.</summary>
    /// <param name="message">Human-readable description.</param>
    /// <param name="position">0-based character offset into the query text.</param>
    public QueryParseException(string message, int position)
        : base($"{message} (at position {position})")
    {
        Position = position;
    }

    /// <summary>0-based character offset of the error in the query text.</summary>
    public int Position { get; }
}
