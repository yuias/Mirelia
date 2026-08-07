using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Mirelia.Query.Compilation;

namespace Mirelia.Library.Querying;

/// <summary>A display row for query results.</summary>
/// <param name="Id">Track id.</param>
/// <param name="Title">Title, falling back to the file name at scan time.</param>
/// <param name="Artist">Artist name, or null.</param>
/// <param name="Album">Album name, or null.</param>
/// <param name="DurationMs">Duration in milliseconds.</param>
public sealed record TrackSummary(long Id, string? Title, string? Artist, string? Album, long DurationMs);

/// <summary>
/// Executes compiled queries. Results are ID arrays (kickoff §4.3); hydration
/// happens separately and only for the rows actually displayed.
/// </summary>
public static class TrackQueryRunner
{
    /// <summary>Runs a compiled query, returning matching track ids in query order.</summary>
    public static List<long> Run(LibraryDatabase db, CompiledQuery query)
    {
        using SqliteCommand cmd = db.Command(query.Sql);
        foreach ((string name, object value) in query.Parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        var ids = new List<long>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>Hydrates summaries for <paramref name="ids"/>, preserving their order.</summary>
    public static List<TrackSummary> Hydrate(LibraryDatabase db, IReadOnlyList<long> ids)
    {
        var byId = new Dictionary<long, TrackSummary>(ids.Count);
        foreach (long[] chunk in ids.Chunk(500))
        {
            string placeholders = string.Join(", ", chunk.Select((_, i) => $"$id{i}"));
            using SqliteCommand cmd = db.Command($"""
                SELECT t.id, t.title, ar.name, al.name, t.duration_ms
                FROM track t
                LEFT JOIN artist ar ON ar.id = t.artist_id
                LEFT JOIN album al ON al.id = t.album_id
                WHERE t.id IN ({placeholders})
                """);
            for (int i = 0; i < chunk.Length; i++)
            {
                cmd.Parameters.AddWithValue($"$id{i}", chunk[i]);
            }

            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var row = new TrackSummary(
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetInt64(4));
                byId[row.Id] = row;
            }
        }

        return ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
    }
}
