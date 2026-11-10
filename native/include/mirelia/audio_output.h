/*
 * Mirelia output plugin ABI, version 1.
 *
 * An output plugin is a shared library exporting a single entry point:
 *
 *     const MireliaOutputVtable *mireliaOutputEntry(uint32_t abi_version);
 *
 * The entry point returns NULL if it does not implement `abi_version`.
 *
 * Threading contract: `write` is called from the engine's audio thread and
 * must not allocate, lock, or perform blocking I/O other than blocking on
 * the device's own buffer availability. Everything else is called from
 * normal engine threads, never concurrently with itself.
 */

#ifndef MIRELIA_AUDIO_OUTPUT_H
#define MIRELIA_AUDIO_OUTPUT_H

#include <stdint.h>
#include <stddef.h>

#include "audio_core.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct MireliaOutputVtable {
    uint32_t    abi_version;
    const char *name;            /* backend id, e.g. "asio". Shown in device UI. */

    /* Snapshot semantics identical to mireliaDevicesEnumerate: the returned
     * array is owned by the plugin and valid until the next enumerate or
     * enumerate_free call. */
    MireliaResult (*enumerate)(const MireliaDeviceInfo **out_devices, size_t *out_count);
    void          (*enumerate_free)(void);

    MireliaResult (*open)(const char *device_id,
                          MireliaShareMode mode,
                          const MireliaStreamFormat *desired,
                          MireliaStreamFormat *out_actual,
                          void **out_ctx);
    MireliaResult (*start)(void *ctx);
    MireliaResult (*stop)(void *ctx);

    /* Writes interleaved frames in the format negotiated at open.
     * Blocks until the device has consumed the data or an error occurs.
     * Returns frames written, or a negative MireliaResult on error.
     * RT-safe: no allocation, no locks beyond the device's own buffer wait. */
    int64_t       (*write)(void *ctx, const void *interleaved, int64_t frames);

    /* Current device+driver latency in frames, for position reporting. */
    int64_t       (*latency_frames)(void *ctx);

    void          (*close)(void *ctx);
} MireliaOutputVtable;

/* The single required export. Returns NULL on ABI version mismatch. */
typedef const MireliaOutputVtable *(*MireliaOutputEntryFn)(uint32_t abi_version);

#ifdef __cplusplus
}
#endif
#endif /* MIRELIA_AUDIO_OUTPUT_H */
