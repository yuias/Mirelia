using Mirelia.Query;
using Mirelia.Query.Ast;
using Mirelia.Query.Parsing;
using Xunit;

namespace Mirelia.Query.Tests;

/// <summary>
/// The DSL round-trip suite: parse → print must reach a fixed point, because
/// the printed canonical form is what gets persisted in playlist.query_text.
/// </summary>
public class RoundTripTests
{
    public static TheoryData<string> Queries => new()
    {
        // The four kickoff examples.
        """genre = "Jazz" AND rating >= 80 ORDER BY added DESC LIMIT 100""",
        """albumartist =~ "山田" AND (playcount > 10 OR lastplayed > @now - 30d)""",
        """is_lossless AND samplerate > 44100 ORDER BY random LIMIT 50""",
        """any("live") AND NOT playlist = "Excluded" LIMIT 3 BY albumartist""",
        // Operators.
        """title ^= "abc" OR title $= "xyz" OR title ~= "^[0-9]+" OR title !~ "demo" """,
        """year IN (1990, 1995, 2000)""",
        """duration BETWEEN 90s AND 10m""",
        """rating = null""",
        """rating != null""",
        """added > 2024-01-01""",
        """lastplayed > @now""",
        """lastplayed < @now + 1h""",
        // Nesting and precedence.
        """(genre = "Jazz" OR genre = "Blues") AND NOT (rating < 50 AND playcount = 0)""",
        """NOT is_lossless""",
        """codec IN ("mp3", "aac") ORDER BY artist, album, trackno""",
    };

    [Theory]
    [MemberData(nameof(Queries))]
    public void PrintIsAFixedPoint(string text)
    {
        ParsedQuery first = QueryParser.Parse(text);
        string printed = DslPrinter.Print(first);
        ParsedQuery second = QueryParser.Parse(printed);
        Assert.Equal(printed, DslPrinter.Print(second));
    }

    [Theory]
    [InlineData("genre = \"Jazz\" and rating >= 80", "genre = \"Jazz\" AND rating >= 80")]
    [InlineData("a =~ \"x\" && b =~ \"y\"", "a =~ \"x\" AND b =~ \"y\"")]
    [InlineData("a = 1 || b = 2", "a = 1 OR b = 2")]
    [InlineData("!is_lossless", "NOT is_lossless")]
    [InlineData("Genre = \"Jazz\"", "genre = \"Jazz\"")]
    [InlineData("year>2000 order by year desc limit 5", "year > 2000 ORDER BY year DESC LIMIT 5")]
    [InlineData("title = \"a \\\"quoted\\\" word\"", "title = \"a \\\"quoted\\\" word\"")]
    public void CanonicalFormIsStable(string input, string expected)
    {
        Assert.Equal(expected, DslPrinter.Print(QueryParser.Parse(input)));
    }

    [Theory]
    [InlineData("title = \"unterminated")]
    [InlineData("year > ")]
    [InlineData("duration > 30d")] // d is not a duration unit
    [InlineData("lastplayed > @now - 30")] // offset needs a unit
    [InlineData("title = \"x\" LIMIT 0")]
    [InlineData("title = \"x\" extra")]
    [InlineData("@nope")]
    public void MalformedQueriesThrow(string input)
    {
        Assert.Throws<QueryParseException>(() => QueryParser.Parse(input));
    }

    [Fact]
    public void PrecedenceParsesAndBeforeOr()
    {
        ParsedQuery q = QueryParser.Parse("""a = 1 OR b = 2 AND c = 3""");
        OrExpr or_ = Assert.IsType<OrExpr>(q.Where);
        Assert.Equal(2, or_.Operands.Count);
        Assert.IsType<Comparison>(or_.Operands[0]);
        Assert.IsType<AndExpr>(or_.Operands[1]);
    }
}
