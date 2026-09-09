//! f32 → device-format sample conversion for exclusive mode.
//!
//! Everything here runs on the render thread: no allocation, no branching
//! beyond the format dispatch, TPDF dither optional at the target LSB.

/// Negotiated device sample format (exclusive mode).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DeviceFormat {
    /// 32-bit float; passthrough, never dithered.
    F32,
    /// 32-bit integer container (32 or 24 valid bits).
    S32,
    /// Packed 24-bit, 3 bytes little-endian.
    S24Packed,
    /// 16-bit integer.
    S16,
}

impl DeviceFormat {
    /// Container bytes per sample.
    pub fn bytes_per_sample(self) -> usize {
        match self {
            DeviceFormat::F32 | DeviceFormat::S32 => 4,
            DeviceFormat::S24Packed => 3,
            DeviceFormat::S16 => 2,
        }
    }
}

/// TPDF dither generator (two independent xorshift uniforms per sample).
pub struct Dither {
    enabled: bool,
    state: u32,
}

impl Dither {
    /// Creates the generator; when disabled, samples round undithered.
    pub fn new(enabled: bool) -> Self {
        Self {
            enabled,
            state: 0x9E37_79B9,
        }
    }

    #[inline]
    fn next_uniform(&mut self) -> f32 {
        // xorshift32; cheap, RT-safe, quality is ample for dither noise.
        let mut x = self.state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        self.state = x;
        // Map to [0, 1).
        (x >> 8) as f32 / 16_777_216.0
    }

    /// Triangular noise in (-1.0, 1.0) LSB units, or 0 when disabled.
    #[inline]
    fn next_tpdf(&mut self) -> f32 {
        if !self.enabled {
            return 0.0;
        }

        self.next_uniform() - self.next_uniform()
    }
}

/// Converts interleaved f32 into the device buffer. `dst` must hold
/// `src.len() * format.bytes_per_sample()` bytes.
///
/// # Safety
///
/// `dst` must be valid for writes of that many bytes.
pub unsafe fn write_samples(src: &[f32], dst: *mut u8, format: DeviceFormat, dither: &mut Dither) {
    unsafe {
        match format {
            DeviceFormat::F32 => {
                std::ptr::copy_nonoverlapping(src.as_ptr().cast::<u8>(), dst, src.len() * 4);
            }
            DeviceFormat::S32 => {
                let out = dst.cast::<i32>();
                for (i, &s) in src.iter().enumerate() {
                    let scaled = f64::from(s) * 2_147_483_648.0 + f64::from(dither.next_tpdf());
                    out.add(i).write_unaligned(
                        scaled.round().clamp(-2_147_483_648.0, 2_147_483_647.0) as i32,
                    );
                }
            }
            DeviceFormat::S24Packed => {
                for (i, &s) in src.iter().enumerate() {
                    let scaled = f64::from(s) * 8_388_608.0 + f64::from(dither.next_tpdf());
                    let v = scaled.round().clamp(-8_388_608.0, 8_388_607.0) as i32;
                    let bytes = v.to_le_bytes();
                    let base = dst.add(i * 3);
                    base.write(bytes[0]);
                    base.add(1).write(bytes[1]);
                    base.add(2).write(bytes[2]);
                }
            }
            DeviceFormat::S16 => {
                let out = dst.cast::<i16>();
                for (i, &s) in src.iter().enumerate() {
                    let scaled = f64::from(s) * 32_768.0 + f64::from(dither.next_tpdf());
                    out.add(i)
                        .write_unaligned(scaled.round().clamp(-32_768.0, 32_767.0) as i16);
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn convert(src: &[f32], format: DeviceFormat, dither_on: bool) -> Vec<u8> {
        let mut out = vec![0u8; src.len() * format.bytes_per_sample()];
        let mut dither = Dither::new(dither_on);
        unsafe { write_samples(src, out.as_mut_ptr(), format, &mut dither) };
        out
    }

    #[test]
    fn s16_scales_and_clamps() {
        let out = convert(&[0.0, 0.5, 1.0, -1.0], DeviceFormat::S16, false);
        let v: Vec<i16> = out
            .as_chunks::<2>()
            .0
            .iter()
            .map(|c| i16::from_le_bytes(*c))
            .collect();
        assert_eq!(v, [0, 16_384, 32_767, -32_768]);
    }

    #[test]
    fn s24_packs_three_bytes_little_endian() {
        let out = convert(&[0.5, -0.5], DeviceFormat::S24Packed, false);
        assert_eq!(out.len(), 6);
        let a = i32::from_le_bytes([out[0], out[1], out[2], if out[2] & 0x80 != 0 { 0xFF } else { 0 }]);
        let b = i32::from_le_bytes([out[3], out[4], out[5], if out[5] & 0x80 != 0 { 0xFF } else { 0 }]);
        assert_eq!(a, 4_194_304);
        assert_eq!(b, -4_194_304);
    }

    #[test]
    fn f32_is_bit_exact_passthrough() {
        let src = [0.123_456_79f32, -0.987_654_3];
        let out = convert(&src, DeviceFormat::F32, true);
        let round: Vec<f32> = out
            .as_chunks::<4>()
            .0
            .iter()
            .map(|c| f32::from_le_bytes(*c))
            .collect();
        assert_eq!(round, src);
    }

    #[test]
    fn dither_stays_within_one_lsb() {
        let src = vec![0.25f32; 4096];
        let out = convert(&src, DeviceFormat::S16, true);
        let expected = (0.25f64 * 32_768.0).round() as i16;
        for chunk in out.as_chunks::<2>().0 {
            let v = i16::from_le_bytes(*chunk);
            assert!((v - expected).abs() <= 1, "dithered value {v} vs {expected}");
        }
    }
}
