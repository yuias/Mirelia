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

typedef MireliaResult (*EngineCreateFn)(const MireliaEngineConfig *, MireliaEngine **);
typedef void (*EngineDestroyFn)(MireliaEngine *);
typedef const char *(*EngineLastErrorFn)(MireliaEngine *);
typedef MireliaResult (*GetPositionFn)(MireliaEngine *, MireliaPosition *);
typedef size_t (*PollEventFn)(MireliaEngine *, MireliaEvent *, size_t);
typedef MireliaResult (*DevicesEnumerateFn)(MireliaEngine *, const MireliaDeviceInfo **, size_t *);
typedef MireliaResult (*OpenDeviceFn)(MireliaEngine *, const char *, MireliaShareMode,
                                      const MireliaStreamFormat *, MireliaStreamFormat *);
typedef MireliaResult (*CloseDeviceFn)(MireliaEngine *);
typedef MireliaResult (*ProbeFn)(MireliaEngine *, const char *, MireliaProbeResult *);
typedef MireliaResult (*RegisterDspLibraryFn)(MireliaEngine *, const char *);
typedef MireliaResult (*DspEnumerateFn)(MireliaEngine *, const MireliaDspInfo **, size_t *);
typedef void (*DspFreeFn)(MireliaEngine *);
typedef MireliaResult (*EngineSetResamplerFn)(MireliaEngine *, const char *);

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

static MireliaEngineConfig defaultConfig(void) {
    MireliaEngineConfig cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.abi_version = MIRELIA_ABI_VERSION;
    cfg.allow_resample = 1;
    cfg.dither_to_int = 1;
    return cfg;
}

/* ---- tests ------------------------------------------------------------- */

static void testVersionNegotiation(void) {
    MireliaEngineConfig cfg = defaultConfig();
    cfg.abi_version = 0xDEAD;
    MireliaEngine *e = NULL;
    CHECK(p_create(&cfg, &e) == MireliaErrAbiMismatch,
          "create rejects unknown ABI version with MireliaErrAbiMismatch");
    CHECK(e == NULL, "engine stays null on ABI mismatch");
    const char *msg = p_last_error(NULL);
    CHECK(msg != NULL && msg[0] != '\0', "last_error carries detail after mismatch");
}

static void testArgumentValidation(void) {
    CHECK(p_create(NULL, NULL) == MireliaErrInvalidArg,
          "create rejects null arguments with MireliaErrInvalidArg");
}

static void testLifecycleAndIdleContract(void) {
    MireliaEngineConfig cfg = defaultConfig();
    MireliaEngine *e = NULL;
    CHECK(p_create(&cfg, &e) == MireliaOk, "create succeeds for ABI v1");
    CHECK(e != NULL, "engine handle is non-null");
    if (e == NULL) {
        return;
    }

    MireliaPosition pos;
    memset(&pos, 0xFF, sizeof(pos));
    CHECK(p_get_position(e, &pos) == MireliaOk, "get_position succeeds");
    CHECK(pos.state == MireliaStateIdle, "fresh engine is idle");
    CHECK(pos.total_frames == -1, "total_frames is -1 when nothing is loaded");
    CHECK(pos.user_token == 0, "user_token is 0 when nothing is audible");

    MireliaPosition later;
    CHECK(p_get_position(e, &later) == MireliaOk, "second get_position succeeds");
    CHECK(later.host_time_ns >= pos.host_time_ns, "host_time_ns is monotonic");

    MireliaEvent events[8];
    CHECK(p_poll_event(e, events, 8) == 0, "fresh engine has no queued events");

    const MireliaDeviceInfo *devices = NULL;
    size_t count = (size_t)-1;
    MireliaResult rc = p_devices_enumerate(e, &devices, &count);
    CHECK(rc == MireliaOk || rc == MireliaErrUnsupported,
          "devices_enumerate returns MireliaOk or MireliaErrUnsupported");
    if (rc == MireliaOk) {
        CHECK(count != (size_t)-1, "devices_enumerate writes a count");
    }

    p_destroy(e);
    p_destroy(NULL); /* must be a harmless no-op */
    printf("ok   destroy(NULL) is a no-op\n");
}

static void testDitherModes(void) {
    /* 0 = off, 1 = always (unchanged), 2 = resampled tracks only. */
    for (int mode = 0; mode <= 2; mode++) {
        MireliaEngineConfig cfg = defaultConfig();
        cfg.dither_to_int = mode;
        MireliaEngine *e = NULL;
        char msg[64];
        snprintf(msg, sizeof(msg), "create succeeds with dither_to_int = %d", mode);
        CHECK(p_create(&cfg, &e) == MireliaOk && e != NULL, msg);
        if (e != NULL) {
            p_destroy(e);
        }
    }
}

static void testProbe(const char *fixtures_dir) {
    char path[1024];
    snprintf(path, sizeof(path), "%s/fixture.flac", fixtures_dir);

    MireliaEngineConfig cfg = defaultConfig();
    MireliaEngine *e = NULL;
    if (p_create(&cfg, &e) != MireliaOk) {
        CHECK(0, "probe: engine creation failed");
        return;
    }

    MireliaProbeResult probe;
    memset(&probe, 0, sizeof(probe));
    CHECK(p_probe(e, path, &probe) == MireliaOk, "probe succeeds on FLAC fixture");
    CHECK(probe.sample_rate == 44100, "probe reports 44100 Hz");
    CHECK(probe.channels == 2, "probe reports 2 channels");
    CHECK(probe.total_frames == 17640, "probe reports exact frame count");
    CHECK(strcmp(probe.codec, "flac") == 0, "probe reports codec 'flac'");

    MireliaProbeResult missing;
    CHECK(p_probe(e, "no-such-file.flac", &missing) == MireliaErrIo,
          "probe reports MireliaErrIo for a missing file");

    p_destroy(e);
}

static void testDeviceOpen(void) {
    MireliaEngineConfig cfg = defaultConfig();
    MireliaEngine *e = NULL;
    if (p_create(&cfg, &e) != MireliaOk) {
        CHECK(0, "device: engine creation failed");
        return;
    }

    MireliaStreamFormat actual;
    memset(&actual, 0, sizeof(actual));
    MireliaResult rc = p_open_device(e, NULL, MireliaShareShared, NULL, &actual);
    if (rc == MireliaOk) {
        CHECK(actual.sample_rate > 0, "opened device reports a sample rate");
        CHECK(actual.channels > 0, "opened device reports channels");
        CHECK(p_close_device(e) == MireliaOk, "close_device succeeds");
    } else if (rc == MireliaErrDeviceLost || rc == MireliaErrUnsupported) {
        /* CI runners have no audio endpoint; that must fail cleanly, not crash. */
        printf("skip open_device: no usable device here (%d)\n", (int)rc);
    } else {
        CHECK(0, "open_device returned an unexpected error");
    }

    /* Rejected at the ABI boundary, so this needs no audio endpoint. */
    MireliaStreamFormat zero_rate = {0, 2, MireliaFmtF32, 0};
    CHECK(p_open_device(e, NULL, MireliaShareShared, &zero_rate, &actual) == MireliaErrInvalidArg,
          "open_device rejects a desired sample rate of 0 with MireliaErrInvalidArg");

    /* channels == 0 fixes the rate only; the stream opens at the first load. */
    MireliaStreamFormat fixed_rate = {48000, 0, MireliaFmtF32, 0};
    memset(&actual, 0, sizeof(actual));
    rc = p_open_device(e, NULL, MireliaShareShared, &fixed_rate, &actual);
    if (rc == MireliaOk) {
        CHECK(actual.sample_rate == 48000, "open_device with a fixed rate reports that rate");
    } else if (rc == MireliaErrDeviceLost || rc == MireliaErrUnsupported) {
        printf("skip fixed-rate open_device: no usable device here (%d)\n", (int)rc);
    } else {
        CHECK(0, "fixed-rate open_device returned an unexpected error");
    }

    p_destroy(e);
}

static void testDspRegistration(const char *backend_path, const char *plugin_path) {
    MireliaEngineConfig cfg = defaultConfig();
    MireliaEngine *e = NULL;
    if (p_create(&cfg, &e) != MireliaOk) {
        CHECK(0, "dsp: engine creation failed");
        return;
    }

    CHECK(p_register_dsp_library(e, NULL) == MireliaErrInvalidArg,
          "register_dsp_library rejects a null path with MireliaErrInvalidArg");

    MireliaResult rc = p_register_dsp_library(e, "relative-plugin.dll");
    if (rc == MireliaErrUnsupported) {
        printf("skip dsp registration tests: plugins are not implemented here\n");
        p_destroy(e);
        return;
    }
    CHECK(rc == MireliaErrInvalidArg,
          "register_dsp_library rejects a relative path with MireliaErrInvalidArg");

    char missing[1024];
    if (makeAbsolute("no-such-plugin.dll", missing, sizeof(missing))) {
        CHECK(p_register_dsp_library(e, missing) == MireliaErrIo,
              "register_dsp_library reports MireliaErrIo for a missing file");
    } else {
        CHECK(0, "dsp: cannot make a missing path absolute");
    }

    char backend[1024];
    if (makeAbsolute(backend_path, backend, sizeof(backend))) {
        CHECK(p_register_dsp_library(e, backend) == MireliaErrFormat,
              "register_dsp_library reports MireliaErrFormat for a library without kn_dsp_entry");
    } else {
        CHECK(0, "dsp: cannot make the backend path absolute");
    }

    const MireliaDspInfo *infos = NULL;
    size_t count = (size_t)-1;
    CHECK(p_dsp_enumerate(e, &infos, &count) == MireliaOk && count == 0,
          "dsp_enumerate lists nothing before any registration");
    CHECK(p_engine_set_resampler(e, "no-such-plugin") == MireliaErrInvalidArg,
          "engine_set_resampler rejects an unregistered id with MireliaErrInvalidArg");
    CHECK(p_engine_set_resampler(e, NULL) == MireliaOk,
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
    CHECK(p_register_dsp_library(e, plugin) == MireliaOk,
          "register_dsp_library registers the plugin");
    CHECK(p_dsp_enumerate(e, &infos, &count) == MireliaOk && count == 1,
          "dsp_enumerate lists the registered plugin");
    if (count == 1) {
        CHECK(infos[0].kind == MireliaDspKindResampler, "enumerated plugin is a resampler");
        CHECK(infos[0].id != NULL && infos[0].id[0] != '\0' &&
                  infos[0].name != NULL && infos[0].name[0] != '\0',
              "enumerated plugin has an id and a name");

        char id[128];
        snprintf(id, sizeof(id), "%s", infos[0].id);
        CHECK(p_register_dsp_library(e, plugin) == MireliaErrState,
              "register_dsp_library rejects a second plugin with the same id");
        CHECK(p_engine_set_resampler(e, id) == MireliaOk, "engine_set_resampler accepts the id");
        CHECK(p_engine_set_resampler(e, "no-such-plugin") == MireliaErrInvalidArg,
              "engine_set_resampler still rejects an unregistered id");
        CHECK(p_engine_set_resampler(e, NULL) == MireliaOk,
              "engine_set_resampler accepts NULL after a plugin was selected");
        CHECK(p_engine_set_resampler(e, id) == MireliaOk,
              "engine_set_resampler selects the id again");
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
