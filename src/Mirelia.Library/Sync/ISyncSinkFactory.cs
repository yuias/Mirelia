namespace Mirelia.Library.Sync;

/// <summary>Opens the sinks one sync run writes through.</summary>
/// <remarks>
/// A run needs two rooted views — the music root and the playlist directory —
/// and on MTP they must share a single device session: opening the same device
/// twice is allowed but doubles the enumeration cost and lets the two caches
/// disagree. The factory owns whatever session that takes, so the call sites
/// look the same whichever transport they got.
/// </remarks>
public interface ISyncSinkFactory : IDisposable
{
    /// <summary>Opens a sink rooted at <paramref name="root"/>.</summary>
    ISyncFileSink Open(string root);
}

/// <summary>Sink factory for ordinary mounted paths.</summary>
/// <remarks>
/// There is no session to share, so this exists only to keep the call sites
/// uniform.
/// </remarks>
public sealed class FileSystemSinkFactory : ISyncSinkFactory
{
    /// <inheritdoc />
    public ISyncFileSink Open(string root) => new FileSystemSink(root);

    /// <inheritdoc />
    public void Dispose()
    {
        // Each sink owns nothing; the caller disposes the ones it opened.
    }
}
