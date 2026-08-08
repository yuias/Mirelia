using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Mirelia.Library.Scanning;
using Xunit;

namespace Mirelia.Library.Tests;

public sealed class DateKeyTests
{
    [Theory]
    [InlineData("2025", null, 20250101)]
    [InlineData("2025-08-12", null, 20250812)]
    [InlineData("2025/10/5", null, 20251005)]
    [InlineData("2026/12/20", null, 20261220)]
    [InlineData("2025.03.07", null, 20250307)]
    [InlineData("2025-08", null, 20250801)]
    [InlineData("2025-08-12T09:30:00", null, 20250812)]
    [InlineData("2025-08-12 09:30", null, 20250812)]
    [InlineData("2025-99", null, 20250101)] // month out of range: keep year
    [InlineData("2025-02-99", null, 20250201)] // day out of range: keep month
    [InlineData("someday", 1999, 19990101)] // unparseable: numeric year
    [InlineData(null, 2001, 20010101)]
    [InlineData(null, null, null)]
    [InlineData("05/09/2020", 2020, 20200101)] // US order unsupported: year fallback
    public void ComputeNormalizesRealWorldSpellings(string? text, int? year, int? expected)
    {
        Assert.Equal(expected, DateKey.Compute(text, year));
    }

    [Fact]
    public void MixedSpellingsSortChronologically()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-dk-{Guid.NewGuid():N}.db");
        using var db = LibraryDatabase.Open(dbPath);
        try
        {
            // The user's exact example, inserted out of order.
            long d = Insert(db, "D", "2026/12/20");
            long b = Insert(db, "B", "2025-08-12");
            long a = Insert(db, "A", "2025");
            long c = Insert(db, "C", "2025/10/5");
            long undated = Insert(db, "U", null);

            List<long> sorted = TrackPager.SortIds(
                db, [d, b, a, c, undated], TrackSortOrder.Year);
            Assert.Equal([a, b, c, d, undated], sorted);
        }
        finally
        {
            db.Dispose();
            File.Delete(dbPath);
        }
    }

    [Fact]
    public void BackfillFillsMissingKeys()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-bf-{Guid.NewGuid():N}.db");
        using var db = LibraryDatabase.Open(dbPath);
        try
        {
            long id = Insert(db, "T", "2024/6/3");
            db.Execute("UPDATE track SET date_key = NULL");
            db.BackfillDateKeys();
            Assert.Equal(20240603L, db.Scalar(
                "SELECT date_key FROM track WHERE id = $id", ("$id", id)));
        }
        finally
        {
            db.Dispose();
            File.Delete(dbPath);
        }
    }

    private static long Insert(LibraryDatabase db, string title, string? dateText)
    {
        return (long)db.Scalar("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix, title, date_text, date_key,
                title_sort, title_search, duration_ms, sample_rate, channels,
                codec, is_lossless, added_unix, modified_unix)
            VALUES ($t, lower($t), 1, 0, $t, $d, $k, lower($t), lower($t),
                    1000, 44100, 2, 'flac', 1, 0, 0)
            RETURNING id
            """,
            ("$t", title),
            ("$d", dateText),
            ("$k", DateKey.Compute(dateText, null)))!;
    }
}
