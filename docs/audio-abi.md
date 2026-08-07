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
3. **Position is reported in frames** at the device sample rate, plus a
   monotonic host timestamp for interpolation. Never in milliseconds.
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
`NativeLibrary.SetDllImportResolver`, choosing from:

```
audio_core_rs.dll / libaudio_core_rs.dylib     (default)
audio_core_cpp.dll                             (opt-in, Windows only, if present)
```

Selection is a user setting with an "auto" default.

## Plugin ABIs

- `audio_decoder.h` — decode plugins. Single export
  `kn_decoder_entry(uint32_t abi_version)`, returns `NULL` on mismatch.
  Decoders always output **interleaved f32**; this removes format
  negotiation from the plugin contract entirely.
- `audio_output.h` — output plugins, same entry-point pattern
  (`kn_output_entry`). `write` is the only vtable member called from the RT
  thread and is bound by the real-time rule above.

## Version history

| ABI | Date | Change |
|---|---|---|
| 1 | 2026-08-08 | Initial freeze |
