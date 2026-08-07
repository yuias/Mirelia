//! Mirelia CoreAudio output backend.
//!
//! M0 status: placeholder. Default-device output lands in M2, hog mode and
//! sample-rate follow in M5. macOS-only code is gated so the workspace
//! builds everywhere.

#![cfg(target_os = "macos")]
