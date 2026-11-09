/*
 * Mirelia audio core ABI, version 1.
 *
 * This header is THE contract between the managed host and any native audio
 * backend. It is hand-written, frozen at v1, and versioned via KN_ABI_VERSION.
 * Changes require an ABI version bump and a matching entry in docs/audio-abi.md.
 *
 * Design constraints (see docs/audio-abi.md for rationale):
 *   1. Plain C. No C++ types, no Rust types, no callbacks into managed code
 *      from the real-time thread.
 *   2. Events are polled, not pushed.
 *   3. Position is reported in frames, never in milliseconds.
 *   4. Gapless is explicit: the host preloads the next slot.
 *   5. Version negotiation so backends can be swapped at runtime.
 */

#ifndef MIRELIA_AUDIO_CORE_H
#define MIRELIA_AUDIO_CORE_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

#define KN_ABI_VERSION 1

typedef enum kn_result {
    KN_OK                    =  0,
    KN_ERR_INVALID_ARG       = -1,
    KN_ERR_UNSUPPORTED       = -2,
    KN_ERR_DEVICE_LOST       = -3,
    KN_ERR_DEVICE_BUSY       = -4,   /* exclusive mode denied */
    KN_ERR_DECODE            = -5,
    KN_ERR_IO                = -6,
    KN_ERR_FORMAT            = -7,
    KN_ERR_STATE             = -8,
    KN_ERR_OOM               = -9,
    KN_ERR_ABI_MISMATCH      = -10,
    KN_ERR_INTERNAL          = -99
} kn_result;

typedef struct kn_engine kn_engine;

/* ---- formats --------------------------------------------------------- */

typedef enum kn_sample_format {
    KN_FMT_S16 = 0,
    KN_FMT_S24 = 1,     /* packed, 3 bytes */
    KN_FMT_S32 = 2,
    KN_FMT_F32 = 3
} kn_sample_format;

typedef struct kn_stream_format {
    uint32_t         sample_rate;
    uint16_t         channels;
    kn_sample_format format;
    uint32_t         channel_mask;   /* WAVEFORMATEXTENSIBLE-compatible; 0 = default */
} kn_stream_format;

/* ---- device enumeration ---------------------------------------------- */

typedef enum kn_share_mode {
    KN_SHARE_SHARED    = 0,
    KN_SHARE_EXCLUSIVE = 1   /* WASAPI exclusive / CoreAudio hog mode */
} kn_share_mode;

typedef struct kn_device_info {
    const char *id;              /* stable, opaque, UTF-8, backend-scoped   */
    const char *name;            /* UTF-8, human readable                   */
    const char *backend;         /* "wasapi" | "coreaudio" | "asio" | ...   */
    int         is_default;
    uint32_t    native_rate;
    uint16_t    native_channels;
    uint32_t    supported_rate_mask;    /* bitfield, see KN_RATE_* */
    uint32_t    supported_format_mask;  /* 1 << kn_sample_format   */
    int         supports_exclusive;
    uint32_t    min_period_frames;      /* IAudioClient3 min periodicity */
    uint32_t    default_period_frames;
} kn_device_info;

/* Snapshot: valid until the next call to kn_devices_free or kn_devices_enumerate. */
kn_result kn_devices_enumerate(kn_engine *e,
                               const kn_device_info **out_devices,
                               size_t *out_count);
void      kn_devices_free(kn_engine *e);

/* ---- engine lifecycle ------------------------------------------------- */

typedef struct kn_engine_config {
    uint32_t abi_version;        /* must be KN_ABI_VERSION */
    uint32_t target_latency_ms;  /* hint; 0 = backend default */
    uint32_t decode_ahead_ms;    /* ring buffer depth, default 2000 */
    int      allow_resample;     /* 0 = fail rather than resample */
    int      dither_to_int;      /* TPDF dither on float->int: 0 = off,
                                    2 = only on tracks the backend had to
                                    resample, any other value = always.
                                    Exclusive mode with integer devices only. */
} kn_engine_config;

kn_result kn_engine_create(const kn_engine_config *cfg, kn_engine **out);
void      kn_engine_destroy(kn_engine *e);

/* Human-readable detail for the most recent failure on this thread. */
const char *kn_engine_last_error(kn_engine *e);

/*
 * `desired` selects the output format of every stream until the device is
 * closed:
 *   - NULL: follow the source's sample rate and channels. out_actual is the
 *     device's native format.
 *   - non-NULL: sample_rate is the fixed output rate; sources at another rate
 *     are resampled to it (KN_ERR_FORMAT on load if cfg.allow_resample is 0).
 *     sample_rate must be non-zero, otherwise KN_ERR_INVALID_ARG is returned
 *     before the device is touched.
 *     channels == 0 follows the source's channel count, and the stream then
 *     opens at the first load; out_actual carries the desired rate and the
 *     device's native channel count. A non-zero channels opens the stream
 *     now. format and channel_mask are ignored.
 */
kn_result kn_engine_open_device(kn_engine *e,
                                const char *device_id,   /* NULL = system default */
                                kn_share_mode mode,
                                const kn_stream_format *desired, /* NULL = follow source */
                                kn_stream_format *out_actual);
kn_result kn_engine_close_device(kn_engine *e);

/* ---- transport -------------------------------------------------------- */

/*
 * A "slot" is a decoded source. Two slots exist: current and next.
 * Loading into the next slot is what makes gapless work.
 */
typedef enum kn_slot { KN_SLOT_CURRENT = 0, KN_SLOT_NEXT = 1 } kn_slot;

typedef struct kn_load_params {
    const char *path;             /* UTF-8. Backends must widen on Windows. */
    int64_t     start_frame;      /* trim head (encoder delay applied on top) */
    int64_t     end_frame;        /* -1 = to end */
    uint32_t    encoder_delay;    /* frames; 0 if unknown */
    uint32_t    encoder_padding;  /* frames; 0 if unknown */
    float       gain_db;          /* ReplayGain, already resolved by host */
    float       peak;             /* for clipping prevention; 0 = unknown */
    uint64_t    user_token;       /* echoed back in events. Host's track id. */
} kn_load_params;

kn_result kn_load(kn_engine *e, kn_slot slot, const kn_load_params *p);
kn_result kn_clear(kn_engine *e, kn_slot slot);

kn_result kn_play(kn_engine *e);
kn_result kn_pause(kn_engine *e);
kn_result kn_stop(kn_engine *e);          /* clears both slots */
kn_result kn_seek(kn_engine *e, int64_t frame);
kn_result kn_skip_to_next(kn_engine *e);  /* immediate, not gapless */

/* Linear gain, 0.0 .. 1.0+. Applied after ReplayGain. Smoothed internally. */
kn_result kn_set_volume(kn_engine *e, float gain);

typedef enum kn_state {
    KN_STATE_IDLE = 0, KN_STATE_PLAYING, KN_STATE_PAUSED, KN_STATE_STALLED
} kn_state;

typedef struct kn_position {
    kn_state state;
    uint64_t user_token;      /* what is audible right now */
    int64_t  frame;           /* position within the current source */
    int64_t  total_frames;    /* -1 if unknown */
    uint32_t sample_rate;     /* rate of the source; frame and total_frames use it */
    uint64_t host_time_ns;    /* monotonic, for client-side interpolation */
    float    buffer_fill;     /* 0..1, for the stall indicator */
} kn_position;

kn_result kn_get_position(kn_engine *e, kn_position *out);

/* ---- events (polled) --------------------------------------------------- */

typedef enum kn_event_type {
    KN_EV_STATE_CHANGED = 1,
    KN_EV_TRACK_STARTED,        /* the NEXT slot became CURRENT */
    KN_EV_TRACK_ENDED,
    KN_EV_QUEUE_EMPTY,          /* engine needs a next slot NOW */
    KN_EV_DEVICE_CHANGED,       /* default device switched under us */
    KN_EV_DEVICE_LOST,
    KN_EV_ERROR
} kn_event_type;

typedef struct kn_event {
    kn_event_type type;
    uint64_t      user_token;
    int32_t       code;         /* kn_result for ERROR */
    uint64_t      host_time_ns;
} kn_event;

/*
 * Drains up to `capacity` events. Returns the number written.
 * Safe to call from any single thread; must not be called concurrently.
 */
size_t kn_engine_poll_event(kn_engine *e, kn_event *out, size_t capacity);

/* ---- metadata probe (no playback) --------------------------------------- */

typedef struct kn_probe_result {
    uint32_t sample_rate;
    uint16_t channels;
    uint16_t bit_depth;
    int64_t  total_frames;
    uint32_t encoder_delay;
    uint32_t encoder_padding;
    uint32_t bitrate_kbps;
    char     codec[16];
} kn_probe_result;

kn_result kn_probe(kn_engine *e, const char *path, kn_probe_result *out);

/* EBU R128 scan. Blocking; call from a worker thread. */
kn_result kn_scan_loudness(kn_engine *e, const char *path,
                           float *out_gain_db, float *out_peak);

/* ---- plugins ------------------------------------------------------------ */

kn_result kn_register_decoder_library(kn_engine *e, const char *dll_path);
kn_result kn_register_output_library (kn_engine *e, const char *dll_path);

#ifdef __cplusplus
}
#endif
#endif /* MIRELIA_AUDIO_CORE_H */
