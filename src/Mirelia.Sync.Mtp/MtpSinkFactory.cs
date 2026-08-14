using System.Runtime.Versioning;
using Mirelia.Library.Sync;

namespace Mirelia.Sync.Mtp;

/// <summary>
/// Hands out sinks rooted inside one portable device, all sharing the device
/// session this factory owns.
/// </summary>
[SupportedOSPlatform("windows5.0")]
public sealed class MtpSinkFactory : ISyncSinkFactory
{
    private readonly IMtpObjectStore _store;

    /// <summary>Takes ownership of an already-open device session.</summary>
    public MtpSinkFactory(IMtpObjectStore store) => _store = store;

    /// <summary>Opens a session on the device and a factory over it.</summary>
    /// <param name="deviceId">Plug-and-Play device id recorded on the target.</param>
    /// <param name="label">Cached friendly name, for messages.</param>
    public static MtpSinkFactory Open(string deviceId, string label)
        => new(MtpDevices.Open(deviceId, label));

    /// <inheritdoc />
    public ISyncFileSink Open(string root) => new MtpSink(_store, root);

    /// <inheritdoc />
    public void Dispose() => _store.Dispose();
}
