using System.Text;
using Mirelia.Library.Database;
using Mirelia.Library.Import;
using Mirelia.Library.Playlists;
using Mirelia.Query;
using Mirelia.Query.Ast;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>
/// The MusicBee migration, against synthetic exports built over the real audio
/// fixtures — the importer scans them, so they have to be files a scanner
/// accepts.
/// </summary>
public sealed class MusicBeeImportTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _musicDir;
    private readonly string _libraryDir;
    private readonly LibraryDatabase _db;

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    /// <summary>Lays out a music folder, a MusicBee folder, and a database.</summary>
    public MusicBeeImportTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"mirelia-mb-{Guid.NewGuid():N}");
        _musicDir = Path.Combine(_workDir, "music");
        _libraryDir = Path.Combine(_workDir, "MusicBee");
        Directory.CreateDirectory(_musicDir);
        Directory.CreateDirectory(_libraryDir);
        foreach (string name in ScannerTests.TagFixtures)
        {
            File.Copy(Path.Combine(FixturesDir, name), Path.Combine(_musicDir, name));
        }

        _db = LibraryDatabase.Open(Path.Combine(_workDir, "library.db"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_workDir, recursive: true);
    }

    // ---- the export reader ------------------------------------------------

    [Fact]
    public void ReadsTrackStateFromTheExport()
    {
        string path = WriteExport(
            [
                Track(1, "fixture.mp3", rating: 80, plays: 12,
                    played: "2024-08-26T09:36:06Z", added: "2019-11-12T06:04:54Z"),
            ],
            []);

        MusicBeeLibrary library = MusicBeeLibraryReader.Read(path);

        MusicBeeTrack track = Assert.Single(library.Tracks);
        Assert.Equal(Path.Combine(_musicDir, "fixture.mp3"), track.Path);
        Assert.Equal(80, track.Rating);
        Assert.Equal(12, track.PlayCount);
        Assert.Equal(1724664966, track.LastPlayedUnix);
        Assert.Equal(1573538694, track.AddedUnix);
    }

    [Fact]
    public void AnUnratedNeverPlayedTrackReadsAsSuch()
    {
        string path = WriteExport([Track(1, "fixture.flac")], []);

        MusicBeeTrack track = Assert.Single(MusicBeeLibraryReader.Read(path).Tracks);

        Assert.Null(track.Rating);
        Assert.Equal(0, track.PlayCount);
        Assert.Null(track.LastPlayedUnix);
    }

    [Fact]
    public void EntriesThatAreNotFilesAreSkipped()
    {
        string path = WriteExport(
            [
                Track(1, "fixture.mp3"),
                """
                        <key>2</key>
                        <dict>
                            <key>Track ID</key><integer>2</integer>
                            <key>Name</key><string>A radio stream</string>
                            <key>Track Type</key><string>URL</string>
                        </dict>
                """,
            ],
            []);

        Assert.Single(MusicBeeLibraryReader.Read(path).Tracks);
    }

    [Fact]
    public void ReadsThePlaylistTreeWithItsOrder()
    {
        string path = WriteExport(
            [Track(1, "fixture.mp3"), Track(2, "fixture.flac")],
            [
                Folder("FOLDER01", "Anime"),
                Playlist("PL01", "Favourites", parent: "FOLDER01", trackIds: [2, 1]),
            ]);

        MusicBeeLibrary library = MusicBeeLibraryReader.Read(path);

        Assert.Equal(2, library.Playlists.Count);
        Assert.True(library.Playlists[0].IsFolder);
        Assert.Equal("Anime", library.Playlists[0].Name);
        Assert.Equal("FOLDER01", library.Playlists[1].ParentPersistentId);
        Assert.Equal([2, 1], library.Playlists[1].TrackIds);
    }

    // ---- the auto-playlist reader -----------------------------------------

    [Fact]
    public void ReadsConditionsWithNumberedOperands()
    {
        string file = WriteAutoPlaylist(
            "Rated",
            """
                <Conditions CombineMethod="All">
                  <Condition Field="Rating" Comparison="InRange" Value1="60" Value2="80" />
                </Conditions>
            """);

        MusicBeeAutoPlaylist playlist = MusicBeeAutoPlaylistReader.Read(file, PlaylistDir);

        MusicBeeCondition condition = Assert.Single(playlist.Conditions);
        Assert.Equal("Rating", condition.Field);
        Assert.Equal(["60", "80"], condition.Values);
    }

    [Fact]
    public void ADisabledLimitIsNoLimit()
    {
        string file = WriteAutoPlaylist(
            "Rated",
            """
                <Conditions CombineMethod="All">
                  <Condition Field="Rating" Comparison="IsNotNull" />
                </Conditions>
                <Limit Enabled="False" Count="25" Type="Items" />
            """);

        Assert.Null(MusicBeeAutoPlaylistReader.Read(file, PlaylistDir).Limit);
    }

    [Fact]
    public void AnEnabledLimitCarriesItsCount()
    {
        string file = WriteAutoPlaylist(
            "Rated",
            """
                <Conditions CombineMethod="All">
                  <Condition Field="Rating" Comparison="IsNotNull" />
                </Conditions>
                <Limit Enabled="True" Count="25" Type="Items" />
            """);

        Assert.Equal(25, MusicBeeAutoPlaylistReader.Read(file, PlaylistDir).Limit);
    }

    [Fact]
    public void AReferenceIsTheFolderPathWithoutTheExtension()
    {
        Assert.Equal(
            "Tmp/Filters/Live.vcl",
            MusicBeeAutoPlaylistReader.Reference(
                Path.Combine(PlaylistDir, "Tmp", "Filters", "Live.vcl.mbp"), PlaylistDir));
    }

    [Fact]
    public void AFolderReferenceKeepsItsTrailingSeparator()
    {
        Assert.Equal("Hidden/", MusicBeeAutoPlaylistReader.Reference(@"Hidden\", PlaylistDir));
    }

    // ---- the translator ---------------------------------------------------

    [Fact]
    public void TranslatesTextAndNumberConditions()
    {
        Assert.Equal(
            """album ^= "テスト" AND rating > 40""",
            Translate(
                Condition("Album", "StartsWith", "テスト"),
                Condition("Rating", "GreaterThan", "40")));
    }

    [Fact]
    public void InRangeBecomesBetween()
    {
        Assert.Equal(
            "rating BETWEEN 60 AND 80",
            Translate(Condition("Rating", "InRange", "60", "80")));
    }

    [Fact]
    public void IsNotNullBecomesANullTest()
    {
        Assert.Equal("rating != null", Translate(Condition("Rating", "IsNotNull")));
    }

    [Fact]
    public void InTheLastBecomesAnOffsetFromNow()
    {
        Assert.Equal(
            "added >= @now - 100d",
            Translate(Condition("FileDateAdded", "InTheLast", "100d")));
    }

    [Fact]
    public void MusicBeeMonthsBecomeTheDslMonthUnit()
    {
        // MusicBee's 'm' is months; the DSL's 'm' is minutes and 'mo' months.
        Assert.Equal(
            "added >= @now - 12mo",
            Translate(Condition("FileDateAdded", "InTheLast", "12m")));
    }

    [Fact]
    public void NotInTheLastFlipsTheComparison()
    {
        Assert.Equal(
            "lastplayed < @now - 6mo",
            Translate(Condition("LastPlayed", "NotInTheLast", "6m")));
    }

    [Fact]
    public void AnyCombinesConditionsWithOr()
    {
        Assert.Equal(
            """album ^= "テスト" OR album ^= "実験" """.TrimEnd(),
            Translate(
                matchAll: false,
                Condition("Album", "StartsWith", "テスト"),
                Condition("Album", "StartsWith", "実験")));
    }

    [Fact]
    public void PlaylistMembershipTranslatesBothWays()
    {
        Assert.Equal(
            """playlist = "Picks" """.TrimEnd(),
            Translate(Condition("Playlists", "Is", @"Tmp\Picks")));

        Assert.Equal(
            """playlist != "Picks" AND playlist != "Skip" """.TrimEnd(),
            Translate(Condition("Playlists", "IsNotIn", @"Tmp\Picks", @"Hidden\skip")));
    }

    [Fact]
    public void AFolderReferenceCoversThePlaylistsInside()
    {
        Assert.Equal(
            """playlist != "Skip" """.TrimEnd(),
            Translate(Condition("Playlists", "IsNotIn", @"Hidden\")));
    }

    [Fact]
    public void ASourcePlaylistNarrowsTheConditions()
    {
        var source = new MusicBeeAutoPlaylist(
            "Mixes",
            [@"Tmp\Picks"],
            MatchAll: true,
            [Condition("Rating", "IsNotNull")],
            Limit: null);

        AutoPlaylistTranslation result = AutoPlaylistTranslator.Translate(source, Resolver);

        Assert.NotNull(result.Query);
        Assert.Equal("""playlist = "Picks" AND rating != null""", DslPrinter.Print(result.Query));
    }

    [Fact]
    public void ALimitSurvives()
    {
        var source = new MusicBeeAutoPlaylist(
            "Recent", [], MatchAll: true, [Condition("Rating", "IsNotNull")], Limit: 25);

        AutoPlaylistTranslation result = AutoPlaylistTranslator.Translate(source, Resolver);

        Assert.NotNull(result.Query);
        Assert.Equal("rating != null LIMIT 25", DslPrinter.Print(result.Query));
    }

    [Fact]
    public void AFieldWithNoColumnStopsTheTranslation()
    {
        AutoPlaylistTranslation result = AutoPlaylistTranslator.Translate(
            new MusicBeeAutoPlaylist(
                "x", [], MatchAll: true, [Condition("Mood", "Is", "Calm")], Limit: null),
            Resolver);

        Assert.Null(result.Query);
        Assert.Contains(result.Problems, p => p.Contains("Mood", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownReferenceStopsTheTranslation()
    {
        AutoPlaylistTranslation result = AutoPlaylistTranslator.Translate(
            new MusicBeeAutoPlaylist(
                "x", [], MatchAll: true, [Condition("Playlists", "Is", "Nowhere")], Limit: null),
            Resolver);

        Assert.Null(result.Query);
        Assert.NotEmpty(result.Problems);
    }

    // ---- the importer -----------------------------------------------------

    [Fact]
    public void AppliesTheStateTheScannerNeverTouches()
    {
        WriteExport(
            [Track(1, "fixture.mp3", rating: 60, plays: 7,
                played: "2024-08-26T09:36:06Z", added: "2019-11-12T06:04:54Z")],
            []);

        MusicBeeImportReport report = Import();

        Assert.Equal(1, report.TracksMatched);
        Assert.Equal(0, report.TracksMissing);
        (long? rating, long plays, long? played, long added) = StateOf("fixture.mp3");
        Assert.Equal(60, rating);
        Assert.Equal(7, plays);
        Assert.Equal(1724664966, played);
        Assert.Equal(1573538694, added);
    }

    [Fact]
    public void ReportsTracksWithNoFileBehindThem()
    {
        WriteExport([Track(1, "fixture.mp3"), Track(2, "gone.mp3")], []);

        MusicBeeImportReport report = Import();

        Assert.Equal(1, report.TracksMatched);
        Assert.Equal(1, report.TracksMissing);
        Assert.Contains(report.Notes, n => n.Contains("gone.mp3", StringComparison.Ordinal));
    }

    [Fact]
    public void ImportsAStaticPlaylistInItsOwnOrder()
    {
        WriteExport(
            [Track(1, "fixture.mp3"), Track(2, "fixture.flac")],
            [Playlist("PL01", "Favourites", trackIds: [2, 1])]);

        Import();

        var store = new PlaylistStore(_db);
        PlaylistInfo playlist = Assert.Single(store.List(), p => p.Name == "Favourites");
        Assert.Equal(PlaylistKind.Static, playlist.Kind);
        Assert.Equal(
            [PathOf("fixture.flac"), PathOf("fixture.mp3")],
            store.GetStaticTracks(playlist.Id).Select(TrackPath));
    }

    [Fact]
    public void FoldersComeAcrossWithTheirNesting()
    {
        WriteExport(
            [Track(1, "fixture.mp3")],
            [
                Folder("FOLDER01", "Anime"),
                Playlist("PL01", "Favourites", parent: "FOLDER01", trackIds: [1]),
            ]);

        MusicBeeImportReport report = Import();

        Assert.Equal(1, report.FoldersCreated);
        List<PlaylistInfo> all = new PlaylistStore(_db).List();
        PlaylistInfo folder = Assert.Single(all, p => p.Kind == PlaylistKind.Folder);
        PlaylistInfo child = Assert.Single(all, p => p.Name == "Favourites");
        Assert.Equal(folder.Id, child.ParentId);
    }

    [Fact]
    public void AnAutoPlaylistBecomesASmartPlaylist()
    {
        WriteExport(
            [Track(1, "fixture.mp3", rating: 60)],
            [Playlist("PL01", "Rated", trackIds: [1])]);
        WriteAutoPlaylist(
            "Rated",
            """
                <Conditions CombineMethod="All">
                  <Condition Field="Rating" Comparison="IsNotNull" />
                </Conditions>
            """);

        MusicBeeImportReport report = Import();

        Assert.Equal(1, report.SmartPlaylists);
        Assert.Equal(0, report.SnapshotPlaylists);
        PlaylistInfo playlist = Assert.Single(new PlaylistStore(_db).List(), p => p.Name == "Rated");
        Assert.Equal(PlaylistKind.Smart, playlist.Kind);
        Assert.Equal("rating != null", playlist.QueryText);
    }

    [Fact]
    public void AnAutoPlaylistMayBuildOnAnother()
    {
        WriteExport(
            [Track(1, "fixture.mp3", rating: 60), Track(2, "fixture.flac")],
            [
                Playlist("PL01", "Rated", trackIds: [1]),
                Playlist("PL02", "Rated Lossy", trackIds: [1]),
            ]);
        WriteAutoPlaylist(
            "Rated",
            """
                <Conditions CombineMethod="All">
                  <Condition Field="Rating" Comparison="IsNotNull" />
                </Conditions>
            """);
        WriteAutoPlaylist(
            "Rated Lossy",
            """
                <Conditions CombineMethod="All">
                  <Condition Field="Playlists" Comparison="Is" Value="Rated" />
                  <Condition Field="Kind" Comparison="Is" Value="mp3" />
                </Conditions>
            """);

        MusicBeeImportReport report = Import();

        // The referenced playlist has to exist first for the reference to be
        // checkable when the referring one is saved.
        Assert.Equal(2, report.SmartPlaylists);
        var store = new PlaylistStore(_db);
        PlaylistInfo derived = Assert.Single(store.List(), p => p.Name == "Rated Lossy");
        Assert.Equal("""playlist = "Rated" AND codec = "mp3" """.TrimEnd(), derived.QueryText);
        Assert.Equal([PathOf("fixture.mp3")], store.Evaluate(derived).Select(TrackPath));
    }

    [Fact]
    public void AnUntranslatableAutoPlaylistIsFrozenAsItStands()
    {
        WriteExport(
            [Track(1, "fixture.mp3"), Track(2, "fixture.flac")],
            [Playlist("PL01", "Calm", trackIds: [1, 2])]);
        WriteAutoPlaylist(
            "Calm",
            """
                <Conditions CombineMethod="All">
                  <Condition Field="Mood" Comparison="Is" Value="Calm" />
                </Conditions>
            """);

        MusicBeeImportReport report = Import();

        Assert.Equal(1, report.SnapshotPlaylists);
        PlaylistInfo playlist = Assert.Single(new PlaylistStore(_db).List(), p => p.Name == "Calm");
        Assert.Equal(PlaylistKind.Static, playlist.Kind);
        Assert.Equal(2, new PlaylistStore(_db).GetStaticTracks(playlist.Id).Count);
        Assert.Contains(report.Notes, n => n.Contains("Mood", StringComparison.Ordinal));
    }

    [Fact]
    public void ClashingNamesFallBackToTheFullPath()
    {
        WriteExport(
            [Track(1, "fixture.mp3")],
            [
                Playlist("PL01", "Favourites", trackIds: [1]),
                Folder("FOLDER01", "Anime"),
                Playlist("PL02", "Favourites", parent: "FOLDER01", trackIds: [1]),
            ]);

        Import();

        List<string> names = [.. new PlaylistStore(_db).List().Select(p => p.Name)];
        Assert.Contains("Favourites", names);
        Assert.Contains("Anime/Favourites", names);
    }

    [Fact]
    public void AnExportThatIsNotThereIsAClearFailure()
    {
        Assert.Throws<FileNotFoundException>(() => Import());
    }

    // ---- helpers ----------------------------------------------------------

    private string PlaylistDir => Path.Combine(_libraryDir, "Playlists");

    private MusicBeeImportReport Import(MusicBeeImportOptions? options = null)
        => new MusicBeeImporter(_db).Import(_libraryDir, options);

    private string PathOf(string name) => Path.Combine(_musicDir, name);

    private string TrackPath(long id)
        => (string)_db.Scalar("SELECT path FROM track WHERE id = $id", ("$id", id))!;

    private (long? Rating, long Plays, long? Played, long Added) StateOf(string name)
    {
        using var cmd = _db.Command(
            "SELECT rating, play_count, last_played_unix, added_unix FROM track WHERE path_lower = $p",
            ("$p", PathOf(name).ToLowerInvariant()));
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        return (
            reader.IsDBNull(0) ? null : reader.GetInt64(0),
            reader.GetInt64(1),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private static MusicBeeCondition Condition(string field, string comparison, params string[] values)
        => new(field, comparison, values);

    /// <summary>
    /// Resolves the references the translator tests use: two playlists, plus
    /// a folder holding one of them.
    /// </summary>
    private static IReadOnlyList<string> Resolver(string reference) =>
        reference.Replace('\\', '/') switch
        {
            "Tmp/Picks" => ["Picks"],
            "Hidden/skip" => ["Skip"],
            "Hidden/" => ["Skip"],
            _ => [],
        };

    private static string Translate(params MusicBeeCondition[] conditions)
        => Translate(matchAll: true, conditions);

    private static string Translate(bool matchAll, params MusicBeeCondition[] conditions)
    {
        AutoPlaylistTranslation result = AutoPlaylistTranslator.Translate(
            new MusicBeeAutoPlaylist("x", [], matchAll, conditions, Limit: null), Resolver);
        Assert.Empty(result.Problems);
        Assert.NotNull(result.Query);
        return DslPrinter.Print(result.Query);
    }

    private string WriteAutoPlaylist(string name, string body)
    {
        Directory.CreateDirectory(PlaylistDir);
        string file = Path.Combine(PlaylistDir, name + MusicBeeAutoPlaylistReader.Extension);
        File.WriteAllText(
            file,
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <SmartPlaylist LiveUpdating="True">
              <Source Type="1">
                <Description />
            {body}
              </Source>
            </SmartPlaylist>
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return file;
    }

    /// <summary>Track entry for a file under the music folder.</summary>
    private string Track(
        int id,
        string name,
        int? rating = null,
        int plays = 0,
        string? played = null,
        string? added = null)
    {
        var body = new StringBuilder();
        body.Append($"\t\t<key>{id}</key>\n\t\t<dict>\n");
        body.Append($"\t\t\t<key>Track ID</key><integer>{id}</integer>\n");
        body.Append($"\t\t\t<key>Name</key><string>{name}</string>\n");
        body.Append("\t\t\t<key>Track Type</key><string>File</string>\n");
        if (rating is { } stars)
        {
            body.Append($"\t\t\t<key>Rating</key><integer>{stars}</integer>\n");
        }

        if (plays > 0)
        {
            body.Append($"\t\t\t<key>Play Count</key><integer>{plays}</integer>\n");
        }

        if (played is not null)
        {
            body.Append($"\t\t\t<key>Play Date UTC</key><date>{played}</date>\n");
        }

        if (added is not null)
        {
            body.Append($"\t\t\t<key>Date Added</key><date>{added}</date>\n");
        }

        body.Append($"\t\t\t<key>Location</key><string>{FileUrl(PathOf(name))}</string>\n");
        body.Append("\t\t</dict>");
        return body.ToString();
    }

    private static string Folder(string persistentId, string name)
        => $"""
                    <dict>
                        <key>Playlist Persistent ID</key><string>{persistentId}</string>
                        <key>Name</key><string>{name}</string>
                        <key>Folder</key><true/>
                        <key>Playlist Items</key>
                        <array>
                        </array>
                    </dict>
            """;

    private static string Playlist(
        string persistentId, string name, string? parent = null, params int[] trackIds)
    {
        var items = new StringBuilder();
        foreach (int id in trackIds)
        {
            items.Append(
                $"\t\t\t\t<dict><key>Track ID</key><integer>{id}</integer></dict>\n");
        }

        string parentLine = parent is null
            ? string.Empty
            : $"\t\t\t<key>Parent Persistent ID</key><string>{parent}</string>\n";

        return "\t\t<dict>\n"
            + $"\t\t\t<key>Playlist Persistent ID</key><string>{persistentId}</string>\n"
            + parentLine
            + $"\t\t\t<key>Name</key><string>{name}</string>\n"
            + "\t\t\t<key>Playlist Items</key>\n\t\t\t<array>\n"
            + items
            + "\t\t\t</array>\n\t\t</dict>";
    }

    private string WriteExport(IReadOnlyList<string> tracks, IReadOnlyList<string> playlists)
    {
        string path = Path.Combine(_libraryDir, MusicBeeLibraryReader.FileName);
        File.WriteAllText(
            path,
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
            + "<!DOCTYPE plist PUBLIC \"-//Apple Computer//DTD PLIST 1.0//EN\" "
            + "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n"
            + "<plist version=\"1.0\">\n<dict>\n"
            + "\t<key>Major Version</key><integer>1</integer>\n"
            + "\t<key>Tracks</key>\n\t<dict>\n"
            + string.Join('\n', tracks) + "\n"
            + "\t</dict>\n"
            + "\t<key>Playlists</key>\n\t<array>\n"
            + string.Join('\n', playlists) + "\n"
            + "\t</array>\n</dict>\n</plist>\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static string FileUrl(string path)
    {
        string[] parts = path.Split(Path.DirectorySeparatorChar);
        IEnumerable<string> escaped = parts.Select((p, i) => i == 0 ? p : Uri.EscapeDataString(p));
        return "file://localhost/" + string.Join('/', escaped);
    }
}
