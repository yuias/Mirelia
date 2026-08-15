using Avalonia.Media.Imaging;

namespace Mirelia.App.Services;

/// <summary>
/// Reads a track's cover art: the embedded picture first, then an image
/// sitting beside the file. Embedded art is authoritative per file; the
/// folder image is what rescues albums ripped without one, which matters far
/// more in a wall of album blocks than in a single Now Playing panel.
/// </summary>
internal static class ArtworkReader
{
    private static readonly string[] Names = ["cover", "folder", "front", "album"];
    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png"];

    /// <summary>Encoded image bytes, or null when the track has no cover.</summary>
    public static byte[]? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using TagLib.File file = TagLib.File.Create(path);
            if (file.Tag.Pictures.FirstOrDefault() is { } picture)
            {
                return picture.Data.Data;
            }
        }
        catch (Exception ex) when (ex is TagLib.CorruptFileException
            or TagLib.UnsupportedFormatException
            or IOException)
        {
            // A file we cannot parse may still sit next to a cover image.
        }

        return ReadFolderImage(Path.GetDirectoryName(path));
    }

    private static byte[]? ReadFolderImage(string? directory)
    {
        if (directory is null)
        {
            return null;
        }

        // A dozen stats beats enumerating a directory that may hold thousands
        // of files, and it fixes the precedence between candidate names.
        foreach (string name in Names)
        {
            foreach (string extension in Extensions)
            {
                string candidate = Path.Combine(directory, name + extension);
                try
                {
                    if (File.Exists(candidate))
                    {
                        return File.ReadAllBytes(candidate);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Unreadable candidate; try the next one.
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Decoded cover art, keyed by the track it is read from and the width it is
/// decoded at. Bounded LRU with in-flight de-duplication and negative caching,
/// because a grouped layout asks for dozens of covers per scroll and a
/// coverless album must not be re-read from disk every time it scrolls by.
///
/// The cache owns the bitmaps it hands out: callers must not dispose them.
/// All members must be called on the UI thread; the file read and decode
/// happen off it.
/// </summary>
public sealed class ArtworkCache(LibraryService library)
{
    private const int MaxEntries = 128;

    private readonly Dictionary<ArtworkKey, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly Dictionary<ArtworkKey, Task<Bitmap?>> _inFlight = [];

    /// <summary>
    /// The cover of <paramref name="trackId"/> decoded to
    /// <paramref name="decodeWidth"/>, or null when the track has none.
    /// </summary>
    public Task<Bitmap?> GetAsync(long trackId, int decodeWidth)
    {
        var key = new ArtworkKey(trackId, decodeWidth);
        if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            Touch(node);
            return Task.FromResult(node.Value.Bitmap);
        }

        // A block scrolling in and out re-asks while the first read is still
        // running; both callers must share that one read.
        if (_inFlight.TryGetValue(key, out Task<Bitmap?>? pending))
        {
            return pending;
        }

        Task<Bitmap?> load = LoadAsync(key);
        _inFlight[key] = load;
        return load;
    }

    /// <summary>
    /// Drops everything. Track ids do not survive a library switch, so the
    /// entries would otherwise name tracks in a library that is no longer
    /// open.
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        _lru.Clear();
        _inFlight.Clear();
    }

    private async Task<Bitmap?> LoadAsync(ArtworkKey key)
    {
        Bitmap? bitmap = null;
        try
        {
            // Only the path lookup goes through the library's gate; the file
            // read stays off it so a wall of covers cannot stall row
            // hydration behind it.
            if (await library.GetPathAsync(key.TrackId) is { } path)
            {
                byte[]? bytes = await Task.Run(() => ArtworkReader.Read(path));
                if (bytes is not null)
                {
                    bitmap = await Task.Run(() => Decode(bytes, key.DecodeWidth));
                }
            }
        }
        finally
        {
            _inFlight.Remove(key);
            Store(key, bitmap);
        }

        return bitmap;
    }

    private static Bitmap? Decode(byte[] bytes, int width)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception)
        {
            // Broken embedded images are not worth surfacing.
            return null;
        }
    }

    private void Store(ArtworkKey key, Bitmap? bitmap)
    {
        if (_entries.ContainsKey(key))
        {
            return;
        }

        // Nulls are cached too: "this track has no cover" is an answer worth
        // remembering, and it is the expensive one to recompute.
        LinkedListNode<Entry> node = _lru.AddFirst(new Entry(key, bitmap));
        _entries[key] = node;

        while (_entries.Count > MaxEntries && _lru.Last is { } oldest)
        {
            _lru.RemoveLast();
            _entries.Remove(oldest.Value.Key);

            // Evicted bitmaps are not disposed: one may still be bound to a
            // visible Image, and disposing it would fault the renderer.
            // Dropping the reference is enough — the GC reclaims the pixels.
        }
    }

    private void Touch(LinkedListNode<Entry> node)
    {
        if (node != _lru.First)
        {
            _lru.Remove(node);
            _lru.AddFirst(node);
        }
    }

    private readonly record struct ArtworkKey(long TrackId, int DecodeWidth);

    private sealed record Entry(ArtworkKey Key, Bitmap? Bitmap);
}
