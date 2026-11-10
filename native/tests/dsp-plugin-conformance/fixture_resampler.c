/*
 * Reference resampler plugin for the DSP conformance suite.
 *
 * A small windowed-sinc resampler that follows the resampler contract in
 * audio_dsp.h. It exists so the suite can be proven against a known-good plugin
 * before any real plugin is measured with it. It is written for clarity, not
 * speed, and is plain C so the suite needs no Rust or C++ toolchain.
 *
 * Output frame m is evaluated directly from the input history at the exact
 * rational time m * in_rate / out_rate, so the result of a run does not depend
 * on how the input was split into process calls. The filter holds back `half`
 * input frames (its look-ahead), which is what keeps the alignment natural: no
 * output frame is ever dropped to compensate a delay.
 *
 * Deliberately wrong variants of this plugin are selected by one compile
 * definition each, FIXTURE_FAULT_<NAME> (for example FIXTURE_FAULT_DELAY). With
 * none defined the plugin follows the contract. Each deviation sits in one of
 * the small helpers below (fixture_out_frames, fixture_capacity_ok,
 * fixture_emit_frame and the vtable members), so a variant changes one place.
 * The suite must report each variant under the check named in CMakeLists.txt.
 */

#include <math.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#include <mirelia/audio_dsp.h>

/* Half width of the kernel in source frames, per unit of in/out for downsampling. */
#define FIXTURE_TAPS_PER_SIDE 32

static const double kPi = 3.14159265358979323846;

typedef struct fixture_ctx {
    uint32_t in_rate;
    uint32_t out_rate;
    uint16_t channels;
    int64_t half;       /* L: kernel half width in source frames */
    uint64_t phases;    /* distinct fractional offsets: out_rate / gcd(in, out) */
    uint64_t gcd;
    double *kernels;    /* phases * 2 * half taps, each kernel normalised to unit DC gain */
    uint64_t fed;
    uint64_t emitted;
    int flushed;
    float *hist;        /* interleaved source frames [base, base + count) */
    uint64_t base;
    uint64_t count;
    uint64_t hist_cap;  /* in frames */
#ifdef FIXTURE_FAULT_FAIL_MIDSTREAM
    uint64_t calls;     /* process calls in the current run */
#endif
} fixture_ctx;

static uint64_t gcd_u64(uint64_t a, uint64_t b) {
    while (b != 0) {
        uint64_t t = a % b;
        a = b;
        b = t;
    }
    return a;
}

/* out(n) = ceil(n * out_rate / in_rate) */
static uint64_t fixture_out_frames(const fixture_ctx *c, uint64_t n) {
#ifdef FIXTURE_FAULT_FLOOR_LENGTH
    return n * c->out_rate / c->in_rate;
#else
    return (n * c->out_rate + c->in_rate - 1) / c->in_rate;
#endif
}

/* Whether a call may proceed with `capacity` frames when it needs `needed`. */
static int fixture_capacity_ok(uint64_t capacity, uint64_t needed) {
#ifdef FIXTURE_FAULT_IGNORE_CAPACITY
    (void)capacity;
    (void)needed;
    return 1;
#else
    return capacity >= needed;
#endif
}

/* Output frames that can be produced from `fed` frames without zero padding. */
static uint64_t fixture_holdback(const fixture_ctx *c, uint64_t fed) {
    uint64_t half = (uint64_t)c->half;
    return fed > half ? fixture_out_frames(c, fed - half) : 0;
}

static double blackman(double u) {
    return 0.42 + 0.5 * cos(kPi * u) + 0.08 * cos(2.0 * kPi * u);
}

static double sinc(double y) {
    return y == 0.0 ? 1.0 : sin(kPi * y) / (kPi * y);
}

static void fixture_build_kernels(fixture_ctx *c) {
    double cutoff = (double)c->out_rate < (double)c->in_rate
                        ? (double)c->out_rate / (double)c->in_rate
                        : 1.0;
    int64_t taps = 2 * c->half;
    for (uint64_t p = 0; p < c->phases; p++) {
        double frac = (double)(p * c->gcd) / (double)c->out_rate;
        double *k = c->kernels + p * (uint64_t)taps;
        double sum = 0.0;
        for (int64_t j = 0; j < taps; j++) {
            /* Tap j reads source frame n0 - half + 1 + j, at distance x from the output. */
            double x = frac + (double)(c->half - 1 - j);
            k[j] = cutoff * sinc(cutoff * x) * blackman(x / (double)c->half);
            sum += k[j];
        }
        for (int64_t j = 0; j < taps; j++) {
            k[j] /= sum;
        }
    }
}

/* Writes output frame m. Source frames outside [0, fed) are zeros. */
static void fixture_emit_frame(const fixture_ctx *c, uint64_t m, float *out_frame) {
#ifdef FIXTURE_FAULT_DELAY
    /* Frame 0 repeats, so the output is the signal delayed by one frame. */
    m = m > 0 ? m - 1 : 0;
#endif
    uint64_t pos = m * c->in_rate;
#ifdef FIXTURE_FAULT_DRIFT
    /* Scales the time step by 1 + 1e-8. The extra is below one table phase for
     * every short run, so only a run of millions of frames is audibly off. */
    pos += pos / 100000000u;
#endif
    int64_t n0 = (int64_t)(pos / c->out_rate);
    uint64_t phase = (pos % c->out_rate) / c->gcd;
    const double *kernel = c->kernels + phase * (uint64_t)(2 * c->half);
    int64_t first = n0 - c->half + 1;

    for (uint16_t ch = 0; ch < c->channels; ch++) {
        uint16_t src_ch = ch;
#ifdef FIXTURE_FAULT_SWAP_CHANNELS
        src_ch = (uint16_t)(c->channels - 1 - ch);
#endif
        double acc = 0.0;
        for (int64_t j = 0; j < 2 * c->half; j++) {
            int64_t k = first + j;
            if (k >= 0 && (uint64_t)k < c->fed) {
                acc += kernel[j] * (double)c->hist[((uint64_t)k - c->base) * c->channels + src_ch];
            }
        }
        out_frame[ch] = (float)acc;
    }
}

static kn_result fixture_create(uint32_t in_rate, uint32_t out_rate, uint16_t channels,
                                void **out_ctx) {
    fixture_ctx *c = (fixture_ctx *)calloc(1, sizeof(*c));
    if (c == NULL) {
        return KN_ERR_OOM;
    }
    c->in_rate = in_rate;
    c->out_rate = out_rate;
    c->channels = channels;
    double ratio = (double)in_rate / (double)out_rate;
    c->half = (int64_t)ceil(FIXTURE_TAPS_PER_SIDE * (ratio > 1.0 ? ratio : 1.0));
    c->gcd = gcd_u64(in_rate, out_rate);
    c->phases = out_rate / c->gcd;
    c->kernels = (double *)malloc(c->phases * (uint64_t)(2 * c->half) * sizeof(double));
    if (c->kernels == NULL) {
        free(c);
        return KN_ERR_OOM;
    }
    fixture_build_kernels(c);
    *out_ctx = c;
    return KN_OK;
}

static int64_t fixture_process(void *ctx, const float *in, int64_t in_frames, float *out,
                               int64_t out_capacity) {
    fixture_ctx *c = (fixture_ctx *)ctx;
#ifdef FIXTURE_FAULT_FAIL_MIDSTREAM
    if (++c->calls >= 64) {
        return KN_ERR_INTERNAL;
    }
#endif
    if (c->flushed) {
#ifdef FIXTURE_FAULT_PROCESS_AFTER_FLUSH
        return 0;
#else
        return KN_ERR_STATE;
#endif
    }
    if (in_frames < 0 || out_capacity < 0) {
        return KN_ERR_INVALID_ARG;
    }
    uint64_t new_fed = c->fed + (uint64_t)in_frames;
    if (!fixture_capacity_ok((uint64_t)out_capacity,
                             fixture_out_frames(c, new_fed) - c->emitted)) {
        return KN_ERR_INVALID_ARG;
    }

    if (c->count + (uint64_t)in_frames > c->hist_cap) {
        uint64_t cap = c->count + (uint64_t)in_frames;
        float *grown = (float *)realloc(c->hist, cap * c->channels * sizeof(float));
        if (grown == NULL) {
            return KN_ERR_OOM;
        }
        c->hist = grown;
        c->hist_cap = cap;
    }
    if (in_frames > 0) {
        memcpy(c->hist + c->count * c->channels, in,
               (size_t)in_frames * c->channels * sizeof(float));
    }
    c->count += (uint64_t)in_frames;
    c->fed = new_fed;

    uint64_t target = fixture_holdback(c, c->fed);
    int64_t written = 0;
    while (c->emitted < target) {
        fixture_emit_frame(c, c->emitted, out + (size_t)written * c->channels);
        c->emitted++;
        written++;
    }
#ifdef FIXTURE_FAULT_SPLIT
    /* Depends on where the host cut its input into calls. */
    if (written > 0) {
        out[0] += 1e-4f;
    }
#endif

    /* Drop source frames no later output frame reads. */
    uint64_t next = c->emitted;
#ifdef FIXTURE_FAULT_DELAY
    /* The next frame reads the source position of the one before it. */
    next = next > 0 ? next - 1 : 0;
#endif
    int64_t keep_from = (int64_t)(next * c->in_rate / c->out_rate) - c->half + 1;
    if (keep_from > (int64_t)c->base) {
        uint64_t drop = (uint64_t)keep_from - c->base;
        memmove(c->hist, c->hist + drop * c->channels,
                (size_t)(c->count - drop) * c->channels * sizeof(float));
        c->base += drop;
        c->count -= drop;
    }
    return written;
}

static int64_t fixture_flush(void *ctx, float *out, int64_t out_capacity) {
    fixture_ctx *c = (fixture_ctx *)ctx;
    uint64_t pending = fixture_out_frames(c, c->fed) - c->emitted;
    if (out_capacity < 0 || !fixture_capacity_ok((uint64_t)out_capacity, pending)) {
        return KN_ERR_INVALID_ARG;
    }
    int64_t written = 0;
    while (c->emitted < fixture_out_frames(c, c->fed)) {
        fixture_emit_frame(c, c->emitted, out + (size_t)written * c->channels);
        c->emitted++;
        written++;
    }
    c->flushed = 1;
    return written;
}

static int64_t fixture_pending_frames(void *ctx) {
    fixture_ctx *c = (fixture_ctx *)ctx;
#ifdef FIXTURE_FAULT_PENDING_ZERO
    (void)c;
    return 0;
#else
    return (int64_t)(fixture_out_frames(c, c->fed) - c->emitted);
#endif
}

static kn_result fixture_reset(void *ctx) {
    fixture_ctx *c = (fixture_ctx *)ctx;
    c->fed = 0;
    c->emitted = 0;
    c->flushed = 0;
#ifdef FIXTURE_FAULT_FAIL_MIDSTREAM
    c->calls = 0;
#endif
#ifdef FIXTURE_FAULT_RESET_STALE
    /* Keeps the previous run's frames, which the next run then reads as its own
     * first input. base is rewound so the stale frames stay inside the buffer. */
    c->base = 0;
#else
    c->base = 0;
    c->count = 0;
#endif
    return KN_OK;
}

static void fixture_destroy(void *ctx) {
    fixture_ctx *c = (fixture_ctx *)ctx;
    if (c == NULL) {
        return;
    }
    free(c->hist);
    free(c->kernels);
    free(c);
}

static const kn_dsp_resampler_vtable fixture_vtable = {
    fixture_create, fixture_process, fixture_flush,
    fixture_pending_frames, fixture_reset, fixture_destroy,
};

static const kn_dsp_capability fixture_capabilities[] = {
    {KN_DSP_KIND_RESAMPLER, &fixture_vtable},
};

static const kn_dsp_plugin fixture_plugin = {
    KN_DSP_ABI_VERSION, "fixture-sinc", "Reference windowed-sinc fixture", 1,
    fixture_capabilities,
};

KN_DSP_EXPORT const kn_dsp_plugin *kn_dsp_entry(uint32_t abi_version) {
#ifdef FIXTURE_FAULT_BAD_VERSION
    (void)abi_version;
    return &fixture_plugin;
#else
    return abi_version == KN_DSP_ABI_VERSION ? &fixture_plugin : NULL;
#endif
}
