# ADR 0002: Rust-first native audio core behind a frozen C ABI

- Status: accepted
- Date: 2026-08-08

## Decision

The audio engine is a native cdylib (`audio-core-rs`, Rust 2024) behind a
hand-written, versioned C ABI (`native/include/mirelia/audio_core.h`).
A C++ backend (`audio-core-cpp`) is written only if measurements or a
required third-party SDK demand it, as a drop-in alternate selected at
runtime.

## Rationale

- On both Windows and macOS, "high quality" means exclusive/hog-mode access,
  native sample-rate follow, no OS-mixer resampling. Rust reaches all of it:
  WASAPI/`IAudioClient3` via the official `windows` crate, CoreAudio via
  `coreaudio-sys`. There is no capability gap, only an ergonomics gap.
- The parts genuinely easier in C++ (ASIO SDK, vendor DSP, foobar2000-shaped
  hosts) belong behind the plugin ABI, not in the core.
- Two implementations behind one ABI are only cheap if the ABI is designed
  first — hence the header is frozen at v1 before engine work starts.

## Constraints encoded in the ABI

- Plain C surface, no MSVC-specific ABI (the eventual C++ side builds with
  clang + CMake + Ninja).
- Events polled, never pushed; no callbacks into managed code from the
  real-time thread.
- The RT thread never allocates, never locks, never calls managed code.

## Dependencies introduced

| Crate | Replaces | Why |
|---|---|---|
| (workspace only at M0) | — | Engine crates gain `symphonia`, `windows`, `coreaudio-sys` etc. at M2; each addition gets its own row here or a new ADR |
