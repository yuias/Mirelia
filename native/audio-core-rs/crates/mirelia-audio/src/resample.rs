//! Streaming sample-rate conversion for the decode worker.
//!
//! Conversion never runs on the render thread: the worker converts each decoded
//! block before it enters the sample ring, so the render callback stays
//! allocation-free and lock-free.

use rubato::audioadapter_buffers::direct::InterleavedSlice;
use rubato::{Fft, FixedSync, Resampler};

/// The `Fft` chunk hint for a rate pair (pub so tests can build the same `Fft`
/// as [`Converter`]).
///
/// The filter cutoff is set by the smaller of the two FFT block sides, so the
/// hint is sized to keep that side at 2048 frames or more.
pub fn chunk_hint(in_rate: usize, out_rate: usize) -> usize {
    const MIN_BLOCK: usize = 2048;
    (MIN_BLOCK * in_rate).div_ceil(out_rate).max(MIN_BLOCK)
}

/// Streaming sample-rate converter for one source-rate/output-rate pair.
///
/// A "run" starts at construction or [`Converter::reset`]; the frame counters
/// are relative to it. `FixedSync::Both` makes every resampler call consume
/// exactly `chunk_in` frames and produce exactly `chunk_out` frames, in the
/// exact ratio of the rates, so all frame accounting here is exact.
pub struct Converter {
    fft: Fft<f32>,
    in_rate: u32,
    out_rate: u32,
    channels: usize,
    chunk_in: usize,
    chunk_out: usize,
    // Interleaved input not yet forming a whole chunk.
    fifo: Vec<f32>,
    // Interleaved output of one resampler call.
    scratch: Vec<f32>,
    // Output frames of filter delay still to drop from the start of the run.
    to_skip: usize,
    fed: u64,
    emitted: u64,
}

impl Converter {
    /// Both rates must be non-zero: the ABI rejects a zero output rate and
    /// `Engine::load` rejects a zero source rate.
    pub fn new(in_rate: u32, out_rate: u32, channels: usize) -> Self {
        let fft = Fft::<f32>::new(
            in_rate as usize,
            out_rate as usize,
            chunk_hint(in_rate as usize, out_rate as usize),
            channels,
            FixedSync::Both,
        )
        .expect("rates and chunk hint are non-zero");
        let chunk_in = fft.input_frames_next();
        let chunk_out = fft.output_frames_next();
        let to_skip = fft.output_delay();
        Self {
            fft,
            in_rate,
            out_rate,
            channels,
            chunk_in,
            chunk_out,
            fifo: Vec::with_capacity(chunk_in * channels * 2),
            scratch: vec![0.0; chunk_out * channels],
            to_skip,
            fed: 0,
            emitted: 0,
        }
    }

    /// The source rate this converter was built for.
    pub fn in_rate(&self) -> u32 {
        self.in_rate
    }

    /// Appends converted output for `input` (interleaved) to `out`.
    pub fn process(&mut self, input: &[f32], out: &mut Vec<f32>) {
        self.fed += (input.len() / self.channels) as u64;
        self.fifo.extend_from_slice(input);
        let chunk_samples = self.chunk_in * self.channels;
        let mut start = 0;
        while self.fifo.len() - start >= chunk_samples {
            self.run_chunk(start, out, u64::MAX);
            start += chunk_samples;
        }
        self.fifo.drain(..start);
    }

    /// Appends everything still owed for the source fed so far: pumps zeros
    /// through the filter and stops at exactly `expected()` output frames.
    ///
    /// The filter state is not usable for further input afterwards; call
    /// [`Converter::reset`] before reuse.
    pub fn flush(&mut self, out: &mut Vec<f32>) {
        while self.emitted < self.expected() {
            // The FIFO holds less than one chunk here, so this only pads.
            self.fifo.resize(self.chunk_in * self.channels, 0.0);
            self.run_chunk(0, out, self.expected() - self.emitted);
            self.fifo.clear();
        }
    }

    /// Output frames owed for the source fed so far but not yet emitted.
    /// Zero after `flush()` and for a run that has been fed nothing.
    pub fn pending_frames(&self) -> u64 {
        self.expected() - self.emitted
    }

    /// Starts a new run: clears the filter, FIFO and counters and re-arms the
    /// delay trim.
    pub fn reset(&mut self) {
        self.fft.reset();
        self.fifo.clear();
        self.to_skip = self.fft.output_delay();
        self.fed = 0;
        self.emitted = 0;
    }

    /// Output frames the run owes for the source fed so far: the first output
    /// frame of source frame `n` is at output index `ceil(n * out / in)`.
    fn expected(&self) -> u64 {
        (self.fed * u64::from(self.out_rate)).div_ceil(u64::from(self.in_rate))
    }

    // Resamples the chunk at `fifo[start..]` and appends at most `cap` frames
    // of it to `out`, after dropping the filter delay from the run's start.
    fn run_chunk(&mut self, start: usize, out: &mut Vec<f32>, cap: u64) {
        let input = InterleavedSlice::new(
            &self.fifo[start..start + self.chunk_in * self.channels],
            self.channels,
            self.chunk_in,
        )
        .expect("chunk matches the channel count");
        let mut output =
            InterleavedSlice::new_mut(&mut self.scratch, self.channels, self.chunk_out)
                .expect("scratch matches the channel count");
        // Only wrong buffer sizes can fail, and both are sized from the resampler.
        self.fft
            .process_into_buffer(&input, &mut output, None)
            .expect("buffers match the resampler");

        let skip = self.to_skip.min(self.chunk_out);
        self.to_skip -= skip;
        let take = ((self.chunk_out - skip) as u64).min(cap) as usize;
        out.extend_from_slice(&self.scratch[skip * self.channels..(skip + take) * self.channels]);
        self.emitted += take as u64;
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::f32::consts::TAU;

    const PAIRS: [(u32, u32); 5] = [
        (44100, 48000),
        (48000, 44100),
        (44100, 96000),
        (96000, 44100),
        (22050, 48000),
    ];

    // Deterministic broadband noise in [-0.5, 0.5), interleaved.
    fn noise(frames: usize, channels: usize) -> Vec<f32> {
        let mut state = 0x2545_f491_u32;
        (0..frames * channels)
            .map(|_| {
                state = state.wrapping_mul(1_664_525).wrapping_add(1_013_904_223);
                (state >> 8) as f32 / (1 << 24) as f32 - 0.5
            })
            .collect()
    }

    fn convert_in_blocks(conv: &mut Converter, signal: &[f32], channels: usize) -> Vec<f32> {
        const BLOCKS: [usize; 8] = [1, 7, 4096, 333, 2058, 5, 1000, 64];
        let mut out = Vec::new();
        let mut pos = 0;
        let mut i = 0;
        while pos < signal.len() {
            let end = (pos + BLOCKS[i % BLOCKS.len()] * channels).min(signal.len());
            conv.process(&signal[pos..end], &mut out);
            pos = end;
            i += 1;
        }
        conv.flush(&mut out);
        out
    }

    #[test]
    fn output_matches_process_all_for_any_block_split() {
        let channels = 2;
        let frames = 30_001;
        for (in_rate, out_rate) in PAIRS {
            let signal = noise(frames, channels);
            let mut conv = Converter::new(in_rate, out_rate, channels);
            let got = convert_in_blocks(&mut conv, &signal, channels);

            let (i, o) = (in_rate as usize, out_rate as usize);
            let mut fft =
                Fft::<f32>::new(i, o, chunk_hint(i, o), channels, FixedSync::Both).unwrap();
            let input = InterleavedSlice::new(&signal, channels, frames).unwrap();
            let want = fft.process_all(&input, frames, None).unwrap().take_data();

            assert_eq!(got.len(), want.len(), "{in_rate}->{out_rate}");
            let worst = got
                .iter()
                .zip(&want)
                .map(|(a, b)| (a - b).abs())
                .fold(0.0_f32, f32::max);
            assert!(worst <= 1e-6, "{in_rate}->{out_rate}: worst diff {worst}");
        }
    }

    #[test]
    fn flush_emits_the_exact_output_length() {
        for (in_rate, out_rate) in PAIRS {
            for frames in [1_usize, 100, 2058, 12_345, 30_000] {
                let mut conv = Converter::new(in_rate, out_rate, 1);
                let out = convert_in_blocks(&mut conv, &noise(frames, 1), 1);
                let want = (frames as u64 * u64::from(out_rate)).div_ceil(u64::from(in_rate));
                assert_eq!(out.len() as u64, want, "{in_rate}->{out_rate} N={frames}");
            }
        }
    }

    #[test]
    fn first_output_frame_is_aligned_to_the_first_source_frame() {
        let (in_rate, out_rate) = (44100_u32, 48000_u32);
        let frames = 20_000;
        let freq = 997.0_f32;
        let signal: Vec<f32> = (0..frames)
            .map(|n| (TAU * freq * n as f32 / in_rate as f32).cos())
            .collect();
        let mut conv = Converter::new(in_rate, out_rate, 1);
        let out = convert_in_blocks(&mut conv, &signal, 1);

        // Stay clear of the start and end transients.
        let margin = 4_000;
        for (m, got) in out.iter().enumerate().take(out.len() - margin).skip(margin) {
            let want = (TAU * freq * m as f32 / out_rate as f32).cos();
            assert!((got - want).abs() < 1e-3, "frame {m}: {got} vs {want}");
        }
    }

    #[test]
    fn pending_frames_counts_the_tail_and_flush_clears_it() {
        let (in_rate, out_rate) = (44100_u32, 48000_u32);
        let mut conv = Converter::new(in_rate, out_rate, 2);
        let mut out = Vec::new();
        assert_eq!(conv.pending_frames(), 0);

        // Less than one chunk: nothing is emitted yet, all of it is owed.
        conv.process(&noise(100, 2), &mut out);
        assert!(out.is_empty());
        assert_eq!(conv.pending_frames(), 109);

        conv.process(&noise(10_000, 2), &mut out);
        let fed = 10_100_u64;
        let owed = (fed * 48000).div_ceil(44100);
        assert_eq!(conv.pending_frames(), owed - (out.len() / 2) as u64);
        assert!(conv.pending_frames() > 0);

        conv.flush(&mut out);
        assert_eq!(conv.pending_frames(), 0);
        assert_eq!((out.len() / 2) as u64, owed);
    }

    #[test]
    fn reset_makes_the_next_run_identical() {
        let channels = 2;
        let signal = noise(9_000, channels);
        let mut fresh = Converter::new(44100, 48000, channels);
        let want = convert_in_blocks(&mut fresh, &signal, channels);

        // Leave the first run unflushed with filter state and FIFO contents.
        let mut conv = Converter::new(44100, 48000, channels);
        let mut sink = Vec::new();
        conv.process(&noise(5_000, channels), &mut sink);
        conv.reset();
        assert_eq!(conv.pending_frames(), 0);
        let got = convert_in_blocks(&mut conv, &signal, channels);
        assert_eq!(got, want);

        conv.reset();
        assert_eq!(convert_in_blocks(&mut conv, &signal, channels), want);
    }

    #[test]
    fn cutoff_stays_close_to_nyquist() {
        let rates = [22050_usize, 44100, 48000, 88200, 96000, 176400, 192000];
        for &i in &rates {
            for &o in &rates {
                if i == o {
                    continue;
                }
                let fft = Fft::<f32>::new(i, o, chunk_hint(i, o), 2, FixedSync::Both).unwrap();
                let cutoff_hz = fft.cutoff() * i as f32 / 2.0;
                let floor_hz = 0.99 * i.min(o) as f32 / 2.0;
                assert!(cutoff_hz >= floor_hz, "{i}->{o}: {cutoff_hz} < {floor_hz}");
            }
        }
    }
}
