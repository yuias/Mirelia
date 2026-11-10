/*
 * ABI conformance suite for Mirelia audio backends.
 *
 * Loads the backend under test dynamically (path given as argv[1]) so the
 * same binary validates audio-core-rs, audio-core-cpp, or any future
 * implementation. Any backend must pass identically.
 *
 * Covered: symbol presence, version negotiation, lifecycle, argument
 * validation, the idle position/event contract, device open/close, probe,
 * and DSP plugin registration. Gapless and RT-thread allocation checks are
 * still to be added.
 */

#include <stdio.h>
#include <string.h>

#include <mirelia/audio_core.h>

#ifdef _WIN32
#include <windows.h>
typedef HMODULE LibHandle;
static LibHandle libOpen(const char *path) { return LoadLibraryA(path); }
static void *libSym(LibHandle h, const char *name) {
    return (void *)GetProcAddress(h, name);
}
static int makeAbsolute(const char *path, char *out, size_t size) {
    DWORD n = GetFullPathNameA(path, (DWORD)size, out, NULL);
    return n > 0 && n < size;
}
#else
#include <dlfcn.h>
#include <unistd.h>
typedef void *LibHandle;
static LibHandle libOpen(const char *path) { return dlopen(path, RTLD_NOW); }
static void *libSym(LibHandle h, const char *name) { return dlsym(h, name); }
/* Unlike realpath, this also works for a file that does not exist. */
static int makeAbsolute(const char *path, char *out, size_t size) {
    if (path[0] == '/') {
        return snprintf(out, size, "%s", path) < (int)size;
    }
    char cwd[1024];
    if (getcwd(cwd, sizeof(cwd)) == NULL) {
        return 0;
    }
    return snprintf(out, size, "%s/%s", cwd, path) < (int)size;
}
#endif

/* ---- resolved backend entry points ------------------------------------ */

typedef kn_result (*EngineCreateFn)(const kn_engine_config *, kn_engine **);
typedef void (*EngineDestroyFn)(kn_engine *);
typedef const char *(*EngineLastErrorFn)(kn_engine *);
typedef kn_result (*GetPositionFn)(kn_engine *, kn_position *);
typedef size_t (*PollEventFn)(kn_engine *, kn_event *, size_t);
typedef kn_result (*DevicesEnumerateFn)(kn_engine *, const kn_device_info **, size_t *);
typedef kn_result (*OpenDeviceFn)(kn_engine *, const char *, kn_share_mode,
                                  const kn_stream_format *, kn_stream_format *);
typedef kn_result (*CloseDeviceFn)(kn_engine *);
typedef kn_result (*ProbeFn)(kn_engine *, const char *, kn_probe_result *);
typedef kn_result (*RegisterDspLibraryFn)(kn_engine *, const char *);
typedef kn_result (*DspEnumerateFn)(kn_engine *, const kn_dsp_info **, size_t *);
typedef void (*DspFreeFn)(kn_engine *);
typedef kn_result (*EngineSetResamplerFn)(kn_engine *, const char *);

static EngineCreateFn p_create;
static EngineDestroyFn p_destroy;
static EngineLastErrorFn p_last_error;
static GetPositionFn p_get_position;
static PollEventFn p_poll_event;
static DevicesEnumerateFn p_devices_enumerate;
static OpenDeviceFn p_open_device;
static CloseDeviceFn p_close_device;
static ProbeFn p_probe;
static RegisterDspLibraryFn p_register_dsp_library;
static DspEnumerateFn p_dsp_enumerate;
static DspFreeFn p_dsp_free;
static EngineSetResamplerFn p_engine_set_resampler;

/* ---- tiny test harness ------------------------------------------------- */

static int failures = 0;

#define CHECK(cond, msg)                                       \
    do {                                                       \
        if (cond) {                                            \
            printf("ok   %s\n", msg);                          \
        } else {                                               \
            printf("FAIL %s (%s:%d)\n", msg, __FILE__, __LINE__); \
            failures++;                                        \
        }                                                      \
    } while (0)

static kn_engine_config defaultConfig(void) {
    kn_engine_config cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.abi_version = KN_ABI_VERSION;
    cfg.allow_resample = 1;
    cfg.dither_to_int = 1;
    return cfg;
}

/* ---- tests ------------------------------------------------------------- */

static void testVersionNegotiation(void) {
    kn_engine_config cfg = defaultConfig();
    cfg.abi_version = 0xDEAD;
    kn_engine *e = NULL;
    CHECK(p_create(&cfg, &e) == KN_ERR_ABI_MISMATCH,
          "create rejects unknown ABI version with KN_ERR_ABI_MISMATCH");
    CHECK(e == NULL, "engine stays null on ABI mismatch");
    const char *msg = p_last_error(NULL);
    CHECK(msg != NULL && msg[0] != '\0', "last_error carries detail after mismatch");
}

static void testArgumentValidation(void) {
    CHECK(p_create(NULL, NULL) == KN_ERR_INVALID_ARG,
          "create rejects null arguments with KN_ERR_INVALID_ARG");
}

static void testLifecycleAndIdleContract(void) {
    kn_engine_config cfg = defaultConfig();
    kn_engine *e = NULL;
    CHECK(p_create(&cfg, &e) == KN_OK, "create succeeds for ABI v1");
    CHECK(e != NULL, "engine handle is non-null");
    if (e == NULL) {
        return;
    }

    kn_position pos;
    memset(&pos, 0xFF, sizeof(pos));
    CHECK(p_get_position(e, &pos) == KN_OK, "get_position succeeds");
    CHECK(pos.state == KN_STATE_IDLE, "fresh engine is idle");
    CHECK(pos.total_frames == -1, "total_frames is -1 when nothing is loaded");
    CHECK(pos.user_token == 0, "user_token is 0 when nothing is audible");

    kn_position later;
    CHECK(p_get_position(e, &later) == KN_OK, "second get_position succeeds");
    CHECK(later.host_time_ns >= pos.host_time_ns, "host_time_ns is monotonic");

    kn_event events[8];
    CHECK(p_poll_event(e, events, 8) == 0, "fresh engine has no queued events");

    const kn_device_info *devices = NULL;
    size_t count = (size_t)-1;
    kn_result rc = p_devices_enumerate(e, &devices, &count);
    CHECK(rc == KN_OK || rc == KN_ERR_UNSUPPORTED,
          "devices_enumerate returns KN_OK or KN_ERR_UNSUPPORTED");
    if (rc == KN_OK) {
        CHECK(count != (size_t)-1, "devices_enumerate writes a count");
    }

    p_destroy(e);
    p_destroy(NULL); /* must be a harmless no-op */
    printf("ok   destroy(NULL) is a no-op\n");
}

static void testDitherModes(void) {
    /* 0 = off, 1 = always (unchanged), 2 = resampled tracks only. */
    for (int mode = 0; mode <= 2; mode++) {
        kn_engine_config cfg = defaultConfig();
        cfg.dither_to_int = mode;
        kn_engine *e = NULL;
        char msg[64];
        snprintf(msg, sizeof(msg), "create succeeds with dither_to_int = %d", mode);
        CHECK(p_create(&cfg, &e) == KN_OK && e != NULL, msg);
        if (e != NULL) {
            p_destroy(e);
        }
    }
}

static void testProbe(const char *fixtures_dir) {
    char path[1024];
    snprintf(path, sizeof(path), "%s/fixture.flac", fixtures_dir);

    kn_engine_config cfg = defaultConfig();
    kn_engine *e = NULL;
    if (p_create(&cfg, &e) != KN_OK) {
        CHECK(0, "probe: engine creation failed");
        return;
    }

    kn_probe_result probe;
    memset(&probe, 0, sizeof(probe));
    CHECK(p_probe(e, path, &probe) == KN_OK, "probe succeeds on FLAC fixture");
    CHECK(probe.sample_rate == 44100, "probe reports 44100 Hz");
    CHECK(probe.channels == 2, "probe reports 2 channels");
    CHECK(probe.total_frames == 17640, "probe reports exact frame count");
    CHECK(strcmp(probe.codec, "flac") == 0, "probe reports codec 'flac'");

    kn_probe_result missing;
    CHECK(p_probe(e, "no-such-file.flac", &missing) == KN_ERR_IO,
          "probe reports KN_ERR_IO for a missing file");

    p_destroy(e);
}

static void testDeviceOpen(void) {
    kn_engine_config cfg = defaultConfig();
    kn_engine *e = NULL;
    if (p_create(&cfg, &e) != KN_OK) {
        CHECK(0, "device: engine creation failed");
        return;
    }

    kn_stream_format actual;
    memset(&actual, 0, sizeof(actual));
    kn_result rc = p_open_device(e, NULL, KN_SHARE_SHARED, NULL, &actual);
    if (rc == KN_OK) {
        CHECK(actual.sample_rate > 0, "opened device reports a sample rate");
        CHECK(actual.channels > 0, "opened device reports channels");
        CHECK(p_close_device(e) == KN_OK, "close_device succeeds");
    } else if (rc == KN_ERR_DEVICE_LOST || rc == KN_ERR_UNSUPPORTED) {
        /* CI runners have no audio endpoint; that must fail cleanly, not crash. */
        printf("skip open_device: no usable device here (%d)\n", (int)rc);
    } else {
        CHECK(0, "open_device returned an unexpected error");
    }

    /* Rejected at the ABI boundary, so this needs no audio endpoint. */
    kn_stream_format zero_rate = {0, 2, KN_FMT_F32, 0};
    CHECK(p_open_device(e, NULL, KN_SHARE_SHARED, &zero_rate, &actual) == KN_ERR_INVALID_ARG,
          "open_device rejects a desired sample rate of 0 with KN_ERR_INVALID_ARG");

    /* channels == 0 fixes the rate only; the stream opens at the first load. */
    kn_stream_format fixed_rate = {48000, 0, KN_FMT_F32, 0};
    memset(&actual, 0, sizeof(actual));
    rc = p_open_device(e, NULL, KN_SHARE_SHARED, &fixed_rate, &actual);
    if (rc == KN_OK) {
        CHECK(actual.sample_rate == 48000, "open_device with a fixed rate reports that rate");
    } else if (rc == KN_ERR_DEVICE_LOST || rc == KN_ERR_UNSUPPORTED) {
        printf("skip fixed-rate open_device: no usable device here (%d)\n", (int)rc);
    } else {
        CHECK(0, "fixed-rate open_device returned an unexpected error");
    }

    p_destroy(e);
}

static void testDspRegistration(const char *backend_path, const char *plugin_path) {
    kn_engine_config cfg = defaultConfig();
    kn_engine *e = NULL;
    if (p_create(&cfg, &e) != KN_OK) {
        CHECK(0, "dsp: engine creation failed");
        return;
    }

    CHECK(p_register_dsp_library(e, NULL) == KN_ERR_INVALID_ARG,
          "register_dsp_library rejects a null path with KN_ERR_INVALID_ARG");

    kn_result rc = p_register_dsp_library(e, "relative-plugin.dll");
    if (rc == KN_ERR_UNSUPPORTED) {
        printf("skip dsp registration tests: plugins are not implemented here\n");
        p_destroy(e);
        return;
    }
    CHECK(rc == KN_ERR_INVALID_ARG,
          "register_dsp_library rejects a relative path with KN_ERR_INVALID_ARG");

    char missing[1024];
    if (makeAbsolute("no-such-plugin.dll", missing, sizeof(missing))) {
        CHECK(p_register_dsp_library(e, missing) == KN_ERR_IO,
              "register_dsp_library reports KN_ERR_IO for a missing file");
    } else {
        CHECK(0, "dsp: cannot make a missing path absolute");
    }

    char backend[1024];
    if (makeAbsolute(backend_path, backend, sizeof(backend))) {
        CHECK(p_register_dsp_library(e, backend) == KN_ERR_FORMAT,
              "register_dsp_library reports KN_ERR_FORMAT for a library without kn_dsp_entry");
    } else {
        CHECK(0, "dsp: cannot make the backend path absolute");
    }

    const kn_dsp_info *infos = NULL;
    size_t count = (size_t)-1;
    CHECK(p_dsp_enumerate(e, &infos, &count) == KN_OK && count == 0,
          "dsp_enumerate lists nothing before any registration");
    CHECK(p_engine_set_resampler(e, "no-such-plugin") == KN_ERR_INVALID_ARG,
          "engine_set_resampler rejects an unregistered id with KN_ERR_INVALID_ARG");
    CHECK(p_engine_set_resampler(e, NULL) == KN_OK,
          "engine_set_resampler accepts NULL for the built-in resampler");

    if (plugin_path == NULL) {
        printf("skip dsp plugin tests: no plugin path given\n");
        p_destroy(e);
        return;
    }

    char plugin[1024];
    if (!makeAbsolute(plugin_path, plugin, sizeof(plugin))) {
        CHECK(0, "dsp: cannot make the plugin path absolute");
        p_destroy(e);
        return;
    }
    CHECK(p_register_dsp_library(e, plugin) == KN_OK, "register_dsp_library registers the plugin");
    CHECK(p_dsp_enumerate(e, &infos, &count) == KN_OK && count == 1,
          "dsp_enumerate lists the registered plugin");
    if (count == 1) {
        CHECK(infos[0].kind == KN_DSP_KIND_RESAMPLER, "enumerated plugin is a resampler");
        CHECK(infos[0].id != NULL && infos[0].id[0] != '\0' &&
                  infos[0].name != NULL && infos[0].name[0] != '\0',
              "enumerated plugin has an id and a name");

        char id[128];
        snprintf(id, sizeof(id), "%s", infos[0].id);
        CHECK(p_register_dsp_library(e, plugin) == KN_ERR_STATE,
              "register_dsp_library rejects a second plugin with the same id");
        CHECK(p_engine_set_resampler(e, id) == KN_OK, "engine_set_resampler accepts the id");
        CHECK(p_engine_set_resampler(e, "no-such-plugin") == KN_ERR_INVALID_ARG,
              "engine_set_resampler still rejects an unregistered id");
        CHECK(p_engine_set_resampler(e, NULL) == KN_OK,
              "engine_set_resampler accepts NULL after a plugin was selected");
        CHECK(p_engine_set_resampler(e, id) == KN_OK, "engine_set_resampler selects the id again");
    }

    p_dsp_free(e);
    p_dsp_free(NULL); /* must be a harmless no-op */
    printf("ok   dsp_free(NULL) is a no-op\n");
    p_destroy(e); /* unloads the plugin library after the selection is released */
}

/* ---- main -------------------------------------------------------------- */

int main(int argc, char **argv) {
    if (argc < 2 || argc > 4) {
        fprintf(stderr,
                "usage: abi-conformance <path-to-backend-library> [fixtures-dir] "
                "[dsp-plugin-library]\n");
        return 2;
    }

    LibHandle lib = libOpen(argv[1]);
    if (!lib) {
        fprintf(stderr, "cannot load backend: %s\n", argv[1]);
        return 2;
    }

    struct {
        const char *name;
        void **slot;
    } symbols[] = {
        {"kn_engine_create", (void **)&p_create},
        {"kn_engine_destroy", (void **)&p_destroy},
        {"kn_engine_last_error", (void **)&p_last_error},
        {"kn_get_position", (void **)&p_get_position},
        {"kn_engine_poll_event", (void **)&p_poll_event},
        {"kn_devices_enumerate", (void **)&p_devices_enumerate},
        {"kn_engine_open_device", (void **)&p_open_device},
        {"kn_engine_close_device", (void **)&p_close_device},
        {"kn_probe", (void **)&p_probe},
        {"kn_register_dsp_library", (void **)&p_register_dsp_library},
        {"kn_dsp_enumerate", (void **)&p_dsp_enumerate},
        {"kn_dsp_free", (void **)&p_dsp_free},
        {"kn_engine_set_resampler", (void **)&p_engine_set_resampler},
    };
    for (size_t i = 0; i < sizeof(symbols) / sizeof(symbols[0]); i++) {
        *symbols[i].slot = libSym(lib, symbols[i].name);
        if (*symbols[i].slot == NULL) {
            printf("FAIL missing export: %s\n", symbols[i].name);
            failures++;
        } else {
            printf("ok   export present: %s\n", symbols[i].name);
        }
    }
    if (failures) {
        return 1;
    }

    testVersionNegotiation();
    testArgumentValidation();
    testLifecycleAndIdleContract();
    testDitherModes();
    testDeviceOpen();
    if (argc >= 3) {
        testProbe(argv[2]);
    } else {
        printf("skip probe tests: no fixtures directory given\n");
    }
    testDspRegistration(argv[1], argc == 4 ? argv[3] : NULL);

    printf(failures ? "\n%d FAILURE(S)\n" : "\nall tests passed\n", failures);
    return failures ? 1 : 0;
}
