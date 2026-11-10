# MSIX packaging

How Mirelia is packaged as an MSIX for the Microsoft Store, and why the package
declares the `runFullTrust` restricted capability.

MSIX packaging is Windows only. The macOS build is covered by
`scripts/package.cs`, which produces a plain archive.

## Build pipeline

```sh
dotnet run scripts/package-msix.cs -- \
  --package-name           <Identity Name from Partner Center> \
  --publisher              "CN=<publisher GUID from Partner Center>" \
  --publisher-display-name "<PublisherDisplayName from Partner Center>"
```

This builds the Rust backend, publishes the app and the CLI, generates the
visual assets, expands the manifest template and calls `makeappx pack`. The
result is `artifacts/Mirelia-<version>.msix`.

| Flag | Effect |
|---|---|
| `--output <dir>` | Write somewhere other than `artifacts/` |
| `--version <x.y.z>` | Override the version from `Directory.Build.props` |
| `--skip-native` | Reuse the existing `cargo build --release` output and the built r8brain plugin |
| `--allow-placeholder-identity` | Build with the placeholder identity, for local testing |

The three identity flags are mandatory. Their defaults are obvious placeholders
and the script refuses to pack with them unless
`--allow-placeholder-identity` is passed, which also marks the file name, so a
placeholder identity cannot reach a submission by accident.

Requirements beyond the ordinary build: the Windows 10/11 SDK, for
`makeappx.exe`. The script scans `Windows Kits\10\bin` and takes the newest
version it finds. Without `--skip-native` the script also builds the r8brain
resampler plugin, which needs clang, CMake and Ninja in the developer
environment described under "Building C and C++ on Windows" in `AGENTS.md`, and
network access to fetch the r8brain source at configure time.

The manifest template is `packaging/msix/AppxManifest.xml` and uses
`{{PLACEHOLDER}}` tokens substituted at pack time. Visual assets are generated
during the pack from `resources/icons/Mirelia-icon-master-1254.png` with
SkiaSharp; the source icon is square, so the wide tile and the splash screen get
it centred on transparency.

### Why this publish is self-contained

`scripts/package.cs` publishes framework-dependent, because someone downloading
an archive can install the .NET runtime themselves. A packaged app cannot: the
Store has framework packages for the C++ runtime and the Windows App SDK, but
none that carries the .NET runtime. So the MSIX publish passes
`--self-contained true` and the runtime travels inside the package, which is
most of its 56 MB. ReadyToRun is on for both, and trimming is off for both, for
the reasons in `scripts/package.cs`.

### The Visual C++ runtime dependency

`audio_core_rs.dll` imports `VCRUNTIME140.dll`, and a packaged full-trust app
cannot use the machine-wide Visual C++ redistributable. The manifest therefore
declares a dependency on the `Microsoft.VCLibs.140.00.UWPDesktop` framework
package, which the Store installs before first launch. `MinVersion` tracks the
`FrameworkIdentity-Retail` MinVersion in the Windows SDK's
`Microsoft.VCLibs.Desktop` manifest — bump it when the MSVC toolchain moves.

Sideloading does not install framework dependencies. Install
`Microsoft.VCLibs.x64.14.00.Desktop.appx` from the SDK first when testing that
way.

## Why Mirelia declares `runFullTrust`

Mirelia is a classic Win32 desktop application. Its core functionality cannot be
implemented inside the UWP application sandbox.

1. **Native audio backend loaded by the managed process.** Playback runs in a
   Rust library reached over a C ABI (`docs/audio-abi.md`). The managed side
   resolves and loads it with `NativeLibrary`, including from a path given by an
   environment variable during development, which is not permitted inside the
   sandboxed process model.

2. **WASAPI device handling.** The backend enumerates endpoints, opens streams,
   and subscribes to device-change and device-lost notifications so playback
   survives the user switching outputs. This is a desktop audio session, not the
   UWP audio graph.

3. **Arbitrary file-system read access.** A music library points wherever the
   user keeps their music: any drive, any folder, network shares included.
   `broadFileSystemAccess` is insufficient, because scanning happens outside any
   picker interaction and covers paths the known-folder model does not describe.

4. **Windows Portable Devices COM for sync.** Copying playlists to a phone drives
   the WPD COM interfaces directly (`docs/sync-protocol.md`), enumerating
   devices, walking their object trees and writing files.

5. **SQLite through a native provider.** The library database is `e_sqlite3`
   loaded into the process, reading and writing a file the app owns.

### How the capability is used

- Reading the audio files and folders the user adds to the library, and the
  artwork beside them.
- Writing the library database, settings and cover-art cache under the user's
  application data directory.
- Writing exported playlists and synced audio files to the folder or device the
  user chose as a sync target.
- Loading `audio_core_rs.dll` and `e_sqlite3.dll` from the package.

### What the capability is not used for

Mirelia performs no elevated or system-wide actions. Specifically, it does not:

- collect, store, or transmit personal information;
- send telemetry, analytics, or crash reports anywhere;
- open outbound network connections of any kind;
- read, write, or enumerate data belonging to other applications;
- modify system-wide state, the registry, or protected directories;
- run background tasks or persist processes after the window closes.

Everything happens locally, scoped to the folders and devices the user picks.

## The CLI inside the package

The package carries `mirelia-cli.exe` alongside the app, and the manifest
declares an app execution alias of the same name. An installed package keeps its
files under a directory users cannot browse into, so without the alias the CLI
would ship unreachable; with it, `mirelia-cli` works from any shell once the
package is installed.

```sh
mirelia-cli --help
mirelia-cli import musicbee "<path to the MusicBee library folder>"
```

The CLI shares the app's library, subject to the redirection below. The import
and its flags are documented in
[docs/musicbee-import.md](musicbee-import.md).

## Where the library lives under a packaged install

The app puts its library at `%APPDATA%/Mirelia/library.db`, and a packaged app
does not write there the way an unpackaged one does. On Windows 10 version 1903
and later, a *new* file under `AppData` is written to a per-user, per-package
private location and merged into the visible `AppData` at runtime, while a file
that *already* exists in the real `AppData` is opened from there and edited in
place, with no redirection.

So someone moving from the archive build to the Store build keeps the library
they had: it exists, so the packaged app opens the real file. A library the
packaged app creates from nothing lives in the package's private storage
instead, and uninstalling the package deletes it. Anyone who wants the database
somewhere predictable should set `MIRELIA_DB` or use File ▸ Open Library…,
which take an explicit path in both builds.

## Signing

The produced `.msix` is intentionally left **unsigned**. The Store re-signs
submissions with its own certificate, so local signing buys nothing for that
path.

To install one locally for testing, sign it with a certificate whose subject
matches the manifest `Publisher` exactly, and place that certificate in the
machine's Trusted People store:

```powershell
signtool sign /fd SHA256 /a /f <certificate.pfx> /p <password> <package.msix>
Add-AppxPackage <package.msix>
```

## Store submission notes

- **Publisher identity** and **Package Identity Name** are assigned by Partner
  Center when the app name is reserved. They are script parameters with no real
  defaults; take the values from the app's identity page.
- **Version.** The Store reserves the fourth part, so it has to be zero. The
  script appends `.0` to the three-part version in `Directory.Build.props`.
- **Privacy policy URL** is mandatory for desktop products under Store policy
  10.5.1 whether or not the app collects anything. Mirelia collects nothing, and
  the policy still has to exist and be linked from the submission.
- **Third-party notices.** The package ships Avalonia, SkiaSharp, HarfBuzz,
  TagLib#, SQLite and the Rust crates the backend links. Their licences travel
  with the submission's own notices, not with the manifest.
