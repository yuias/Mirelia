using Mirelia.Library.Database;
using Mirelia.Library.Export;
using Mirelia.Library.Playlists;
using Mirelia.Library.Querying;
using Mirelia.Library.Scanning;
using Mirelia.Query.Ast;
using Mirelia.Query.Compilation;

namespace Mirelia.App.Services;

/// <summary>
/// Async facade over the library database. One read connection, serialized
/// by a gate so the UI thread never blocks on SQLite; scans run on their own
/// connection (WAL allows a writer alongside readers).
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;
    private readonly PlaylistStore _playlists;
    private readonly ViewSettingsStore _viewSettings;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Opens (creating if needed) the library at <paramref name="dbPath"/>.</summary>
    public LibraryService(string dbPath)
    {
        _dbPath = dbPath;
        _db = LibraryDatabase.Open(dbPath);
        _playlists = new PlaylistStore(_db);
        _viewSettings = new ViewSettingsStore(_db);
    }

    /// <summary>Per-view UI settings for a scope key.</summary>
    public Task<ViewSettings> GetViewSettingsAsync(string scopeKey)
        => RunAsync(() => _viewSettings.Get(scopeKey));

    /// <summary>Stores per-view UI settings.</summary>
    public Task SetViewSettingsAsync(string scopeKey, ViewSettings settings)
        => RunAsync<object?>(() =>
        {
            _viewSettings.Set(scopeKey, settings);
            return null;
        });

    /// <summary>Re-sorts an id array in SQL by a preset.</summary>
    public Task<long[]> SortIdsAsync(long[] ids, TrackSortOrder order)
        => RunAsync(() => TrackPager.SortIds(_db, ids, order).ToArray());

    /// <summary>Re-sorts an id array in SQL by a header-click column sort.</summary>
    public Task<long[]> SortIdsAsync(long[] ids, ColumnSort sort)
        => RunAsync(() => TrackPager.SortIds(_db, ids, sort).ToArray());

    /// <summary>All track ids in a browse order.</summary>
    public Task<long[]> BrowseAllAsync(TrackSortOrder order)
        => RunAsync(() => TrackPager.BrowseAll(_db, order).ToArray());

    /// <summary>All track ids sorted by a header-click column sort.</summary>
    public Task<long[]> BrowseAllAsync(ColumnSort sort)
        => RunAsync(() => TrackPager.BrowseAll(_db, sort).ToArray());

    /// <summary>Most recently added track ids.</summary>
    public Task<long[]> BrowseRecentlyAddedAsync()
        => RunAsync(() => TrackPager.BrowseRecentlyAdded(_db).ToArray());

    /// <summary>Free-text FTS search, artist-ordered.</summary>
    public Task<long[]> SearchAsync(string text)
    {
        return RunAsync(() =>
        {
            // Built as an AST directly — no parsing, no injection surface.
            var query = new ParsedQuery(
                new FreeText(text),
                [new OrderTerm("artist", Descending: false)],
                Limit: null);
            CompiledQuery compiled = SqlCompiler.Compile(query);
            return TrackQueryRunner.Run(_db, compiled).ToArray();
        });
    }

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

    /// <summary>Total track count.</summary>
    public Task<long> CountAsync()
        => RunAsync(() => TrackPager.Count(_db));

    /// <summary>
    /// Scans a folder on its own database connection so browsing stays
    /// responsive while the scan runs.
    /// </summary>
    public Task<ScanResult> ScanAsync(string directory, IProgress<int>? progress)
    {
        return Task.Run(() =>
        {
            using var scanDb = LibraryDatabase.Open(_dbPath);
            return new LibraryScanner(scanDb).Scan(directory, progress);
        });
    }

    /// <summary>
    /// Force re-reads the whole library — watch folders plus tracked files
    /// outside them — so tag improvements (like date_text) reach every row.
    /// </summary>
    public Task<ScanResult> RescanAllAsync(IProgress<int>? progress)
    {
        return Task.Run(() =>
        {
            using var scanDb = LibraryDatabase.Open(_dbPath);
            return LibraryScanner.RescanEverything(scanDb, progress);
        });
    }

    /// <summary>Scans dropped files/folders, same isolation as <see cref="ScanAsync"/>.</summary>
    public Task<ScanResult> ScanPathsAsync(IReadOnlyList<string> paths, IProgress<int>? progress)
    {
        return Task.Run(() =>
        {
            using var scanDb = LibraryDatabase.Open(_dbPath);
            return new LibraryScanner(scanDb).ScanPaths(paths, progress);
        });
    }

    // ---- playlists -----------------------------------------------------

    /// <summary>All playlists.</summary>
    public Task<List<PlaylistInfo>> GetPlaylistsAsync()
        => RunAsync(_playlists.List);

    /// <summary>Membership (evaluated for smart) of one playlist.</summary>
    public Task<long[]> EvaluatePlaylistAsync(PlaylistInfo playlist)
        => RunAsync(() => _playlists.Evaluate(playlist));

    /// <summary>Creates a static playlist.</summary>
    public Task<PlaylistInfo> CreateStaticPlaylistAsync(string name)
        => RunAsync(() => _playlists.CreateStatic(name));

    /// <summary>Creates a smart playlist; validation errors propagate.</summary>
    public Task<PlaylistInfo> CreateSmartPlaylistAsync(string name, string queryText)
        => RunAsync(() => _playlists.CreateSmart(name, queryText));

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
    /// Embedded artwork bytes of a track's file, or null when the file is
    /// missing or carries no picture.
    /// </summary>
    public Task<byte[]?> GetArtworkAsync(long trackId)
        => RunAsync<byte[]?>(() =>
        {
            string? path = TrackPager.GetTrackPath(_db, trackId);
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            try
            {
                using TagLib.File file = TagLib.File.Create(path);
                TagLib.IPicture? picture = file.Tag.Pictures.FirstOrDefault();
                return picture?.Data.Data;
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException
                or TagLib.UnsupportedFormatException
                or IOException)
            {
                return null;
            }
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

    /// <inheritdoc />
    public void Dispose()
    {
        _gate.Dispose();
        _db.Dispose();
    }

    private async Task<T> RunAsync<T>(Func<T> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await Task.Run(operation).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
