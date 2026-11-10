//! Mirelia audio engine core.
//!
//! Invariant carried through all milestones: the real-time thread never
//! allocates, never locks, never calls managed code. See `engine.rs` for
//! the thread model that enforces it.

pub mod engine;
pub mod events;
pub mod output;
pub mod resample;
pub mod rt_guard;
pub mod spsc;

use std::sync::OnceLock;
use std::time::Instant;

pub use engine::{Engine, EngineError, LoadParams, Slot};
/// When TPDF dither is applied on float-to-int conversion.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DitherMode {
    /// Never.
    Off,
    /// Every block, whatever the track.
    #[default]
    Always,
    /// Only blocks made up entirely of tracks that Mirelia's resampler
    /// converted. A track that passes through at its own rate is never
    /// dithered, which keeps it bit-perfect.
    Resampled,
}

/// Engine configuration, mirroring `MireliaEngineConfig` minus the ABI version
/// (version negotiation is the ABI layer's concern, not the engine's).
#[derive(Debug, Clone, Copy)]
pub struct EngineConfig {
    /// Output latency hint in milliseconds; 0 means backend default.
    pub target_latency_ms: u32,
    /// Decode-ahead ring buffer depth in milliseconds.
    pub decode_ahead_ms: u32,
    /// When false, a load that needs Mirelia's own sample-rate conversion
    /// (a fixed output rate that differs from the source's) fails with a
    /// format error instead.
    pub allow_resample: bool,
    /// When to apply TPDF dither on float-to-int conversion.
    pub dither: DitherMode,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self {
            target_latency_ms: 0,
            decode_ahead_ms: 2000,
            allow_resample: true,
            dither: DitherMode::Always,
        }
    }
}

/// Transport state, mirroring `MireliaState`.
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
    /// ABI-stable numeric value (`MireliaState`).
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

/// A position snapshot, mirroring `MireliaPosition`.
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
