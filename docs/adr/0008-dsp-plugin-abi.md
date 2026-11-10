# ADR 0008: A DSP plugin ABI with sample-rate conversion

- Status: accepted
- Date: 2026-11-10

## Decision

DSP plugins are shared libraries behind a versioned C ABI,
`native/include/mirelia/audio_dsp.h`, with its own constant
`KN_DSP_ABI_VERSION` (1). Version 1 defines one capability kind, sample-rate
conversion. The built-in resampler (rubato) stays the default. A sample plugin
built on r8brain-free-src, `mirelia_r8brain.dll`, ships with the Windows
builds.

- **Entry point and capabilities.** A plugin exports
  `kn_dsp_entry(uint32_t abi_version)`, which returns static data: an id, a
  name, and an array of `{kind, vtable}` capabilities. The entry returns `NULL`
  for a version it does not implement.
- **Later kinds.** A new kind adds a `kn_dsp_kind` value, its own vtable struct
  and contract in `audio_dsp.h`, and capability records in the plugins that
  offer it. `KN_DSP_ABI_VERSION` and the resampler do not change, and older
  plugins and hosts keep working because a host ignores kinds it does not
  know. Changing an existing vtable or contract needs version 2, with the host
  asking for the newest version it knows first and falling back.
- **Registration and selection.** `kn_register_dsp_library` registers only the
  first resampler record of a plugin, because one id selects one resampler.
  Registering does not select; `kn_engine_set_resampler` does, and the
  Preferences dialog stores the id in `settings.json`.
- **Source of r8brain.** The plugin's `CMakeLists.txt` fetches r8brain-free-src
  with `FetchContent` at a pinned commit. The source is not copied into the
  repository. Configuring the plugin therefore needs network access once per
  build directory, in local builds, packaging and CI. The licence texts and the
  notices for r8brain and the Ooura FFT it compiles in are in the repository,
  because the DLL ships that code. If the source must be in the tree later, use
  a git submodule.
- **Static CRT.** The plugin links the C++ runtime statically
  (`CMAKE_MSVC_RUNTIME_LIBRARY` `MultiThreaded`), so it imports system DLLs
  only. A dynamic-CRT plugin would import `MSVCP140.dll` and `VCRUNTIME140.dll`.
  The zip's prerequisite note covers only the backend's `VCRUNTIME140.dll`, and
  in the MSIX the runtime comes from a framework package that
  `LOAD_LIBRARY_SEARCH_*` loading may not search. Static linking leaves both
  packaging paths without a new prerequisite.
- **In-process, no crash isolation.** A plugin runs in the application's
  process. An access violation in a plugin ends the application. The
  conformance suite in `native/tests/dsp-plugin-conformance` is the guard
  against wrong plugins. The contract forbids C++ exceptions, process
  termination and output on stdout or stderr across the boundary.
- **Fallback to the built-in resampler.** A plugin that is missing, rejected at
  registration, or unsupported for a rate pair never stops playback. When a
  plugin fails during playback, the engine reports `KN_ERR_PLUGIN`, replaces the
  output frames the plugin still owed with silence, and continues with rubato
  until the host selects a resampler again.

## Rationale

- **Capability array instead of per-kind pointer fields.** A struct that grows a
  field per kind needs a size field and a rule for shorter structs from old
  plugins. An array of `{kind, vtable}` never changes layout.
- **Separate version number.** `KN_ABI_VERSION` stays at 1, and every addition
  to `audio_core.h` is additive (one error code, new types, new exports), as in
  the earlier 2026-11-10 history rows of `docs/audio-abi.md`. The plugin ABI can
  move to version 2 without touching the frozen core.
- **f32 only.** As in `audio_decoder.h`, this removes format negotiation from the
  contract. The decoder output, gain stage and ring buffer are all f32.
- **Exact length and alignment in the contract.** The engine's gapless joins and
  position reporting depend on a track converting to exactly
  `ceil(frames * out_rate / in_rate)` frames with output frame 0 aligned to
  source frame 0. Leaving that to each plugin would make a plugin able to
  break gapless playback silently. The suite checks it, including a
  ten-million-frame run that detects a rate that is off by a tiny amount.
- **rubato stays the default.** It needs no plugin file, is covered by the
  existing tests, and is the fallback for every plugin failure. A track at the
  output rate is never given to any resampler, so pass-through stays bit-perfect.
- **Fetching instead of vendoring r8brain.** The decision of the project owner on
  2026-11-10. The source is not kept in the repository, which keeps the
  repository small and avoids carrying a third-party tree. The cost is network
  access at configure time, and builds depend on the upstream repository
  being reachable. The pinned commit keeps every build on the source the suite
  was run against.
- **Plugins only from the application's own directory.** The MSIX install
  directory is read-only, so a Store build can load only the plugins that were
  packaged and signed with it. No user-writable directory is scanned, and the
  work adds no trust or signature scheme for third-party plugin files.

## Consequences

- Only resampling exists. Other DSP kinds (equaliser, effects), parameter
  systems and plugin UI are not part of version 1.
- macOS has no plugin: `kn_register_dsp_library` returns `KN_ERR_UNSUPPORTED`,
  the Preferences selector is hidden, and playback uses rubato. A port needs a
  `dlopen` branch next to the Windows loader and removal of the configure-time
  error in the plugin's `CMakeLists.txt`. r8brain itself is portable.
- Decoder and output plugins are unchanged: their registration functions still
  return `KN_ERR_UNSUPPORTED`.
