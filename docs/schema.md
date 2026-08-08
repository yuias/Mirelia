# Database schema and migration log

SQLite via `Microsoft.Data.Sqlite`, raw ADO.NET (ADR 0003).
`meta['schema_version']` tracks the applied version; migrations are applied
in-order at startup and logged at the bottom of this file.

Pragmas at open:

```
journal_mode = WAL
synchronous  = NORMAL
foreign_keys = ON
temp_store   = MEMORY
mmap_size    = 268435456
```

## Schema v1

```sql
CREATE TABLE track (
    id                INTEGER PRIMARY KEY,

    -- identity ------------------------------------------------------------
    path              TEXT    NOT NULL,          -- absolute, OS-native separators
    path_lower        TEXT    NOT NULL,          -- for case-insensitive lookup
    volume_id         TEXT,                      -- Win: volume serial; mac: fsid
    file_id           TEXT,                      -- Win: FileId; mac: inode. Move detection.
    size_bytes        INTEGER NOT NULL,
    mtime_unix        INTEGER NOT NULL,
    content_hash      BLOB,                      -- xxh3 of first+last 1MiB + size. Lazy.

    -- descriptive ---------------------------------------------------------
    title             TEXT,
    album_id          INTEGER REFERENCES album(id),
    artist_id         INTEGER REFERENCES artist(id),
    album_artist_id   INTEGER REFERENCES artist(id),
    composer_id       INTEGER REFERENCES artist(id),
    genre_id          INTEGER REFERENCES genre(id),
    track_no          INTEGER,
    disc_no           INTEGER,
    year              INTEGER,
    date_text         TEXT,                      -- original, unparsed

    -- sorting / matching --------------------------------------------------
    title_sort        TEXT NOT NULL,
    title_search      TEXT NOT NULL DEFAULT '',   -- normalized for =~ matching
    -- (album/artist sort keys live on their own tables)

    -- technical -----------------------------------------------------------
    duration_ms       INTEGER NOT NULL,
    sample_rate       INTEGER NOT NULL,
    channels          INTEGER NOT NULL,
    bit_depth         INTEGER,                   -- NULL for lossy
    bitrate_kbps      INTEGER,
    codec             TEXT    NOT NULL,          -- 'mp3','aac','alac','flac','pcm',...
    is_lossless       INTEGER NOT NULL,

    -- gapless -------------------------------------------------------------
    encoder_delay     INTEGER,                   -- frames
    encoder_padding   INTEGER,                   -- frames
    exact_frames      INTEGER,                   -- decoded length, frames

    -- loudness ------------------------------------------------------------
    rg_track_gain     REAL,
    rg_track_peak     REAL,
    rg_album_gain     REAL,
    rg_album_peak     REAL,

    -- user state ----------------------------------------------------------
    rating            INTEGER,                   -- 0..100, NULL = unrated
    play_count        INTEGER NOT NULL DEFAULT 0,
    skip_count        INTEGER NOT NULL DEFAULT 0,
    last_played_unix  INTEGER,
    added_unix        INTEGER NOT NULL,
    modified_unix     INTEGER NOT NULL,

    tag_generation    INTEGER NOT NULL DEFAULT 0 -- bumped on any tag write
);

CREATE UNIQUE INDEX ix_track_path ON track(path_lower);
CREATE INDEX ix_track_album  ON track(album_id, disc_no, track_no);
CREATE INDEX ix_track_artist ON track(artist_id, title_sort);
CREATE INDEX ix_track_added  ON track(added_unix DESC);
CREATE INDEX ix_track_fileid ON track(volume_id, file_id);

-- Interned string tables. UNIQUE(name) keeps the string heap small and makes
-- browse-by-artist a plain index scan.
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

-- Full-text search over the fields users actually type into the search box.
-- contentless_delete requires SQLite 3.43+; LibraryDatabase.Open asserts it.
CREATE VIRTUAL TABLE track_fts USING fts5(
    title, artist, album, album_artist, composer, genre,
    content='', contentless_delete=1,
    tokenize='unicode61 remove_diacritics 2'
);

CREATE TABLE playlist (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL,
    kind          TEXT NOT NULL,      -- 'static' | 'smart' | 'folder'
    parent_id     INTEGER REFERENCES playlist(id),
    sort_order    INTEGER NOT NULL,
    -- smart only:
    query_text    TEXT,               -- canonical DSL source. The source of truth.
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
    completed   INTEGER NOT NULL     -- 0 = skipped
);

CREATE TABLE watch_folder (
    id             INTEGER PRIMARY KEY,
    path           TEXT NOT NULL UNIQUE,
    recursive      INTEGER NOT NULL DEFAULT 1,
    last_scan_unix INTEGER
);

CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
-- meta['schema_version'], meta['library_generation'], ...
```

Notes:

- `library_generation` is a monotonically increasing counter bumped on any
  write that could change smart-playlist membership. Cached smart-playlist
  results are keyed on it.
- Sort keys: explicit sort tag (normalized) wins; otherwise derived via
  NFKC → katakana→hiragana → casefold → leading-article strip. The rules
  live in `Mirelia.Query.Text.TextNormalizer` — write side (scanner) and
  read side (`=~` compiler) share them by construction.
- `*_search` shadow columns hold the same normalization applied to the raw
  value, without article stripping.

## Schema v2 additions

```sql
-- Per-view UI settings (columns, sort), keyed by scope:
-- 'source:music', 'source:recent', 'playlist:<id>'.
CREATE TABLE view_settings (
    scope_key TEXT PRIMARY KEY,
    json      TEXT NOT NULL
);
```

## Schema v3 additions

```sql
-- Normalized release-date sort key: yyyymmdd as INTEGER, missing month/day
-- as 01 (so "2025" sorts as 2025-01-01). Computed from date_text (which
-- keeps the raw spelling for display) with year as fallback; backfilled
-- from existing rows during the migration.
ALTER TABLE track ADD COLUMN date_key INTEGER;
```

## Migration log

| Version | Date | Change |
|---|---|---|
| 1 | 2026-08-08 | Initial schema, applied by `LibraryDatabase.Open` |
| 2 | 2026-08-08 | `view_settings` table for per-view columns/sort |
| 3 | 2026-08-08 | `track.date_key` normalized date sort key, backfilled |
