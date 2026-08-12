namespace Mirelia.Library.Sync;

/// <summary>
/// How a destination path is spelled inside a playlist. Shared by the runner
/// and the settings preview so what the user sees is what gets written.
/// </summary>
public static class SyncPaths
{
    /// <summary>
    /// Path as the device will read it: rooted at the device's own view of
    /// the music root, or relative to the playlist file's own directory.
    /// </summary>
    /// <param name="relPath">Destination path below the music root.</param>
    /// <param name="playlistRelPath">Playlist path below the playlist directory.</param>
    /// <param name="config">Target settings.</param>
    public static string Reference(string relPath, string playlistRelPath, SyncTargetConfig config)
    {
        char separator = config.Separator.Length > 0 ? config.Separator[0] : '\\';

        if (config.PathStyle == SyncPathStyle.Absolute)
        {
            string root = (string.IsNullOrWhiteSpace(config.DeviceRoot)
                ? config.MusicRoot
                : config.DeviceRoot).TrimEnd('\\', '/');
            return Retarget($"{root}/{relPath}", separator);
        }

        string musicPath = Path.GetFullPath(Path.Combine(config.MusicRoot, relPath));
        string playlistDir = Path.GetDirectoryName(
            Path.GetFullPath(Path.Combine(config.PlaylistDir, playlistRelPath)))!;
        return Retarget(Path.GetRelativePath(playlistDir, musicPath), separator);
    }

    private static string Retarget(string path, char separator) =>
        path.Replace('\\', separator).Replace('/', separator);
}
