namespace Mirelia.Library.Import;

using Mirelia.Library.Playlists;

/// <summary>
/// The rules that decide which Mirelia playlist a MusicBee one refers to.
/// Shared so <see cref="MusicBeeImporter"/> and
/// <see cref="MusicBeePlaylistLinker"/> cannot drift apart: the linker's whole
/// promise is that what it stamps is what a later <c>--merge</c> will match.
/// </summary>
internal static class MusicBeePlaylistIdentity
{
    /// <summary>Folder path plus name, which is how a rule names a playlist.</summary>
    internal static string ReferenceOf(
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

    /// <summary>Every reference in one export, keyed by persistent id.</summary>
    internal static Dictionary<string, string> ReferencesOf(MusicBeeLibrary library)
    {
        var byId = library.Playlists.ToDictionary(p => p.PersistentId, StringComparer.Ordinal);
        return library.Playlists.ToDictionary(
            p => p.PersistentId, p => ReferenceOf(p, byId), StringComparer.Ordinal);
    }

    /// <summary>
    /// Rows an unlinked MusicBee playlist could be. The bare name first
    /// because that is what an import normally creates; the reference second
    /// because that is the name an import falls back to when two playlists
    /// share one. Only rows no import has claimed are considered, which keeps
    /// this disjoint from matching on the persistent id.
    /// </summary>
    /// <param name="spokenFor">
    /// Rows a caller has already decided on but not yet written, so a run
    /// working out several playlists at once sees the same picture the
    /// database would give it afterwards.
    /// </param>
    internal static (PlaylistInfo? ByName, PlaylistInfo? ByReference) Candidates(
        PlaylistStore store,
        MusicBeePlaylist source,
        string reference,
        IReadOnlySet<long>? spokenFor = null)
    {
        PlaylistInfo? Free(string name)
            => store.FindByNameWithoutMusicBeeId(name) is { } row
                && spokenFor?.Contains(row.Id) != true
                ? row
                : null;

        return (Free(source.Name), reference == source.Name ? null : Free(reference));
    }
}
