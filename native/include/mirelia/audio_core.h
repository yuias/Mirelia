/*
 * Mirelia audio core ABI, version 1.
 *
 * This header is THE contract between the managed host and any native audio
 * backend. It is hand-written, frozen at v1, and versioned via MIRELIA_ABI_VERSION.
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

#define MIRELIA_ABI_VERSION 1

typedef enum MireliaResult {
    MireliaOk                =  0,
    MireliaErrInvalidArg     = -1,
    MireliaErrUnsupported    = -2,
    MireliaErrDeviceLost     = -3,
    MireliaErrDeviceBusy     = -4,   /* exclusive mode denied */
    MireliaErrDecode         = -5,
    MireliaErrIo             = -6,
    MireliaErrFormat         = -7,
    MireliaErrState          = -8,
    MireliaErrOom            = -9,
    MireliaErrAbiMismatch    = -10,
    MireliaErrPlugin         = -11,  /* a DSP plugin failed; playback continued without it */
    MireliaErrInternal       = -99
} MireliaResult;

typedef struct MireliaEngine MireliaEngine;

/* ---- formats --------------------------------------------------------- */

typedef enum MireliaSampleFormat {
    MireliaFmtS16 = 0,
    MireliaFmtS24 = 1,     /* packed, 3 bytes */
    MireliaFmtS32 = 2,
    MireliaFmtF32 = 3
} MireliaSampleFormat;

typedef struct MireliaStreamFormat {
    uint32_t            sample_rate;
    uint16_t            channels;
    MireliaSampleFormat format;
    uint32_t            channel_mask;   /* WAVEFORMATEXTENSIBLE-compatible; 0 = default */
} MireliaStreamFormat;

/* ---- device enumeration ---------------------------------------------- */

typedef enum MireliaShareMode {
    MireliaShareShared    = 0,
    MireliaShareExclusive = 1   /* WASAPI exclusive / CoreAudio hog mode */
} MireliaShareMode;

typedef struct MireliaDeviceInfo {
    const char *id;              /* stable, opaque, UTF-8, backend-scoped   */
    const char *name;            /* UTF-8, human readable                   */
    const char *backend;         /* "wasapi" | "coreaudio" | "asio" | ...   */
    int         is_default;
    uint32_t    native_rate;
    uint16_t    native_channels;
    uint32_t    supported_rate_mask;    /* reserved: no bits defined in v1, backends write 0 */
    uint32_t    supported_format_mask;  /* 1 << MireliaSampleFormat */
    int         supports_exclusive;
    uint32_t    min_period_frames;      /* IAudioClient3 min periodicity */
    uint32_t    default_period_frames;
} MireliaDeviceInfo;

/* Snapshot: valid until the next call to kn_devices_free or kn_devices_enumerate. */
MireliaResult kn_devices_enumerate(MireliaEngine *e,
                                   const MireliaDeviceInfo **out_devices,
                                   size_t *out_count);
void          kn_devices_free(MireliaEngine *e);

/* ---- engine lifecycle ------------------------------------------------- */

typedef struct MireliaEngineConfig {
    uint32_t abi_version;        /* must be MIRELIA_ABI_VERSION */
    uint32_t target_latency_ms;  /* hint; 0 = backend default */
    uint32_t decode_ahead_ms;    /* ring buffer depth, default 2000 */
    int      allow_resample;     /* 0 = fail rather than resample */
    int      dither_to_int;      /* TPDF dither on float->int: 0 = off,
                                    2 = only on tracks the backend had to
                                    resample, any other value = always.
                                    Exclusive mode with integer devices only. */
} MireliaEngineConfig;

MireliaResult kn_engine_create(const MireliaEngineConfig *cfg, MireliaEngine **out);
void          kn_engine_destroy(MireliaEngine *e);

/* Human-readable detail for the most recent failure on this thread. */
const char *kn_engine_last_error(MireliaEngine *e);

/*
 * `desired` selects the output format of every stream until the device is
 * closed:
 *   - NULL: follow the source's sample rate and channels. out_actual is the
 *     device's native format.
 *   - non-NULL: sample_rate is the fixed output rate; sources at another rate
 *     are resampled to it (MireliaErrFormat on load if cfg.allow_resample is 0).
 *     sample_rate must be non-zero, otherwise MireliaErrInvalidArg is returned
 *     before the device is touched.
 *     channels == 0 follows the source's channel count, and the stream then
 *     opens at the first load; out_actual carries the desired rate and the
 *     device's native channel count. A non-zero channels opens the stream
 *     now. format and channel_mask are ignored.
 */
MireliaResult kn_engine_open_device(MireliaEngine *e,
                                    const char *device_id,   /* NULL = system default */
                                    MireliaShareMode mode,
                                    const MireliaStreamFormat *desired, /* NULL = follow source */
                                    MireliaStreamFormat *out_actual);
MireliaResult kn_engine_close_device(MireliaEngine *e);

/* ---- transport -------------------------------------------------------- */

/*
 * A "slot" is a decoded source. Two slots exist: current and next.
 * Loading into the next slot is what makes gapless work.
 */
typedef enum MireliaSlot { MireliaSlotCurrent = 0, MireliaSlotNext = 1 } MireliaSlot;

typedef struct MireliaLoadParams {
    const char *path;             /* UTF-8. Backends must widen on Windows. */
    int64_t     start_frame;      /* trim head (encoder delay applied on top) */
    int64_t     end_frame;        /* -1 = to end */
    uint32_t    encoder_delay;    /* frames; 0 if unknown */
    uint32_t    encoder_padding;  /* frames; 0 if unknown */
    float       gain_db;          /* ReplayGain, already resolved by host */
    float       peak;             /* for clipping prevention; 0 = unknown */
    uint64_t    user_token;       /* echoed back in events. Host's track id. */
} MireliaLoadParams;

MireliaResult kn_load(MireliaEngine *e, MireliaSlot slot, const MireliaLoadParams *p);
MireliaResult kn_clear(MireliaEngine *e, MireliaSlot slot);

MireliaResult kn_play(MireliaEngine *e);
MireliaResult kn_pause(MireliaEngine *e);
MireliaResult kn_stop(MireliaEngine *e);          /* clears both slots */
MireliaResult kn_seek(MireliaEngine *e, int64_t frame);
MireliaResult kn_skip_to_next(MireliaEngine *e);  /* immediate, not gapless */

/* Linear gain, 0.0 .. 1.0+. Applied after ReplayGain. Smoothed internally. */
MireliaResult kn_set_volume(MireliaEngine *e, float gain);

typedef enum MireliaState {
    MireliaStateIdle = 0, MireliaStatePlaying, MireliaStatePaused, MireliaStateStalled
} MireliaState;

typedef struct MireliaPosition {
    MireliaState state;
    uint64_t     user_token;      /* what is audible right now */
    int64_t      frame;           /* position within the current source */
    int64_t      total_frames;    /* -1 if unknown */
    uint32_t     sample_rate;     /* rate of the source; frame and total_frames use it */
    uint64_t     host_time_ns;    /* monotonic, for client-side interpolation */
    float        buffer_fill;     /* 0..1, for the stall indicator */
} MireliaPosition;

MireliaResult kn_get_position(MireliaEngine *e, MireliaPosition *out);

/* ---- events (polled) --------------------------------------------------- */

typedef enum MireliaEventType {
    MireliaEvStateChanged = 1,
    MireliaEvTrackStarted,        /* the NEXT slot became CURRENT */
    MireliaEvTrackEnded,
    MireliaEvQueueEmpty,          /* engine needs a next slot NOW */
    MireliaEvDeviceChanged,       /* default device switched under us */
    MireliaEvDeviceLost,
    MireliaEvError
} MireliaEventType;

typedef struct MireliaEvent {
    MireliaEventType type;
    uint64_t         user_token;
    int32_t          code;         /* MireliaResult for ERROR */
    uint64_t         host_time_ns;
} MireliaEvent;

/*
 * Drains up to `capacity` events. Returns the number written.
 * Safe to call from any single thread; must not be called concurrently.
 */
size_t kn_engine_poll_event(MireliaEngine *e, MireliaEvent *out, size_t capacity);

/* ---- metadata probe (no playback) --------------------------------------- */

typedef struct MireliaProbeResult {
    uint32_t sample_rate;
    uint16_t channels;
    uint16_t bit_depth;
    int64_t  total_frames;
    uint32_t encoder_delay;
    uint32_t encoder_padding;
    uint32_t bitrate_kbps;
    char     codec[16];
} MireliaProbeResult;

MireliaResult kn_probe(MireliaEngine *e, const char *path, MireliaProbeResult *out);

/* EBU R128 scan. Blocking; call from a worker thread. */
MireliaResult kn_scan_loudness(MireliaEngine *e, const char *path,
                               float *out_gain_db, float *out_peak);

/* ---- plugins ------------------------------------------------------------ */

MireliaResult kn_register_decoder_library(MireliaEngine *e, const char *dll_path);
MireliaResult kn_register_output_library (MireliaEngine *e, const char *dll_path);

typedef enum MireliaDspKind {
    MireliaDspKindResampler = 1
} MireliaDspKind;

typedef struct MireliaDspInfo {
    const char     *id;
    const char     *name;
    MireliaDspKind  kind;
} MireliaDspInfo;

/* dll_path is an absolute UTF-8 path. Loads the library and validates it, on the
 * calling thread. MireliaErrUnsupported where plugins are not implemented, or for
 * a plugin offering no kind this backend knows. */
MireliaResult kn_register_dsp_library(MireliaEngine *e, const char *dll_path);

/* Snapshot of one entry per registered plugin: valid until the next call to
 * kn_dsp_free or kn_dsp_enumerate. Only the first resampler capability of a
 * plugin is registered, because one id selects one resampler. */
MireliaResult kn_dsp_enumerate(MireliaEngine *e, const MireliaDspInfo **out_infos,
                               size_t *out_count);
void          kn_dsp_free(MireliaEngine *e);

/* Selects the resampler plugin by id; NULL selects the built-in resampler. Takes
 * effect at the next kn_load into MireliaSlotCurrent. MireliaErrInvalidArg for an id
 * that is not registered. */
MireliaResult kn_engine_set_resampler(MireliaEngine *e, const char *plugin_id);

#ifdef __cplusplus
}
#endif
#endif /* MIRELIA_AUDIO_CORE_H */
