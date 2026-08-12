namespace Mirelia.Library.Sync;

/// <summary>Transport a sync target uses.</summary>
public enum SyncTargetKind
{
    /// <summary>Writes to a mounted path: SD card, USB stick, UNC share.</summary>
    Folder,

    /// <summary>Serves over HTTP to OpenSubsonic clients.</summary>
    Network,
}

/// <summary>Which part of the library a target receives.</summary>
public enum SyncScopeKind
{
    /// <summary>Every track in the library.</summary>
    AllMusic,

    /// <summary>The union of every playlist's tracks.</summary>
    AllPlaylists,

    /// <summary>The union of the playlists recorded in <c>sync_target_scope</c>.</summary>
    Selected,
}

/// <summary>Form of the paths written inside exported playlists.</summary>
public enum SyncPathStyle
{
    /// <summary>Relative to the playlist file's own directory.</summary>
    Relative,

    /// <summary>Rooted at the device's own view of the music root.</summary>
    Absolute,
}

/// <summary>How the playlist tree maps onto the flat playlist directory.</summary>
public enum PlaylistFolderMode
{
    /// <summary>One directory, ancestor names joined into the file name.</summary>
    Flatten,

    /// <summary>Directory hierarchy mirroring the playlist tree.</summary>
    Mirror,
}

/// <summary>What to do with target files no longer in scope.</summary>
public enum OrphanPolicy
{
    /// <summary>Remove them.</summary>
    Delete,

    /// <summary>Leave them in place.</summary>
    Keep,
}

/// <summary>Substitution applied to characters filesystems reject.</summary>
public enum ReservedCharMode
{
    /// <summary>Replace with <c>_</c>.</summary>
    Underscore,

    /// <summary>Replace with the full-width look-alike (<c>：</c>, <c>？</c>, …).</summary>
    FullWidth,
}

/// <summary>
/// Per-target settings. Persisted as the JSON in <c>sync_target.config_json</c>
/// so new options (the M8+ transcode settings above all) do not need a
/// migration.
/// </summary>
public sealed record SyncTargetConfig
{
    /// <summary>Directory audio files are written under, as seen from this PC.</summary>
    public string MusicRoot { get; init; } = string.Empty;

    /// <summary>Directory playlist files are written to, as seen from this PC.</summary>
    public string PlaylistDir { get; init; } = string.Empty;

    /// <summary>Which tracks the target receives.</summary>
    public SyncScopeKind Scope { get; init; } = SyncScopeKind.AllPlaylists;

    /// <summary>Path form used inside exported playlists.</summary>
    public SyncPathStyle PathStyle { get; init; } = SyncPathStyle.Relative;

    /// <summary>
    /// Stands in for <see cref="MusicRoot"/> when <see cref="PathStyle"/> is
    /// <see cref="SyncPathStyle.Absolute"/> — a card mounted at <c>X:\</c> here
    /// is often <c>A:\</c> to the device reading it.
    /// </summary>
    public string? DeviceRoot { get; init; }

    /// <summary>Separator written into playlists; independent of the host OS.</summary>
    public string Separator { get; init; } = "\\";

    /// <summary>Playlist file format: <c>m3u8</c> (UTF-8) or <c>m3u</c>.</summary>
    public string PlaylistFormat { get; init; } = "m3u8";

    /// <summary>Code page for <c>m3u</c>; ignored for <c>m3u8</c>.</summary>
    public int Codepage { get; init; } = 1252;

    /// <summary>Whether the playlist tree is flattened or mirrored.</summary>
    public PlaylistFolderMode PlaylistFolders { get; init; } = PlaylistFolderMode.Flatten;

    /// <summary>Destination path template; see <see cref="PathTemplate"/>.</summary>
    public string LayoutTemplate { get; init; } = PathTemplate.Default;

    /// <summary>Substitution for characters the destination filesystem rejects.</summary>
    public ReservedCharMode ReservedChars { get; init; } = ReservedCharMode.Underscore;

    /// <summary>Whether files outside the current scope are removed.</summary>
    public OrphanPolicy OrphanPolicy { get; init; } = OrphanPolicy.Delete;

    /// <summary>
    /// Length budget for the device-side path. Checked against the
    /// <see cref="DeviceRoot"/> form, which can be longer than the path on
    /// this PC.
    /// </summary>
    public int MaxDevicePathLength { get; init; } = 255;

    /// <summary>
    /// What the device can play. Null accepts everything, which is right for
    /// a plain card whose player is unknown. The M8+ transcode settings
    /// attach here.
    /// </summary>
    public DeviceProfile? Device { get; init; }
}

/// <summary>One persisted sync destination.</summary>
/// <param name="Id">Target id.</param>
/// <param name="Name">Display name; unique across targets.</param>
/// <param name="Kind">Transport.</param>
/// <param name="Config">Per-target settings.</param>
public sealed record SyncTarget(
    long Id,
    string Name,
    SyncTargetKind Kind,
    SyncTargetConfig Config);
