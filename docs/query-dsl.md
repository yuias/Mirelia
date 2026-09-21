# Smart-playlist query DSL — grammar and semantics

The DSL text stored in `playlist.query_text` is the **canonical persisted
form** of a smart playlist. The GUI condition builder and the advanced text
editor are two views of one thing and must round-trip through it.

Status: grammar frozen at kickoff; the field vocabulary still grows.
Implementation lives in `src/Mirelia.Query`.

## v1 implementation decisions

- `any(...)` accepts a **quoted string only** (the grammar's inner `or_expr`
  is reserved for a later version).
- A bare number on a duration field means **seconds** (`duration > 180` ==
  `duration > 180s`).
- Relative-date units `mo` and `y` are fixed 30-day / 365-day approximations;
  calendar-aware arithmetic is post-v1.
- `~=` (regex) matches the raw column value, case-insensitively, with a
  bounded execution time; the other text operators match the normalized
  `*_search` shadow columns.
- `@now` is resolved when the query is compiled, not per row.
- Keywords are case-insensitive on input; the canonical printed form
  uppercases keywords and lowercases field names.
- Date literals are `YYYY-MM-DD` (optionally with `THH:MM[:SS]`) or
  `YYYY-MM`; on datetime fields a month literal means its first instant.
- `date` targets the normalized `date_key` and takes `YYYY` (a bare
  number), `YYYY-MM`, or `YYYY-MM-DD`. A partial date covers its whole
  period: `=` is containment (`date = 2025` matches all of 2025), `>=`/`<`
  anchor at the period start, `<=`/`>` at its end, and BETWEEN/IN bounds
  are inclusive periods. Caveat: year-only tags are stored as Jan 1
  (`date_key` padding), so month/day-precision predicates cannot match
  them — prefer year or range predicates when tags may be coarse.
- The GUI condition builder (`SimpleQueryModel`) displays a recursive tree
  of AND/OR groups over comparisons and bool tests, at most one ORDER BY
  term, and a plain LIMIT. IN/BETWEEN, playlist refs, `any()`, `NOT` over
  anything but a bare bool field, and `LIMIT … BY` stay text-editor-only;
  such queries open on the Text tab and remain fully editable there.

## Grammar

```ebnf
query      = or_expr [ order_clause ] [ limit_clause ] ;

or_expr    = and_expr { ( "OR" | "||" ) and_expr } ;
and_expr   = unary    { ( "AND" | "&&" ) unary } ;
unary      = [ "NOT" | "!" ] primary ;
primary    = "(" or_expr ")" | predicate ;

predicate  = field op value
           | field "IN" "(" value { "," value } ")"
           | field "BETWEEN" value "AND" value
           | "playlist" ( "=" | "!=" ) string          (* membership *)
           | "any" "(" or_expr ")" ;                   (* free-text over FTS *)

op         = "=" | "!=" | "<" | "<=" | ">" | ">="
           | "=~"          (* contains, case/width/kana-insensitive *)
           | "!~"
           | "^="          (* starts with *)
           | "$="          (* ends with *)
           | "~="          (* regex *) ;

value      = string | number | duration | datetime | relative | "null" ;
relative   = "@now" [ ( "-" | "+" ) number unit ] ;
unit       = "s" | "m" | "h" | "d" | "w" | "mo" | "y" ;
duration   = number ( "s" | "m" | "h" ) ;

order_clause = "ORDER" "BY" order_term { "," order_term } ;
order_term   = ( field | "random" ) [ "ASC" | "DESC" ] ;
limit_clause = "LIMIT" number [ "BY" field ] ;   (* BY = at most N per group *)
```

## Examples

```
genre = "Jazz" AND rating >= 80 ORDER BY added DESC LIMIT 100

albumartist =~ "山田" AND (playcount > 10 OR lastplayed > @now - 30d)

is_lossless AND samplerate > 44100 ORDER BY random LIMIT 50

any("live") AND NOT playlist = "Excluded" LIMIT 3 BY albumartist
```

## Field vocabulary

Fixed, documented here, mapped to columns by a table in `Mirelia.Query`.
Adding a field is a one-line change plus a row below.

| Field | Column | Type | Notes |
|---|---|---|---|
| `title` | `track.title` | string | |
| `artist` | `artist.name` via `artist_id` | string | |
| `albumartist` | `artist.name` via `album_artist_id` | string | |
| `composer` | `artist.name` via `composer_id` | string | |
| `album` | `album.name` via `album_id` | string | |
| `genre` | `genre.name` via `genre_id` | string | |
| `year` | `track.year` | number | |
| `date` | `track.date_key` | date-key | release date, yyyymmdd; partial dates = periods |
| `rating` | `track.rating` | number | 0..100, `null` = unrated |
| `playcount` | `track.play_count` | number | |
| `skipcount` | `track.skip_count` | number | |
| `lastplayed` | `track.last_played_unix` | datetime | |
| `added` | `track.added_unix` | datetime | |
| `modified` | `track.modified_unix` | datetime | |
| `duration` | `track.duration_ms` | duration | |
| `samplerate` | `track.sample_rate` | number | |
| `bitdepth` | `track.bit_depth` | number | `null` for lossy |
| `bitrate` | `track.bitrate_kbps` | number | |
| `codec` | `track.codec` | string | `'mp3'`,`'aac'`,`'alac'`,`'flac'`,`'pcm'`,… |
| `is_lossless` | `track.is_lossless` | bool | bare field allowed as predicate |
| `trackno` | `track.track_no` | number | |
| `discno` | `track.disc_no` | number | |
| `path` | `track.path` | string | |

## Text matching semantics

`=~` matches irrespective of case, half/full width, and hiragana/katakana.
Both sides are normalized with NFKC + katakana→hiragana fold + casefold into
precomputed `*_search` shadow columns; matching runs against those. Never
`LIKE` on the raw column.

## Evaluation and caching

- Compile to a single `SELECT track.id …`. Cache compiled SQL keyed by
  `query_text`.
- Cache resulting ID arrays keyed by `(playlist_id, library_generation)`.
- Any membership-affecting write bumps `library_generation`. Coarse, correct,
  cheap — no fine-grained dependency tracking in v1.
- `ORDER BY random` is materialized once per evaluation, not per read.
- Cyclic `playlist =` references are rejected at save time with a DFS over
  the reference graph.

## Playlist references

`playlist = "name"` reaches both kinds. A static playlist is tested against
its entries; a smart one keeps none, so its own predicate is compiled in
place of the reference. Either way the result is live — editing the
referenced playlist changes what refers to it on the next evaluation.

Two consequences worth stating:

- **A referenced playlist may not carry a `LIMIT`.** A limit picks rows out
  of an ordered result, and a predicate about a single track has no result to
  order; ignoring it would quietly widen the reference to the unlimited set.
  Referencing one is a compile error.
- **Compilation needs the library.** `SqlCompiler.Compile` only sees smart
  playlists when handed a resolver — `PlaylistStore.SmartPlaylistResolver`.
  Without one it falls back to entries alone, which is right for a static
  reference and matches nothing for a smart one.
