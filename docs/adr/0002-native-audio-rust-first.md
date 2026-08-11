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
| `symphonia` 0.6 (M2 at 0.5, migrated M6.5) | FFmpeg / OS decoders | Pure-Rust decode for the whole v1 format set with gapless trimming and sample-accurate seek. Known gap: AAC is LC-only — HE-AAC (SBR) files fail to decode until a plugin or OS decoder covers them (0.6 detects SBR and rejects the stream) |
| `windows` 0.62 (M2) | hand-written WASAPI FFI | Official Microsoft bindings for WASAPI/MMDevice; shared mode + `AUTOCONVERTPCM` now, `IAudioClient3` exclusive in M5 |
| `ebur128` 0.1 (M5) | hand-written R128 meter | EBU R128 integrated loudness + true peak for ReplayGain 2; a faithful libebur128 port beats re-deriving K-weighting and gating by hand |
| `coreaudio-sys` (planned) | — | macOS output backend, scheduled after the Windows milestones |

## Upgrade to symphonia 0.6 — done (M6.5, 2026-08-11)

The 0.5 pin was deliberate but temporary; the migration landed in M6.5,
confined to `mirelia-decode` as planned. Re-verification outcomes:

- **Gapless trimming:** moved from `FormatOptions::enable_gapless` (0.5)
  to `AudioDecoderOptions::gapless` (0.6) — the decoder now trims
  delay/padding itself from per-packet `trim_start`/`trim_end` populated
  by the demuxer (LAME/iTunSMPB). Fixture tests (exact trimmed length,
  bit-exact FLAC splice) pass unchanged.
- **Seek:** `SeekMode::Accurate` semantics unchanged (lands at or before
  the requested position); sample-accurate and headerless-VBR seek tests
  pass with the existing skip-trim logic.
- **AAC first-block priming:** kept. 0.6's isomp4 demuxer parses the esds
  ASC so the current fixtures never exercise the loop, but it remains the
  fallback for streams that declare no rate/channels up front.
- **Decoded output vs 0.5:** bit-identical on the lossless fixture set.
- **HE-AAC (SBR):** still not decoded in 0.6 — the AAC codec detects SBR
  and rejects the stream. The LC-only gap stays open until the plugin
  decoder work (M8+).
