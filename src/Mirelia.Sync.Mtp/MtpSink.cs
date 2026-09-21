using Mirelia.Library.Sync;

namespace Mirelia.Sync.Mtp;

/// <summary>
/// <see cref="ISyncFileSink"/> over a portable device. MTP has no paths — every
/// object is an opaque id reached by walking parent links — so this is where
/// the relative paths the rest of sync speaks are translated, and where the
/// tree is cached so the walk happens once.
/// </summary>
/// <remarks>
/// The sink does not own its <see cref="IMtpObjectStore"/>: the music root and
/// the playlist directory are two sinks that must share one device session, and
/// the caller that opened the session closes it. <see cref="Dispose"/> only
/// drops this sink's cache.
/// </remarks>
public sealed class MtpSink : ISyncFileSink
{
    private readonly IMtpObjectStore _store;
    private readonly string[] _rootParts;

    /// <summary>Directories reached so far, keyed by path relative to the root.</summary>
    private readonly Dictionary<string, MtpDirectory> _dirs =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _storageId;

    /// <summary>
    /// Roots a sink at <paramref name="rootPath"/> inside the device, storage
    /// name first — <c>Internal shared storage/Music</c>. That is the path a
    /// user reads in Explorer, and a phone with a card exposes its storages as
    /// siblings, so no separate storage key is needed.
    /// </summary>
    public MtpSink(IMtpObjectStore store, string rootPath)
    {
        _store = store;
        _rootParts = SplitPath(rootPath);
    }

    /// <inheritdoc />
    public string Root => _rootParts.Length == 0
        ? _store.DeviceLabel
        : $"{_store.DeviceLabel}\\{string.Join('\\', _rootParts)}";

    /// <inheritdoc />
    public long? FreeSpaceBytes =>
        Storage() is { } storage ? _store.GetFreeSpaceBytes(storage) : null;

    /// <inheritdoc />
    /// <remarks>
    /// The modification time is always zero: the device stamps its own, and a
    /// sizeable minority of drivers report none at all. Nothing needs it —
    /// change detection compares the library against <c>sync_target_file</c>,
    /// and this answers only "is the file still there".
    /// </remarks>
    public SinkEntry? Stat(string relPath)
    {
        (string dirPath, string name) = SplitLeaf(relPath);
        MtpDirectory? dir = FindDirectory(dirPath);
        if (dir is null)
        {
            return null;
        }

        Load(dir);
        return dir.Children.TryGetValue(name, out MtpObject entry) && !entry.IsFolder
            ? new SinkEntry(entry.SizeBytes, 0)
            : null;
    }

    /// <inheritdoc />
    public void WriteFile(string relPath, string sourcePath, CancellationToken ct)
    {
        using FileStream source = File.OpenRead(sourcePath);
        Write(relPath, source, source.Length, ct);
    }

    /// <inheritdoc />
    public void WriteBytes(string relPath, byte[] content)
    {
        using var source = new MemoryStream(content, writable: false);
        Write(relPath, source, content.Length, CancellationToken.None);
    }

    /// <inheritdoc />
    public void Delete(string relPath)
    {
        (string dirPath, string name) = SplitLeaf(relPath);
        MtpDirectory? dir = FindDirectory(dirPath);
        if (dir is null)
        {
            return;
        }

        Load(dir);
        if (!dir.Children.TryGetValue(name, out MtpObject entry))
        {
            return;
        }

        _store.Delete(entry.ObjectId);
        dir.Children.Remove(name);
        PruneEmptyDirectories(dirPath);
    }

    /// <inheritdoc />
    public void Dispose() => _dirs.Clear();

    private void Write(string relPath, Stream source, long size, CancellationToken ct)
    {
        (string dirPath, string name) = SplitLeaf(relPath);
        MtpDirectory dir = EnsureDirectory(dirPath);
        Load(dir);
        ct.ThrowIfCancellationRequested();

        // MTP has no truncate, and creating a second object with the same name
        // leaves two of them on most drivers, so an overwrite is delete first.
        if (dir.Children.TryGetValue(name, out MtpObject existing))
        {
            _store.Delete(existing.ObjectId);
            dir.Children.Remove(name);
        }

        string objectId = _store.CreateFile(dir.ObjectId, name, source, size, ct);
        dir.Children[name] = new MtpObject(objectId, name, false, size);
    }

    private MtpDirectory EnsureDirectory(string relDir) => Resolve(relDir, create: true)!;

    private MtpDirectory? FindDirectory(string relDir) => Resolve(relDir, create: false);

    private MtpDirectory? Resolve(string relDir, bool create)
    {
        if (_dirs.TryGetValue(relDir, out MtpDirectory? cached))
        {
            return cached;
        }

        if (relDir.Length == 0)
        {
            return ResolveRoot(create);
        }

        (string parentPath, string name) = SplitLeaf(relDir);
        MtpDirectory? parent = Resolve(parentPath, create);
        if (parent is null)
        {
            return null;
        }

        Load(parent);
        if (parent.Children.TryGetValue(name, out MtpObject existing))
        {
            if (!existing.IsFolder)
            {
                return create
                    ? throw new IOException($"'{relDir}' on {Root} is a file, not a folder.")
                    : null;
            }

            return Track(relDir, existing.ObjectId, loaded: false);
        }

        if (!create)
        {
            return null;
        }

        string created = _store.CreateFolder(parent.ObjectId, name);
        parent.Children[name] = new MtpObject(created, name, true, 0);

        // Nothing can be inside a folder made a moment ago, so skip the listing.
        return Track(relDir, created, loaded: true);
    }

    private MtpDirectory? ResolveRoot(bool create)
    {
        if (_rootParts.Length == 0)
        {
            return Track(string.Empty, _store.DeviceObjectId, loaded: false);
        }

        string? parentId = Storage();
        if (parentId is null)
        {
            // Storages are physical; unlike a folder, one cannot be created.
            return create
                ? throw new IOException(
                    $"Storage '{_rootParts[0]}' is not present on {_store.DeviceLabel}.")
                : null;
        }

        bool justCreated = false;
        for (int i = 1; i < _rootParts.Length; i++)
        {
            string name = _rootParts[i];
            MtpObject? found = Match(_store.GetChildren(parentId), name);
            if (found is { IsFolder: true } folder)
            {
                parentId = folder.ObjectId;
                justCreated = false;
                continue;
            }

            if (found is not null)
            {
                return create
                    ? throw new IOException($"'{name}' on {_store.DeviceLabel} is a file, not a folder.")
                    : null;
            }

            if (!create)
            {
                return null;
            }

            parentId = _store.CreateFolder(parentId, name);
            justCreated = true;
        }

        return Track(string.Empty, parentId, justCreated);
    }

    /// <summary>Object id of the storage the root sits on, resolved once.</summary>
    private string? Storage()
    {
        if (_storageId is not null || _rootParts.Length == 0)
        {
            return _storageId;
        }

        _storageId = Match(_store.GetChildren(_store.DeviceObjectId), _rootParts[0])?.ObjectId;
        return _storageId;
    }

    private MtpDirectory Track(string relDir, string objectId, bool loaded)
    {
        var dir = new MtpDirectory(objectId) { Loaded = loaded };
        _dirs[relDir] = dir;
        return dir;
    }

    /// <summary>
    /// Fills a directory's listing on first touch. A listing costs a round trip
    /// per object, so re-walking the tree per file is the difference between a
    /// sync that finishes and one that does not.
    /// </summary>
    private void Load(MtpDirectory dir)
    {
        if (dir.Loaded)
        {
            return;
        }

        foreach (MtpObject child in _store.GetChildren(dir.ObjectId))
        {
            dir.Children[child.Name] = child;
        }

        dir.Loaded = true;
    }

    /// <summary>
    /// Removes directories a deletion emptied, walking up to but not including
    /// the root.
    /// </summary>
    private void PruneEmptyDirectories(string relDir)
    {
        string current = relDir;
        while (current.Length > 0
            && _dirs.TryGetValue(current, out MtpDirectory? dir)
            && dir is { Loaded: true, Children.Count: 0 })
        {
            (string parentPath, string name) = SplitLeaf(current);
            _store.Delete(dir.ObjectId);
            _dirs.Remove(current);
            if (_dirs.TryGetValue(parentPath, out MtpDirectory? parent))
            {
                parent.Children.Remove(name);
            }

            current = parentPath;
        }
    }

    /// <summary>
    /// Case-insensitive, because that is what the name sanitizer and the
    /// <c>sync_target_file</c> records already assume.
    /// </summary>
    private static MtpObject? Match(IReadOnlyList<MtpObject> children, string name)
    {
        foreach (MtpObject child in children)
        {
            if (string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return child;
            }
        }

        return null;
    }

    private static (string Directory, string Name) SplitLeaf(string relPath)
    {
        string[] parts = SplitPath(relPath);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException("An empty path has no name.");
        }

        return (string.Join('/', parts[..^1]), parts[^1]);
    }

    private static string[] SplitPath(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            // A template or a stored path must never reach outside the root.
            if (part is "." or "..")
            {
                throw new InvalidOperationException($"Path '{path}' escapes the sink root.");
            }
        }

        return parts;
    }

    /// <summary>
    /// A directory's object id and its children, so a name can be resolved
    /// without another listing.
    /// </summary>
    private sealed class MtpDirectory(string objectId)
    {
        public string ObjectId { get; } = objectId;

        public Dictionary<string, MtpObject> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public bool Loaded { get; set; }
    }
}
