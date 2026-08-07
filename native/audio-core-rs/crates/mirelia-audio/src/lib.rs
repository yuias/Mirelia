//! Mirelia audio engine core.
//!
//! M0 status: engine lifecycle and state reporting only. The audio graph,
//! ring buffer, and output backends land in M2.
//!
//! Invariant carried through all milestones: the real-time thread never
//! allocates, never locks, never calls managed code.

use std::sync::OnceLock;
use std::time::Instant;

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

/// The audio engine. One instance per host process is the expected shape,
/// but nothing forbids more.
#[derive(Debug)]
pub struct Engine {
    config: EngineConfig,
    state: State,
}

impl Engine {
    /// Creates an engine with no device open and no sources loaded.
    pub fn new(config: EngineConfig) -> Self {
        Self {
            config,
            state: State::Idle,
        }
    }

    /// The configuration the engine was created with.
    pub fn config(&self) -> &EngineConfig {
        &self.config
    }

    /// Current transport state.
    pub fn state(&self) -> State {
        self.state
    }

    /// Snapshot of the current position.
    pub fn position(&self) -> Position {
        Position {
            state: self.state,
            user_token: 0,
            frame: 0,
            total_frames: -1,
            sample_rate: 0,
            host_time_ns: host_time_ns(),
            buffer_fill: 0.0,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn new_engine_is_idle() {
        let e = Engine::new(EngineConfig::default());
        assert_eq!(e.state(), State::Idle);
        assert_eq!(e.position().total_frames, -1);
    }

    #[test]
    fn host_time_is_monotonic() {
        let a = host_time_ns();
        let b = host_time_ns();
        assert!(b >= a);
    }
}
