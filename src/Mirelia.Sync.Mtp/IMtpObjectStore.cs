namespace Mirelia.Sync.Mtp;

/// <summary>One object in the device's content tree.</summary>
/// <param name="ObjectId">Opaque WPD object id; meaningless outside its device.</param>
/// <param name="Name">Original file name, or the object name when it has none.</param>
/// <param name="IsFolder">Whether the object can hold children.</param>
/// <param name="SizeBytes">Size in bytes; zero for folders.</param>
public readonly record struct MtpObject(string ObjectId, string Name, bool IsFolder, long SizeBytes);

/// <summary>
/// The device operations <see cref="MtpSink"/> needs, expressed without a
/// single COM type.
/// </summary>
/// <remarks>
/// <para>
/// The seam exists so the sink's real work — translating relative paths to
/// object ids, caching directory listings, and sequencing an overwrite — is
/// testable against an in-memory fake. A phone cannot be faked in CI, and
/// that logic is where the bugs live; the WPD calls below are thin enough to
/// be checked by hand.
/// </para>
/// <para>
/// Every member addresses objects by id only: this interface has no notion of
/// a path, because neither does MTP.
/// </para>
/// </remarks>
public interface IMtpObjectStore : IDisposable
{
    /// <summary>Friendly device name, for messages and preflight display.</summary>
    string DeviceLabel { get; }

    /// <summary>Object id of the device itself, the parent of every storage.</summary>
    string DeviceObjectId { get; }

    /// <summary>Direct children of an object, in device order.</summary>
    IReadOnlyList<MtpObject> GetChildren(string parentObjectId);

    /// <summary>Creates a child folder and returns its object id.</summary>
    string CreateFolder(string parentObjectId, string name);

    /// <summary>
    /// Creates a child file from <paramref name="source"/> and returns its
    /// object id. The object does not exist until the write completes; a
    /// cancelled write leaves nothing behind.
    /// </summary>
    /// <param name="parentObjectId">Folder or storage to create under.</param>
    /// <param name="name">Original file name to give the object.</param>
    /// <param name="source">Bytes to transfer; read to the end.</param>
    /// <param name="sizeBytes">Total size, which WPD requires up front.</param>
    /// <param name="ct">Checked between chunks.</param>
    string CreateFile(
        string parentObjectId, string name, Stream source, long sizeBytes, CancellationToken ct);

    /// <summary>Removes an object, and everything beneath it when it is a folder.</summary>
    void Delete(string objectId);

    /// <summary>
    /// Free space on a storage object, or null when the device does not report
    /// it. Only storage objects carry the property.
    /// </summary>
    long? GetFreeSpaceBytes(string storageObjectId);
}
