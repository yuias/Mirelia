using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>
/// A <c>playlist = "X"</c> condition compiles to a correlated
/// <c>EXISTS (SELECT 1 FROM playlist_entry pe … WHERE pe.track_id = t.id …)</c>.
/// Membership is indexed by (playlist_id, position), so without a reverse
/// index that lookup scans every entry, once per candidate track and once per
/// reference — a playlist combining nine references never finished.
/// </summary>
public sealed class PlaylistMembershipIndexTests : IDisposable
{
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;

    public PlaylistMembershipIndexTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mirelia-idx-{Guid.NewGuid():N}.db");
        _db = LibraryDatabase.Open(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public void MembershipByTrackIsIndexed()
    {
        Assert.Equal(1L, _db.Scalar("""
            SELECT count(*) FROM sqlite_master
            WHERE type = 'index' AND name = 'ix_playlist_entry_track'
            """));
    }

    [Fact]
    public void TheMembershipSubqueryPlanUsesThatIndex()
    {
        // The shape SqlCompiler emits for `playlist = "X"`.
        string plan = QueryPlan("""
            SELECT t.id FROM track t
            WHERE EXISTS (
                SELECT 1 FROM playlist_entry pe
                JOIN playlist pl ON pl.id = pe.playlist_id
                WHERE pe.track_id = t.id AND pl.name = 'x')
            """);

        Assert.Contains("ix_playlist_entry_track", plan);
        // Belt and braces: a plan that still scans the entry table is the bug.
        Assert.DoesNotContain("SCAN pe", plan);
    }

    private string QueryPlan(string sql)
    {
        using SqliteCommand cmd = _db.Command("EXPLAIN QUERY PLAN " + sql);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
        {
            lines.Add(reader.GetString(3));
        }

        return string.Join(" | ", lines);
    }
}
