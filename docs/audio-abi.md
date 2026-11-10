# Audio ABI — prose companion to `audio_core.h`

The contract itself is `native/include/mirelia/audio_core.h`, hand-written and
frozen at **v1** (`KN_ABI_VERSION 1`). This document explains the intent behind
it; the header wins on any disagreement about the letter.

## Design constraints

1. **Plain C.** No C++ types, no Rust types, no callbacks into managed code
   from the real-time thread. The exported surface must be implementable by
   both `audio-core-rs` (Rust) and a future `audio-core-cpp` (clang/C++20)
   without any MSVC-specific ABI.
2. **Events are polled, not pushed.** The engine pushes state changes onto an
   SPSC queue; the host drains it with `kn_engine_poll_event()` from a normal
   thread. This makes GC pauses, managed exceptions, and reentrancy
   structurally impossible on the RT path.
3. **Position is reported in frames** at the source sample rate of the audible
   track (`kn_position.sample_rate`), plus a monotonic host timestamp for
   interpolation. When the output runs at a different rate, the engine
   converts before publishing. Never in milliseconds.
4. **Explicit gapless.** The host loads what plays next into `KN_SLOT_NEXT`;
   the engine preloads and splices. The host is never asked to react at the
   moment of transition. `KN_EV_QUEUE_EMPTY` is the engine's request for a
   next slot.
5. **Version negotiation.** `kn_engine_create` rejects an `abi_version` it
   does not implement with `KN_ERR_ABI_MISMATCH`, so a stale backend fails
   loudly at startup instead of corrupting state.

## The real-time rule

> The audio real-time thread never allocates, never locks, never calls
> managed code.

This is enforced by the conformance suite (`native/tests/abi-conformance`):
the Rust backend runs under a custom global allocator asserting the RT thread
id; the eventual C++ backend under an overridden `operator new`.

## Backend selection

`Mirelia.AudioBridge` resolves the native library through
`NativeLibrary.SetDllImportResolver`. There is one backend,
`audio_core_rs.dll` / `libaudio_core_rs.dylib`, and the resolver looks for it
in this order:

1. The full path in the `MIRELIA_AUDIO_CORE` environment variable, if set.
2. Next to the executable, which is how a packaged build is laid out.
3. The cargo target directory, walking up from the application base, so
   `dotnet run` works after `cargo build` without a copy step.

A second backend would be substituted through the same resolver. There is no
user-facing setting for it.

## Plugin ABIs

- `audio_decoder.h` — decode plugins. Single export
  `kn_decoder_entry(uint32_t abi_version)`, returns `NULL` on mismatch.
  Decoders always output **interleaved f32**; this removes format
  negotiation from the plugin contract entirely.
- `audio_output.h` — output plugins, same entry-point pattern
  (`kn_output_entry`). `write` is the only vtable member called from the RT
  thread and is bound by the real-time rule above.
- `audio_dsp.h` — DSP plugins, in `native/include/mirelia/`. Unlike the two
  above this ABI is implemented. It has its own version,
  `KN_DSP_ABI_VERSION` (1), so it can change without touching the frozen core
  ABI. See "DSP plugins" below.

### DSP plugins

A DSP plugin is a shared library with one export,
`const kn_dsp_plugin *kn_dsp_entry(uint32_t abi_version)`, which returns `NULL`
for a version it does not implement. The returned struct is static data: an id
(stored in settings), a display name, and an array of capabilities. A capability
is a `{kind, vtable}` pair. Version 1 defines one kind, `KN_DSP_KIND_RESAMPLER`,
and a host ignores kinds it does not know. A later kind adds an enum value, its
own vtable struct and contract, and capability records in the plugins that offer
it; `KN_DSP_ABI_VERSION` stays 1. Changing an existing vtable or contract needs
version 2. The decision is recorded in
[ADR 0008](adr/0008-dsp-plugin-abi.md).

**Resampler contract.** The header is the contract; this is a summary.

- One context converts one stream at one rate pair and channel count. Samples
  are interleaved f32.
- Output frame 0 aligns with source frame 0. The plugin removes its own filter
  delay.
- After `flush` the total output is exactly `ceil(fed * out_rate / in_rate)`
  frames, and never more than that before it.
- `pending_frames` is that total minus what was emitted, and is above 0 while a
  non-empty run is unflushed.
- The output does not depend on how the input is split into `process` calls.
  `reset` restores the state of a fresh context.
- The host passes enough output capacity. A plugin given less returns
  `KN_ERR_INVALID_ARG`. After any other negative return the host calls only
  `destroy` on that context.
- Every member runs on the engine's decode worker, never on the real-time
  thread. The real-time rule above is unchanged.

**Registration.** `kn_register_dsp_library(e, path)` takes an absolute UTF-8
path, loads the library and validates its contents once, on the calling thread.
It returns `KN_ERR_INVALID_ARG` for a null or relative path, `KN_ERR_IO` for a
missing file, `KN_ERR_FORMAT` for a library that does not load, has no
`kn_dsp_entry`, or has malformed contents, `KN_ERR_ABI_MISMATCH` when the entry
returns `NULL`, `KN_ERR_UNSUPPORTED` when no capability has a kind this backend
knows, and `KN_ERR_STATE` for an id that is already registered on the engine.
On every failure the library is unloaded.

Only the first resampler record of a plugin is registered, because one id
selects one resampler. `kn_dsp_enumerate` therefore lists one entry per
registered plugin. Further resampler records in the same plugin are ignored.

Registering does not select. `kn_engine_set_resampler(e, id)` selects a
registered resampler, or the built-in one for `NULL`, and takes effect at the
next `kn_load` into `KN_SLOT_CURRENT`. An id that is not registered is
`KN_ERR_INVALID_ARG`. A plugin is used only when the output rate is fixed
(`kn_engine_open_device` with a non-NULL `desired`), and a track whose source
rate equals the output rate is never given to any resampler, so pass-through
stays bit-perfect.

**Fallback.** The built-in resampler (rubato) is the default and is never
removed. If `create` returns `KN_ERR_UNSUPPORTED` for a rate pair or channel
count, the built-in resampler converts that stream and nothing is reported. If a
plugin fails during playback, or returns counts that break the contract, the
engine emits an `Error` event with `KN_ERR_PLUGIN`. Output frames the plugin
still owed are replaced by silence, and the built-in resampler converts the rest
of the session until the host selects a resampler again. Playback does not stop.

**Process model.** Plugins run in the engine's process. A plugin that crashes
ends the application; the conformance suite is the guard against wrong plugins.
A plugin must not let a C++ exception cross the ABI.

**Where plugins are found.** The desktop app registers every `*.dll` in
`plugins/dsp/` next to the executable. `MIRELIA_DSP_PLUGIN_DIR` replaces that
directory for development and tests; like `MIRELIA_AUDIO_CORE`, it is not a
user-facing setting. Plugins are implemented on Windows only. On other platforms
`kn_register_dsp_library` returns `KN_ERR_UNSUPPORTED`, `kn_dsp_enumerate` is
empty, and `kn_engine_set_resampler(e, NULL)` succeeds. The sample plugin
`mirelia_r8brain.dll`, built from `native/plugins/r8brain-resampler`, is
packaged into the Windows archives under `plugins/dsp/`.

**Conformance suite.** `native/tests/dsp-plugin-conformance` is a C program that
loads a plugin by path and checks the contract: entry point, creation, exact
length, alignment, block independence, `pending_frames`, reset, state after
flush, capacity handling, and drift over ten million frames. It also builds a
reference plugin and eleven deliberately wrong ones, so that CTest shows each
check fails on the fault it targets (12 tests). To run it, from a shell with the
Visual Studio developer environment on Windows:

```sh
cmake -S native/tests/dsp-plugin-conformance -B build/dsp-conformance -G Ninja -DCMAKE_C_COMPILER=clang
cmake --build build/dsp-conformance
ctest --test-dir build/dsp-conformance --output-on-failure
build/dsp-conformance/dsp-plugin-conformance.exe <path-to-plugin>
```

The last command exits 0 and prints no `FAIL` line when the plugin conforms.
`abi-conformance` takes an optional third argument, a plugin path, and checks
registration and selection against the backend.

Building the sample plugin needs network access once per build directory:
its `CMakeLists.txt` fetches r8brain-free-src at a pinned commit when CMake
configures it.

## Version history

| ABI | Date | Change |
|---|---|---|
| 1 | 2026-08-08 | Initial freeze |
| 1 | 2026-11-10 | No layout or signature change. A non-NULL `desired` in `kn_engine_open_device` fixes the output rate (`sample_rate == 0` is `KN_ERR_INVALID_ARG`, `channels == 0` follows the source). `kn_position` is in source frames, not device frames. |
| 1 | 2026-11-10 | No layout or signature change. `kn_engine_config.dither_to_int` is a mode: `0` off, `2` dither only tracks the backend resampled, any other non-zero value (including the former `1`) always. A track that passes through at its own rate is never dithered in mode `2`, so it stays bit-perfect. |
| 1 | 2026-11-10 | No layout or signature change. Additive: new error code `KN_ERR_PLUGIN` (-11), types `kn_dsp_kind` and `kn_dsp_info`, and the exports `kn_register_dsp_library`, `kn_dsp_enumerate`, `kn_dsp_free` and `kn_engine_set_resampler`. DSP plugins have their own header and version (`audio_dsp.h`, `KN_DSP_ABI_VERSION` 1). `KN_ABI_VERSION` stays 1. |
