using Mirelia.Library.Database;
using Mirelia.Library.Playlists;

namespace Mirelia.Library.Import;

/// <summary>What linking did with one MusicBee playlist.</summary>
public enum MusicBeeLinkOutcome
{
    /// <summary>A row was found and stamped with the persistent id.</summary>
    Linked,

    /// <summary>The row already carried this persistent id; nothing to do.</summary>
    AlreadyLinked,

    /// <summary>No unclaimed row answers to either of its names.</summary>
    NoMatch,

    /// <summary>
    /// More than one row could be meant, or one row could be meant by more
    /// than one playlist. Left alone: a wrong link is permanent and would aim
    /// a later merge at the wrong playlist.
    /// </summary>
    Conflict,
}

/// <summary>One MusicBee playlist's result.</summary>
/// <param name="Reference">Its folder path and name in MusicBee.</param>
/// <param name="IsFolder">Whether MusicBee holds it as a folder.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="PlaylistName">The Mirelia playlist it refers to, when known.</param>
/// <param name="Reason">Why, when the outcome needs one.</param>
public sealed record MusicBeeLinkEntry(
    string Reference,
    bool IsFolder,
    MusicBeeLinkOutcome Outcome,
    string? PlaylistName,
    string? Reason);

/// <summary>What one linking run found.</summary>
/// <param name="Entries">One per playlist and folder in the export.</param>
/// <param name="Unlinked">
/// Mirelia playlists carrying no persistent id once this run is done, in
/// display order. These are the ones a later merge can never touch.
/// </param>
/// <param name="Applied">False for a dry run, which writes nothing.</param>
public sealed record MusicBeeLinkReport(
    IReadOnlyList<MusicBeeLinkEntry> Entries,
    IReadOnlyList<string> Unlinked,
    bool Applied)
{
    /// <summary>Rows this run stamped, or would stamp on a dry run.</summary>
    public int LinkedCount => Entries.Count(e => e.Outcome == MusicBeeLinkOutcome.Linked);

    /// <summary>Playlists already carrying their id before this run.</summary>
    public int AlreadyLinkedCount =>
        Entries.Count(e => e.Outcome == MusicBeeLinkOutcome.AlreadyLinked);

    /// <summary>Export entries with nothing to link to.</summary>
    public int NoMatchCount => Entries.Count(e => e.Outcome == MusicBeeLinkOutcome.NoMatch);

    /// <summary>Export entries left alone because the match was not certain.</summary>
    public int ConflictCount => Entries.Count(e => e.Outcome == MusicBeeLinkOutcome.Conflict);
}

/// <summary>
/// Records which Mirelia playlist each MusicBee one became, without touching
/// any playlist's name, contents or nesting.
/// </summary>
/// <remarks>
/// For a library imported before the persistent id was stored (schema v7).
/// That library's first merge would otherwise fall back to matching on names,
/// which cannot tell a previous import from a playlist made by hand. Running
/// this first turns that one risky run into a list that can be reviewed.
///
/// It is deliberately stricter than the importer's own fallback. The importer
/// deletes and recreates whatever it matches, so a wrong guess costs one
/// rebuild; a wrong id is permanent and silently aims the next merge at the
/// wrong playlist. Anything ambiguous is reported rather than resolved.
/// </remarks>
public sealed class MusicBeePlaylistLinker(LibraryDatabase db)
{
    /// <summary>
    /// Matches the export against the library. Writes nothing unless
    /// <paramref name="apply"/> is set.
    /// </summary>
    /// <param name="libraryFolder">The MusicBee folder holding the export.</param>
    /// <param name="apply">Whether to store the ids this run worked out.</param>
    public MusicBeeLinkReport Link(string libraryFolder, bool apply = false)
    {
        MusicBeeLibrary library = MusicBeeLibraryReader.Read(
            Path.Combine(libraryFolder, MusicBeeLibraryReader.FileName));
        Dictionary<string, string> references = MusicBeePlaylistIdentity.ReferencesOf(library);
        var store = new PlaylistStore(db);

        var resolved = new List<Resolution>(library.Playlists.Count);
        foreach (MusicBeePlaylist source in library.Playlists)
        {
            resolved.Add(Resolve(store, source, references[source.PersistentId]));
        }

        // A row two playlists both point at cannot be handed to either.
        HashSet<Resolution> contested = [.. resolved
            .Where(r => r.Match is not null)
            .GroupBy(r => r.Match!.Id)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)];

        var entries = new List<MusicBeeLinkEntry>(resolved.Count);
        var linkedIds = new HashSet<long>();
        foreach (Resolution r in resolved)
        {
            if (contested.Contains(r))
            {
                entries.Add(r.Entry with
                {
                    Outcome = MusicBeeLinkOutcome.Conflict,
                    PlaylistName = null,
                    Reason = $"'{r.Match!.Name}' is also the best match for another playlist",
                });
                continue;
            }

            entries.Add(r.Entry);
            if (r.Entry.Outcome == MusicBeeLinkOutcome.Linked && r.Match is { } match)
            {
                linkedIds.Add(match.Id);
                if (apply)
                {
                    store.SetMusicBeeId(match.Id, r.ExportId!);
                }
            }
        }

        // Everything the library keeps to itself once this run is done. Read
        // before the writes above would change it on a dry run, so both modes
        // report the same list.
        List<string> unlinked = [.. store.List()
            .Where(p => !linkedIds.Contains(p.Id) && !store.HasMusicBeeId(p.Id))
            .Select(p => p.Name)];

        return new MusicBeeLinkReport(entries, unlinked, apply);
    }

    private sealed record Resolution(
        MusicBeeLinkEntry Entry, PlaylistInfo? Match, string? ExportId);

    private static Resolution Resolve(
        PlaylistStore store, MusicBeePlaylist source, string reference)
    {
        MusicBeeLinkEntry Entry(MusicBeeLinkOutcome outcome, string? name, string? reason)
            => new(reference, source.IsFolder, outcome, name, reason);

        if (source.ExportId is not { } exportId)
        {
            return new Resolution(
                Entry(MusicBeeLinkOutcome.NoMatch, null, "the export carries no persistent id"),
                null,
                null);
        }

        if (store.FindByMusicBeeId(exportId) is { } already)
        {
            return new Resolution(
                Entry(MusicBeeLinkOutcome.AlreadyLinked, already.Name, null), null, exportId);
        }

        (PlaylistInfo? byName, PlaylistInfo? byReference) =
            MusicBeePlaylistIdentity.Candidates(store, source, reference);

        if (byName is not null && byReference is not null && byName.Id != byReference.Id)
        {
            return new Resolution(
                Entry(
                    MusicBeeLinkOutcome.Conflict,
                    null,
                    $"both '{byName.Name}' and '{byReference.Name}' could be meant"),
                null,
                exportId);
        }

        if ((byName ?? byReference) is not { } match)
        {
            return new Resolution(
                Entry(MusicBeeLinkOutcome.NoMatch, null, "no playlist answers to either name"),
                null,
                exportId);
        }

        // A folder and a playlist are different things however alike their
        // names are; linking across would make a later merge replace the
        // wrong one.
        if ((match.Kind == PlaylistKind.Folder) != source.IsFolder)
        {
            return new Resolution(
                Entry(
                    MusicBeeLinkOutcome.Conflict,
                    match.Name,
                    source.IsFolder
                        ? $"'{match.Name}' is a playlist here, a folder in MusicBee"
                        : $"'{match.Name}' is a folder here, a playlist in MusicBee"),
                null,
                exportId);
        }

        return new Resolution(
            Entry(MusicBeeLinkOutcome.Linked, match.Name, null), match, exportId);
    }
}
