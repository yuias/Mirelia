namespace Mirelia.Library.Sync;

/// <summary>What a sink knows about a file it already holds.</summary>
/// <param name="SizeBytes">Size on the destination.</param>
/// <param name="MTimeUnix">Modification time on the destination.</param>
public readonly record struct SinkEntry(long SizeBytes, long MTimeUnix);

/// <summary>
/// Byte-moving layer of a sync target, rooted at one directory. Paths are
/// relative to that root and use <c>/</c> separators.
/// </summary>
/// <remarks>
/// The abstraction exists so MTP destinations (M8+) can be a second
/// implementation: they are reachable only through the WPD COM API, never
/// through <see cref="System.IO"/>, and cannot preserve modification times.
/// </remarks>
public interface ISyncFileSink : IDisposable
{
    /// <summary>Human-readable root, for messages and preflight display.</summary>
    string Root { get; }

    /// <summary>Free space at the root, or null when the sink cannot report it.</summary>
    long? FreeSpaceBytes { get; }

    /// <summary>Metadata of an existing file, or null when it is absent.</summary>
    SinkEntry? Stat(string relPath);

    /// <summary>Copies a local file in, creating parent directories.</summary>
    void WriteFile(string relPath, string sourcePath, CancellationToken ct);

    /// <summary>Writes bytes directly, creating parent directories.</summary>
    void WriteBytes(string relPath, byte[] content);

    /// <summary>Removes a file, along with any directories it leaves empty.</summary>
    void Delete(string relPath);
}
