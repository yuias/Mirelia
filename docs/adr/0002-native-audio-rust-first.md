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
| `symphonia` 0.5 (M2) | FFmpeg / OS decoders | Pure-Rust decode for the whole v1 format set with gapless trimming and sample-accurate seek. Pinned to 0.5: 0.6.0 shipped 2026-05 with a reworked API; migrate deliberately. Known gap: AAC is LC-only — HE-AAC (SBR) files fail to decode until a plugin or OS decoder covers them |
| `windows` 0.62 (M2) | hand-written WASAPI FFI | Official Microsoft bindings for WASAPI/MMDevice; shared mode + `AUTOCONVERTPCM` now, `IAudioClient3` exclusive in M5 |
| `ebur128` 0.1 (M5) | hand-written R128 meter | EBU R128 integrated loudness + true peak for ReplayGain 2; a faithful libebur128 port beats re-deriving K-weighting and gating by hand |
| `coreaudio-sys` (planned) | — | macOS output backend, scheduled after the Windows milestones |

## Planned upgrade: symphonia 0.6+

The 0.5 pin is deliberate but temporary. Migration plan:

- **When:** after M5, once the conformance suite fully covers gapless
  (delay/padding fixtures), seek accuracy on headerless VBR, and position
  precision. Those tests are the safety net that makes the API migration a
  mechanical exercise instead of a re-verification project.
- **What to re-verify on 0.6:** gapless trimming behaviour (0.5 handles it
  inside `enable_gapless`; confirm the 0.6 equivalent), sample-accurate
  seek semantics (`SeekMode::Accurate` landing point), the AAC
  first-block-priming workaround (may become unnecessary), and decoded
  output equality against 0.5 on the fixture set (bit-compare lossless
  decodes).
- **Also evaluate:** whether 0.6 adds HE-AAC (SBR) support — if so, the
  known AAC-LC-only gap closes for free and the decoder row above gets
  updated.
- All symphonia usage is confined to `mirelia-decode`, so the migration
  cannot leak past one crate.
