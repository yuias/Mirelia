//! Mirelia WASAPI output backend: shared and exclusive mode.
//!
//! Shared streams open event-driven with AUTOCONVERTPCM, so the engine can
//! always run at the source format and let the OS mixer convert. Exclusive
//! streams negotiate the device's best integer/float format at the source
//! rate (bit-perfect: no mixer, no SRC) and convert f32 → device format on
//! the render thread with optional TPDF dither.
//!
//! The render loop body is the engine's callback; this crate only waits on
//! the WASAPI event, maps the device buffer, and converts.

#![cfg(windows)]

mod convert;

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::JoinHandle;

use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows::Win32::Media::Audio::{
    AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED, AUDCLNT_E_DEVICE_IN_USE, AUDCLNT_E_DEVICE_INVALIDATED,
    AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED, AUDCLNT_E_UNSUPPORTED_FORMAT, AUDCLNT_SHAREMODE_EXCLUSIVE,
    AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, DEVICE_STATE_ACTIVE, IAudioClient,
    IAudioRenderClient, IMMDevice, IMMDeviceEnumerator, MMDeviceEnumerator, WAVEFORMATEX,
    WAVEFORMATEXTENSIBLE, WAVEFORMATEXTENSIBLE_0, eConsole, eRender,
};
use windows::Win32::Media::KernelStreaming::{
    KSDATAFORMAT_SUBTYPE_PCM, SPEAKER_FRONT_CENTER, SPEAKER_FRONT_LEFT, SPEAKER_FRONT_RIGHT,
    WAVE_FORMAT_EXTENSIBLE,
};
use windows::Win32::Media::Multimedia::KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
use windows::Win32::System::Com::{
    CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree, STGM_READ,
};
use windows::Win32::System::Threading::{CreateEventW, SetEvent, WaitForSingleObject};
use windows::core::PCWSTR;

use convert::{DeviceFormat, Dither};
use mirelia_audio::output::{
    DeviceInfo, OutputBackend, OutputError, OutputStream, RenderCallback, RenderInfo, ShareMode,
    StreamFormat, StreamOptions,
};

/// WASAPI render backend. Create one per engine.
pub struct WasapiBackend;

impl WasapiBackend {
    /// Creates the backend. COM is initialized per calling thread, lazily.
    #[allow(clippy::new_without_default)]
    pub fn new() -> Self {
        Self
    }
}

fn com_init() {
    // S_FALSE (already initialized) is fine; RPC_E_CHANGED_MODE means the
    // host initialized STA first, which MTA-safe MMDevice usage tolerates.
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    }
}

fn internal(context: &str, e: windows::core::Error) -> OutputError {
    let code = e.code();
    if code == AUDCLNT_E_DEVICE_INVALIDATED {
        OutputError::DeviceLost(format!("{context}: {e}"))
    } else if code == AUDCLNT_E_DEVICE_IN_USE || code == AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED {
        OutputError::DeviceBusy(format!("{context}: {e}"))
    } else if code == AUDCLNT_E_UNSUPPORTED_FORMAT {
        OutputError::Format(format!("{context}: {e}"))
    } else {
        OutputError::Internal(format!("{context}: {e}"))
    }
}

fn enumerator() -> Result<IMMDeviceEnumerator, OutputError> {
    com_init();
    unsafe { CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL) }
        .map_err(|e| internal("create device enumerator", e))
}

fn device_for(
    enumerator: &IMMDeviceEnumerator,
    device_id: Option<&str>,
) -> Result<IMMDevice, OutputError> {
    unsafe {
        match device_id {
            None => enumerator
                .GetDefaultAudioEndpoint(eRender, eConsole)
                .map_err(|e| OutputError::DeviceLost(format!("no default render device: {e}"))),
            Some(id) => {
                let wide: Vec<u16> = id.encode_utf16().chain(std::iter::once(0)).collect();
                enumerator
                    .GetDevice(PCWSTR(wide.as_ptr()))
                    .map_err(|e| OutputError::DeviceLost(format!("device '{id}': {e}")))
            }
        }
    }
}

fn device_id_string(device: &IMMDevice) -> Result<String, OutputError> {
    unsafe {
        let pw = device.GetId().map_err(|e| internal("GetId", e))?;
        let s = pw
            .to_string()
            .map_err(|e| OutputError::Internal(format!("device id encoding: {e}")))?;
        CoTaskMemFree(Some(pw.0 as _));
        Ok(s)
    }
}

fn device_name(device: &IMMDevice) -> String {
    unsafe {
        device
            .OpenPropertyStore(STGM_READ)
            .and_then(|store| store.GetValue(&PKEY_Device_FriendlyName))
            .map(|v| v.to_string())
            .unwrap_or_else(|_| "(unknown)".to_string())
    }
}

fn mix_format(device: &IMMDevice) -> Result<(u32, u16), OutputError> {
    unsafe {
        let client: IAudioClient = device
            .Activate(CLSCTX_ALL, None)
            .map_err(|e| internal("activate audio client", e))?;
        let wf = client
            .GetMixFormat()
            .map_err(|e| internal("GetMixFormat", e))?;
        let rate = (*wf).nSamplesPerSec;
        let channels = (*wf).nChannels;
        CoTaskMemFree(Some(wf as _));
        Ok((rate, channels))
    }
}

fn channel_mask(channels: u16) -> u32 {
    match channels {
        1 => SPEAKER_FRONT_CENTER,
        2 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT,
        n => (1u32 << n) - 1,
    }
}

/// Builds a WAVEFORMATEXTENSIBLE for one candidate device format.
fn make_wfx(format: StreamFormat, device_format: DeviceFormat, valid_bits: u16) -> WAVEFORMATEXTENSIBLE {
    let bytes = device_format.bytes_per_sample() as u16;
    let sub_format = match device_format {
        DeviceFormat::F32 => KSDATAFORMAT_SUBTYPE_IEEE_FLOAT,
        _ => KSDATAFORMAT_SUBTYPE_PCM,
    };
    WAVEFORMATEXTENSIBLE {
        Format: WAVEFORMATEX {
            wFormatTag: WAVE_FORMAT_EXTENSIBLE as u16,
            nChannels: format.channels,
            nSamplesPerSec: format.sample_rate,
            nAvgBytesPerSec: format.sample_rate * u32::from(format.channels) * u32::from(bytes),
            nBlockAlign: format.channels * bytes,
            wBitsPerSample: bytes * 8,
            cbSize: (size_of::<WAVEFORMATEXTENSIBLE>() - size_of::<WAVEFORMATEX>()) as u16,
        },
        Samples: WAVEFORMATEXTENSIBLE_0 {
            wValidBitsPerSample: valid_bits,
        },
        dwChannelMask: channel_mask(format.channels),
        SubFormat: sub_format,
    }
}

/// Finds the best exclusive-mode format at the source rate: float first,
/// then integer by descending depth (32, 24-in-32, 24 packed, 16).
fn negotiate_exclusive(
    client: &IAudioClient,
    format: StreamFormat,
) -> Result<(DeviceFormat, WAVEFORMATEXTENSIBLE), OutputError> {
    let candidates: [(DeviceFormat, u16); 5] = [
        (DeviceFormat::F32, 32),
        (DeviceFormat::S32, 32),
        (DeviceFormat::S32, 24),
        (DeviceFormat::S24Packed, 24),
        (DeviceFormat::S16, 16),
    ];
    for (device_format, valid_bits) in candidates {
        let wfx = make_wfx(format, device_format, valid_bits);
        let hr = unsafe {
            client.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, &wfx.Format, None)
        };
        if hr.is_ok() {
            return Ok((device_format, wfx));
        }
    }

    Err(OutputError::Format(format!(
        "device does not support {} Hz / {} ch in exclusive mode",
        format.sample_rate, format.channels
    )))
}

struct ExclusiveSetup {
    client: IAudioClient,
    render: IAudioRenderClient,
    event: HANDLE,
    buffer_frames: u32,
    device_format: DeviceFormat,
}

/// Exclusive event-driven initialization, including the documented
/// BUFFER_SIZE_NOT_ALIGNED dance (recompute the period from the aligned
/// buffer size and initialize a fresh client).
fn setup_exclusive(
    device: &IMMDevice,
    format: StreamFormat,
) -> Result<ExclusiveSetup, OutputError> {
    unsafe {
        let client: IAudioClient = device
            .Activate(CLSCTX_ALL, None)
            .map_err(|e| internal("activate audio client", e))?;
        let (device_format, wfx) = negotiate_exclusive(&client, format)?;

        let mut default_period = 0i64;
        let mut min_period = 0i64;
        client
            .GetDevicePeriod(Some(&mut default_period), Some(&mut min_period))
            .map_err(|e| internal("GetDevicePeriod", e))?;

        // Event-driven exclusive requires buffer duration == periodicity.
        let mut client = client;
        let mut period = default_period;
        for attempt in 0..2 {
            match client.Initialize(
                AUDCLNT_SHAREMODE_EXCLUSIVE,
                AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                period,
                period,
                &wfx.Format,
                None,
            ) {
                Ok(()) => break,
                Err(e) if e.code() == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED && attempt == 0 => {
                    let frames = client
                        .GetBufferSize()
                        .map_err(|e| internal("GetBufferSize (align)", e))?;
                    period = (f64::from(frames) / f64::from(format.sample_rate) * 10_000_000.0)
                        .round() as i64;
                    client = device
                        .Activate(CLSCTX_ALL, None)
                        .map_err(|e| internal("re-activate audio client", e))?;
                }
                Err(e) => return Err(internal("Initialize (exclusive)", e)),
            }
        }

        let event =
            CreateEventW(None, false, false, None).map_err(|e| internal("CreateEvent", e))?;
        client
            .SetEventHandle(event)
            .map_err(|e| internal("SetEventHandle", e))?;
        let render: IAudioRenderClient = client
            .GetService()
            .map_err(|e| internal("GetService(IAudioRenderClient)", e))?;
        let buffer_frames = client
            .GetBufferSize()
            .map_err(|e| internal("GetBufferSize", e))?;

        Ok(ExclusiveSetup {
            client,
            render,
            event,
            buffer_frames,
            device_format,
        })
    }
}

fn setup_shared(
    device: &IMMDevice,
    format: StreamFormat,
) -> Result<ExclusiveSetup, OutputError> {
    unsafe {
        let client: IAudioClient = device
            .Activate(CLSCTX_ALL, None)
            .map_err(|e| internal("activate audio client", e))?;
        let wfx = make_wfx(format, DeviceFormat::F32, 32);
        client
            .Initialize(
                AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                    | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                    | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                0,
                0,
                &wfx.Format,
                None,
            )
            .map_err(|e| internal("Initialize (shared)", e))?;

        let event =
            CreateEventW(None, false, false, None).map_err(|e| internal("CreateEvent", e))?;
        client
            .SetEventHandle(event)
            .map_err(|e| internal("SetEventHandle", e))?;
        let render: IAudioRenderClient = client
            .GetService()
            .map_err(|e| internal("GetService(IAudioRenderClient)", e))?;
        let buffer_frames = client
            .GetBufferSize()
            .map_err(|e| internal("GetBufferSize", e))?;

        Ok(ExclusiveSetup {
            client,
            render,
            event,
            buffer_frames,
            device_format: DeviceFormat::F32,
        })
    }
}

impl OutputBackend for WasapiBackend {
    fn enumerate(&mut self) -> Result<Vec<DeviceInfo>, OutputError> {
        let enumerator = enumerator()?;
        let default_id = device_for(&enumerator, None)
            .and_then(|d| device_id_string(&d))
            .unwrap_or_default();

        unsafe {
            let collection = enumerator
                .EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE)
                .map_err(|e| internal("EnumAudioEndpoints", e))?;
            let count = collection.GetCount().map_err(|e| internal("GetCount", e))?;

            let mut devices = Vec::with_capacity(count as usize);
            for i in 0..count {
                let device = collection.Item(i).map_err(|e| internal("Item", e))?;
                let id = device_id_string(&device)?;
                let (native_rate, native_channels) = mix_format(&device)?;
                devices.push(DeviceInfo {
                    is_default: id == default_id,
                    name: device_name(&device),
                    id,
                    native_rate,
                    native_channels,
                });
            }

            Ok(devices)
        }
    }

    fn probe_device(&mut self, device_id: Option<&str>) -> Result<DeviceInfo, OutputError> {
        let enumerator = enumerator()?;
        let device = device_for(&enumerator, device_id)?;
        let (native_rate, native_channels) = mix_format(&device)?;
        Ok(DeviceInfo {
            id: device_id_string(&device)?,
            name: device_name(&device),
            is_default: device_id.is_none(),
            native_rate,
            native_channels,
        })
    }

    fn open_stream(
        &mut self,
        device_id: Option<&str>,
        format: StreamFormat,
        options: StreamOptions,
        mut callback: RenderCallback,
    ) -> Result<Box<dyn OutputStream>, OutputError> {
        // COM interfaces are not Send in windows-rs, so the whole stream —
        // activation, initialization, and the render loop — lives on one
        // thread; setup success is reported back synchronously.
        let (result_tx, result_rx) = std::sync::mpsc::channel();
        let stop = Arc::new(AtomicBool::new(false));
        let stop_render = stop.clone();
        let owned_id = device_id.map(str::to_owned);
        let channels = usize::from(format.channels);

        let join = std::thread::Builder::new()
            .name("mirelia-render".into())
            .spawn(move || {
                com_init();
                let setup = enumerator()
                    .and_then(|e| device_for(&e, owned_id.as_deref()))
                    .and_then(|device| match options.mode {
                        ShareMode::Shared => setup_shared(&device, format),
                        ShareMode::Exclusive => setup_exclusive(&device, format),
                    });

                match setup {
                    Ok(s) => {
                        let _ = result_tx.send(Ok(s.event.0 as isize));
                        let mut scratch =
                            vec![0.0f32; s.buffer_frames as usize * channels];
                        let mut dither = Dither::new(
                            options.dither && s.device_format != DeviceFormat::F32,
                        );
                        unsafe {
                            render_loop(
                                &s,
                                options.mode,
                                channels,
                                &mut scratch,
                                &mut dither,
                                &stop_render,
                                &mut callback,
                            );
                            let _ = CloseHandle(s.event);
                        }
                    }
                    Err(e) => {
                        let _ = result_tx.send(Err(e));
                    }
                }
            })
            .map_err(|e| OutputError::Internal(format!("spawn render thread: {e}")))?;

        match result_rx.recv() {
            Ok(Ok(event_raw)) => Ok(Box::new(WasapiStream {
                format,
                stop,
                event_raw,
                join: Some(join),
            })),
            Ok(Err(e)) => {
                let _ = join.join();
                Err(e)
            }
            Err(_) => {
                let _ = join.join();
                Err(OutputError::Internal("render thread died during setup".into()))
            }
        }
    }
}

/// The render loop for both modes. Shared mode writes f32 directly into
/// the device buffer; exclusive fills a preallocated f32 scratch and
/// converts. Everything inside the `while` runs with RT constraints.
unsafe fn render_loop(
    s: &ExclusiveSetup,
    mode: ShareMode,
    channels: usize,
    scratch: &mut [f32],
    dither: &mut Dither,
    stop: &AtomicBool,
    callback: &mut RenderCallback,
) {
    unsafe {
        if s.client.Start().is_err() {
            return;
        }

        while !stop.load(Ordering::Acquire) {
            if WaitForSingleObject(s.event, 2000) != WAIT_OBJECT_0 {
                continue;
            }
            if stop.load(Ordering::Acquire) {
                break;
            }

            let (avail, queued) = match mode {
                ShareMode::Shared => {
                    let Ok(padding) = s.client.GetCurrentPadding() else {
                        break; // device invalidated
                    };
                    (s.buffer_frames - padding, padding)
                }
                // Event-driven exclusive: each event frees one full period.
                ShareMode::Exclusive => (s.buffer_frames, s.buffer_frames),
            };
            if avail == 0 {
                continue;
            }

            let Ok(ptr) = s.render.GetBuffer(avail) else {
                break;
            };
            let samples = avail as usize * channels;
            let info = RenderInfo {
                device_queued_frames: queued,
            };
            match (mode, s.device_format) {
                (ShareMode::Shared, _) | (_, DeviceFormat::F32) => {
                    let buf = std::slice::from_raw_parts_mut(ptr.cast::<f32>(), samples);
                    callback(buf, &info);
                }
                _ => {
                    let buf = &mut scratch[..samples];
                    callback(buf, &info);
                    convert::write_samples(buf, ptr, s.device_format, dither);
                }
            }

            if s.render.ReleaseBuffer(avail, 0).is_err() {
                break;
            }
        }

        let _ = s.client.Stop();
    }
}

struct WasapiStream {
    format: StreamFormat,
    stop: Arc<AtomicBool>,
    event_raw: isize,
    join: Option<JoinHandle<()>>,
}

impl OutputStream for WasapiStream {
    fn format(&self) -> StreamFormat {
        self.format
    }
}

impl Drop for WasapiStream {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        // Wake the render loop so it observes the stop flag promptly.
        unsafe {
            let _ = SetEvent(HANDLE(self.event_raw as _));
        }
        if let Some(join) = self.join.take() {
            let _ = join.join();
        }
    }
}
