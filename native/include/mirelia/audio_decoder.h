/*
 * Mirelia decoder plugin ABI, version 1.
 *
 * A decoder plugin is a shared library exporting a single entry point:
 *
 *     const kn_decoder_vtable *kn_decoder_entry(uint32_t abi_version);
 *
 * The entry point returns NULL if it does not implement `abi_version`.
 * Decoders always output interleaved f32; format negotiation is the
 * engine's problem, not the plugin's.
 */

#ifndef MIRELIA_AUDIO_DECODER_H
#define MIRELIA_AUDIO_DECODER_H

#include <stdint.h>
#include <stddef.h>

#include "audio_core.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct kn_decoder_vtable {
    uint32_t    abi_version;
    const char *name;
    const char *extensions;      /* semicolon-separated, e.g. "opus;spx;tak" */

    /* Probe by content, not extension. Returns 0..100 confidence. */
    int  (*probe)(const uint8_t *head, size_t head_len, const char *path);

    kn_result (*open)(const char *path, void **out_ctx, kn_probe_result *out_info);
    /* Returns frames written. 0 = EOF, negative = kn_result error.
     * Must not block indefinitely. */
    int64_t   (*read_f32)(void *ctx, float *interleaved, int64_t max_frames);
    kn_result (*seek)(void *ctx, int64_t frame);
    void      (*close)(void *ctx);
} kn_decoder_vtable;

/* The single required export. Returns NULL on ABI version mismatch. */
typedef const kn_decoder_vtable *(*kn_decoder_entry_fn)(uint32_t abi_version);

#ifdef __cplusplus
}
#endif
#endif /* MIRELIA_AUDIO_DECODER_H */
