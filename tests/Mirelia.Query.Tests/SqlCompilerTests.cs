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
    public void DateKeyPartialDatesCoverTheirPeriod()
    {
        // '=' on a partial date is containment.
        CompiledQuery year = Compile("date = 2025");
        Assert.Contains("t.date_key BETWEEN $p0 AND $p1", year.Sql);
        Assert.Equal(20250101L, year.Parameters[0].Value);
        Assert.Equal(20251231L, year.Parameters[1].Value);

        // >= anchors at the period start, <= at its end, > after its end.
        Assert.Equal(20250801L, Compile("date >= 2025-08").Parameters[0].Value);
        Assert.Equal(20250831L, Compile("date <= 2025-08").Parameters[0].Value);
        Assert.Equal(20251231L, Compile("date > 2025").Parameters[0].Value);
        Assert.Equal(20250101L, Compile("date < 2025").Parameters[0].Value);

        // A full date is a single key.
        CompiledQuery day = Compile("date = 2025-08-12");
        Assert.Contains("t.date_key = $p0", day.Sql);
        Assert.Equal(20250812L, day.Parameters[0].Value);

        // != keeps the NULL-counts-as-different convention over the period.
        Assert.Contains(
            "(t.date_key IS NULL OR t.date_key NOT BETWEEN $p0 AND $p1)",
            Compile("date != 2025").Sql);
    }

    [Fact]
    public void DateKeyRangesAndListsExpandPeriods()
    {
        CompiledQuery between = Compile("date BETWEEN 2024 AND 2025-06");
        Assert.Equal(20240101L, between.Parameters[0].Value);
        Assert.Equal(20250631L, between.Parameters[1].Value);

        CompiledQuery inList = Compile("date IN (2024, 2025-08-12)");
        Assert.Contains("t.date_key BETWEEN $p0 AND $p1 OR t.date_key = $p2", inList.Sql);
        Assert.Equal(20240101L, inList.Parameters[0].Value);
        Assert.Equal(20241231L, inList.Parameters[1].Value);
        Assert.Equal(20250812L, inList.Parameters[2].Value);

        Assert.Contains("t.date_key IS NULL", Compile("date = null").Sql);
    }

    [Fact]
    public void DateKeyRejectsNonDateValues()
    {
        // Raw yyyymmdd numbers are ambiguous with years; only the dashed
        // spellings (and bare years) are accepted.
        Assert.Throws<QueryCompileException>(() => Compile("date = 20250812"));
        Assert.Throws<QueryCompileException>(() => Compile("date > @now"));
        Assert.Throws<QueryCompileException>(() => Compile("""date = "2025" """));
        Assert.Throws<QueryCompileException>(() => Compile("date IN (20250812)"));
    }

    [Fact]
    public void MonthLiteralMeansItsFirstInstantOnDateTimeFields()
    {
        CompiledQuery q = Compile("added > 2025-08");
        DateTimeOffset firstOfMonth = DateTimeOffset.Parse(
            "2025-08-01",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal);
        Assert.Equal(firstOfMonth.ToUnixTimeSeconds(), q.Parameters[0].Value);
    }

    [Fact]
    public void MalformedDateLiteralsFailToParse()
    {
        Assert.Throws<QueryParseException>(() => Compile("date = 2025-081"));
        Assert.Throws<QueryParseException>(() => Compile("date = 2025-13"));
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
