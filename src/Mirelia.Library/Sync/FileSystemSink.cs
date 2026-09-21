namespace Mirelia.Library.Sync;

/// <summary>
/// <see cref="ISyncFileSink"/> over an ordinary mounted path: SD card reader,
/// USB stick, UNC share. Drive letters and UNC only — MTP devices have
/// neither and are handled by <c>Mirelia.Sync.Mtp</c> instead.
/// </summary>
public sealed class FileSystemSink : ISyncFileSink
{
    private readonly string _root;

    /// <summary>Opens a sink rooted at <paramref name="root"/>, creating it if absent.</summary>
    public FileSystemSink(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    /// <inheritdoc />
    public string Root => _root;

    /// <inheritdoc />
    public long? FreeSpaceBytes
    {
        get
        {
            try
            {
                return new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // UNC shares and some removable media report no drive at all.
                return null;
            }
        }
    }

    /// <inheritdoc />
    public SinkEntry? Stat(string relPath)
    {
        var info = new FileInfo(Resolve(relPath));
        return info.Exists
            ? new SinkEntry(info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds())
            : null;
    }

    /// <inheritdoc />
    public void WriteFile(string relPath, string sourcePath, CancellationToken ct)
    {
        string destination = Resolve(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        ct.ThrowIfCancellationRequested();
        File.Copy(sourcePath, destination, overwrite: true);

        // Carrying the source timestamp over is what lets the next run skip
        // the file without re-reading it.
        File.SetLastWriteTimeUtc(destination, File.GetLastWriteTimeUtc(sourcePath));
    }

    /// <inheritdoc />
    public void WriteBytes(string relPath, byte[] content)
    {
        string destination = Resolve(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination, content);
    }

    /// <inheritdoc />
    public void Delete(string relPath)
    {
        string target = Resolve(relPath);
        if (!File.Exists(target))
        {
            return;
        }

        File.Delete(target);
        PruneEmptyDirectories(Path.GetDirectoryName(target)!);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Nothing to release; the interface is disposable for MTP's sake.
    }

    private string Resolve(string relPath)
    {
        string full = Path.GetFullPath(Path.Combine(_root, relPath.Replace('/', Path.DirectorySeparatorChar)));

        // A template or a stored path must never reach outside the root.
        if (!full.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Path '{relPath}' escapes the sink root.");
        }

        return full;
    }

    private void PruneEmptyDirectories(string directory)
    {
        string current = directory;
        while (current.Length > _root.Length
            && current.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(current)
            && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            current = Path.GetDirectoryName(current)!;
        }
    }
}
