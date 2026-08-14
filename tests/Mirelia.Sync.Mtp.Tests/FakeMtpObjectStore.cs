namespace Mirelia.Sync.Mtp.Tests;

/// <summary>
/// In-memory stand-in for a portable device. A phone cannot be faked in CI, so
/// this is what the path translation, the directory cache and the overwrite
/// sequence are tested against.
/// </summary>
public sealed class FakeMtpObjectStore : IMtpObjectStore
{
    private readonly Dictionary<string, Node> _nodes = [];
    private int _nextId;

    /// <summary>Opens a fake device with nothing but its root object.</summary>
    public FakeMtpObjectStore()
    {
        _nodes[DeviceObjectId] = new Node(DeviceObjectId, string.Empty, isFolder: true);
    }

    /// <summary>Every call made, in order, for sequencing assertions.</summary>
    public List<string> Log { get; } = [];

    /// <summary>Free space the storage object reports; null means "not supported".</summary>
    public long? FreeSpace { get; set; } = 1024L * 1024 * 1024;

    /// <summary>Object ids whose next operation throws, simulating a device error.</summary>
    public HashSet<string> FailingObjectIds { get; } = [];

    /// <inheritdoc />
    public string DeviceLabel => "Fake DAP";

    /// <inheritdoc />
    public string DeviceObjectId => "DEVICE";

    /// <summary>Creates a storage object directly under the device root.</summary>
    public string AddStorage(string name) => Create(DeviceObjectId, name, isFolder: true);

    /// <summary>Creates a folder at <paramref name="path"/>, parents included.</summary>
    public string AddFolder(string path)
    {
        string parent = DeviceObjectId;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            parent = Child(parent, part)?.Id ?? Create(parent, part, isFolder: true);
        }

        return parent;
    }

    /// <summary>Creates a file at <paramref name="path"/>, parents included.</summary>
    public string AddFile(string path, byte[] content)
    {
        int cut = path.LastIndexOf('/');
        string parent = cut < 0 ? DeviceObjectId : AddFolder(path[..cut]);
        string id = Create(parent, path[(cut + 1)..], isFolder: false);
        _nodes[id].Content = content;
        return id;
    }

    /// <summary>Contents of a file by path, or null when it is absent.</summary>
    public byte[]? Read(string path)
    {
        string parent = DeviceObjectId;
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (Child(parent, parts[i]) is not { } folder)
            {
                return null;
            }

            parent = folder.Id;
        }

        return Child(parent, parts[^1])?.Content;
    }

    /// <summary>Whether an object still exists.</summary>
    public bool Exists(string objectId) => _nodes.ContainsKey(objectId);

    /// <summary>How many listings were requested for one object.</summary>
    public int ListingsOf(string objectId) => Log.Count(e => e == $"children:{objectId}");

    /// <inheritdoc />
    public IReadOnlyList<MtpObject> GetChildren(string parentObjectId)
    {
        Log.Add($"children:{parentObjectId}");
        Check(parentObjectId);
        return [.. _nodes[parentObjectId].Children
            .Select(id => _nodes[id])
            .Select(n => new MtpObject(n.Id, n.Name, n.IsFolder, n.Content?.LongLength ?? 0))];
    }

    /// <inheritdoc />
    public string CreateFolder(string parentObjectId, string name)
    {
        Log.Add($"mkdir:{parentObjectId}/{name}");
        Check(parentObjectId);
        return Create(parentObjectId, name, isFolder: true);
    }

    /// <inheritdoc />
    public string CreateFile(
        string parentObjectId, string name, Stream source, long sizeBytes, CancellationToken ct)
    {
        Log.Add($"create:{parentObjectId}/{name}");
        Check(parentObjectId);

        // Mirrors the real sink: nothing is committed until the last chunk, so
        // a cancelled transfer leaves no object behind.
        using var buffer = new MemoryStream();
        var chunk = new byte[64];
        for (int read = source.Read(chunk); read > 0; read = source.Read(chunk))
        {
            ct.ThrowIfCancellationRequested();
            buffer.Write(chunk, 0, read);
        }

        ct.ThrowIfCancellationRequested();
        string id = Create(parentObjectId, name, isFolder: false);
        _nodes[id].Content = buffer.ToArray();
        return id;
    }

    /// <inheritdoc />
    public void Delete(string objectId)
    {
        Log.Add($"delete:{objectId}");
        Check(objectId);
        Node node = _nodes[objectId];
        foreach (string child in node.Children.ToList())
        {
            Delete(child);
        }

        if (node.ParentId is { } parent)
        {
            _nodes[parent].Children.Remove(objectId);
        }

        _nodes.Remove(objectId);
    }

    /// <inheritdoc />
    public long? GetFreeSpaceBytes(string storageObjectId)
    {
        Log.Add($"free:{storageObjectId}");
        Check(storageObjectId);
        return FreeSpace;
    }

    /// <inheritdoc />
    public void Dispose() => Log.Add("dispose");

    private void Check(string objectId)
    {
        if (!_nodes.ContainsKey(objectId))
        {
            throw new IOException($"Object '{objectId}' is gone.");
        }

        if (FailingObjectIds.Remove(objectId))
        {
            throw new IOException($"Object '{objectId}' is unwritable.");
        }
    }

    private Node? Child(string parentObjectId, string name) => _nodes[parentObjectId].Children
        .Select(id => _nodes[id])
        .FirstOrDefault(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));

    private string Create(string parentObjectId, string name, bool isFolder)
    {
        string id = $"o{++_nextId}";
        _nodes[id] = new Node(id, name, isFolder) { ParentId = parentObjectId };
        _nodes[parentObjectId].Children.Add(id);
        return id;
    }

    private sealed class Node(string id, string name, bool isFolder)
    {
        public string Id { get; } = id;

        public string Name { get; } = name;

        public bool IsFolder { get; } = isFolder;

        public string? ParentId { get; init; }

        public List<string> Children { get; } = [];

        public byte[]? Content { get; set; }
    }
}
