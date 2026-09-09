# Mirelia

A desktop music library and player.

Tracks live in a SQLite library with a text query DSL driving smart playlists,
and sync copies playlists to a folder or to a phone over MTP. The app, library
and query engine are C# 14 on .NET 10 with Avalonia; the audio engine is Rust
behind a C ABI.

## Platform support

Windows is the supported platform. The solution builds and the app runs on
macOS, but the audio backend there is a stub — devices do not enumerate and
nothing plays. CoreAudio output is still to be written. MTP sync is Windows-only
as well, since it drives the Windows Portable Devices interfaces directly.

## Install

There is no published release yet. Both packaging routes below build from a
checkout: the archive runs where you extract it, and the MSIX needs either the
Store or a signature the machine trusts.

## Requirements

- .NET SDK 10
- Rust (stable)
- CMake — only to run the ABI conformance suite
- Windows 10/11 SDK — only to build an MSIX

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

One-way migration off MusicBee: tracks, ratings, play counts, dates, static
playlists with their folder tree, and auto-playlists translated into the query
DSL. It is CLI only — the app has no import command.

The import reads MusicBee's `iTunes Music Library.xml` export, so **that export
has to be enabled in MusicBee** before it can run, and it takes auto-playlist
rules from the `.xautopf` files beside it.

```sh
dotnet run --project src/Mirelia.Cli -- import musicbee "<MusicBee library folder>"

# from an installed package, where the CLI is on PATH under its alias
mirelia-cli import musicbee "<MusicBee library folder>"
```

Flags, what happens on a re-run, and which rules the DSL cannot express are in
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

An archive for the host platform — app, CLI and native backend,
framework-dependent, so the target machine needs the .NET runtime:

```sh
dotnet run scripts/package.cs
```

Writes `artifacts/Mirelia-<version>-<githash>.zip`. On Windows the archive also
needs the Visual C++ 2015-2022 redistributable, because the Rust audio backend
links `VCRUNTIME140.dll` dynamically. The MSIX covers this with a framework
package instead.

An MSIX for the Microsoft Store, self-contained and unsigned, Windows only:

```sh
dotnet run scripts/package-msix.cs -- \
  --package-name <name> --publisher <dn> --publisher-display-name <name>
```

Writes `artifacts/Mirelia-<version>.msix`. The identity values come from Partner
Center; see [docs/msix-packaging.md](docs/msix-packaging.md) for those, for the
`runFullTrust` justification, and for how a packaged install treats the library
file.

## Docs

| | |
|---|---|
| [docs/query-dsl.md](docs/query-dsl.md) | Smart-playlist query language |
| [docs/schema.md](docs/schema.md) | Library database schema |
| [docs/audio-abi.md](docs/audio-abi.md) | Managed ↔ native audio ABI |
| [docs/sync-protocol.md](docs/sync-protocol.md) | Device sync |
| [docs/musicbee-import.md](docs/musicbee-import.md) | MusicBee migration |
| [docs/msix-packaging.md](docs/msix-packaging.md) | Microsoft Store packaging |
| [docs/adr/](docs/adr/) | Architecture decisions |

## License

MIT. See [LICENSE](LICENSE).
