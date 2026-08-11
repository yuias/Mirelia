//! Engine events, mirrored to `kn_event` at the ABI layer. Events travel
//! through SPSC rings so pushing them is legal on the real-time thread.

use crate::spsc::Producer;

/// Event kinds, matching `kn_event_type` values in `audio_core.h`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
#[repr(u8)]
pub enum EventKind {
    /// Placeholder for empty ring slots; never delivered.
    #[default]
    None = 0,
    /// Transport state changed.
    StateChanged = 1,
    /// A (new) track became audible.
    TrackStarted = 2,
    /// The audible track finished.
    TrackEnded = 3,
    /// The engine has nothing queued after the current track.
    QueueEmpty = 4,
    /// The default device switched.
    DeviceChanged = 5,
    /// The device disappeared.
    DeviceLost = 6,
    /// An asynchronous error occurred; `code` carries the kn_result.
    Error = 7,
}

/// One engine event.
#[derive(Debug, Clone, Copy, Default)]
pub struct EngineEvent {
    /// What happened.
    pub kind: EventKind,
    /// The host's track token, when applicable.
    pub token: u64,
    /// A `kn_result` for `Error` events; 0 otherwise.
    pub code: i32,
    /// Monotonic timestamp of the event.
    pub time_ns: u64,
}

/// A handle for pushing engine events from a thread that does not already
/// own the render callback's producer — an output backend's own render
/// thread, or a callback the backend registers on another OS thread (e.g.
/// a COM notification). Each `EventSink` wraps a dedicated single-producer
/// ring, so backends get a safe way to report device-level conditions
/// without needing to share the `ev_render` producer's ring or know
/// anything about `spsc` internals.
pub struct EventSink {
    producer: Producer<EngineEvent>,
}

impl EventSink {
    /// Wraps a producer as an event sink.
    pub(crate) fn new(producer: Producer<EngineEvent>) -> Self {
        Self { producer }
    }

    /// Pushes one event. Best-effort, like every other event push in the
    /// engine: never blocks, silently drops if the ring is full (these
    /// events are rare, so that should not happen in practice).
    pub fn push(&mut self, kind: EventKind, token: u64, code: i32) {
        let _ = self.producer.push_slice(&[EngineEvent {
            kind,
            token,
            code,
            time_ns: crate::host_time_ns(),
        }]);
    }
}
