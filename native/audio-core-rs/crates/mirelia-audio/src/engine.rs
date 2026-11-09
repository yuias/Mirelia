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
    DeviceInfo, OutputBackend, OutputError, OutputStream, RenderInfo, ShareMode, StreamFormat,
    StreamOptions,
};
use crate::spsc::{self, Consumer, Producer, SpscCore};
use crate::{EngineConfig, Position, State, host_time_ns};

/// Engine failure classification, mirroring `kn_result`.
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

/// The two source slots (`kn_slot`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Slot {
    /// What is playing (or will play).
    Current,
    /// What plays after the current source ends.
    Next,
}

/// Per-load parameters, mirroring `kn_load_params`.
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

    /// Selects a device (None = system default). With no desired format the
    /// stream itself is opened lazily at the first load, following the
    /// source format; the returned format is then the device's native one.
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
        });
        if let Some(fmt) = desired {
            self.ensure_stream(fmt)?;
            Ok(fmt)
        } else {
            Ok(StreamFormat {
                sample_rate: probe.native_rate,
                channels: probe.native_channels,
            })
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
        let fmt = StreamFormat {
            sample_rate: decoder.info().sample_rate,
            channels: decoder.info().channels,
        };

        match slot {
            Slot::Current => {
                if self.request.is_none() {
                    return Err(EngineError::State("open a device before loading".into()));
                }

                self.shutdown_worker();
                self.ensure_stream(fmt)?;
                self.shared.eof_no_next.store(false, Ordering::Release);
                self.shared.ended_signaled.store(false, Ordering::Release);
                self.spawn_worker(decoder, params, fmt);
                Ok(())
            }
            Slot::Next => {
                let Some(session) = &self.session else {
                    return Err(EngineError::State("load a current track first".into()));
                };
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

        self.shared.volume_bits.store(gain.to_bits(), Ordering::Release);
        Ok(())
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

        let callback = render_callback(
            spsc::consumer(ring.clone()),
            spsc::consumer(tags.clone()),
            spsc::producer(self.ev_render.clone()),
            self.shared.clone(),
            fmt,
            ring.capacity(),
        );

        let events = EventSink::new(spsc::producer(self.ev_device.clone()));

        let options = StreamOptions {
            mode: request.mode,
            dither: self.config.dither_to_int,
        };
        let stream = self
            .backend
            .open_stream(request.id.as_deref(), fmt, options, callback, events)?;
        self.shared.pub_rate.store(fmt.sample_rate, Ordering::Release);
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
    if db == 0.0 { 1.0 } else { 10f32.powf(db / 20.0) }
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
    start_track(&mut w, &mut tags, &mut events, &ring, TAG_TRACK_START, None);

    loop {
        // Commands first; they beat production.
        loop {
            match cmd.try_recv() {
                Ok(Cmd::Shutdown) => return,
                Ok(Cmd::SetNext(next)) => {
                    w.next = next;
                    w.queue_empty_sent = false;
                }
                Ok(Cmd::Seek(frame)) => handle_seek(&mut w, &shared, &mut tags, &mut events, &ring, frame),
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
            if let Some(boxed) = w.next.take() {
                // Auto-advance: same stream, boundary tag marks the switch.
                let (decoder, params) = *boxed;
                w.current = decoder;
                w.gain = db_to_linear(params.gain_db);
                w.params = params;
                w.at_eof = false;
                w.queue_empty_sent = false;
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
    // A 16-slot tag ring cannot overflow in practice (tags are rare); if it
    // ever is full the tag is dropped and positions stay on the old base,
    // which is survivable — never block here.
    let _ = tags.push_slice(&[Tag {
        kind,
        at_sample: ring.write_index(),
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

/// Builds the RT callback. Everything it needs is owned or atomic;
/// the body allocates nothing and takes no locks.
fn render_callback(
    mut samples: Consumer<f32>,
    mut tags: Consumer<Tag>,
    mut events: Producer<EngineEvent>,
    shared: Arc<Shared>,
    fmt: StreamFormat,
    ring_capacity: usize,
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
            buf.fill(0.0);
            shared.pub_fill_permille.store(
                (samples.len() * 1000 / ring_capacity) as u32,
                Ordering::Release,
            );
            return;
        }

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
    use std::path::PathBuf;
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
        e.load(Slot::Current, load_params("fixture.wav", 7)).unwrap();
        e.play().unwrap();

        assert!(wait_for_event(&mut e, &mut seen, EventKind::TrackStarted, Duration::from_secs(5)));
        assert!(wait_for_event(&mut e, &mut seen, EventKind::TrackEnded, Duration::from_secs(5)));

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
        e.load(Slot::Current, load_params("fixture.wav", 1)).unwrap();
        e.load(Slot::Next, load_params("fixture.flac", 2)).unwrap();
        e.play().unwrap();

        assert!(wait_for_event(&mut e, &mut seen, EventKind::TrackEnded, Duration::from_secs(5)));
        assert!(wait_for_event(&mut e, &mut seen, EventKind::QueueEmpty, Duration::from_secs(5)));
        let pos = e.position();
        assert_eq!(pos.user_token, 2);
    }

    #[test]
    fn seek_rebases_position() {
        let mut e = engine();
        let mut seen = Vec::new();
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("fixture.flac", 3)).unwrap();
        e.play().unwrap();
        e.seek(10_000).unwrap();

        assert!(wait_for_event(&mut e, &mut seen, EventKind::TrackEnded, Duration::from_secs(5)));
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
        let master = {
            let mut d =
                mirelia_decode::Decoder::open(&fixture("gapless-master.wav")).unwrap();
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
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("gapless-a.flac", 1)).unwrap();
        e.load(Slot::Next, load_params("gapless-b.flac", 2)).unwrap();
        // Let the decode worker prefill so playback starts without a stall.
        std::thread::sleep(Duration::from_millis(100));
        e.play().unwrap();

        assert!(wait_for_event(&mut e, &mut seen, EventKind::QueueEmpty, Duration::from_secs(10)));
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

    #[test]
    fn pause_freezes_position() {
        let mut e = engine();
        let mut seen = Vec::new();
        e.open_device(None, None, ShareMode::Shared).unwrap();
        e.load(Slot::Current, load_params("fixture.wav", 9)).unwrap();
        e.play().unwrap();
        assert!(wait_for_event(&mut e, &mut seen, EventKind::TrackStarted, Duration::from_secs(5)));
        e.pause().unwrap();
        std::thread::sleep(Duration::from_millis(10));
        let a = e.position();
        std::thread::sleep(Duration::from_millis(30));
        let b = e.position();
        assert_eq!(a.frame, b.frame);
        assert_eq!(b.state, State::Paused);
    }
}
