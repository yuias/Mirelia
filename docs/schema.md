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

## Which file is open

One library at a time. The app opens `%APPDATA%/Mirelia/library.db` unless
`MIRELIA_DB` is set (tests and the UI smoke run) or the user picked another
file with **File ▸ Open Library…**, which is remembered in `settings.json` as
`LibraryPath` and reopened next launch — MIRELIA_DB still outranks it.

`settings.json` itself is anchored to the default library's folder, not the
open one, so theme, font, output device and window placement do not fork per
library. Everything genuinely per-library — playlists, view settings, sync
targets — already lives inside the `.db`, so it moves with the file.

A switch reopens the new file before closing the old one, so a bad pick leaves
the current library untouched. Track and playlist ids do not survive the move:
playback stops, the queue is emptied, and every cached view is rebuilt.
`LibraryDatabase.DescribeLibraryProblem` gates the pick — `Open` would
otherwise adopt an unrelated file as a brand-new library and start creating
tables in it.

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
    modified_unix INTEGER NOT NULL,
    -- v7: MusicBee's Playlist Persistent ID when this row came from an
    -- import, so --merge follows the playlist through a rename on either
    -- side. Null for anything made here.
    mb_persistent_id TEXT
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

One reserved key is not a view: `tree:playlists` holds the ids of the
expanded playlist folders. They are rows of this library, so the state cannot
live in the app-level settings file.

## Schema v3 additions

```sql
-- Normalized release-date sort key: yyyymmdd as INTEGER, missing month/day
-- as 01 (so "2025" sorts as 2025-01-01). Computed from date_text (which
-- keeps the raw spelling for display) with year as fallback; backfilled
-- from existing rows during the migration.
ALTER TABLE track ADD COLUMN date_key INTEGER;
```

## Schema v4 additions

```sql
-- A named sync destination. 'folder' targets write to a mounted path;
-- 'network' targets serve over HTTP. config_json holds the per-kind
-- settings (roots, path style, layout template, orphan policy, and the
-- delivery/transcode slot, left empty until that pipeline exists).
CREATE TABLE sync_target (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE,
    kind          TEXT NOT NULL,      -- 'folder' | 'network'
    config_json   TEXT NOT NULL,
    created_unix  INTEGER NOT NULL,
    modified_unix INTEGER NOT NULL
);

-- Populated only when config_json's scope is 'selected'.
CREATE TABLE sync_target_scope (
    target_id   INTEGER NOT NULL REFERENCES sync_target(id) ON DELETE CASCADE,
    playlist_id INTEGER NOT NULL REFERENCES playlist(id) ON DELETE CASCADE,
    PRIMARY KEY (target_id, playlist_id)
) WITHOUT ROWID;

-- What is actually on the target, as of the last sync. This is the source of
-- truth for deletion and orphan detection: destination paths are never
-- re-derived from the layout template, so a template change or a collision
-- suffix cannot orphan files already written.
CREATE TABLE sync_target_file (
    target_id      INTEGER NOT NULL REFERENCES sync_target(id) ON DELETE CASCADE,
    track_id       INTEGER NOT NULL REFERENCES track(id) ON DELETE CASCADE,
    rel_path       TEXT    NOT NULL,  -- below musicRoot, '/'-separated
    rel_path_lower TEXT    NOT NULL,
    size_bytes     INTEGER NOT NULL,
    mtime_unix     INTEGER NOT NULL,
    content_hash   BLOB,
    synced_unix    INTEGER NOT NULL,
    PRIMARY KEY (target_id, track_id)
) WITHOUT ROWID;

CREATE UNIQUE INDEX ix_sync_target_file_path
    ON sync_target_file(target_id, rel_path_lower);
```

## Schema v5 additions

```sql
-- Reverse index on playlist membership. `playlist = "X"` compiles to a
-- correlated EXISTS keyed on pe.track_id, but playlist_entry is indexed by
-- (playlist_id, position) only, so that lookup scanned the whole entry table
-- once per candidate track and once per reference. A smart playlist combining
-- nine references never finished evaluating.
CREATE INDEX ix_playlist_entry_track ON playlist_entry(track_id);
```

## Schema v6 additions

```sql
-- User-defined sort orders. terms_json is the ordered term list a sort
-- compiles from: [{"Field":"albumartist","Descending":false}, …], with
-- Field naming an entry in Mirelia.Library.Querying.SortFields.
CREATE TABLE sort_order (
    id            INTEGER PRIMARY KEY,
    name          TEXT NOT NULL UNIQUE,
    terms_json    TEXT NOT NULL,
    position      INTEGER NOT NULL,
    created_unix  INTEGER NOT NULL,
    modified_unix INTEGER NOT NULL
);
```

The built-in orders (Playlist Order, Artist, Album, Title, Recently Added,
Year, Album Year) are **not** rows here. They cannot be edited or deleted, so
a copy per library would only be state to keep in step with the build; they
live in `SortPresets` instead. `view_settings.json` gains `CustomSort`, the id
of a row above; when it is null — or names a row since deleted — the view
falls back to its `Sort` preset.

## Schema v7 additions

```sql
-- MusicBee's Playlist Persistent ID on rows an import created, so --merge
-- can follow a playlist through a rename on either side instead of matching
-- on the name.
ALTER TABLE playlist ADD COLUMN mb_persistent_id TEXT;
CREATE INDEX ix_playlist_mb_persistent_id
    ON playlist(mb_persistent_id) WHERE mb_persistent_id IS NOT NULL;
```

The index is deliberately **not** unique. Two MusicBee libraries imported into
one Mirelia library could reuse an id, and a unique index would turn that into
an import that fails outright with nothing the user can do about it; matching
one of the two is the milder wrong answer.

An import run before v7 left no id behind. The first `--merge` after the
migration therefore still matches by name, and writes the id while it does, so
every later run matches on the id. Rows that already carry an id are excluded
from that fallback, which keeps the two candidate sets disjoint. An export
with no ids at all stores none. See `docs/musicbee-import.md`.

## Migration log

| Version | Date | Change |
|---|---|---|
| 1 | 2026-08-08 | Initial schema, applied by `LibraryDatabase.Open` |
| 2 | 2026-08-08 | `view_settings` table for per-view columns/sort |
| 3 | 2026-08-08 | `track.date_key` normalized date sort key, backfilled |
| 4 | 2026-08-13 | `sync_target`, `sync_target_scope`, `sync_target_file` for sync |
| 5 | 2026-08-15 | `ix_playlist_entry_track`, so `playlist =` references stop scanning |
| 6 | 2026-08-17 | `sort_order` for user-defined sort orders; `view_settings.json` gains `CustomSort` |
| 7 | 2026-09-21 | `playlist.mb_persistent_id`, so re-importing a MusicBee library follows its playlists through a rename instead of matching by name |
