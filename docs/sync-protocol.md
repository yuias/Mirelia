# Sync protocol

Status: local folder sync **shipped in M7**; the network transport is
designed but not built. This file records both, plus the decisions frozen at
kickoff so earlier milestones don't preclude them.

## Transports

Two ways to get music onto a device. They share the scope selection, the
delivery policy, and the target bookkeeping tables; only the byte-moving
layer differs.

| Transport | Kind | Destination | Status |
|---|---|---|---|
| Local folder | `folder` | A drive letter or UNC path — mounted SD card, USB stick, network share | M7 |
| Network | `network` | OpenSubsonic HTTP server, reached over WiFi | M8+ |

Writes go through a single `ISyncFileSink` abstraction (`Stat`, `WriteFile`,
`WriteBytes`, `Delete`, `Enumerate`, `FreeSpaceBytes`). Local folder sync
implements it over `System.IO`; MTP lands later behind the same interface
(see below).

## Strategy

The network transport ships an OpenSubsonic-compatible HTTP server
(`Mirelia.Sync`, ASP.NET Core minimal APIs). Mature third-party clients on
both mobile platforms give immediate usability with offline caching solved,
and validate the protocol layer before any first-party mobile code is
written.

It was deferred out of M7 (user decision, 2026-08-13) because the mobile app
specification is not settled, which leaves MTP the higher-value next step:
it serves the same "get music onto the phone" need against a spec that is
already fixed.

First-party mobile apps come after, as a separate stack (Kotlin/Compose,
Swift/SwiftUI). Nothing is shared but the wire protocol.

## Constraints designed around from day one

- **Smart playlists are materialized before transfer.** The desktop evaluates
  the query, snapshots it, and sends a static list plus files. Mobile never
  needs a query engine.
- **iOS cannot be sync-only.** No API writes into the system Music library;
  an iOS companion is necessarily a full player. Either budget for that or
  lean on OpenSubsonic clients and skip a first-party iOS app.

## Delivery policy

Every "what format does this track arrive in" decision lives in one place, so
new formats never touch sync code:

```csharp
DeliveryDecision Decide(TrackLayoutInfo track, DeviceProfile? profile);
// => AsIs | Transcode(codec, bitrate) | Skip
```

The profile is per-target and nullable; null means "send anything", which is
right for a plain card whose player is unknown.

```csharp
DeviceProfile { List<string>? SupportedCodecs; bool SkipLossless; }
```

M7 returns `AsIs` or `Skip` only:

- a codec outside `SupportedCodecs` is skipped;
- lossless is skipped when `SkipLossless` is set.

Both become `Transcode` when the pipeline lands (M8+), and nothing else in
sync changes — the planner already routes on `Kind`. The kickoff's third
input, "the track came from a plugin decoder, so always transcode", has no
schema field yet; it arrives with the plugin decoder work.

A skipped track never reaches the layout, so a target already holding one
treats it as an orphan and removes it. Changing a profile therefore cleans
the device up on the next run rather than leaving unplayable files behind.

## Sync targets

A sync target is a persisted, named profile — not a single set of global
preferences. Users routinely keep more than one (a DAP, a phone's SD card, a
car USB stick) with different scopes and layouts, and the profile is also
where the M8+ transcode settings attach without disturbing anything else.

Storage is `sync_target`, `sync_target_scope` and `sync_target_file`; see
`schema.md` (v4) for the DDL. The per-target configuration is JSON:

| Key | Meaning | Example |
|---|---|---|
| `musicRoot` | Where audio files are written | `X:\Music` |
| `playlistDir` | Where `.m3u8` files are written | `X:\Playlists` |
| `scope` | `allMusic` \| `allPlaylists` \| `selected` | `selected` |
| `pathStyle` | Path form inside playlists | `absolute` |
| `deviceRoot` | Stands in for `musicRoot` when `pathStyle` is `absolute` | `A:\Music` |
| `separator` | Path separator written into playlists | `\` |
| `playlistFormat` | `m3u8` \| `m3u` (with `codepage`) | `m3u8` |
| `playlistFolders` | `flatten` \| `mirror` | `flatten` |
| `layoutTemplate` | Destination path template | see below |
| `orphanPolicy` | `delete` \| `keep` | `delete` |
| `delivery` | Empty in M7; the transcode settings slot | `{}` |

## Local folder sync

### Scope

Three choices, shared with the network transport:

- **All music** — every track in the library.
- **All playlists** — the union of every playlist's tracks.
- **Selected playlists** — the union of the playlists listed in
  `sync_target_scope`.

Smart playlists are materialized at export time, as already frozen above.
Playlist folders (`playlist.kind = 'folder'`) contribute their descendants,
not an entry of their own.

### Destination layout

Audio files are written under `musicRoot` at a path derived from a template.
The default:

```
{AlbumArtist}/{Album}/{Disc?}{Track:00} {Title}.{ext}
```

- `{Disc?}` expands to `<n>-` only when the album spans more than one disc,
  so single-disc albums stay clean.
- `{AlbumArtist}` falls back to `{Artist}`, then to `Unknown Artist`.
- Compilations resolve `{AlbumArtist}` to `Various Artists`.

Shipped presets:

| Preset | Template |
|---|---|
| Artist / Album (default) | `{AlbumArtist}/{Album}/{Disc?}{Track:00} {Title}` |
| Flat album | `{AlbumArtist} - {Album}/{Disc?}{Track:00} {Title}` |
| Mirror source | the track's path relative to its watch folder, verbatim |

Opaque destination names (a UUID or ID per album directory) were considered
and rejected. The manageability they buy is already covered by
`sync_target_file`, which maps `track_id` to its destination path exactly —
deletion, rename and orphan detection never re-derive a name. What they cost
is real: folder-browsing DAPs become unusable, the card is unreadable when
plugged into a PC, and the `folder.jpg` album-art convention breaks.

### Name sanitization

Applied per path component, targeting exFAT/FAT32:

1. Replace `\ / : * ? " < > |` and control characters with `_`. An option
   substitutes full-width look-alikes (`：`, `？`, …) instead.
2. Strip trailing dots and spaces.
3. Suffix `_` onto Windows reserved names (`CON`, `PRN`, `AUX`, `NUL`,
   `COM1`–`COM9`, `LPT1`–`LPT9`).
4. Truncate to 60 characters; when truncation occurred, append
   ` ~<4-char hash>` so distinct sources stay distinct.
5. Enforce the total length limit against the **device-side** path (the
   `deviceRoot` form), not the Windows-side one — a path that fits under
   `X:\Music` can still overflow on the device.

### Collision resolution

When two tracks sanitize to the same relative path, the later one gets
` [<short-id>]` appended to its album component, where `short-id` is the
album ID in base32, six characters. This happens only on an actual
collision, and the resolved path is recorded in `sync_target_file`, so it
stays stable across subsequent syncs.

### Playlist export

One file per playlist under `playlistDir`.

- **Encoding.** `.m3u8` is UTF-8 **without BOM**, CRLF line endings; more
  players break on a BOM than require one. `.m3u` writes a user-chosen
  codepage for players that need it, dropping unrepresentable entries with a
  warning.
- **Headers.** `#EXTM3U` plus `#EXTINF:<seconds>,<artist> - <title>` per
  entry. Players that don't understand them ignore them.
- **Relative paths** are relative to the playlist file's own location. When
  `playlistDir` is not an ancestor of `musicRoot` this produces leading
  `..\` segments, which a fair number of DAPs cannot resolve — the UI warns
  on that combination rather than silently emitting them.
- **Absolute paths** are formed by replacing the `musicRoot` prefix with
  `deviceRoot`. This exists because a card mounted at `X:\` on Windows is
  often `A:\` to the device itself.
- **Separator** is written verbatim from `separator`; its initial value is
  inferred from the shape of `deviceRoot`.
- **Nested playlists** are flattened by default, joining ancestor names with
  ` - ` (`Anime - 2020s - Favorites.m3u8`). Mirroring the folder hierarchy
  is optional and off by default, because many devices scan only the top
  playlist directory.

Writing reuses `Mirelia.Library.Export.M3u8Exporter` from M4 rather than
adding a second writer. It already emits `#EXTM3U`/`#EXTINF` as UTF-8
without BOM, and `M3u8Options.PathRewrite` is exactly the `musicRoot` →
`deviceRoot` mapping. Three additions are needed: the separator option, the
`.m3u` codepage variant, and CRLF line endings (it currently writes LF).

### Forcing a re-copy

The delta believes size and modification time. That is wrong for one real
workflow: tag editors that rewrite a file in place and restore its original
timestamp, which some people deliberately prefer. The result is identical to
the delta and different on disk, so the device keeps stale tags forever.

`SyncPlanOptions` covers it, as a one-shot instruction rather than target
state — it describes an action the user is taking now, not how the target
behaves:

```csharp
SyncPlanOptions { bool ForceAll; IReadOnlyCollection<long>? ForcePlaylistIds; }
```

Forced tracks copy even when the delta calls them current, reported as
`CopyReason.Forced`. A real reason still wins: a track that is genuinely
missing on the target reports `MissingOnTarget`, not `Forced`, so the
preflight summary stays honest about why work is happening. Folders in
`ForcePlaylistIds` expand to their descendants, as everywhere else. The UI
clears the selection after a successful run.

### Sync algorithm

1. Resolve the scope into a desired set of tracks, materializing smart
   playlists.
2. Filter through the delivery policy, then diff against `sync_target_file`
   for this target.
3. Copy additions and changes. A track is unchanged when size matches and
   the stored `content_hash` matches; `mtime` is only a cheap pre-filter and
   is compared with a 2-second tolerance, since FAT timestamps are stored at
   that granularity.
4. Write playlist files.
5. Delete orphans when `orphanPolicy` is `delete`.
6. Reconcile against the real filesystem, to catch files the user deleted on
   the device by hand.

Only step 3 changes when the transcode pipeline lands.

Orphan removal covers only files recorded in `sync_target_file`. The music
root can hold data this app never wrote, and no scope change is worth
deleting a stranger's files; step 6 exists to re-copy what the user deleted
on the device, not to delete what it does not recognize.

`Sync now` is always preceded by a preflight summary — files to copy and
their total size, files to delete, playlists to write, and free space at the
destination. Deletion is in play, so a dry run is not optional.

### Deferred to M8+

Android devices connected over **MTP** appear in Explorer as a shell
namespace, not a drive letter, and `System.IO` cannot reach them; they need
the WPD `IPortableDevice` COM API. M7 therefore accepts drive letters and
UNC paths only. MTP arrives as a second `ISyncFileSink` implementation.
Note that MTP also cannot reliably preserve modification times, so it will
have to lean entirely on size and `content_hash` for its delta.

## Network transport: discovery and delta

- Discovery: mDNS (`mirelia._tcp`), plus manual host entry.
- Pairing: short code → long-lived token.
- Delta: monotonic change journal (`change_log(seq, entity, id, op)`) plus
  `content_hash` for conflict detection. Client sends its last seen `seq`.
