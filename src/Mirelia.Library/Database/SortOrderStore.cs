using System.Text.Json;
using Microsoft.Data.Sqlite;
using Mirelia.Library.Querying;

namespace Mirelia.Library.Database;

/// <summary>One user-defined sort order.</summary>
/// <param name="Id">Row id; the handle view settings store.</param>
/// <param name="Name">Display name; unique across sort orders.</param>
/// <param name="Spec">The terms it orders by.</param>
public sealed record SortOrderInfo(long Id, string Name, SortSpec Spec);

/// <summary>
/// Persists user-defined sort orders (schema v6). The built-in ones live in
/// <see cref="SortPresets"/>, not here — they are the same in every library
/// and the user may not edit or delete them.
/// </summary>
public sealed class SortOrderStore(LibraryDatabase db)
{
    private static readonly JsonSerializerOptions Options = new();

    /// <summary>All sort orders, in display order.</summary>
    public List<SortOrderInfo> List()
    {
        using SqliteCommand cmd = db.Command(
            "SELECT id, name, terms_json FROM sort_order ORDER BY position, id");
        using SqliteDataReader reader = cmd.ExecuteReader();
        var result = new List<SortOrderInfo>();
        while (reader.Read())
        {
            result.Add(new SortOrderInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseTerms(reader.GetString(2))));
        }

        return result;
    }

    /// <summary>Creates a sort order and returns its id.</summary>
    /// <param name="name">Display name; must not collide with an existing one.</param>
    /// <param name="spec">The terms it orders by.</param>
    public long Create(string name, SortSpec spec)
    {
        long position = db.Scalar("SELECT COALESCE(MAX(position), -1) + 1 FROM sort_order") is long p
            ? p
            : 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return (long)db.Scalar("""
            INSERT INTO sort_order (name, terms_json, position, created_unix, modified_unix)
            VALUES ($n, $t, $p, $c, $c)
            RETURNING id
            """,
            ("$n", name),
            ("$t", SerializeTerms(spec)),
            ("$p", position),
            ("$c", now))!;
    }

    /// <summary>Renames a sort order and replaces its terms.</summary>
    public void Update(long id, string name, SortSpec spec)
    {
        db.Execute("""
            UPDATE sort_order SET name = $n, terms_json = $t, modified_unix = $m
            WHERE id = $id
            """,
            ("$n", name),
            ("$t", SerializeTerms(spec)),
            ("$m", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
            ("$id", id));
    }

    /// <summary>
    /// Deletes a sort order. Views referencing it are not rewritten — they
    /// fall back to their preset the next time they load, which is what a
    /// stale reference has always meant here.
    /// </summary>
    public void Delete(long id)
        => db.Execute("DELETE FROM sort_order WHERE id = $id", ("$id", id));

    /// <summary>Stores a new display order, given the ids in the order wanted.</summary>
    public void Reorder(IReadOnlyList<long> idsInOrder)
    {
        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        for (int i = 0; i < idsInOrder.Count; i++)
        {
            db.Execute(
                "UPDATE sort_order SET position = $p WHERE id = $id",
                ("$p", i),
                ("$id", idsInOrder[i]));
        }

        tx.Commit();
    }

    private static string SerializeTerms(SortSpec spec)
        => JsonSerializer.Serialize(spec.Terms, Options);

    /// <summary>
    /// Reads stored terms, tolerating anything unreadable as "no terms" —
    /// a corrupt row must not take the sort selector down with it.
    /// </summary>
    private static SortSpec ParseTerms(string json)
    {
        try
        {
            return new SortSpec(JsonSerializer.Deserialize<List<SortTerm>>(json, Options) ?? []);
        }
        catch (JsonException)
        {
            return new SortSpec([]);
        }
    }
}
