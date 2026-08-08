//! C ABI surface of the Rust audio backend.
//!
//! This crate implements `native/include/mirelia/audio_core.h` and is the
//! only crate in the workspace that exposes `extern "C"`. The header is the
//! contract; every exported symbol, struct layout, and error code here must
//! match it exactly. On disagreement, the header wins.
//!
//! M2 status: device enumeration, shared-mode playback, transport, seek,
//! volume, probe. Exclusive mode, loudness scan, and plugin registration
//! return `KN_ERR_UNSUPPORTED` until their milestones.

use std::cell::RefCell;
use std::ffi::{CStr, CString, c_char};
use std::path::PathBuf;

use mirelia_audio::engine::{EngineError, LoadParams, Slot};
use mirelia_audio::events::EngineEvent;
use mirelia_audio::output::{OutputBackend, StreamFormat};
use mirelia_audio::{Engine, EngineConfig, State};

// ---- kn_result ---------------------------------------------------------

/// `kn_result` values. Mirrors `audio_core.h`; the header is the documentation.
#[allow(missing_docs)]
pub mod result {
    pub const KN_OK: i32 = 0;
    pub const KN_ERR_INVALID_ARG: i32 = -1;
    pub const KN_ERR_UNSUPPORTED: i32 = -2;
    pub const KN_ERR_DEVICE_LOST: i32 = -3;
    pub const KN_ERR_DEVICE_BUSY: i32 = -4;
    pub const KN_ERR_DECODE: i32 = -5;
    pub const KN_ERR_IO: i32 = -6;
    pub const KN_ERR_FORMAT: i32 = -7;
    pub const KN_ERR_STATE: i32 = -8;
    pub const KN_ERR_OOM: i32 = -9;
    pub const KN_ERR_ABI_MISMATCH: i32 = -10;
    pub const KN_ERR_INTERNAL: i32 = -99;
}
use result::*;

/// The ABI version this backend implements (`KN_ABI_VERSION`).
pub const KN_ABI_VERSION: u32 = 1;

// ---- C struct mirrors --------------------------------------------------

/// Mirrors of the `kn_*` structs. Layouts must match `audio_core.h` exactly;
/// the header is the documentation.
#[allow(missing_docs)]
pub mod types {
    use std::ffi::c_char;

    #[repr(C)]
    pub struct KnEngineConfig {
        pub abi_version: u32,
        pub target_latency_ms: u32,
        pub decode_ahead_ms: u32,
        pub allow_resample: i32,
        pub dither_to_int: i32,
    }

    #[repr(C)]
    pub struct KnStreamFormat {
        pub sample_rate: u32,
        pub channels: u16,
        pub format: i32,
        pub channel_mask: u32,
    }

    #[repr(C)]
    pub struct KnDeviceInfo {
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
    pub struct KnLoadParams {
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
    pub struct KnPosition {
        pub state: i32,
        pub user_token: u64,
        pub frame: i64,
        pub total_frames: i64,
        pub sample_rate: u32,
        pub host_time_ns: u64,
        pub buffer_fill: f32,
    }

    #[repr(C)]
    pub struct KnEvent {
        pub event_type: i32,
        pub user_token: u64,
        pub code: i32,
        pub host_time_ns: u64,
    }

    #[repr(C)]
    pub struct KnProbeResult {
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
        fn enumerate(
            &mut self,
        ) -> Result<Vec<mirelia_audio::output::DeviceInfo>, mirelia_audio::output::OutputError>
        {
            Ok(Vec::new())
        }

        fn probe_device(
            &mut self,
            _id: Option<&str>,
        ) -> Result<mirelia_audio::output::DeviceInfo, mirelia_audio::output::OutputError>
        {
            Err(mirelia_audio::output::OutputError::Unsupported(
                "no output backend for this platform yet".into(),
            ))
        }

        fn open_stream(
            &mut self,
            _id: Option<&str>,
            _format: StreamFormat,
            _callback: mirelia_audio::output::RenderCallback,
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

/// The opaque `kn_engine`: the engine plus ABI-side snapshot storage.
pub struct KnEngine {
    engine: Engine,
    /// Device snapshot backing storage; pointers in `device_infos` point here.
    device_strings: Vec<CString>,
    device_infos: Vec<KnDeviceInfo>,
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
        EngineError::InvalidArg(_) => KN_ERR_INVALID_ARG,
        EngineError::Unsupported(_) => KN_ERR_UNSUPPORTED,
        EngineError::DeviceLost(_) => KN_ERR_DEVICE_LOST,
        EngineError::DeviceBusy(_) => KN_ERR_DEVICE_BUSY,
        EngineError::Decode(_) => KN_ERR_DECODE,
        EngineError::Io(_) => KN_ERR_IO,
        EngineError::Format(_) => KN_ERR_FORMAT,
        EngineError::State(_) => KN_ERR_STATE,
        EngineError::Internal(_) => KN_ERR_INTERNAL,
    }
}

fn decode_err(e: &mirelia_decode::DecodeError) -> i32 {
    set_last_error(&e.to_string());
    match e {
        mirelia_decode::DecodeError::Io(_) => KN_ERR_IO,
        mirelia_decode::DecodeError::Unsupported(_) => KN_ERR_FORMAT,
        mirelia_decode::DecodeError::Malformed(_) => KN_ERR_DECODE,
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
        .map_err(|_| fail(KN_ERR_INVALID_ARG, "string argument is not valid UTF-8"))
}

// ---- engine lifecycle --------------------------------------------------

/// Implements `kn_engine_create`. Rejects any `abi_version` other than
/// [`KN_ABI_VERSION`] with `KN_ERR_ABI_MISMATCH`.
///
/// # Safety
///
/// `cfg` must point to a valid `kn_engine_config`; `out` must point to
/// writable storage for one pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_create(
    cfg: *const KnEngineConfig,
    out: *mut *mut KnEngine,
) -> i32 {
    if cfg.is_null() || out.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_engine_create: null argument");
    }
    let cfg = unsafe { &*cfg };
    if cfg.abi_version != KN_ABI_VERSION {
        return fail(
            KN_ERR_ABI_MISMATCH,
            &format!(
                "kn_engine_create: host requested ABI v{}, backend implements v{KN_ABI_VERSION}",
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
            dither_to_int: cfg.dither_to_int != 0,
        },
        make_backend(),
    );
    let wrapper = Box::new(KnEngine {
        engine,
        device_strings: Vec::new(),
        device_infos: Vec::new(),
    });
    unsafe { *out = Box::into_raw(wrapper) };
    KN_OK
}

/// Implements `kn_engine_destroy`. Null is a no-op.
///
/// # Safety
///
/// `e` must be null or a pointer previously returned by [`kn_engine_create`]
/// that has not been destroyed yet.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_destroy(e: *mut KnEngine) {
    if !e.is_null() {
        drop(unsafe { Box::from_raw(e) });
    }
}

/// Implements `kn_engine_last_error`. The returned pointer is valid until
/// the next failing call on the same thread.
///
/// # Safety
///
/// The returned pointer must not be freed or retained past the next call
/// into this library from the same thread.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_last_error(_e: *mut KnEngine) -> *const c_char {
    LAST_ERROR.with(|e| e.borrow().as_ptr())
}

// ---- device enumeration ------------------------------------------------

/// Implements `kn_devices_enumerate`. The snapshot stays valid until the
/// next enumerate or `kn_devices_free` call, per the header.
///
/// # Safety
///
/// All pointers must be valid; `e` must be a live engine.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_devices_enumerate(
    e: *mut KnEngine,
    out_devices: *mut *const KnDeviceInfo,
    out_count: *mut usize,
) -> i32 {
    if e.is_null() || out_devices.is_null() || out_count.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_devices_enumerate: null argument");
    }
    let wrapper = unsafe { &mut *e };

    let devices = match wrapper.engine.enumerate() {
        Ok(d) => d,
        Err(err) => return engine_err(&err),
    };

    wrapper.device_strings.clear();
    wrapper.device_infos.clear();
    let backend_name = CString::new("wasapi").unwrap();
    wrapper.device_strings.push(backend_name);
    let backend_ptr = wrapper.device_strings[0].as_ptr();

    for d in &devices {
        let id = CString::new(d.id.as_str()).unwrap_or_default();
        let name = CString::new(d.name.as_str()).unwrap_or_default();
        wrapper.device_strings.push(id);
        let id_ptr = wrapper.device_strings.last().unwrap().as_ptr();
        wrapper.device_strings.push(name);
        let name_ptr = wrapper.device_strings.last().unwrap().as_ptr();
        wrapper.device_infos.push(KnDeviceInfo {
            id: id_ptr,
            name: name_ptr,
            backend: backend_ptr,
            is_default: i32::from(d.is_default),
            native_rate: d.native_rate,
            native_channels: d.native_channels,
            supported_rate_mask: 0,
            supported_format_mask: 1 << 3, // KN_FMT_F32 via shared-mode mixer
            supports_exclusive: 0,         // M5
            min_period_frames: 0,
            default_period_frames: 0,
        });
    }

    unsafe {
        *out_devices = wrapper.device_infos.as_ptr();
        *out_count = wrapper.device_infos.len();
    }
    KN_OK
}

/// Implements `kn_devices_free`: drops the enumeration snapshot.
///
/// # Safety
///
/// `e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_devices_free(e: *mut KnEngine) {
    if !e.is_null() {
        let wrapper = unsafe { &mut *e };
        wrapper.device_infos.clear();
        wrapper.device_strings.clear();
    }
}

// ---- device open/close -------------------------------------------------

/// Implements `kn_engine_open_device`. Exclusive mode is M5.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_open_device(
    e: *mut KnEngine,
    device_id: *const c_char,
    mode: i32,
    desired: *const KnStreamFormat,
    out_actual: *mut KnStreamFormat,
) -> i32 {
    if e.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_engine_open_device: null engine");
    }
    if mode != 0 {
        return fail(
            KN_ERR_UNSUPPORTED,
            "kn_engine_open_device: exclusive mode lands in M5",
        );
    }
    let wrapper = unsafe { &mut *e };
    let id = match unsafe { utf8_arg(device_id) } {
        Ok(id) => id,
        Err(code) => return code,
    };
    let desired_fmt = if desired.is_null() {
        None
    } else {
        let d = unsafe { &*desired };
        Some(StreamFormat {
            sample_rate: d.sample_rate,
            channels: d.channels,
        })
    };

    match wrapper.engine.open_device(id.as_deref(), desired_fmt) {
        Ok(actual) => {
            if !out_actual.is_null() {
                unsafe {
                    *out_actual = KnStreamFormat {
                        sample_rate: actual.sample_rate,
                        channels: actual.channels,
                        format: 3, // KN_FMT_F32
                        channel_mask: 0,
                    };
                }
            }
            KN_OK
        }
        Err(err) => engine_err(&err),
    }
}

/// Implements `kn_engine_close_device`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_close_device(e: *mut KnEngine) -> i32 {
    if e.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_engine_close_device: null engine");
    }
    unsafe { &mut *e }.engine.close_device();
    KN_OK
}

// ---- transport ---------------------------------------------------------

fn slot_from(slot: i32) -> Result<Slot, i32> {
    match slot {
        0 => Ok(Slot::Current),
        1 => Ok(Slot::Next),
        _ => Err(fail(KN_ERR_INVALID_ARG, "invalid slot")),
    }
}

/// Implements `kn_load`.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_load(e: *mut KnEngine, slot: i32, p: *const KnLoadParams) -> i32 {
    if e.is_null() || p.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_load: null argument");
    }
    let wrapper = unsafe { &mut *e };
    let p = unsafe { &*p };
    let slot = match slot_from(slot) {
        Ok(s) => s,
        Err(code) => return code,
    };
    let Ok(Some(path)) = (unsafe { utf8_arg(p.path) }) else {
        return fail(KN_ERR_INVALID_ARG, "kn_load: path is null or not UTF-8");
    };

    let params = LoadParams {
        path: PathBuf::from(path),
        start_frame: p.start_frame,
        end_frame: p.end_frame,
        gain_db: p.gain_db,
        user_token: p.user_token,
    };
    match wrapper.engine.load(slot, params) {
        Ok(()) => KN_OK,
        Err(err) => engine_err(&err),
    }
}

/// Implements `kn_clear`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_clear(e: *mut KnEngine, slot: i32) -> i32 {
    if e.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_clear: null engine");
    }
    let slot = match slot_from(slot) {
        Ok(s) => s,
        Err(code) => return code,
    };
    match unsafe { &mut *e }.engine.clear(slot) {
        Ok(()) => KN_OK,
        Err(err) => engine_err(&err),
    }
}

macro_rules! transport {
    ($fn_name:ident, $method:ident, $doc:literal) => {
        #[doc = $doc]
        ///
        /// # Safety
        ///
        /// `e` must be a live engine pointer.
        #[unsafe(no_mangle)]
        pub unsafe extern "C" fn $fn_name(e: *mut KnEngine) -> i32 {
            if e.is_null() {
                return fail(KN_ERR_INVALID_ARG, concat!(stringify!($fn_name), ": null engine"));
            }
            match unsafe { &mut *e }.engine.$method() {
                Ok(()) => KN_OK,
                Err(err) => engine_err(&err),
            }
        }
    };
}

transport!(kn_play, play, "Implements `kn_play`.");
transport!(kn_pause, pause, "Implements `kn_pause`.");
transport!(kn_stop, stop, "Implements `kn_stop` (clears both slots).");
transport!(kn_skip_to_next, skip_to_next, "Implements `kn_skip_to_next`.");

/// Implements `kn_seek`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_seek(e: *mut KnEngine, frame: i64) -> i32 {
    if e.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_seek: null engine");
    }
    match unsafe { &mut *e }.engine.seek(frame) {
        Ok(()) => KN_OK,
        Err(err) => engine_err(&err),
    }
}

/// Implements `kn_set_volume`.
///
/// # Safety
///
/// `e` must be a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_set_volume(e: *mut KnEngine, gain: f32) -> i32 {
    if e.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_set_volume: null engine");
    }
    match unsafe { &mut *e }.engine.set_volume(gain) {
        Ok(()) => KN_OK,
        Err(err) => engine_err(&err),
    }
}

/// Implements `kn_get_position`.
///
/// # Safety
///
/// `e` must be a live engine pointer; `out` must point to writable storage
/// for one `kn_position`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_get_position(e: *mut KnEngine, out: *mut KnPosition) -> i32 {
    if e.is_null() || out.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_get_position: null argument");
    }
    let pos = unsafe { &*e }.engine.position();
    unsafe {
        *out = KnPosition {
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
    KN_OK
}

// ---- events ------------------------------------------------------------

/// Implements `kn_engine_poll_event`.
///
/// # Safety
///
/// `out` must point to writable storage for `capacity` events. Must not be
/// called concurrently with itself.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_poll_event(
    e: *mut KnEngine,
    out: *mut KnEvent,
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
            *out.add(i) = KnEvent {
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

/// Implements `kn_probe`.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_probe(
    e: *mut KnEngine,
    path: *const c_char,
    out: *mut KnProbeResult,
) -> i32 {
    if e.is_null() || out.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_probe: null argument");
    }
    let Ok(Some(path)) = (unsafe { utf8_arg(path) }) else {
        return fail(KN_ERR_INVALID_ARG, "kn_probe: path is null or not UTF-8");
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
        *out = KnProbeResult {
            sample_rate: info.sample_rate,
            channels: info.channels,
            bit_depth: info.bit_depth.unwrap_or(0),
            total_frames: info.total_frames.map(|f| f as i64).unwrap_or(-1),
            // Symphonia consumes delay/padding internally when trimming;
            // explicit values are surfaced in M5 for the conformance suite.
            encoder_delay: 0,
            encoder_padding: 0,
            bitrate_kbps: info.bitrate_kbps.unwrap_or(0),
            codec,
        };
    }
    KN_OK
}

/// Implements `kn_scan_loudness`. M5.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_scan_loudness(
    _e: *mut KnEngine,
    _path: *const c_char,
    _out_gain_db: *mut f32,
    _out_peak: *mut f32,
) -> i32 {
    fail(KN_ERR_UNSUPPORTED, "kn_scan_loudness: lands in M5")
}

// ---- plugins -----------------------------------------------------------

/// Implements `kn_register_decoder_library`. Post-v1.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_register_decoder_library(
    _e: *mut KnEngine,
    _dll_path: *const c_char,
) -> i32 {
    fail(KN_ERR_UNSUPPORTED, "decoder plugins land post-v1")
}

/// Implements `kn_register_output_library`. Post-v1.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_register_output_library(
    _e: *mut KnEngine,
    _dll_path: *const c_char,
) -> i32 {
    fail(KN_ERR_UNSUPPORTED, "output plugins land post-v1")
}

// ---- tests -------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use std::ffi::CStr;
    use std::ptr;

    fn config(abi: u32) -> KnEngineConfig {
        KnEngineConfig {
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
        let mut e: *mut KnEngine = ptr::null_mut();
        let rc = unsafe { kn_engine_create(&cfg, &mut e) };
        assert_eq!(rc, KN_ERR_ABI_MISMATCH);
        assert!(e.is_null());
        let msg = unsafe { CStr::from_ptr(kn_engine_last_error(e)) };
        assert!(msg.to_str().unwrap().contains("ABI"));
    }

    #[test]
    fn create_and_destroy_roundtrip() {
        let cfg = config(KN_ABI_VERSION);
        let mut e: *mut KnEngine = ptr::null_mut();
        assert_eq!(unsafe { kn_engine_create(&cfg, &mut e) }, KN_OK);
        assert!(!e.is_null());

        let mut pos = KnPosition {
            state: -1,
            user_token: 1,
            frame: 1,
            total_frames: 0,
            sample_rate: 1,
            host_time_ns: 0,
            buffer_fill: 1.0,
        };
        assert_eq!(unsafe { kn_get_position(e, &mut pos) }, KN_OK);
        assert_eq!(pos.state, 0);
        assert_eq!(pos.total_frames, -1);

        unsafe { kn_engine_destroy(e) };
    }

    #[test]
    fn null_arguments_are_rejected() {
        let rc = unsafe { kn_engine_create(ptr::null(), ptr::null_mut()) };
        assert_eq!(rc, KN_ERR_INVALID_ARG);
    }

    #[test]
    fn probe_reads_fixture_metadata() {
        let cfg = config(KN_ABI_VERSION);
        let mut e: *mut KnEngine = ptr::null_mut();
        assert_eq!(unsafe { kn_engine_create(&cfg, &mut e) }, KN_OK);

        let path = std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../../tests/fixtures/fixture.flac");
        let c_path = std::ffi::CString::new(path.to_str().unwrap()).unwrap();
        let mut probe = unsafe { std::mem::zeroed::<KnProbeResult>() };
        assert_eq!(unsafe { kn_probe(e, c_path.as_ptr(), &mut probe) }, KN_OK);
        assert_eq!(probe.sample_rate, 44_100);
        assert_eq!(probe.channels, 2);
        assert_eq!(probe.total_frames, 17_640);
        let codec = unsafe { CStr::from_ptr(probe.codec.as_ptr()) };
        assert_eq!(codec.to_str().unwrap(), "flac");

        unsafe { kn_engine_destroy(e) };
    }
}
