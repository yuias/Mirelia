/*
 * ABI conformance suite for Mirelia audio backends.
 *
 * Loads the backend under test dynamically (path given as argv[1]) so the
 * same binary validates audio-core-rs, audio-core-cpp, or any future
 * implementation. Any backend must pass identically.
 *
 * M0 coverage: symbol presence, version negotiation, lifecycle, argument
 * validation, idle position/event contract. Device, gapless, and RT-thread
 * allocation checks are added milestone by milestone (see kickoff §5.6).
 */

#include <stdio.h>
#include <string.h>

#include <mirelia/audio_core.h>

#ifdef _WIN32
#include <windows.h>
typedef HMODULE lib_handle;
static lib_handle lib_open(const char *path) { return LoadLibraryA(path); }
static void *lib_sym(lib_handle h, const char *name) {
    return (void *)GetProcAddress(h, name);
}
#else
#include <dlfcn.h>
typedef void *lib_handle;
static lib_handle lib_open(const char *path) { return dlopen(path, RTLD_NOW); }
static void *lib_sym(lib_handle h, const char *name) { return dlsym(h, name); }
#endif

/* ---- resolved backend entry points ------------------------------------ */

typedef kn_result (*fn_engine_create)(const kn_engine_config *, kn_engine **);
typedef void (*fn_engine_destroy)(kn_engine *);
typedef const char *(*fn_engine_last_error)(kn_engine *);
typedef kn_result (*fn_get_position)(kn_engine *, kn_position *);
typedef size_t (*fn_poll_event)(kn_engine *, kn_event *, size_t);
typedef kn_result (*fn_devices_enumerate)(kn_engine *, const kn_device_info **, size_t *);

static fn_engine_create p_create;
static fn_engine_destroy p_destroy;
static fn_engine_last_error p_last_error;
static fn_get_position p_get_position;
static fn_poll_event p_poll_event;
static fn_devices_enumerate p_devices_enumerate;

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

static kn_engine_config default_config(void) {
    kn_engine_config cfg;
    memset(&cfg, 0, sizeof(cfg));
    cfg.abi_version = KN_ABI_VERSION;
    cfg.allow_resample = 1;
    cfg.dither_to_int = 1;
    return cfg;
}

/* ---- tests ------------------------------------------------------------- */

static void test_version_negotiation(void) {
    kn_engine_config cfg = default_config();
    cfg.abi_version = 0xDEAD;
    kn_engine *e = NULL;
    CHECK(p_create(&cfg, &e) == KN_ERR_ABI_MISMATCH,
          "create rejects unknown ABI version with KN_ERR_ABI_MISMATCH");
    CHECK(e == NULL, "engine stays null on ABI mismatch");
    const char *msg = p_last_error(NULL);
    CHECK(msg != NULL && msg[0] != '\0', "last_error carries detail after mismatch");
}

static void test_argument_validation(void) {
    CHECK(p_create(NULL, NULL) == KN_ERR_INVALID_ARG,
          "create rejects null arguments with KN_ERR_INVALID_ARG");
}

static void test_lifecycle_and_idle_contract(void) {
    kn_engine_config cfg = default_config();
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

/* ---- main -------------------------------------------------------------- */

int main(int argc, char **argv) {
    if (argc != 2) {
        fprintf(stderr, "usage: abi-conformance <path-to-backend-library>\n");
        return 2;
    }

    lib_handle lib = lib_open(argv[1]);
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
    };
    for (size_t i = 0; i < sizeof(symbols) / sizeof(symbols[0]); i++) {
        *symbols[i].slot = lib_sym(lib, symbols[i].name);
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

    test_version_negotiation();
    test_argument_validation();
    test_lifecycle_and_idle_contract();

    printf(failures ? "\n%d FAILURE(S)\n" : "\nall tests passed\n", failures);
    return failures ? 1 : 0;
}
