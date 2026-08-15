using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Mirelia.Library.Export;
using Mirelia.Library.Playlists;
using Mirelia.Library.Querying;
using Mirelia.Library.Scanning;
using Mirelia.Library.Sync;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;
using Mirelia.Query.Parsing;
using Mirelia.Sync.Mtp;

namespace Mirelia.App.Services;

/// <summary>
/// Async facade over the library database. Two connections, each serialized
/// by its own gate so the UI thread never blocks on SQLite; scans run on their
/// own connection again (WAL allows a writer alongside readers).
///
/// The split is between short reads and view queries. Everything the UI needs
/// while a view is on screen — row hydration, cover paths, view settings,
/// playlist lists, and every write — goes through <see cref="_db"/>. Building
/// a view (evaluating a playlist, sorting an id array, browsing the library)
/// goes through <see cref="_viewDb"/>, because those are the only reads that
/// can run long. Sharing one connection meant a slow playlist also stalled
/// scrolling and artwork, which is what turned "this view is slow" into "the
/// app is dead".
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _viewGate = new(1, 1);

    private string _dbPath;
    private LibraryDatabase _db;
    private PlaylistStore _playlists;
    private ViewSettingsStore _viewSettings;

    // The view connection and the playlist store bound to it. Statements are
    // not safe to run concurrently on one connection, so anything the view
    // queries reach — including the smart-playlist resolver — must be this
    // store, never the one on _db.
    private LibraryDatabase _viewDb;
    private PlaylistStore _viewPlaylists;

    // Scans and syncs run off the gate on their own connections, so the gate
    // cannot tell whether one is still writing. A switch has to wait for
    // them, and this is the only thing that knows they are in flight.
    private int _backgroundJobs;

    /// <summary>Opens (creating if needed) the library at <paramref name="dbPath"/>.</summary>
    public LibraryService(string dbPath)
    {
        _dbPath = dbPath;
        _db = LibraryDatabase.Open(dbPath);
        _playlists = new PlaylistStore(_db);
        _viewSettings = new ViewSettingsStore(_db);
        _viewDb = LibraryDatabase.Open(dbPath);
        _viewPlaylists = new PlaylistStore(_viewDb);
    }

    /// <summary>The library file currently open.</summary>
    public string DatabasePath => _dbPath;

    /// <summary>True while a scan or sync is writing on its own connection.</summary>
    public bool IsBusy => Volatile.Read(ref _backgroundJobs) > 0;

    /// <summary>
    /// Points this service at a different library file. The new database is
    /// opened before the old one is closed, so a bad path leaves the current
    /// library untouched. Callers own the invalidation of everything derived
    /// from the old library — track ids do not survive the move.
    /// </summary>
    /// <exception cref="InvalidOperationException">A scan or sync is running.</exception>
    public async Task SwitchToAsync(string dbPath)
    {
        if (IsBusy)
        {
            throw new InvalidOperationException(
                "A scan or sync is still running against the current library.");
        }

        // Both gates, always in this order — nothing else holds both, so the
        // order is enough to keep it deadlock-free.
        await _gate.WaitAsync().ConfigureAwait(false);
        await _viewGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // Both open before either closes, so a bad path leaves the current
            // library untouched.
            LibraryDatabase opened = await Task.Run(() => LibraryDatabase.Open(dbPath))
                .ConfigureAwait(false);
            LibraryDatabase openedForViews;
            try
            {
                openedForViews = await Task.Run(() => LibraryDatabase.Open(dbPath))
                    .ConfigureAwait(false);
            }
            catch
            {
                opened.Dispose();
                throw;
            }

            _db.Dispose();
            _viewDb.Dispose();
            _db = opened;
            _viewDb = openedForViews;
            _dbPath = dbPath;
            _playlists = new PlaylistStore(_db);
            _viewSettings = new ViewSettingsStore(_db);
            _viewPlaylists = new PlaylistStore(_viewDb);
        }
        finally
        {
            _viewGate.Release();
            _gate.Release();
        }
    }

    /// <summary>Per-view UI settings for a scope key.</summary>
    public Task<ViewSettings> GetViewSettingsAsync(
        string scopeKey, CancellationToken cancellationToken = default)
        => RunAsync(() => _viewSettings.Get(scopeKey), cancellationToken);

    /// <summary>Stores per-view UI settings.</summary>
    public Task SetViewSettingsAsync(string scopeKey, ViewSettings settings)
        => RunAsync<object?>(() =>
        {
            _viewSettings.Set(scopeKey, settings);
            return null;
        });

    /// <summary>Clears the saved column layout and widths of every view.</summary>
    public Task ClearAllViewColumnsAsync()
        => RunAsync<object?>(() =>
        {
            _viewSettings.ClearAllColumns();
            return null;
        });

    /// <summary>Re-sorts an id array in SQL by a preset.</summary>
    public Task<long[]> SortIdsAsync(
        long[] ids, TrackSortOrder order, CancellationToken cancellationToken = default)
        => RunViewAsync(() => TrackPager.SortIds(_viewDb, ids, order).ToArray(), cancellationToken);

    /// <summary>Re-sorts an id array in SQL by a header-click column sort.</summary>
    public Task<long[]> SortIdsAsync(
        long[] ids, ColumnSort sort, CancellationToken cancellationToken = default)
        => RunViewAsync(() => TrackPager.SortIds(_viewDb, ids, sort).ToArray(), cancellationToken);

    /// <summary>
    /// Contiguous group runs over an ordered id array, for a grouped layout.
    /// </summary>
    public Task<List<TrackGroupRun>> LoadGroupRunsAsync(
        long[] ids, TrackGroupField field, CancellationToken cancellationToken = default)
        => RunViewAsync(() => TrackGrouper.LoadRuns(_viewDb, ids, field), cancellationToken);

    /// <summary>All track ids in a browse order.</summary>
    public Task<long[]> BrowseAllAsync(
        TrackSortOrder order, CancellationToken cancellationToken = default)
        => RunViewAsync(() => TrackPager.BrowseAll(_viewDb, order).ToArray(), cancellationToken);

    /// <summary>All track ids sorted by a header-click column sort.</summary>
    public Task<long[]> BrowseAllAsync(
        ColumnSort sort, CancellationToken cancellationToken = default)
        => RunViewAsync(() => TrackPager.BrowseAll(_viewDb, sort).ToArray(), cancellationToken);

    /// <summary>Most recently added track ids.</summary>
    public Task<long[]> BrowseRecentlyAddedAsync(CancellationToken cancellationToken = default)
        => RunViewAsync(() => TrackPager.BrowseRecentlyAdded(_viewDb).ToArray(), cancellationToken);

    /// <summary>Free-text FTS search, artist-ordered.</summary>
    public Task<long[]> SearchAsync(string text, CancellationToken cancellationToken = default)
    {
        return RunViewAsync(() =>
        {
            // Built as an AST directly — no parsing, no injection surface.
            var query = new ParsedQuery(
                new FreeText(text),
                [new OrderTerm("artist", Descending: false)],
                Limit: null);
            CompiledQuery compiled = SqlCompiler.Compile(query);
            return TrackQueryRunner.Run(_viewDb, compiled).ToArray();
        },
        cancellationToken);
    }

    /// <summary>
    /// Runs a raw DSL query (§7.3 query bar). Parse/compile errors
    /// propagate. <c>Ordered</c> reports whether the query carried its own
    /// ORDER BY, in which case the caller must not re-sort.
    /// </summary>
    /// <exception cref="QueryParseException">Malformed DSL.</exception>
    /// <exception cref="QueryCompileException">Unknown fields.</exception>
    public Task<(long[] Ids, bool Ordered)> QueryDslAsync(
        string dsl, CancellationToken cancellationToken = default)
        => RunViewAsync(
            () =>
            {
                ParsedQuery parsed = QueryParser.Parse(dsl);
                CompiledQuery compiled = SqlCompiler.Compile(
                    parsed, now: null, _viewPlaylists.SmartPlaylistResolver);
                return (TrackQueryRunner.Run(_viewDb, compiled).ToArray(), parsed.OrderBy.Count > 0);
            },
            cancellationToken);

    /// <summary>Hydrates one page of grid rows.</summary>
    public Task<Dictionary<long, TrackPageRow>> HydrateAsync(long[] ids)
        => RunAsync(() => TrackPager.HydratePage(_db, ids));

    /// <summary>Absolute file path for a track, or null if unknown.</summary>
    public Task<string?> GetPathAsync(long id)
        => RunAsync(() => TrackPager.GetTrackPath(_db, id));

    /// <summary>Stored ReplayGain track gain (dB), or null if not scanned.</summary>
    public Task<double?> GetTrackGainAsync(long id)
        => RunAsync(() => TrackPager.GetTrackGain(_db, id));

    /// <summary>Tracks with no ReplayGain values yet.</summary>
    public Task<List<(long Id, string Path)>> GetTracksMissingReplayGainAsync()
        => RunAsync(() => TrackPager.GetTracksMissingReplayGain(_db));

    /// <summary>Stores scanned ReplayGain values.</summary>
    public Task SetTrackReplayGainAsync(long id, double gainDb, double peak)
        => RunAsync<object?>(() =>
        {
            TrackPager.SetTrackReplayGain(_db, id, gainDb, peak);
            return null;
        });

    /// <summary>Stores a track's rating (0-100, or null to clear it).</summary>
    public Task SetTrackRatingAsync(long id, int? rating)
        => RunAsync<object?>(() =>
        {
            TrackPager.SetTrackRating(_db, id, rating);
            return null;
        });

    /// <summary>Total track count.</summary>
    public Task<long> CountAsync()
        => RunAsync(() => TrackPager.Count(_db));

    /// <summary>
    /// Scans a folder on its own database connection so browsing stays
    /// responsive while the scan runs.
    /// </summary>
    public Task<ScanResult> ScanAsync(string directory, IProgress<int>? progress)
    {
        string path = _dbPath;
        return RunJobAsync(() =>
        {
            using var scanDb = LibraryDatabase.Open(path);
            return new LibraryScanner(scanDb).Scan(directory, progress);
        });
    }

    /// <summary>
    /// Force re-reads the whole library — watch folders plus tracked files
    /// outside them — so tag improvements (like date_text) reach every row.
    /// </summary>
    public Task<ScanResult> RescanAllAsync(IProgress<int>? progress)
    {
        string path = _dbPath;
        return RunJobAsync(() =>
        {
            using var scanDb = LibraryDatabase.Open(path);
            return LibraryScanner.RescanEverything(scanDb, progress);
        });
    }

    /// <summary>Scans dropped files/folders, same isolation as <see cref="ScanAsync"/>.</summary>
    public Task<ScanResult> ScanPathsAsync(IReadOnlyList<string> paths, IProgress<int>? progress)
    {
        string path = _dbPath;
        return RunJobAsync(() =>
        {
            using var scanDb = LibraryDatabase.Open(path);
            return new LibraryScanner(scanDb).ScanPaths(paths, progress);
        });
    }

    // ---- playlists -----------------------------------------------------

    /// <summary>All playlists.</summary>
    public Task<List<PlaylistInfo>> GetPlaylistsAsync()
        => RunAsync(_playlists.List);

    /// <summary>Membership (evaluated for smart) of one playlist.</summary>
    public Task<long[]> EvaluatePlaylistAsync(
        PlaylistInfo playlist, CancellationToken cancellationToken = default)
        => RunViewAsync(() => _viewPlaylists.Evaluate(playlist), cancellationToken);

    /// <summary>Creates a static playlist.</summary>
    public Task<PlaylistInfo> CreateStaticPlaylistAsync(string name)
        => RunAsync(() => _playlists.CreateStatic(name));

    /// <summary>Creates a smart playlist; validation errors propagate.</summary>
    public Task<PlaylistInfo> CreateSmartPlaylistAsync(string name, string queryText)
        => RunAsync(() => _playlists.CreateSmart(name, queryText));

    /// <summary>Replaces a smart playlist's query; validation errors propagate.</summary>
    public Task UpdateSmartQueryAsync(long playlistId, string queryText)
        => RunAsync<object?>(() =>
        {
            _playlists.UpdateSmartQuery(playlistId, queryText);
            return null;
        });

    /// <summary>Creates a playlist folder.</summary>
    public Task<PlaylistInfo> CreateFolderAsync(string name)
        => RunAsync(() => _playlists.CreateFolder(name));

    /// <summary>Moves a playlist/folder under a folder (null = top level).</summary>
    public Task SetPlaylistParentAsync(long id, long? parentId)
        => RunAsync<object?>(() =>
        {
            _playlists.SetParent(id, parentId);
            return null;
        });

    /// <summary>
    /// Moves a static playlist entry (grid drag reorder). <paramref name="to"/>
    /// follows <see cref="PlaylistStore.Move"/>'s contract: the index in the
    /// list <em>after</em> the entry at <paramref name="from"/> is removed.
    /// </summary>
    public Task MovePlaylistEntryAsync(long playlistId, int from, int to)
        => RunAsync<object?>(() =>
        {
            _playlists.Move(playlistId, from, to);
            return null;
        });

    /// <summary>Appends tracks to a static playlist.</summary>
    public Task AddToPlaylistAsync(long playlistId, IReadOnlyList<long> trackIds)
        => RunAsync<object?>(() =>
        {
            _playlists.AddTracks(playlistId, trackIds);
            return null;
        });

    /// <summary>Renames a playlist.</summary>
    public Task RenamePlaylistAsync(long playlistId, string newName)
        => RunAsync<object?>(() =>
        {
            _playlists.Rename(playlistId, newName);
            return null;
        });

    /// <summary>Deletes a playlist.</summary>
    public Task DeletePlaylistAsync(long playlistId)
        => RunAsync<object?>(() =>
        {
            _playlists.Delete(playlistId);
            return null;
        });

    /// <summary>Exports a playlist to .m3u8.</summary>
    public Task<ExportResult> ExportPlaylistAsync(
        PlaylistInfo playlist, string outPath, M3u8Options? options = null)
        => RunAsync(() => M3u8Exporter.Export(_db, _playlists.Evaluate(playlist), outPath, options));

    // ---- sync ----------------------------------------------------------

    /// <summary>All configured sync targets.</summary>
    public Task<List<SyncTarget>> GetSyncTargetsAsync()
        => RunAsync(() => new SyncTargetStore(_db).List());

    /// <summary>Playlist ids a target syncs when its scope is "selected".</summary>
    public Task<List<long>> GetSyncScopeAsync(long targetId)
        => RunAsync(() => new SyncTargetStore(_db).GetScope(targetId));

    /// <summary>Creates a target and returns it.</summary>
    public Task<SyncTarget> CreateSyncTargetAsync(
        string name, SyncTargetKind kind, SyncTargetConfig config)
        => RunAsync(() => new SyncTargetStore(_db).Create(name, kind, config));

    /// <summary>
    /// Portable devices Windows can currently see, for the target editor.
    /// Empty everywhere else, and when nothing is plugged in.
    /// </summary>
    public static Task<IReadOnlyList<MtpDeviceInfo>> GetMtpDevicesAsync()
        => OperatingSystem.IsWindowsVersionAtLeast(5)
            ? Task.Run(MtpDevices.List)
            : Task.FromResult<IReadOnlyList<MtpDeviceInfo>>([]);

    /// <summary>Stores a target's name, settings, and selected playlists.</summary>
    public Task SaveSyncTargetAsync(long id, string name, SyncTargetConfig config, IEnumerable<long> scope)
        => RunAsync<object?>(() =>
        {
            var store = new SyncTargetStore(_db);
            store.Update(id, name, config);
            store.SetScope(id, scope);
            return null;
        });

    /// <summary>Deletes a target; its file records go with it.</summary>
    public Task DeleteSyncTargetAsync(long id)
        => RunAsync<object?>(() =>
        {
            new SyncTargetStore(_db).Delete(id);
            return null;
        });

    /// <summary>
    /// Computes the preflight plan. Runs on its own connection: planning
    /// stats every destination file and would otherwise hold the read gate
    /// for the whole sweep.
    /// </summary>
    public Task<SyncPlan> PlanSyncAsync(
        SyncTarget target, SyncPlanOptions? options = null, CancellationToken ct = default)
    {
        string path = _dbPath;
        return RunJobAsync(
            () =>
            {
                using var syncDb = LibraryDatabase.Open(path);
                using ISyncSinkFactory sinks = OpenSinks(target);
                using ISyncFileSink sink = sinks.Open(target.Config.MusicRoot);
                var planner = new SyncPlanner(
                    syncDb, new PlaylistStore(syncDb), new SyncTargetStore(syncDb));
                return planner.Plan(target, sink, options, ct);
            },
            ct);
    }

    /// <summary>Codecs actually present in the library, for the device profile editor.</summary>
    public Task<List<string>> GetCodecsAsync()
        => RunAsync(() =>
        {
            var codecs = new List<string>();
            using SqliteCommand cmd = _db.Command("SELECT DISTINCT codec FROM track ORDER BY codec");
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                codecs.Add(reader.GetString(0));
            }

            return codecs;
        });

    /// <summary>Executes a plan, on its own connection for the same reason.</summary>
    public Task<SyncResult> RunSyncAsync(
        SyncPlan plan, IProgress<SyncProgress>? progress, CancellationToken ct = default)
    {
        string path = _dbPath;
        return RunJobAsync(
            () =>
            {
                using var syncDb = LibraryDatabase.Open(path);
                using ISyncSinkFactory sinks = OpenSinks(plan.Target);
                using ISyncFileSink music = sinks.Open(plan.Target.Config.MusicRoot);
                using ISyncFileSink playlists = sinks.Open(plan.Target.Config.PlaylistDir);
                return new SyncRunner(new SyncTargetStore(syncDb))
                    .Run(plan, music, playlists, progress, ct);
            },
            ct);
    }

    /// <summary>
    /// Opens the byte-moving layer a target needs. MTP wants both roots on one
    /// device session, so the factory outlives the sinks it hands out.
    /// </summary>
    private static ISyncSinkFactory OpenSinks(SyncTarget target)
    {
        if (target.Kind != SyncTargetKind.Mtp)
        {
            return new FileSystemSinkFactory();
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(5))
        {
            throw new PlatformNotSupportedException(
                "MTP targets need Windows Portable Devices.");
        }

        if (string.IsNullOrEmpty(target.Config.DeviceId))
        {
            throw new InvalidOperationException($"Target '{target.Name}' names no device.");
        }

        return MtpSinkFactory.Open(target.Config.DeviceId, target.Config.DeviceLabel ?? target.Name);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        _viewGate.Dispose();
        _db.Dispose();
        _viewDb.Dispose();
    }

    private async Task<T> RunJobAsync<T>(Func<T> job, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _backgroundJobs);
        try
        {
            return await Task.Run(job, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _backgroundJobs);
        }
    }

    /// <summary>
    /// Runs a view query on the view connection. Same contract as
    /// <see cref="RunAsync"/>; the separate connection is what keeps a long
    /// one from blocking hydration, artwork and settings behind it.
    /// </summary>
    private Task<T> RunViewAsync<T>(
        Func<T> operation, CancellationToken cancellationToken = default)
        => RunOnAsync(operation, _viewGate, () => _viewDb, cancellationToken);

    private Task<T> RunAsync<T>(
        Func<T> operation, CancellationToken cancellationToken = default)
        => RunOnAsync(operation, _gate, () => _db, cancellationToken);

    private async Task<T> RunOnAsync<T>(
        Func<T> operation,
        SemaphoreSlim gate,
        Func<LibraryDatabase> current,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Read under the gate: a library switch holds it while swapping, and
        // the statement to abandon is the one on the connection we started on.
        LibraryDatabase db = current();
        try
        {
            // Cancelling the token alone would only abandon the wait — the
            // statement already executing keeps a core busy until it finishes.
            // sqlite3_interrupt is what actually stops it, and disposing the
            // registration below waits for that callback, so a late interrupt
            // cannot reach the next operation through the gate.
            using CancellationTokenRegistration registration =
                cancellationToken.Register(db.Interrupt);
            return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
            when (LibraryDatabase.IsInterrupted(ex) && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }
}
