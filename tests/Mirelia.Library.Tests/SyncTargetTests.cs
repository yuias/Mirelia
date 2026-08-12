using Mirelia.Library.Database;
using Mirelia.Library.Playlists;
using Mirelia.Library.Scanning;
using Mirelia.Library.Sync;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>
/// Planner and runner against a real fixture library written to a real
/// directory, since the delta is defined by what is on the destination.
/// </summary>
public sealed class SyncTargetTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _musicDir;
    private readonly string _deviceMusic;
    private readonly string _devicePlaylists;
    private readonly LibraryDatabase _db;
    private readonly PlaylistStore _playlists;
    private readonly SyncTargetStore _targets;
    private readonly List<long> _trackIds;

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public SyncTargetTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"mirelia-sync-{Guid.NewGuid():N}");
        _musicDir = Path.Combine(_workDir, "library");
        _deviceMusic = Path.Combine(_workDir, "device", "Music");
        _devicePlaylists = Path.Combine(_workDir, "device", "Playlists");
        Directory.CreateDirectory(_musicDir);
        foreach (string name in ScannerTests.TagFixtures)
        {
            File.Copy(Path.Combine(FixturesDir, name), Path.Combine(_musicDir, name));
        }

        _db = LibraryDatabase.Open(Path.Combine(_workDir, "library.db"));
        new LibraryScanner(_db).Scan(_musicDir);
        _playlists = new PlaylistStore(_db);
        _targets = new SyncTargetStore(_db);

        _trackIds = [];
        using var cmd = _db.Command("SELECT id FROM track ORDER BY id");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            _trackIds.Add(reader.GetInt64(0));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_workDir, recursive: true);
    }

    private SyncTargetConfig Config(SyncTargetConfig? overrides = null) =>
        (overrides ?? new SyncTargetConfig()) with
        {
            MusicRoot = _deviceMusic,
            PlaylistDir = _devicePlaylists,
        };

    private SyncPlan PlanFor(SyncTarget target, SyncPlanOptions? options = null)
    {
        using var sink = new FileSystemSink(_deviceMusic);
        return new SyncPlanner(_db, _playlists, _targets).Plan(target, sink, options);
    }

    private SyncResult Run(SyncPlan plan)
    {
        using var music = new FileSystemSink(_deviceMusic);
        using var playlistSink = new FileSystemSink(_devicePlaylists);
        return new SyncRunner(_targets).Run(plan, music, playlistSink);
    }

    private SyncTarget AllMusicTarget(SyncTargetConfig? overrides = null) =>
        _targets.Create(
            "Walkman",
            SyncTargetKind.Folder,
            Config(overrides) with { Scope = SyncScopeKind.AllMusic });

    [Fact]
    public void ConfigurationRoundTripsThroughTheStore()
    {
        SyncTarget created = _targets.Create("Card", SyncTargetKind.Folder, Config(new SyncTargetConfig
        {
            Scope = SyncScopeKind.Selected,
            PathStyle = SyncPathStyle.Absolute,
            DeviceRoot = @"A:\Music",
            Separator = "/",
            ReservedChars = ReservedCharMode.FullWidth,
            OrphanPolicy = OrphanPolicy.Keep,
        }));

        SyncTarget? loaded = _targets.Get(created.Id);
        Assert.NotNull(loaded);
        Assert.Equal(SyncTargetKind.Folder, loaded.Kind);
        Assert.Equal(SyncScopeKind.Selected, loaded.Config.Scope);
        Assert.Equal(@"A:\Music", loaded.Config.DeviceRoot);
        Assert.Equal("/", loaded.Config.Separator);
        Assert.Equal(ReservedCharMode.FullWidth, loaded.Config.ReservedChars);
        Assert.Equal(OrphanPolicy.Keep, loaded.Config.OrphanPolicy);

        PlaylistInfo playlist = _playlists.CreateStatic("Favourites");
        _targets.SetScope(created.Id, [playlist.Id]);
        Assert.Equal([playlist.Id], _targets.GetScope(created.Id));

        _targets.Delete(created.Id);
        Assert.Null(_targets.Get(created.Id));
        Assert.Empty(_targets.GetScope(created.Id));
    }

    [Fact]
    public void FirstRunCopiesEverythingAndTheSecondCopiesNothing()
    {
        SyncTarget target = AllMusicTarget();

        SyncPlan first = PlanFor(target);
        Assert.Equal(5, first.Copies.Count);
        Assert.All(first.Copies, c => Assert.Equal(CopyReason.New, c.Reason));
        Assert.True(first.BytesToCopy > 0);

        SyncResult result = Run(first);
        Assert.Equal(5, result.Copied);
        Assert.Empty(result.Failures);
        Assert.Equal(5, Directory.GetFiles(_deviceMusic, "*", SearchOption.AllDirectories).Length);

        SyncPlan second = PlanFor(target);
        Assert.Empty(second.Copies);
        Assert.Empty(second.Deletes);
        Assert.Equal(5, second.UnchangedCount);
    }

    [Fact]
    public void FilesDeletedOnTheDeviceAreCopiedAgain()
    {
        SyncTarget target = AllMusicTarget();
        SyncPlan plan = PlanFor(target);
        Run(plan);

        string victim = Directory.GetFiles(_deviceMusic, "*", SearchOption.AllDirectories)[0];
        File.Delete(victim);

        SyncPlan replan = PlanFor(target);
        SyncCopy copy = Assert.Single(replan.Copies);
        Assert.Equal(CopyReason.MissingOnTarget, copy.Reason);
    }

    [Fact]
    public void AChangedSourceIsCopiedAgain()
    {
        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));

        string source = (string)_db.Scalar("SELECT path FROM track ORDER BY id LIMIT 1")!;
        File.AppendAllText(source, "padding");
        new LibraryScanner(_db).Scan(_musicDir);

        SyncPlan replan = PlanFor(target);
        SyncCopy copy = Assert.Single(replan.Copies);
        Assert.Equal(CopyReason.SourceChanged, copy.Reason);
    }

    [Fact]
    public void LayoutChangesRelocateRatherThanDuplicate()
    {
        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));

        _targets.Update(target.Id, target.Name, target.Config with
        {
            LayoutTemplate = "{AlbumArtist}/{Title}",
        });
        SyncTarget updated = _targets.Get(target.Id)!;

        SyncPlan replan = PlanFor(updated);
        Assert.Equal(5, replan.Copies.Count);
        Assert.All(replan.Copies, c =>
        {
            Assert.Equal(CopyReason.Relocated, c.Reason);
            Assert.NotNull(c.PreviousRelPath);
        });

        Run(replan);
        Assert.Equal(5, Directory.GetFiles(_deviceMusic, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void NarrowingTheScopeRemovesWhatIsNoLongerWanted()
    {
        PlaylistInfo playlist = _playlists.CreateStatic("Two");
        _playlists.AddTracks(playlist.Id, _trackIds.Take(2));

        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));

        _targets.Update(target.Id, target.Name, target.Config with { Scope = SyncScopeKind.Selected });
        _targets.SetScope(target.Id, [playlist.Id]);

        SyncPlan replan = PlanFor(_targets.Get(target.Id)!);
        Assert.Equal(3, replan.Deletes.Count);

        Run(replan);
        Assert.Equal(2, Directory.GetFiles(_deviceMusic, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void KeepPolicyLeavesOrphansAlone()
    {
        PlaylistInfo playlist = _playlists.CreateStatic("Two");
        _playlists.AddTracks(playlist.Id, _trackIds.Take(2));

        SyncTarget target = AllMusicTarget(new SyncTargetConfig { OrphanPolicy = OrphanPolicy.Keep });
        Run(PlanFor(target));

        _targets.Update(target.Id, target.Name, target.Config with { Scope = SyncScopeKind.Selected });
        _targets.SetScope(target.Id, [playlist.Id]);

        Assert.Empty(PlanFor(_targets.Get(target.Id)!).Deletes);
    }

    [Fact]
    public void PlaylistsAreWrittenWithAbsoluteDevicePaths()
    {
        PlaylistInfo playlist = _playlists.CreateStatic("Road Trip");
        _playlists.AddTracks(playlist.Id, _trackIds);

        SyncTarget target = _targets.Create("Car", SyncTargetKind.Folder, Config(new SyncTargetConfig
        {
            Scope = SyncScopeKind.AllPlaylists,
            PathStyle = SyncPathStyle.Absolute,
            DeviceRoot = @"A:\Music",
            Separator = "\\",
        }));

        SyncPlan plan = PlanFor(target);
        PlaylistExport export = Assert.Single(plan.Playlists);
        Assert.Equal("Road Trip.m3u8", export.RelPath);

        SyncResult result = Run(plan);
        Assert.Equal(1, result.PlaylistsWritten);

        string[] lines = File.ReadAllLines(Path.Combine(_devicePlaylists, "Road Trip.m3u8"));
        Assert.Equal("#EXTM3U", lines[0]);
        string[] paths = [.. lines.Where(l => !l.StartsWith('#'))];
        Assert.Equal(5, paths.Length);
        Assert.All(paths, p => Assert.StartsWith(@"A:\Music\", p));
    }

    [Fact]
    public void RelativePlaylistPathsResolveAgainstThePlaylistFile()
    {
        PlaylistInfo playlist = _playlists.CreateStatic("Local");
        _playlists.AddTracks(playlist.Id, _trackIds);

        SyncTarget target = _targets.Create("Stick", SyncTargetKind.Folder, Config(new SyncTargetConfig
        {
            Scope = SyncScopeKind.AllPlaylists,
            PathStyle = SyncPathStyle.Relative,
            Separator = "/",
        }));

        Run(PlanFor(target));

        string[] paths = [.. File.ReadAllLines(Path.Combine(_devicePlaylists, "Local.m3u8"))
            .Where(l => !l.StartsWith('#'))];
        Assert.Equal(5, paths.Length);
        Assert.All(paths, p =>
        {
            Assert.DoesNotContain('\\', p);
            Assert.True(
                File.Exists(Path.GetFullPath(Path.Combine(_devicePlaylists, p))),
                p);
        });
    }

    [Fact]
    public void NestedPlaylistsFlattenIntoOneDirectory()
    {
        PlaylistInfo folder = _playlists.CreateFolder("Anime");
        PlaylistInfo playlist = _playlists.CreateStatic("2020s");
        _playlists.SetParent(playlist.Id, folder.Id);
        _playlists.AddTracks(playlist.Id, _trackIds.Take(1));

        SyncTarget target = _targets.Create("Flat", SyncTargetKind.Folder, Config(new SyncTargetConfig
        {
            Scope = SyncScopeKind.AllPlaylists,
        }));

        PlaylistExport export = Assert.Single(PlanFor(target).Playlists);
        Assert.Equal("Anime - 2020s.m3u8", export.RelPath);
    }

    [Fact]
    public void SelectingAFolderSyncsThePlaylistsBeneathIt()
    {
        PlaylistInfo folder = _playlists.CreateFolder("Anime");
        PlaylistInfo playlist = _playlists.CreateStatic("2020s");
        _playlists.SetParent(playlist.Id, folder.Id);
        _playlists.AddTracks(playlist.Id, _trackIds.Take(3));

        SyncTarget target = _targets.Create("Sub", SyncTargetKind.Folder, Config(new SyncTargetConfig
        {
            Scope = SyncScopeKind.Selected,
        }));
        _targets.SetScope(target.Id, [folder.Id]);

        SyncPlan plan = PlanFor(target);
        Assert.Single(plan.Playlists);
        Assert.Equal(3, plan.Copies.Count);
    }

    [Fact]
    public void RelativePathsOutsideTheMusicRootAreFlagged()
    {
        SyncTarget target = AllMusicTarget(new SyncTargetConfig
        {
            PathStyle = SyncPathStyle.Relative,
        });

        Assert.Contains(PlanFor(target).Warnings, w => w.Contains("'..'"));
    }

    [Fact]
    public void AbsolutePathsWithoutADeviceRootAreFlagged()
    {
        SyncTarget target = AllMusicTarget(new SyncTargetConfig
        {
            PathStyle = SyncPathStyle.Absolute,
        });

        Assert.Contains(PlanFor(target).Warnings, w => w.Contains("device root"));
    }

    [Fact]
    public void UnplayableCodecsAreSkippedAndRemovedIfAlreadyThere()
    {
        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));
        Assert.Equal(5, Directory.GetFiles(_deviceMusic, "*", SearchOption.AllDirectories).Length);

        // The fixtures cover mp3/aac/alac/flac/pcm; this device plays two.
        _targets.Update(target.Id, target.Name, target.Config with
        {
            Device = new DeviceProfile { SupportedCodecs = ["mp3", "aac"] },
        });

        SyncPlan replan = PlanFor(_targets.Get(target.Id)!);
        Assert.Equal(3, replan.SkippedByPolicyCount);
        Assert.Equal(3, replan.Deletes.Count);
        Assert.Empty(replan.Copies);

        Run(replan);
        Assert.Equal(2, Directory.GetFiles(_deviceMusic, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void SkipLosslessLeavesTheLossyTracksAlone()
    {
        SyncTarget target = AllMusicTarget(new SyncTargetConfig
        {
            Device = new DeviceProfile { SkipLossless = true },
        });

        SyncPlan plan = PlanFor(target);
        Assert.Equal(3, plan.SkippedByPolicyCount); // alac, flac, pcm
        Assert.Equal(2, plan.Copies.Count);
    }

    [Fact]
    public void AnEmptyDeviceProfileAcceptsEverything()
    {
        SyncTarget target = AllMusicTarget(new SyncTargetConfig
        {
            Device = new DeviceProfile { SupportedCodecs = [] },
        });

        SyncPlan plan = PlanFor(target);
        Assert.Equal(0, plan.SkippedByPolicyCount);
        Assert.Equal(5, plan.Copies.Count);
    }

    [Fact]
    public void ForcingEverythingRecopiesTracksTheDeltaCallsCurrent()
    {
        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));
        Assert.Empty(PlanFor(target).Copies);

        SyncPlan forced = PlanFor(target, new SyncPlanOptions { ForceAll = true });
        Assert.Equal(5, forced.Copies.Count);
        Assert.All(forced.Copies, c => Assert.Equal(CopyReason.Forced, c.Reason));
        Assert.Equal(5, Run(forced).Copied);
    }

    [Fact]
    public void ForcingOnePlaylistLeavesTheRestOnTheDelta()
    {
        PlaylistInfo playlist = _playlists.CreateStatic("Retagged");
        _playlists.AddTracks(playlist.Id, _trackIds.Take(2));

        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));

        SyncPlan forced = PlanFor(target, new SyncPlanOptions
        {
            ForcePlaylistIds = [playlist.Id],
        });
        Assert.Equal(2, forced.Copies.Count);
        Assert.Equal(3, forced.UnchangedCount);
    }

    [Fact]
    public void ForcingAFolderCoversThePlaylistsBeneathIt()
    {
        PlaylistInfo folder = _playlists.CreateFolder("Retagged");
        PlaylistInfo playlist = _playlists.CreateStatic("Batch 1");
        _playlists.SetParent(playlist.Id, folder.Id);
        _playlists.AddTracks(playlist.Id, _trackIds.Take(3));

        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));

        SyncPlan forced = PlanFor(target, new SyncPlanOptions
        {
            ForcePlaylistIds = [folder.Id],
        });
        Assert.Equal(3, forced.Copies.Count);
    }

    [Fact]
    public void ForcingDoesNotMaskARealReason()
    {
        SyncTarget target = AllMusicTarget();
        Run(PlanFor(target));

        string victim = Directory.GetFiles(_deviceMusic, "*", SearchOption.AllDirectories)[0];
        File.Delete(victim);

        SyncPlan forced = PlanFor(target, new SyncPlanOptions { ForceAll = true });
        Assert.Equal(5, forced.Copies.Count);
        Assert.Single(forced.Copies, c => c.Reason == CopyReason.MissingOnTarget);
    }

    [Fact]
    public void MissingSourceFilesAreCountedAndKeptOutOfPlaylists()
    {
        PlaylistInfo playlist = _playlists.CreateStatic("Ghosts");
        long ghost = (long)_db.Scalar("""
            INSERT INTO track (path, path_lower, size_bytes, mtime_unix, title,
                title_sort, title_search, duration_ms, sample_rate, channels,
                codec, is_lossless, added_unix, modified_unix)
            VALUES ('Z:\gone.flac', 'z:\gone.flac', 1, 0, 'Ghost',
                'ghost', 'ghost', 1000, 44100, 2, 'flac', 1, 0, 0)
            RETURNING id
            """)!;
        _playlists.AddTracks(playlist.Id, [ghost, _trackIds[0]]);

        SyncTarget target = _targets.Create("Ghosty", SyncTargetKind.Folder, Config(new SyncTargetConfig
        {
            Scope = SyncScopeKind.AllPlaylists,
        }));

        SyncPlan plan = PlanFor(target);
        Assert.Equal(1, plan.MissingSourceCount);
        Assert.Single(plan.Copies);

        Run(plan);
        string[] paths = [.. File.ReadAllLines(Path.Combine(_devicePlaylists, "Ghosts.m3u8"))
            .Where(l => !l.StartsWith('#'))];
        Assert.Single(paths);
    }
}
