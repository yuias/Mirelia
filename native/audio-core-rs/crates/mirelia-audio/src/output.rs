//! Output backend abstraction. The engine drives any backend that can run a
//! render callback; WASAPI and CoreAudio implement this in their own crates.

use std::fmt;

/// PCM stream format the engine works in (always interleaved f32).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct StreamFormat {
    /// Sample rate in Hz.
    pub sample_rate: u32,
    /// Channel count.
    pub channels: u16,
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
    /// Lists render devices.
    fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, OutputError>;

    /// Checks that a device exists (None = system default) and reports its
    /// native format, without opening a stream.
    fn probe_device(&mut self, device_id: Option<&str>) -> Result<DeviceInfo, OutputError>;

    /// Opens a stream on the device and starts the render thread. The
    /// callback runs until the returned stream is dropped.
    fn open_stream(
        &mut self,
        device_id: Option<&str>,
        format: StreamFormat,
        callback: RenderCallback,
    ) -> Result<Box<dyn OutputStream>, OutputError>;
}
