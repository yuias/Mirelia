//! C ABI surface of the Rust audio backend.
//!
//! This crate implements `native/include/mirelia/audio_core.h` and is the
//! only crate in the workspace that exposes `extern "C"`. The header is the
//! contract; every exported symbol, struct layout, and error code here must
//! match it exactly. On disagreement, the header wins.
//!
//! M0 status: engine lifecycle, version negotiation, position/event polling.
//! Device I/O, transport, probing, and plugins return `KN_ERR_UNSUPPORTED`
//! until M2.

use std::cell::RefCell;
use std::ffi::{CString, c_char};

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

fn unsupported(what: &str) -> i32 {
    fail(KN_ERR_UNSUPPORTED, &format!("{what}: not implemented in M0"))
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
    out: *mut *mut Engine,
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
    let engine = Engine::new(EngineConfig {
        target_latency_ms: cfg.target_latency_ms,
        decode_ahead_ms: if cfg.decode_ahead_ms == 0 {
            EngineConfig::default().decode_ahead_ms
        } else {
            cfg.decode_ahead_ms
        },
        allow_resample: cfg.allow_resample != 0,
        dither_to_int: cfg.dither_to_int != 0,
    });
    unsafe { *out = Box::into_raw(Box::new(engine)) };
    KN_OK
}

/// Implements `kn_engine_destroy`. Null is a no-op.
///
/// # Safety
///
/// `e` must be null or a pointer previously returned by [`kn_engine_create`]
/// that has not been destroyed yet.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_destroy(e: *mut Engine) {
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
pub unsafe extern "C" fn kn_engine_last_error(_e: *mut Engine) -> *const c_char {
    LAST_ERROR.with(|e| e.borrow().as_ptr())
}

// ---- device enumeration ------------------------------------------------

/// Implements `kn_devices_enumerate`. M0: reports an empty device list.
///
/// # Safety
///
/// `out_devices` and `out_count` must point to writable storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_devices_enumerate(
    e: *mut Engine,
    out_devices: *mut *const KnDeviceInfo,
    out_count: *mut usize,
) -> i32 {
    if e.is_null() || out_devices.is_null() || out_count.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_devices_enumerate: null argument");
    }
    unsafe {
        *out_devices = std::ptr::null();
        *out_count = 0;
    }
    KN_OK
}

/// Implements `kn_devices_free`. M0: no snapshot exists, so this is a no-op.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_devices_free(_e: *mut Engine) {}

// ---- device open/close -------------------------------------------------

/// Implements `kn_engine_open_device`. M2.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_open_device(
    _e: *mut Engine,
    _device_id: *const c_char,
    _mode: i32,
    _desired: *const KnStreamFormat,
    _out_actual: *mut KnStreamFormat,
) -> i32 {
    unsupported("kn_engine_open_device")
}

/// Implements `kn_engine_close_device`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_close_device(_e: *mut Engine) -> i32 {
    unsupported("kn_engine_close_device")
}

// ---- transport ---------------------------------------------------------

/// Implements `kn_load`. M2.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_load(_e: *mut Engine, _slot: i32, _p: *const KnLoadParams) -> i32 {
    unsupported("kn_load")
}

/// Implements `kn_clear`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_clear(_e: *mut Engine, _slot: i32) -> i32 {
    unsupported("kn_clear")
}

/// Implements `kn_play`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_play(_e: *mut Engine) -> i32 {
    unsupported("kn_play")
}

/// Implements `kn_pause`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_pause(_e: *mut Engine) -> i32 {
    unsupported("kn_pause")
}

/// Implements `kn_stop`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_stop(_e: *mut Engine) -> i32 {
    unsupported("kn_stop")
}

/// Implements `kn_seek`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_seek(_e: *mut Engine, _frame: i64) -> i32 {
    unsupported("kn_seek")
}

/// Implements `kn_skip_to_next`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_skip_to_next(_e: *mut Engine) -> i32 {
    unsupported("kn_skip_to_next")
}

/// Implements `kn_set_volume`. M2.
///
/// # Safety
///
/// `_e` must be null or a live engine pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_set_volume(_e: *mut Engine, _gain: f32) -> i32 {
    unsupported("kn_set_volume")
}

/// Implements `kn_get_position`.
///
/// # Safety
///
/// `e` must be a live engine pointer; `out` must point to writable storage
/// for one `kn_position`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_get_position(e: *mut Engine, out: *mut KnPosition) -> i32 {
    if e.is_null() || out.is_null() {
        return fail(KN_ERR_INVALID_ARG, "kn_get_position: null argument");
    }
    let pos = unsafe { &*e }.position();
    unsafe {
        *out = KnPosition {
            state: match pos.state {
                State::Idle => 0,
                State::Playing => 1,
                State::Paused => 2,
                State::Stalled => 3,
            },
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

/// Implements `kn_engine_poll_event`. M0: the queue is always empty.
///
/// # Safety
///
/// `out` must point to writable storage for `capacity` events. Must not be
/// called concurrently with itself.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_engine_poll_event(
    _e: *mut Engine,
    _out: *mut KnEvent,
    _capacity: usize,
) -> usize {
    0
}

// ---- metadata probe ----------------------------------------------------

/// Implements `kn_probe`. M2.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_probe(
    _e: *mut Engine,
    _path: *const c_char,
    _out: *mut KnProbeResult,
) -> i32 {
    unsupported("kn_probe")
}

/// Implements `kn_scan_loudness`. M5.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_scan_loudness(
    _e: *mut Engine,
    _path: *const c_char,
    _out_gain_db: *mut f32,
    _out_peak: *mut f32,
) -> i32 {
    unsupported("kn_scan_loudness")
}

// ---- plugins -----------------------------------------------------------

/// Implements `kn_register_decoder_library`. Post-v1.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_register_decoder_library(
    _e: *mut Engine,
    _dll_path: *const c_char,
) -> i32 {
    unsupported("kn_register_decoder_library")
}

/// Implements `kn_register_output_library`. Post-v1.
///
/// # Safety
///
/// Pointer arguments must be valid per the header contract.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn kn_register_output_library(
    _e: *mut Engine,
    _dll_path: *const c_char,
) -> i32 {
    unsupported("kn_register_output_library")
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
        let mut e: *mut Engine = ptr::null_mut();
        let rc = unsafe { kn_engine_create(&cfg, &mut e) };
        assert_eq!(rc, KN_ERR_ABI_MISMATCH);
        assert!(e.is_null());
        let msg = unsafe { CStr::from_ptr(kn_engine_last_error(e)) };
        assert!(msg.to_str().unwrap().contains("ABI"));
    }

    #[test]
    fn create_and_destroy_roundtrip() {
        let cfg = config(KN_ABI_VERSION);
        let mut e: *mut Engine = ptr::null_mut();
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
}
