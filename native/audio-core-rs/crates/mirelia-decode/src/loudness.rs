//! EBU R128 loudness scanning for ReplayGain 2.
//!
//! RG2 defines track gain as the difference between the -18 LUFS reference
//! and the program's integrated loudness; peak is the true peak so clipping
//! prevention can account for inter-sample overs.

use std::path::Path;

use ebur128::{EbuR128, Mode};

use crate::{DecodeError, Decoder};

/// ReplayGain 2 reference level in LUFS.
const REFERENCE_LUFS: f64 = -18.0;

/// Scan result: RG2 gain and true peak.
#[derive(Debug, Clone, Copy)]
pub struct LoudnessResult {
    /// Gain to apply, in dB (positive = track is quieter than reference).
    pub gain_db: f32,
    /// True peak as linear amplitude (1.0 = full scale).
    pub peak: f32,
}

/// Decodes the whole file and computes integrated loudness + true peak.
/// Blocking; call from a worker thread.
pub fn scan(path: &Path) -> Result<LoudnessResult, DecodeError> {
    let mut decoder = Decoder::open(path)?;
    let channels = u32::from(decoder.info().channels);
    let rate = decoder.info().sample_rate;

    let mut meter = EbuR128::new(channels, rate, Mode::I | Mode::TRUE_PEAK)
        .map_err(|e| DecodeError::Malformed(format!("loudness meter init: {e}")))?;

    while let Some(block) = decoder.next_block()? {
        meter
            .add_frames_f32(block)
            .map_err(|e| DecodeError::Malformed(format!("loudness feed: {e}")))?;
    }

    let loudness = meter
        .loudness_global()
        .map_err(|e| DecodeError::Malformed(format!("integrated loudness: {e}")))?;
    let mut peak = 0f64;
    for ch in 0..channels {
        peak = peak.max(
            meter
                .true_peak(ch)
                .map_err(|e| DecodeError::Malformed(format!("true peak: {e}")))?,
        );
    }

    // Silent or too-short programs report -inf; a zero gain is the sane fallback.
    let gain_db = if loudness.is_finite() {
        (REFERENCE_LUFS - loudness) as f32
    } else {
        0.0
    };

    Ok(LoudnessResult {
        gain_db,
        peak: peak as f32,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::path::PathBuf;

    fn fixture(name: &str) -> PathBuf {
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("../../../../tests/fixtures")
            .join(name)
    }

    #[test]
    fn cosine_fixture_scans_to_plausible_values() {
        // 0.8 full-scale 440 Hz tone: loudness lands near -6.6 LUFS
        // (K-weighting adds ~+0.6 dB at 440 Hz), so RG2 gain is strongly
        // negative; true peak is ~0.8.
        let result = scan(&fixture("gapless-master.wav")).unwrap();
        assert!(
            (-13.0..=-9.0).contains(&result.gain_db),
            "gain {} dB",
            result.gain_db
        );
        // Sample peak is 0.8, but the fixture starts abruptly at the cosine
        // maximum; the band-limited reconstruction overshoots that onset, so
        // the true peak legitimately reads higher (~0.9 measured).
        assert!((0.78..=0.95).contains(&result.peak), "peak {}", result.peak);
    }

    #[test]
    fn scan_is_consistent_across_lossless_encodes_of_the_same_audio() {
        let wav = scan(&fixture("gapless-master.wav")).unwrap();
        let flac_a = scan(&fixture("gapless-a.flac")).unwrap();
        // Same signal, so the half must measure like the master.
        assert!(
            (wav.gain_db - flac_a.gain_db).abs() < 0.2,
            "{wav:?} vs {flac_a:?}"
        );
        assert!((wav.peak - flac_a.peak).abs() < 0.01);
    }
}
