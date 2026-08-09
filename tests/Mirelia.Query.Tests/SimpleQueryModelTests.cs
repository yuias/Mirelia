using Mirelia.Query.Ast;
using Mirelia.Query.Builder;
using Mirelia.Query.Parsing;
using Xunit;

namespace Mirelia.Query.Tests;

/// <summary>
/// The condition builder and the text editor are two views of one thing:
/// builder model → DSL → parse → model must reproduce the model, and any
/// query the builder can display must survive the reverse trip.
/// </summary>
public class SimpleQueryModelTests
{
    [Fact]
    public void ModelRoundTripsThroughDsl()
    {
        var model = new SimpleQueryModel(
            MatchAll: true,
            Conditions:
            [
                new SimpleCondition("genre", ConditionOp.Eq, "Jazz"),
                new SimpleCondition("rating", ConditionOp.Ge, "80"),
                new SimpleCondition("lastplayed", ConditionOp.Gt, "@now - 30d"),
                new SimpleCondition("duration", ConditionOp.Le, "10m"),
                new SimpleCondition("is_lossless", ConditionOp.IsTrue, ""),
            ],
            OrderBy: new OrderTerm("added", Descending: true),
            Limit: 100);

        string dsl = model.ToDsl();
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(dsl), out SimpleQueryModel back));
        Assert.Equal(model.MatchAll, back.MatchAll);
        Assert.Equal(model.Conditions, back.Conditions);
        Assert.Equal(model.OrderBy, back.OrderBy);
        Assert.Equal(model.Limit, back.Limit);
    }

    [Fact]
    public void AnyOfModelUsesOr()
    {
        var model = new SimpleQueryModel(
            MatchAll: false,
            Conditions:
            [
                new SimpleCondition("genre", ConditionOp.Eq, "Jazz"),
                new SimpleCondition("genre", ConditionOp.Eq, "Blues"),
            ],
            OrderBy: null,
            Limit: null);

        Assert.Equal("""genre = "Jazz" OR genre = "Blues" """.TrimEnd(), model.ToDsl());
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(model.ToDsl()), out SimpleQueryModel back));
        Assert.False(back.MatchAll);
    }

    [Fact]
    public void TextValuesAreQuotedAndEscaped()
    {
        var model = new SimpleQueryModel(
            true,
            [new SimpleCondition("title", ConditionOp.Contains, "say \"hi\" \\ wave")],
            null,
            null);

        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(model.ToDsl()), out SimpleQueryModel back));
        Assert.Equal("say \"hi\" \\ wave", back.Conditions[0].ValueText);
    }

    [Fact]
    public void BoolAndNullConditionsRoundTrip()
    {
        Assert.True(SimpleQueryModel.TryFrom(
            QueryParser.Parse("NOT is_lossless AND rating != null"), out SimpleQueryModel model));
        Assert.Equal(
            [
                new SimpleCondition("is_lossless", ConditionOp.IsFalse, ""),
                new SimpleCondition("rating", ConditionOp.Ne, "null"),
            ],
            model.Conditions);
        Assert.Equal("NOT is_lossless AND rating != null", model.ToDsl());
    }

    [Theory]
    // Nested boolean structure.
    [InlineData("""(genre = "Jazz" OR genre = "Blues") AND rating > 50""")]
    // Not a bare-field NOT.
    [InlineData("""NOT title =~ "demo" """)]
    // IN / BETWEEN / playlist / any().
    [InlineData("year IN (1990, 1995)")]
    [InlineData("duration BETWEEN 90s AND 10m")]
    [InlineData("""playlist = "Favorites" """)]
    [InlineData("""any("live")""")]
    // LIMIT BY and multi-term ORDER BY.
    [InlineData("rating > 0 LIMIT 3 BY albumartist")]
    [InlineData("rating > 0 ORDER BY artist, album")]
    // A null test on a text field would print back as the string "null".
    [InlineData("title = null")]
    public void ComplexQueriesAreRejected(string dsl)
    {
        Assert.False(SimpleQueryModel.TryFrom(QueryParser.Parse(dsl), out _));
    }

    [Fact]
    public void CanonicalPrintedFormIsAccepted()
    {
        // What the store persists (DslPrinter canonical form) must re-open
        // in the builder when it is simple enough.
        ParsedQuery parsed = QueryParser.Parse(
            """genre = "Jazz" AND rating >= 80 ORDER BY added DESC LIMIT 100""");
        string canonical = DslPrinter.Print(parsed);
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(canonical), out _));
    }
}
