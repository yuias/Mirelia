using Mirelia.Library.Database;
using Mirelia.Library.Export;
using Mirelia.Library.Scanning;
using Xunit;

namespace Mirelia.Library.Tests;

/// <summary>Exports over the scanned fixture library (real files on disk).</summary>
public sealed class M3u8ExporterTests : IDisposable
{
    private readonly string _workDir;
    private readonly string _musicDir;
    private readonly string _dbPath;
    private readonly LibraryDatabase _db;
    private readonly List<long> _ids;

    private static string FixturesDir => Path.Combine(AppContext.BaseDirectory, "fixtures");

    public M3u8ExporterTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"mirelia-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);

        // Audio lives inside the work dir so playlist and files share a
        // volume — relative paths across drive roots cannot exist.
        _musicDir = Path.Combine(_workDir, "music");
        Directory.CreateDirectory(_musicDir);
        foreach (string file in Directory.GetFiles(FixturesDir))
        {
            File.Copy(file, Path.Combine(_musicDir, Path.GetFileName(file)));
        }

        _dbPath = Path.Combine(_workDir, "library.db");
        _db = LibraryDatabase.Open(_dbPath);
        new LibraryScanner(_db).Scan(_musicDir);

        using var cmd = _db.Command("SELECT id FROM track ORDER BY codec");
        using var reader = cmd.ExecuteReader();
        _ids = [];
        while (reader.Read())
        {
            _ids.Add(reader.GetInt64(0));
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        Directory.Delete(_workDir, recursive: true);
    }

    [Fact]
    public void WritesExtendedM3uWithAbsolutePaths()
    {
        string outPath = Path.Combine(_workDir, "all.m3u8");
        ExportResult result = M3u8Exporter.Export(_db, _ids, outPath);

        Assert.Equal(5, result.Written);
        Assert.Equal(0, result.Missing);

        string[] lines = File.ReadAllLines(outPath);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.StartsWith("#EXTINF:0,", lines[1]); // 0.4 s rounds to 0
        Assert.True(Path.IsPathRooted(lines[2]));
        Assert.Contains("Fixture Artist - ", string.Join('\n', lines));
        // Japanese metadata survives the UTF-8 write.
        Assert.Contains("山田太郎 - テスト曲", string.Join('\n', lines));
    }

    [Fact]
    public void RelativePathsAreRelativeToThePlaylist()
    {
        string outPath = Path.Combine(_workDir, "rel.m3u8");
        M3u8Exporter.Export(_db, _ids, outPath, new M3u8Options { RelativePaths = true });

        string[] paths = File.ReadAllLines(outPath).Where(l => !l.StartsWith('#')).ToArray();
        Assert.Equal(5, paths.Length);
        Assert.All(paths, p => Assert.False(Path.IsPathRooted(p), p));
        Assert.All(paths, p => Assert.True(
            File.Exists(Path.GetFullPath(Path.Combine(_workDir, p))), p));
    }

    [Fact]
    public void PrefixRewriteRemapsPaths()
    {
        string outPath = Path.Combine(_workDir, "rewrite.m3u8");
        string from = Path.GetFullPath(_musicDir);
        M3u8Exporter.Export(_db, _ids, outPath, new M3u8Options
        {
            PathRewrite = (from, "/mnt/music"),
        });

        string[] paths = File.ReadAllLines(outPath).Where(l => !l.StartsWith('#')).ToArray();
        Assert.All(paths, p => Assert.StartsWith("/mnt/music", p));
    }

    [Fact]
    public void CopyFilesPlacesCopiesNextToThePlaylist()
    {
        string subDir = Path.Combine(_workDir, "device");
        string outPath = Path.Combine(subDir, "sync.m3u8");
        ExportResult result = M3u8Exporter.Export(
            _db, _ids, outPath, new M3u8Options { CopyFiles = true });

        Assert.Equal(5, result.Copied);
        string[] paths = File.ReadAllLines(outPath).Where(l => !l.StartsWith('#')).ToArray();
        Assert.All(paths, p =>
        {
            Assert.False(Path.IsPathRooted(p), p);
            Assert.True(File.Exists(Path.Combine(subDir, p)), p);
        });
    }

    [Fact]
    public void MissingFilesAreCountedNotWritten()
    {
        long ghost = (long)_db.Scalar("""
            INSERT INTO track (path, path_lower, size_bytes, mtime_unix, title,
                title_sort, title_search, duration_ms, sample_rate, channels,
                codec, is_lossless, added_unix, modified_unix)
            VALUES ('Z:\gone.flac', 'z:\gone.flac', 1, 0, 'Ghost',
                'ghost', 'ghost', 1000, 44100, 2, 'flac', 1, 0, 0)
            RETURNING id
            """)!;

        string outPath = Path.Combine(_workDir, "missing.m3u8");
        ExportResult result = M3u8Exporter.Export(_db, [ghost, _ids[0]], outPath);
        Assert.Equal(1, result.Written);
        Assert.Equal(1, result.Missing);
    }
}
