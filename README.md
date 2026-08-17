# Mirelia

A desktop music library and player for Windows and macOS.

The app, library and query engine are C# 14 on .NET 10 with Avalonia; the
audio engine is Rust behind a C ABI. Tracks live in a SQLite library with a
text query DSL driving smart playlists, and sync copies playlists to a folder
or an MTP device.

## Requirements

- .NET SDK 10
- Rust (stable)
- CMake — only to run the ABI conformance suite

## Build

The native backend first, then the solution:

```sh
cargo build --release --manifest-path native/audio-core-rs/Cargo.toml
dotnet build Mirelia.slnx
```

`dotnet run` needs no copy step: the bridge probes
`native/audio-core-rs/target/{debug,release}` when the library is not next to
the executable. `MIRELIA_AUDIO_CORE` overrides the path outright.

## Run

```sh
dotnet run --project src/Mirelia.App
```

The library is `%APPDATA%/Mirelia/library.db` (`MIRELIA_DB` overrides it,
File ▸ Open Library… switches it). Point it at your music with
File ▸ Add Folder….

There is also a headless CLI covering scanning, queries, playlists, export and
ReplayGain:

```sh
dotnet run --project src/Mirelia.Cli -- --help
```

## Importing from MusicBee

One-way migration off MusicBee — tracks, ratings, play counts, dates, static
playlists with their folder tree, and auto-playlists translated into the query
DSL. There is no UI for this; it is CLI only.

It reads MusicBee's `iTunes Music Library.xml` export, so **that export has to
be enabled in MusicBee** before the import can run. Auto-playlist rules come
from the `.xautopf` files beside it.

```sh
dotnet run --project src/Mirelia.Cli -- import musicbee "C:\Users\me\Music\MusicBee"
```

The import owns the whole path: it scans the roots it finds in the export,
then writes the export's user state over them. Options:

| Flag | Effect |
|---|---|
| `--no-scan` | Skip the scan; match against tracks already in the library |
| `--no-playlists` | Tracks and their user state only |
| `--merge` | Replace same-named playlists instead of importing a second copy |

Re-running without `--merge` duplicates playlists (`Genres (2)`); tracks are
matched by path and updated in place either way. Rules the DSL cannot express
become static playlists holding what the auto-playlist matched at export time,
each with a line in the report. Details in
[docs/musicbee-import.md](docs/musicbee-import.md).

## Tests

```sh
cargo clippy --manifest-path native/audio-core-rs/Cargo.toml --all-targets -- -D warnings
cargo test --manifest-path native/audio-core-rs/Cargo.toml
dotnet build Mirelia.slnx -warnaserror
dotnet test Mirelia.slnx --no-build
```

If you touched the FFI, also run the ABI conformance suite:

```sh
cmake -S native/tests/abi-conformance -B build/conformance
cmake --build build/conformance --config Release
build/conformance/Release/abi-conformance.exe \
  native/audio-core-rs/target/release/audio_core_rs.dll tests/fixtures
```

## Packaging

```sh
dotnet run scripts/package.cs
```

Writes `artifacts/Mirelia-<version>-<githash>.zip` for the host platform —
app, CLI and native backend, framework-dependent.

## Docs

| | |
|---|---|
| [docs/query-dsl.md](docs/query-dsl.md) | Smart-playlist query language |
| [docs/schema.md](docs/schema.md) | Library database schema |
| [docs/audio-abi.md](docs/audio-abi.md) | Managed ↔ native audio ABI |
| [docs/sync-protocol.md](docs/sync-protocol.md) | Device sync |
| [docs/musicbee-import.md](docs/musicbee-import.md) | MusicBee migration |
| [docs/adr/](docs/adr/) | Architecture decisions |
