# MusicBee import

Status: source format settled 2026-08-15; nothing built yet.

A one-way migration off MusicBee, not a playlist importer. What has to
survive the move: the library entries themselves, ratings, play counts, date
added, static playlists with their folder hierarchy, and auto-playlist
definitions translated into the query DSL.

## Where the data comes from

MusicBee's own library file, `MusicBeeLibrary.mbl`, is an undocumented binary
that changes shape between versions. It is not the source, because a better
one sits beside it.

### `iTunes Music Library.xml`

MusicBee keeps an iTunes-compatible export next to its library, refreshed
whenever the library changes. It is a plist XML against a published DTD, and
it already carries almost everything on the list:

| Field | Key | Notes |
|---|---|---|
| Path | `Location` | `file://localhost/X:/…`, percent-encoded |
| Title, artist, album artist, album | `Name`, `Artist`, `Album Artist`, `Album` | |
| Genre, year, composer, comments | `Genre`, `Year`, `Composer`, `Comments` | |
| Track / disc | `Track Number`, `Track Count`, `Disc Number`, `Disc Count` | |
| Size, duration, bitrate, sample rate | `Size`, `Total Time`, `Bit Rate`, `Sample Rate` | `Total Time` is milliseconds |
| Date added | `Date Added` | ISO 8601 UTC |
| Play count | `Play Count` | absent when zero |
| Last played | `Play Date UTC` | absent when never |
| Rating | `Rating` | **0–100, the same scale Mirelia stores** — no conversion |

Playlists follow the tracks in the same file: `Playlist Persistent ID`,
`Parent Persistent ID` for nesting, `Folder` marking a folder rather than a
playlist, and an ordered `Playlist Items` array of `Track ID`s. The folder
hierarchy therefore survives without reading anything else.

An auto-playlist appears here too, but only as the tracks it currently
matches. Its rules are not in this file.

The export has to be switched on in MusicBee for the file to exist. It is a
prerequisite of the import, not something the import can produce.

### `<library>/Playlists/**/*.xautopf`

Auto-playlist rules, as plain XML, one file per playlist, laid out in the same
folder tree the playlists themselves use:

```xml
<SmartPlaylist ...>
  <Source Type="1">
    <Conditions CombineMethod="All">
      <Condition Field="FileDateAdded" Comparison="InTheLast" Value="100d" />
    </Conditions>
    <Limit Enabled="False" Count="25" Type="Items" SelectedBy="MostRecentlyAdded" />
  </Source>
</SmartPlaylist>
```

- `Source Type="1"` draws from the whole library. `Source Type="16"` draws
  from another playlist, named by a sibling `<SourcePath Location="…mbp" />`.
- `CombineMethod` is `All` or `Any`.
- A file maps to its entry in the XML by folder path plus name:
  `Playlists\Genres.Rated.xautopf` is the playlist `Genres.Rated` whose
  parent is the folder `Playlists`.

`.mbp`, the static-playlist format, is a readable enough mix of NUL-terminated
header strings and length-prefixed UTF-8 paths, but nothing needs it: the same
playlists are in the XML, in order, already resolved to tracks.

## How tracks arrive

The import owns the whole path (user decision, 2026-08-15). It reads the
distinct roots out of the XML's `Location` values, registers each as a watch
folder and scans it, and only then applies what the XML knows on top:

1. Scan the roots. `LibraryScanner` writes the tags, and never touches the
   user-state columns — which is exactly the block this import owns.
2. Match each XML track to a Mirelia track by `path_lower`. A path with no
   match is counted and listed; it means the file is gone, or the root was
   not scannable.
3. Write `rating`, `play_count`, `last_played_unix` and `added_unix`.
   The import wins on every one of them: this is a migration, and a freshly
   scanned track has nothing worth preserving.
4. Create the playlists.

Ratings need no conversion — MusicBee writes 0–100 into the XML, and that is
what `track.rating` stores.

## Playlists

Folders first, then playlists, so a parent always exists before its children.
Mirelia requires playlist names to be unique across the whole library while
MusicBee only requires them unique within a folder, so a collision takes the
folder path as a prefix: `Filters\Live` next to a top-level `Live`.

Static playlists come from the XML, in order, already resolved to tracks.

### Auto-playlists

Each `.xautopf` is matched to its XML entry by folder path plus name, and its
conditions are translated into DSL text. What translates:

| MusicBee | DSL |
|---|---|
| `Title`, `Artist`, `AlbumArtist`, `Album`, `Composer`, `Genre` | the same-named text fields |
| `Year`, `Rating`, `PlayCount`, `SkipCount`, `TrackNo`, `DiscNo` | the same-named number fields |
| `FileDateAdded`, `FileDateModified`, `LastPlayed` | `added`, `modified`, `lastplayed` |
| `Is`, `IsNot` | `=`, `!=` |
| `Contains`, `DoesNotContain` | `=~`, `!~` |
| `StartsWith`, `EndsWith` | `^=`, `$=` |
| `MatchesRegEx`, `MatchesRegExIgnoreCase` | `~=` |
| `GreaterThan`, `LessThan`, and the `OrEqual` pair | `>`, `<`, `>=`, `<=` |
| `InRange` | `BETWEEN … AND …` |
| `IsNull`, `IsNotNull` | `= null`, `!= null` |
| `InTheLast 30d`, `NotInTheLast 30d` | `>= @now - 30d`, `< @now - 30d` |
| `Playlists Is`/`IsIn` | `(playlist = "A" OR playlist = "B")` |
| `Playlists IsNot`/`IsNotIn` | `(playlist != "A" AND playlist != "B")` |
| `<Source Type="16">` with N `<SourcePath>` | the same membership OR, ANDed onto the rest |

A playlist reference ending in a backslash names a folder, and expands to the
playlists inside it. References carry no extension: `Tmp\Filters\Live.vcl` is
the file `Tmp\Filters\Live.vcl.mbp`.

`<Limit Enabled="True" Count="n">` becomes `LIMIT n`. Everything else on the
element — `SortBy`, `GroupBy`, `DefinedSort`, `Layout` — is presentation, and
is dropped.

### When a rule will not translate

Anything left over becomes a **static playlist holding what the auto-playlist
matched at export time**, which the XML already lists, and a line in the
import report saying why. The cases:

- A condition on a field Mirelia has no column for — comment, publisher,
  grouping, mood, conductor, BPM, and the multi-value `Artist2`/`Genre2`
  family. Adding these means schema columns, which the import is not the
  place to decide.
- A reference to a playlist that carries a limit, which the DSL cannot
  express — see `query-dsl.md`.

An auto-playlist that builds on another comes across intact. Playlists are
created in reference order so that saving one can check what it points at.

Against the library this was developed on, every auto-playlist
translates and none has to be frozen.

## Where the code lives

`Mirelia.Library/Import/`, driven by `Mirelia.Cli` first
(`mirelia-cli import musicbee <library folder>`) and the app afterwards. That
matches how every other library operation is already split.
