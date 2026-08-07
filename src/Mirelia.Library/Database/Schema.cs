namespace Mirelia.Library.Database;

/// <summary>
/// Schema DDL. Version history and rationale live in docs/schema.md; the two
/// must be kept in sync (the migration log there is the authority on what
/// each version contains).
/// </summary>
internal static class Schema
{
    /// <summary>Current schema version written to <c>meta['schema_version']</c>.</summary>
    internal const int CurrentVersion = 1;

    /// <summary>Schema v1, executed on a fresh database.</summary>
    internal const string V1 = """
        CREATE TABLE track (
            id                INTEGER PRIMARY KEY,

            path              TEXT    NOT NULL,
            path_lower        TEXT    NOT NULL,
            volume_id         TEXT,
            file_id           TEXT,
            size_bytes        INTEGER NOT NULL,
            mtime_unix        INTEGER NOT NULL,
            content_hash      BLOB,

            title             TEXT,
            album_id          INTEGER REFERENCES album(id),
            artist_id         INTEGER REFERENCES artist(id),
            album_artist_id   INTEGER REFERENCES artist(id),
            composer_id       INTEGER REFERENCES artist(id),
            genre_id          INTEGER REFERENCES genre(id),
            track_no          INTEGER,
            disc_no           INTEGER,
            year              INTEGER,
            date_text         TEXT,

            title_sort        TEXT NOT NULL,
            title_search      TEXT NOT NULL DEFAULT '',

            duration_ms       INTEGER NOT NULL,
            sample_rate       INTEGER NOT NULL,
            channels          INTEGER NOT NULL,
            bit_depth         INTEGER,
            bitrate_kbps      INTEGER,
            codec             TEXT    NOT NULL,
            is_lossless       INTEGER NOT NULL,

            encoder_delay     INTEGER,
            encoder_padding   INTEGER,
            exact_frames      INTEGER,

            rg_track_gain     REAL,
            rg_track_peak     REAL,
            rg_album_gain     REAL,
            rg_album_peak     REAL,

            rating            INTEGER,
            play_count        INTEGER NOT NULL DEFAULT 0,
            skip_count        INTEGER NOT NULL DEFAULT 0,
            last_played_unix  INTEGER,
            added_unix        INTEGER NOT NULL,
            modified_unix     INTEGER NOT NULL,

            tag_generation    INTEGER NOT NULL DEFAULT 0
        );

        CREATE UNIQUE INDEX ix_track_path ON track(path_lower);
        CREATE INDEX ix_track_album  ON track(album_id, disc_no, track_no);
        CREATE INDEX ix_track_artist ON track(artist_id, title_sort);
        CREATE INDEX ix_track_added  ON track(added_unix DESC);
        CREATE INDEX ix_track_fileid ON track(volume_id, file_id);

        CREATE TABLE artist (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL UNIQUE,
            name_sort   TEXT NOT NULL,
            name_search TEXT NOT NULL
        );

        CREATE TABLE album (
            id              INTEGER PRIMARY KEY,
            name            TEXT NOT NULL,
            name_sort       TEXT NOT NULL,
            name_search     TEXT NOT NULL,
            album_artist_id INTEGER REFERENCES artist(id),
            UNIQUE(name, album_artist_id)
        );

        CREATE TABLE genre (
            id          INTEGER PRIMARY KEY,
            name        TEXT NOT NULL UNIQUE,
            name_sort   TEXT NOT NULL,
            name_search TEXT NOT NULL
        );

        CREATE VIRTUAL TABLE track_fts USING fts5(
            title, artist, album, album_artist, composer, genre,
            content='', contentless_delete=1,
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TABLE playlist (
            id            INTEGER PRIMARY KEY,
            name          TEXT NOT NULL,
            kind          TEXT NOT NULL,
            parent_id     INTEGER REFERENCES playlist(id),
            sort_order    INTEGER NOT NULL,
            query_text    TEXT,
            query_limit   INTEGER,
            query_order   TEXT,
            live_update   INTEGER NOT NULL DEFAULT 1,
            created_unix  INTEGER NOT NULL,
            modified_unix INTEGER NOT NULL
        );

        CREATE TABLE playlist_entry (
            playlist_id INTEGER NOT NULL REFERENCES playlist(id) ON DELETE CASCADE,
            position    INTEGER NOT NULL,
            track_id    INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
            PRIMARY KEY (playlist_id, position)
        ) WITHOUT ROWID;

        CREATE TABLE play_history (
            id          INTEGER PRIMARY KEY,
            track_id    INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
            played_unix INTEGER NOT NULL,
            completed   INTEGER NOT NULL
        );

        CREATE TABLE watch_folder (
            id             INTEGER PRIMARY KEY,
            path           TEXT NOT NULL UNIQUE,
            recursive      INTEGER NOT NULL DEFAULT 1,
            last_scan_unix INTEGER
        );

        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
        """;
}
