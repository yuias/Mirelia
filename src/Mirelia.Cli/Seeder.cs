using Microsoft.Data.Sqlite;
using Mirelia.Library.Database;
using Mirelia.Query.Text;

namespace Mirelia.Cli;

/// <summary>
/// Fills a library with synthetic tracks (no files on disk) so UI
/// virtualization and query performance can be measured at 100k+ rows
/// without owning a library that size. Deterministic by seed.
/// </summary>
internal static class Seeder
{
    private static readonly string[] GenreNames =
        ["Rock", "Jazz", "Classical", "Electronic", "Pop", "Hip-Hop", "Ambient", "Folk"];

    private static readonly string[] Codecs = ["mp3", "aac", "alac", "flac", "pcm"];

    internal static void Seed(LibraryDatabase db, int trackCount)
    {
        var rng = new Random(42);
        int artistCount = Math.Max(1, trackCount / 50);
        int albumCount = Math.Max(1, trackCount / 12);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using LibraryDatabase.TransactionScope tx = db.BeginTransaction();

        var genreIds = new long[GenreNames.Length];
        for (int i = 0; i < GenreNames.Length; i++)
        {
            genreIds[i] = InsertNamed(db, "genre", GenreNames[i]);
        }

        var artistIds = new long[artistCount];
        for (int i = 0; i < artistCount; i++)
        {
            artistIds[i] = InsertNamed(db, "artist", $"Artist {i:D4}");
        }

        var albumIds = new long[albumCount];
        var albumArtists = new long[albumCount];
        using (SqliteCommand album = db.Command("""
            INSERT INTO album (name, name_sort, name_search, album_artist_id)
            VALUES ($n, $s, $s, $a) RETURNING id
            """))
        {
            SqliteParameter n = album.Parameters.AddWithValue("$n", string.Empty);
            SqliteParameter s = album.Parameters.AddWithValue("$s", string.Empty);
            SqliteParameter a = album.Parameters.AddWithValue("$a", 0L);
            for (int i = 0; i < albumCount; i++)
            {
                string name = $"Album {i:D5}";
                n.Value = name;
                s.Value = TextNormalizer.NormalizeSearch(name);
                albumArtists[i] = artistIds[rng.Next(artistIds.Length)];
                a.Value = albumArtists[i];
                albumIds[i] = (long)album.ExecuteScalar()!;
            }
        }

        using SqliteCommand track = db.Command("""
            INSERT INTO track (
                path, path_lower, size_bytes, mtime_unix,
                title, album_id, artist_id, album_artist_id, genre_id,
                track_no, disc_no, year,
                title_sort, title_search,
                duration_ms, sample_rate, channels, bit_depth, bitrate_kbps,
                codec, is_lossless, rating, play_count,
                added_unix, modified_unix)
            VALUES (
                $path, $path, $size, $now,
                $title, $album, $artist, $albumartist, $genre,
                $trackno, 1, $year,
                $sort, $sort,
                $dur, 44100, 2, $bits, $kbps,
                $codec, $lossless, $rating, $plays,
                $added, $now)
            RETURNING id
            """);
        SqliteParameter pPath = track.Parameters.AddWithValue("$path", string.Empty);
        SqliteParameter pSize = track.Parameters.AddWithValue("$size", 0L);
        track.Parameters.AddWithValue("$now", now);
        SqliteParameter pTitle = track.Parameters.AddWithValue("$title", string.Empty);
        SqliteParameter pAlbum = track.Parameters.AddWithValue("$album", 0L);
        SqliteParameter pArtist = track.Parameters.AddWithValue("$artist", 0L);
        SqliteParameter pAlbumArtist = track.Parameters.AddWithValue("$albumartist", 0L);
        SqliteParameter pGenre = track.Parameters.AddWithValue("$genre", 0L);
        SqliteParameter pTrackNo = track.Parameters.AddWithValue("$trackno", 0);
        SqliteParameter pYear = track.Parameters.AddWithValue("$year", 0);
        SqliteParameter pSort = track.Parameters.AddWithValue("$sort", string.Empty);
        SqliteParameter pDur = track.Parameters.AddWithValue("$dur", 0L);
        SqliteParameter pBits = track.Parameters.AddWithValue("$bits", (object?)null);
        SqliteParameter pKbps = track.Parameters.AddWithValue("$kbps", 0);
        SqliteParameter pCodec = track.Parameters.AddWithValue("$codec", string.Empty);
        SqliteParameter pLossless = track.Parameters.AddWithValue("$lossless", 0);
        SqliteParameter pRating = track.Parameters.AddWithValue("$rating", (object?)null);
        SqliteParameter pPlays = track.Parameters.AddWithValue("$plays", 0);
        SqliteParameter pAdded = track.Parameters.AddWithValue("$added", 0L);

        using SqliteCommand fts = db.Command("""
            INSERT INTO track_fts (rowid, title, artist, album, album_artist, composer, genre)
            VALUES ($id, $title, $artist, $album, $artist, '', $genre)
            """);
        SqliteParameter fId = fts.Parameters.AddWithValue("$id", 0L);
        SqliteParameter fTitle = fts.Parameters.AddWithValue("$title", string.Empty);
        SqliteParameter fArtist = fts.Parameters.AddWithValue("$artist", string.Empty);
        SqliteParameter fAlbum = fts.Parameters.AddWithValue("$album", string.Empty);
        SqliteParameter fGenre = fts.Parameters.AddWithValue("$genre", string.Empty);

        for (int i = 0; i < trackCount; i++)
        {
            int albumIx = rng.Next(albumCount);
            string title = $"Track {i:D6}";
            string codec = Codecs[rng.Next(Codecs.Length)];
            bool lossless = codec is "flac" or "alac" or "pcm";

            pPath.Value = $"Z:\\seeded\\{i:D6}.{codec}";
            pSize.Value = (long)rng.Next(2_000_000, 60_000_000);
            pTitle.Value = title;
            pAlbum.Value = albumIds[albumIx];
            pArtist.Value = artistIds[rng.Next(artistIds.Length)];
            pAlbumArtist.Value = albumArtists[albumIx];
            pGenre.Value = genreIds[rng.Next(genreIds.Length)];
            pTrackNo.Value = (i % 12) + 1;
            pYear.Value = rng.Next(1965, 2027);
            pSort.Value = TextNormalizer.NormalizeSearch(title);
            pDur.Value = (long)rng.Next(90_000, 420_000);
            pBits.Value = lossless ? 16 : DBNull.Value;
            pKbps.Value = lossless ? 900 : rng.Next(128, 320);
            pCodec.Value = codec;
            pLossless.Value = lossless ? 1 : 0;
            pRating.Value = rng.Next(5) == 0 ? rng.Next(0, 101) : DBNull.Value;
            pPlays.Value = rng.Next(0, 200);
            pAdded.Value = now - rng.Next(0, 3 * 365 * 24 * 3600);
            long id = (long)track.ExecuteScalar()!;

            fId.Value = id;
            fTitle.Value = title;
            fArtist.Value = $"Artist {rng.Next(artistCount):D4}";
            fAlbum.Value = $"Album {albumIx:D5}";
            fGenre.Value = GenreNames[rng.Next(GenreNames.Length)];
            fts.ExecuteNonQuery();
        }

        db.BumpLibraryGeneration();
        tx.Commit();
    }

    private static long InsertNamed(LibraryDatabase db, string table, string name)
    {
        return (long)db.Scalar(
            $"INSERT INTO {table} (name, name_sort, name_search) VALUES ($n, $s, $s) RETURNING id",
            ("$n", name),
            ("$s", TextNormalizer.NormalizeSearch(name)))!;
    }
}
