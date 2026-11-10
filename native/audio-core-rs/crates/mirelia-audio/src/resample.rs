//! Streaming sample-rate conversion for the decode worker.
//!
//! Conversion never runs on the render thread: the worker converts each decoded
//! block before it enters the sample ring, so the render callback stays
//! allocation-free and lock-free.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

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

/// Value of `MireliaErrPlugin`, reported in the `Error` event when a plugin fails.
pub const MIRELIA_ERR_PLUGIN: i32 = -11;

/// Why a resampler could not do what was asked.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ResamplerError {
    /// The plugin does not convert this rate pair or channel count.
    Unsupported,
    /// The plugin failed; it must not be used again.
    Failed,
}

/// One streaming conversion, following the contract in `audio_dsp.h`.
///
/// The method semantics are those of the inherent methods of [`Converter`].
pub trait StreamResampler: Send {
    /// Appends converted output for `input` (interleaved) to `out`.
    fn process(&mut self, input: &[f32], out: &mut Vec<f32>) -> Result<(), ResamplerError>;

    /// Appends everything still owed for the source fed so far to `out`.
    fn flush(&mut self, out: &mut Vec<f32>) -> Result<(), ResamplerError>;

    /// Output frames owed for the source fed so far but not yet emitted.
    fn pending_frames(&self) -> u64;

    /// Starts a new run.
    fn reset(&mut self) -> Result<(), ResamplerError>;
}

impl StreamResampler for Converter {
    fn process(&mut self, input: &[f32], out: &mut Vec<f32>) -> Result<(), ResamplerError> {
        Converter::process(self, input, out);
        Ok(())
    }

    fn flush(&mut self, out: &mut Vec<f32>) -> Result<(), ResamplerError> {
        Converter::flush(self, out);
        Ok(())
    }

    fn pending_frames(&self) -> u64 {
        Converter::pending_frames(self)
    }

    fn reset(&mut self) -> Result<(), ResamplerError> {
        Converter::reset(self);
        Ok(())
    }
}

/// Creates resamplers of one kind (a loaded plugin).
pub trait ResamplerFactory: Send + Sync {
    /// Both rates are non-zero and different, and `channels` is at least 1.
    fn create(
        &self,
        in_rate: u32,
        out_rate: u32,
        channels: usize,
    ) -> Result<Box<dyn StreamResampler>, ResamplerError>;
}

/// The engine's selection: a factory, disabled for good after a failure so a
/// failing plugin is tried once, not once per track.
pub struct SelectedResampler {
    factory: Arc<dyn ResamplerFactory>,
    disabled: AtomicBool,
}

impl SelectedResampler {
    /// Selects `factory`. Selecting again builds a new value, which is what
    /// re-enables a plugin that failed.
    pub fn new(factory: Arc<dyn ResamplerFactory>) -> Self {
        Self {
            factory,
            disabled: AtomicBool::new(false),
        }
    }
}

/// The worker's converter: the selected plugin when usable, rubato otherwise.
///
/// Every failure of a plugin is absorbed here, so the worker sees infallible
/// calls and only polls [`ResampleStage::take_failure`].
pub struct ResampleStage {
    inner: Box<dyn StreamResampler>,
    selected: Option<Arc<SelectedResampler>>,
    in_rate: u32,
    out_rate: u32,
    channels: usize,
    failure: Option<i32>,
}

impl ResampleStage {
    /// Tries `selected` first unless it is disabled, then the built-in. Never
    /// fails.
    pub fn new(
        selected: Option<Arc<SelectedResampler>>,
        in_rate: u32,
        out_rate: u32,
        channels: usize,
    ) -> Self {
        let mut inner: Option<Box<dyn StreamResampler>> = None;
        let mut failure = None;
        if let Some(sel) = selected
            .as_ref()
            .filter(|s| !s.disabled.load(Ordering::Relaxed))
        {
            match sel.factory.create(in_rate, out_rate, channels) {
                Ok(plugin) => inner = Some(plugin),
                // Only this stream is left to the built-in: the plugin stays
                // enabled for other rate pairs.
                Err(ResamplerError::Unsupported) => {}
                Err(ResamplerError::Failed) => {
                    sel.disabled.store(true, Ordering::Relaxed);
                    failure = Some(MIRELIA_ERR_PLUGIN);
                }
            }
        }
        Self {
            inner: inner.unwrap_or_else(|| Box::new(Converter::new(in_rate, out_rate, channels))),
            selected,
            in_rate,
            out_rate,
            channels,
            failure,
        }
    }

    /// The source rate this stage was built for.
    pub fn in_rate(&self) -> u32 {
        self.in_rate
    }

    /// Appends converted output for `input` (interleaved) to `out`.
    pub fn process(&mut self, input: &[f32], out: &mut Vec<f32>) {
        let owed = self.inner.pending_frames();
        let len_before = out.len();
        if self.inner.process(input, out).is_err() {
            self.fall_back(out, len_before, owed);
            // The built-in starts a new run, so it takes the whole block.
            let _ = self.inner.process(input, out);
        }
    }

    /// Appends everything still owed for the source fed so far to `out`. Like
    /// [`Converter::flush`], the stage needs a [`ResampleStage::reset`] before
    /// further input.
    pub fn flush(&mut self, out: &mut Vec<f32>) {
        let owed = self.inner.pending_frames();
        let len_before = out.len();
        if self.inner.flush(out).is_err() {
            self.fall_back(out, len_before, owed);
        }
    }

    /// Output frames owed for the source fed so far but not yet emitted.
    pub fn pending_frames(&self) -> u64 {
        self.inner.pending_frames()
    }

    /// Starts a new run.
    pub fn reset(&mut self) {
        // The old run is being abandoned, so a failed reset owes nothing.
        if self.inner.reset().is_err() {
            self.replace_with_builtin();
        }
    }

    /// A plugin failure not yet reported, as a `MireliaResult`.
    pub fn take_failure(&mut self) -> Option<i32> {
        self.failure.take()
    }

    // Handles a failed `process` or `flush`. The `owed` frames are inside the
    // dead plugin, so `out` is cut back to `len_before` and given that many
    // frames of silence instead: dropping them would shift everything after
    // them, while silence keeps the ring position and the track length exact to
    // within one frame.
    fn fall_back(&mut self, out: &mut Vec<f32>, len_before: usize, owed: u64) {
        out.truncate(len_before);
        out.resize(len_before + owed as usize * self.channels, 0.0);
        self.replace_with_builtin();
    }

    // Swaps in a fresh built-in converter, reports the failure once and keeps
    // the plugin from being tried again until the host selects it again.
    fn replace_with_builtin(&mut self) {
        self.inner = Box::new(Converter::new(self.in_rate, self.out_rate, self.channels));
        self.failure = Some(MIRELIA_ERR_PLUGIN);
        if let Some(sel) = &self.selected {
            sel.disabled.store(true, Ordering::Relaxed);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::f32::consts::TAU;
    use std::sync::atomic::AtomicUsize;

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

    // Number of calls the fakes saw, shared with the test that built the factory.
    #[derive(Default)]
    struct Calls {
        creates: AtomicUsize,
        processes: AtomicUsize,
        flushes: AtomicUsize,
        resets: AtomicUsize,
    }

    #[derive(Clone, Copy, Default)]
    struct Faults {
        create: Option<ResamplerError>,
        // 1-based index of the `process` call that fails.
        process_at: Option<usize>,
        flush: bool,
        reset: bool,
    }

    // A plugin that is the built-in converter with scripted failures.
    struct FakePlugin {
        inner: Converter,
        faults: Faults,
        calls: Arc<Calls>,
        process_calls: usize,
    }

    impl StreamResampler for FakePlugin {
        fn process(&mut self, input: &[f32], out: &mut Vec<f32>) -> Result<(), ResamplerError> {
            self.calls.processes.fetch_add(1, Ordering::Relaxed);
            self.process_calls += 1;
            if self.faults.process_at == Some(self.process_calls) {
                // A failing plugin may leave partial output behind.
                out.extend_from_slice(&[1.0; 6]);
                return Err(ResamplerError::Failed);
            }
            self.inner.process(input, out);
            Ok(())
        }

        fn flush(&mut self, out: &mut Vec<f32>) -> Result<(), ResamplerError> {
            self.calls.flushes.fetch_add(1, Ordering::Relaxed);
            if self.faults.flush {
                out.extend_from_slice(&[1.0; 6]);
                return Err(ResamplerError::Failed);
            }
            self.inner.flush(out);
            Ok(())
        }

        fn pending_frames(&self) -> u64 {
            self.inner.pending_frames()
        }

        fn reset(&mut self) -> Result<(), ResamplerError> {
            self.calls.resets.fetch_add(1, Ordering::Relaxed);
            if self.faults.reset {
                return Err(ResamplerError::Failed);
            }
            self.inner.reset();
            Ok(())
        }
    }

    struct FakeFactory {
        faults: Faults,
        calls: Arc<Calls>,
    }

    impl ResamplerFactory for FakeFactory {
        fn create(
            &self,
            in_rate: u32,
            out_rate: u32,
            channels: usize,
        ) -> Result<Box<dyn StreamResampler>, ResamplerError> {
            self.calls.creates.fetch_add(1, Ordering::Relaxed);
            if let Some(err) = self.faults.create {
                return Err(err);
            }
            Ok(Box::new(FakePlugin {
                inner: Converter::new(in_rate, out_rate, channels),
                faults: self.faults,
                calls: Arc::clone(&self.calls),
                process_calls: 0,
            }))
        }
    }

    fn select(faults: Faults) -> (Arc<SelectedResampler>, Arc<Calls>) {
        let calls = Arc::new(Calls::default());
        let factory = FakeFactory {
            faults,
            calls: Arc::clone(&calls),
        };
        (Arc::new(SelectedResampler::new(Arc::new(factory))), calls)
    }

    const BLOCK: usize = 4096;

    // Feeds `signal` in `BLOCK`-frame blocks without flushing.
    fn stage_feed(stage: &mut ResampleStage, signal: &[f32], channels: usize) -> Vec<f32> {
        let mut out = Vec::new();
        for block in signal.chunks(BLOCK * channels) {
            stage.process(block, &mut out);
        }
        out
    }

    fn stage_convert(stage: &mut ResampleStage, signal: &[f32], channels: usize) -> Vec<f32> {
        let mut out = stage_feed(stage, signal, channels);
        stage.flush(&mut out);
        out
    }

    fn out_len(frames: usize, in_rate: u32, out_rate: u32) -> usize {
        (frames as u64 * u64::from(out_rate)).div_ceil(u64::from(in_rate)) as usize
    }

    #[test]
    fn stage_without_a_selection_equals_the_builtin() {
        let signal = noise(20_000, 2);
        let mut stage = ResampleStage::new(None, 44100, 48000, 2);
        assert_eq!(stage.in_rate(), 44100);
        let got = stage_convert(&mut stage, &signal, 2);

        let mut conv = Converter::new(44100, 48000, 2);
        let want = convert_in_blocks(&mut conv, &signal, 2);
        assert_eq!(got, want);
        assert_eq!(stage.take_failure(), None);
    }

    #[test]
    fn stage_uses_the_selected_plugin() {
        let (sel, calls) = select(Faults::default());
        let signal = noise(20_000, 1);
        let mut stage = ResampleStage::new(Some(sel), 44100, 48000, 1);
        let got = stage_convert(&mut stage, &signal, 1);

        assert_eq!(calls.creates.load(Ordering::Relaxed), 1);
        assert!(calls.processes.load(Ordering::Relaxed) > 0);
        assert_eq!(calls.flushes.load(Ordering::Relaxed), 1);
        assert_eq!(got.len(), out_len(20_000, 44100, 48000));
        assert_eq!(stage.take_failure(), None);

        stage.reset();
        assert_eq!(calls.resets.load(Ordering::Relaxed), 1);
    }

    #[test]
    fn stage_unsupported_create_uses_the_builtin_without_reporting() {
        let (sel, calls) = select(Faults {
            create: Some(ResamplerError::Unsupported),
            ..Faults::default()
        });
        let signal = noise(20_000, 1);
        let mut stage = ResampleStage::new(Some(Arc::clone(&sel)), 44100, 48000, 1);
        let got = stage_convert(&mut stage, &signal, 1);

        let mut conv = Converter::new(44100, 48000, 1);
        assert_eq!(got, convert_in_blocks(&mut conv, &signal, 1));
        assert_eq!(stage.take_failure(), None);

        // The selection stays enabled, so another stream may still use it.
        let _ = ResampleStage::new(Some(sel), 48000, 44100, 1);
        assert_eq!(calls.creates.load(Ordering::Relaxed), 2);
    }

    #[test]
    fn stage_process_failure_pads_what_was_owed_and_continues_on_the_builtin() {
        let (in_rate, out_rate) = (44100_u32, 48000_u32);
        let frames = 30_000;
        let (sel, _) = select(Faults {
            process_at: Some(3),
            ..Faults::default()
        });
        let signal = noise(frames, 1);
        let mut stage = ResampleStage::new(Some(sel), in_rate, out_rate, 1);

        // The third block fails: two whole blocks were fed before it.
        let fed = 2 * BLOCK;
        let mut out = stage_feed(&mut stage, &signal[..fed], 1);
        let emitted = out.len();
        assert_eq!(stage.take_failure(), None);
        let owed = out_len(fed, in_rate, out_rate) - emitted;
        assert!(owed > 0);

        stage.process(&signal[fed..fed + BLOCK], &mut out);
        assert!(out[emitted..emitted + owed].iter().all(|&s| s == 0.0));
        assert_eq!(stage.take_failure(), Some(MIRELIA_ERR_PLUGIN));
        assert_eq!(stage.take_failure(), None);

        stage.process(&signal[fed + BLOCK..], &mut out);
        stage.flush(&mut out);
        assert_eq!(
            out.len(),
            out_len(fed, in_rate, out_rate) + out_len(frames - fed, in_rate, out_rate)
        );
        assert_eq!(stage.pending_frames(), 0);
        assert_eq!(stage.take_failure(), None);
    }

    #[test]
    fn stage_flush_failure_finishes_with_nothing_pending() {
        let (sel, _) = select(Faults {
            flush: true,
            ..Faults::default()
        });
        let frames = 10_000;
        let mut stage = ResampleStage::new(Some(sel), 44100, 48000, 2);
        let got = stage_convert(&mut stage, &noise(frames, 2), 2);

        assert_eq!(got.len(), out_len(frames, 44100, 48000) * 2);
        assert_eq!(stage.pending_frames(), 0);
        assert_eq!(stage.take_failure(), Some(MIRELIA_ERR_PLUGIN));
        assert_eq!(stage.take_failure(), None);
    }

    #[test]
    fn stage_reset_failure_switches_to_the_builtin() {
        let (sel, calls) = select(Faults {
            reset: true,
            ..Faults::default()
        });
        let signal = noise(12_000, 1);
        let mut stage = ResampleStage::new(Some(Arc::clone(&sel)), 44100, 48000, 1);
        let _ = stage_feed(&mut stage, &signal[..5_000], 1);
        stage.reset();

        assert_eq!(stage.pending_frames(), 0);
        assert_eq!(stage.take_failure(), Some(MIRELIA_ERR_PLUGIN));
        assert!(sel.disabled.load(Ordering::Relaxed));

        // The new run is the built-in's, whole.
        let processes_before = calls.processes.load(Ordering::Relaxed);
        let got = stage_convert(&mut stage, &signal, 1);
        let mut conv = Converter::new(44100, 48000, 1);
        assert_eq!(got, convert_in_blocks(&mut conv, &signal, 1));
        assert_eq!(calls.processes.load(Ordering::Relaxed), processes_before);
    }

    #[test]
    fn a_disabled_selection_is_not_tried_again() {
        let (sel, calls) = select(Faults {
            process_at: Some(1),
            ..Faults::default()
        });
        let signal = noise(9_000, 1);
        let mut first = ResampleStage::new(Some(Arc::clone(&sel)), 44100, 48000, 1);
        let _ = stage_convert(&mut first, &signal, 1);
        assert_eq!(first.take_failure(), Some(MIRELIA_ERR_PLUGIN));
        assert_eq!(calls.creates.load(Ordering::Relaxed), 1);

        let mut second = ResampleStage::new(Some(sel), 44100, 48000, 1);
        assert_eq!(calls.creates.load(Ordering::Relaxed), 1);
        let got = stage_convert(&mut second, &signal, 1);
        let mut conv = Converter::new(44100, 48000, 1);
        assert_eq!(got, convert_in_blocks(&mut conv, &signal, 1));
        assert_eq!(second.take_failure(), None);
    }
}
