//! DSP plugin ABI: mirrors of `native/include/mirelia/audio_dsp.h`, the
//! validation of a plugin's static description, and the wrapper that turns a
//! plugin's resampler vtable into a [`StreamResampler`].
//!
//! Plugin files are data from outside the program. Their static contents are
//! checked once, in [`validate_plugin`]; the frame counts a plugin returns at
//! run time are checked at each call in [`PluginResampler`]. Everything past
//! those two points trusts the types.

use std::ffi::{CStr, CString, c_char, c_void};
use std::sync::Arc;

use mirelia_audio::resample::{ResamplerError, ResamplerFactory, StreamResampler};

use crate::result::{KN_ERR_FORMAT, KN_ERR_UNSUPPORTED, KN_OK};

/// `KN_DSP_ABI_VERSION`: the DSP plugin ABI version this host implements.
pub const KN_DSP_ABI_VERSION: u32 = 1;

/// `KN_DSP_KIND_RESAMPLER`: the capability kind of a sample-rate converter.
pub const KN_DSP_KIND_RESAMPLER: i32 = 1;

const MAX_ID_BYTES: usize = 64;
const MAX_NAME_BYTES: usize = 128;
const MAX_CAPABILITIES: u32 = 32;

// ---- C struct mirrors --------------------------------------------------

/// Mirror of `kn_dsp_capability`. Layouts must match `audio_dsp.h` exactly;
/// the header is the documentation.
#[repr(C)]
pub struct KnDspCapability {
    /// A `kn_dsp_kind` value.
    pub kind: i32,
    /// Layout chosen by `kind`; for a resampler, a `KnDspResamplerVtable`.
    pub vtable: *const c_void,
}

/// Mirror of `kn_dsp_plugin`.
#[repr(C)]
pub struct KnDspPlugin {
    /// The ABI version the plugin answered for.
    pub abi_version: u32,
    /// Stable identifier, 1..64 bytes of `[a-z0-9._-]`.
    pub id: *const c_char,
    /// Display name, UTF-8, 1..128 bytes.
    pub name: *const c_char,
    /// Number of records behind `capabilities`, 1..32.
    pub capability_count: u32,
    /// The capability records.
    pub capabilities: *const KnDspCapability,
}

/// `kn_dsp_resampler_vtable::create`.
pub type CreateFn = unsafe extern "C" fn(u32, u32, u16, *mut *mut c_void) -> i32;
/// `kn_dsp_resampler_vtable::process`.
pub type ProcessFn = unsafe extern "C" fn(*mut c_void, *const f32, i64, *mut f32, i64) -> i64;
/// `kn_dsp_resampler_vtable::flush`.
pub type FlushFn = unsafe extern "C" fn(*mut c_void, *mut f32, i64) -> i64;
/// `kn_dsp_resampler_vtable::pending_frames`.
pub type PendingFn = unsafe extern "C" fn(*mut c_void) -> i64;
/// `kn_dsp_resampler_vtable::reset`.
pub type ResetFn = unsafe extern "C" fn(*mut c_void) -> i32;
/// `kn_dsp_resampler_vtable::destroy`.
pub type DestroyFn = unsafe extern "C" fn(*mut c_void);

/// Mirror of `kn_dsp_resampler_vtable`. Members are `Option` so a NULL pointer
/// from a plugin can be detected instead of being called.
#[repr(C)]
pub struct KnDspResamplerVtable {
    /// Creates a context for one stream.
    pub create: Option<CreateFn>,
    /// Converts a block of input.
    pub process: Option<ProcessFn>,
    /// Writes the output still owed and ends the run.
    pub flush: Option<FlushFn>,
    /// Output frames owed but not yet emitted.
    pub pending_frames: Option<PendingFn>,
    /// Starts a new run.
    pub reset: Option<ResetFn>,
    /// Releases the context.
    pub destroy: Option<DestroyFn>,
}

/// Mirror of `kn_dsp_info`, one entry of the enumeration snapshot.
#[repr(C)]
pub struct KnDspInfo {
    /// Plugin id, NUL-terminated.
    pub id: *const c_char,
    /// Plugin display name, NUL-terminated.
    pub name: *const c_char,
    /// A `kn_dsp_kind` value.
    pub kind: i32,
}

// ---- validation --------------------------------------------------------

/// The six resampler functions of a validated plugin, none of them NULL.
#[derive(Debug, Clone, Copy)]
pub struct ResamplerFns {
    /// Creates a context.
    pub create: CreateFn,
    /// Converts a block of input.
    pub process: ProcessFn,
    /// Writes the output still owed.
    pub flush: FlushFn,
    /// Output frames owed but not yet emitted.
    pub pending_frames: PendingFn,
    /// Starts a new run.
    pub reset: ResetFn,
    /// Releases a context.
    pub destroy: DestroyFn,
}

/// A plugin description that passed [`validate_plugin`], copied out of the
/// plugin's static data except for the function pointers.
#[derive(Debug)]
pub struct ValidPlugin {
    /// The plugin id.
    pub id: CString,
    /// The plugin display name.
    pub name: CString,
    /// One entry per `KN_DSP_KIND_RESAMPLER` record, in the plugin's order.
    pub resamplers: Vec<ResamplerFns>,
}

// Reads a C string of at most `max` bytes. A plugin's static data may lack the
// terminator, so the scan stops one byte past the limit instead of running on
// into whatever follows the array.
//
// SAFETY: `p` must be readable up to its first NUL or to `max + 1` bytes,
// whichever comes first, for the lifetime `'a`.
unsafe fn bounded_cstr<'a>(p: *const c_char, max: usize) -> Option<&'a CStr> {
    let bytes = p.cast::<u8>();
    // SAFETY: each byte read is at or before the first NUL and within `max + 1`.
    let len = (0..=max).find(|&i| unsafe { *bytes.add(i) } == 0)?;
    // SAFETY: `len + 1` bytes were just read, and the last one is the only NUL.
    CStr::from_bytes_with_nul(unsafe { std::slice::from_raw_parts(bytes, len + 1) }).ok()
}

/// Checks the static description a plugin's entry point returned.
///
/// Returns `KN_ERR_UNSUPPORTED` when no capability is of a kind this host
/// knows, and `KN_ERR_FORMAT` for anything malformed. Records of unknown kinds
/// are skipped without looking at their vtable. On failure the message is left
/// in the thread's last-error slot.
///
/// # Safety
///
/// `plugin` must be null or point to a `kn_dsp_plugin` that stays valid for
/// the call, with `capability_count` records behind `capabilities`. A non-null
/// `id` or `name` must be readable up to its first NUL or to one byte past the
/// maximum length, whichever comes first; termination itself is checked here.
pub unsafe fn validate_plugin(
    plugin: *const KnDspPlugin,
    requested_version: u32,
) -> Result<ValidPlugin, i32> {
    if plugin.is_null() {
        return Err(crate::fail(
            KN_ERR_FORMAT,
            "plugin entry returned no description",
        ));
    }
    // SAFETY: non-null, and valid per the caller's contract.
    let plugin = unsafe { &*plugin };
    if plugin.abi_version != requested_version {
        return Err(crate::fail(
            KN_ERR_FORMAT,
            &format!(
                "plugin describes ABI version {} but {requested_version} was requested",
                plugin.abi_version
            ),
        ));
    }

    if plugin.id.is_null() {
        return Err(crate::fail(KN_ERR_FORMAT, "plugin id is null"));
    }
    // SAFETY: non-null, and readable as far as `bounded_cstr` reads per the
    // caller's contract.
    let id = unsafe { bounded_cstr(plugin.id, MAX_ID_BYTES) }.filter(|id| {
        !id.is_empty()
            && id
                .to_bytes()
                .iter()
                .all(|b| matches!(b, b'a'..=b'z' | b'0'..=b'9' | b'.' | b'_' | b'-'))
    });
    let Some(id) = id else {
        return Err(crate::fail(
            KN_ERR_FORMAT,
            "plugin id must be 1..64 bytes of [a-z0-9._-]",
        ));
    };

    if plugin.name.is_null() {
        return Err(crate::fail(KN_ERR_FORMAT, "plugin name is null"));
    }
    // SAFETY: non-null, and readable as far as `bounded_cstr` reads per the
    // caller's contract.
    let name = unsafe { bounded_cstr(plugin.name, MAX_NAME_BYTES) };
    let Some(name) = name.filter(|n| !n.is_empty() && n.to_str().is_ok()) else {
        return Err(crate::fail(
            KN_ERR_FORMAT,
            "plugin name must be 1..128 bytes of UTF-8",
        ));
    };

    if !(1..=MAX_CAPABILITIES).contains(&plugin.capability_count) || plugin.capabilities.is_null() {
        return Err(crate::fail(
            KN_ERR_FORMAT,
            "plugin must list 1..32 capabilities",
        ));
    }
    // SAFETY: non-null, and `capability_count` records per the caller's contract.
    let capabilities = unsafe {
        std::slice::from_raw_parts(plugin.capabilities, plugin.capability_count as usize)
    };

    let mut resamplers = Vec::new();
    for capability in capabilities {
        if capability.kind != KN_DSP_KIND_RESAMPLER {
            continue;
        }
        if capability.vtable.is_null() {
            return Err(crate::fail(
                KN_ERR_FORMAT,
                "resampler capability has no vtable",
            ));
        }
        // SAFETY: non-null, and a resampler vtable for this kind per the
        // caller's contract.
        let vtable = unsafe { &*capability.vtable.cast::<KnDspResamplerVtable>() };
        let (
            Some(create),
            Some(process),
            Some(flush),
            Some(pending_frames),
            Some(reset),
            Some(destroy),
        ) = (
            vtable.create,
            vtable.process,
            vtable.flush,
            vtable.pending_frames,
            vtable.reset,
            vtable.destroy,
        )
        else {
            return Err(crate::fail(
                KN_ERR_FORMAT,
                "resampler vtable has a null function pointer",
            ));
        };
        resamplers.push(ResamplerFns {
            create,
            process,
            flush,
            pending_frames,
            reset,
            destroy,
        });
    }
    if resamplers.is_empty() {
        return Err(crate::fail(
            KN_ERR_UNSUPPORTED,
            "plugin offers no capability this backend knows",
        ));
    }

    Ok(ValidPlugin {
        id: id.to_owned(),
        name: name.to_owned(),
        resamplers,
    })
}

// ---- library handle ----------------------------------------------------

/// A loaded plugin library. Every resampler and registry entry that can call
/// into the library holds an `Arc` of this, so the library outlives its last
/// context. Dropping the last reference unloads the library.
pub struct PluginLibrary {
    #[cfg(windows)]
    module: windows::Win32::Foundation::HMODULE,
}

// SAFETY: an `HMODULE` is a process-wide handle with no thread affinity, and
// `FreeLibrary` may be called from any thread. `ResamplerFactory` requires
// `Send + Sync`, and the handle is never mutated after loading, so no lock is
// needed.
unsafe impl Send for PluginLibrary {}
// SAFETY: see the `Send` impl above.
unsafe impl Sync for PluginLibrary {}

impl PluginLibrary {
    /// A library that owns no module, for tests with in-process fakes.
    #[cfg(test)]
    fn detached() -> Self {
        Self {
            #[cfg(windows)]
            module: windows::Win32::Foundation::HMODULE::default(),
        }
    }
}

#[cfg(windows)]
impl Drop for PluginLibrary {
    fn drop(&mut self) {
        // A detached library (tests only) has a null handle.
        if !self.module.0.is_null() {
            // SAFETY: `module` came from a successful `LoadLibraryExW` and is
            // released only here. Every context that could call into the
            // library holds an `Arc` of this, so none is alive.
            let _ = unsafe { windows::Win32::Foundation::FreeLibrary(self.module) };
        }
    }
}

/// A plugin entry point: `kn_dsp_entry` in `audio_dsp.h`.
pub type EntryFn = unsafe extern "C" fn(u32) -> *const KnDspPlugin;

/// A loaded library together with its validated description.
pub struct LoadedPlugin {
    /// Keeps the library loaded; the functions in `plugin` are only valid while
    /// a reference to it exists.
    pub library: Arc<PluginLibrary>,
    /// The plugin's validated static description.
    pub plugin: ValidPlugin,
}

/// Loads the library at `path`, asks its entry point for
/// `KN_DSP_ABI_VERSION` and validates the answer. The library is unloaded again
/// on every failure.
///
/// Failure codes: `KN_ERR_INVALID_ARG` for a relative path, `KN_ERR_IO` for a
/// missing file, `KN_ERR_FORMAT` for a file that does not load or has no entry
/// point, `KN_ERR_ABI_MISMATCH` when the entry point answers `NULL`, and the
/// codes of [`validate_plugin`]. The message is left in the thread's last-error
/// slot.
#[cfg(windows)]
pub fn load_plugin(path: &str) -> Result<LoadedPlugin, i32> {
    use windows::Win32::System::LibraryLoader::{
        GetProcAddress, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR, LOAD_LIBRARY_SEARCH_SYSTEM32,
        LoadLibraryExW,
    };
    use windows::core::{HSTRING, s};

    use crate::result::{KN_ERR_ABI_MISMATCH, KN_ERR_INVALID_ARG, KN_ERR_IO};

    let file = std::path::Path::new(path);
    // `LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR` only works with an absolute path.
    if !file.is_absolute() {
        return Err(crate::fail(
            KN_ERR_INVALID_ARG,
            "kn_register_dsp_library: path is not absolute",
        ));
    }
    // Decided here because `LoadLibraryExW` reports a missing dependency with
    // the same error as a missing file.
    if let Err(err) = std::fs::metadata(file) {
        return Err(crate::fail(KN_ERR_IO, &format!("{path}: {err}")));
    }

    let wide = HSTRING::from(path);
    // SAFETY: `wide` is a NUL-terminated UTF-16 string that outlives the call.
    let module = unsafe {
        LoadLibraryExW(
            &wide,
            None,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32,
        )
    }
    .map_err(|err| crate::fail(KN_ERR_FORMAT, &format!("{path}: cannot load library: {err}")))?;
    // From here on every early return unloads the library through `Drop`.
    let library = Arc::new(PluginLibrary { module });

    // SAFETY: `module` is loaded and the name is NUL-terminated.
    let Some(entry) = (unsafe { GetProcAddress(module, s!("kn_dsp_entry")) }) else {
        return Err(crate::fail(
            KN_ERR_FORMAT,
            &format!("{path}: no kn_dsp_entry export"),
        ));
    };
    // SAFETY: `audio_dsp.h` defines the signature of `kn_dsp_entry`.
    let entry = unsafe {
        std::mem::transmute::<unsafe extern "system" fn() -> isize, EntryFn>(entry)
    };
    // SAFETY: the library stays loaded for the call, and the entry point may be
    // called with any version number.
    let description = unsafe { entry(KN_DSP_ABI_VERSION) };
    if description.is_null() {
        return Err(crate::fail(
            KN_ERR_ABI_MISMATCH,
            &format!("{path}: plugin does not implement DSP ABI version {KN_DSP_ABI_VERSION}"),
        ));
    }
    // SAFETY: a non-null answer from the entry point is static data of the
    // library, which stays loaded for the call.
    let plugin = unsafe { validate_plugin(description, KN_DSP_ABI_VERSION) }?;
    Ok(LoadedPlugin { library, plugin })
}

/// Plugins are not implemented on this platform.
#[cfg(not(windows))]
pub fn load_plugin(_path: &str) -> Result<LoadedPlugin, i32> {
    Err(crate::fail(
        KN_ERR_UNSUPPORTED,
        "DSP plugins are not implemented on this platform",
    ))
}

// ---- resampler wrapper -------------------------------------------------

/// Output frames for `frames` source frames: `ceil(frames * out_rate / in_rate)`.
fn out_frames(frames: u64, in_rate: u32, out_rate: u32) -> u64 {
    (frames * u64::from(out_rate)).div_ceil(u64::from(in_rate))
}

/// One plugin context, driven through the contract in `audio_dsp.h`.
///
/// The wrapper keeps its own `fed` and `emitted` counters and checks every
/// count the plugin returns against them, so a wrong plugin value is turned
/// into [`ResamplerError::Failed`] here and never reaches the engine.
pub struct PluginResampler {
    ctx: *mut c_void,
    fns: ResamplerFns,
    // Keeps the library loaded until after `destroy` has run in `Drop`.
    _library: Arc<PluginLibrary>,
    in_rate: u32,
    out_rate: u32,
    channels: usize,
    fed: u64,
    emitted: u64,
}

// SAFETY: a context is used from one thread at a time, which is what `Send`
// needs: the engine moves the resampler into the decode worker and never shares
// it.
unsafe impl Send for PluginResampler {}

impl PluginResampler {
    /// Wraps a context made by `fns.create` for this rate pair and channel
    /// count. Takes ownership: the context is destroyed on drop.
    ///
    /// # Safety
    ///
    /// `ctx` must be a live context from `fns.create`, not owned by anything
    /// else, and `library` must be the library `fns` belongs to.
    pub unsafe fn new(
        ctx: *mut c_void,
        fns: ResamplerFns,
        library: Arc<PluginLibrary>,
        in_rate: u32,
        out_rate: u32,
        channels: usize,
    ) -> Self {
        Self {
            ctx,
            fns,
            _library: library,
            in_rate,
            out_rate,
            channels,
            fed: 0,
            emitted: 0,
        }
    }

    fn owed(&self) -> u64 {
        out_frames(self.fed, self.in_rate, self.out_rate) - self.emitted
    }
}

impl StreamResampler for PluginResampler {
    fn process(&mut self, input: &[f32], out: &mut Vec<f32>) -> Result<(), ResamplerError> {
        let in_frames = (input.len() / self.channels) as u64;
        let new_fed = self.fed + in_frames;
        let new_total = out_frames(new_fed, self.in_rate, self.out_rate);
        let cap = new_total - self.emitted;

        let start = out.len();
        out.resize(start + cap as usize * self.channels, 0.0);
        // SAFETY: `ctx` is live, `input` holds `in_frames` frames and the
        // output region holds `cap` frames, which is what the call is told.
        let written = unsafe {
            (self.fns.process)(
                self.ctx,
                input.as_ptr(),
                in_frames as i64,
                out[start..].as_mut_ptr(),
                cap as i64,
            )
        };
        if written < 0 || written as u64 > cap {
            out.truncate(start);
            return Err(ResamplerError::Failed);
        }
        let written = written as u64;

        // SAFETY: `ctx` is live and was not failed by the call above.
        let pending = unsafe { (self.fns.pending_frames)(self.ctx) };
        if pending != (new_total - (self.emitted + written)) as i64 {
            out.truncate(start);
            return Err(ResamplerError::Failed);
        }

        out.truncate(start + written as usize * self.channels);
        self.fed = new_fed;
        self.emitted += written;
        Ok(())
    }

    fn flush(&mut self, out: &mut Vec<f32>) -> Result<(), ResamplerError> {
        let pending = self.owed();
        let start = out.len();
        out.resize(start + pending as usize * self.channels, 0.0);
        // SAFETY: `ctx` is live and the output region holds `pending` frames,
        // which is what the call is told.
        let written =
            unsafe { (self.fns.flush)(self.ctx, out[start..].as_mut_ptr(), pending as i64) };
        if written != pending as i64 {
            out.truncate(start);
            return Err(ResamplerError::Failed);
        }
        self.emitted += pending;
        Ok(())
    }

    fn pending_frames(&self) -> u64 {
        self.owed()
    }

    fn reset(&mut self) -> Result<(), ResamplerError> {
        // SAFETY: `ctx` is live.
        if unsafe { (self.fns.reset)(self.ctx) } != KN_OK {
            return Err(ResamplerError::Failed);
        }
        self.fed = 0;
        self.emitted = 0;
        Ok(())
    }
}

impl Drop for PluginResampler {
    fn drop(&mut self) {
        // SAFETY: `ctx` is live and owned by this wrapper; nothing uses it
        // afterwards, and `_library` is released only after this returns.
        unsafe { (self.fns.destroy)(self.ctx) };
    }
}

/// Creates [`PluginResampler`]s from one plugin's resampler capability.
pub struct PluginResamplerFactory {
    library: Arc<PluginLibrary>,
    fns: ResamplerFns,
}

impl PluginResamplerFactory {
    /// A factory for the resampler capability `fns` of `library`.
    pub fn new(library: Arc<PluginLibrary>, fns: ResamplerFns) -> Self {
        Self { library, fns }
    }
}

impl ResamplerFactory for PluginResamplerFactory {
    fn create(
        &self,
        in_rate: u32,
        out_rate: u32,
        channels: usize,
    ) -> Result<Box<dyn StreamResampler>, ResamplerError> {
        // A channel count that does not fit the ABI is one no plugin converts.
        let channels_u16 = u16::try_from(channels).map_err(|_| ResamplerError::Unsupported)?;
        let mut ctx: *mut c_void = std::ptr::null_mut();
        // SAFETY: `ctx` is writable storage for one pointer.
        let code = unsafe { (self.fns.create)(in_rate, out_rate, channels_u16, &mut ctx) };
        match code {
            KN_OK if !ctx.is_null() => {
                // SAFETY: `ctx` is a live context from this plugin's `create`,
                // owned by nothing else.
                let resampler = unsafe {
                    PluginResampler::new(
                        ctx,
                        self.fns,
                        Arc::clone(&self.library),
                        in_rate,
                        out_rate,
                        channels,
                    )
                };
                Ok(Box::new(resampler))
            }
            KN_ERR_UNSUPPORTED => Err(ResamplerError::Unsupported),
            _ => Err(ResamplerError::Failed),
        }
    }
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicI64, AtomicUsize, Ordering};

    use super::*;
    use crate::result::{KN_ERR_INTERNAL, KN_ERR_INVALID_ARG};

    // ---- in-process fake plugin ----

    #[derive(Clone, Copy, PartialEq, Eq)]
    enum Mode {
        Honest,
        // `process` reports one frame more than the capacity it was given.
        OverCapacity,
        // `pending_frames` reports one frame more than is owed.
        WrongPending,
        // `flush` reports one frame less than it was asked for.
        ShortFlush,
    }

    #[derive(Default)]
    struct Probe {
        last_capacity: AtomicI64,
        destroyed: AtomicUsize,
    }

    struct FakeCtx {
        in_rate: u32,
        out_rate: u32,
        channels: usize,
        fed: u64,
        emitted: u64,
        mode: Mode,
        probe: Arc<Probe>,
    }

    impl FakeCtx {
        fn owed(&self) -> u64 {
            out_frames(self.fed, self.in_rate, self.out_rate) - self.emitted
        }
    }

    fn fake_ctx(
        mode: Mode,
        in_rate: u32,
        out_rate: u32,
        channels: usize,
        probe: &Arc<Probe>,
    ) -> *mut c_void {
        Box::into_raw(Box::new(FakeCtx {
            in_rate,
            out_rate,
            channels,
            fed: 0,
            emitted: 0,
            mode,
            probe: Arc::clone(probe),
        }))
        .cast()
    }

    unsafe extern "C" fn fake_create(
        in_rate: u32,
        out_rate: u32,
        channels: u16,
        out_ctx: *mut *mut c_void,
    ) -> i32 {
        let ctx = fake_ctx(
            Mode::Honest,
            in_rate,
            out_rate,
            usize::from(channels),
            &Arc::new(Probe::default()),
        );
        unsafe { *out_ctx = ctx };
        KN_OK
    }

    unsafe extern "C" fn unsupported_create(_: u32, _: u32, _: u16, _: *mut *mut c_void) -> i32 {
        KN_ERR_UNSUPPORTED
    }

    unsafe extern "C" fn failing_create(_: u32, _: u32, _: u16, _: *mut *mut c_void) -> i32 {
        KN_ERR_INTERNAL
    }

    unsafe extern "C" fn null_ctx_create(_: u32, _: u32, _: u16, _: *mut *mut c_void) -> i32 {
        KN_OK
    }

    unsafe extern "C" fn fake_process(
        ctx: *mut c_void,
        _in: *const f32,
        in_frames: i64,
        out: *mut f32,
        out_capacity: i64,
    ) -> i64 {
        let c = unsafe { &mut *ctx.cast::<FakeCtx>() };
        c.probe.last_capacity.store(out_capacity, Ordering::SeqCst);
        let fed = c.fed + in_frames as u64;
        let total = out_frames(fed, c.in_rate, c.out_rate);
        // Hold back one frame so that pending stays above zero, as the
        // contract requires of a plugin with no delay of its own.
        let emit = total.saturating_sub(1).saturating_sub(c.emitted);
        if emit as i64 > out_capacity {
            return i64::from(KN_ERR_INVALID_ARG);
        }
        unsafe { std::slice::from_raw_parts_mut(out, emit as usize * c.channels) }.fill(1.0);
        c.fed = fed;
        c.emitted += emit;
        if c.mode == Mode::OverCapacity {
            return out_capacity + 1;
        }
        emit as i64
    }

    unsafe extern "C" fn fake_flush(ctx: *mut c_void, out: *mut f32, out_capacity: i64) -> i64 {
        let c = unsafe { &mut *ctx.cast::<FakeCtx>() };
        let owed = c.owed();
        if owed as i64 > out_capacity {
            return i64::from(KN_ERR_INVALID_ARG);
        }
        unsafe { std::slice::from_raw_parts_mut(out, owed as usize * c.channels) }.fill(1.0);
        c.emitted += owed;
        if c.mode == Mode::ShortFlush {
            return owed as i64 - 1;
        }
        owed as i64
    }

    unsafe extern "C" fn fake_pending(ctx: *mut c_void) -> i64 {
        let c = unsafe { &*ctx.cast::<FakeCtx>() };
        let owed = c.owed() as i64;
        if c.mode == Mode::WrongPending {
            owed + 1
        } else {
            owed
        }
    }

    unsafe extern "C" fn fake_reset(ctx: *mut c_void) -> i32 {
        let c = unsafe { &mut *ctx.cast::<FakeCtx>() };
        c.fed = 0;
        c.emitted = 0;
        KN_OK
    }

    unsafe extern "C" fn fake_destroy(ctx: *mut c_void) {
        let c = unsafe { Box::from_raw(ctx.cast::<FakeCtx>()) };
        c.probe.destroyed.fetch_add(1, Ordering::SeqCst);
    }

    fn fake_vtable() -> KnDspResamplerVtable {
        KnDspResamplerVtable {
            create: Some(fake_create),
            process: Some(fake_process),
            flush: Some(fake_flush),
            pending_frames: Some(fake_pending),
            reset: Some(fake_reset),
            destroy: Some(fake_destroy),
        }
    }

    fn fake_fns() -> ResamplerFns {
        ResamplerFns {
            create: fake_create,
            process: fake_process,
            flush: fake_flush,
            pending_frames: fake_pending,
            reset: fake_reset,
            destroy: fake_destroy,
        }
    }

    fn fake_resampler(mode: Mode, channels: usize, probe: &Arc<Probe>) -> PluginResampler {
        let ctx = fake_ctx(mode, 44100, 48000, channels, probe);
        // SAFETY: `ctx` is a fresh context that the fake's `destroy` frees.
        unsafe {
            PluginResampler::new(
                ctx,
                fake_fns(),
                Arc::new(PluginLibrary::detached()),
                44100,
                48000,
                channels,
            )
        }
    }

    // ---- validate_plugin ----

    fn capability(vtable: &KnDspResamplerVtable) -> KnDspCapability {
        KnDspCapability {
            kind: KN_DSP_KIND_RESAMPLER,
            vtable: std::ptr::from_ref(vtable).cast(),
        }
    }

    fn plugin(id: &CStr, name: &CStr, capabilities: &[KnDspCapability]) -> KnDspPlugin {
        KnDspPlugin {
            abi_version: KN_DSP_ABI_VERSION,
            id: id.as_ptr(),
            name: name.as_ptr(),
            capability_count: capabilities.len() as u32,
            capabilities: capabilities.as_ptr(),
        }
    }

    fn validate(p: &KnDspPlugin) -> Result<ValidPlugin, i32> {
        // SAFETY: `p` and everything it points to outlive the call.
        unsafe { validate_plugin(p, KN_DSP_ABI_VERSION) }
    }

    #[test]
    fn validate_accepts_a_well_formed_plugin() {
        let vtable = fake_vtable();
        let caps = [capability(&vtable)];
        let p = plugin(c"fake.one-2_x", c"Fake", &caps);
        let valid = validate(&p).expect("well-formed plugin");
        assert_eq!(valid.id.to_str(), Ok("fake.one-2_x"));
        assert_eq!(valid.name.to_str(), Ok("Fake"));
        assert_eq!(valid.resamplers.len(), 1);
    }

    #[test]
    fn validate_rejects_a_null_plugin() {
        // SAFETY: null is allowed.
        let result = unsafe { validate_plugin(std::ptr::null(), KN_DSP_ABI_VERSION) };
        assert_eq!(result.err(), Some(KN_ERR_FORMAT));
    }

    #[test]
    fn validate_rejects_a_wrong_version() {
        let vtable = fake_vtable();
        let caps = [capability(&vtable)];
        let mut p = plugin(c"fake", c"Fake", &caps);
        p.abi_version = 2;
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
    }

    #[test]
    fn validate_rejects_a_bad_id() {
        let vtable = fake_vtable();
        let caps = [capability(&vtable)];
        for id in [c"", c"Upper", c"has space", c"slash/x"] {
            let p = plugin(id, c"Fake", &caps);
            assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT), "{id:?}");
        }
        let long = CString::new("a".repeat(65)).unwrap();
        let p = plugin(&long, c"Fake", &caps);
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
        let longest = CString::new("a".repeat(64)).unwrap();
        let p = plugin(&longest, c"Fake", &caps);
        assert!(validate(&p).is_ok());
        // No terminator within the limit: the scan must stop inside the array.
        let unterminated = [b'a'; 65];
        let mut p = plugin(c"fake", c"Fake", &caps);
        p.id = unterminated.as_ptr().cast();
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
    }

    #[test]
    fn validate_rejects_an_overlong_name() {
        let vtable = fake_vtable();
        let caps = [capability(&vtable)];
        let long = CString::new("n".repeat(129)).unwrap();
        let p = plugin(c"fake", &long, &caps);
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
        let empty = plugin(c"fake", c"", &caps);
        assert_eq!(validate(&empty).err(), Some(KN_ERR_FORMAT));
        let invalid_utf8 = CString::new(vec![0xff, 0xfe]).unwrap();
        let p = plugin(c"fake", &invalid_utf8, &caps);
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
        let unterminated = [b'n'; 129];
        let mut p = plugin(c"fake", c"Fake", &caps);
        p.name = unterminated.as_ptr().cast();
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
    }

    #[test]
    fn validate_rejects_no_capabilities() {
        let p = plugin(c"fake", c"Fake", &[]);
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));

        let vtable = fake_vtable();
        let caps = vec![capability(&vtable)];
        let mut p = plugin(c"fake", c"Fake", &caps);
        p.capabilities = std::ptr::null();
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));

        let many: Vec<_> = (0..33).map(|_| capability(&vtable)).collect();
        let p = plugin(c"fake", c"Fake", &many);
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
    }

    #[test]
    fn validate_rejects_a_null_function_pointer() {
        let clear: [fn(&mut KnDspResamplerVtable); 6] = [
            |v| v.create = None,
            |v| v.process = None,
            |v| v.flush = None,
            |v| v.pending_frames = None,
            |v| v.reset = None,
            |v| v.destroy = None,
        ];
        for (member, clear) in clear.iter().enumerate() {
            let mut vtable = fake_vtable();
            clear(&mut vtable);
            let caps = [capability(&vtable)];
            let p = plugin(c"fake", c"Fake", &caps);
            assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT), "member {member}");
        }

        let null_vtable = [KnDspCapability {
            kind: KN_DSP_KIND_RESAMPLER,
            vtable: std::ptr::null(),
        }];
        let p = plugin(c"fake", c"Fake", &null_vtable);
        assert_eq!(validate(&p).err(), Some(KN_ERR_FORMAT));
    }

    #[test]
    fn validate_skips_unknown_kinds() {
        let vtable = fake_vtable();
        let caps = [
            // An unknown kind is skipped even with a null vtable.
            KnDspCapability {
                kind: 99,
                vtable: std::ptr::null(),
            },
            capability(&vtable),
        ];
        let p = plugin(c"fake", c"Fake", &caps);
        let valid = validate(&p).expect("the known record is enough");
        assert_eq!(valid.resamplers.len(), 1);
    }

    #[test]
    fn validate_reports_unsupported_with_only_unknown_kinds() {
        let caps = [
            KnDspCapability {
                kind: 99,
                vtable: std::ptr::null(),
            },
            KnDspCapability {
                kind: 7,
                vtable: std::ptr::null(),
            },
        ];
        let p = plugin(c"fake", c"Fake", &caps);
        assert_eq!(validate(&p).err(), Some(KN_ERR_UNSUPPORTED));
    }

    // ---- PluginResampler ----

    #[test]
    fn wrapper_passes_the_exact_capacity() {
        let probe = Arc::new(Probe::default());
        let mut r = fake_resampler(Mode::Honest, 2, &probe);
        let mut out = Vec::new();

        // ceil(100 * 48000 / 44100) = 109 frames, nothing emitted yet.
        r.process(&[0.0; 200], &mut out).unwrap();
        assert_eq!(probe.last_capacity.load(Ordering::SeqCst), 109);
        assert_eq!(out.len(), 108 * 2);
        assert_eq!(r.pending_frames(), 1);

        // ceil(150 * 48000 / 44100) - 108 = 56 frames.
        r.process(&[0.0; 100], &mut out).unwrap();
        assert_eq!(probe.last_capacity.load(Ordering::SeqCst), 56);
        assert_eq!(out.len(), 163 * 2);
        assert_eq!(r.pending_frames(), 1);

        r.flush(&mut out).unwrap();
        assert_eq!(out.len(), 164 * 2);
        assert_eq!(r.pending_frames(), 0);

        r.reset().unwrap();
        assert_eq!(r.pending_frames(), 0);
        r.process(&[0.0; 200], &mut out).unwrap();
        assert_eq!(probe.last_capacity.load(Ordering::SeqCst), 109);
    }

    #[test]
    fn wrapper_rejects_output_above_capacity() {
        let probe = Arc::new(Probe::default());
        let mut r = fake_resampler(Mode::OverCapacity, 1, &probe);
        let mut out = vec![0.5; 3];
        assert_eq!(
            r.process(&[0.0; 100], &mut out),
            Err(ResamplerError::Failed)
        );
        assert_eq!(out, [0.5; 3]);
    }

    #[test]
    fn wrapper_rejects_a_wrong_pending_count() {
        let probe = Arc::new(Probe::default());
        let mut r = fake_resampler(Mode::WrongPending, 1, &probe);
        let mut out = vec![0.5; 3];
        assert_eq!(
            r.process(&[0.0; 100], &mut out),
            Err(ResamplerError::Failed)
        );
        assert_eq!(out, [0.5; 3]);
    }

    #[test]
    fn wrapper_rejects_a_short_flush() {
        let probe = Arc::new(Probe::default());
        let mut r = fake_resampler(Mode::ShortFlush, 1, &probe);
        let mut out = Vec::new();
        r.process(&[0.0; 100], &mut out).unwrap();
        let before = out.len();
        assert_eq!(r.flush(&mut out), Err(ResamplerError::Failed));
        assert_eq!(out.len(), before);
    }

    #[test]
    fn wrapper_destroys_the_context_on_drop() {
        let probe = Arc::new(Probe::default());
        let r = fake_resampler(Mode::Honest, 1, &probe);
        assert_eq!(probe.destroyed.load(Ordering::SeqCst), 0);
        drop(r);
        assert_eq!(probe.destroyed.load(Ordering::SeqCst), 1);
    }

    // ---- PluginResamplerFactory ----

    #[test]
    fn factory_maps_unsupported_create() {
        let factory = |create: CreateFn| {
            PluginResamplerFactory::new(
                Arc::new(PluginLibrary::detached()),
                ResamplerFns {
                    create,
                    ..fake_fns()
                },
            )
        };
        let result = factory(unsupported_create).create(44100, 48000, 2);
        assert!(matches!(result, Err(ResamplerError::Unsupported)));
        let result = factory(failing_create).create(44100, 48000, 2);
        assert!(matches!(result, Err(ResamplerError::Failed)));
        // OK with no context is a broken plugin, not a context.
        let result = factory(null_ctx_create).create(44100, 48000, 2);
        assert!(matches!(result, Err(ResamplerError::Failed)));
        let result = factory(fake_create).create(44100, 48000, 2);
        let mut resampler = result.expect("the fake creates a context");
        let mut out = Vec::new();
        resampler.process(&[0.0; 200], &mut out).unwrap();
        assert_eq!(out.len(), 108 * 2);
    }
}
