using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;
using Xunit;

namespace Mirelia.Query.Tests;

public class SqlCompilerTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static CompiledQuery Compile(string dsl)
        => SqlCompiler.Compile(QueryParser.Parse(dsl), FixedNow);

    [Fact]
    public void SimpleComparisonProducesParameterizedSql()
    {
        CompiledQuery q = Compile("""genre = "Jazz" AND rating >= 80""");
        Assert.Contains("g.name = $p0", q.Sql);
        Assert.Contains("t.rating >= $p1", q.Sql);
        Assert.Equal("Jazz", q.Parameters[0].Value);
        Assert.Equal(80L, q.Parameters[1].Value);
    }

    [Fact]
    public void ContainsMatchesNormalizedSearchColumn()
    {
        CompiledQuery q = Compile("""albumartist =~ "ヤマダ" """);
        Assert.Contains("COALESCE(aa.name_search, '') LIKE $p0 ESCAPE '\\'", q.Sql);
        // Katakana folds to hiragana in the needle.
        Assert.Equal("%やまだ%", q.Parameters[0].Value);
    }

    [Fact]
    public void RelativeDateResolvesAgainstAnchor()
    {
        CompiledQuery q = Compile("lastplayed > @now - 30d");
        long expected = FixedNow.ToUnixTimeSeconds() - (30L * 86_400);
        Assert.Equal(expected, q.Parameters[0].Value);
    }

    [Fact]
    public void DurationLiteralsBecomeMilliseconds()
    {
        CompiledQuery q = Compile("duration BETWEEN 90s AND 10m");
        Assert.Equal(90_000L, q.Parameters[0].Value);
        Assert.Equal(600_000L, q.Parameters[1].Value);
    }

    [Fact]
    public void BareNumberOnDurationMeansSeconds()
    {
        CompiledQuery q = Compile("duration > 180");
        Assert.Equal(180_000L, q.Parameters[0].Value);
    }

    [Fact]
    public void NullComparisonUsesIsNull()
    {
        Assert.Contains("t.rating IS NULL", Compile("rating = null").Sql);
        Assert.Contains("t.rating IS NOT NULL", Compile("rating != null").Sql);
    }

    [Fact]
    public void NotEqualIncludesNullRows()
    {
        CompiledQuery q = Compile("""genre != "Jazz" """);
        Assert.Contains("(g.name IS NULL OR g.name != $p0)", q.Sql);
    }

    [Fact]
    public void FreeTextGoesThroughFts()
    {
        CompiledQuery q = Compile("""any("live at budokan")""");
        Assert.Contains("track_fts MATCH $p0", q.Sql);
        Assert.Equal("\"live at budokan\"", q.Parameters[0].Value);
    }

    [Fact]
    public void PlaylistMembershipCompilesToExists()
    {
        CompiledQuery q = Compile("""NOT playlist = "Excluded" """);
        Assert.Contains("NOT EXISTS", q.Sql);
        Assert.Equal("Excluded", q.Parameters[0].Value);
    }

    [Fact]
    public void GroupLimitUsesWindowFunction()
    {
        CompiledQuery q = Compile("""any("live") LIMIT 3 BY albumartist""");
        Assert.Contains("row_number() OVER (PARTITION BY grp", q.Sql);
        Assert.Contains("rn <= 3", q.Sql);
    }

    [Fact]
    public void OrderByUsesSortColumns()
    {
        CompiledQuery q = Compile("""year > 2000 ORDER BY artist, added DESC""");
        Assert.Contains("ORDER BY ar.name_sort, t.added_unix DESC", q.Sql);
    }

    [Theory]
    [InlineData("nosuchfield = 1")]
    [InlineData("""year =~ "x" """)]
    [InlineData("""duration > "text" """)]
    [InlineData("year ORDER BY year")] // bare non-boolean field
    [InlineData("""title = "x" ORDER BY nosuchfield""")]
    [InlineData("""title = "x" LIMIT 5 BY nosuchfield""")]
    public void CompileErrorsAreReported(string dsl)
    {
        Assert.Throws<QueryCompileException>(() => Compile(dsl));
    }
}
