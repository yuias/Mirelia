namespace Mirelia.Library.Sync;

/// <summary>Why a file is being copied.</summary>
public enum CopyReason
{
    /// <summary>The target has never held this track.</summary>
    New,

    /// <summary>The source file changed since it was copied.</summary>
    SourceChanged,

    /// <summary>The target no longer has the file — deleted on the device by hand.</summary>
    MissingOnTarget,

    /// <summary>The layout now puts the track somewhere else.</summary>
    Relocated,

    /// <summary>The user asked for this one to be re-copied regardless.</summary>
    Forced,
}

/// <summary>
/// One-shot overrides for a single plan. Not persisted on the target: these
/// describe an action the user is taking now, not how the target behaves.
/// </summary>
public sealed record SyncPlanOptions
{
    /// <summary>Re-copy every in-scope track, whatever the delta says.</summary>
    public bool ForceAll { get; init; }

    /// <summary>
    /// Playlists (and folders, expanded to their descendants) whose tracks
    /// are re-copied whatever the delta says.
    /// </summary>
    /// <remarks>
    /// This exists because size and modification time are not proof: tag
    /// editors that rewrite in place and restore the timestamp produce a file
    /// that differs from the copy on the device while looking identical to
    /// the delta.
    /// </remarks>
    public IReadOnlyCollection<long>? ForcePlaylistIds { get; init; }

    /// <summary>Whether anything is being forced at all.</summary>
    public bool IsForcing => ForceAll || ForcePlaylistIds is { Count: > 0 };
}

/// <summary>Where one in-scope track lands on the target.</summary>
/// <param name="Track">Source track.</param>
/// <param name="RelPath">Destination path below the music root.</param>
public sealed record PlannedFile(TrackLayoutInfo Track, string RelPath);

/// <summary>One file to copy.</summary>
/// <param name="File">Source track and its destination.</param>
/// <param name="Reason">What made it necessary.</param>
/// <param name="PreviousRelPath">Path to remove first, when relocating.</param>
public sealed record SyncCopy(
    PlannedFile File,
    CopyReason Reason,
    string? PreviousRelPath = null);

/// <summary>One file to remove from the target.</summary>
/// <param name="TrackId">Track it came from.</param>
/// <param name="RelPath">Path below the music root.</param>
public sealed record SyncDelete(long TrackId, string RelPath);

/// <summary>One playlist file to write.</summary>
/// <param name="PlaylistId">Source playlist.</param>
/// <param name="RelPath">Path below the playlist directory, extension included.</param>
/// <param name="TrackIds">Membership, in playlist order, already materialized.</param>
public sealed record PlaylistExport(long PlaylistId, string RelPath, IReadOnlyList<long> TrackIds);

/// <summary>
/// Everything one sync would do, computed before anything is written. The UI
/// shows this as the preflight summary; deletion is in play, so a run is
/// never started without one.
/// </summary>
/// <param name="Target">Target the plan belongs to.</param>
/// <param name="Files">Every in-scope track and where it lands, copied or already current.</param>
/// <param name="Copies">Files to copy.</param>
/// <param name="Deletes">Files to remove.</param>
/// <param name="Playlists">Playlist files to write.</param>
/// <param name="Warnings">Conditions worth showing the user before they commit.</param>
/// <param name="BytesToCopy">Total size of <paramref name="Copies"/>.</param>
/// <param name="FreeSpaceBytes">Free space at the music root, when known.</param>
/// <param name="UnchangedCount">Tracks already present and current.</param>
/// <param name="MissingSourceCount">Tracks skipped because the source file is gone.</param>
/// <param name="SkippedByPolicy">Tracks the delivery policy left behind, and why.</param>
public sealed record SyncPlan(
    SyncTarget Target,
    IReadOnlyDictionary<long, PlannedFile> Files,
    IReadOnlyList<SyncCopy> Copies,
    IReadOnlyList<SyncDelete> Deletes,
    IReadOnlyList<PlaylistExport> Playlists,
    IReadOnlyList<string> Warnings,
    long BytesToCopy,
    long? FreeSpaceBytes,
    int UnchangedCount,
    int MissingSourceCount,
    IReadOnlyDictionary<string, int> SkippedByPolicy)
{
    /// <summary>Total tracks the delivery policy left behind.</summary>
    public int SkippedByPolicyCount => SkippedByPolicy.Values.Sum();

    /// <summary>Whether the plan would change anything on the target.</summary>
    public bool IsEmpty => Copies.Count == 0 && Deletes.Count == 0 && Playlists.Count == 0;

    /// <summary>Whether the copies fit in the reported free space.</summary>
    public bool FitsInFreeSpace => FreeSpaceBytes is not { } free || BytesToCopy <= free;
}
