# Third-party notices

Mirelia is distributed with the components below. Each is used under its own
license; this file reproduces the notices those licenses require. License
identifiers are the SPDX expressions the projects publish in their own package
metadata (NuGet `.nuspec`, crate `Cargo.toml`).

Mirelia itself is MIT licensed — see [LICENSE](LICENSE).

## Components that carry conditions beyond attribution

**TagLib# (`TagLibSharp` 2.3.0) — LGPL-2.1-only**
<https://github.com/mono/taglib-sharp>

Used to read and write audio file tags. It ships as its own assembly
(`TagLibSharp.dll`) alongside the application rather than being merged into it,
so it can be replaced with a modified build of the same version by overwriting
that file. The library's source is available from the project page above.

**Symphonia 0.6.0 — MPL-2.0**
<https://github.com/pdeljanov/Symphonia>

The decoding stack (`symphonia`, `symphonia-common`, `symphonia-core`,
`symphonia-bundle-flac`, `symphonia-bundle-mp3`, `symphonia-codec-aac`,
`symphonia-codec-alac`, `symphonia-codec-pcm`, `symphonia-format-isomp4`,
`symphonia-format-riff`, `symphonia-metadata`), linked into the native audio
backend. Mirelia uses these crates unmodified; the source for the exact
versions is available from the project page above and from crates.io. The
MPL-2.0 text is at <https://mozilla.org/MPL/2.0/>.

## Managed components

| Component | Version | License |
| --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Controls.DataGrid, and the Avalonia platform assemblies (Win32, X11, Native, Skia, HarfBuzz, FreeDesktop, Remote.Protocol) | 12.1.1 | MIT |
| Avalonia.Angle.Windows.Natives (the ANGLE project) | 2.1.27548.20260419 | BSD-3-Clause |
| MicroCom.Runtime | 0.11.6 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| SkiaSharp and its native assets (the Skia project) | 3.119.4 | MIT |
| HarfBuzzSharp and its native assets (the HarfBuzz project) | 8.3.1.3 | MIT |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.10 | MIT |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3) | 2.1.12 | Apache-2.0 |
| TagLib# | 2.3.0 | LGPL-2.1-only (see above) |

SQLite itself, bundled through `SQLitePCLRaw.lib.e_sqlite3`, is in the public
domain: <https://sqlite.org/copyright.html>.

`Microsoft.Windows.CsWin32` (MIT) generates the WPD interop used by MTP sync at
build time and contributes no runtime component.

## Native components

Linked into `audio_core_rs`, the native audio backend.

| Crate | Version | License |
| --- | --- | --- |
| symphonia and its format/codec crates | 0.6.0 | MPL-2.0 (see above) |
| ebur128 | 0.1.10 | MIT |
| extended | 0.1.0 | MIT |
| bytemuck | 1.25.2 | Zlib OR Apache-2.0 OR MIT |
| dasp_frame, dasp_sample | 0.11.0 | MIT OR Apache-2.0 |
| num-complex | 0.4.6 | MIT OR Apache-2.0 |
| num-traits | 0.2.19 | MIT OR Apache-2.0 |
| bitflags | 1.3.2, 2.13.1 | MIT OR Apache-2.0 |
| smallvec | 1.15.2 | MIT OR Apache-2.0 |
| lazy_static | 1.5.0 | MIT OR Apache-2.0 |
| log | 0.4.33 | MIT OR Apache-2.0 |
| regex-lite | 0.1.9 | MIT OR Apache-2.0 |
| windows and the windows-\* support crates | 0.62.2 and companions | MIT OR Apache-2.0 |
| autocfg | 1.5.1 | Apache-2.0 OR MIT |
| proc-macro2, quote, syn | 1.0.107, 1.0.47, 2.0.119 | MIT OR Apache-2.0 |
| unicode-ident | 1.0.24 | (MIT OR Apache-2.0) AND Unicode-3.0 |

Where a crate offers a choice of licenses, Mirelia takes it under the MIT
option.

## Runtime

The Windows archive build requires the Microsoft Visual C++ redistributable;
the MSIX package declares it as a framework dependency instead. The .NET
runtime and the Windows App SDK components a packaged build depends on are
distributed by Microsoft under their own terms.

## Refreshing this file

The managed list comes from the `libraries` section of the built
`Mirelia.App.deps.json` cross-referenced with each package's `.nuspec`; the
native list comes from `native/audio-core-rs/Cargo.lock` cross-referenced with
each crate's `Cargo.toml`. Re-check both whenever a dependency is added or a
version moves.
