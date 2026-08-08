//! Mirelia WASAPI output backend, shared mode.
//!
//! Streams open event-driven with AUTOCONVERTPCM, so the engine can always
//! run at the source format and let the OS mixer convert; bit-perfect
//! exclusive mode (`IAudioClient3`) lands in M5.
//!
//! The render loop body is the engine's callback; this crate only waits on
//! the WASAPI event, maps the device buffer, and reports queued frames.

#![cfg(windows)]

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread::JoinHandle;

use windows::Win32::Devices::FunctionDiscovery::PKEY_Device_FriendlyName;
use windows::Win32::Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0};
use windows::Win32::Media::Audio::{
    AUDCLNT_E_DEVICE_INVALIDATED, AUDCLNT_SHAREMODE_SHARED,
    AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM, AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
    AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, DEVICE_STATE_ACTIVE, IAudioClient,
    IAudioRenderClient, IMMDevice, IMMDeviceEnumerator, MMDeviceEnumerator, WAVEFORMATEX,
    WAVEFORMATEXTENSIBLE, WAVEFORMATEXTENSIBLE_0, eConsole, eRender,
};
use windows::Win32::Media::KernelStreaming::{
    SPEAKER_FRONT_CENTER, SPEAKER_FRONT_LEFT, SPEAKER_FRONT_RIGHT, WAVE_FORMAT_EXTENSIBLE,
};
use windows::Win32::Media::Multimedia::KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
use windows::Win32::System::Com::{
    CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree, STGM_READ,
};
use windows::Win32::System::Threading::{CreateEventW, SetEvent, WaitForSingleObject};
use windows::core::PCWSTR;

use mirelia_audio::output::{
    DeviceInfo, OutputBackend, OutputError, OutputStream, RenderCallback, RenderInfo, StreamFormat,
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
    if e.code() == AUDCLNT_E_DEVICE_INVALIDATED {
        OutputError::DeviceLost(format!("{context}: {e}"))
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

fn make_format(format: StreamFormat) -> WAVEFORMATEXTENSIBLE {
    let channels = u32::from(format.channels);
    let block_align = format.channels * 4;
    let mask = match format.channels {
        1 => SPEAKER_FRONT_CENTER,
        2 => SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT,
        n => (1u32 << n) - 1,
    };
    WAVEFORMATEXTENSIBLE {
        Format: WAVEFORMATEX {
            wFormatTag: WAVE_FORMAT_EXTENSIBLE as u16,
            nChannels: format.channels,
            nSamplesPerSec: format.sample_rate,
            nAvgBytesPerSec: format.sample_rate * channels * 4,
            nBlockAlign: block_align,
            wBitsPerSample: 32,
            cbSize: (size_of::<WAVEFORMATEXTENSIBLE>() - size_of::<WAVEFORMATEX>()) as u16,
        },
        Samples: WAVEFORMATEXTENSIBLE_0 {
            wValidBitsPerSample: 32,
        },
        dwChannelMask: mask,
        SubFormat: KSDATAFORMAT_SUBTYPE_IEEE_FLOAT,
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
                let setup = || -> Result<(IAudioClient, IAudioRenderClient, HANDLE, u32), OutputError> {
                    let enumerator = enumerator()?;
                    let device = device_for(&enumerator, owned_id.as_deref())?;
                    unsafe {
                        let client: IAudioClient = device
                            .Activate(CLSCTX_ALL, None)
                            .map_err(|e| internal("activate audio client", e))?;

                        let wf = make_format(format);
                        client
                            .Initialize(
                                AUDCLNT_SHAREMODE_SHARED,
                                AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                                    | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                                    | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                                0,
                                0,
                                &wf.Format,
                                None,
                            )
                            .map_err(|e| internal("Initialize (shared)", e))?;

                        let event = CreateEventW(None, false, false, None)
                            .map_err(|e| internal("CreateEvent", e))?;
                        client
                            .SetEventHandle(event)
                            .map_err(|e| internal("SetEventHandle", e))?;

                        let render: IAudioRenderClient = client
                            .GetService()
                            .map_err(|e| internal("GetService(IAudioRenderClient)", e))?;
                        let buffer_frames = client
                            .GetBufferSize()
                            .map_err(|e| internal("GetBufferSize", e))?;
                        Ok((client, render, event, buffer_frames))
                    }
                };

                match setup() {
                    Ok((client, render, event, buffer_frames)) => {
                        let _ = result_tx.send(Ok(event.0 as isize));
                        unsafe {
                            render_loop(
                                &client,
                                &render,
                                event,
                                buffer_frames,
                                channels,
                                &stop_render,
                                &mut callback,
                            );
                            let _ = CloseHandle(event);
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

/// The render loop. Everything inside the `while` runs with RT constraints;
/// the engine callback does the actual filling.
unsafe fn render_loop(
    client: &IAudioClient,
    render: &IAudioRenderClient,
    event: HANDLE,
    buffer_frames: u32,
    channels: usize,
    stop: &AtomicBool,
    callback: &mut RenderCallback,
) {
    unsafe {
        if client.Start().is_err() {
            return;
        }

        while !stop.load(Ordering::Acquire) {
            if WaitForSingleObject(event, 2000) != WAIT_OBJECT_0 {
                continue;
            }
            if stop.load(Ordering::Acquire) {
                break;
            }

            let Ok(padding) = client.GetCurrentPadding() else {
                break; // device invalidated
            };
            let avail = buffer_frames - padding;
            if avail == 0 {
                continue;
            }

            let Ok(ptr) = render.GetBuffer(avail) else {
                break;
            };
            let buf =
                std::slice::from_raw_parts_mut(ptr as *mut f32, avail as usize * channels);
            callback(
                buf,
                &RenderInfo {
                    device_queued_frames: padding,
                },
            );
            if render.ReleaseBuffer(avail, 0).is_err() {
                break;
            }
        }

        let _ = client.Stop();
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
