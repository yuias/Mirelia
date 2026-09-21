//! Output backend abstraction. The engine drives any backend that can run a
//! render callback; WASAPI and CoreAudio implement this in their own crates.

use std::fmt;

use crate::events::EventSink;

/// PCM stream format the engine works in (always interleaved f32).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StreamFormat {
    /// Sample rate in Hz.
    pub sample_rate: u32,
    /// Channel count.
    pub channels: u16,
}

/// Device access mode, mirroring `kn_share_mode`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ShareMode {
    /// OS mixer path; format conversion is the mixer's problem.
    #[default]
    Shared,
    /// Exclusive/hog access: bit-perfect, source-rate, no mixer.
    Exclusive,
}

/// Per-stream options beyond the PCM format.
#[derive(Debug, Clone, Copy, Default)]
pub struct StreamOptions {
    /// Access mode to open the device with.
    pub mode: ShareMode,
    /// Apply TPDF dither when the device takes integer samples
    /// (exclusive mode only; shared mode stays float end to end).
    pub dither: bool,
}

/// One playback device, as reported by a backend.
#[derive(Debug, Clone)]
pub struct DeviceInfo {
    /// Stable, backend-scoped identifier.
    pub id: String,
    /// Human-readable name.
    pub name: String,
    /// True for the system default render device.
    pub is_default: bool,
    /// The device's current mix/native rate.
    pub native_rate: u32,
    /// The device's current channel count.
    pub native_channels: u16,
}

/// Backend failure classification, mirroring the ABI error split.
#[derive(Debug)]
pub enum OutputError {
    /// The requested device does not exist or disappeared.
    DeviceLost(String),
    /// The device rejected the requested access mode.
    DeviceBusy(String),
    /// The format cannot be used on this device.
    Format(String),
    /// The backend does not support the request.
    Unsupported(String),
    /// Anything else.
    Internal(String),
}

impl fmt::Display for OutputError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            OutputError::DeviceLost(m) => write!(f, "device lost: {m}"),
            OutputError::DeviceBusy(m) => write!(f, "device busy: {m}"),
            OutputError::Format(m) => write!(f, "format rejected: {m}"),
            OutputError::Unsupported(m) => write!(f, "unsupported: {m}"),
            OutputError::Internal(m) => write!(f, "output backend error: {m}"),
        }
    }
}

impl std::error::Error for OutputError {}

/// Per-callback timing details the backend can provide.
#[derive(Debug, Clone, Copy, Default)]
pub struct RenderInfo {
    /// Frames already queued in the device (written but not yet audible).
    /// Used to convert "consumed" positions into audible positions.
    pub device_queued_frames: u32,
}

/// The engine's render callback. Runs on the backend's audio thread with
/// real-time constraints: no allocation, no locks, no blocking.
/// It must fill the entire buffer (silence when there is no data).
pub type RenderCallback = Box<dyn FnMut(&mut [f32], &RenderInfo) + Send>;

/// An open, running output stream. Dropping it stops the stream.
pub trait OutputStream: Send {
    /// The format the stream was opened with.
    fn format(&self) -> StreamFormat;
}

/// A factory for output streams on one audio API.
pub trait OutputBackend: Send {
    /// Short identifier of the platform API behind this backend, reported to
    /// the host through `kn_device_info.backend`. Stable and lowercase.
    fn name(&self) -> &'static str;

    /// Lists render devices.
    fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, OutputError>;

    /// Checks that a device exists (None = system default) and reports its
    /// native format, without opening a stream.
    fn probe_device(&mut self, device_id: Option<&str>) -> Result<DeviceInfo, OutputError>;

    /// Opens a stream on the device and starts the render thread. The
    /// callback runs until the returned stream is dropped.
    ///
    /// `events` is separate from `callback`: it lets the backend report
    /// device-level conditions (default-device switch, mid-stream device
    /// loss) that the render callback cannot carry, either because they
    /// originate on a different OS thread (e.g. a COM notification) or
    /// because they are detected at a point where the callback can no
    /// longer be invoked (a terminal I/O failure on the render thread
    /// itself, right before it exits).
    fn open_stream(
        &mut self,
        device_id: Option<&str>,
        format: StreamFormat,
        options: StreamOptions,
        callback: RenderCallback,
        events: EventSink,
    ) -> Result<Box<dyn OutputStream>, OutputError>;
}
