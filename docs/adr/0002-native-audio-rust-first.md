# ADR 0002: Rust-first native audio core behind a frozen C ABI

- Status: accepted
- Date: 2026-08-08

## Decision

The audio engine is a native cdylib (`audio-core-rs`, Rust 2024) behind a
hand-written, versioned C ABI (`native/include/mirelia/audio_core.h`).
A C++ backend (`audio-core-cpp`) is written only if measurements or a
required third-party SDK demand it, as a drop-in alternate resolved through
the same `DllImport` resolver. Nothing of it exists yet.

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
| `symphonia` 0.6 (adopted at 0.5, migrated 2026-08-11) | FFmpeg / OS decoders | Pure-Rust decode for the whole v1 format set with gapless trimming and sample-accurate seek. Known gap: AAC is LC-only — an HE-AAC file decodes as its LC core, at half the sample rate and, for parametric stereo, in mono |
| `windows` 0.62 | hand-written WASAPI FFI | Official Microsoft bindings for WASAPI/MMDevice; shared mode + `AUTOCONVERTPCM`, and `IAudioClient3` for exclusive mode |
| `ebur128` 0.1 | hand-written R128 meter | EBU R128 integrated loudness + true peak for ReplayGain 2; a faithful libebur128 port beats re-deriving K-weighting and gating by hand |
| `coreaudio-sys` (planned) | — | macOS output backend, scheduled after the Windows one |

## Upgrade to symphonia 0.6 — done 2026-08-11

The 0.5 pin was deliberate but temporary; the migration landed confined to
`mirelia-decode` as planned. Re-verification outcomes:

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
- **HE-AAC (SBR):** still not decoded in 0.6, but not in the way this ADR
  first recorded. The decoder does reject a stream whose `sbr_present` is
  set, yet for the usual backward-compatible signalling — base object type
  LC, SBR declared in a `0x2B7` sync extension — that flag is never set.
  `AudioSpecificConfig` parsing reads the sync extension only when
  `sbr_ps_info` is already populated, and that happens solely when the base
  object type is itself SBR or PS (`symphonia-common` 0.6.0,
  `src/mpeg/audio/mod.rs:246-261` and `:400`). Such a file therefore opens
  and plays as its LC core, silently: half the sample rate, and mono where
  parametric stereo was meant.

  Measured 2026-09-21 over an 11,313-track AAC collection: 8,272 plain LC,
  3,039 declaring the sync extension with `sbrPresentFlag` clear (so LC in
  substance), 2 genuinely carrying SBR. Both of those play, degraded. The
  practical cost of the gap is much smaller than "files fail", and the
  symptom is wrong output rather than an error — which is the worse
  property of the two.
