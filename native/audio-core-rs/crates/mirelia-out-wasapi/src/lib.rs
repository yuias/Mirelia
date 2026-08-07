//! Mirelia WASAPI output backend.
//!
//! M0 status: placeholder. Shared-mode output lands in M2, exclusive mode
//! (`IAudioClient3`, event-driven) in M5. Windows-only code is gated so the
//! workspace builds everywhere.

#![cfg(windows)]
