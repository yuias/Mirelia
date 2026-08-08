//! Mirelia decoder layer: symphonia glue.
//!
//! Decoders always yield interleaved f32 (the plugin ABI makes the same
//! choice, see `audio_decoder.h`). Seeks are sample-accurate: symphonia
//! seeks to the nearest packet boundary at or before the target and this
//! layer discards the remainder.

use std::fmt;
use std::fs::File;
use std::path::Path;

use symphonia::core::audio::SampleBuffer;
use symphonia::core::codecs::{self, DecoderOptions};
use symphonia::core::errors::Error as SymphoniaError;
use symphonia::core::formats::{FormatOptions, FormatReader, SeekMode, SeekTo};
use symphonia::core::io::MediaSourceStream;
use symphonia::core::meta::MetadataOptions;
use symphonia::core::probe::Hint;

/// Decoder failure classification, mirroring the ABI error split.
#[derive(Debug)]
pub enum DecodeError {
    /// File system level failure.
    Io(std::io::Error),
    /// The container or codec is not in the supported set.
    Unsupported(String),
    /// The stream is recognized but cannot be decoded.
    Malformed(String),
}

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DecodeError::Io(e) => write!(f, "I/O error: {e}"),
            DecodeError::Unsupported(m) => write!(f, "unsupported format: {m}"),
            DecodeError::Malformed(m) => write!(f, "decode error: {m}"),
        }
    }
}

impl std::error::Error for DecodeError {}

impl From<SymphoniaError> for DecodeError {
    fn from(e: SymphoniaError) -> Self {
        match e {
            SymphoniaError::IoError(io) => DecodeError::Io(io),
            SymphoniaError::Unsupported(m) => DecodeError::Unsupported(m.to_string()),
            other => DecodeError::Malformed(other.to_string()),
        }
    }
}

/// Static properties of an opened stream.
#[derive(Debug, Clone)]
pub struct StreamInfo {
    /// Sample rate in Hz.
    pub sample_rate: u32,
    /// Channel count.
    pub channels: u16,
    /// Source bit depth, when the codec reports one (lossless formats).
    pub bit_depth: Option<u16>,
    /// Total frames after gapless trimming, when known.
    pub total_frames: Option<u64>,
    /// Canonical codec id: mp3, aac, alac, flac, pcm.
    pub codec: &'static str,
    /// Average bitrate estimated from file size and duration.
    pub bitrate_kbps: Option<u32>,
}

/// A decoding session over one audio file.
pub struct Decoder {
    format: Box<dyn FormatReader>,
    decoder: Box<dyn codecs::Decoder>,
    track_id: u32,
    info: StreamInfo,
    sample_buf: Option<SampleBuffer<f32>>,
    /// True when sample_buf holds a not-yet-delivered block from priming.
    primed: bool,
    /// Next frame index to be handed out.
    position: u64,
    /// Frames to drop after a coarse seek, for sample accuracy.
    pending_skip: u64,
}

impl Decoder {
    /// Opens a file and prepares the default audio track for decoding.
    /// Gapless trimming (encoder delay/padding) is enabled where the
    /// container carries the information (LAME/iTunSMPB).
    pub fn open(path: &Path) -> Result<Self, DecodeError> {
        let file = File::open(path).map_err(DecodeError::Io)?;
        let file_len = file.metadata().map(|m| m.len()).unwrap_or(0);
        let mss = MediaSourceStream::new(Box::new(file), Default::default());

        let mut hint = Hint::new();
        if let Some(ext) = path.extension().and_then(|e| e.to_str()) {
            hint.with_extension(ext);
        }

        let probed = symphonia::default::get_probe()
            .format(
                &hint,
                mss,
                &FormatOptions {
                    enable_gapless: true,
                    ..Default::default()
                },
                &MetadataOptions::default(),
            )
            .map_err(DecodeError::from)?;
        let format = probed.format;

        let track = format
            .tracks()
            .iter()
            .find(|t| t.codec_params.codec != codecs::CODEC_TYPE_NULL)
            .ok_or_else(|| DecodeError::Unsupported("no decodable audio track".into()))?;
        let track_id = track.id;
        let params = track.codec_params.clone();
        let mut format = format;

        let mut decoder = symphonia::default::get_codecs()
            .make(&params, &DecoderOptions::default())
            .map_err(DecodeError::from)?;

        // Some codecs (notably AAC) declare the signal spec only in the
        // bitstream, not the container. Decode ahead until the first frame
        // to learn it; the primed block is handed out by next_block later.
        let mut primed_buf: Option<SampleBuffer<f32>> = None;
        let (sample_rate, channels) = match (params.sample_rate, params.channels) {
            (Some(r), Some(c)) => (r, c.count() as u16),
            _ => loop {
                let packet = match format.next_packet() {
                    Ok(p) => p,
                    Err(e) => {
                        return Err(DecodeError::Malformed(format!(
                            "stream parameters undetermined before EOF: {e}"
                        )));
                    }
                };
                if packet.track_id() != track_id {
                    continue;
                }

                match decoder.decode(&packet) {
                    Ok(d) if d.frames() > 0 => {
                        let spec = *d.spec();
                        let mut buf = SampleBuffer::new(d.capacity() as u64, spec);
                        buf.copy_interleaved_ref(d);
                        primed_buf = Some(buf);
                        break (spec.rate, spec.channels.count() as u16);
                    }
                    Ok(_) | Err(SymphoniaError::DecodeError(_)) => continue,
                    Err(e) => return Err(e.into()),
                }
            },
        };
        let total_frames = params.n_frames;
        let bitrate_kbps = total_frames
            .filter(|&frames| frames > 0 && file_len > 0)
            .map(|frames| {
                let seconds = frames as f64 / f64::from(sample_rate);
                (file_len as f64 * 8.0 / seconds / 1000.0).round() as u32
            });

        let info = StreamInfo {
            sample_rate,
            channels,
            bit_depth: params.bits_per_sample.map(|b| b as u16),
            total_frames,
            codec: codec_name(params.codec)?,
            bitrate_kbps,
        };

        let primed = primed_buf.is_some();
        Ok(Self {
            format,
            decoder,
            track_id,
            info,
            sample_buf: primed_buf,
            primed,
            position: 0,
            pending_skip: 0,
        })
    }

    /// Stream properties.
    pub fn info(&self) -> &StreamInfo {
        &self.info
    }

    /// The frame index the next `next_block` call will start at.
    pub fn position(&self) -> u64 {
        self.position
    }

    /// Decodes the next packet and returns its interleaved f32 samples, or
    /// `None` at end of stream. Corrupt packets are skipped.
    pub fn next_block(&mut self) -> Result<Option<&[f32]>, DecodeError> {
        if self.primed {
            self.primed = false;
            let channels = usize::from(self.info.channels);
            let samples = self.sample_buf.as_ref().unwrap().samples();
            self.position += (samples.len() / channels) as u64;
            return Ok(Some(samples));
        }

        loop {
            let packet = match self.format.next_packet() {
                Ok(p) => p,
                Err(SymphoniaError::IoError(e))
                    if e.kind() == std::io::ErrorKind::UnexpectedEof =>
                {
                    return Ok(None);
                }
                Err(SymphoniaError::ResetRequired) => return Ok(None),
                Err(e) => return Err(e.into()),
            };
            if packet.track_id() != self.track_id {
                continue;
            }

            let decoded = match self.decoder.decode(&packet) {
                Ok(d) => d,
                // A corrupt packet is not fatal; resynchronize on the next one.
                Err(SymphoniaError::DecodeError(_)) => continue,
                Err(e) => return Err(e.into()),
            };
            if decoded.frames() == 0 {
                continue;
            }

            let spec = *decoded.spec();
            let buf = self
                .sample_buf
                .get_or_insert_with(|| SampleBuffer::new(decoded.capacity() as u64, spec));
            buf.copy_interleaved_ref(decoded);

            let channels = usize::from(self.info.channels);
            let frames = buf.samples().len() / channels;
            if self.pending_skip >= frames as u64 {
                self.pending_skip -= frames as u64;
                continue;
            }

            let skip_samples = (self.pending_skip as usize) * channels;
            self.pending_skip = 0;
            let samples = &self.sample_buf.as_ref().unwrap().samples()[skip_samples..];
            self.position += (samples.len() / channels) as u64;
            return Ok(Some(samples));
        }
    }

    /// Seeks so the next `next_block` starts exactly at `frame`.
    pub fn seek(&mut self, frame: u64) -> Result<(), DecodeError> {
        let seeked = self.format.seek(
            SeekMode::Accurate,
            SeekTo::TimeStamp {
                ts: frame,
                track_id: self.track_id,
            },
        )?;
        self.decoder.reset();
        self.primed = false;
        // Accurate mode lands at or before the target; trim the difference.
        self.pending_skip = frame.saturating_sub(seeked.actual_ts);
        self.position = frame;
        Ok(())
    }
}

fn codec_name(codec: codecs::CodecType) -> Result<&'static str, DecodeError> {
    match codec {
        codecs::CODEC_TYPE_MP3 => Ok("mp3"),
        codecs::CODEC_TYPE_AAC => Ok("aac"),
        codecs::CODEC_TYPE_ALAC => Ok("alac"),
        codecs::CODEC_TYPE_FLAC => Ok("flac"),
        c if c == codecs::CODEC_TYPE_PCM_S16LE
            || c == codecs::CODEC_TYPE_PCM_S24LE
            || c == codecs::CODEC_TYPE_PCM_S32LE
            || c == codecs::CODEC_TYPE_PCM_F32LE
            || c == codecs::CODEC_TYPE_PCM_U8 =>
        {
            Ok("pcm")
        }
        other => Err(DecodeError::Unsupported(format!("codec {other:?}"))),
    }
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

    fn decode_all(decoder: &mut Decoder) -> u64 {
        let channels = u64::from(decoder.info().channels);
        let mut samples: u64 = 0;
        while let Some(block) = decoder.next_block().unwrap() {
            samples += block.len() as u64;
        }
        samples / channels
    }

    #[test]
    fn opens_all_fixture_formats() {
        for (file, codec) in [
            ("fixture.mp3", "mp3"),
            ("fixture.flac", "flac"),
            ("fixture.wav", "pcm"),
            ("fixture-aac.m4a", "aac"),
            ("fixture-alac.m4a", "alac"),
        ] {
            let d = Decoder::open(&fixture(file)).unwrap();
            assert_eq!(d.info().codec, codec, "{file}");
            assert_eq!(d.info().sample_rate, 44_100, "{file}");
            assert_eq!(d.info().channels, 2, "{file}");
        }
    }

    #[test]
    fn decoded_length_matches_duration() {
        // Fixtures are 0.4 s at 44.1 kHz = 17640 frames. Lossy codecs may
        // pad slightly; lossless must be near-exact.
        for (file, tolerance) in [
            ("fixture.wav", 0u64),
            ("fixture.flac", 0),
            ("fixture-alac.m4a", 64),
            ("fixture.mp3", 1152 * 2),
            ("fixture-aac.m4a", 1024 * 2),
        ] {
            let mut d = Decoder::open(&fixture(file)).unwrap();
            let frames = decode_all(&mut d);
            let expected = 17_640u64;
            assert!(
                frames.abs_diff(expected) <= tolerance,
                "{file}: {frames} frames, expected {expected} ±{tolerance}"
            );
        }
    }

    #[test]
    fn seek_is_sample_accurate() {
        let mut d = Decoder::open(&fixture("fixture.flac")).unwrap();
        d.seek(10_000).unwrap();
        assert_eq!(d.position(), 10_000);
        let remaining = decode_all(&mut d);
        assert_eq!(remaining, 17_640 - 10_000);
    }

    #[test]
    fn missing_file_is_io_error() {
        assert!(matches!(
            Decoder::open(&fixture("nope.flac")),
            Err(DecodeError::Io(_))
        ));
    }

    fn decode_samples(name: &str) -> Vec<f32> {
        let mut d = Decoder::open(&fixture(name)).unwrap();
        let mut all = Vec::new();
        while let Some(block) = d.next_block().unwrap() {
            all.extend_from_slice(block);
        }
        all
    }

    #[test]
    fn mp3_lame_gapless_trim_is_exact() {
        // gapless-a.mp3 is exactly 1 s (44100 frames, mono) encoded with
        // LAME; the delay/padding trim must restore the exact length.
        let mut d = Decoder::open(&fixture("gapless-a.mp3")).unwrap();
        assert_eq!(decode_all(&mut d), 44_100);
    }

    #[test]
    fn flac_splice_reproduces_the_master_bit_exactly() {
        // The master cosine was split losslessly; decoding the halves and
        // splicing must reproduce the master decode sample for sample.
        let master = decode_samples("gapless-master.wav");
        let mut spliced = decode_samples("gapless-a.flac");
        spliced.extend(decode_samples("gapless-b.flac"));
        assert_eq!(master.len(), spliced.len());
        assert_eq!(master, spliced);
    }

    #[test]
    fn seek_is_accurate_on_vbr_mp3_without_xing_header() {
        let mut full = Decoder::open(&fixture("vbr-noxing.mp3")).unwrap();
        let total = decode_all(&mut full);
        assert!(total > 44_100, "fixture shorter than expected: {total}");

        let mut seeked = Decoder::open(&fixture("vbr-noxing.mp3")).unwrap();
        seeked.seek(10_000).unwrap();
        assert_eq!(decode_all(&mut seeked), total - 10_000);
    }
}
