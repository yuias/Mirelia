using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Mirelia.Library.Querying;
using Mirelia.Query;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

namespace Mirelia.Library.Playlists;

/// <summary>Playlist kinds, matching <c>playlist.kind</c> values.</summary>
public enum PlaylistKind
{
    /// <summary>Ordered track list maintained by the user.</summary>
    Static,

    /// <summary>Rule-based; membership comes from evaluating the DSL.</summary>
    Smart,

    /// <summary>Grouping node in the playlist tree.</summary>
    Folder,
}

/// <summary>One playlist row.</summary>
/// <param name="Id">Playlist id.</param>
/// <param name="Name">Display name; unique across playlists.</param>
/// <param name="Kind">Static, smart, or folder.</param>
/// <param name="ParentId">Folder parent, or null at root.</param>
/// <param name="QueryText">Canonical DSL source; smart playlists only.</param>
public sealed record PlaylistInfo(
    long Id,
    string Name,
    PlaylistKind Kind,
    long? ParentId,
    string? QueryText);

/// <summary>
/// Playlist persistence and evaluation. The canonical persisted form of a
/// smart playlist is its DSL text (kickoff §6.1); this store canonicalizes
/// on save and caches evaluation results keyed on
/// <c>(playlist id, library_generation)</c> per §6.3.
/// </summary>
public sealed class PlaylistStore(LibraryDatabase db)
{
    private readonly Dictionary<long, (long Generation, long[] Ids)> _smartCache = [];

    /// <summary>All playlists, ordered for display.</summary>
    public List<PlaylistInfo> List()
    {
        using SqliteCommand cmd = db.Command(
            "SELECT id, name, kind, parent_id, query_text FROM playlist ORDER BY sort_order, id");
        using SqliteDataReader reader = cmd.ExecuteReader();
        var result = new List<PlaylistInfo>();
        while (reader.Read())
        {
            result.Add(new PlaylistInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseKind(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return result;
    }

    /// <summary>Finds a playlist by exact name.</summary>
    public PlaylistInfo? FindByName(string name)
        => List().FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// Finds the playlist a previous MusicBee import created for
    /// <paramref name="persistentId"/>, or null. The id survives a rename in
    /// either application, which the name does not.
    /// </summary>
    public PlaylistInfo? FindByMusicBeeId(string persistentId)
    {
        if (db.Scalar(
                """
                SELECT id FROM playlist
                WHERE mb_persistent_id = $p
                ORDER BY id
                LIMIT 1
                """,
                ("$p", persistentId)) is not long id)
        {
            return null;
        }

        return List().FirstOrDefault(p => p.Id == id);
    }

    /// <summary>
    /// Finds a playlist by exact name, ignoring rows a MusicBee import already
    /// claimed. Used as the import's fallback: restricting it this way keeps
    /// the id and name candidate sets disjoint, so a name match can never take
    /// a row that belongs to a different MusicBee playlist.
    /// </summary>
    public PlaylistInfo? FindByNameWithoutMusicBeeId(string name)
    {
        if (db.Scalar(
                """
                SELECT id FROM playlist
                WHERE name = $n AND mb_persistent_id IS NULL
                ORDER BY id
                LIMIT 1
                """,
                ("$n", name)) is not long id)
        {
            return null;
        }

        return List().FirstOrDefault(p => p.Id == id);
    }

    /// <summary>Whether a MusicBee import has claimed this row.</summary>
    public bool HasMusicBeeId(long id)
        => db.Scalar(
            "SELECT 1 FROM playlist WHERE id = $id AND mb_persistent_id IS NOT NULL",
            ("$id", id)) is not null;

    /// <summary>
    /// Records which MusicBee playlist this row came from. Called on every
    /// import path, including the one that matched by name, so a library
    /// imported before the id was stored is backfilled on its next run.
    /// </summary>
    public void SetMusicBeeId(long id, string persistentId)
        => db.Execute(
            "UPDATE playlist SET mb_persistent_id = $p WHERE id = $id",
            ("$p", persistentId),
            ("$id", id));

    /// <summary>Creates an empty static playlist.</summary>
    public PlaylistInfo CreateStatic(string name)
        => Insert(name, PlaylistKind.Static, queryText: null);

    /// <summary>Creates a folder for grouping playlists.</summary>
    public PlaylistInfo CreateFolder(string name)
        => Insert(name, PlaylistKind.Folder, queryText: null);

    /// <summary>
    /// Moves a playlist or folder under <paramref name="parentId"/>
    /// (null = root). The parent must be a folder, and moving a folder into
    /// its own subtree is rejected.
    /// </summary>
    public void SetParent(long id, long? parentId)
    {
        if (parentId is { } parent)
        {
            List<PlaylistInfo> all = List();
            PlaylistInfo target = all.FirstOrDefault(p => p.Id == parent)
                ?? throw new InvalidOperationException($"Playlist {parent} does not exist.");
            if (target.Kind != PlaylistKind.Folder)
            {
                throw new InvalidOperationException($"'{target.Name}' is not a folder.");
            }

            // Walk up from the target; hitting `id` means we'd create a loop.
            Dictionary<long, long?> parents = all.ToDictionary(p => p.Id, p => p.ParentId);
            for (long? cursor = parent; cursor is { } c; cursor = parents.GetValueOrDefault(c))
            {
                if (c == id)
                {
                    throw new InvalidOperationException(
                        "Cannot move a folder into its own subtree.");
                }
            }
        }

        db.Execute(
            "UPDATE playlist SET parent_id = $parent, modified_unix = $now WHERE id = $id",
            ("$parent", parentId),
            ("$now", Now()),
            ("$id", id));
    }

    /// <summary>
    /// Creates a smart playlist. The query is parsed, compiled (validating
    /// fields), checked for playlist-reference cycles, and persisted in
    /// canonical printed form.
    /// </summary>
    /// <exception cref="QueryParseException">Malformed DSL.</exception>
    /// <exception cref="QueryCompileException">Unknown fields or cycles.</exception>
    public PlaylistInfo CreateSmart(string name, string queryText)
    {
        string canonical = Validate(name, queryText);
        return Insert(name, PlaylistKind.Smart, canonical);
    }

    /// <summary>Replaces a smart playlist's query, with the same validation as create.</summary>
    public void UpdateSmartQuery(long id, string queryText)
    {
        PlaylistInfo playlist = Require(id);
        if (playlist.Kind != PlaylistKind.Smart)
        {
            throw new InvalidOperationException($"'{playlist.Name}' is not a smart playlist.");
        }

        string canonical = Validate(playlist.Name, queryText);
        db.Execute(
            "UPDATE playlist SET query_text = $q, modified_unix = $now WHERE id = $id",
            ("$q", canonical),
            ("$now", Now()),
            ("$id", id));
        _smartCache.Remove(id);

        // Another playlist may reference this one, and its membership just
        // changed with this query.
        db.BumpLibraryGeneration();
    }

    /// <summary>Renames a playlist; names stay unique.</summary>
    public void Rename(long id, string newName)
    {
        RequireUniqueName(newName, exceptId: id);
        db.Execute(
            "UPDATE playlist SET name = $n, modified_unix = $now WHERE id = $id",
            ("$n", newName),
            ("$now", Now()),
            ("$id", id));

        // References are by name, so a rename moves what they point at.
        db.BumpLibraryGeneration();
    }

    /// <summary>
    /// Deletes a playlist; entries cascade. Deleting a folder re-parents its
    /// children to the folder's own parent instead of orphaning them.
    /// </summary>
    public void Delete(long id)
    {
        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        db.Execute("""
            UPDATE playlist
            SET parent_id = (SELECT parent_id FROM playlist WHERE id = $id)
            WHERE parent_id = $id
            """, ("$id", id));
        db.Execute("DELETE FROM playlist WHERE id = $id", ("$id", id));
        _smartCache.Remove(id);
        // Smart playlists referencing this one by name change membership.
        db.BumpLibraryGeneration();
        tx.Commit();
    }

    /// <summary>Track ids of a static playlist, in position order.</summary>
    public List<long> GetStaticTracks(long playlistId)
    {
        using SqliteCommand cmd = db.Command(
            "SELECT track_id FROM playlist_entry WHERE playlist_id = $p ORDER BY position",
            ("$p", playlistId));
        using SqliteDataReader reader = cmd.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    /// <summary>Appends tracks to a static playlist (duplicates allowed).</summary>
    public void AddTracks(long playlistId, IEnumerable<long> trackIds)
    {
        PlaylistInfo playlist = Require(playlistId);
        if (playlist.Kind != PlaylistKind.Static)
        {
            throw new InvalidOperationException($"'{playlist.Name}' is not a static playlist.");
        }

        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        long next = (long)db.Scalar(
            "SELECT COALESCE(MAX(position) + 1, 0) FROM playlist_entry WHERE playlist_id = $p",
            ("$p", playlistId))!;
        foreach (long trackId in trackIds)
        {
            db.Execute(
                "INSERT INTO playlist_entry (playlist_id, position, track_id) VALUES ($p, $pos, $t)",
                ("$p", playlistId),
                ("$pos", next++),
                ("$t", trackId));
        }

        Touch(playlistId);
        db.BumpLibraryGeneration();
        tx.Commit();
    }

    /// <summary>
    /// Removes the given entries from a static playlist and renumbers the rest.
    /// Library tracks are untouched. Returns false, changing nothing, when any
    /// entry's position is out of range or no longer holds the expected track.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not a static playlist, or no such playlist.</exception>
    public bool RemoveEntries(long playlistId, IReadOnlyCollection<PlaylistEntryRef> entries)
    {
        PlaylistInfo playlist = Require(playlistId);
        if (playlist.Kind != PlaylistKind.Static)
        {
            throw new InvalidOperationException($"'{playlist.Name}' is not a static playlist.");
        }

        if (entries.Count == 0)
        {
            return true;
        }

        bool removed = false;
        Reorder(playlistId, ids =>
        {
            foreach (PlaylistEntryRef entry in entries)
            {
                if (entry.Position < 0
                    || entry.Position >= ids.Count
                    || ids[entry.Position] != entry.TrackId)
                {
                    return null;
                }
            }

            var drop = new HashSet<int>(entries.Select(e => e.Position));
            removed = true;
            return ids.Where((_, i) => !drop.Contains(i)).ToList();
        });
        return removed;
    }

    /// <summary>
    /// Removes tracks from a static playlist by id and renumbers the rest, for
    /// views whose rows do not carry entry positions. Each id is a multiset
    /// element: an id given <c>c</c> times removes its last <c>c</c> occurrences.
    /// Library tracks are untouched. Returns false, changing nothing, when any
    /// track occurs fewer times than requested.
    /// </summary>
    /// <exception cref="InvalidOperationException">Not a static playlist, or no such playlist.</exception>
    public bool RemoveOccurrences(long playlistId, IReadOnlyCollection<long> trackIds)
    {
        PlaylistInfo playlist = Require(playlistId);
        if (playlist.Kind != PlaylistKind.Static)
        {
            throw new InvalidOperationException($"'{playlist.Name}' is not a static playlist.");
        }

        if (trackIds.Count == 0)
        {
            return true;
        }

        var pending = new Dictionary<long, int>();
        foreach (long id in trackIds)
        {
            pending[id] = pending.GetValueOrDefault(id) + 1;
        }

        bool removed = false;
        Reorder(playlistId, ids =>
        {
            // Walking from the end takes the last occurrences first.
            var drop = new HashSet<int>();
            for (int i = ids.Count - 1; i >= 0; i--)
            {
                if (pending.TryGetValue(ids[i], out int left) && left > 0)
                {
                    pending[ids[i]] = left - 1;
                    drop.Add(i);
                }
            }

            if (pending.Values.Any(left => left > 0))
            {
                return null;
            }

            removed = true;
            return ids.Where((_, i) => !drop.Contains(i)).ToList();
        });
        return removed;
    }

    /// <summary>Moves the entry at <paramref name="from"/> to <paramref name="to"/>.</summary>
    public void Move(long playlistId, int from, int to)
    {
        Reorder(playlistId, ids =>
        {
            if (from < 0 || from >= ids.Count || to < 0 || to >= ids.Count)
            {
                return ids;
            }

            long moved = ids[from];
            ids.RemoveAt(from);
            ids.Insert(to, moved);
            return ids;
        });
    }

    /// <summary>
    /// Membership of any playlist: entries for static, evaluated (and
    /// cached per library generation) for smart.
    /// </summary>
    public long[] Evaluate(PlaylistInfo playlist)
    {
        if (playlist.Kind != PlaylistKind.Smart)
        {
            return [.. GetStaticTracks(playlist.Id)];
        }

        long generation = db.GetLibraryGeneration();
        if (_smartCache.TryGetValue(playlist.Id, out (long Generation, long[] Ids) cached)
            && cached.Generation == generation)
        {
            return cached.Ids;
        }

        // @now is resolved here, so time-relative playlists refresh on any
        // library write; a coarse but correct staleness bound for v1 (§6.3).
        CompiledQuery compiled = SqlCompiler.Compile(
            QueryParser.Parse(playlist.QueryText ?? ""), now: null, SmartPlaylistResolver);
        long[] ids = [.. TrackQueryRunner.Run(db, compiled)];
        _smartCache[playlist.Id] = (generation, ids);
        return ids;
    }

    /// <summary>
    /// Looks a smart playlist up by name, so <c>playlist =</c> can test
    /// membership of one. Static playlists resolve to null here and are
    /// matched against their entries instead.
    /// </summary>
    /// <remarks>
    /// Pass this to <see cref="SqlCompiler.Compile"/> anywhere a query is run
    /// against a real library; without it a reference to a smart playlist
    /// matches nothing at all.
    /// </remarks>
    public ParsedQuery? SmartPlaylistResolver(string name)
        => FindByName(name) is { Kind: PlaylistKind.Smart, QueryText: { } text }
            ? QueryParser.Parse(text)
            : null;

    // ---- internals -----------------------------------------------------

    private string Validate(string name, string queryText)
    {
        ParsedQuery parsed = QueryParser.Parse(queryText);

        // Cycles first: compiling inlines the playlists this one references,
        // and a loop would be found as a depth limit rather than as a cycle.
        RejectCycles(name, parsed);
        _ = SqlCompiler.Compile(parsed, now: null, SmartPlaylistResolver);
        return DslPrinter.Print(parsed);
    }

    /// <summary>
    /// DFS over playlist-name references: adding <paramref name="name"/>
    /// with <paramref name="query"/> must not close a reference loop (§6.3).
    /// </summary>
    private void RejectCycles(string name, ParsedQuery query)
    {
        Dictionary<string, List<string>> edges = List()
            .Where(p => p.Kind == PlaylistKind.Smart && p.QueryText is not null)
            .ToDictionary(
                p => p.Name,
                p => CollectPlaylistRefs(QueryParser.Parse(p.QueryText!).Where));
        edges[name] = CollectPlaylistRefs(query.Where);

        var visiting = new HashSet<string>();
        var done = new HashSet<string>();
        Visit(name);
        return;

        void Visit(string node)
        {
            if (done.Contains(node) || !edges.ContainsKey(node))
            {
                return;
            }

            if (!visiting.Add(node))
            {
                throw new QueryCompileException(
                    $"Playlist reference cycle involving '{node}'");
            }

            foreach (string next in edges[node])
            {
                Visit(next);
            }

            visiting.Remove(node);
            done.Add(node);
        }
    }

    private static List<string> CollectPlaylistRefs(QueryExpr expr)
    {
        var refs = new List<string>();
        Walk(expr);
        return refs;

        void Walk(QueryExpr e)
        {
            switch (e)
            {
                case PlaylistMembership p:
                    refs.Add(p.PlaylistName);
                    break;
                case OrExpr or_:
                    foreach (QueryExpr op in or_.Operands)
                    {
                        Walk(op);
                    }

                    break;
                case AndExpr and:
                    foreach (QueryExpr op in and.Operands)
                    {
                        Walk(op);
                    }

                    break;
                case NotExpr not:
                    Walk(not.Operand);
                    break;
            }
        }
    }

    private void Reorder(long playlistId, Func<List<long>, List<long>?> transform)
    {
        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        List<long> ids = GetStaticTracks(playlistId);
        List<long>? reordered = transform(ids);

        // Null means "leave the playlist as it is"; disposing rolls back the read.
        if (reordered is null)
        {
            return;
        }

        db.Execute("DELETE FROM playlist_entry WHERE playlist_id = $p", ("$p", playlistId));
        for (int i = 0; i < reordered.Count; i++)
        {
            db.Execute(
                "INSERT INTO playlist_entry (playlist_id, position, track_id) VALUES ($p, $pos, $t)",
                ("$p", playlistId),
                ("$pos", i),
                ("$t", reordered[i]));
        }

        Touch(playlistId);
        db.BumpLibraryGeneration();
        tx.Commit();
    }

    private PlaylistInfo Insert(string name, PlaylistKind kind, string? queryText)
    {
        RequireUniqueName(name, exceptId: null);
        long now = Now();
        long id = (long)db.Scalar("""
            INSERT INTO playlist (name, kind, sort_order, query_text, created_unix, modified_unix)
            VALUES ($n, $k, (SELECT COALESCE(MAX(sort_order) + 1, 0) FROM playlist), $q, $now, $now)
            RETURNING id
            """,
            ("$n", name),
            ("$k", KindText(kind)),
            ("$q", queryText),
            ("$now", now))!;
        return new PlaylistInfo(id, name, kind, null, queryText);
    }

    private PlaylistInfo Require(long id)
    {
        return List().FirstOrDefault(p => p.Id == id)
            ?? throw new InvalidOperationException($"Playlist {id} does not exist.");
    }

    private void RequireUniqueName(string name, long? exceptId)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Playlist name must not be empty.");
        }

        PlaylistInfo? existing = FindByName(name);
        if (existing is not null && existing.Id != exceptId)
        {
            // Names are the DSL's playlist reference key, so they must be unique.
            throw new InvalidOperationException($"A playlist named '{name}' already exists.");
        }
    }

    private void Touch(long playlistId)
    {
        db.Execute(
            "UPDATE playlist SET modified_unix = $now WHERE id = $id",
            ("$now", Now()),
            ("$id", playlistId));
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static string KindText(PlaylistKind kind) => kind switch
    {
        PlaylistKind.Static => "static",
        PlaylistKind.Smart => "smart",
        _ => "folder",
    };

    private static PlaylistKind ParseKind(string kind) => kind switch
    {
        "static" => PlaylistKind.Static,
        "smart" => PlaylistKind.Smart,
        _ => PlaylistKind.Folder,
    };
}
