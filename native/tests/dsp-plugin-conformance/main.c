/*
 * Conformance suite for Mirelia DSP resampler plugins.
 *
 * Loads the plugin under test dynamically (path given as argv[1]) and drives it
 * through mireliaDspEntry and the resampler vtable exactly as a host does, so
 * any plugin that passes behaves the same for the engine. No engine is needed.
 * The contract being checked is documented in mirelia/audio_dsp.h.
 *
 * Every failure prints "FAIL [<tag>] <message> (<file>:<line>)". All checks run
 * even after a failure, and the exit code is non-zero if any check failed.
 */

#include <math.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <mirelia/audio_dsp.h>

#ifdef _WIN32
#include <windows.h>
typedef HMODULE LibHandle;
static LibHandle libOpen(const char *path) { return LoadLibraryA(path); }
static void *libSym(LibHandle h, const char *name) {
    return (void *)GetProcAddress(h, name);
}
#else
#include <dlfcn.h>
typedef void *LibHandle;
static LibHandle libOpen(const char *path) { return dlopen(path, RTLD_NOW); }
static void *libSym(LibHandle h, const char *name) { return dlsym(h, name); }
#endif

/* ---- tiny test harness ------------------------------------------------- */

static int failures = 0;

static void reportFail(const char *tag, const char *file, int line, const char *fmt, ...) {
    char msg[256];
    va_list args;
    va_start(args, fmt);
    vsnprintf(msg, sizeof(msg), fmt, args);
    va_end(args);
    printf("FAIL [%s] %s (%s:%d)\n", tag, msg, file, line);
    /* A faulty plugin may crash the process later; the line must already be out. */
    fflush(stdout);
    failures++;
}

#define FAIL(tag, ...) reportFail(tag, __FILE__, __LINE__, __VA_ARGS__)

#define CHECK(tag, cond, ...)         \
    do {                              \
        if (!(cond)) {                \
            FAIL(tag, __VA_ARGS__);   \
        }                             \
    } while (0)

/*
 * A negative return where success was expected is a runtime failure of the
 * plugin, reported under its own tag whatever check hit it. Evaluates to
 * whether the call succeeded, so the caller can stop using the context.
 */
#define RUNTIME_OK(rc, what)                                                       \
    (runtimeOk((int64_t)(rc), what, __FILE__, __LINE__))

static int runtimeOk(int64_t rc, const char *what, const char *file, int line) {
    if (rc < 0) {
        reportFail("runtime", file, line, "%s returned %lld", what, (long long)rc);
        return 0;
    }
    return 1;
}

/*
 * A call that returns more frames than the capacity it was given broke the
 * contract (and wrote out of bounds), reported under `tag`. Evaluates to whether
 * rc is within the capacity, so the caller can stop using the context.
 */
#define WITHIN_CAP(tag, rc, cap, what)                                                 \
    (withinCap(tag, (int64_t)(rc), (int64_t)(cap), what, __FILE__, __LINE__))

static int withinCap(const char *tag, int64_t rc, int64_t cap, const char *what,
                     const char *file, int line) {
    if (rc > cap) {
        reportFail(tag, file, line, "%s returned %lld frames for a capacity of %lld", what,
                   (long long)rc, (long long)cap);
        return 0;
    }
    return 1;
}

static void *xmalloc(size_t n) {
    void *p = malloc(n > 0 ? n : 1);
    if (p == NULL) {
        printf("FAIL [runtime] out of memory in the harness\n");
        exit(2);
    }
    return p;
}

/* ---- the plugin under test ---------------------------------------------- */

static const MireliaDspResamplerVtable *vt;

typedef struct RatePair {
    uint32_t in_rate;
    uint32_t out_rate;
} RatePair;

static const RatePair kPairs[] = {
    {44100, 48000}, {48000, 44100}, {44100, 96000}, {96000, 44100},
    {22050, 48000}, {44100, 88200}, {192000, 48000},
};
#define PAIR_COUNT (sizeof(kPairs) / sizeof(kPairs[0]))

static const uint16_t kChannelCounts[] = {1, 2};
#define CHANNEL_COUNTS (sizeof(kChannelCounts) / sizeof(kChannelCounts[0]))

/* Maximum tolerated difference from the ideal tone. */
static const double kTolerance = 1e-3;
/* Output frames at each end that the alignment and drift checks do not compare. */
#define MARGIN_FRAMES 8000

static const double kPi = 3.14159265358979323846;

/* out(n) = ceil(n * out_rate / in_rate), in exact integer arithmetic. */
static int64_t outFrames(int64_t n, uint32_t in_rate, uint32_t out_rate) {
    return (int64_t)(((uint64_t)n * out_rate + in_rate - 1) / in_rate);
}

/* Deterministic broadband noise in [-0.5, 0.5), interleaved; the generator of the Rust tests. */
static float *makeNoise(int64_t frames, uint16_t channels, uint32_t seed) {
    float *buf = (float *)xmalloc((size_t)(frames * channels) * sizeof(float));
    uint32_t state = seed;
    for (int64_t i = 0; i < frames * channels; i++) {
        state = state * 1664525u + 1013904223u;
        buf[i] = (float)(state >> 8) / (float)(1 << 24) - 0.5f;
    }
    return buf;
}

/*
 * Cosine of amplitude 0.5 at sample k of a stream at `rate`. Computed in double
 * with the phase reduced first, so that long runs keep their precision; k * freq
 * is an exact integer in a double for every length used here.
 */
static double tone(int64_t k, double freq, uint32_t rate) {
    return 0.5 * cos(2.0 * kPi * fmod((double)k * freq, (double)rate) / (double)rate);
}

static double channelFreq(uint16_t channel) { return channel == 0 ? 997.0 : 1499.0; }

/* ---- running a stream through a context --------------------------------- */

/*
 * Feeds `frames` source frames to a fresh (or freshly reset) context in blocks
 * and flushes it. Capacities are exactly what the contract requires. `out`
 * must hold out(frames) frames. Returns the output frame count, or -1 if the
 * plugin returned an error, which has been reported as a runtime failure.
 *
 * blocks/block_count: block sizes used cyclically; NULL means a single call.
 * zero_calls: if set, a zero-frame call follows every second block.
 */
static int64_t runStream(void *ctx, const float *in, int64_t frames, uint16_t channels,
                         uint32_t in_rate, uint32_t out_rate, const int64_t *blocks,
                         size_t block_count, int zero_calls, float *out) {
    int64_t fed = 0;
    int64_t emitted = 0;
    size_t block_index = 0;
    while (fed < frames) {
        int64_t n = blocks != NULL ? blocks[block_index % block_count] : frames;
        if (n > frames - fed) {
            n = frames - fed;
        }
        int64_t cap = outFrames(fed + n, in_rate, out_rate) - emitted;
        int64_t rc = vt->process(ctx, in + fed * channels, n, out + emitted * channels, cap);
        if (!RUNTIME_OK(rc, "process") || !WITHIN_CAP("runtime", rc, cap, "process")) {
            return -1;
        }
        emitted += rc;
        fed += n;
        block_index++;
        if (zero_calls && block_index % 2 == 0) {
            cap = outFrames(fed, in_rate, out_rate) - emitted;
            rc = vt->process(ctx, in, 0, out + emitted * channels, cap);
            if (!RUNTIME_OK(rc, "process with zero frames") ||
                !WITHIN_CAP("runtime", rc, cap, "process with zero frames")) {
                return -1;
            }
            emitted += rc;
        }
    }
    int64_t cap = outFrames(fed, in_rate, out_rate) - emitted;
    int64_t rc = vt->flush(ctx, out + emitted * channels, cap);
    if (!RUNTIME_OK(rc, "flush") || !WITHIN_CAP("runtime", rc, cap, "flush")) {
        return -1;
    }
    return emitted + rc;
}

/* Creates a context, reporting a failure as a runtime one. NULL on failure. */
static void *createCtx(uint32_t in_rate, uint32_t out_rate, uint16_t channels) {
    void *ctx = NULL;
    MireliaResult rc = vt->create(in_rate, out_rate, channels, &ctx);
    if (!RUNTIME_OK(rc, "create") || ctx == NULL) {
        return NULL;
    }
    return ctx;
}

/* Output of a whole run on a fresh context; returns the frame count, or -1. */
static int64_t referenceRun(const float *in, int64_t frames, uint16_t channels,
                            uint32_t in_rate, uint32_t out_rate, float **out) {
    *out = (float *)xmalloc((size_t)(outFrames(frames, in_rate, out_rate) * channels + 1) *
                            sizeof(float));
    void *ctx = createCtx(in_rate, out_rate, channels);
    if (ctx == NULL) {
        return -1;
    }
    int64_t n = runStream(ctx, in, frames, channels, in_rate, out_rate, NULL, 0, 0, *out);
    vt->destroy(ctx);
    return n;
}

/* ---- checks -------------------------------------------------------------- */

static int validId(const char *id) {
    size_t n = id != NULL ? strlen(id) : 0;
    if (n < 1 || n > 64) {
        return 0;
    }
    for (size_t i = 0; i < n; i++) {
        char c = id[i];
        if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' ||
              c == '-')) {
            return 0;
        }
    }
    return 1;
}

static int validUtf8(const unsigned char *s, size_t n) {
    size_t i = 0;
    while (i < n) {
        unsigned char c = s[i];
        size_t extra;
        uint32_t min;
        uint32_t cp;
        if (c < 0x80) {
            i++;
            continue;
        } else if (c >= 0xC2 && c <= 0xDF) {
            extra = 1; min = 0x80; cp = c & 0x1F;
        } else if (c >= 0xE0 && c <= 0xEF) {
            extra = 2; min = 0x800; cp = c & 0x0F;
        } else if (c >= 0xF0 && c <= 0xF4) {
            extra = 3; min = 0x10000; cp = c & 0x07;
        } else {
            return 0;
        }
        if (i + extra >= n) {
            return 0;
        }
        for (size_t j = 1; j <= extra; j++) {
            if ((s[i + j] & 0xC0) != 0x80) {
                return 0;
            }
            cp = (cp << 6) | (s[i + j] & 0x3F);
        }
        if (cp < min || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF)) {
            return 0;
        }
        i += extra + 1;
    }
    return 1;
}

/* Returns the plugin, or NULL if its static contents are too broken to test further. */
static const MireliaDspPlugin *checkEntry(MireliaDspEntryFn entry) {
    CHECK("entry", entry(0xDEAD) == NULL, "mireliaDspEntry(0xDEAD) must return NULL");

    const MireliaDspPlugin *plugin = entry(MIRELIA_DSP_ABI_VERSION);
    if (plugin == NULL) {
        FAIL("entry", "mireliaDspEntry(%d) returned NULL", MIRELIA_DSP_ABI_VERSION);
        return NULL;
    }
    CHECK("entry", plugin->abi_version == MIRELIA_DSP_ABI_VERSION, "abi_version is %u",
          (unsigned)plugin->abi_version);
    CHECK("entry", validId(plugin->id), "id must be 1..64 bytes of [a-z0-9._-]");
    size_t name_len = plugin->name != NULL ? strlen(plugin->name) : 0;
    CHECK("entry",
          name_len >= 1 && name_len <= 128 &&
              validUtf8((const unsigned char *)plugin->name, name_len),
          "name must be 1..128 bytes of UTF-8");
    CHECK("entry", plugin->capability_count >= 1 && plugin->capability_count <= 32,
          "capability_count is %u, expected 1..32", (unsigned)plugin->capability_count);
    if (plugin->capabilities == NULL) {
        FAIL("entry", "capabilities is NULL");
        return NULL;
    }
    if (plugin->capability_count < 1 || plugin->capability_count > 32) {
        return NULL;
    }

    unsigned resamplers = 0;
    for (uint32_t i = 0; i < plugin->capability_count; i++) {
        if (plugin->capabilities[i].kind != MireliaDspKindResampler) {
            continue;
        }
        resamplers++;
        const MireliaDspResamplerVtable *v =
            (const MireliaDspResamplerVtable *)plugin->capabilities[i].vtable;
        if (v == NULL || v->create == NULL || v->process == NULL || v->flush == NULL ||
            v->pending_frames == NULL || v->reset == NULL || v->destroy == NULL) {
            FAIL("entry", "resampler capability %u has a NULL vtable or function pointer",
                 (unsigned)i);
            return NULL;
        }
        if (vt == NULL) {
            vt = v;
        }
    }
    CHECK("entry", resamplers >= 1, "no MireliaDspKindResampler capability");
    return vt != NULL ? plugin : NULL;
}

static void checkCreate(void) {
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            void *ctx = NULL;
            MireliaResult rc = vt->create(kPairs[p].in_rate, kPairs[p].out_rate,
                                          kChannelCounts[c], &ctx);
            CHECK("create", rc == MireliaOk && ctx != NULL,
                  "create(%u -> %u, %u ch) returned %d", (unsigned)kPairs[p].in_rate,
                  (unsigned)kPairs[p].out_rate, (unsigned)kChannelCounts[c], (int)rc);
            if (ctx != NULL) {
                vt->destroy(ctx);
            }
        }
    }
}

static void checkLength(void) {
    static const int64_t kLengths[] = {1, 2, 100, 147, 2058, 12345, 30000, 44100};
    /* A prime block size, so block edges do not line up with the filter's. */
    const int64_t block = 977;
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        uint32_t in_rate = kPairs[p].in_rate;
        uint32_t out_rate = kPairs[p].out_rate;
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            uint16_t ch = kChannelCounts[c];
            for (size_t l = 0; l < sizeof(kLengths) / sizeof(kLengths[0]); l++) {
                int64_t n = kLengths[l];
                float *in = makeNoise(n, ch, 1);
                float *out = (float *)xmalloc((size_t)(outFrames(n, in_rate, out_rate) * ch + 1) *
                                              sizeof(float));
                void *ctx = createCtx(in_rate, out_rate, ch);
                if (ctx != NULL) {
                    int64_t fed = 0;
                    int64_t emitted = 0;
                    int ok = 1;
                    while (ok && fed < n) {
                        int64_t k = n - fed < block ? n - fed : block;
                        int64_t cap = outFrames(fed + k, in_rate, out_rate) - emitted;
                        int64_t rc = vt->process(ctx, in + fed * ch, k, out + emitted * ch, cap);
                        /* rc <= cap is the same as emitted <= out(fed) after this call. */
                        ok = RUNTIME_OK(rc, "process") && WITHIN_CAP("length", rc, cap, "process");
                        if (!ok) {
                            break;
                        }
                        emitted += rc;
                        fed += k;
                    }
                    if (ok) {
                        int64_t cap = outFrames(fed, in_rate, out_rate) - emitted;
                        int64_t rc = vt->flush(ctx, out + emitted * ch, cap);
                        if (RUNTIME_OK(rc, "flush") && WITHIN_CAP("length", rc, cap, "flush")) {
                            emitted += rc;
                            CHECK("length", emitted == outFrames(n, in_rate, out_rate),
                                  "%u -> %u, %u ch, N=%lld: total output %lld, expected %lld",
                                  (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch,
                                  (long long)n, (long long)emitted,
                                  (long long)outFrames(n, in_rate, out_rate));
                        }
                    }
                    vt->destroy(ctx);
                }
                free(in);
                free(out);
            }
        }
    }
}

static void checkAlignment(void) {
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        uint32_t in_rate = kPairs[p].in_rate;
        uint32_t out_rate = kPairs[p].out_rate;
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            uint16_t ch = kChannelCounts[c];
            int64_t n = in_rate; /* one second */
            float *in = (float *)xmalloc((size_t)(n * ch) * sizeof(float));
            for (int64_t k = 0; k < n; k++) {
                for (uint16_t i = 0; i < ch; i++) {
                    in[k * ch + i] = (float)tone(k, channelFreq(i), in_rate);
                }
            }
            float *out = (float *)xmalloc((size_t)(outFrames(n, in_rate, out_rate) * ch + 1) *
                                          sizeof(float));
            void *ctx = createCtx(in_rate, out_rate, ch);
            if (ctx != NULL) {
                const int64_t blocks[] = {4096};
                int64_t total = runStream(ctx, in, n, ch, in_rate, out_rate, blocks, 1, 0, out);
                vt->destroy(ctx);
                int64_t expected = outFrames(n, in_rate, out_rate);
                /* A wrong total length is the length check's finding, not this one's. */
                if (total > expected) {
                    total = expected;
                }
                /* A negative total is a runtime failure that runStream has reported. */
                if (total >= 0) {
                    double worst = 0.0;
                    int64_t worst_at = 0;
                    for (int64_t m = MARGIN_FRAMES; m < total - MARGIN_FRAMES; m++) {
                        for (uint16_t i = 0; i < ch; i++) {
                            double err = fabs((double)out[m * ch + i] -
                                              tone(m, channelFreq(i), out_rate));
                            if (err > worst) {
                                worst = err;
                                worst_at = m;
                            }
                        }
                    }
                    if (total > 2 * MARGIN_FRAMES) {
                        printf("info [alignment] %u -> %u, %u ch: worst error %.3g (output frame %lld)\n",
                               (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, worst,
                               (long long)worst_at);
                    }
                    CHECK("alignment", total > 2 * MARGIN_FRAMES && worst <= kTolerance,
                          "%u -> %u, %u ch: worst error %.3g at output frame %lld exceeds %.0e",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, worst,
                          (long long)worst_at, kTolerance);
                }
            }
            free(in);
            free(out);
        }
    }
}

static void checkSplit(void) {
    static const int64_t kBlocks[] = {1, 7, 4096, 333, 2058, 5, 1000, 64};
    const int64_t n = 20000;
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        uint32_t in_rate = kPairs[p].in_rate;
        uint32_t out_rate = kPairs[p].out_rate;
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            uint16_t ch = kChannelCounts[c];
            float *in = makeNoise(n, ch, 0x2545f491u);
            float *whole = NULL;
            int64_t whole_n = referenceRun(in, n, ch, in_rate, out_rate, &whole);
            float *split = (float *)xmalloc((size_t)(outFrames(n, in_rate, out_rate) * ch + 1) *
                                            sizeof(float));
            void *ctx = createCtx(in_rate, out_rate, ch);
            if (ctx != NULL && whole_n >= 0) {
                int64_t split_n = runStream(ctx, in, n, ch, in_rate, out_rate, kBlocks,
                                            sizeof(kBlocks) / sizeof(kBlocks[0]), 1, split);
                if (split_n >= 0) {
                    CHECK("split",
                          split_n == whole_n &&
                              memcmp(whole, split, (size_t)(whole_n * ch) * sizeof(float)) == 0,
                          "%u -> %u, %u ch: split input gives different output (%lld vs %lld frames)",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch,
                          (long long)split_n, (long long)whole_n);
                }
            }
            if (ctx != NULL) {
                vt->destroy(ctx);
            }
            free(in);
            free(whole);
            free(split);
        }
    }
}

static void checkPending(void) {
    static const int64_t kBlocks[] = {1, 100, 0, 5000, 7, 0, 333};
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        uint32_t in_rate = kPairs[p].in_rate;
        uint32_t out_rate = kPairs[p].out_rate;
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            uint16_t ch = kChannelCounts[c];
            int64_t total_in = 0;
            for (size_t b = 0; b < sizeof(kBlocks) / sizeof(kBlocks[0]); b++) {
                total_in += kBlocks[b];
            }
            float *in = makeNoise(total_in, ch, 3);
            float *out = (float *)xmalloc((size_t)(outFrames(total_in, in_rate, out_rate) * ch + 1) *
                                          sizeof(float));
            void *ctx = createCtx(in_rate, out_rate, ch);
            if (ctx != NULL) {
                int64_t pending = vt->pending_frames(ctx);
                int ok = RUNTIME_OK(pending, "pending_frames");
                if (ok) {
                    CHECK("pending", pending == 0,
                          "%u -> %u, %u ch: %lld pending on a fresh context", (unsigned)in_rate,
                          (unsigned)out_rate, (unsigned)ch, (long long)pending);
                }

                int64_t fed = 0;
                int64_t emitted = 0;
                for (size_t b = 0; ok && b < sizeof(kBlocks) / sizeof(kBlocks[0]); b++) {
                    int64_t cap = outFrames(fed + kBlocks[b], in_rate, out_rate) - emitted;
                    int64_t rc = vt->process(ctx, in + fed * ch, kBlocks[b], out + emitted * ch,
                                             cap);
                    ok = RUNTIME_OK(rc, "process") && WITHIN_CAP("runtime", rc, cap, "process");
                    if (!ok) {
                        break;
                    }
                    emitted += rc;
                    fed += kBlocks[b];
                    pending = vt->pending_frames(ctx);
                    ok = RUNTIME_OK(pending, "pending_frames");
                    if (!ok) {
                        break;
                    }
                    int64_t expected = outFrames(fed, in_rate, out_rate) - emitted;
                    CHECK("pending", pending == expected,
                          "%u -> %u, %u ch: pending_frames is %lld after %lld fed, expected %lld",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch,
                          (long long)pending, (long long)fed, (long long)expected);
                    CHECK("pending", fed == 0 || pending > 0,
                          "%u -> %u, %u ch: nothing pending after %lld fed and not flushed",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, (long long)fed);
                }
                if (ok) {
                    int64_t cap = outFrames(fed, in_rate, out_rate) - emitted;
                    int64_t rc = vt->flush(ctx, out + emitted * ch, cap);
                    /* After a failed call only destroy is valid on the context. */
                    ok = RUNTIME_OK(rc, "flush") && WITHIN_CAP("runtime", rc, cap, "flush");
                    if (ok) {
                        pending = vt->pending_frames(ctx);
                        ok = RUNTIME_OK(pending, "pending_frames");
                    }
                    if (ok) {
                        CHECK("pending", pending == 0,
                              "%u -> %u, %u ch: %lld pending after flush", (unsigned)in_rate,
                              (unsigned)out_rate, (unsigned)ch, (long long)pending);
                        MireliaResult rr = vt->reset(ctx);
                        if (RUNTIME_OK(rr, "reset")) {
                            pending = vt->pending_frames(ctx);
                            if (RUNTIME_OK(pending, "pending_frames")) {
                                CHECK("pending", pending == 0,
                                      "%u -> %u, %u ch: %lld pending after reset",
                                      (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch,
                                      (long long)pending);
                            }
                        }
                    }
                }
                vt->destroy(ctx);
            }
            free(in);
            free(out);
        }
    }
}

static void checkReset(void) {
    const int64_t n_a = 3000;
    const int64_t n_b = 5000;
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        uint32_t in_rate = kPairs[p].in_rate;
        uint32_t out_rate = kPairs[p].out_rate;
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            uint16_t ch = kChannelCounts[c];
            float *in_a = makeNoise(n_a, ch, 11);
            float *in_b = makeNoise(n_b, ch, 22);
            float *reference = NULL;
            int64_t ref_n = referenceRun(in_b, n_b, ch, in_rate, out_rate, &reference);
            float *scratch = (float *)xmalloc(
                (size_t)(outFrames(n_a > n_b ? n_a : n_b, in_rate, out_rate) * ch + 1) *
                sizeof(float));
            float *got = (float *)xmalloc((size_t)(outFrames(n_b, in_rate, out_rate) * ch + 1) *
                                          sizeof(float));
            void *ctx = createCtx(in_rate, out_rate, ch);
            if (ctx != NULL && ref_n >= 0) {
                /* 0: fresh, 1: mid-run (unflushed), 2: flushed. */
                for (int state = 0; state < 3; state++) {
                    int ok = 1;
                    if (state > 0) {
                        /* The previous round left the context flushed. */
                        ok = RUNTIME_OK(vt->reset(ctx), "reset");
                    }
                    if (ok && state == 1) {
                        int64_t cap = outFrames(n_a - 1000, in_rate, out_rate);
                        int64_t rc = vt->process(ctx, in_a, n_a - 1000, scratch, cap);
                        ok = RUNTIME_OK(rc, "process") && WITHIN_CAP("runtime", rc, cap, "process");
                    } else if (ok && state == 2) {
                        ok = runStream(ctx, in_a, n_a, ch, in_rate, out_rate, NULL, 0, 0,
                                       scratch) >= 0;
                    }
                    if (!ok || !RUNTIME_OK(vt->reset(ctx), "reset")) {
                        break;
                    }
                    int64_t got_n = runStream(ctx, in_b, n_b, ch, in_rate, out_rate, NULL, 0, 0,
                                              got);
                    if (got_n < 0) {
                        break;
                    }
                    CHECK("reset",
                          got_n == ref_n &&
                              memcmp(got, reference, (size_t)(ref_n * ch) * sizeof(float)) == 0,
                          "%u -> %u, %u ch: output after reset from state %d differs from a fresh context",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, state);
                    /* Leave the context flushed, so the next round resets from there. */
                }
            }
            if (ctx != NULL) {
                vt->destroy(ctx);
            }
            free(in_a);
            free(in_b);
            free(reference);
            free(scratch);
            free(got);
        }
    }
}

static void checkState(void) {
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        uint32_t in_rate = kPairs[p].in_rate;
        uint32_t out_rate = kPairs[p].out_rate;
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            uint16_t ch = kChannelCounts[c];
            const int64_t n = 500;
            float *in = makeNoise(n, ch, 5);
            /* Room for any call below: two runs of n frames. */
            float *out = (float *)xmalloc((size_t)(outFrames(2 * n, in_rate, out_rate) * ch + 1) *
                                          sizeof(float));
            void *ctx = createCtx(in_rate, out_rate, ch);
            if (ctx != NULL) {
                if (runStream(ctx, in, n, ch, in_rate, out_rate, NULL, 0, 0, out) >= 0) {
                    int64_t rc = vt->process(ctx, in, n, out, outFrames(n, in_rate, out_rate));
                    CHECK("state", rc == MireliaErrState,
                          "%u -> %u, %u ch: process after flush returned %lld, expected MireliaErrState",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, (long long)rc);
                    rc = vt->flush(ctx, out, outFrames(n, in_rate, out_rate));
                    CHECK("state", rc == 0,
                          "%u -> %u, %u ch: second flush returned %lld, expected 0",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, (long long)rc);
                    if (RUNTIME_OK(vt->reset(ctx), "reset")) {
                        int64_t cap = outFrames(n, in_rate, out_rate);
                        rc = vt->process(ctx, in, n, out, cap);
                        if (RUNTIME_OK(rc, "process after reset")) {
                            WITHIN_CAP("runtime", rc, cap, "process after reset");
                        }
                    }
                }
                vt->destroy(ctx);
            }
            free(in);
            free(out);
        }
    }
}

static void checkCapacity(void) {
    const int64_t n = 1000;
    const int64_t guard = 64;
    for (size_t p = 0; p < PAIR_COUNT; p++) {
        uint32_t in_rate = kPairs[p].in_rate;
        uint32_t out_rate = kPairs[p].out_rate;
        for (size_t c = 0; c < CHANNEL_COUNTS; c++) {
            uint16_t ch = kChannelCounts[c];
            float *in = makeNoise(n, ch, 9);
            float *reference = NULL;
            int64_t ref_n = referenceRun(in, n, ch, in_rate, out_rate, &reference);
            int64_t required = outFrames(n, in_rate, out_rate);
            /* Output buffer of exactly the run's size, followed by a guard region. */
            size_t total_samples = (size_t)((required + guard) * ch);
            float *buf = (float *)xmalloc(total_samples * sizeof(float));
            memset(buf, 0xA5, total_samples * sizeof(float));
            unsigned char *sentinel = (unsigned char *)xmalloc(total_samples * sizeof(float));
            memset(sentinel, 0xA5, total_samples * sizeof(float));
            void *ctx = createCtx(in_rate, out_rate, ch);
            if (ctx != NULL && ref_n >= 0) {
                /* One frame short: rejected, nothing written beyond the capacity, nothing consumed. */
                int64_t rc = vt->process(ctx, in, n, buf, required - 1);
                /* Any other result leaves the context in a state unknown here, so stop. */
                int ok = rc == MireliaErrInvalidArg;
                CHECK("capacity", ok,
                      "%u -> %u, %u ch: process with a capacity one frame short returned %lld",
                      (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, (long long)rc);
                CHECK("capacity",
                      memcmp((char *)buf + (size_t)((required - 1) * ch) * sizeof(float),
                             sentinel, (size_t)((guard + 1) * ch) * sizeof(float)) == 0,
                      "%u -> %u, %u ch: process wrote beyond a short capacity",
                      (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch);

                int64_t emitted = 0;
                int64_t pending = 0;
                if (ok) {
                    /* The same call with the exact capacity then behaves as on a fresh context. */
                    rc = vt->process(ctx, in, n, buf, required);
                    ok = RUNTIME_OK(rc, "process") && WITHIN_CAP("runtime", rc, required, "process");
                }
                if (ok) {
                    emitted = rc;
                    pending = required - emitted;
                    /* The same holds for flush. */
                    if (pending > 0) {
                        rc = vt->flush(ctx, buf + emitted * ch, pending - 1);
                        ok = rc == MireliaErrInvalidArg;
                        CHECK("capacity", ok,
                              "%u -> %u, %u ch: flush with a capacity one frame short returned %lld",
                              (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch, (long long)rc);
                    }
                }
                if (ok) {
                    rc = vt->flush(ctx, buf + emitted * ch, pending);
                    if (RUNTIME_OK(rc, "flush") && WITHIN_CAP("runtime", rc, pending, "flush")) {
                        emitted += rc;
                        CHECK("capacity",
                              emitted == ref_n &&
                                  memcmp(buf, reference, (size_t)(ref_n * ch) * sizeof(float)) ==
                                      0,
                              "%u -> %u, %u ch: output after a rejected call differs from the reference",
                              (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch);
                    }
                    CHECK("capacity",
                          memcmp((char *)buf + (size_t)(required * ch) * sizeof(float),
                                 sentinel + (size_t)(required * ch) * sizeof(float),
                                 (size_t)(guard * ch) * sizeof(float)) == 0,
                          "%u -> %u, %u ch: output overran an exactly sized buffer",
                          (unsigned)in_rate, (unsigned)out_rate, (unsigned)ch);
                }
            }
            if (ctx != NULL) {
                vt->destroy(ctx);
            }
            free(in);
            free(reference);
            free(buf);
            free(sentinel);
        }
    }
}

static void checkDrift(void) {
    const uint32_t in_rate = 44100;
    const uint32_t out_rate = 48000;
    const int64_t total_in = 10000000;
    const int64_t block = 65536;
    const int64_t total_out = outFrames(total_in, in_rate, out_rate);
    const int64_t window_end = total_out - MARGIN_FRAMES;
    const int64_t window_start = window_end - 16000;

    float *in = (float *)xmalloc((size_t)block * sizeof(float));
    int64_t out_cap = outFrames(block, in_rate, out_rate) + 2;
    float *out = (float *)xmalloc((size_t)out_cap * sizeof(float));
    void *ctx = createCtx(in_rate, out_rate, 1);
    if (ctx == NULL) {
        free(in);
        free(out);
        return;
    }

    int64_t fed = 0;
    int64_t emitted = 0;
    double worst = 0.0;
    int64_t compared = 0;
    int ok = 1;
    while (ok && fed < total_in + 1) {
        /* The extra iteration at fed == total_in is the flush. */
        int64_t n = total_in - fed < block ? total_in - fed : block;
        int64_t rc;
        int64_t cap = outFrames(fed + n, in_rate, out_rate) - emitted;
        /* A plugin may hold output back, so the capacity passed can exceed one block's worth. */
        if (cap > out_cap) {
            free(out);
            out = (float *)xmalloc((size_t)cap * sizeof(float));
            out_cap = cap;
        }
        if (fed < total_in) {
            for (int64_t i = 0; i < n; i++) {
                in[i] = (float)tone(fed + i, 997.0, in_rate);
            }
            rc = vt->process(ctx, in, n, out, cap);
        } else {
            rc = vt->flush(ctx, out, cap);
        }
        ok = RUNTIME_OK(rc, fed < total_in ? "process" : "flush") &&
             WITHIN_CAP("runtime", rc, cap, fed < total_in ? "process" : "flush");
        if (!ok) {
            break;
        }
        for (int64_t i = 0; i < rc; i++) {
            int64_t m = emitted + i;
            if (m >= window_start && m < window_end) {
                double err = fabs((double)out[i] - tone(m, 997.0, out_rate));
                if (err > worst) {
                    worst = err;
                }
                compared++;
            }
        }
        emitted += rc;
        if (fed >= total_in) {
            break;
        }
        fed += n;
    }
    if (ok) {
        printf("info [drift] 44100 -> 48000, %lld frames: worst error %.3g over %lld compared frames\n",
               (long long)total_in, worst, (long long)compared);
        CHECK("drift", compared == window_end - window_start && worst <= kTolerance,
              "worst error %.3g over %lld frames (expected %lld) exceeds %.0e", worst,
              (long long)compared, (long long)(window_end - window_start), kTolerance);
    }
    vt->destroy(ctx);
    free(in);
    free(out);
}

/* ---- main --------------------------------------------------------------- */

static void runCheck(const char *tag, void (*check)(void)) {
    int before = failures;
    check();
    if (failures == before) {
        printf("ok   [%s]\n", tag);
    }
}

int main(int argc, char **argv) {
    if (argc < 2) {
        fprintf(stderr, "usage: %s <plugin library>\n", argv[0]);
        return 2;
    }

    LibHandle lib = libOpen(argv[1]);
    if (lib == NULL) {
        FAIL("entry", "cannot load %s", argv[1]);
        return 1;
    }
    MireliaDspEntryFn entry = (MireliaDspEntryFn)libSym(lib, "mireliaDspEntry");
    if (entry == NULL) {
        FAIL("entry", "mireliaDspEntry is not exported");
        return 1;
    }

    int before = failures;
    const MireliaDspPlugin *plugin = checkEntry(entry);
    if (failures == before) {
        printf("ok   [entry]\n");
    }
    if (plugin == NULL) {
        return 1;
    }
    printf("plugin %s (%s)\n", plugin->id, plugin->name);

    runCheck("create", checkCreate);
    runCheck("length", checkLength);
    runCheck("alignment", checkAlignment);
    runCheck("split", checkSplit);
    runCheck("pending", checkPending);
    runCheck("reset", checkReset);
    runCheck("state", checkState);
    runCheck("capacity", checkCapacity);
    runCheck("drift", checkDrift);

    if (failures == 0) {
        printf("all checks passed\n");
        return 0;
    }
    printf("%d check(s) failed\n", failures);
    return 1;
}
