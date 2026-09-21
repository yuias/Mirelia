using Mirelia.Library.Database;
using Mirelia.Library.Import;
using Mirelia.Library.Playlists;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>
/// Linking a library imported before the persistent id was stored. The export
/// here needs no audio files: linking reads names and nothing else.
/// </summary>
public sealed class MusicBeeLinkTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _libraryDir;
    private readonly LibraryDatabase _db;

    public MusicBeeLinkTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"mirelia-link-{Guid.NewGuid():N}");
        _libraryDir = Path.Combine(_workDir, "MusicBee");
        Directory.CreateDirectory(_libraryDir);
        _db = LibraryDatabase.Open(Path.Combine(_workDir, "library.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void ADryRunReportsTheLinksWithoutWritingThem()
    {
        var store = new PlaylistStore(_db);
        store.CreateStatic("Favourites");
        WriteExport(Playlist("PL01", "Favourites"));

        MusicBeeLinkReport report = Link();

        Assert.False(report.Applied);
        Assert.Equal(1, report.LinkedCount);
        Assert.Equal(MusicBeeLinkOutcome.Linked, Assert.Single(report.Entries).Outcome);
        Assert.False(store.HasMusicBeeId(store.List().Single().Id));
    }

    [Fact]
    public void ApplyingStoresTheIdAndLeavesEverythingElseAlone()
    {
        var store = new PlaylistStore(_db);
        PlaylistInfo created = store.CreateStatic("Favourites");
        WriteExport(Playlist("PL01", "Favourites"));

        MusicBeeLinkReport report = Link(apply: true);

        Assert.True(report.Applied);
        Assert.Equal(created.Id, store.FindByMusicBeeId("PL01")?.Id);
        PlaylistInfo after = Assert.Single(store.List());
        Assert.Equal("Favourites", after.Name);
        Assert.Equal(PlaylistKind.Static, after.Kind);
    }

    [Fact]
    public void AHandMadePlaylistStaysUnlinkedAndIsReportedAsSuch()
    {
        var store = new PlaylistStore(_db);
        store.CreateStatic("Favourites");
        store.CreateStatic("Mine");
        WriteExport(Playlist("PL01", "Favourites"));

        MusicBeeLinkReport report = Link(apply: true);

        Assert.Equal(1, report.LinkedCount);
        Assert.Equal("Mine", Assert.Single(report.Unlinked));
    }

    [Fact]
    public void RunningItTwiceReportsTheSecondRunAsAlreadyLinked()
    {
        new PlaylistStore(_db).CreateStatic("Favourites");
        WriteExport(Playlist("PL01", "Favourites"));
        Link(apply: true);

        MusicBeeLinkReport report = Link(apply: true);

        Assert.Equal(0, report.LinkedCount);
        Assert.Equal(1, report.AlreadyLinkedCount);
        Assert.Empty(report.Unlinked);
    }

    [Fact]
    public void AFolderIsNotLinkedToAPlaylistOfTheSameName()
    {
        new PlaylistStore(_db).CreateStatic("Anime");
        WriteExport(Folder("FOLDER01", "Anime"));

        MusicBeeLinkReport report = Link(apply: true);

        MusicBeeLinkEntry entry = Assert.Single(report.Entries);
        Assert.Equal(MusicBeeLinkOutcome.Conflict, entry.Outcome);
        Assert.Contains("playlist here", entry.Reason);
        Assert.False(new PlaylistStore(_db).HasMusicBeeId(new PlaylistStore(_db).List().Single().Id));
    }

    [Fact]
    public void TwoPlaylistsWantingOneRowAreBothLeftAlone()
    {
        // The earlier import gave one of them the bare name and the other the
        // full reference; only the bare name survives here, so both resolve
        // to it.
        new PlaylistStore(_db).CreateStatic("Mixes");
        WriteExport(
            Folder("FOLDER01", "Rock"),
            Playlist("PL01", "Mixes"),
            Playlist("PL02", "Mixes", parent: "FOLDER01"));

        MusicBeeLinkReport report = Link(apply: true);

        Assert.Equal(0, report.LinkedCount);
        Assert.Equal(2, report.Entries.Count(e =>
            !e.IsFolder && e.Outcome == MusicBeeLinkOutcome.Conflict));
        Assert.Equal("Mixes", Assert.Single(report.Unlinked));
    }

    [Fact]
    public void APlaylistNamedAfterItsFullReferenceIsFound()
    {
        // What the importer produces for the second of two playlists sharing
        // a name: the reference rather than the bare name.
        new PlaylistStore(_db).CreateStatic("Rock/Mixes");
        WriteExport(
            Folder("FOLDER01", "Rock"),
            Playlist("PL02", "Mixes", parent: "FOLDER01"));

        MusicBeeLinkReport report = Link(apply: true);

        Assert.Equal("Rock/Mixes", new PlaylistStore(_db).FindByMusicBeeId("PL02")?.Name);
        Assert.Contains(report.Entries, e => e.Reference == "Rock/Mixes");
    }

    [Fact]
    public void AnExportEntryWithNothingToMatchIsReportedNotLinked()
    {
        WriteExport(Playlist("PL01", "Gone"));

        MusicBeeLinkReport report = Link(apply: true);

        MusicBeeLinkEntry entry = Assert.Single(report.Entries);
        Assert.Equal(MusicBeeLinkOutcome.NoMatch, entry.Outcome);
        Assert.Equal(0, report.LinkedCount);
    }

    [Fact]
    public void LinkingMakesTheNextMergeReplaceExactlyTheLinkedPlaylists()
    {
        var store = new PlaylistStore(_db);
        store.CreateStatic("Favourites");
        store.CreateStatic("Mine");
        WriteExport(Playlist("PL01", "Favourites"));
        Link(apply: true);

        // The promise the whole command exists for.
        Assert.Equal("Favourites", store.FindByMusicBeeId("PL01")?.Name);
        Assert.Null(store.FindByNameWithoutMusicBeeId("Favourites"));
        Assert.NotNull(store.FindByNameWithoutMusicBeeId("Mine"));
    }

    private MusicBeeLinkReport Link(bool apply = false)
        => new MusicBeePlaylistLinker(_db).Link(_libraryDir, apply);

    private void WriteExport(params string[] playlists)
    {
        File.WriteAllText(
            Path.Combine(_libraryDir, MusicBeeLibraryReader.FileName),
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<plist version=\"1.0\">\n<dict>\n"
            + "\t<key>Tracks</key>\n\t<dict>\n\t</dict>\n"
            + "\t<key>Playlists</key>\n\t<array>\n"
            + string.Join('\n', playlists)
            + "\n\t</array>\n</dict>\n</plist>\n");
    }

    private static string Playlist(string persistentId, string name, string? parent = null)
    {
        string parentLine = parent is null
            ? string.Empty
            : $"\t\t\t<key>Parent Persistent ID</key><string>{parent}</string>\n";

        return "\t\t<dict>\n"
            + $"\t\t\t<key>Playlist Persistent ID</key><string>{persistentId}</string>\n"
            + parentLine
            + $"\t\t\t<key>Name</key><string>{name}</string>\n"
            + "\t\t\t<key>Playlist Items</key>\n\t\t\t<array>\n\t\t\t</array>\n"
            + "\t\t</dict>";
    }

    private static string Folder(string persistentId, string name)
        => "\t\t<dict>\n"
            + $"\t\t\t<key>Playlist Persistent ID</key><string>{persistentId}</string>\n"
            + $"\t\t\t<key>Name</key><string>{name}</string>\n"
            + "\t\t\t<key>Folder</key><true/>\n"
            + "\t\t\t<key>Playlist Items</key>\n\t\t\t<array>\n\t\t\t</array>\n"
            + "\t\t</dict>";
}
