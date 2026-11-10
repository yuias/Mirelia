/*
 * Mirelia DSP plugin ABI, version 1.
 *
 * A DSP plugin is a shared library exporting a single entry point:
 *
 *     const MireliaDspPlugin *kn_dsp_entry(uint32_t abi_version);
 *
 * The entry point returns NULL if it does not implement `abi_version`. The
 * returned struct, its strings and its vtables are static data valid until the
 * library is unloaded. The host does not free them, and no memory allocated on
 * one side of this ABI is freed on the other.
 *
 * A plugin lists what it does as an array of capabilities. Version 1 defines
 * one kind, MireliaDspKindResampler. A later kind adds a MireliaDspKind value and its
 * own vtable struct and does not change this version: a host ignores
 * capabilities whose kind it does not know, and one plugin may list several.
 *
 * Plugin code must not let a C++ exception or any other unwinding cross this
 * boundary, must not terminate the process, and must not write to stdout or
 * stderr.
 */

#ifndef MIRELIA_AUDIO_DSP_H
#define MIRELIA_AUDIO_DSP_H

#include <stdint.h>
#include <stddef.h>

#include "audio_core.h"

#ifdef __cplusplus
extern "C" {
#endif

#define MIRELIA_DSP_ABI_VERSION 1

#if defined(_WIN32)
#define MIRELIA_DSP_EXPORT __declspec(dllexport)
#else
#define MIRELIA_DSP_EXPORT __attribute__((visibility("default")))
#endif

/*
 * Sample-rate conversion. One context converts one stream, for one rate pair
 * and channel count, in a sequence of "runs". A run starts at create or reset.
 *
 * Terms, all per run, in exact integer arithmetic:
 *   fed      source frames accepted by process, including any the plugin is
 *            still buffering
 *   emitted  output frames returned by process and flush
 *   out(n)   ceil(n * out_rate / in_rate)
 *
 * Contract:
 *  - Alignment. Output frame m is the source signal at time m / out_rate,
 *    where source frame n is at time n / in_rate, so output frame 0 aligns with
 *    source frame 0. Filter delay is the plugin's to remove: it drops the output
 *    frames its filter delays by. Input before the run and input after `fed`
 *    are zeros.
 *  - Exact length. After flush, emitted == out(fed). Before flush,
 *    emitted <= out(fed) at every point.
 *  - pending_frames == out(fed) - emitted. It is 0 at the start of a run, after
 *    reset and after flush. It is greater than 0 after any process call that
 *    leaves fed > 0 unflushed: a plugin with no filter delay of its own must
 *    still hold back at least one output frame.
 *  - Block independence. The concatenated output of a run is bit-identical for
 *    every way of splitting the same input into process calls, including calls
 *    with zero frames.
 *  - Reset restores the state of a fresh create from any state (fresh, mid-run,
 *    flushed). The output after reset is bit-identical to that of a fresh
 *    context given the same input.
 *  - Capacity. The host passes out_capacity >= out(fed + in_frames) - emitted to
 *    process and out_capacity >= pending_frames to flush, and the plugin never
 *    writes beyond out_capacity frames. A plugin given less returns
 *    MireliaErrInvalidArg and changes no state, consuming nothing.
 *  - Samples are interleaved f32 with the channel order unchanged, each channel
 *    converted independently. Output is not clamped.
 *  - Failure. After any negative return from process, flush or pending_frames,
 *    or any result other than MireliaOk from reset, the host calls only destroy on
 *    that context. Two returns are not such failures and leave the context in
 *    the state it had: MireliaErrInvalidArg for a too-small capacity and
 *    MireliaErrState from process after flush.
 *
 * Threading. All members run on the engine's decode worker, never on the
 * real-time thread, so they may allocate and may block briefly. A context is
 * used from one thread at a time. Separate contexts may be used concurrently.
 */
typedef struct MireliaDspResamplerVtable {
    /* The host guarantees in_rate != out_rate, both non-zero, and channels > 0.
     * Returns MireliaErrUnsupported for a rate pair or channel count the plugin
     * does not convert; the host then uses its built-in resampler for that
     * stream without reporting an error. On failure *out_ctx is not used. */
    MireliaResult (*create)(uint32_t in_rate, uint32_t out_rate, uint16_t channels,
                            void **out_ctx);

    /* Consumes all in_frames and returns the output frames written (zero or
     * more), or a negative MireliaResult. MireliaErrState if called after flush
     * without a reset. in_frames may be 0. */
    int64_t       (*process)(void *ctx, const float *in, int64_t in_frames,
                             float *out, int64_t out_capacity);

    /* Treats further input as zeros and writes exactly pending_frames frames.
     * Returns that count, or a negative MireliaResult. After flush, only reset,
     * destroy, pending_frames (which is 0) and flush (returns 0) are valid. */
    int64_t       (*flush)(void *ctx, float *out, int64_t out_capacity);

    /* out(fed) - emitted, or a negative MireliaResult. */
    int64_t       (*pending_frames)(void *ctx);

    MireliaResult (*reset)(void *ctx);
    void          (*destroy)(void *ctx);
} MireliaDspResamplerVtable;

typedef struct MireliaDspCapability {
    MireliaDspKind kind;
    /* Layout chosen by kind. MireliaDspKindResampler: const MireliaDspResamplerVtable *. */
    const void *vtable;
} MireliaDspCapability;

typedef struct MireliaDspPlugin {
    uint32_t                    abi_version;
    const char                 *id;    /* 1..64 bytes of [a-z0-9._-]; stable, stored in settings */
    const char                 *name;  /* UTF-8, 1..128 bytes; shown in the UI */
    uint32_t                    capability_count;  /* 1..32 */
    const MireliaDspCapability *capabilities;
} MireliaDspPlugin;

/* The single required export. Returns NULL on ABI version mismatch. */
typedef const MireliaDspPlugin *(*MireliaDspEntryFn)(uint32_t abi_version);

#ifdef __cplusplus
}
#endif
#endif /* MIRELIA_AUDIO_DSP_H */
