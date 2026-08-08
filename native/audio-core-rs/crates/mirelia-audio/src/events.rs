//! Engine events, mirrored to `kn_event` at the ABI layer. Events travel
//! through SPSC rings so pushing them is legal on the real-time thread.

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
