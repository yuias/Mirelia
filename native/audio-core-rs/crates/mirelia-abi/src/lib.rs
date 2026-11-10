//! C ABI surface of the Rust audio backend.
//!
//! This crate implements `native/include/mirelia/audio_core.h` and is the
//! only crate in the workspace that exposes `extern "C"`. The header is the
//! contract; every exported symbol, struct layout, and error code here must
//! match it exactly. On disagreement, the header wins.
//!
//! Implemented: device enumeration, shared and exclusive playback,
//! transport, seek, volume, probe, loudness scan, and DSP (resampler) plugins
//! on Windows. Decoder and output plugin registration returns
//! `MireliaErrUnsupported`; it is post-v1.

pub mod dsp;

use std::cell::RefCell;
use std::ffi::{CStr, CString, c_char};
use std::path::PathBuf;
use std::sync::Arc;

use mirelia_audio::engine::{EngineError, LoadParams, Slot};
use mirelia_audio::events::EngineEvent;
use mirelia_audio::output::{OutputBackend, StreamFormat};
use mirelia_audio::resample::ResamplerFactory;
use mirelia_audio::{DitherMode, Engine, EngineConfig, State};

use dsp::MireliaDspInfo;

// ---- MireliaResult -----------------------------------------------------

/// `MireliaResult` values. Mirrors `audio_core.h`; the header is the documentation.
#[allow(missing_docs)]
pub mod result {
    pub const MIRELIA_OK: i32 = 0;
    pub const MIRELIA_ERR_INVALID_ARG: i32 = -1;
    pub const MIRELIA_ERR_UNSUPPORTED: i32 = -2;
    pub const MIRELIA_ERR_DEVICE_LOST: i32 = -3;
    pub const MIRELIA_ERR_DEVICE_BUSY: i32 = -4;
    pub const MIRELIA_ERR_DECODE: i32 = -5;
    pub const MIRELIA_ERR_IO: i32 = -6;
    pub const MIRELIA_ERR_FORMAT: i32 = -7;
    pub const MIRELIA_ERR_STATE: i32 = -8;
    pub const MIRELIA_ERR_OOM: i32 = -9;
    pub const MIRELIA_ERR_ABI_MISMATCH: i32 = -10;
    pub const MIRELIA_ERR_PLUGIN: i32 = -11;
    pub const MIRELIA_ERR_INTERNAL: i32 = -99;
}
use result::*;

/// The ABI version this backend implements (`MIRELIA_ABI_VERSION`).
pub const MIRELIA_ABI_VERSION: u32 = 1;

// ---- C struct mirrors --------------------------------------------------

/// Mirrors of the `audio_core.h` structs. Layouts must match the header exactly;
/// the header is the documentation.
#[allow(missing_docs)]
pub mod types {
    use std::ffi::c_char;

    #[repr(C)]
    pub struct MireliaEngineConfig {
        pub abi_version: u32,
        pub target_latency_ms: u32,
        pub decode_ahead_ms: u32,
        pub allow_resample: i32,
        pub dither_to_int: i32,
    }

    #[repr(C)]
    pub struct MireliaStreamFormat {
        pub sample_rate: u32,
        pub channels: u16,
        pub format: i32,
        pub channel_mask: u32,
    }

    #[repr(C)]
    pub struct MireliaDeviceInfo {
        pub id: *const c_char,
        pub name: *const c_char,
        pub backend: *const c_char,
        pub is_default: i32,
        pub native_rate: u32,
        pub native_channels: u16,
        pub supported_rate_mask: u32,
        pub supported_format_mask: u32,
        pub supports_exclusive: i32,
        pub min_period_frames: u32,
        pub default_period_frames: u32,
    }

    #[repr(C)]
    pub struct MireliaLoadParams {
        pub path: *const c_char,
        pub start_frame: i64,
        pub end_frame: i64,
        pub encoder_delay: u32,
        pub encoder_padding: u32,
        pub gain_db: f32,
        pub peak: f32,
        pub user_token: u64,
    }

    #[repr(C)]
    pub struct MireliaPosition {
        pub state: i32,
        pub user_token: u64,
        pub frame: i64,
        pub total_frames: i64,
        pub sample_rate: u32,
        pub host_time_ns: u64,
        pub buffer_fill: f32,
    }

    #[repr(C)]
    pub struct MireliaEvent {
        pub event_type: i32,
        pub user_token: u64,
        pub code: i32,
        pub host_time_ns: u64,
    }

    #[repr(C)]
    pub struct MireliaProbeResult {
        pub sample_rate: u32,
        pub channels: u16,
        pub bit_depth: u16,
        pub total_frames: i64,
        pub encoder_delay: u32,
        pub encoder_padding: u32,
        pub bitrate_kbps: u32,
        pub codec: [c_char; 16],
    }
}
use types::*;

// ---- backend selection -------------------------------------------------

#[cfg(windows)]
fn make_backend() -> Box<dyn OutputBackend> {
    Box::new(mirelia_out_wasapi::WasapiBackend::new())
}

#[cfg(not(windows))]
fn make_backend() -> Box<dyn OutputBackend> {
    // CoreAudio lands late in the schedule (Windows first); until then the
    // engine works but no device can be opened on this platform.
    struct NullBackend;
    impl OutputBackend for NullBackend {
        fn name(&self) -> &'static str {
            "null"
        }

        fn enumerate(
            &mut self,
        ) -> Result<Vec<mirelia_audio::output::DeviceInfo>, mirelia_audio::output::OutputError>
        {
            Ok(Vec::new())
        }

        fn probe_device(
            &mut self,
            _id: Option<&str>,
        ) -> Result<mirelia_audio::output::DeviceInfo, mirelia_audio::output::OutputError> {
            Err(mirelia_audio::output::OutputError::Unsupported(
                "no output backend for this platform yet".into(),
            ))
        }

        fn open_stream(
            &mut self,
            _id: Option<&str>,
            _format: StreamFormat,
            _options: mirelia_audio::output::StreamOptions,
            _callback: mirelia_audio::output::RenderCallback,
            _events: mirelia_audio::events::EventSink,
        ) -> Result<Box<dyn mirelia_audio::output::OutputStream>, mirelia_audio::output::OutputError>
        {
            Err(mirelia_audio::output::OutputError::Unsupported(
                "no output backend for this platform yet".into(),
            ))
        }
    }
    Box::new(NullBackend)
}

// ---- engine wrapper ----------------------------------------------------

/// A registered DSP plugin's resampler.
struct RegisteredDsp {
    id: CString,
    name: CString,
    /// Holds the plugin library, which stays loaded while this entry exists.
    factory: Arc<dyn ResamplerFactory>,
}

/// The opaque `MireliaEngine`: the engine plus ABI-side snapshot storage.
pub struct MireliaEngine {
    // First, so its `Drop` joins the decode worker before the registry (and with
    // it the last reference to a plugin library) is released.
    engine: Engine,
    /// Device snapshot backing storage; pointers in `device_infos` point here.
    device_strings: Vec<CString>,
    device_infos: Vec<MireliaDeviceInfo>,
    /// Registered DSP plugins. The strings that `dsp_infos` points to live here.
    dsp_plugins: Vec<RegisteredDsp>,
    dsp_infos: Vec<MireliaDspInfo>,
}

// ---- last-error storage ------------------------------------------------

thread_local! {
    // Per the header, last_error is scoped to "the most recent failure on
    // this thread", independent of the engine instance.
    static LAST_ERROR: RefCell<CString> = RefCell::new(CString::default());
}

fn set_last_error(msg: &str) {
    let c = CString::new(msg).unwrap_or_default();
    LAST_ERROR.with(|e| *e.borrow_mut() = c);
}

fn fail(code: i32, msg: &str) -> i32 {
    set_last_error(msg);
    code
}

fn engine_err(e: &EngineError) -> i32 {
    set_last_error(&e.to_string());
    match e {
        EngineError::InvalidArg(_) => MIRELIA_ERR_INVALID_ARG,
        EngineError::Unsupported(_) => MIRELIA_ERR_UNSUPPORTED,
        EngineError::DeviceLost(_) => MIRELIA_ERR_DEVICE_LOST,
        EngineError::DeviceBusy(_) => MIRELIA_ERR_DEVICE_BUSY,
        EngineError::Decode(_) => MIRELIA_ERR_DECODE,
        EngineError::Io(_) => MIRELIA_ERR_IO,
        EngineError::Format(_) => MIRELIA_ERR_FORMAT,
        EngineError::State(_) => MIRELIA_ERR_STATE,
        EngineError::Internal(_) => MIRELIA_ERR_INTERNAL,
    }
}

fn decode_err(e: &mirelia_decode::DecodeError) -> i32 {
    set_last_error(&e.to_string());
    match e {
        mirelia_decode::DecodeError::Io(_) => MIRELIA_ERR_IO,
        mirelia_decode::DecodeError::Unsupported(_) => MIRELIA_ERR_FORMAT,
        mirelia_decode::DecodeError::Malformed(_) => MIRELIA_ERR_DECODE,
    }
}

/// Reads a UTF-8 C string; None for null.
///
/// # Safety
///
/// `ptr` must be null or a valid NUL-terminated string.
unsafe fn utf8_arg(ptr: *const c_char) -> Result<Option<String>, i32> {
    if ptr.is_null() {
        return Ok(None);
    }

    unsafe { CStr::from_ptr(ptr) }
        .to_str()
        .map(|s| Some(s.to_owned()))
        .map_err(|_| {
            fail(
                MIRELIA_ERR_INVALID_ARG,
                "string argument is not valid UTF-8",
            )
        })
}

// ---- engine lifecycle --------------------------------------------------

/// Implements `mireliaEngineCreate`. Rejects any `abi_version` other than
/// [`MIRELIA_ABI_VERSION`] with `MireliaErrAbiMismatch`.
///
/// # Safety
///
/// `cfg` must point to a valid `MireliaEngineConfig`; `out` must point to
/// writable storage for one pointer.
#[unsafe(export_name = "mireliaEngineCreate")]
pub unsafe extern "C" fn mirelia_engine_create(
    cfg: *const MireliaEngineConfig,
    out: *mut *mut MireliaEngine,
) -> i32 {
    if cfg.is_null() || out.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaEngineCreate: null argument",
        );
    }
    let cfg = unsafe { &*cfg };
    if cfg.abi_version != MIRELIA_ABI_VERSION {
        return fail(
            MIRELIA_ERR_ABI_MISMATCH,
            &format!(
                "mireliaEngineCreate: host requested ABI v{}, backend implements v{MIRELIA_ABI_VERSION}",
                cfg.abi_version
            ),
        );
    }
    let engine = Engine::new(
        EngineConfig {
            target_latency_ms: cfg.target_latency_ms,
            decode_ahead_ms: if cfg.decode_ahead_ms == 0 {
                EngineConfig::default().decode_ahead_ms
            } else {
                cfg.decode_ahead_ms
            },
            allow_resample: cfg.allow_resample != 0,
            // 0 = off, 2 = resampled tracks only, anything else = always, so
            // hosts that passed 1 for "on" keep their behavior.
            dither: match cfg.dither_to_int {
                0 => DitherMode::Off,
                2 => DitherMode::Resampled,
                _ => DitherMode::Always,
            },
        },
        make_backend(),
    );
    let wrapper = Box::new(MireliaEngine {
        engine,
        device_strings: Vec::new(),
        device_infos: Vec::new(),
        dsp_plugins: Vec::new(),
        dsp_infos: Vec::new(),
    });
    unsafe { *out = Box::into_raw(wrapper) };
    MIRELIA_OK
}

/// Implements `mireliaEngineDestroy`. Null is a no-op.
///
/// # Safety
///
/// `e` must be null or a pointer previously returned by [`mirelia_engine_create`]
/// that has not been destroyed yet.
#[unsafe(export_name = "mireliaEngineDestroy")]
pub unsafe extern "C" fn mirelia_engine_destroy(e: *mut MireliaEngine) {
    if !e.is_null() {
        drop(unsafe { Box::from_raw(e) });
    }
}

/// Implements `mireliaEngineLastError`. The returned pointer is valid until
/// the next failing call on the same thread.
///
/// # Safety
///
/// The returned pointer must not be freed or retained past the next call
/// into this library from the same thread.
#[unsafe(export_name = "mireliaEngineLastError")]
pub unsafe extern "C" fn mirelia_engine_last_error(_e: *mut MireliaEngine) -> *const c_char {
    LAST_ERROR.with(|e| e.borrow().as_ptr())
}

// ---- device enumeration ------------------------------------------------

/// Implements `mireliaDevicesEnumerate`. The snapshot stays valid until the
/// next enumerate or `mireliaDevicesFree` call, per the header.
///
/// # Safety
///
/// All pointers must be valid; `e` must be a live engine.
#[unsafe(export_name = "mireliaDevicesEnumerate")]
pub unsafe extern "C" fn mirelia_devices_enumerate(
    e: *mut MireliaEngine,
    out_devices: *mut *const MireliaDeviceInfo,
    out_count: *mut usize,
) -> i32 {
    if e.is_null() || out_devices.is_null() || out_count.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaDevicesEnumerate: null argument",
        );
    }
    let wrapper = unsafe { &mut *e };

    let devices = match wrapper.engine.enumerate() {
        Ok(d) => d,
        Err(err) => return engine_err(&err),
    };

    wrapper.device_strings.clear();
    wrapper.device_infos.clear();
    let backend_name = CString::new(wrapper.engine.backend_name()).unwrap_or_default();
    wrapper.device_strings.push(backend_name);
    let backend_ptr = wrapper.device_strings[0].as_ptr();

    for d in &devices {
        let id = CString::new(d.id.as_str()).unwrap_or_default();
        let name = CString::new(d.name.as_str()).unwrap_or_default();
        wrapper.device_strings.push(id);
        let id_ptr = wrapper.device_strings.last().unwrap().as_ptr();
        wrapper.device_strings.push(name);
        let name_ptr = wrapper.device_strings.last().unwrap().as_ptr();
        wrapper.device_infos.push(MireliaDeviceInfo {
            id: id_ptr,
            name: name_ptr,
            backend: backend_ptr,
            is_default: i32::from(d.is_default),
            native_rate: d.native_rate,
            native_channels: d.native_channels,
            supported_rate_mask: 0,
            supported_format_mask: 1 << 3, // MireliaFmtF32 via shared-mode mixer
            supports_exclusive: 0,         // Not probed yet; nothing reads it
            min_period_frames: 0,
            default_period_frames: 0,
        });
    }

    unsafe {
        *out_devices = wrapper.device_infos.as_ptr();
        *out_count = wrapper.device_infos.len();
    }
    MIRELIA_OK
}

/// Implements `mireliaDevicesFree`: drops the enumeration snapshot.
///
/// # Safety
///
/// `e` must be null or a live engine pointer.
#[unsafe(export_name = "mireliaDevicesFree")]
pub unsafe extern "C" fn mirelia_devices_free(e: *mut MireliaEngine) {
    if !e.is_null() {
        let wrapper = unsafe { &mut *e };
        wrapper.device_infos.clear();
        wrapper.device_strings.clear();
    }
}

// ---- device open/close -------------------------------------------------

/// Implements `mireliaEngineOpenDevice`.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(export_name = "mireliaEngineOpenDevice")]
pub unsafe extern "C" fn mirelia_engine_open_device(
    e: *mut MireliaEngine,
    device_id: *const c_char,
    mode: i32,
    desired: *const MireliaStreamFormat,
    out_actual: *mut MireliaStreamFormat,
) -> i32 {
    if e.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaEngineOpenDevice: null engine",
        );
    }
    let share_mode = match mode {
        0 => mirelia_audio::output::ShareMode::Shared,
        1 => mirelia_audio::output::ShareMode::Exclusive,
        _ => {
            return fail(
                MIRELIA_ERR_INVALID_ARG,
                "mireliaEngineOpenDevice: unknown share mode",
            );
        }
    };
    let wrapper = unsafe { &mut *e };
    let id = match unsafe { utf8_arg(device_id) } {
        Ok(id) => id,
        Err(code) => return code,
    };
    let desired_fmt = if desired.is_null() {
        None
    } else {
        let d = unsafe { &*desired };
        if d.sample_rate == 0 {
            return fail(
                MIRELIA_ERR_INVALID_ARG,
                "mireliaEngineOpenDevice: desired sample rate is 0",
            );
        }
        Some(StreamFormat {
            sample_rate: d.sample_rate,
            channels: d.channels,
        })
    };

    match wrapper
        .engine
        .open_device(id.as_deref(), desired_fmt, share_mode)
    {
        Ok(actual) => {
            if !out_actual.is_null() {
                unsafe {
                    *out_actual = MireliaStreamFormat {
                        sample_rate: actual.sample_rate,
                        channels: actual.channels,
                        format: 3, // MireliaFmtF32
                        channel_mask: 0,
                    };
                }
            }
            MIRELIA_OK
        }
        Err(err) => engine_err(&err),
    }
}

/// Implements `mireliaEngineCloseDevice`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(export_name = "mireliaEngineCloseDevice")]
pub unsafe extern "C" fn mirelia_engine_close_device(e: *mut MireliaEngine) -> i32 {
    if e.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaEngineCloseDevice: null engine",
        );
    }
    unsafe { &mut *e }.engine.close_device();
    MIRELIA_OK
}

// ---- transport ---------------------------------------------------------

fn slot_from(slot: i32) -> Result<Slot, i32> {
    match slot {
        0 => Ok(Slot::Current),
        1 => Ok(Slot::Next),
        _ => Err(fail(MIRELIA_ERR_INVALID_ARG, "invalid slot")),
    }
}

/// Implements `mireliaLoad`.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(export_name = "mireliaLoad")]
pub unsafe extern "C" fn mirelia_load(
    e: *mut MireliaEngine,
    slot: i32,
    p: *const MireliaLoadParams,
) -> i32 {
    if e.is_null() || p.is_null() {
        return fail(MIRELIA_ERR_INVALID_ARG, "mireliaLoad: null argument");
    }
    let wrapper = unsafe { &mut *e };
    let p = unsafe { &*p };
    let slot = match slot_from(slot) {
        Ok(s) => s,
        Err(code) => return code,
    };
    let Ok(Some(path)) = (unsafe { utf8_arg(p.path) }) else {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaLoad: path is null or not UTF-8",
        );
    };

    let params = LoadParams {
        path: PathBuf::from(path),
        start_frame: p.start_frame,
        end_frame: p.end_frame,
        gain_db: p.gain_db,
        user_token: p.user_token,
    };
    match wrapper.engine.load(slot, params) {
        Ok(()) => MIRELIA_OK,
        Err(err) => engine_err(&err),
    }
}

/// Implements `mireliaClear`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(export_name = "mireliaClear")]
pub unsafe extern "C" fn mirelia_clear(e: *mut MireliaEngine, slot: i32) -> i32 {
    if e.is_null() {
        return fail(MIRELIA_ERR_INVALID_ARG, "mireliaClear: null engine");
    }
    let slot = match slot_from(slot) {
        Ok(s) => s,
        Err(code) => return code,
    };
    match unsafe { &mut *e }.engine.clear(slot) {
        Ok(()) => MIRELIA_OK,
        Err(err) => engine_err(&err),
    }
}

macro_rules! transport {
    ($fn_name:ident, $symbol:literal, $method:ident, $doc:literal) => {
        #[doc = $doc]
        ///
        /// # Safety
        ///
        /// `e` must be a live engine pointer.
        #[unsafe(export_name = $symbol)]
        pub unsafe extern "C" fn $fn_name(e: *mut MireliaEngine) -> i32 {
            if e.is_null() {
                return fail(MIRELIA_ERR_INVALID_ARG, concat!($symbol, ": null engine"));
            }
            match unsafe { &mut *e }.engine.$method() {
                Ok(()) => MIRELIA_OK,
                Err(err) => engine_err(&err),
            }
        }
    };
}

transport!(
    mirelia_play,
    "mireliaPlay",
    play,
    "Implements `mireliaPlay`."
);
transport!(
    mirelia_pause,
    "mireliaPause",
    pause,
    "Implements `mireliaPause`."
);
transport!(
    mirelia_stop,
    "mireliaStop",
    stop,
    "Implements `mireliaStop` (clears both slots)."
);
transport!(
    mirelia_skip_to_next,
    "mireliaSkipToNext",
    skip_to_next,
    "Implements `mireliaSkipToNext`."
);

/// Implements `mireliaSeek`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(export_name = "mireliaSeek")]
pub unsafe extern "C" fn mirelia_seek(e: *mut MireliaEngine, frame: i64) -> i32 {
    if e.is_null() {
        return fail(MIRELIA_ERR_INVALID_ARG, "mireliaSeek: null engine");
    }
    match unsafe { &mut *e }.engine.seek(frame) {
        Ok(()) => MIRELIA_OK,
        Err(err) => engine_err(&err),
    }
}

/// Implements `mireliaSetVolume`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(export_name = "mireliaSetVolume")]
pub unsafe extern "C" fn mirelia_set_volume(e: *mut MireliaEngine, gain: f32) -> i32 {
    if e.is_null() {
        return fail(MIRELIA_ERR_INVALID_ARG, "mireliaSetVolume: null engine");
    }
    match unsafe { &mut *e }.engine.set_volume(gain) {
        Ok(()) => MIRELIA_OK,
        Err(err) => engine_err(&err),
    }
}

/// Implements `mireliaGetPosition`.
///
/// # Safety
///
/// `e` must be a live engine pointer; `out` must point to writable storage
/// for one `MireliaPosition`.
#[unsafe(export_name = "mireliaGetPosition")]
pub unsafe extern "C" fn mirelia_get_position(
    e: *mut MireliaEngine,
    out: *mut MireliaPosition,
) -> i32 {
    if e.is_null() || out.is_null() {
        return fail(MIRELIA_ERR_INVALID_ARG, "mireliaGetPosition: null argument");
    }
    let pos = unsafe { &*e }.engine.position();
    unsafe {
        *out = MireliaPosition {
            state: i32::from(match pos.state {
                State::Idle => 0u8,
                State::Playing => 1,
                State::Paused => 2,
                State::Stalled => 3,
            }),
            user_token: pos.user_token,
            frame: pos.frame,
            total_frames: pos.total_frames,
            sample_rate: pos.sample_rate,
            host_time_ns: pos.host_time_ns,
            buffer_fill: pos.buffer_fill,
        };
    }
    MIRELIA_OK
}

// ---- events ------------------------------------------------------------

/// Implements `mireliaEnginePollEvent`.
///
/// # Safety
///
/// `out` must point to writable storage for `capacity` events. Must not be
/// called concurrently with itself.
#[unsafe(export_name = "mireliaEnginePollEvent")]
pub unsafe extern "C" fn mirelia_engine_poll_event(
    e: *mut MireliaEngine,
    out: *mut MireliaEvent,
    capacity: usize,
) -> usize {
    if e.is_null() || out.is_null() || capacity == 0 {
        return 0;
    }
    let wrapper = unsafe { &mut *e };
    let mut scratch = [EngineEvent::default(); 32];
    let take = capacity.min(scratch.len());
    let n = wrapper.engine.poll_events(&mut scratch[..take]);
    for (i, ev) in scratch[..n].iter().enumerate() {
        unsafe {
            *out.add(i) = MireliaEvent {
                event_type: ev.kind as i32,
                user_token: ev.token,
                code: ev.code,
                host_time_ns: ev.time_ns,
            };
        }
    }
    n
}

// ---- metadata probe ----------------------------------------------------

/// Implements `mireliaProbe`.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(export_name = "mireliaProbe")]
pub unsafe extern "C" fn mirelia_probe(
    e: *mut MireliaEngine,
    path: *const c_char,
    out: *mut MireliaProbeResult,
) -> i32 {
    if e.is_null() || out.is_null() {
        return fail(MIRELIA_ERR_INVALID_ARG, "mireliaProbe: null argument");
    }
    let Ok(Some(path)) = (unsafe { utf8_arg(path) }) else {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaProbe: path is null or not UTF-8",
        );
    };

    let decoder = match mirelia_decode::Decoder::open(std::path::Path::new(&path)) {
        Ok(d) => d,
        Err(err) => return decode_err(&err),
    };
    let info = decoder.info();

    let mut codec = [0 as c_char; 16];
    for (dst, src) in codec.iter_mut().zip(info.codec.bytes()) {
        *dst = src as c_char;
    }

    unsafe {
        *out = MireliaProbeResult {
            sample_rate: info.sample_rate,
            channels: info.channels,
            bit_depth: info.bit_depth.unwrap_or(0),
            total_frames: info.total_frames.map(|f| f as i64).unwrap_or(-1),
            // Symphonia consumes delay/padding internally when trimming, so
            // nothing managed needs the explicit values yet.
            encoder_delay: 0,
            encoder_padding: 0,
            bitrate_kbps: info.bitrate_kbps.unwrap_or(0),
            codec,
        };
    }
    MIRELIA_OK
}

/// Implements `mireliaScanLoudness`: EBU R128 integrated loudness → RG2 gain
/// plus true peak. Blocking; call from a worker thread.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(export_name = "mireliaScanLoudness")]
pub unsafe extern "C" fn mirelia_scan_loudness(
    e: *mut MireliaEngine,
    path: *const c_char,
    out_gain_db: *mut f32,
    out_peak: *mut f32,
) -> i32 {
    if e.is_null() || out_gain_db.is_null() || out_peak.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaScanLoudness: null argument",
        );
    }
    let Ok(Some(path)) = (unsafe { utf8_arg(path) }) else {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaScanLoudness: path is null or not UTF-8",
        );
    };

    match mirelia_decode::loudness::scan(std::path::Path::new(&path)) {
        Ok(result) => {
            unsafe {
                *out_gain_db = result.gain_db;
                *out_peak = result.peak;
            }
            MIRELIA_OK
        }
        Err(err) => decode_err(&err),
    }
}

// ---- plugins -----------------------------------------------------------

/// Implements `mireliaRegisterDecoderLibrary`. Post-v1.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(export_name = "mireliaRegisterDecoderLibrary")]
pub unsafe extern "C" fn mirelia_register_decoder_library(
    _e: *mut MireliaEngine,
    _dll_path: *const c_char,
) -> i32 {
    fail(MIRELIA_ERR_UNSUPPORTED, "decoder plugins land post-v1")
}

/// Implements `mireliaRegisterOutputLibrary`. Post-v1.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(export_name = "mireliaRegisterOutputLibrary")]
pub unsafe extern "C" fn mirelia_register_output_library(
    _e: *mut MireliaEngine,
    _dll_path: *const c_char,
) -> i32 {
    fail(MIRELIA_ERR_UNSUPPORTED, "output plugins land post-v1")
}

/// Implements `mireliaRegisterDspLibrary`. Loads and validates the library on the
/// calling thread and registers its first resampler capability.
///
/// # Safety
///
/// `e` must be a live engine pointer; `dll_path` must be null or a valid
/// NUL-terminated string.
#[unsafe(export_name = "mireliaRegisterDspLibrary")]
pub unsafe extern "C" fn mirelia_register_dsp_library(
    e: *mut MireliaEngine,
    dll_path: *const c_char,
) -> i32 {
    if e.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaRegisterDspLibrary: null engine",
        );
    }
    let wrapper = unsafe { &mut *e };
    let Ok(Some(path)) = (unsafe { utf8_arg(dll_path) }) else {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaRegisterDspLibrary: path is null or not UTF-8",
        );
    };

    let loaded = match dsp::load_plugin(&path) {
        Ok(loaded) => loaded,
        Err(code) => return code,
    };
    if wrapper.dsp_plugins.iter().any(|p| p.id == loaded.plugin.id) {
        return fail(
            MIRELIA_ERR_STATE,
            &format!(
                "a DSP plugin with id {:?} is already registered",
                loaded.plugin.id
            ),
        );
    }

    // Version 1 selects a resampler by plugin id, so one id can name only one
    // resampler; any further resampler records of the plugin are ignored.
    let fns = loaded.plugin.resamplers[0];
    wrapper.dsp_plugins.push(RegisteredDsp {
        id: loaded.plugin.id,
        name: loaded.plugin.name,
        factory: Arc::new(dsp::PluginResamplerFactory::new(loaded.library, fns)),
    });
    MIRELIA_OK
}

/// Implements `mireliaDspEnumerate`. The snapshot stays valid until the next
/// `mireliaDspFree` or `mireliaDspEnumerate` call, per the header.
///
/// # Safety
///
/// `e` must be a live engine pointer; `out_infos` and `out_count` must point to
/// writable storage.
#[unsafe(export_name = "mireliaDspEnumerate")]
pub unsafe extern "C" fn mirelia_dsp_enumerate(
    e: *mut MireliaEngine,
    out_infos: *mut *const MireliaDspInfo,
    out_count: *mut usize,
) -> i32 {
    if e.is_null() || out_infos.is_null() || out_count.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaDspEnumerate: null argument",
        );
    }
    let wrapper = unsafe { &mut *e };
    wrapper.dsp_infos.clear();
    wrapper
        .dsp_infos
        .extend(wrapper.dsp_plugins.iter().map(|p| MireliaDspInfo {
            id: p.id.as_ptr(),
            name: p.name.as_ptr(),
            kind: dsp::MIRELIA_DSP_KIND_RESAMPLER,
        }));
    unsafe {
        *out_infos = wrapper.dsp_infos.as_ptr();
        *out_count = wrapper.dsp_infos.len();
    }
    MIRELIA_OK
}

/// Implements `mireliaDspFree`: drops the enumeration snapshot.
///
/// # Safety
///
/// `e` must be null or a live engine pointer.
#[unsafe(export_name = "mireliaDspFree")]
pub unsafe extern "C" fn mirelia_dsp_free(e: *mut MireliaEngine) {
    if !e.is_null() {
        unsafe { &mut *e }.dsp_infos.clear();
    }
}

/// Implements `mireliaEngineSetResampler`. Null selects the built-in resampler.
///
/// # Safety
///
/// `e` must be a live engine pointer; `plugin_id` must be null or a valid
/// NUL-terminated string.
#[unsafe(export_name = "mireliaEngineSetResampler")]
pub unsafe extern "C" fn mirelia_engine_set_resampler(
    e: *mut MireliaEngine,
    plugin_id: *const c_char,
) -> i32 {
    if e.is_null() {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            "mireliaEngineSetResampler: null engine",
        );
    }
    let wrapper = unsafe { &mut *e };
    let id = match unsafe { utf8_arg(plugin_id) } {
        Ok(id) => id,
        Err(code) => return code,
    };
    let Some(id) = id else {
        wrapper.engine.set_resampler(None);
        return MIRELIA_OK;
    };
    let Some(plugin) = wrapper
        .dsp_plugins
        .iter()
        .find(|p| p.id.as_bytes() == id.as_bytes())
    else {
        return fail(
            MIRELIA_ERR_INVALID_ARG,
            &format!("no resampler plugin with id {id:?} is registered"),
        );
    };
    wrapper
        .engine
        .set_resampler(Some(Arc::clone(&plugin.factory)));
    MIRELIA_OK
}

// ---- tests -------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CStr;
    use std::ptr;

    fn config(abi: u32) -> MireliaEngineConfig {
        MireliaEngineConfig {
            abi_version: abi,
            target_latency_ms: 0,
            decode_ahead_ms: 0,
            allow_resample: 1,
            dither_to_int: 1,
        }
    }

    #[test]
    fn create_rejects_wrong_abi_version() {
        let cfg = config(999);
        let mut e: *mut MireliaEngine = ptr::null_mut();
        let rc = unsafe { mirelia_engine_create(&cfg, &mut e) };
        assert_eq!(rc, MIRELIA_ERR_ABI_MISMATCH);
        assert!(e.is_null());
        let msg = unsafe { CStr::from_ptr(mirelia_engine_last_error(e)) };
        assert!(msg.to_str().unwrap().contains("ABI"));
    }

    #[test]
    fn dither_to_int_maps_to_a_mode() {
        for (value, expected) in [
            (0, DitherMode::Off),
            (1, DitherMode::Always),
            (2, DitherMode::Resampled),
            (-1, DitherMode::Always),
            (7, DitherMode::Always),
        ] {
            let mut cfg = config(MIRELIA_ABI_VERSION);
            cfg.dither_to_int = value;
            let mut e: *mut MireliaEngine = ptr::null_mut();
            assert_eq!(unsafe { mirelia_engine_create(&cfg, &mut e) }, MIRELIA_OK);
            assert_eq!(
                unsafe { &*e }.engine.config().dither,
                expected,
                "value {value}"
            );
            unsafe { mirelia_engine_destroy(e) };
        }
    }

    #[test]
    fn create_and_destroy_roundtrip() {
        let cfg = config(MIRELIA_ABI_VERSION);
        let mut e: *mut MireliaEngine = ptr::null_mut();
        assert_eq!(unsafe { mirelia_engine_create(&cfg, &mut e) }, MIRELIA_OK);
        assert!(!e.is_null());

        let mut pos = MireliaPosition {
            state: -1,
            user_token: 1,
            frame: 1,
            total_frames: 0,
            sample_rate: 1,
            host_time_ns: 0,
            buffer_fill: 1.0,
        };
        assert_eq!(unsafe { mirelia_get_position(e, &mut pos) }, MIRELIA_OK);
        assert_eq!(pos.state, 0);
        assert_eq!(pos.total_frames, -1);

        unsafe { mirelia_engine_destroy(e) };
    }

    #[test]
    fn null_arguments_are_rejected() {
        let rc = unsafe { mirelia_engine_create(ptr::null(), ptr::null_mut()) };
        assert_eq!(rc, MIRELIA_ERR_INVALID_ARG);
    }

    #[test]
    fn probe_reads_fixture_metadata() {
        let cfg = config(MIRELIA_ABI_VERSION);
        let mut e: *mut MireliaEngine = ptr::null_mut();
        assert_eq!(unsafe { mirelia_engine_create(&cfg, &mut e) }, MIRELIA_OK);

        let path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../../tests/fixtures/fixture.flac");
        let c_path = std::ffi::CString::new(path.to_str().unwrap()).unwrap();
        let mut probe = unsafe { std::mem::zeroed::<MireliaProbeResult>() };
        assert_eq!(
            unsafe { mirelia_probe(e, c_path.as_ptr(), &mut probe) },
            MIRELIA_OK
        );
        assert_eq!(probe.sample_rate, 44_100);
        assert_eq!(probe.channels, 2);
        assert_eq!(probe.total_frames, 17_640);
        let codec = unsafe { CStr::from_ptr(probe.codec.as_ptr()) };
        assert_eq!(codec.to_str().unwrap(), "flac");

        unsafe { mirelia_engine_destroy(e) };
    }
}
