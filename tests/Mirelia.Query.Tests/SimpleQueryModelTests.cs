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
    private static ConditionGroup Group(bool matchAll, params ConditionNode[] children)
        => new(matchAll, children);

    private static ConditionLeaf Leaf(string field, ConditionOp op, string valueText = "")
        => new(new SimpleCondition(field, op, valueText));

    [Fact]
    public void ModelRoundTripsThroughDsl()
    {
        var model = new SimpleQueryModel(
            Group(
                true,
                Leaf("genre", ConditionOp.Eq, "Jazz"),
                Leaf("rating", ConditionOp.Ge, "80"),
                Leaf("lastplayed", ConditionOp.Gt, "@now - 30d"),
                Leaf("duration", ConditionOp.Le, "10m"),
                Leaf("is_lossless", ConditionOp.IsTrue)),
            OrderBy: new OrderTerm("added", Descending: true),
            Limit: 100);

        string dsl = model.ToDsl();
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(dsl), out SimpleQueryModel back));
        Assert.Equal(model.Root, back.Root);
        Assert.Equal(model.OrderBy, back.OrderBy);
        Assert.Equal(model.Limit, back.Limit);
    }

    [Fact]
    public void AnyOfModelUsesOr()
    {
        var model = new SimpleQueryModel(
            Group(
                false,
                Leaf("genre", ConditionOp.Eq, "Jazz"),
                Leaf("genre", ConditionOp.Eq, "Blues")),
            OrderBy: null,
            Limit: null);

        Assert.Equal("""genre = "Jazz" OR genre = "Blues" """.TrimEnd(), model.ToDsl());
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(model.ToDsl()), out SimpleQueryModel back));
        Assert.False(back.Root.MatchAll);
    }

    [Fact]
    public void TextValuesAreQuotedAndEscaped()
    {
        var model = new SimpleQueryModel(
            Group(true, Leaf("title", ConditionOp.Contains, "say \"hi\" \\ wave")),
            null,
            null);

        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(model.ToDsl()), out SimpleQueryModel back));
        var leaf = Assert.IsType<ConditionLeaf>(back.Root.Children[0]);
        Assert.Equal("say \"hi\" \\ wave", leaf.Condition.ValueText);
    }

    [Fact]
    public void BoolAndNullConditionsRoundTrip()
    {
        Assert.True(SimpleQueryModel.TryFrom(
            QueryParser.Parse("NOT is_lossless AND rating != null"), out SimpleQueryModel model));
        Assert.Equal(
            [
                Leaf("is_lossless", ConditionOp.IsFalse),
                Leaf("rating", ConditionOp.Ne, "null"),
            ],
            model.Root.Children);
        Assert.Equal("NOT is_lossless AND rating != null", model.ToDsl());
    }

    [Theory]
    // Not a bare-field NOT.
    [InlineData("""NOT title =~ "demo" """)]
    // NOT over a compound expression (not just a bare field).
    [InlineData("""NOT (rating < 50 AND playcount = 0)""")]
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

    [Theory]
    // (a AND b) OR c — a nested AND group under an OR root.
    [InlineData("""(genre = "Jazz" AND rating > 50) OR duration < 10m""")]
    // a AND (b OR (c AND d)) — alternating operators, three levels deep.
    [InlineData("""genre = "Jazz" AND (rating > 50 OR (duration < 10m AND is_lossless))""")]
    // A NOT-bool leaf inside a nested group.
    [InlineData("""genre = "Jazz" OR (NOT is_lossless AND rating > 50)""")]
    public void NestedQueriesAreAccepted(string dsl)
    {
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(dsl), out _));
    }

    [Theory]
    // (a AND b) OR c
    [InlineData("""(genre = "Jazz" AND rating > 50) OR duration < 10m""")]
    // a AND (b OR (c AND d))
    [InlineData("""genre = "Jazz" AND (rating > 50 OR (duration < 10m AND is_lossless))""")]
    // A NOT-bool leaf nested inside a subgroup.
    [InlineData("""genre = "Jazz" OR (NOT is_lossless AND rating > 50)""")]
    public void NestedTreesRoundTripThroughDslAndBackToTheSameTree(string dsl)
    {
        // AST → tree → DSL → AST → tree must be a fixed point: alternating
        // AND/OR nesting forces exactly the parens needed to preserve shape.
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(dsl), out SimpleQueryModel first));
        string printed = first.ToDsl();
        Assert.True(SimpleQueryModel.TryFrom(QueryParser.Parse(printed), out SimpleQueryModel second));
        Assert.Equal(first.Root, second.Root);
        Assert.Equal(printed, second.ToDsl());
    }

    [Fact]
    public void EmptySubgroupsAreAutoPrunedOnBuild()
    {
        // An empty nested group (e.g. one whose only condition was removed
        // in the UI) is silently dropped rather than blocking save.
        var model = new SimpleQueryModel(
            Group(
                true,
                Leaf("genre", ConditionOp.Eq, "Jazz"),
                Group(false)),
            null,
            null);

        Assert.Equal("genre = \"Jazz\"", model.ToDsl());
    }

    [Fact]
    public void EmptyRootThrowsOnBuild()
    {
        var model = new SimpleQueryModel(Group(true), null, null);
        Assert.Throws<InvalidOperationException>(() => model.ToDsl());
    }

    [Fact]
    public void SingleChildGroupCollapsesToItsOperand()
    {
        // A group wrapping exactly one child is equivalent to the child
        // itself; the printed DSL should not carry redundant structure.
        var model = new SimpleQueryModel(
            Group(true, Group(false, Leaf("genre", ConditionOp.Eq, "Jazz"))),
            null,
            null);

        Assert.Equal("genre = \"Jazz\"", model.ToDsl());
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
