// Sample resampler plugin for the Mirelia DSP plugin ABI, built on r8brain-free-src.
//
// r8brain neither returns a fixed output count per input block nor guarantees an
// exact total length, so R8brainResampler feeds it fixed-size chunks (which makes
// the output independent of how the host splits its input) and applies the
// length rules of the ABI contract around it.

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <new>
#include <vector>

#include "CDSPResampler.h"
#include "mirelia/audio_dsp.h"

namespace {

// Source frames given to r8brain per call. Also the largest call it must handle.
constexpr int kChunkFrames = 1024;

// Transition band in percent; upstream's default.
constexpr double kTransitionBand = 2.0;

class R8brainResampler {
public:
    R8brainResampler(uint32_t in_rate, uint32_t out_rate, uint16_t channels)
        : in_rate_(in_rate),
          out_rate_(out_rate),
          channels_(channels),
          chunk_samples_(static_cast<size_t>(kChunkFrames) * channels),
          in_buffer_(kChunkFrames) {
        resamplers_.reserve(channels);
        for (uint16_t c = 0; c < channels; ++c) {
            resamplers_.push_back(std::make_unique<r8b::CDSPResampler24>(
                in_rate, out_rate, kChunkFrames, kTransitionBand));
        }
    }

    int64_t process(const float* in, int64_t in_frames, float* out, int64_t out_capacity) {
        if (flushed_) {
            return MireliaErrState;
        }
        const uint64_t new_fed = fed_ + static_cast<uint64_t>(in_frames);
        const uint64_t limit = outFrames(new_fed) - emitted_;
        if (static_cast<uint64_t>(out_capacity) < limit) {
            return MireliaErrInvalidArg;
        }

        fifo_.insert(fifo_.end(), in, in + static_cast<size_t>(in_frames) * channels_);
        fed_ = new_fed;

        uint64_t written = 0;
        size_t pos = 0;
        while (fifo_.size() - pos >= chunk_samples_) {
            written += convertChunk(fifo_.data() + pos, out + written * channels_, limit - written);
            pos += chunk_samples_;
        }
        fifo_.erase(fifo_.begin(), fifo_.begin() + static_cast<std::ptrdiff_t>(pos));

        emitted_ += written;
        return static_cast<int64_t>(written);
    }

    int64_t flush(float* out, int64_t out_capacity) {
        const uint64_t pending = pendingFrames();
        if (static_cast<uint64_t>(out_capacity) < pending) {
            return MireliaErrInvalidArg;
        }

        // Treat everything after the last real frame as zeros: pad the partial chunk,
        // then keep feeding zero chunks until the exact output length is reached.
        fifo_.resize(chunk_samples_, 0.0f);
        uint64_t written = 0;
        while (written < pending) {
            written += convertChunk(fifo_.data(), out + written * channels_, pending - written);
            std::fill(fifo_.begin(), fifo_.end(), 0.0f);
        }
        fifo_.clear();

        emitted_ += written;
        flushed_ = true;
        return static_cast<int64_t>(written);
    }

    int64_t pendingFrames() const {
        return static_cast<int64_t>(outFrames(fed_) - emitted_);
    }

    void reset() {
        for (auto& resampler : resamplers_) {
            resampler->clear();
        }
        fifo_.clear();
        fed_ = 0;
        emitted_ = 0;
        flushed_ = false;
    }

private:
    // ceil(frames * out_rate / in_rate), the exact output length for a run.
    uint64_t outFrames(uint64_t frames) const {
        return (frames * out_rate_ + in_rate_ - 1) / in_rate_;
    }

    // Converts one whole chunk of interleaved input and writes at most `room`
    // frames of interleaved output. Returns the frames written.
    uint64_t convertChunk(const float* chunk, float* out, uint64_t room) {
        uint64_t frames = 0;
        for (uint16_t c = 0; c < channels_; ++c) {
            for (int i = 0; i < kChunkFrames; ++i) {
                in_buffer_[i] = chunk[static_cast<size_t>(i) * channels_ + c];
            }
            double* produced = nullptr;
            const int count = resamplers_[c]->process(in_buffer_.data(), kChunkFrames, produced);
            // The same filter runs on every channel, so each returns the same count.
            frames = std::min(room, static_cast<uint64_t>(count));
            for (uint64_t i = 0; i < frames; ++i) {
                out[i * channels_ + c] = static_cast<float>(produced[i]);
            }
        }
        return frames;
    }

    uint32_t in_rate_;
    uint32_t out_rate_;
    uint16_t channels_;
    size_t chunk_samples_;
    std::vector<std::unique_ptr<r8b::CDSPResampler24>> resamplers_;
    std::vector<double> in_buffer_;
    // Interleaved input not yet given to r8brain; always shorter than one chunk
    // between calls.
    std::vector<float> fifo_;
    uint64_t fed_ = 0;
    uint64_t emitted_ = 0;
    bool flushed_ = false;
};

R8brainResampler* asResampler(void* ctx) {
    return static_cast<R8brainResampler*>(ctx);
}

MireliaResult create(uint32_t in_rate, uint32_t out_rate, uint16_t channels, void** out_ctx) {
    try {
        *out_ctx = new R8brainResampler(in_rate, out_rate, channels);
        return MireliaOk;
    } catch (const std::bad_alloc&) {
        return MireliaErrOom;
    } catch (...) {
        return MireliaErrInternal;
    }
}

int64_t process(void* ctx, const float* in, int64_t in_frames, float* out, int64_t out_capacity) {
    try {
        return asResampler(ctx)->process(in, in_frames, out, out_capacity);
    } catch (const std::bad_alloc&) {
        return MireliaErrOom;
    } catch (...) {
        return MireliaErrInternal;
    }
}

int64_t flush(void* ctx, float* out, int64_t out_capacity) {
    try {
        return asResampler(ctx)->flush(out, out_capacity);
    } catch (const std::bad_alloc&) {
        return MireliaErrOom;
    } catch (...) {
        return MireliaErrInternal;
    }
}

int64_t pendingFrames(void* ctx) {
    try {
        return asResampler(ctx)->pendingFrames();
    } catch (...) {
        return MireliaErrInternal;
    }
}

MireliaResult reset(void* ctx) {
    try {
        asResampler(ctx)->reset();
        return MireliaOk;
    } catch (const std::bad_alloc&) {
        return MireliaErrOom;
    } catch (...) {
        return MireliaErrInternal;
    }
}

void destroy(void* ctx) {
    try {
        delete asResampler(ctx);
    } catch (...) {
    }
}

const MireliaDspResamplerVtable kResamplerVtable = {
    create, process, flush, pendingFrames, reset, destroy,
};

const MireliaDspCapability kCapabilities[] = {
    {MireliaDspKindResampler, &kResamplerVtable},
};

const MireliaDspPlugin kPlugin = {
    MIRELIA_DSP_ABI_VERSION,
    "r8brain",
    "r8brain-free-src",
    sizeof(kCapabilities) / sizeof(kCapabilities[0]),
    kCapabilities,
};

}  // namespace

extern "C" MIRELIA_DSP_EXPORT const MireliaDspPlugin* mireliaDspEntry(uint32_t abi_version) {
    return abi_version == MIRELIA_DSP_ABI_VERSION ? &kPlugin : nullptr;
}
