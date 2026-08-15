using Microsoft.Data.Sqlite;
using SkiaSharp;

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
/// Scales cover art down to a cached thumbnail. Album art in tags is often
/// 1500 px square or larger; nothing in the UI shows it above ~400 px, and
/// keeping full-size art in memory is what made the previous cache enormous.
/// </summary>
public static class Thumbnailer
{
    /// <summary>
    /// Longest edge of a stored thumbnail. Large enough for any cover the UI
    /// draws (210 px album blocks, the ~180 px Now Playing panel) at high DPI,
    /// while decoding to a fraction of the source.
    /// </summary>
    public const int MaxEdge = 512;

    private const int Quality = 80;

    /// <summary>
    /// Re-encodes <paramref name="source"/> as a WebP thumbnail, or returns
    /// null when it does not decode. Images already within
    /// <see cref="MaxEdge"/> are re-encoded anyway: a consistent small format
    /// is what makes the cache's size predictable.
    /// </summary>
    public static byte[]? Encode(byte[] source)
    {
        try
        {
            using SKData sourceData = SKData.CreateCopy(source);
            using SKCodec? codec = SKCodec.Create(sourceData);
            if (codec is null)
            {
                return null;
            }

            SKImageInfo info = codec.Info;
            int longest = Math.Max(info.Width, info.Height);
            if (longest <= 0)
            {
                return null;
            }

            float scale = longest > MaxEdge ? (float)MaxEdge / longest : 1f;

            // Codecs only decode at the reduced sizes they support (JPEG
            // halves, most others not at all), so ask for the nearest one
            // first: a 4000 px JPEG then never materializes at full
            // resolution just to be thrown away.
            SKSizeI sampled = codec.GetScaledDimensions(scale);
            using SKBitmap? decoded = SKBitmap.Decode(
                codec, info.WithSize(sampled.Width, sampled.Height));
            if (decoded is null)
            {
                return null;
            }

            int width = Math.Max(1, (int)Math.Round(info.Width * scale));
            int height = Math.Max(1, (int)Math.Round(info.Height * scale));
            SKBitmap? exact = decoded.Width == width && decoded.Height == height
                ? null
                : decoded.Resize(
                    new SKSizeI(width, height),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));

            try
            {
                using SKImage image = SKImage.FromBitmap(exact ?? decoded);
                using SKData? encoded = image.Encode(SKEncodedImageFormat.Webp, Quality);
                return encoded?.ToArray();
            }
            finally
            {
                exact?.Dispose();
            }
        }
        catch (Exception)
        {
            // Broken or exotic images are not worth surfacing; the caller
            // shows the placeholder glyph instead.
            return null;
        }
    }
}

/// <summary>
/// Cover thumbnails, keyed by the track they are read from and held as
/// <em>encoded</em> WebP bytes — never as decoded bitmaps.
///
/// That distinction is the whole point. Skia's pixel buffers are native memory
/// behind a plain handle: the GC cannot see their size, so a cache of decoded
/// bitmaps neither respects its own bound nor gets reclaimed promptly when it
/// evicts. Encoded bytes are ordinary managed arrays, so the bound below is
/// real. Callers decode their own bitmap at the size they draw and own its
/// lifetime, which means no two owners ever share one bitmap.
///
/// Behind the in-memory bound sits <see cref="ThumbnailStore"/>, so a cover
/// survives a restart and is read from its file and re-encoded once ever.
///
/// All members must be called on the UI thread; reading, scaling and encoding
/// happen off it.
/// </summary>
public sealed class ArtworkCache(LibraryService library) : IDisposable
{
    /// <summary>
    /// Total encoded bytes held. Thumbnails run ~20-60 KB, so this keeps a few
    /// hundred covers — far more than any view shows at once.
    /// </summary>
    private const long MaxBytes = 24 * 1024 * 1024;

    private readonly Dictionary<long, LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];
    private readonly Dictionary<long, Task<byte[]?>> _inFlight = [];
    private long _bytes;
    private ThumbnailStore? _store;

    /// <summary>
    /// The cover of <paramref name="trackId"/> as encoded thumbnail bytes, or
    /// null when the track has none.
    /// </summary>
    public Task<byte[]?> GetThumbnailAsync(long trackId)
    {
        if (_entries.TryGetValue(trackId, out LinkedListNode<Entry>? node))
        {
            Touch(node);
            return Task.FromResult(node.Value.Thumbnail);
        }

        // A block scrolling in and out re-asks while the first read is still
        // running; both callers must share that one read.
        if (_inFlight.TryGetValue(trackId, out Task<byte[]?>? pending))
        {
            return pending;
        }

        Task<byte[]?> load = LoadAsync(trackId);
        _inFlight[trackId] = load;
        return load;
    }

    /// <summary>
    /// Points the cache at the library now open, dropping what it held for the
    /// previous one. Track ids do not survive a library switch, so the entries
    /// would otherwise name tracks in a library that is no longer open; the
    /// on-disk store lives beside its own library and is simply reopened.
    /// </summary>
    public void UseCurrentLibrary()
    {
        _entries.Clear();
        _lru.Clear();
        _inFlight.Clear();
        _bytes = 0;

        _store?.Dispose();
        try
        {
            _store = new ThumbnailStore(library.DatabasePath);
        }
        catch (SqliteException)
        {
            // A read-only or otherwise unusable location costs the persistent
            // half of the cache, not the feature: covers still load per session.
            _store = null;
        }
    }

    /// <summary>
    /// Throws away every cached cover, in memory and on disk. Nothing
    /// validates a stored thumbnail against its file, so this is how re-tagged
    /// artwork gets picked up.
    /// </summary>
    public void Discard()
    {
        _store?.Clear();
        _entries.Clear();
        _lru.Clear();
        _inFlight.Clear();
        _bytes = 0;
    }

    /// <inheritdoc />
    public void Dispose() => _store?.Dispose();

    private async Task<byte[]?> LoadAsync(long trackId)
    {
        byte[]? thumbnail = null;
        try
        {
            ThumbnailStore? store = _store;
            if (store is not null)
            {
                (bool stored, byte[]? cached) = await Task.Run(() =>
                    (store.TryGet(trackId, out byte[]? found), found));
                if (stored)
                {
                    thumbnail = cached;
                    return thumbnail;
                }
            }

            // Only the path lookup goes through the library's gate; the file
            // read and the re-encode stay off it so a wall of covers cannot
            // stall row hydration behind them.
            if (await library.GetPathAsync(trackId) is { } path)
            {
                thumbnail = await Task.Run(() =>
                    ArtworkReader.Read(path) is { } source
                        ? Thumbnailer.Encode(source)
                        : null);
            }

            // Recorded even when there is no cover: that answer costs a file
            // read to reach, and a coverless album must not pay it every launch.
            if (store is not null)
            {
                byte[]? toStore = thumbnail;
                await Task.Run(() => store.Put(trackId, toStore));
            }
        }
        finally
        {
            _inFlight.Remove(trackId);
            Store(trackId, thumbnail);
        }

        return thumbnail;
    }

    private void Store(long trackId, byte[]? thumbnail)
    {
        if (_entries.ContainsKey(trackId))
        {
            return;
        }

        // Nulls are cached too: "this track has no cover" is an answer worth
        // remembering, and it is the expensive one to recompute.
        LinkedListNode<Entry> node = _lru.AddFirst(new Entry(trackId, thumbnail));
        _entries[trackId] = node;
        _bytes += thumbnail?.LongLength ?? 0;

        while (_bytes > MaxBytes && _lru.Last is { } oldest)
        {
            _lru.RemoveLast();
            _entries.Remove(oldest.Value.TrackId);
            _bytes -= oldest.Value.Thumbnail?.LongLength ?? 0;
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

    private sealed record Entry(long TrackId, byte[]? Thumbnail);
}
