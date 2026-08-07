namespace Mirelia.Query.Compilation;

/// <summary>Thrown when a parsed query cannot be compiled to SQL.</summary>
public sealed class QueryCompileException(string message) : Exception(message);
