using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Mirelia.Library.Playlists;
using Mirelia.Library.Scanning;
using Mirelia.Query;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;

namespace Mirelia.Library.Import;

/// <summary>What an import is doing, for a progress display.</summary>
/// <param name="Phase">Human-readable stage name.</param>
/// <param name="Completed">Items finished in this stage.</param>
/// <param name="Total">Items in this stage, or zero when not yet known.</param>
public readonly record struct MusicBeeImportProgress(string Phase, int Completed, int Total);

/// <summary>Knobs for one import run.</summary>
public sealed record MusicBeeImportOptions
{
    /// <summary>
    /// Whether to register the export's roots as watch folders and scan them.
    /// Off means the import only touches tracks already in the library.
    /// </summary>
    public bool ScanRoots { get; init; } = true;

    /// <summary>Whether playlists come across as well as track state.</summary>
    public bool ImportPlaylists { get; init; } = true;
}

/// <summary>What one import did.</summary>
/// <param name="TracksInExport">File-backed tracks MusicBee listed.</param>
/// <param name="TracksMatched">Those found in the library and updated.</param>
/// <param name="TracksMissing">Those with no matching file in the library.</param>
/// <param name="FoldersCreated">Playlist folders created.</param>
/// <param name="StaticPlaylists">Playlists imported as a fixed track list.</param>
/// <param name="SmartPlaylists">Auto-playlists whose rules were translated.</param>
/// <param name="SnapshotPlaylists">
/// Auto-playlists whose rules would not translate, imported as the tracks
/// they held at export time.
/// </param>
/// <param name="Notes">One line per thing the user should know about.</param>
public sealed record MusicBeeImportReport(
    int TracksInExport,
    int TracksMatched,
    int TracksMissing,
    int FoldersCreated,
    int StaticPlaylists,
    int SmartPlaylists,
    int SnapshotPlaylists,
    IReadOnlyList<string> Notes);

/// <summary>
/// One-way migration from MusicBee: the tracks it knew about, the state it
/// kept on them, and its playlists.
/// </summary>
/// <remarks>
/// Reads MusicBee's iTunes-compatible export rather than its own library
/// file — see <c>docs/musicbee-import.md</c> for why. The import owns the
/// user-state columns it writes and overwrites whatever is there: a track
/// this run has only just scanned holds nothing worth keeping.
/// </remarks>
public sealed class MusicBeeImporter(LibraryDatabase db)
{
    /// <summary>Rows per transaction while writing track state.</summary>
    private const int BatchSize = 500;

    /// <summary>How many missing files are named individually in the report.</summary>
    private const int MaxNamedMissing = 10;

    /// <summary>Folder MusicBee keeps its playlists in, below the library folder.</summary>
    private const string PlaylistFolderName = "Playlists";

    /// <summary>Runs an import against a MusicBee library folder.</summary>
    /// <param name="libraryFolder">
    /// The folder holding <c>iTunes Music Library.xml</c> — by default
    /// <c>%USERPROFILE%\Music\MusicBee</c>.
    /// </param>
    /// <param name="options">Knobs; defaults import everything.</param>
    /// <param name="progress">Stage reporting.</param>
    /// <param name="ct">Checked between tracks and between playlists.</param>
    /// <exception cref="FileNotFoundException">The export is not there.</exception>
    public MusicBeeImportReport Import(
        string libraryFolder,
        MusicBeeImportOptions? options = null,
        IProgress<MusicBeeImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        options ??= new MusicBeeImportOptions();
        var notes = new List<string>();

        string exportPath = Path.Combine(libraryFolder, MusicBeeLibraryReader.FileName);
        if (!File.Exists(exportPath))
        {
            throw new FileNotFoundException(
                $"'{MusicBeeLibraryReader.FileName}' is not in '{libraryFolder}'. "
                + "Switch on the iTunes-compatible export in MusicBee and let it write once.",
                exportPath);
        }

        progress?.Report(new MusicBeeImportProgress("Reading the export", 0, 0));
        MusicBeeLibrary library = MusicBeeLibraryReader.Read(exportPath);

        if (options.ScanRoots)
        {
            ScanRoots(library, notes, progress, ct);
        }

        Dictionary<string, long> byPath = LoadTrackIds();
        (int matched, List<MusicBeeTrack> missing) =
            ApplyTrackState(library, byPath, progress, ct);
        ReportMissing(missing, notes);

        (int folders, int statics, int smart, int snapshots) = options.ImportPlaylists
            ? ImportPlaylists(library, libraryFolder, byPath, notes, progress, ct)
            : (0, 0, 0, 0);

        db.BumpLibraryGeneration();
        return new MusicBeeImportReport(
            library.Tracks.Count, matched, missing.Count, folders, statics, smart, snapshots, notes);
    }

    /// <summary>
    /// Registers the directories the export's files actually live in and scans
    /// them. One root per drive, at the deepest folder that still covers every
    /// track on it, so nothing outside the library gets swept in.
    /// </summary>
    private void ScanRoots(
        MusicBeeLibrary library,
        List<string> notes,
        IProgress<MusicBeeImportProgress>? progress,
        CancellationToken ct)
    {
        var scanner = new LibraryScanner(db);
        foreach (string root in CommonRoots(library.Tracks))
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                notes.Add($"'{root}' is not reachable, so its tracks were not scanned.");
                continue;
            }

            progress?.Report(new MusicBeeImportProgress($"Scanning {root}", 0, 0));
            ScanResult result = scanner.Scan(
                root, new Progress<int>(n => progress?.Report(
                    new MusicBeeImportProgress($"Scanning {root}", n, 0))));
            notes.Add(
                $"Scanned '{root}': {result.Added} added, {result.Updated} updated, "
                + $"{result.Unchanged} unchanged.");
        }
    }

    /// <summary>Deepest directory per drive that still contains every track on it.</summary>
    internal static List<string> CommonRoots(IReadOnlyList<MusicBeeTrack> tracks)
    {
        var byDrive = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (MusicBeeTrack track in tracks)
        {
            string? directory = Path.GetDirectoryName(track.Path);
            string? drive = Path.GetPathRoot(track.Path);
            if (directory is null || string.IsNullOrEmpty(drive))
            {
                continue;
            }

            byDrive[drive] = byDrive.TryGetValue(drive, out string? current)
                ? CommonPrefix(current, directory, drive)
                : directory;
        }

        return [.. byDrive.Values.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static string CommonPrefix(string a, string b, string floor)
    {
        char separator = Path.DirectorySeparatorChar;
        string[] left = a.Split(separator);
        string[] right = b.Split(separator);

        int shared = 0;
        while (shared < left.Length
            && shared < right.Length
            && string.Equals(left[shared], right[shared], StringComparison.OrdinalIgnoreCase))
        {
            shared++;
        }

        string prefix = string.Join(separator, left[..shared]);
        return prefix.Length < floor.Length ? floor : prefix;
    }

    private Dictionary<string, long> LoadTrackIds()
    {
        var byPath = new Dictionary<string, long>(StringComparer.Ordinal);
        using SqliteCommand cmd = db.Command("SELECT id, path_lower FROM track");
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            byPath[reader.GetString(1)] = reader.GetInt64(0);
        }

        return byPath;
    }

    /// <summary>
    /// Writes the state MusicBee kept and the scanner never touches: rating,
    /// play count, last played, and the date the file was first seen.
    /// </summary>
    private (int Matched, List<MusicBeeTrack> Missing) ApplyTrackState(
        MusicBeeLibrary library,
        Dictionary<string, long> byPath,
        IProgress<MusicBeeImportProgress>? progress,
        CancellationToken ct)
    {
        const string sql = """
            UPDATE track
            SET rating           = $rating,
                play_count       = $plays,
                last_played_unix = $last,
                added_unix       = COALESCE($added, added_unix)
            WHERE id = $id
            """;

        var missing = new List<MusicBeeTrack>();
        int matched = 0;
        int processed = 0;

        LibraryDatabase.TransactionScope tx = db.BeginTransaction();
        try
        {
            foreach (MusicBeeTrack track in library.Tracks)
            {
                ct.ThrowIfCancellationRequested();
                if (!byPath.TryGetValue(Key(track.Path), out long id))
                {
                    missing.Add(track);
                }
                else
                {
                    db.Execute(
                        sql,
                        ("$rating", track.Rating),
                        ("$plays", track.PlayCount),
                        ("$last", track.LastPlayedUnix),
                        ("$added", track.AddedUnix),
                        ("$id", id));
                    matched++;
                }

                if (++processed % BatchSize == 0)
                {
                    tx.Commit();
                    tx.Dispose();
                    tx = db.BeginTransaction();
                    progress?.Report(new MusicBeeImportProgress(
                        "Applying ratings and play counts", processed, library.Tracks.Count));
                }
            }

            tx.Commit();
        }
        finally
        {
            tx.Dispose();
        }

        return (matched, missing);
    }

    private static void ReportMissing(List<MusicBeeTrack> missing, List<string> notes)
    {
        if (missing.Count == 0)
        {
            return;
        }

        notes.Add($"{missing.Count} tracks in the export are not in the library.");
        foreach (MusicBeeTrack track in missing.Take(MaxNamedMissing))
        {
            notes.Add($"  missing: {track.Path}");
        }

        if (missing.Count > MaxNamedMissing)
        {
            notes.Add($"  … and {missing.Count - MaxNamedMissing} more.");
        }
    }

    // ---- playlists ---------------------------------------------------------

    /// <summary>A MusicBee playlist paired with what it will become here.</summary>
    private sealed record Planned(
        MusicBeePlaylist Source,
        string Reference,
        string Name,
        MusicBeeAutoPlaylist? Rules)
    {
        /// <summary>Mirelia playlist once created.</summary>
        public PlaylistInfo? Created { get; set; }
    }

    private (int Folders, int Static, int Smart, int Snapshots) ImportPlaylists(
        MusicBeeLibrary library,
        string libraryFolder,
        Dictionary<string, long> byPath,
        List<string> notes,
        IProgress<MusicBeeImportProgress>? progress,
        CancellationToken ct)
    {
        string playlistFolder = Path.Combine(libraryFolder, PlaylistFolderName);
        Dictionary<string, MusicBeeAutoPlaylist> rules = MusicBeeAutoPlaylistReader
            .ReadFolder(playlistFolder)
            .ToDictionary(a => a.Reference, StringComparer.OrdinalIgnoreCase);

        List<Planned> plan = Plan(library, rules);
        var store = new PlaylistStore(db);
        var byReference = plan.ToDictionary(p => p.Reference, StringComparer.OrdinalIgnoreCase);
        var byPersistentId = plan.ToDictionary(p => p.Source.PersistentId, StringComparer.Ordinal);
        var trackIds = library.Tracks.ToDictionary(t => t.Id, t => t.Path);
        List<Planned> planned = InReferenceOrder(plan, byReference);

        int folders = 0, statics = 0, smart = 0, snapshots = 0;
        int done = 0;

        foreach (Planned item in planned)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new MusicBeeImportProgress(
                "Creating playlists", done++, planned.Count));

            if (item.Source.IsFolder)
            {
                item.Created = store.CreateFolder(item.Name);
                folders++;
                continue;
            }

            if (item.Rules is { } rule)
            {
                AutoPlaylistTranslation translation = AutoPlaylistTranslator.Translate(
                    rule,
                    reference => [.. Referenced(reference, byReference).Select(p => p.Name)]);

                if (translation.Query is { } query && TryCreateSmart(store, item, query, notes))
                {
                    smart++;
                    continue;
                }

                foreach (string problem in translation.Problems)
                {
                    notes.Add($"'{item.Reference}' stays a fixed list: {problem}.");
                }

                snapshots++;
            }
            else
            {
                statics++;
            }

            item.Created = store.CreateStatic(item.Name);
            long[] members = [.. item.Source.TrackIds
                .Select(id => trackIds.TryGetValue(id, out string? path) ? path : null)
                .OfType<string>()
                .Select(path => byPath.TryGetValue(Key(path), out long id) ? id : 0)
                .Where(id => id != 0)];
            if (members.Length > 0)
            {
                store.AddTracks(item.Created.Id, members);
            }
        }

        // Parents exist by now, so nesting is one pass with nothing to order.
        foreach (Planned item in planned)
        {
            if (item.Created is { } created
                && item.Source.ParentPersistentId is { } parentId
                && byPersistentId.TryGetValue(parentId, out Planned? parent)
                && parent.Created is { } parentCreated)
            {
                store.SetParent(created.Id, parentCreated.Id);
            }
        }

        return (folders, statics, smart, snapshots);
    }

    private static bool TryCreateSmart(
        PlaylistStore store, Planned item, ParsedQuery query, List<string> notes)
    {
        try
        {
            item.Created = store.CreateSmart(item.Name, DslPrinter.Print(query));
            return true;
        }
        catch (Exception ex) when (ex is QueryParseException
            or QueryCompileException
            or InvalidOperationException)
        {
            notes.Add($"'{item.Reference}' stays a fixed list: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Expands a MusicBee playlist reference. One ending in <c>/</c> names a
    /// folder and covers every playlist below it.
    /// </summary>
    /// <summary>
    /// Expands a MusicBee playlist reference. One ending in a separator names
    /// a folder and covers every playlist below it.
    /// </summary>
    private static List<Planned> Referenced(
        string rawReference, Dictionary<string, Planned> byReference)
    {
        // Rules write Windows separators; plans hold the '/' form.
        string reference = rawReference.Replace('\\', '/');

        if (!reference.EndsWith('/'))
        {
            return byReference.TryGetValue(reference, out Planned? exact) && !exact.Source.IsFolder
                ? [exact]
                : [];
        }

        return
        [
            .. byReference.Values
                .Where(p => !p.Source.IsFolder
                    && p.Reference.StartsWith(reference, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    /// <summary>
    /// Puts a playlist after everything it refers to, so that saving it can
    /// check the reference against a playlist that already exists.
    /// </summary>
    private static List<Planned> InReferenceOrder(
        List<Planned> planned, Dictionary<string, Planned> byReference)
    {
        var ordered = new List<Planned>(planned.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Planned item in planned)
        {
            Visit(item);
        }

        return ordered;

        void Visit(Planned item)
        {
            // Already placed, or being placed: a reference loop stops here and
            // leaves the playlist where the walk reached it.
            if (!seen.Add(item.Reference))
            {
                return;
            }

            foreach (string reference in ReferencesOf(item))
            {
                foreach (Planned dependency in Referenced(reference, byReference))
                {
                    Visit(dependency);
                }
            }

            ordered.Add(item);
        }
    }

    /// <summary>Every playlist reference a rule set names.</summary>
    private static IEnumerable<string> ReferencesOf(Planned item)
    {
        if (item.Rules is not { } rules)
        {
            yield break;
        }

        foreach (string source in rules.SourcePlaylists)
        {
            yield return source;
        }

        foreach (MusicBeeCondition condition in rules.Conditions)
        {
            if (!string.Equals(condition.Field, "Playlists", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string value in condition.Values)
            {
                yield return value;
            }
        }
    }

    /// <summary>
    /// Works out every playlist's reference and its final name before anything
    /// is created, because translating a rule needs to know what the playlists
    /// it points at will be called.
    /// </summary>
    private List<Planned> Plan(
        MusicBeeLibrary library, Dictionary<string, MusicBeeAutoPlaylist> rules)
    {
        var byId = library.Playlists.ToDictionary(p => p.PersistentId, StringComparer.Ordinal);
        var used = new HashSet<string>(
            new PlaylistStore(db).List().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        var planned = new List<Planned>(library.Playlists.Count);
        foreach (MusicBeePlaylist source in library.Playlists)
        {
            string reference = ReferenceOf(source, byId);
            planned.Add(new Planned(
                source,
                reference,
                UniqueName(source.Name, reference, used),
                rules.GetValueOrDefault(reference)));
        }

        return planned;
    }

    /// <summary>Folder path plus name, which is how a rule names a playlist.</summary>
    private static string ReferenceOf(
        MusicBeePlaylist playlist, Dictionary<string, MusicBeePlaylist> byId)
    {
        var parts = new List<string> { playlist.Name };
        var guard = new HashSet<string>(StringComparer.Ordinal) { playlist.PersistentId };

        string? parentId = playlist.ParentPersistentId;
        while (parentId is not null
            && guard.Add(parentId)
            && byId.TryGetValue(parentId, out MusicBeePlaylist? parent))
        {
            parts.Insert(0, parent.Name);
            parentId = parent.ParentPersistentId;
        }

        return string.Join('/', parts);
    }

    /// <summary>
    /// Names are unique per folder in MusicBee and unique library-wide here, so
    /// a clash falls back to the full path, and then to a counter.
    /// </summary>
    private static string UniqueName(string name, string reference, HashSet<string> used)
    {
        if (used.Add(name))
        {
            return name;
        }

        if (used.Add(reference))
        {
            return reference;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{reference} ({i})";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private static string Key(string path) => Path.GetFullPath(path).ToLowerInvariant();
}
