# ADR 0004: No foobar2000 input-plugin hosting in v1

- Status: accepted
- Date: 2026-08-08

## Decision

Hosting foobar2000 input components is rejected for v1.

## Rationale

- The fb2k SDK is bound to the MSVC C++ ABI and would require reimplementing
  `service_base`, `pfc`, `filesystem`, `abort_callback` and friends.
- Windows-only; licensing is unclear.
- The format coverage it would buy is obtainable from libopenmpt, vgmstream,
  game-music-emu, and FFmpeg behind `MireliaDecoderVtable`
  (`native/include/mirelia/audio_decoder.h`).

## If ever revisited

The only acceptable shape is an out-of-process 32-bit host streaming PCM
back over shared memory — which also isolates plugin crashes. That host
would itself appear to the engine as one more `MireliaDecoderVtable`.
