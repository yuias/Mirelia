//! The playback engine: slots, decode worker, real-time render logic,
//! position publishing, and event delivery.
//!
//! Thread model:
//! - Host thread: all `Engine` methods. Never blocks on audio.
//! - Decode worker: owns the decoders, fills the sample ring. May block,
//!   sleep, and allocate freely.
//! - Render thread (owned by the output backend): runs the callback built in
//!   `ensure_stream`. Never allocates, never locks, never calls out.
//!
//! Decode → render communication is exclusively SPSC rings plus atomics:
//! samples, control tags (track boundaries / seek rebases), and the flush
//! protocol (`flush_epoch`/`flush_ack`) that empties the rings from the
//! consumer side on seeks and track replacement.

use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, AtomicI64, AtomicU8, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, mpsc};
use std::thread::JoinHandle;
use std::time::Duration;

use mirelia_decode::{DecodeError, Decoder};

use crate::events::{EngineEvent, EventKind, EventSink};
use crate::output::{
    DeviceInfo, DitherControl, OutputBackend, OutputError, OutputStream, RenderInfo, ShareMode,
    StreamFormat, StreamOptions,
};
use crate::resample::{ResampleStage, ResamplerFactory, SelectedResampler};
use crate::spsc::{self, Consumer, Producer, SpscCore};
use crate::{DitherMode, EngineConfig, Position, State, host_time_ns};

/// Engine failure classification, mirroring `MireliaResult`.
#[derive(Debug)]
pub enum EngineError {
    /// Bad argument from the host.
    InvalidArg(String),
    /// Feature or format outside the current scope.
    Unsupported(String),
    /// Device missing or gone.
    DeviceLost(String),
    /// Device denied the access mode.
    DeviceBusy(String),
    /// Decoding failed.
    Decode(String),
    /// File I/O failed.
    Io(String),
    /// Stream format not usable.
    Format(String),
    /// Call not valid in the current state.
    State(String),
    /// Anything else.
    Internal(String),
}

impl std::fmt::Display for EngineError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let (label, msg) = match self {
            EngineError::InvalidArg(m) => ("invalid argument", m),
            EngineError::Unsupported(m) => ("unsupported", m),
            EngineError::DeviceLost(m) => ("device lost", m),
            EngineError::DeviceBusy(m) => ("device busy", m),
            EngineError::Decode(m) => ("decode error", m),
            EngineError::Io(m) => ("I/O error", m),
            EngineError::Format(m) => ("format error", m),
            EngineError::State(m) => ("invalid state", m),
            EngineError::Internal(m) => ("internal error", m),
        };
        write!(f, "{label}: {msg}")
    }
}

impl std::error::Error for EngineError {}

impl From<DecodeError> for EngineError {
    fn from(e: DecodeError) -> Self {
        match e {
            DecodeError::Io(io) => EngineError::Io(io.to_string()),
            DecodeError::Unsupported(m) => EngineError::Unsupported(m),
            DecodeError::Malformed(m) => EngineError::Decode(m),
        }
    }
}

impl From<OutputError> for EngineError {
    fn from(e: OutputError) -> Self {
        match e {
            OutputError::DeviceLost(m) => EngineError::DeviceLost(m),
            OutputError::DeviceBusy(m) => EngineError::DeviceBusy(m),
            OutputError::Format(m) => EngineError::Format(m),
            OutputError::Unsupported(m) => EngineError::Unsupported(m),
            OutputError::Internal(m) => EngineError::Internal(m),
        }
    }
}

/// The two source slots (`MireliaSlot`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Slot {
    /// What is playing (or will play).
    Current,
    /// What plays after the current source ends.
    Next,
}

/// Per-load parameters, mirroring `MireliaLoadParams`.
#[derive(Debug, Clone)]
pub struct LoadParams {
    /// Source file.
    pub path: PathBuf,
    /// First frame to play.
    pub start_frame: i64,
    /// One past the last frame to play; -1 = to end.
    pub end_frame: i64,
    /// ReplayGain to apply, in dB; 0 = unity.
    pub gain_db: f32,
    /// Host token echoed in events and positions.
    pub user_token: u64,
}

// ---- shared state ------------------------------------------------------

struct Shared {
    state: AtomicU8,
    playing: AtomicBool,
    volume_bits: AtomicU32,
    flush_epoch: AtomicU32,
    flush_ack: AtomicU32,
    /// Decode hit EOF with nothing queued next.
    eof_no_next: AtomicBool,
    /// Render already delivered TRACK_ENDED for the drained track.
    ended_signaled: AtomicBool,
    pub_token: AtomicU64,
    pub_frame: AtomicI64,
    pub_total: AtomicI64,
    pub_rate: AtomicU32,
    pub_fill_permille: AtomicU32,
}

impl Shared {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            state: AtomicU8::new(State::Idle.as_u8()),
            playing: AtomicBool::new(false),
            volume_bits: AtomicU32::new(1.0f32.to_bits()),
            flush_epoch: AtomicU32::new(0),
            flush_ack: AtomicU32::new(0),
            eof_no_next: AtomicBool::new(false),
            ended_signaled: AtomicBool::new(false),
            pub_token: AtomicU64::new(0),
            pub_frame: AtomicI64::new(0),
            pub_total: AtomicI64::new(-1),
            pub_rate: AtomicU32::new(0),
            pub_fill_permille: AtomicU32::new(0),
        })
    }

    fn set_state(&self, s: State) {
        self.state.store(s.as_u8(), Ordering::Release);
    }
}

/// Control tags flowing decode → render alongside the samples.
#[derive(Debug, Clone, Copy, Default)]
struct Tag {
    /// 0 = empty slot, 1 = track start, 2 = position rebase (seek).
    kind: u8,
    /// Producer sample index at which this tag applies.
    at_sample: usize,
    token: u64,
    base_frame: i64,
    total_frames: i64,
    /// Rate of `base_frame` and `total_frames`, and of the published position.
    source_rate: u32,
}

const MIN_CONVERTIBLE_RATE: u32 = 1_000;
const MAX_CONVERTIBLE_RATE: u32 = 768_000;

const TAG_TRACK_START: u8 = 1;
const TAG_REBASE: u8 = 2;

enum Cmd {
    SetNext(Option<Box<(Decoder, LoadParams)>>),
    Seek(i64),
    SkipToNext,
    Shutdown,
}

// ---- engine ------------------------------------------------------------

struct DeviceRequest {
    id: Option<String>,
    mode: ShareMode,
    /// Rate every stream must run at; `None` follows each source's rate.
    output_rate: Option<u32>,
}

struct DeviceState {
    format: StreamFormat,
    ring: Arc<SpscCore<f32>>,
    tags: Arc<SpscCore<Tag>>,
    // Dropped last: dropping stops the render thread which owns consumers.
    _stream: Box<dyn OutputStream>,
}

struct Session {
    worker: Option<JoinHandle<()>>,
    cmd: mpsc::Sender<Cmd>,
    format: StreamFormat,
}

/// The audio engine. One per backend library instance.
pub struct Engine {
    config: EngineConfig,
    backend: Box<dyn OutputBackend>,
    shared: Arc<Shared>,
    request: Option<DeviceRequest>,
    device: Option<DeviceState>,
    session: Option<Session>,
    ev_decode: Arc<SpscCore<EngineEvent>>,
    ev_decode_rx: Consumer<EngineEvent>,
    ev_render: Arc<SpscCore<EngineEvent>>,
    ev_render_rx: Consumer<EngineEvent>,
    // Backend-reported device conditions (change/loss); see `EventSink`.
    // A separate ring because its producer is not the render callback's:
    // it is owned by the backend's own thread(s) instead.
    ev_device: Arc<SpscCore<EngineEvent>>,
    ev_device_rx: Consumer<EngineEvent>,
    // Kept apart from the device state: `close_device` and `open_device` leave
    // the selection alone, so a device recovery does not lose it.
    resampler: Option<Arc<SelectedResampler>>,
}

impl Engine {
    /// Creates an engine over the given output backend.
    pub fn new(config: EngineConfig, backend: Box<dyn OutputBackend>) -> Self {
        let ev_decode = SpscCore::<EngineEvent>::new(64);
        let ev_render = SpscCore::<EngineEvent>::new(64);
        let ev_device = SpscCore::<EngineEvent>::new(16); // device events are rare
        Self {
            config,
            backend,
            shared: Shared::new(),
            request: None,
            device: None,
            session: None,
            ev_decode_rx: spsc::consumer(ev_decode.clone()),
            ev_decode,
            ev_render_rx: spsc::consumer(ev_render.clone()),
            ev_render,
            ev_device_rx: spsc::consumer(ev_device.clone()),
            ev_device,
            resampler: None,
        }
    }

    /// The configuration the engine was created with.
    pub fn config(&self) -> &EngineConfig {
        &self.config
    }

    /// The platform API behind the installed backend.
    pub fn backend_name(&self) -> &'static str {
        self.backend.name()
    }

    /// Lists playback devices.
    pub fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, EngineError> {
        Ok(self.backend.enumerate()?)
    }

    /// Selects a device (None = system default). `desired` fixes the output
    /// rate of every stream; its channel count decides when the stream opens:
    /// - `None`: the stream opens lazily at the first load and follows each
    ///   source's format; the returned format is the device's native one.
    /// - `channels == 0`: the rate is fixed and the channel count follows the
    ///   source, so the stream also opens at the first load (the channel count
    ///   is not known earlier); the returned format is the desired rate with
    ///   the device's native channel count.
    /// - `channels != 0`: the rate is fixed and the stream opens now.
    pub fn open_device(
        &mut self,
        device_id: Option<&str>,
        desired: Option<StreamFormat>,
        mode: ShareMode,
    ) -> Result<StreamFormat, EngineError> {
        self.close_device();
        let probe = self.backend.probe_device(device_id)?;
        self.request = Some(DeviceRequest {
            id: device_id.map(str::to_owned),
            mode,
            output_rate: desired.map(|d| d.sample_rate),
        });
        match desired {
            Some(fmt) if fmt.channels != 0 => {
                self.ensure_stream(fmt)?;
                Ok(fmt)
            }
            Some(fmt) => Ok(StreamFormat {
                sample_rate: fmt.sample_rate,
                channels: probe.native_channels,
            }),
            None => Ok(StreamFormat {
                sample_rate: probe.native_rate,
                channels: probe.native_channels,
            }),
        }
    }

    /// Stops everything and releases the device.
    pub fn close_device(&mut self) {
        self.shutdown_worker();
        self.device = None;
        self.request = None;
        self.reset_transport();
    }

    /// Loads a source into a slot. `Current` replaces what is playing
    /// (position resets; playback continues if the engine was playing);
    /// `Next` queues the follow-up for auto-advance.
    pub fn load(&mut self, slot: Slot, params: LoadParams) -> Result<(), EngineError> {
        let decoder = Decoder::open(&params.path)?;
        let source = StreamFormat {
            sample_rate: decoder.info().sample_rate,
            channels: decoder.info().channels,
        };
        if source.sample_rate == 0 {
            return Err(EngineError::Format("source has no sample rate".into()));
        }

        let fmt = self.output_format(source);
        if fmt.sample_rate != source.sample_rate && !self.config.allow_resample {
            return Err(EngineError::Format(format!(
                "source is {} Hz but the output is fixed at {} Hz and resampling is disabled",
                source.sample_rate, fmt.sample_rate
            )));
        }

        // The rate comes from the file and the filter's size scales with the
        // ratio: a corrupt 1 Hz header would ask for gigabytes per channel.
        if fmt.sample_rate != source.sample_rate
            && !(MIN_CONVERTIBLE_RATE..=MAX_CONVERTIBLE_RATE).contains(&source.sample_rate)
        {
            return Err(EngineError::Format(format!(
                "cannot convert a source rate of {} Hz",
                source.sample_rate
            )));
        }

        match slot {
            Slot::Current => {
                if self.request.is_none() {
                    return Err(EngineError::State("open a device before loading".into()));
                }

                self.shutdown_worker();
                self.ensure_stream(fmt)?;
                // A reused stream skips the store in `ensure_stream`, and a
                // seek before the first track tag is applied scales by this.
                self.shared
                    .pub_rate
                    .store(source.sample_rate, Ordering::Release);
                self.shared.eof_no_next.store(false, Ordering::Release);
                self.shared.ended_signaled.store(false, Ordering::Release);
                self.spawn_worker(decoder, params, fmt);
                Ok(())
            }
            Slot::Next => {
                let Some(session) = &self.session else {
                    return Err(EngineError::State("load a current track first".into()));
                };
                // Both sides are output formats: under a fixed rate only a
                // channel-count change differs.
                if fmt != session.format {
                    return Err(EngineError::Unsupported(
                        "gapless across format changes is not supported; reload as CURRENT instead"
                            .into(),
                    ));
                }

                self.shared.eof_no_next.store(false, Ordering::Release);
                session
                    .cmd
                    .send(Cmd::SetNext(Some(Box::new((decoder, params)))))
                    .map_err(|_| EngineError::Internal("decode worker gone".into()))
            }
        }
    }

    /// Clears a slot. Clearing `Current` unloads everything.
    pub fn clear(&mut self, slot: Slot) -> Result<(), EngineError> {
        match slot {
            Slot::Current => {
                self.shutdown_worker();
                self.reset_transport();
                Ok(())
            }
            Slot::Next => match &self.session {
                Some(session) => session
                    .cmd
                    .send(Cmd::SetNext(None))
                    .map_err(|_| EngineError::Internal("decode worker gone".into())),
                None => Ok(()),
            },
        }
    }

    /// Starts (or resumes) playback.
    pub fn play(&mut self) -> Result<(), EngineError> {
        if self.session.is_none() {
            return Err(EngineError::State("nothing loaded".into()));
        }

        self.shared.playing.store(true, Ordering::Release);
        self.shared.set_state(State::Playing);
        Ok(())
    }

    /// Pauses playback, keeping the position.
    pub fn pause(&mut self) -> Result<(), EngineError> {
        if self.session.is_none() {
            return Err(EngineError::State("nothing loaded".into()));
        }

        self.shared.playing.store(false, Ordering::Release);
        self.shared.set_state(State::Paused);
        Ok(())
    }

    /// Stops playback and clears both slots. The device stays open.
    pub fn stop(&mut self) -> Result<(), EngineError> {
        self.shutdown_worker();
        self.reset_transport();
        Ok(())
    }

    /// Seeks within the current source.
    pub fn seek(&mut self, frame: i64) -> Result<(), EngineError> {
        if frame < 0 {
            return Err(EngineError::InvalidArg("negative seek target".into()));
        }

        match &self.session {
            Some(session) => session
                .cmd
                .send(Cmd::Seek(frame))
                .map_err(|_| EngineError::Internal("decode worker gone".into())),
            None => Err(EngineError::State("nothing loaded".into())),
        }
    }

    /// Jumps to the next slot immediately (not gapless).
    pub fn skip_to_next(&mut self) -> Result<(), EngineError> {
        match &self.session {
            Some(session) => session
                .cmd
                .send(Cmd::SkipToNext)
                .map_err(|_| EngineError::Internal("decode worker gone".into())),
            None => Err(EngineError::State("nothing loaded".into())),
        }
    }

    /// Sets the linear output gain (applied after per-track ReplayGain).
    pub fn set_volume(&mut self, gain: f32) -> Result<(), EngineError> {
        if !(0.0..=4.0).contains(&gain) {
            return Err(EngineError::InvalidArg("volume outside 0.0..=4.0".into()));
        }

        self.shared
            .volume_bits
            .store(gain.to_bits(), Ordering::Release);
        Ok(())
    }

    /// Selects the resampler used for tracks whose rate differs from the
    /// output rate; `None` selects the built-in one. A running worker keeps its
    /// selection until the next load into [`Slot::Current`] starts a new one.
    /// Selecting again also re-enables a plugin that failed earlier.
    pub fn set_resampler(&mut self, factory: Option<Arc<dyn ResamplerFactory>>) {
        self.resampler = factory.map(|f| Arc::new(SelectedResampler::new(f)));
    }

    /// Snapshot of the current position.
    pub fn position(&self) -> Position {
        let s = &self.shared;
        Position {
            state: State::from_u8(s.state.load(Ordering::Acquire)),
            user_token: s.pub_token.load(Ordering::Acquire),
            frame: s.pub_frame.load(Ordering::Acquire),
            total_frames: s.pub_total.load(Ordering::Acquire),
            sample_rate: s.pub_rate.load(Ordering::Acquire),
            host_time_ns: host_time_ns(),
            buffer_fill: s.pub_fill_permille.load(Ordering::Acquire) as f32 / 1000.0,
        }
    }

    /// Drains up to `out.len()` pending events; returns how many were written.
    pub fn poll_events(&mut self, out: &mut [EngineEvent]) -> usize {
        let mut n = self.ev_decode_rx.pop_slice(out);
        n += self.ev_render_rx.pop_slice(&mut out[n..]);
        n += self.ev_device_rx.pop_slice(&mut out[n..]);
        n
    }

    // ---- internals -----------------------------------------------------

    /// The format the stream must have for this source: the fixed output rate
    /// when the host asked for one, the source's own rate otherwise. Channels
    /// always follow the source.
    fn output_format(&self, source: StreamFormat) -> StreamFormat {
        StreamFormat {
            sample_rate: self
                .request
                .as_ref()
                .and_then(|r| r.output_rate)
                .unwrap_or(source.sample_rate),
            channels: source.channels,
        }
    }

    fn reset_transport(&self) {
        let s = &self.shared;
        s.playing.store(false, Ordering::Release);
        s.set_state(State::Idle);
        s.pub_token.store(0, Ordering::Release);
        s.pub_frame.store(0, Ordering::Release);
        s.pub_total.store(-1, Ordering::Release);
        s.eof_no_next.store(false, Ordering::Release);
        s.ended_signaled.store(false, Ordering::Release);
    }

    fn shutdown_worker(&mut self) {
        if let Some(mut session) = self.session.take() {
            let _ = session.cmd.send(Cmd::Shutdown);
            if let Some(handle) = session.worker.take() {
                let _ = handle.join();
            }
        }
    }

    /// Opens (or reuses) the stream for `fmt`. Callers must have shut the
    /// worker down first when a reopen is possible: the flush protocol needs
    /// the render thread of the *new* stream, not the old one.
    fn ensure_stream(&mut self, fmt: StreamFormat) -> Result<(), EngineError> {
        if let Some(device) = &self.device
            && device.format == fmt
        {
            return Ok(());
        }

        self.device = None; // drop the old stream (stops its render thread)

        let request = self
            .request
            .as_ref()
            .ok_or_else(|| EngineError::State("no device selected".into()))?;

        let samples_per_sec = fmt.sample_rate as usize * usize::from(fmt.channels);
        let depth_ms = self.config.decode_ahead_ms.max(200) as usize;
        let ring = SpscCore::<f32>::new((samples_per_sec * depth_ms / 1000).max(8192));
        let tags = SpscCore::<Tag>::new(16);

        // Starts on only for `Always`; under `Resampled` the render callback
        // switches it per block once it knows which track is audible.
        let dither = DitherOutput {
            mode: self.config.dither,
            control: DitherControl::new(self.config.dither == DitherMode::Always),
        };

        let options = StreamOptions {
            mode: request.mode,
            dither: dither.control.clone(),
        };

        let callback = render_callback(
            spsc::consumer(ring.clone()),
            spsc::consumer(tags.clone()),
            spsc::producer(self.ev_render.clone()),
            self.shared.clone(),
            fmt,
            ring.capacity(),
            dither,
        );

        let events = EventSink::new(spsc::producer(self.ev_device.clone()));

        let stream =
            self.backend
                .open_stream(request.id.as_deref(), fmt, options, callback, events)?;
        self.shared
            .pub_rate
            .store(fmt.sample_rate, Ordering::Release);
        self.device = Some(DeviceState {
            format: fmt,
            ring,
            tags,
            _stream: stream,
        });
        Ok(())
    }

    fn spawn_worker(&mut self, decoder: Decoder, params: LoadParams, fmt: StreamFormat) {
        let device = self.device.as_ref().expect("stream must be open");
        let ring = spsc::producer(device.ring.clone());
        let tags = spsc::producer(device.tags.clone());
        let events = spsc::producer(self.ev_decode.clone());
        let shared = self.shared.clone();
        let (tx, rx) = mpsc::channel();
        let state = WorkerState {
            gain: db_to_linear(params.gain_db),
            current: decoder,
            params,
            next: None,
            at_eof: false,
            queue_empty_sent: false,
            leftover: Vec::new(),
            leftover_pos: 0,
            output_rate: fmt.sample_rate,
            converter: None,
            resampler: self.resampler.clone(),
            convert_in: Vec::new(),
        };

        let handle = std::thread::Builder::new()
            .name("mirelia-decode".into())
            .spawn(move || worker_loop(state, ring, tags, events, shared, rx))
            .expect("spawn decode worker");

        self.session = Some(Session {
            worker: Some(handle),
            cmd: tx,
            format: fmt,
        });
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.shutdown_worker();
    }
}

// ---- decode worker -----------------------------------------------------

fn db_to_linear(db: f32) -> f32 {
    if db == 0.0 {
        1.0
    } else {
        10f32.powf(db / 20.0)
    }
}

struct WorkerState {
    current: Decoder,
    params: LoadParams,
    gain: f32,
    next: Option<Box<(Decoder, LoadParams)>>,
    at_eof: bool,
    queue_empty_sent: bool,
    /// Gain-adjusted samples not yet pushed into the ring.
    leftover: Vec<f32>,
    leftover_pos: usize,
    /// Rate of the stream the ring feeds.
    output_rate: u32,
    /// Present while the current track's rate differs from `output_rate`.
    converter: Option<ResampleStage>,
    /// The engine's resampler selection when this worker started; `None` uses
    /// the built-in resampler.
    resampler: Option<Arc<SelectedResampler>>,
    /// Swap buffer: holds the source-rate block while `leftover` receives the
    /// converted output.
    convert_in: Vec<f32>,
}

/// Puts the converter in the state a new run for `w.current` needs. A track at
/// the output rate gets none, so its samples reach the ring untouched.
fn reset_converter(w: &mut WorkerState) {
    let rate = w.current.info().sample_rate;
    if rate == w.output_rate {
        w.converter = None;
        return;
    }

    match &mut w.converter {
        Some(c) if c.in_rate() == rate => c.reset(),
        slot => {
            *slot = Some(ResampleStage::new(
                w.resampler.clone(),
                rate,
                w.output_rate,
                usize::from(w.current.info().channels),
            ));
        }
    }
}

fn worker_loop(
    mut w: WorkerState,
    mut ring: Producer<f32>,
    mut tags: Producer<Tag>,
    mut events: Producer<EngineEvent>,
    shared: Arc<Shared>,
    cmd: mpsc::Receiver<Cmd>,
) {
    // Empty out whatever the previous session left in the rings, then
    // announce this track.
    flush(&shared);
    reset_converter(&mut w);
    start_track(&mut w, &mut tags, &mut events, &ring, TAG_TRACK_START, None);

    loop {
        // Report a plugin failure the stage absorbed. Each call that can record
        // one returns here before the stage can be replaced.
        if let Some(code) = w.converter.as_mut().and_then(ResampleStage::take_failure) {
            push_event(&mut events, EventKind::Error, w.params.user_token, code);
        }

        // Commands first; they beat production.
        loop {
            match cmd.try_recv() {
                Ok(Cmd::Shutdown) => return,
                Ok(Cmd::SetNext(next)) => {
                    w.next = next;
                    w.queue_empty_sent = false;
                }
                Ok(Cmd::Seek(frame)) => {
                    handle_seek(&mut w, &shared, &mut tags, &mut events, &ring, frame)
                }
                Ok(Cmd::SkipToNext) => {
                    if w.next.is_some() {
                        let (decoder, params) = *w.next.take().unwrap();
                        w.current = decoder;
                        w.gain = db_to_linear(params.gain_db);
                        w.params = params;
                        w.leftover.clear();
                        w.leftover_pos = 0;
                        w.at_eof = false;
                        w.queue_empty_sent = false;
                        shared.ended_signaled.store(false, Ordering::Release);
                        // The ring is discarded, so the filter's tail goes too.
                        reset_converter(&mut w);
                        flush(&shared);
                        start_track(&mut w, &mut tags, &mut events, &ring, TAG_TRACK_START, None);
                    }
                }
                Err(mpsc::TryRecvError::Empty) => break,
                Err(mpsc::TryRecvError::Disconnected) => return,
            }
        }

        // Push pending samples before decoding more.
        if w.leftover_pos < w.leftover.len() {
            let pushed = ring.push_slice(&w.leftover[w.leftover_pos..]);
            w.leftover_pos += pushed;
            if w.leftover_pos < w.leftover.len() {
                std::thread::sleep(Duration::from_millis(2));
                continue;
            }
        }

        if w.at_eof {
            // Same source rate and the next track is already queued: keep the
            // filter running so the join stays gapless. The tail of the ending
            // track is then emitted as the next track's samples push through.
            let continues = w.converter.as_ref().is_some_and(|c| {
                c.pending_frames() > 0
                    && w.next
                        .as_ref()
                        .is_some_and(|n| n.0.info().sample_rate == c.in_rate())
            });

            // Otherwise the samples still inside the filter belong to the
            // ending track: push them before announcing the next track or the
            // end of the queue.
            if !continues && w.converter.as_ref().is_some_and(|c| c.pending_frames() > 0) {
                w.leftover.clear();
                w.leftover_pos = 0;
                if let Some(c) = w.converter.as_mut() {
                    c.flush(&mut w.leftover);
                }

                continue;
            }

            if let Some(boxed) = w.next.take() {
                // Auto-advance: same stream, boundary tag marks the switch.
                let (decoder, params) = *boxed;
                w.current = decoder;
                w.gain = db_to_linear(params.gain_db);
                w.params = params;
                w.at_eof = false;
                w.queue_empty_sent = false;
                if !continues {
                    reset_converter(&mut w);
                }

                start_track(&mut w, &mut tags, &mut events, &ring, TAG_TRACK_START, None);
            } else {
                if !w.queue_empty_sent {
                    w.queue_empty_sent = true;
                    shared.eof_no_next.store(true, Ordering::Release);
                    push_event(&mut events, EventKind::QueueEmpty, w.params.user_token, 0);
                }

                std::thread::sleep(Duration::from_millis(5));
                continue;
            }
        }

        if ring.free() < 2048 {
            std::thread::sleep(Duration::from_millis(3));
            continue;
        }

        let channels = usize::from(w.current.info().channels);
        let gain = w.gain;
        let mut got_block = false;
        match w.current.next_block() {
            Ok(Some(block)) => {
                w.leftover.clear();
                w.leftover_pos = 0;
                if gain == 1.0 {
                    w.leftover.extend_from_slice(block);
                } else {
                    w.leftover.extend(block.iter().map(|s| s * gain));
                }

                got_block = true;
            }
            Ok(None) => {
                w.at_eof = true;
            }
            Err(e) => {
                let code = match e {
                    DecodeError::Io(_) => -6,
                    DecodeError::Unsupported(_) => -7,
                    DecodeError::Malformed(_) => -5,
                };
                push_event(&mut events, EventKind::Error, w.params.user_token, code);
                w.at_eof = true;
            }
        }

        // end_frame cap: the decoder position is the frame *after* the
        // block just fetched, so trim anything past the cap.
        if got_block && w.params.end_frame >= 0 {
            let over = w.current.position() as i64 - w.params.end_frame;
            if over > 0 {
                let keep = w.leftover.len().saturating_sub(over as usize * channels);
                w.leftover.truncate(keep);
                w.at_eof = true;
            }
        }

        // Convert after the trim above, which counts in source frames.
        if got_block && let Some(c) = w.converter.as_mut() {
            std::mem::swap(&mut w.leftover, &mut w.convert_in);
            w.leftover.clear();
            c.process(&w.convert_in, &mut w.leftover);
        }
    }
}

fn handle_seek(
    w: &mut WorkerState,
    shared: &Arc<Shared>,
    tags: &mut Producer<Tag>,
    events: &mut Producer<EngineEvent>,
    ring: &Producer<f32>,
    frame: i64,
) {
    let target = if w.params.end_frame >= 0 {
        frame.min(w.params.end_frame)
    } else {
        frame
    };
    match w.current.seek(target.max(0) as u64) {
        Ok(()) => {
            w.leftover.clear();
            w.leftover_pos = 0;
            w.at_eof = false;
            w.queue_empty_sent = false;
            shared.eof_no_next.store(false, Ordering::Release);
            shared.ended_signaled.store(false, Ordering::Release);
            reset_converter(w);
            flush(shared);
            start_track(w, tags, events, ring, TAG_REBASE, Some(target));
        }
        Err(e) => {
            let code = match e {
                DecodeError::Io(_) => -6,
                DecodeError::Unsupported(_) => -7,
                DecodeError::Malformed(_) => -5,
            };
            push_event(events, EventKind::Error, w.params.user_token, code);
        }
    }
}

/// Publishes a track-start or rebase tag anchored at the current producer
/// index. `base_override` carries the seek target for rebases.
fn start_track(
    w: &mut WorkerState,
    tags: &mut Producer<Tag>,
    _events: &mut Producer<EngineEvent>,
    ring: &Producer<f32>,
    kind: u8,
    base_override: Option<i64>,
) {
    if kind == TAG_TRACK_START && w.params.start_frame > 0 {
        // Errors surface on the first next_block instead; a failed head trim
        // just plays from 0.
        let _ = w.current.seek(w.params.start_frame as u64);
    }

    let base = base_override.unwrap_or(w.params.start_frame.max(0));
    let total = w
        .current
        .info()
        .total_frames
        .map(|t| t as i64)
        .unwrap_or(-1);
    // On a join that kept the filter running, the first sample of the new track
    // comes out after the tail the filter still owes the previous one. Every
    // other path has no pending frames (flushed, reset, or no converter).
    let head = w
        .converter
        .as_ref()
        .map_or(0, |c| c.pending_frames() as usize)
        * usize::from(w.current.info().channels);
    // A 16-slot tag ring cannot overflow in practice (tags are rare); if it
    // ever is full the tag is dropped and positions stay on the old base,
    // which is survivable — never block here.
    let _ = tags.push_slice(&[Tag {
        kind,
        at_sample: ring.write_index() + head,
        token: w.params.user_token,
        base_frame: base,
        total_frames: total,
        source_rate: w.current.info().sample_rate,
    }]);
}

/// Seek/replace flush: bump the epoch and wait for the render thread to
/// discard the rings. The render thread always runs while a worker exists.
fn flush(shared: &Shared) {
    let epoch = shared.flush_epoch.fetch_add(1, Ordering::AcqRel) + 1;
    while shared.flush_ack.load(Ordering::Acquire) != epoch {
        std::thread::sleep(Duration::from_millis(1));
    }
}

fn push_event(events: &mut Producer<EngineEvent>, kind: EventKind, token: u64, code: i32) {
    let _ = events.push_slice(&[EngineEvent {
        kind,
        token,
        code,
        time_ns: host_time_ns(),
    }]);
}

// ---- render callback ---------------------------------------------------

/// The dither policy and the flag that carries each block's decision to the
/// backend. One parameter for `render_callback`, which is at clippy's
/// argument limit otherwise.
struct DitherOutput {
    mode: DitherMode,
    control: DitherControl,
}

/// Builds the RT callback. Everything it needs is owned or atomic;
/// the body allocates nothing and takes no locks.
fn render_callback(
    mut samples: Consumer<f32>,
    mut tags: Consumer<Tag>,
    mut events: Producer<EngineEvent>,
    shared: Arc<Shared>,
    fmt: StreamFormat,
    ring_capacity: usize,
    dither: DitherOutput,
) -> crate::output::RenderCallback {
    let channels = usize::from(fmt.channels);
    let mut volume = f32::from_bits(shared.volume_bits.load(Ordering::Acquire));
    let mut token = 0u64;
    let mut base_frame = 0i64;
    let mut track_frames = 0i64;
    let output_rate = i64::from(fmt.sample_rate);
    let mut source_rate = output_rate;

    Box::new(move |buf: &mut [f32], info: &RenderInfo| {
        // Everything below runs under the no-allocation guard; a debug
        // build aborts on any alloc/dealloc until the callback returns.
        let _rt = crate::rt_guard::RtSection::enter();

        // Flush protocol: discard ring contents when the worker asks.
        let epoch = shared.flush_epoch.load(Ordering::Acquire);
        if epoch != shared.flush_ack.load(Ordering::Relaxed) {
            samples.clear();
            tags.clear();
            shared.flush_ack.store(epoch, Ordering::Release);
        }

        let playing = shared.playing.load(Ordering::Acquire);
        if !playing {
            // Only `Always` dithers silence; the other modes keep it digital
            // silence.
            dither
                .control
                .set_enabled(dither.mode == DitherMode::Always);
            buf.fill(0.0);
            shared.pub_fill_permille.store(
                (samples.len() * 1000 / ring_capacity) as u32,
                Ordering::Release,
            );
            return;
        }

        // Whether this block holds audio, and whether any of it is a
        // pass-through track. Tracking every pop (not just the first and last)
        // keeps a short pass-through track between two resampled ones undithered.
        let mut has_audio = false;
        let mut has_pass_through = false;
        let mut filled = 0usize;
        while filled < buf.len() {
            // Apply a control tag once its sample index is reached.
            let limit = match tags.peek() {
                Some(tag) if tag.at_sample <= samples.read_index() => {
                    tags.pop();
                    if tag.kind == TAG_TRACK_START {
                        if token != 0 {
                            push_event(&mut events, EventKind::TrackEnded, token, 0);
                        }
                        push_event(&mut events, EventKind::TrackStarted, tag.token, 0);
                    }
                    token = tag.token;
                    base_frame = tag.base_frame;
                    track_frames = 0;
                    source_rate = i64::from(tag.source_rate);
                    shared.pub_token.store(token, Ordering::Release);
                    shared.pub_total.store(tag.total_frames, Ordering::Release);
                    shared.pub_rate.store(tag.source_rate, Ordering::Release);
                    continue;
                }
                Some(tag) => tag.at_sample - samples.read_index(),
                None => usize::MAX,
            };

            let want = (buf.len() - filled).min(limit);
            let got = samples.pop_slice(&mut buf[filled..filled + want]);
            filled += got;
            track_frames += (got / channels) as i64;
            if got > 0 {
                has_audio = true;
                has_pass_through |= source_rate == output_rate;
            }

            if got < want {
                // Ring is dry: either the track is over or we are stalling.
                if shared.eof_no_next.load(Ordering::Acquire) && tags.peek().is_none() {
                    if !shared.ended_signaled.swap(true, Ordering::AcqRel) {
                        push_event(&mut events, EventKind::TrackEnded, token, 0);
                        push_event(&mut events, EventKind::StateChanged, 0, 0);
                        shared.playing.store(false, Ordering::Release);
                        shared.set_state(State::Idle);
                    }
                } else if State::from_u8(shared.state.load(Ordering::Acquire)) == State::Playing {
                    shared.set_state(State::Stalled);
                    push_event(&mut events, EventKind::StateChanged, token, 0);
                }

                break;
            }

            if State::from_u8(shared.state.load(Ordering::Acquire)) == State::Stalled {
                shared.set_state(State::Playing);
                push_event(&mut events, EventKind::StateChanged, token, 0);
            }
        }

        buf[filled..].fill(0.0);

        dither.control.set_enabled(match dither.mode {
            DitherMode::Off => false,
            DitherMode::Always => true,
            DitherMode::Resampled => has_audio && !has_pass_through,
        });

        // Smoothed volume over the delivered audio.
        let target = f32::from_bits(shared.volume_bits.load(Ordering::Acquire));
        for frame in buf[..filled].chunks_mut(channels) {
            volume += (target - volume) * 0.005;
            for sample in frame {
                *sample *= volume;
            }
        }

        // Publish the audible position in source frames: consumed minus what
        // the device still has queued ahead of the speaker. Both counters are
        // at the output rate, so subtract first and scale the result.
        let audible = base_frame
            + (track_frames - i64::from(info.device_queued_frames)).max(0) * source_rate
                / output_rate;
        shared.pub_frame.store(audible, Ordering::Release);
        shared.pub_fill_permille.store(
            (samples.len() * 1000 / ring_capacity) as u32,
            Ordering::Release,
        );
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::output::OutputStream;
    use crate::resample::{Converter, MIRELIA_ERR_PLUGIN, ResamplerError, StreamResampler};
    use std::path::{Path, PathBuf};
    use std::time::Instant;

    fn fixture(name: &str) -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../../tests/fixtures")
            .join(name)
    }

    /// A backend that pulls the callback from a plain thread much faster
    /// than real time. Lets the whole engine run in CI with no audio device.
    struct MockBackend;

    struct MockStream {
        stop: Arc<AtomicBool>,
        join: Option<JoinHandle<()>>,
        format: StreamFormat,
    }

    impl OutputStream for MockStream {
        fn format(&self) -> StreamFormat {
            self.format
        }
    }

    impl Drop for MockStream {
        fn drop(&mut self) {
            self.stop.store(true, Ordering::Release);
            if let Some(j) = self.join.take() {
                let _ = j.join();
            }
        }
    }

    impl OutputBackend for MockBackend {
        fn name(&self) -> &'static str {
            "mock"
        }

        fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, OutputError> {
            Ok(vec![self.probe_device(None)?])
        }

        fn probe_device(&mut self, _id: Option<&str>) -> Result<DeviceInfo, OutputError> {
            Ok(DeviceInfo {
                id: "mock".into(),
                name: "Mock Device".into(),
                is_default: true,
                native_rate: 44_100,
                native_channels: 2,
            })
        }

        fn open_stream(
            &mut self,
            _device_id: Option<&str>,
            format: StreamFormat,
            _options: StreamOptions,
            mut callback: crate::output::RenderCallback,
            _events: EventSink,
        ) -> Result<Box<dyn OutputStream>, OutputError> {
            let stop = Arc::new(AtomicBool::new(false));
            let stop2 = stop.clone();
            let period = 512 * usize::from(format.channels);
            let join = std::thread::spawn(move || {
                let mut buf = vec![0.0f32; period];
                while !stop2.load(Ordering::Acquire) {
                    callback(&mut buf, &RenderInfo::default());
                    std::thread::sleep(Duration::from_micros(500));
                }
            });
            Ok(Box::new(MockStream {
                stop,
                join: Some(join),
                format,
            }))
        }
    }

    /// Accumulates drained events into `seen` (draining discards, so tests
    /// must keep everything) and waits until one of `kind` has arrived.
    fn wait_for_event(
        engine: &mut Engine,
        seen: &mut Vec<EngineEvent>,
        kind: EventKind,
        timeout: Duration,
    ) -> bool {
        let deadline = Instant::now() + timeout;
        let mut out = [EngineEvent::default(); 16];
        loop {
            let n = engine.poll_events(&mut out);
            seen.extend_from_slice(&out[..n]);
            if seen.iter().any(|e| e.kind == kind) {
                return true;
            }
            if Instant::now() >= deadline {
                return false;
            }
            std::thread::sleep(Duration::from_millis(2));
        }
    }

    fn engine() -> Engine {
        Engine::new(EngineConfig::default(), Box::new(MockBackend))
    }

    fn load_params(name: &str, token: u64) -> LoadParams {
        LoadParams {
            path: fixture(name),
            start_frame: 0,
            end_frame: -1,
            gain_db: 0.0,
            user_token: token,
        }
    }

    #[test]
    fn plays_a_track_to_completion() {
        let mut e = engine();
        let mut seen = Vec::new();
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("fixture.wav", 7))
            .unwrap();
        e.play().unwrap();

        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::TrackStarted,
            Duration::from_secs(5)
        ));
        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::TrackEnded,
            Duration::from_secs(5)
        ));

        let pos = e.position();
        assert_eq!(pos.state, State::Idle);
        assert_eq!(pos.user_token, 7);
        assert_eq!(pos.total_frames, 17_640);
        assert!(pos.frame >= 17_000, "final position {}", pos.frame);
    }

    #[test]
    fn auto_advances_into_next_slot() {
        let mut e = engine();
        let mut seen = Vec::new();
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("fixture.wav", 1))
            .unwrap();
        e.load(Slot::Next, load_params("fixture.flac", 2)).unwrap();
        e.play().unwrap();

        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::TrackEnded,
            Duration::from_secs(5)
        ));
        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::QueueEmpty,
            Duration::from_secs(5)
        ));
        let pos = e.position();
        assert_eq!(pos.user_token, 2);
    }

    #[test]
    fn seek_rebases_position() {
        let mut e = engine();
        let mut seen = Vec::new();
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("fixture.flac", 3))
            .unwrap();
        e.play().unwrap();
        e.seek(10_000).unwrap();

        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::TrackEnded,
            Duration::from_secs(5)
        ));
        let pos = e.position();
        assert!(pos.frame >= 17_000, "position after seek {}", pos.frame);
    }

    #[test]
    fn transport_calls_require_a_loaded_track() {
        let mut e = engine();
        assert!(matches!(e.play(), Err(EngineError::State(_))));
        assert!(matches!(e.seek(0), Err(EngineError::State(_))));
        e.open_device(None, None, ShareMode::Shared).unwrap();
        assert!(matches!(e.play(), Err(EngineError::State(_))));
    }

    /// Like MockBackend, but appends everything the engine renders to a
    /// shared sink so tests can inspect the actual output stream.
    struct CapturingBackend {
        sink: Arc<std::sync::Mutex<Vec<f32>>>,
    }

    impl OutputBackend for CapturingBackend {
        fn name(&self) -> &'static str {
            "capture"
        }

        fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, OutputError> {
            Ok(vec![self.probe_device(None)?])
        }

        fn probe_device(&mut self, _id: Option<&str>) -> Result<DeviceInfo, OutputError> {
            Ok(DeviceInfo {
                id: "capture".into(),
                name: "Capture".into(),
                is_default: true,
                native_rate: 44_100,
                native_channels: 1,
            })
        }

        fn open_stream(
            &mut self,
            _device_id: Option<&str>,
            format: StreamFormat,
            _options: StreamOptions,
            mut callback: crate::output::RenderCallback,
            _events: EventSink,
        ) -> Result<Box<dyn OutputStream>, OutputError> {
            let stop = Arc::new(AtomicBool::new(false));
            let stop2 = stop.clone();
            let sink = self.sink.clone();
            let period = 512 * usize::from(format.channels);
            let join = std::thread::spawn(move || {
                let mut buf = vec![0.0f32; period];
                while !stop2.load(Ordering::Acquire) {
                    callback(&mut buf, &RenderInfo::default());
                    sink.lock().unwrap().extend_from_slice(&buf);
                    std::thread::sleep(Duration::from_micros(500));
                }
            });
            Ok(Box::new(MockStream {
                stop,
                join: Some(join),
                format,
            }))
        }
    }

    /// The end-to-end gapless check: two losslessly split halves played
    /// through CURRENT + NEXT must reproduce the master bit-exactly at the
    /// seam (kickoff §5.6's -90 dBFS requirement, tightened to equality).
    #[test]
    fn gapless_splice_reproduces_master_bit_exactly() {
        gapless_splice_is_bit_exact(None);
    }

    fn gapless_splice_is_bit_exact(desired: Option<StreamFormat>) {
        let master = {
            let mut d = mirelia_decode::Decoder::open(&fixture("gapless-master.wav")).unwrap();
            let mut all = Vec::new();
            while let Some(block) = d.next_block().unwrap() {
                all.extend_from_slice(block);
            }
            all
        };

        let sink = Arc::new(std::sync::Mutex::new(Vec::new()));
        let mut e = Engine::new(
            EngineConfig::default(),
            Box::new(CapturingBackend { sink: sink.clone() }),
        );
        let mut seen = Vec::new();
        e.open_device(None, desired, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("gapless-a.flac", 1))
            .unwrap();
        e.load(Slot::Next, load_params("gapless-b.flac", 2))
            .unwrap();
        // Let the decode worker prefill so playback starts without a stall.
        std::thread::sleep(Duration::from_millis(100));
        e.play().unwrap();

        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::QueueEmpty,
            Duration::from_secs(10)
        ));
        let deadline = Instant::now() + Duration::from_secs(10);
        while e.position().state != State::Idle {
            assert!(Instant::now() < deadline, "engine never went idle");
            std::thread::sleep(Duration::from_millis(2));
        }

        drop(e); // stops the capture thread
        let captured = sink.lock().unwrap().clone();

        // The stream is pre-play silence + master + trailing silence (the
        // callback runs from stream open, before play). Trim the silent
        // edges; any *interior* stall would leave extra zeros and fail the
        // length equality below. The master starts at 0.8, not 0, so the
        // leading trim cannot eat real samples.
        let start = captured.iter().position(|&s| s != 0.0).unwrap();
        let end = captured.iter().rposition(|&s| s != 0.0).unwrap() + 1;
        let trimmed = &captured[start..end];

        assert_eq!(trimmed.len(), master.len());
        assert_eq!(trimmed, &master[..]);
    }

    /// The render callback counts output frames but publishes source frames:
    /// 480 frames at 48 kHz are 441 frames of a 44.1 kHz source.
    #[test]
    fn position_is_published_in_source_frames() {
        let shared = Shared::new();
        shared.playing.store(true, Ordering::Release);
        let ring = SpscCore::<f32>::new(8192);
        let tag_ring = SpscCore::<Tag>::new(16);
        let mut samples = spsc::producer(ring.clone());
        let mut tags = spsc::producer(tag_ring.clone());
        let fmt = StreamFormat {
            sample_rate: 48_000,
            channels: 2,
        };
        let mut callback = render_callback(
            spsc::consumer(ring.clone()),
            spsc::consumer(tag_ring),
            spsc::producer(SpscCore::<EngineEvent>::new(64)),
            shared.clone(),
            fmt,
            ring.capacity(),
            DitherOutput {
                mode: DitherMode::Off,
                control: DitherControl::default(),
            },
        );

        tags.push_slice(&[Tag {
            kind: TAG_TRACK_START,
            at_sample: 0,
            token: 1,
            base_frame: 1000,
            total_frames: 100_000,
            source_rate: 44_100,
        }]);
        samples.push_slice(&[0.25; 480 * 2]);

        let mut buf = [0.0f32; 480 * 2];
        callback(&mut buf, &RenderInfo::default());

        assert_eq!(shared.pub_frame.load(Ordering::Acquire), 1441);
        assert_eq!(shared.pub_rate.load(Ordering::Acquire), 44_100);
    }

    /// One 48 kHz stereo render callback fed by hand, for the dither decision.
    struct DitherRig {
        control: DitherControl,
        shared: Arc<Shared>,
        samples: Producer<f32>,
        tags: Producer<Tag>,
        callback: crate::output::RenderCallback,
    }

    const RIG_FRAMES: usize = 480;

    impl DitherRig {
        fn new(mode: DitherMode) -> Self {
            let shared = Shared::new();
            shared.playing.store(true, Ordering::Release);
            let ring = SpscCore::<f32>::new(8192);
            let tag_ring = SpscCore::<Tag>::new(16);
            let control = DitherControl::default();
            let callback = render_callback(
                spsc::consumer(ring.clone()),
                spsc::consumer(tag_ring.clone()),
                spsc::producer(SpscCore::<EngineEvent>::new(64)),
                shared.clone(),
                StreamFormat {
                    sample_rate: 48_000,
                    channels: 2,
                },
                ring.capacity(),
                DitherOutput {
                    mode,
                    control: control.clone(),
                },
            );
            Self {
                control,
                shared,
                samples: spsc::producer(ring),
                tags: spsc::producer(tag_ring),
                callback,
            }
        }

        /// Queues a track start at a sample index of the producer stream.
        fn start_track(&mut self, at_sample: usize, source_rate: u32) {
            self.tags.push_slice(&[Tag {
                kind: TAG_TRACK_START,
                at_sample,
                token: 1,
                base_frame: 0,
                total_frames: 100_000,
                source_rate,
            }]);
        }

        /// Renders one block of `RIG_FRAMES` frames and returns whether it was
        /// to be dithered. Refills the ring so the block is always full audio.
        fn render(&mut self) -> bool {
            self.samples.push_slice(&[0.25; RIG_FRAMES * 2]);
            let mut buf = [0.0f32; RIG_FRAMES * 2];
            (self.callback)(&mut buf, &RenderInfo::default());
            self.control.is_enabled()
        }
    }

    #[test]
    fn dither_off_never_dithers() {
        let mut rig = DitherRig::new(DitherMode::Off);
        rig.start_track(0, 44_100);
        assert!(!rig.render());
        rig.start_track(RIG_FRAMES * 2, 48_000);
        assert!(!rig.render());
    }

    #[test]
    fn dither_always_dithers_every_block_even_when_paused() {
        let mut rig = DitherRig::new(DitherMode::Always);
        rig.start_track(0, 48_000);
        assert!(rig.render());
        rig.start_track(RIG_FRAMES * 2, 44_100);
        assert!(rig.render());

        rig.shared.playing.store(false, Ordering::Release);
        assert!(rig.render());
    }

    #[test]
    fn dither_resampled_follows_the_audible_track() {
        let mut rig = DitherRig::new(DitherMode::Resampled);

        // 44.1 kHz source on a 48 kHz stream is converted.
        rig.start_track(0, 44_100);
        assert!(rig.render());

        // A 48 kHz source passes through untouched.
        rig.start_track(RIG_FRAMES * 2, 48_000);
        assert!(!rig.render());
    }

    #[test]
    fn dither_resampled_leaves_a_block_straddling_a_boundary_undithered() {
        // Resampled to pass-through, boundary in the middle of the block.
        let mut rig = DitherRig::new(DitherMode::Resampled);
        rig.start_track(0, 44_100);
        assert!(rig.render());
        rig.start_track(RIG_FRAMES * 2 + RIG_FRAMES, 48_000);
        assert!(!rig.render());
        // The next block is wholly pass-through.
        assert!(!rig.render());

        // Pass-through to resampled, boundary in the middle of the block.
        let mut rig = DitherRig::new(DitherMode::Resampled);
        rig.start_track(0, 48_000);
        assert!(!rig.render());
        rig.start_track(RIG_FRAMES * 2 + RIG_FRAMES, 44_100);
        assert!(!rig.render());
        // The next block is wholly resampled and dithers again.
        assert!(rig.render());
    }

    #[test]
    fn dither_resampled_skips_a_pass_through_track_between_resampled_ones() {
        let mut rig = DitherRig::new(DitherMode::Resampled);
        rig.start_track(0, 44_100);
        rig.start_track(RIG_FRAMES / 2, 48_000);
        rig.start_track(RIG_FRAMES, 44_100);
        assert!(!rig.render());
    }

    #[test]
    fn dither_resampled_leaves_silence_and_pause_undithered() {
        let mut rig = DitherRig::new(DitherMode::Resampled);
        rig.start_track(0, 44_100);
        assert!(rig.render());

        // An empty ring renders silence.
        let mut buf = [0.0f32; RIG_FRAMES * 2];
        (rig.callback)(&mut buf, &RenderInfo::default());
        assert!(!rig.control.is_enabled());

        rig.shared.playing.store(false, Ordering::Release);
        assert!(!rig.render());
    }

    #[test]
    fn pause_freezes_position() {
        let mut e = engine();
        let mut seen = Vec::new();
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("fixture.wav", 9))
            .unwrap();
        e.play().unwrap();
        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::TrackStarted,
            Duration::from_secs(5)
        ));
        e.pause().unwrap();
        std::thread::sleep(Duration::from_millis(10));
        let a = e.position();
        std::thread::sleep(Duration::from_millis(30));
        let b = e.position();
        assert_eq!(a.frame, b.frame);
        assert_eq!(b.state, State::Paused);
    }

    /// Wraps `MockBackend` and remembers the format of every stream opened.
    struct RecordingBackend {
        inner: MockBackend,
        opened: Arc<std::sync::Mutex<Vec<StreamFormat>>>,
    }

    impl OutputBackend for RecordingBackend {
        fn name(&self) -> &'static str {
            "recording"
        }

        fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, OutputError> {
            self.inner.enumerate()
        }

        fn probe_device(&mut self, id: Option<&str>) -> Result<DeviceInfo, OutputError> {
            self.inner.probe_device(id)
        }

        fn open_stream(
            &mut self,
            device_id: Option<&str>,
            format: StreamFormat,
            options: StreamOptions,
            callback: crate::output::RenderCallback,
            events: EventSink,
        ) -> Result<Box<dyn OutputStream>, OutputError> {
            self.opened.lock().unwrap().push(format);
            self.inner
                .open_stream(device_id, format, options, callback, events)
        }
    }

    fn recording_engine(
        config: EngineConfig,
    ) -> (Engine, Arc<std::sync::Mutex<Vec<StreamFormat>>>) {
        let opened = Arc::new(std::sync::Mutex::new(Vec::new()));
        let backend = RecordingBackend {
            inner: MockBackend,
            opened: opened.clone(),
        };
        (Engine::new(config, Box::new(backend)), opened)
    }

    /// A WAV file in the temp directory that is deleted on drop. Declare it
    /// before the engine that plays it: on Windows the file cannot be removed
    /// while the engine's decoder still holds it open.
    struct TempWav(PathBuf);

    impl TempWav {
        fn path(&self) -> &Path {
            &self.0
        }
    }

    impl Drop for TempWav {
        fn drop(&mut self) {
            let _ = std::fs::remove_file(&self.0);
        }
    }

    /// Writes a 32-bit float WAV with a unique name under the temp directory.
    /// Samples are interleaved.
    fn write_wav(rate: u32, channels: u16, samples: &[f32]) -> TempWav {
        use std::sync::atomic::AtomicUsize;
        static COUNTER: AtomicUsize = AtomicUsize::new(0);

        let data_len = (samples.len() * 4) as u32;
        let block_align = channels * 4;
        let mut bytes = Vec::with_capacity(44 + samples.len() * 4);
        bytes.extend_from_slice(b"RIFF");
        bytes.extend_from_slice(&(36 + data_len).to_le_bytes());
        bytes.extend_from_slice(b"WAVEfmt ");
        bytes.extend_from_slice(&16u32.to_le_bytes());
        bytes.extend_from_slice(&3u16.to_le_bytes()); // IEEE float
        bytes.extend_from_slice(&channels.to_le_bytes());
        bytes.extend_from_slice(&rate.to_le_bytes());
        bytes.extend_from_slice(&(rate * u32::from(block_align)).to_le_bytes());
        bytes.extend_from_slice(&block_align.to_le_bytes());
        bytes.extend_from_slice(&32u16.to_le_bytes());
        bytes.extend_from_slice(b"data");
        bytes.extend_from_slice(&data_len.to_le_bytes());
        for s in samples {
            bytes.extend_from_slice(&s.to_le_bytes());
        }

        let path = std::env::temp_dir().join(format!(
            "mirelia-audio-test-{}-{}.wav",
            std::process::id(),
            COUNTER.fetch_add(1, Ordering::Relaxed)
        ));
        std::fs::write(&path, bytes).unwrap();
        TempWav(path)
    }

    /// A short test tone: `frames` frames of `channels` identical samples.
    fn tone(rate: u32, channels: u16, frames: usize) -> TempWav {
        let samples: Vec<f32> = (0..frames)
            .flat_map(|n| {
                let s = (n as f32 * 0.05).sin() * 0.5;
                std::iter::repeat_n(s, usize::from(channels))
            })
            .collect();
        write_wav(rate, channels, &samples)
    }

    fn params_for(wav: &TempWav, token: u64) -> LoadParams {
        LoadParams {
            path: wav.path().to_path_buf(),
            start_frame: 0,
            end_frame: -1,
            gain_db: 0.0,
            user_token: token,
        }
    }

    fn fixed(rate: u32) -> Option<StreamFormat> {
        Some(StreamFormat {
            sample_rate: rate,
            channels: 0,
        })
    }

    #[test]
    fn fixed_rate_equal_to_source_stays_bit_exact() {
        gapless_splice_is_bit_exact(fixed(44_100));
    }

    #[test]
    fn fixed_rate_opens_the_stream_at_that_rate_and_source_channels() {
        let (low, mid, stereo) = (
            tone(22_050, 1, 2205),
            tone(32_000, 1, 2205),
            tone(44_100, 2, 2205),
        );
        let (mut e, opened) = recording_engine(EngineConfig::default());
        let returned = e
            .open_device(None, fixed(48_000), ShareMode::Shared)
            .unwrap();
        assert_eq!(
            returned,
            StreamFormat {
                sample_rate: 48_000,
                channels: 2
            }
        );
        // The channel count is only known at load, so nothing opens yet.
        assert!(opened.lock().unwrap().is_empty());

        e.load(Slot::Current, params_for(&low, 1)).unwrap();
        // A different source rate with the same channels reuses the stream.
        e.load(Slot::Current, params_for(&mid, 2)).unwrap();
        e.load(Slot::Current, params_for(&stereo, 3)).unwrap();

        let want = |channels| StreamFormat {
            sample_rate: 48_000,
            channels,
        };
        assert_eq!(*opened.lock().unwrap(), [want(1), want(2)]);
    }

    #[test]
    fn fixed_rate_with_channels_opens_the_stream_immediately() {
        let (mut e, opened) = recording_engine(EngineConfig::default());
        let desired = StreamFormat {
            sample_rate: 96_000,
            channels: 2,
        };
        assert_eq!(
            e.open_device(None, Some(desired), ShareMode::Shared)
                .unwrap(),
            desired
        );
        assert_eq!(*opened.lock().unwrap(), [desired]);
    }

    #[test]
    fn fixed_rate_accepts_a_next_slot_of_a_different_source_rate() {
        let (current, next) = (tone(44_100, 1, 2205), tone(22_050, 1, 2205));
        let (mut e, _) = recording_engine(EngineConfig::default());
        e.open_device(None, fixed(48_000), ShareMode::Shared)
            .unwrap();
        e.load(Slot::Current, params_for(&current, 1)).unwrap();
        e.load(Slot::Next, params_for(&next, 2)).unwrap();
    }

    #[test]
    fn fixed_rate_still_refuses_a_next_slot_channel_change() {
        let (current, stereo) = (tone(44_100, 1, 2205), tone(44_100, 2, 2205));
        let (mut e, _) = recording_engine(EngineConfig::default());
        e.open_device(None, fixed(48_000), ShareMode::Shared)
            .unwrap();
        e.load(Slot::Current, params_for(&current, 1)).unwrap();
        let next = e.load(Slot::Next, params_for(&stereo, 2));
        assert!(matches!(next, Err(EngineError::Unsupported(_))), "{next:?}");
    }

    #[test]
    fn fixed_rate_absent_still_refuses_a_next_slot_rate_change() {
        let (current, next) = (tone(44_100, 1, 2205), tone(22_050, 1, 2205));
        let (mut e, _) = recording_engine(EngineConfig::default());
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, params_for(&current, 1)).unwrap();
        let next = e.load(Slot::Next, params_for(&next, 2));
        assert!(matches!(next, Err(EngineError::Unsupported(_))), "{next:?}");
    }

    #[test]
    fn fixed_rate_with_allow_resample_off_rejects_conversion_but_passes_equal_rates() {
        let config = EngineConfig {
            allow_resample: false,
            ..EngineConfig::default()
        };
        let (low, equal) = (tone(22_050, 1, 2205), tone(48_000, 1, 2205));
        let (mut e, _) = recording_engine(config);
        e.open_device(None, fixed(48_000), ShareMode::Shared)
            .unwrap();

        let rejected = e.load(Slot::Current, params_for(&low, 1));
        assert!(
            matches!(rejected, Err(EngineError::Format(_))),
            "{rejected:?}"
        );

        e.load(Slot::Current, params_for(&equal, 2)).unwrap();
        let rejected = e.load(Slot::Next, params_for(&low, 3));
        assert!(
            matches!(rejected, Err(EngineError::Format(_))),
            "{rejected:?}"
        );

        // Without a fixed rate nothing is converted, so any rate loads.
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, params_for(&low, 4)).unwrap();
    }

    #[test]
    fn fixed_rate_rejects_a_source_rate_it_cannot_convert() {
        let absurd = tone(1, 1, 8);
        let (mut e, _) = recording_engine(EngineConfig::default());
        e.open_device(None, fixed(48_000), ShareMode::Shared)
            .unwrap();

        let rejected = e.load(Slot::Current, params_for(&absurd, 1));
        assert!(
            matches!(rejected, Err(EngineError::Format(_))),
            "{rejected:?}"
        );
    }

    #[test]
    fn fixed_rate_position_reports_the_source_rate_after_load() {
        let (low, mid) = (tone(22_050, 1, 2205), tone(32_000, 1, 2205));
        let (mut e, _) = recording_engine(EngineConfig::default());
        e.open_device(None, fixed(48_000), ShareMode::Shared)
            .unwrap();
        e.load(Slot::Current, params_for(&low, 1)).unwrap();
        assert_eq!(e.position().sample_rate, 22_050);

        // The stream is reused here, so the rate must come from `load`.
        e.load(Slot::Current, params_for(&mid, 2)).unwrap();
        assert_eq!(e.position().sample_rate, 32_000);
    }

    // ---- resampling on the decode worker ---------------------------------

    type Sink = Arc<std::sync::Mutex<Vec<f32>>>;

    const FREQ: f64 = 997.0;

    /// Mono `0.5 * cos` at `FREQ`, so the first sample is non-zero and the
    /// silence around a capture can be trimmed.
    fn cosine(rate: u32, frames: usize) -> Vec<f32> {
        (0..frames)
            .map(|n| {
                (0.5 * (std::f64::consts::TAU * FREQ * n as f64 / f64::from(rate)).cos()) as f32
            })
            .collect()
    }

    /// The same sine at the 48 kHz output, `m` frames after the output frame
    /// that is aligned with source frame `first`.
    fn ideal(first: usize, in_rate: u32, m: usize) -> f32 {
        let t = first as f64 / f64::from(in_rate) + m as f64 / 48_000.0;
        (0.5 * (std::f64::consts::TAU * FREQ * t).cos()) as f32
    }

    /// Output frames a converted run of `frames` source frames owes.
    fn out_for(frames: usize, in_rate: u32) -> usize {
        (frames * 48_000).div_ceil(in_rate as usize)
    }

    /// An engine whose stream renders into a sink, with the output fixed at
    /// 48 kHz. The mock device is mono, so sources are mono too.
    fn fixed_capturing_engine(config: EngineConfig) -> (Engine, Sink) {
        let sink = Sink::default();
        let backend = CapturingBackend { sink: sink.clone() };
        let mut e = Engine::new(config, Box::new(backend));
        e.open_device(None, fixed(48_000), ShareMode::Shared)
            .unwrap();
        (e, sink)
    }

    /// Waits until the engine has played everything and gone idle.
    fn wait_for_idle(e: &Engine) {
        let deadline = Instant::now() + Duration::from_secs(10);
        while e.position().state != State::Idle {
            assert!(Instant::now() < deadline, "engine never went idle");
            std::thread::sleep(Duration::from_millis(2));
        }
    }

    /// Everything the stream rendered from the first non-zero sample on. The
    /// stream runs before `play`, so the leading silence has no fixed length.
    /// Stops the stream first, so the capture is complete.
    fn rendered_from_first_sound(e: Engine, sink: &Sink) -> Vec<f32> {
        drop(e);
        let captured = sink.lock().unwrap();
        let start = captured.iter().position(|&s| s != 0.0).unwrap();
        captured[start..].to_vec()
    }

    /// `rendered_from_first_sound` without the trailing silence. An interior
    /// stall would leave zeros inside and show up as extra length.
    fn rendered_sound(e: Engine, sink: &Sink) -> Vec<f32> {
        let mut sound = rendered_from_first_sound(e, sink);
        let end = sound.iter().rposition(|&s| s != 0.0).unwrap() + 1;
        sound.truncate(end);
        sound
    }

    /// Asserts that `out[margin..len - margin]` follows the ideal 48 kHz sine
    /// of a source that started at source frame `first`. The margins stay
    /// clear of the filter's start and end transients.
    fn assert_follows_ideal(out: &[f32], first: usize, in_rate: u32) {
        let margin = 4_000;
        for (m, got) in out.iter().enumerate().take(out.len() - margin).skip(margin) {
            let want = ideal(first, in_rate, m);
            assert!((got - want).abs() < 1e-3, "frame {m}: {got} vs {want}");
        }
    }

    /// Waits until the whole queue is decoded into the ring, before `play`.
    fn prefill(e: &mut Engine, seen: &mut Vec<EngineEvent>) {
        assert!(wait_for_event(
            e,
            seen,
            EventKind::QueueEmpty,
            Duration::from_secs(10)
        ));
    }

    #[test]
    fn resampled_track_has_exact_length_and_is_aligned() {
        let frames = 20_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        wait_for_idle(&e);

        let out = rendered_sound(e, &sink);
        assert_eq!(out.len(), out_for(frames, 44_100));
        assert_follows_ideal(&out, 0, 44_100);
    }

    #[test]
    fn resampled_track_end_is_not_cut() {
        let frames = 20_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();

        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::TrackEnded,
            Duration::from_secs(10)
        ));
        wait_for_idle(&e);
        // The last callback publishes its position after going idle.
        std::thread::sleep(Duration::from_millis(20));
        let pos = e.position();
        assert_eq!(pos.total_frames, frames as i64);
        assert_eq!(pos.sample_rate, 44_100);
        assert!(
            (frames as i64 - 1..=frames as i64).contains(&pos.frame),
            "final position {}",
            pos.frame
        );

        // Every converted frame reached the device, including the filter tail.
        assert_eq!(rendered_sound(e, &sink).len(), out_for(frames, 44_100));
    }

    #[test]
    fn resampled_rate_change_join_is_gapless() {
        let b: Vec<f32> = cosine(48_000, 6_000)
            .iter()
            .map(|s| s * 0.5 + 0.1)
            .collect();
        let a_frames = 6_000;
        let a_wav = write_wav(44_100, 1, &cosine(44_100, a_frames));
        let b_wav = write_wav(48_000, 1, &b);
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&a_wav, 1)).unwrap();
        e.load(Slot::Next, params_for(&b_wav, 2)).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        wait_for_idle(&e);

        // B passes through, so it must follow the converted A with neither a
        // gap nor an overlap.
        let out = rendered_from_first_sound(e, &sink);
        let join = out_for(a_frames, 44_100);
        assert_eq!(out[join..join + b.len()], b[..]);
        assert!(out[join + b.len()..].iter().all(|&s| s == 0.0));
    }

    #[test]
    fn resampled_seek_restarts_aligned() {
        let frames = 30_000;
        let target = 5_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        prefill(&mut e, &mut seen);

        // The seek discards the first run; a fresh QueueEmpty means the rest
        // of the track is in the ring.
        seen.clear();
        e.seek(target as i64).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        wait_for_idle(&e);

        let out = rendered_sound(e, &sink);
        assert_eq!(out.len(), out_for(frames - target, 44_100));
        assert_follows_ideal(&out, target, 44_100);
    }

    #[test]
    fn resampled_end_frame_trims_in_source_frames() {
        let end_frame = 20_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, 30_000));
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        let mut seen = Vec::new();
        let params = LoadParams {
            end_frame: end_frame as i64,
            ..params_for(&wav, 1)
        };
        e.load(Slot::Current, params).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        wait_for_idle(&e);

        let out = rendered_sound(e, &sink);
        assert_eq!(out.len(), out_for(end_frame, 44_100));
        assert_follows_ideal(&out, 0, 44_100);
    }

    #[test]
    fn resampled_skip_to_next_discards_the_pending_tail() {
        // The three next rates cover a reused converter, a rebuilt one, and
        // none at all.
        for next_rate in [44_100, 22_050, 48_000] {
            // Longer than the ring holds, so the worker is stuck mid-track
            // with a tail inside the filter when the skip arrives.
            let long = write_wav(44_100, 1, &cosine(44_100, 150_000));
            let next_frames = 20_000;
            let next = write_wav(next_rate, 1, &cosine(next_rate, next_frames));
            let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
            let mut seen = Vec::new();
            e.load(Slot::Current, params_for(&long, 1)).unwrap();
            e.load(Slot::Next, params_for(&next, 2)).unwrap();
            let deadline = Instant::now() + Duration::from_secs(10);
            while e.position().buffer_fill < 0.7 {
                assert!(Instant::now() < deadline, "ring never filled");
                std::thread::sleep(Duration::from_millis(2));
            }

            e.skip_to_next().unwrap();
            prefill(&mut e, &mut seen);
            e.play().unwrap();
            wait_for_idle(&e);
            assert_eq!(e.position().user_token, 2);

            // Nothing of the first track survives: the output is exactly the
            // next track's own run.
            let out = rendered_sound(e, &sink);
            assert_eq!(
                out.len(),
                out_for(next_frames, next_rate),
                "next rate {next_rate}"
            );
            assert_follows_ideal(&out, 0, next_rate);
        }
    }

    #[test]
    fn resampled_two_converted_tracks_join_at_different_rates() {
        let (a_frames, b_frames) = (10_000, 10_000);
        let a_wav = write_wav(44_100, 1, &cosine(44_100, a_frames));
        let b_wav = write_wav(22_050, 1, &cosine(22_050, b_frames));
        let (mut e, step) = stepped_engine(EngineConfig::default(), fixed(48_000));
        e.load(Slot::Current, params_for(&a_wav, 1)).unwrap();
        e.load(Slot::Next, params_for(&b_wav, 2)).unwrap();
        e.play().unwrap();

        let join = out_for(a_frames, 44_100);
        render(&e, &step, join + out_for(b_frames, 22_050));
        let out = step.sink.lock().unwrap().clone();

        // Each track is its own run, so B's first frame follows A's last one
        // directly and is aligned with B's first source frame.
        assert_follows_ideal(&out[..join], 0, 44_100);
        assert_follows_ideal(&out[join..], 0, 22_050);
    }

    // ---- gapless joins with a running converter ---------------------------

    /// A backend whose render thread pulls frames only against `permits`, so a
    /// test decides exactly how much audio is rendered and when. With no permit
    /// it still calls the callback with an empty buffer, which keeps the flush
    /// protocol acknowledged. Mono, like `CapturingBackend`.
    #[derive(Default)]
    struct Stepper {
        /// Frames the render thread may still pull.
        permits: std::sync::atomic::AtomicUsize,
        /// Everything rendered so far.
        sink: std::sync::Mutex<Vec<f32>>,
    }

    struct SteppedBackend {
        step: Arc<Stepper>,
    }

    impl OutputBackend for SteppedBackend {
        fn name(&self) -> &'static str {
            "stepped"
        }

        fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, OutputError> {
            Ok(vec![self.probe_device(None)?])
        }

        fn probe_device(&mut self, _id: Option<&str>) -> Result<DeviceInfo, OutputError> {
            Ok(DeviceInfo {
                id: "stepped".into(),
                name: "Stepped".into(),
                is_default: true,
                native_rate: 44_100,
                native_channels: 1,
            })
        }

        fn open_stream(
            &mut self,
            _device_id: Option<&str>,
            format: StreamFormat,
            _options: StreamOptions,
            mut callback: crate::output::RenderCallback,
            _events: EventSink,
        ) -> Result<Box<dyn OutputStream>, OutputError> {
            let stop = Arc::new(AtomicBool::new(false));
            let stop2 = stop.clone();
            let step = self.step.clone();
            let channels = usize::from(format.channels);
            let join = std::thread::spawn(move || {
                let mut buf = vec![0.0f32; 256 * channels];
                while !stop2.load(Ordering::Acquire) {
                    let frames = step.permits.load(Ordering::Acquire).min(256);
                    callback(&mut buf[..frames * channels], &RenderInfo::default());
                    if frames > 0 {
                        step.sink
                            .lock()
                            .unwrap()
                            .extend_from_slice(&buf[..frames * channels]);
                        // Released after the callback has published its
                        // position, so `permits == 0` means "rendered".
                        step.permits.fetch_sub(frames, Ordering::AcqRel);
                    }

                    std::thread::sleep(Duration::from_micros(100));
                }
            });
            Ok(Box::new(MockStream {
                stop,
                join: Some(join),
                format,
            }))
        }
    }

    /// An engine on the stepped backend, with the output rate set as `desired`.
    fn stepped_engine(
        config: EngineConfig,
        desired: Option<StreamFormat>,
    ) -> (Engine, Arc<Stepper>) {
        let step = Arc::new(Stepper::default());
        let backend = SteppedBackend { step: step.clone() };
        let mut e = Engine::new(config, Box::new(backend));
        e.open_device(None, desired, ShareMode::Shared).unwrap();
        (e, step)
    }

    fn wait_until(what: &str, mut done: impl FnMut() -> bool) {
        let deadline = Instant::now() + Duration::from_secs(10);
        while !done() {
            assert!(Instant::now() < deadline, "timed out waiting for {what}");
            std::thread::sleep(Duration::from_millis(1));
        }
    }

    fn ring_len(e: &Engine) -> usize {
        e.device.as_ref().unwrap().ring.len()
    }

    /// Renders `frames` frames in small steps, each only once the ring holds
    /// it, so a slow decode worker never makes the render thread insert zeros.
    /// Stops waiting for data once the worker has announced the end of the
    /// queue. The stream is mono, so ring samples are frames.
    fn render(e: &Engine, step: &Stepper, frames: usize) {
        let mut left = frames;
        while left > 0 {
            let now = left.min(256);
            wait_until("the ring to fill", || {
                ring_len(e) >= now || e.shared.eof_no_next.load(Ordering::Acquire)
            });
            step.permits.fetch_add(now, Ordering::AcqRel);
            wait_until("the render thread", || {
                step.permits.load(Ordering::Acquire) == 0
            });
            left -= now;
        }
    }

    fn decode_all(name: &str) -> (Vec<f32>, u32) {
        let mut d = Decoder::open(&fixture(name)).unwrap();
        let rate = d.info().sample_rate;
        assert_eq!(d.info().channels, 1, "{name}");
        let mut all = Vec::new();
        while let Some(block) = d.next_block().unwrap() {
            all.extend_from_slice(block);
        }

        (all, rate)
    }

    #[test]
    fn gapless_resampled_same_rate_join_matches_one_continuous_run() {
        let (master, rate) = decode_all("gapless-master.wav");
        assert_eq!(rate, 44_100);
        let mut expected = Vec::new();
        let mut converter = Converter::new(rate, 48_000, 1);
        converter.process(&master, &mut expected);
        converter.flush(&mut expected);

        let (mut e, step) = stepped_engine(EngineConfig::default(), fixed(48_000));
        e.load(Slot::Current, load_params("gapless-a.flac", 1))
            .unwrap();
        e.load(Slot::Next, load_params("gapless-b.flac", 2))
            .unwrap();
        e.play().unwrap();
        render(&e, &step, expected.len());

        // Restarting the filter at the join would change the samples around it.
        let out = step.sink.lock().unwrap().clone();
        assert_eq!(out.len(), expected.len());
        for (n, (got, want)) in out.iter().zip(&expected).enumerate() {
            assert!((got - want).abs() <= 1e-6, "frame {n}: {got} vs {want}");
        }
    }

    #[test]
    fn gapless_resampled_track_start_lands_on_the_boundary_sample() {
        let a_frames = {
            let d = Decoder::open(&fixture("gapless-a.flac")).unwrap();
            assert_eq!(d.info().sample_rate, 44_100);
            d.info().total_frames.unwrap() as usize
        };
        let boundary = out_for(a_frames, 44_100);

        let (mut e, step) = stepped_engine(EngineConfig::default(), fixed(48_000));
        e.load(Slot::Current, load_params("gapless-a.flac", 1))
            .unwrap();
        e.load(Slot::Next, load_params("gapless-b.flac", 2))
            .unwrap();
        e.play().unwrap();

        render(&e, &step, boundary - 16);
        assert_eq!(e.position().user_token, 1);

        // 16 output frames into B are 14 source frames at 44.1 kHz.
        render(&e, &step, 32);
        let pos = e.position();
        assert_eq!(pos.user_token, 2);
        assert_eq!(pos.frame, 14);
    }

    #[test]
    fn resampled_filter_tail_is_pushed_before_the_queue_is_announced_empty() {
        // 22.05 to 44.1 kHz: the filter delay and one chunk are 2048 and 4096
        // output frames. The ring holds 8820 samples, so with nothing rendered
        // the non-tail output fits and the tail does not.
        let frames = 2 * 2048 + 2000;
        let wav = write_wav(22_050, 1, &cosine(22_050, frames));
        let mut expected = Vec::new();
        let mut converter = Converter::new(22_050, 44_100, 1);
        converter.process(&cosine(22_050, frames), &mut expected);
        let tail = converter.pending_frames() as usize;
        converter.flush(&mut expected);

        let config = EngineConfig {
            decode_ahead_ms: 200,
            ..EngineConfig::default()
        };
        let (mut e, step) = stepped_engine(config, fixed(44_100));
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        let capacity = e.device.as_ref().unwrap().ring.capacity();
        // The worker only decodes while 2048 samples are free, so it reaches
        // the end of the file only if the non-tail output leaves that much.
        assert!(expected.len() > capacity, "the whole output must not fit");
        assert!(
            expected.len() - tail + 2048 <= capacity,
            "the worker must reach EOF"
        );

        // Not playing, so nothing drains: the ring is full and the worker is
        // stuck pushing the rest of the tail, so the queue is not empty yet.
        let mut seen = Vec::new();
        assert!(!wait_for_event(
            &mut e,
            &mut seen,
            EventKind::QueueEmpty,
            Duration::from_millis(300)
        ));
        assert_eq!(ring_len(&e), capacity);

        e.play().unwrap();
        render(&e, &step, expected.len());
        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::QueueEmpty,
            Duration::from_secs(10)
        ));

        let out = step.sink.lock().unwrap().clone();
        assert_eq!(out.len(), expected.len());
        for (n, (got, want)) in out.iter().zip(&expected).enumerate() {
            assert!((got - want).abs() <= 1e-6, "frame {n}: {got} vs {want}");
        }
    }

    #[test]
    fn resampled_seek_in_the_middle_of_a_run_restarts_aligned() {
        let frames = 60_000;
        let target = 25_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let (mut e, step) = stepped_engine(EngineConfig::default(), fixed(48_000));
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        e.play().unwrap();

        // Mid-run: output has been rendered, the ring is full, and the filter
        // and its input FIFO hold samples of the first position.
        render(&e, &step, 3_000);
        wait_until("the ring to refill", || ring_len(&e) > 8_000);
        e.seek(target as i64).unwrap();
        wait_until("the seek flush", || {
            e.shared.flush_ack.load(Ordering::Acquire) >= 2
        });
        let mark = step.sink.lock().unwrap().len();

        render(&e, &step, out_for(frames - target, 44_100));
        let out = step.sink.lock().unwrap()[mark..].to_vec();
        assert_follows_ideal(&out, target, 44_100);
    }

    // ---- a selected resampler plugin ---------------------------------------

    type CallLog = Arc<std::sync::Mutex<Vec<(&'static str, Option<String>)>>>;

    /// A factory of in-process stand-ins for a plugin. Each one wraps the
    /// built-in converter, so a passing run must match a built-in run exactly.
    #[derive(Default)]
    struct FakeFactory {
        /// Number of `create` calls, including unsupported ones.
        creates: std::sync::atomic::AtomicUsize,
        /// Answer every `create` with `Unsupported`.
        unsupported: bool,
        /// 1-based number of the `process` call that fails, counted over the
        /// factory's lifetime.
        fail_process_call: Option<usize>,
        /// Source frames the failing plugin had accepted before it failed.
        fed_at_failure: Arc<std::sync::atomic::AtomicUsize>,
        /// Every call a plugin received, with the name of the calling thread.
        calls: CallLog,
    }

    impl ResamplerFactory for FakeFactory {
        fn create(
            &self,
            in_rate: u32,
            out_rate: u32,
            channels: usize,
        ) -> Result<Box<dyn StreamResampler>, ResamplerError> {
            self.creates.fetch_add(1, Ordering::Relaxed);
            record_call(&self.calls, "create");
            if self.unsupported {
                return Err(ResamplerError::Unsupported);
            }

            Ok(Box::new(FakePlugin {
                inner: Converter::new(in_rate, out_rate, channels),
                channels,
                fed: 0,
                process_calls: 0,
                fail_process_call: self.fail_process_call,
                fed_at_failure: self.fed_at_failure.clone(),
                calls: self.calls.clone(),
            }))
        }
    }

    struct FakePlugin {
        inner: Converter,
        channels: usize,
        fed: usize,
        process_calls: usize,
        fail_process_call: Option<usize>,
        fed_at_failure: Arc<std::sync::atomic::AtomicUsize>,
        calls: CallLog,
    }

    fn record_call(calls: &CallLog, what: &'static str) {
        let thread = std::thread::current().name().map(str::to_owned);
        calls.lock().unwrap().push((what, thread));
    }

    impl StreamResampler for FakePlugin {
        fn process(&mut self, input: &[f32], out: &mut Vec<f32>) -> Result<(), ResamplerError> {
            record_call(&self.calls, "process");
            self.process_calls += 1;
            if self.fail_process_call == Some(self.process_calls) {
                self.fed_at_failure.store(self.fed, Ordering::Relaxed);
                return Err(ResamplerError::Failed);
            }

            self.fed += input.len() / self.channels;
            self.inner.process(input, out);
            Ok(())
        }

        fn flush(&mut self, out: &mut Vec<f32>) -> Result<(), ResamplerError> {
            record_call(&self.calls, "flush");
            self.inner.flush(out);
            Ok(())
        }

        fn pending_frames(&self) -> u64 {
            self.inner.pending_frames()
        }

        fn reset(&mut self) -> Result<(), ResamplerError> {
            record_call(&self.calls, "reset");
            self.fed = 0;
            self.inner.reset();
            Ok(())
        }
    }

    impl Drop for FakePlugin {
        fn drop(&mut self) {
            record_call(&self.calls, "drop");
        }
    }

    /// Drains pending events into `seen` without waiting.
    fn drain_events(e: &mut Engine, seen: &mut Vec<EngineEvent>) {
        let mut out = [EngineEvent::default(); 16];
        loop {
            let n = e.poll_events(&mut out);
            seen.extend_from_slice(&out[..n]);
            if n == 0 {
                return;
            }
        }
    }

    /// What a built-in run of the whole cosine owes: the reference for a
    /// plugin that behaves like the built-in.
    fn builtin_run(rate: u32, frames: usize) -> Vec<f32> {
        let mut expected = Vec::new();
        let mut converter = Converter::new(rate, 48_000, 1);
        converter.process(&cosine(rate, frames), &mut expected);
        converter.flush(&mut expected);
        expected
    }

    #[test]
    fn plugin_resampler_output_equals_builtin_through_a_passing_fake() {
        let frames = 20_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let factory = Arc::new(FakeFactory::default());
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        e.set_resampler(Some(factory.clone()));
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        wait_for_idle(&e);
        drain_events(&mut e, &mut seen);

        assert!(factory.creates.load(Ordering::Relaxed) > 0);
        assert!(
            !seen.iter().any(|ev| ev.kind == EventKind::Error),
            "{seen:?}"
        );
        assert_eq!(rendered_sound(e, &sink), builtin_run(44_100, frames));
    }

    #[test]
    fn plugin_resampler_failure_mid_track_keeps_playing_and_reports() {
        let frames = 40_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let factory = Arc::new(FakeFactory {
            fail_process_call: Some(3),
            ..FakeFactory::default()
        });
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        e.set_resampler(Some(factory.clone()));
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        assert!(wait_for_event(
            &mut e,
            &mut seen,
            EventKind::TrackEnded,
            Duration::from_secs(10)
        ));
        wait_for_idle(&e);
        drain_events(&mut e, &mut seen);

        let reports: Vec<_> = seen
            .iter()
            .filter(|ev| ev.kind == EventKind::Error)
            .map(|ev| ev.code)
            .collect();
        assert_eq!(reports, [MIRELIA_ERR_PLUGIN]);

        // The plugin owed `out(fed)` frames when it died, which became silence;
        // the built-in then converted the rest as a run of its own.
        let fed = factory.fed_at_failure.load(Ordering::Relaxed);
        assert!(fed > 0 && fed < frames, "failed after {fed} source frames");
        let out = rendered_from_first_sound(e, &sink);
        let sound_end = out.iter().rposition(|&s| s != 0.0).unwrap() + 1;
        assert_eq!(
            sound_end,
            out_for(fed, 44_100) + out_for(frames - fed, 44_100)
        );
        assert_follows_ideal(&out[out_for(fed, 44_100)..sound_end], fed, 44_100);
    }

    #[test]
    fn plugin_resampler_unsupported_create_reports_nothing() {
        let frames = 20_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let factory = Arc::new(FakeFactory {
            unsupported: true,
            ..FakeFactory::default()
        });
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        e.set_resampler(Some(factory.clone()));
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        wait_for_idle(&e);
        drain_events(&mut e, &mut seen);

        assert!(factory.creates.load(Ordering::Relaxed) > 0);
        assert!(
            !seen.iter().any(|ev| ev.kind == EventKind::Error),
            "{seen:?}"
        );
        assert_eq!(rendered_sound(e, &sink), builtin_run(44_100, frames));
    }

    #[test]
    fn plugin_resampler_calls_run_only_on_the_decode_worker() {
        let frames = 30_000;
        let wav = write_wav(44_100, 1, &cosine(44_100, frames));
        let factory = Arc::new(FakeFactory::default());
        let (mut e, sink) = fixed_capturing_engine(EngineConfig::default());
        e.set_resampler(Some(factory.clone()));
        let mut seen = Vec::new();
        e.load(Slot::Current, params_for(&wav, 1)).unwrap();
        prefill(&mut e, &mut seen);

        // A seek resets the stage, and the end of the track flushes it.
        seen.clear();
        e.seek(5_000).unwrap();
        prefill(&mut e, &mut seen);
        e.play().unwrap();
        wait_for_idle(&e);

        // Dropping the engine joins the worker, which drops the plugin.
        drop(rendered_from_first_sound(e, &sink));
        let calls = factory.calls.lock().unwrap();
        for what in ["create", "process", "flush", "reset", "drop"] {
            assert!(
                calls.iter().any(|(call, _)| *call == what),
                "no {what} call"
            );
        }
        for (what, thread) in calls.iter() {
            assert_eq!(thread.as_deref(), Some("mirelia-decode"), "{what}");
        }
    }
}
