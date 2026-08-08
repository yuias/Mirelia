using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Mirelia.Library.Scanning;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>
/// End-to-end scan + query over the committed fixture files (all five v1
/// formats, including Japanese-tagged FLAC).
/// </summary>
public sealed class ScannerTests : IDisposable
{
    /// <summary>
    /// The five tagged format fixtures. The fixtures directory also holds
    /// gapless/seek material, so tests scan an isolated copy of just these.
    /// </summary>
    internal static readonly string[] TagFixtures =
    [
        "fixture.mp3", "fixture.flac", "fixture.wav",
        "fixture-aac.m4a", "fixture-alac.m4a",
    ];

    private readonly string _workDir;
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;
    private readonly ScanResult _result;

    private static string FixturesDir =>
        Path.Combine(AppContext.BaseDirectory, "fixtures");

    public ScannerTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"mirelia-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        foreach (string name in TagFixtures)
        {
            File.Copy(Path.Combine(FixturesDir, name), Path.Combine(_workDir, name));
        }

        _dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-test-{Guid.NewGuid():N}.db");
        _db = LibraryDatabase.Open(_dbPath);
        _result = new LibraryScanner(_db).Scan(_workDir);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
        Directory.Delete(_workDir, recursive: true);
    }

    private List<TrackSummary> Query(string dsl)
    {
        CompiledQuery compiled = SqlCompiler.Compile(QueryParser.Parse(dsl));
        return TrackQueryRunner.Hydrate(_db, TrackQueryRunner.Run(_db, compiled));
    }

    [Fact]
    public void AllFixtureFormatsAreScanned()
    {
        Assert.Equal(5, _result.Added);
        Assert.Equal(0, _result.Skipped);
    }

    [Theory]
    [InlineData("mp3", false)]
    [InlineData("aac", false)]
    [InlineData("alac", true)]
    [InlineData("flac", true)]
    [InlineData("pcm", true)]
    public void CodecAndLosslessnessAreDetected(string codec, bool lossless)
    {
        List<TrackSummary> rows = Query($"""codec = "{codec}" """);
        TrackSummary row = Assert.Single(rows);
        List<TrackSummary> viaFlag = Query($"""codec = "{codec}" AND is_lossless""");
        Assert.Equal(lossless ? 1 : 0, viaFlag.Count);
        Assert.NotNull(row.Title);
    }

    [Fact]
    public void TagsAreReadIncludingNumbers()
    {
        object? year = _db.Scalar(
            "SELECT year FROM track WHERE codec = 'mp3'");
        Assert.Equal(2020L, year);
        object? trackNo = _db.Scalar(
            "SELECT track_no FROM track WHERE codec = 'flac'");
        Assert.Equal(2L, trackNo);
    }

    [Fact]
    public void FullReleaseDatesSurviveAsDateText()
    {
        // The FLAC fixture carries DATE=2021-07-15; the numeric year column
        // keeps 2021 while date_text preserves the full string.
        Assert.Equal("2021-07-15", _db.Scalar(
            "SELECT date_text FROM track WHERE codec = 'flac'"));
        Assert.Equal(2021L, _db.Scalar(
            "SELECT year FROM track WHERE codec = 'flac'"));
    }

    [Fact]
    public void ForcedRescanRereadsUnchangedFiles()
    {
        _db.Execute("UPDATE track SET date_text = NULL");
        ScanResult forced = new LibraryScanner(_db).Scan(_workDir, force: true);
        Assert.Equal(5, forced.Updated);
        Assert.Equal("2021-07-15", _db.Scalar(
            "SELECT date_text FROM track WHERE codec = 'flac'"));
    }

    [Fact]
    public void ScanRemembersWatchFolders()
    {
        Assert.Contains(Path.GetFullPath(_workDir), LibraryScanner.GetWatchFolders(_db));
    }

    [Fact]
    public void WavRiffInfoArtistLandsOnArtist()
    {
        // TagLib# surfaces RIFF INFO's IART as album artist; the scanner
        // backfills the artist side so WAV rows are not artist-less.
        object? artist = _db.Scalar("""
            SELECT ar.name FROM track t
            JOIN artist ar ON ar.id = t.artist_id
            WHERE t.codec = 'pcm'
            """);
        Assert.Equal("Fixture Artist", artist);
    }

    [Fact]
    public void ArtistsAreInterned()
    {
        // Three fixtures share "Fixture Artist"; it must exist exactly once.
        Assert.Equal(1L, _db.Scalar(
            "SELECT count(*) FROM artist WHERE name = 'Fixture Artist'"));
    }

    [Fact]
    public void JapaneseContainsMatchIsKanaAndWidthInsensitive()
    {
        // Stored artist is 山田太郎; katakana query must not match (different
        // script), but hiragana/katakana folding must make ヤマダ == やまだ
        // on a hiragana-tagged value. Verify with the title テスト曲:
        Assert.Single(Query("""title =~ "テスト" """));
        Assert.Single(Query("""title =~ "ﾃｽﾄ" """)); // half-width
        Assert.Single(Query("""title =~ "てすと" """)); // hiragana
        Assert.Single(Query("""artist = "山田太郎" """));
    }

    [Fact]
    public void FreeTextSearchHitsFts()
    {
        Assert.Empty(Query("""any("budokan_nothing")"""));
        Assert.Single(Query("""any("実験アルバム")"""));
    }

    [Fact]
    public void SortKeysAreComputed()
    {
        object? sort = _db.Scalar(
            "SELECT title_search FROM track WHERE codec = 'flac'");
        Assert.Equal("てすと曲", sort);
    }

    [Fact]
    public void RescanLeavesUnchangedFilesAlone()
    {
        ScanResult second = new LibraryScanner(_db).Scan(_workDir);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Updated);
        Assert.Equal(5, second.Unchanged);
    }

    [Fact]
    public void LibraryGenerationBumpsOnChanges()
    {
        long generation = _db.GetLibraryGeneration();
        Assert.True(generation >= 1);
        ScanResult second = new LibraryScanner(_db).Scan(_workDir);
        Assert.Equal(0, second.Added);
        Assert.Equal(generation, _db.GetLibraryGeneration());
    }

    [Fact]
    public void DurationAndTechnicalFieldsArePlausible()
    {
        List<TrackSummary> all = Query("duration > 0");
        Assert.Equal(5, all.Count);
        Assert.Equal(5L, _db.Scalar("SELECT count(*) FROM track WHERE sample_rate = 44100"));
    }
}
