//! Mirelia audio engine core.
//!
//! Invariant carried through all milestones: the real-time thread never
//! allocates, never locks, never calls managed code. See `engine.rs` for
//! the thread model that enforces it.

pub mod engine;
pub mod events;
pub mod output;
pub mod spsc;

use std::sync::OnceLock;
use std::time::Instant;

pub use engine::{Engine, EngineError, LoadParams, Slot};

/// Engine configuration, mirroring `kn_engine_config` minus the ABI version
/// (version negotiation is the ABI layer's concern, not the engine's).
#[derive(Debug, Clone, Copy)]
pub struct EngineConfig {
    /// Output latency hint in milliseconds; 0 means backend default.
    pub target_latency_ms: u32,
    /// Decode-ahead ring buffer depth in milliseconds.
    pub decode_ahead_ms: u32,
    /// When false, fail rather than resample.
    pub allow_resample: bool,
    /// Apply TPDF dither on float-to-int conversion.
    pub dither_to_int: bool,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self {
            target_latency_ms: 0,
            decode_ahead_ms: 2000,
            allow_resample: true,
            dither_to_int: true,
        }
    }
}

/// Transport state, mirroring `kn_state`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum State {
    /// No source loaded.
    Idle,
    /// Audible.
    Playing,
    /// Loaded, clock stopped.
    Paused,
    /// Playing but the decode buffer ran dry.
    Stalled,
}

impl State {
    /// ABI-stable numeric value (`kn_state`).
    pub fn as_u8(self) -> u8 {
        match self {
            State::Idle => 0,
            State::Playing => 1,
            State::Paused => 2,
            State::Stalled => 3,
        }
    }

    /// Inverse of [`State::as_u8`]; unknown values map to Idle.
    pub fn from_u8(v: u8) -> Self {
        match v {
            1 => State::Playing,
            2 => State::Paused,
            3 => State::Stalled,
            _ => State::Idle,
        }
    }
}

/// A position snapshot, mirroring `kn_position`.
#[derive(Debug, Clone, Copy)]
pub struct Position {
    /// Transport state at snapshot time.
    pub state: State,
    /// Host token of the audible source; 0 when idle.
    pub user_token: u64,
    /// Frame position within the current source.
    pub frame: i64,
    /// Total frames of the current source; -1 if unknown.
    pub total_frames: i64,
    /// Sample rate the position is expressed in.
    pub sample_rate: u32,
    /// Monotonic host timestamp for client-side interpolation.
    pub host_time_ns: u64,
    /// Decode buffer fill ratio, 0..1.
    pub buffer_fill: f32,
}

/// Monotonic nanoseconds since process-local epoch.
pub fn host_time_ns() -> u64 {
    static EPOCH: OnceLock<Instant> = OnceLock::new();
    let epoch = *EPOCH.get_or_init(Instant::now);
    u64::try_from(epoch.elapsed().as_nanos()).unwrap_or(u64::MAX)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn state_u8_roundtrip() {
        for s in [State::Idle, State::Playing, State::Paused, State::Stalled] {
            assert_eq!(State::from_u8(s.as_u8()), s);
        }
    }

    #[test]
    fn host_time_is_monotonic() {
        let a = host_time_ns();
        let b = host_time_ns();
        assert!(b >= a);
    }
}
