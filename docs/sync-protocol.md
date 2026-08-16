# Sync protocol

Status: local folder sync **shipped in M7**; MTP is **built in M8** and has now
been run against a physical device, though the checklist below is not finished;
the network transport is designed and not built.
This file records all three, plus the decisions frozen at kickoff so earlier
milestones don't preclude them.

## Transports

Two ways to get music onto a device. They share the scope selection, the
delivery policy, and the target bookkeeping tables; only the byte-moving
layer differs.

| Transport | Kind | Destination | Status |
|---|---|---|---|
| Local folder | `folder` | A drive letter or UNC path — mounted SD card, USB stick, network share | M7 |
| MTP | `mtp` | An Android phone or DAP connected over USB, reached through WPD | M8, partly verified |
| Network | `network` | OpenSubsonic HTTP server, reached over WiFi | M8+ |

Writes go through a single `ISyncFileSink` abstraction (`Stat`, `WriteFile`,
`WriteBytes`, `Delete`, `Enumerate`, `FreeSpaceBytes`). Local folder sync
implements it over `System.IO`; MTP is a second implementation over the WPD
COM API (see below).

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

The transcode pipeline; see the delivery policy above. Everything else in
local folder sync shipped in M7.

## MTP transport

Android devices connected over USB appear in Explorer as a shell namespace,
not a drive letter, and `System.IO` cannot reach them at all. They are
reachable only through Windows Portable Devices — the `IPortableDevice` COM
API. `MtpSink` is therefore a second `ISyncFileSink`, and nothing above the
sink changes: scope selection, layout, delivery policy, playlist export and
orphan handling are already transport-agnostic.

### Interop

WPD is generated by [CsWin32] from the Win32 metadata
(`Windows.Win32.Devices.PortableDevices`), rather than hand-written COM
declarations or a third-party wrapper. The generated surface covers every
interface the sink needs: `IPortableDeviceManager`, `IPortableDevice`,
`IPortableDeviceContent`, `IPortableDeviceProperties`,
`IPortableDeviceValues`, `IPortableDeviceDataStream` and
`IEnumPortableDeviceObjectIDs`.

[CsWin32]: https://github.com/microsoft/CsWin32

WPD lives in its own `Mirelia.Sync.Mtp` project, referenced by `Mirelia.App`
and `Mirelia.Cli`. `Mirelia.Library` keeps owning `ISyncFileSink` and never
sees a COM type. The split is for dependency hygiene — CsWin32, `unsafe`, and
a Windows-only surface do not belong in the core library — not for the target
framework: the project stays on plain `net10.0` like everything else.

That works because the WPD interfaces themselves carry no platform
annotation; only the `IStream` the write path goes through is marked
`[SupportedOSPlatform("windows5.0")]`. One matching attribute on the type
that touches it satisfies CA1416 under `-warnaserror`, so neither
`Mirelia.App` nor `Mirelia.Cli` needs a `net10.0-windows` TFM. Callers guard
with `OperatingSystem.IsWindows()`.

CsWin32 generates `internal` types, so no WPD type can appear in a public
signature even by accident. The sink's own seam (`IMtpObjectStore`) is what
the rest of the codebase sees.

COM apartment: the sink calls `CoInitializeEx` in `MTA` on the thread that
opens the device, and every call on that device happens on that thread. Sync
already runs on a dedicated task (`LibraryService.RunSyncAsync`), so this
costs nothing.

### Addressing

WPD has no paths. Every object is identified by an opaque object ID, and a
"path" is a chain of `WPD_OBJECT_PARENT_ID` links whose components are
`WPD_OBJECT_ORIGINAL_FILE_NAME`. `ISyncFileSink` speaks relative paths, so
the sink owns the translation.

A `MtpDirectory` cache maps a relative directory path to its object ID, and
holds that directory's children as a name → (object ID, size) dictionary.
It is filled lazily by `EnumObjects` on first touch, and updated in place on
every create and delete. This matters more than it looks: a directory listing
over MTP costs a round trip per object, and re-walking the tree for every one
of several thousand files is the difference between a sync that finishes and
one that does not.

Name matching is case-insensitive, because that is what the sanitizer and the
`sync_target_file` records already assume.

Config addresses a device by its WPD device ID, which is a Plug-and-Play
name and is stable across reconnects for the same physical device:

| Key | Meaning | Example |
|---|---|---|
| `deviceId` | WPD Plug-and-Play device ID | `\\?\usb#vid_18d1&pid_4ee1#...` |
| `deviceLabel` | Friendly name, cached for display when the device is absent | `Pixel 8` |
| `musicRoot` | Path inside the device, storage name first | `Internal shared storage/Music` |

`deviceLabel` exists so the target list can render "Pixel 8 (not connected)"
instead of a raw PnP string.

Storage is addressed as the first path component rather than by a separate
key. A phone with a card exposes two storage objects as siblings under the
device root, so `Internal shared storage/Music` and `SD card/Music` are
already unambiguous, and the path a user reads in Explorer is the path they
type here.

### Delta

MTP cannot preserve modification times: the device stamps its own, and a
sizeable minority of drivers do not report `WPD_OBJECT_DATE_MODIFIED` at all.

This costs less than it appears. `SyncPlanner.SourceChanged` already compares
the **library-side** track against the `sync_target_file` record — size, then
`content_hash`, with `mtime` only as the fallback when no hash is stored. The
device is never consulted for change detection. The sink's `Stat` is used for
exactly one question, "is the file still there" (`CopyReason.MissingOnTarget`),
and the cached directory listing answers it without a round trip.

So `MtpSink.Stat` returns the real size and `MTimeUnix = 0`, and the delta
is correct as long as `content_hash` is populated. Tracks whose hash is still
null fall back to mtime comparison against the record written at copy time,
which is the source's mtime, not the device's — also correct.

### Failure translation

`SyncRunner` catches `IOException` and `UnauthorizedAccessException` per file
and continues; anything else aborts the whole run. WPD reports failures as
`COMException`, which would turn one unwritable file into a dead sync. The
sink translates: every WPD call is wrapped, and a failed `HRESULT` becomes an
`IOException` carrying the object path and the HRESULT. Only device-level
failures — the device disconnected mid-run — are allowed to propagate, as
`IOException` is wrong for those and retrying the remaining files is pointless.

### Free space

There is no device-wide free space property; `WPD_STORAGE_FREE_SPACE_IN_BYTES`
lives on the storage object. `FreeSpaceBytes` walks to the storage object that
is the first component of `musicRoot` and reads it there. When the device does
not report it, the property returns null and the preflight summary omits the
line — which `ISyncFileSink` already allows for UNC shares.

### Writing

`CreateObjectWithPropertiesAndData` returns a stream; the object does not
exist until `Commit`. The sink writes in `pdwOptimalWriteBufferSize`-sized
chunks (the driver's own preference — ignoring it costs real throughput) and
checks the cancellation token between chunks. On cancellation it calls
`Revert`, so an aborted sync leaves no half-written file for the next run to
mistake for a complete one.

Overwrite is delete-then-create: MTP has no truncate, and creating a second
object with the same name yields two objects with the same name on most
drivers.

Directory creation is `CreateObjectWithPropertiesOnly` with
`WPD_OBJECT_CONTENT_TYPE = WPD_CONTENT_TYPE_FOLDER`, walking down from the
nearest cached ancestor.

### One session, two roots

`musicRoot` and `playlistDir` are two `ISyncFileSink` instances. On MTP they
must share one `IPortableDevice` session — opening the same device twice is
allowed but doubles the enumeration cost and lets the two caches disagree.
`ISyncSinkFactory` owns the session and hands out rooted views;
`FileSystemSinkFactory` gets the same treatment so the call sites stay uniform.

### Scope of the sink

`Enumerate` is used by the reconcile step and by nothing else. It walks the
cached tree, filling it depth-first, and is the one genuinely expensive
operation — proportional to the whole music root, not to the delta. It stays,
because step 6 of the sync algorithm is what catches files deleted on the
phone by hand, which is a real workflow on a device the user browses.

### What is not attempted

- **Transferring metadata.** WPD can set `WPD_MEDIA_ARTIST` and friends on
  the created object. Mirelia does not: the tags travel inside the file, and
  a device that reads one and not the other is not worth the divergence.
- **Automated tests against a real device.** There is no way to fake a phone
  in CI. The path→object-ID translation, the cache, and the overwrite
  sequence are tested against an in-memory fake WPD content layer
  (`Mirelia.Sync.Mtp.Tests`); the WPD layer beneath is verified by the manual
  checklist below.

### Manual checklist

Items 1–4, 6 and 9 were run on 2026-08-17 against an Android phone over USB,
driving `WpdObjectStore` and `MtpSink` from a throwaway harness rather than
through the UI. The overwrite and the directory pruning were checked by
re-reading the tree from the device rather than from the sink's own cache,
since a stale cache is exactly what would hide a duplicate object.

Two things only a real driver could settle came out right: the device does
report `WPD_STORAGE_FREE_SPACE_IN_BYTES`, and its storage arrives as a
functional object rather than a folder — which `Describe` already counts as a
folder, so the root resolves.

Items 5, 7, 8 and 10, and the device dropdown in the target editor, have not
been run.

1. **Device list.** Plug in an unlocked Android phone set to file transfer.
   The target editor's device dropdown names it.
2. **First copy.** Point a target at `Internal shared storage/Music`, sync,
   and check the files and folders appear with the expected names.
3. **Re-run.** Sync again with nothing changed: zero copies, zero deletes.
4. **Overwrite.** Re-tag a track, sync, and confirm one object with the new
   bytes — not two objects with the same name.
5. **Delete on the device.** Remove a file with Explorer, sync, and confirm it
   comes back.
6. **Orphan removal.** Narrow the scope, sync, and confirm the files leave and
   the directories they emptied go with them.
7. **Cancel mid-copy.** Stop during a large file; confirm no partial object is
   left, and that the next run copies it.
8. **Disconnect mid-run.** Unplug during a sync: the run stops with a
   disconnect message rather than churning through every remaining file.
9. **Free space.** The preflight summary reports the phone's free space, or
   omits the line if the driver does not supply it.
10. **Reconnect.** Unplug, replug, sync again: the target still resolves the
    same device by its Plug-and-Play id.

## Network transport: discovery and delta

- Discovery: mDNS (`mirelia._tcp`), plus manual host entry.
- Pairing: short code → long-lived token.
- Delta: monotonic change journal (`change_log(seq, entity, id, op)`) plus
  `content_hash` for conflict detection. Client sends its last seen `seq`.
