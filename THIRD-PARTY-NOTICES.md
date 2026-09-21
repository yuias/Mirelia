# Third-party notices

Mirelia is distributed with the components below, each under its own license.
License identifiers come from the projects' own package metadata (NuGet
`.nuspec`, crate `Cargo.toml`), except where a package bundles native code
under a different license than its wrapper — those are taken from the license
text the package itself carries.

The full license texts are in [licenses/](licenses/), which both packaging
routes include in what they produce.

Mirelia itself is MIT licensed — see [LICENSE](LICENSE).

## Components that carry conditions beyond attribution

**TagLib# (`TagLibSharp` 2.3.0) — LGPL-2.1-only**
<https://github.com/mono/taglib-sharp> · [licenses/LGPL-2.1.txt](licenses/LGPL-2.1.txt)

Used to read and write audio file tags. It ships as its own assembly
(`TagLibSharp.dll`) alongside the application rather than being merged into it,
so it can be replaced with a modified build of the same version by overwriting
that file. The library's source is available from the project page above.

**Symphonia 0.6.0 — MPL-2.0**
<https://github.com/pdeljanov/Symphonia> · [licenses/MPL-2.0.txt](licenses/MPL-2.0.txt)

The decoding stack (`symphonia`, `symphonia-common`, `symphonia-core`,
`symphonia-bundle-flac`, `symphonia-bundle-mp3`, `symphonia-codec-aac`,
`symphonia-codec-alac`, `symphonia-codec-pcm`, `symphonia-format-isomp4`,
`symphonia-format-riff`, `symphonia-metadata`), linked into the native audio
backend. Mirelia uses these crates unmodified; the source for the exact
versions is available from the project page above and from crates.io.

## Managed components

| Component | Version | License | Text |
| --- | --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Controls.DataGrid, and the Avalonia platform assemblies (Win32, X11, Native, Skia, HarfBuzz, FreeDesktop, Remote.Protocol) | 12.1.1 | MIT | [MIT.txt](licenses/MIT.txt) |
| Avalonia.Angle.Windows.Natives (the ANGLE project) | 2.1.27548.20260419 | BSD-3-Clause | [ANGLE.txt](licenses/ANGLE.txt) |
| MicroCom.Runtime | 0.11.6 | MIT | [MIT.txt](licenses/MIT.txt) |
| Tmds.DBus.Protocol | 0.94.1 | MIT | [MIT.txt](licenses/MIT.txt) |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | [CommunityToolkit.Mvvm.txt](licenses/CommunityToolkit.Mvvm.txt) |
| SkiaSharp (the managed wrapper) | 3.119.4 | MIT | [MIT.txt](licenses/MIT.txt) |
| HarfBuzzSharp (the managed wrapper) | 8.3.1.3 | MIT | [HarfBuzzSharp.txt](licenses/HarfBuzzSharp.txt) |
| Microsoft.Data.Sqlite, Microsoft.Data.Sqlite.Core | 10.0.10 | MIT | [MIT.txt](licenses/MIT.txt) |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3) | 2.1.12 | Apache-2.0 | [Apache-2.0.txt](licenses/Apache-2.0.txt) |
| TagLib# | 2.3.0 | LGPL-2.1-only (see above) | [LGPL-2.1.txt](licenses/LGPL-2.1.txt) |

The `SkiaSharp.NativeAssets.*` and `HarfBuzzSharp.NativeAssets.*` packages
carry the native `libSkiaSharp` and `libHarfBuzzSharp` libraries, which are not
covered by the wrappers' MIT license. Skia itself is BSD-3-Clause (Google);
HarfBuzz is "Old MIT"; both bundle further components of their own. The
complete notice Microsoft ships with those packages is reproduced verbatim in
[licenses/SkiaSharp-native-third-party.txt](licenses/SkiaSharp-native-third-party.txt),
which also covers ANGLE, FreeType, ICU, libpng, libjpeg-turbo, libwebp, expat
and the rest of Skia's dependency set.

SQLite itself, bundled through `SQLitePCLRaw.lib.e_sqlite3`, is in the public
domain: <https://sqlite.org/copyright.html>.

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
| windows, windows-core, windows-collections, windows-future, windows-link, windows-numerics, windows-result, windows-strings, windows-threading | 0.62.2 and companions | MIT OR Apache-2.0 |

Where a crate offers a choice of licenses, Mirelia takes it under the MIT
option; the Zlib text is included for bytemuck's benefit at
[licenses/Zlib.txt](licenses/Zlib.txt).

`autocfg`, `proc-macro2`, `quote`, `syn`, `unicode-ident`, `windows-implement`
and `windows-interface` (all MIT OR Apache-2.0, `unicode-ident` additionally
Unicode-3.0) run at build time only and contribute no code to the shipped
library. `Microsoft.Windows.CsWin32` (MIT) is the same case on the managed
side: it generates the WPD interop used by MTP sync and ships nothing.

## Runtime

The Windows archive build is framework-dependent: the target machine needs the
.NET 10 runtime and the Microsoft Visual C++ 2015-2022 redistributable, the
latter because the Rust audio backend imports `VCRUNTIME140.dll`.

The MSIX is self-contained, so it carries the .NET runtime itself, and it
declares the Visual C++ runtime as a framework dependency
(`Microsoft.VCLibs.140.00.UWPDesktop`, published by Microsoft) which the Store
installs before first launch. Both are distributed by Microsoft under their own
terms.

## Refreshing this file

The managed list comes from the `libraries` section of the built
`Mirelia.App.deps.json` cross-referenced with each package's `.nuspec` and with
any license or notice file the package itself carries; the native list comes
from `native/audio-core-rs/Cargo.lock` cross-referenced with each crate's
`Cargo.toml`. Re-check both, and the texts under `licenses/`, whenever a
dependency is added or a version moves.
