// Copyright (c) 2026 ESSVB. All Rights Reserved.
// Zero-dependency native Rust image-processing & change-detection hot-path kernels for ESSVB-sub.

use std::slice;

const SIGNATURE_WIDTH: usize = 32;
const SIGNATURE_HEIGHT: usize = 12;

#[no_mangle]
pub extern "C" fn essvb_fast_version() -> u32 {
    2
}

/// Normalizes dim HDR/dark frames so the top 0.5% luminance reaches near-white.
/// Returns 1 if `dst` (tightly packed `width * 4` stride) was populated, 0 if no scaling is needed, -1 on error.
#[no_mangle]
pub unsafe extern "C" fn essvb_auto_level(
    src: *const u8,
    dst: *mut u8,
    width: i32,
    height: i32,
    stride: i32,
) -> i32 {
    if src.is_null() || dst.is_null() || width <= 0 || height <= 0 || stride < width * 4 {
        return -1;
    }

    let w = width as usize;
    let h = height as usize;
    let s = stride as usize;
    let src_bytes = slice::from_raw_parts(src, (h - 1) * s + w * 4);
    let dst_bytes = slice::from_raw_parts_mut(dst, h * w * 4);

    let mut histogram = [0i32; 256];
    for y in 0..h {
        let row = &src_bytes[y * s..y * s + w * 4];
        for px in row.chunks_exact(4) {
            let luma = ((px[2] as i32 * 3) + (px[1] as i32 * 6) + (px[0] as i32)) / 10;
            let idx = luma.clamp(0, 255) as usize;
            histogram[idx] += 1;
        }
    }

    let limit = (w as f64) * (h as f64) * 0.005;
    let mut top: i32 = 255;
    let mut count: i32 = 0;
    while top > 0 {
        count += histogram[top as usize];
        if (count as f64) >= limit {
            break;
        }
        top -= 1;
    }

    if top >= 235 || top < 60 {
        return 0;
    }

    let scale = 250.0 / (top as f64);
    let mut lut = [0u8; 256];
    for (v, out) in lut.iter_mut().enumerate() {
        let mapped = ((v as f64) * scale) as i32;
        *out = mapped.min(255) as u8;
    }

    let dst_stride = w * 4;
    for y in 0..h {
        let s_row = &src_bytes[y * s..y * s + w * 4];
        let d_row = &mut dst_bytes[y * dst_stride..(y + 1) * dst_stride];
        for (sp, dp) in s_row.chunks_exact(4).zip(d_row.chunks_exact_mut(4)) {
            dp[0] = lut[sp[0] as usize];
            dp[1] = lut[sp[1] as usize];
            dp[2] = lut[sp[2] as usize];
            dp[3] = 255;
        }
    }

    1
}

/// Integral-image outlined text extraction (ShadowText & WhiteTextOnly) into crisp black text on white canvas.
#[no_mangle]
pub unsafe extern "C" fn essvb_extract_outlined_text(
    src: *const u8,
    dst: *mut u8,
    width: i32,
    height: i32,
    stride: i32,
    left_pad: i32,
    br_pad: i32,
    min_bright: i32,
    max_spread: i32,
    dark_luma: i32,
    include_colored: i32,
    directional_shadow: i32,
) -> i32 {
    if src.is_null() || dst.is_null() || width <= 0 || height <= 0 || stride < width * 4 {
        return -1;
    }

    let w = width as usize;
    let h = height as usize;
    let s = stride as usize;
    let src_bytes = slice::from_raw_parts(src, (h - 1) * s + w * 4);
    let dst_bytes = slice::from_raw_parts_mut(dst, h * w * 4);

    let mut bright = vec![0u8; w * h];
    let iw = w + 1;
    let mut dark_integral = vec![0i32; iw * (h + 1)];
    let allow_color = include_colored != 0;
    let directional = directional_shadow != 0;

    for y in 0..h {
        let mut row_sum: i32 = 0;
        let s_row = &src_bytes[y * s..y * s + w * 4];
        let b_row = &mut bright[y * w..(y + 1) * w];
        let prev_irow = y * iw;
        let curr_irow = (y + 1) * iw;

        for (x, px) in s_row.chunks_exact(4).enumerate() {
            let b = px[0] as i32;
            let g = px[1] as i32;
            let r = px[2] as i32;
            let min_c = r.min(g).min(b);
            let max_c = r.max(g).max(b);
            let luma = ((r * 3) + (g * 6) + b) / 10;

            let is_bright_neutral = min_c >= min_bright && (max_c - min_c) <= max_spread;
            let is_bright_colored = allow_color && max_c >= 170 && (max_c - min_c) >= 60 && luma >= 120;
            b_row[x] = u8::from(is_bright_neutral || is_bright_colored);

            if luma < dark_luma {
                row_sum += 1;
            }
            dark_integral[curr_irow + x + 1] = dark_integral[prev_irow + x + 1] + row_sum;
        }
    }

    let lp = left_pad.max(0) as usize;
    let brp = br_pad.max(0) as usize;
    let dst_stride = w * 4;

    for y in 0..h {
        let top = if directional { y } else { y.saturating_sub(lp) };
        let bottom = (y + brp + 1).min(h);
        let top_off = top * iw;
        let bot_off = bottom * iw;
        let b_row = &bright[y * w..(y + 1) * w];
        let d_row = &mut dst_bytes[y * dst_stride..(y + 1) * dst_stride];

        for (x, (&is_br, dp)) in b_row.iter().zip(d_row.chunks_exact_mut(4)).enumerate() {
            let mut is_text = false;
            if is_br != 0 {
                let left = if directional { x } else { x.saturating_sub(lp) };
                let right = (x + brp + 1).min(w);
                let dark_count = dark_integral[bot_off + right]
                    - dark_integral[top_off + right]
                    - dark_integral[bot_off + left]
                    + dark_integral[top_off + left];
                is_text = dark_count > 0;
            }
            let pix = if is_text { 0u8 } else { 255u8 };
            dp[0] = pix;
            dp[1] = pix;
            dp[2] = pix;
            dp[3] = 255;
        }
    }

    1
}

/// Computes compact 32x12 grayscale perceptual change signature.
#[no_mangle]
pub unsafe extern "C" fn essvb_compute_signature(
    src: *const u8,
    width: i32,
    height: i32,
    stride: i32,
    out_sig: *mut u8,
) -> i32 {
    if src.is_null() || out_sig.is_null() || width <= 0 || height <= 0 || stride < width * 4 {
        return -1;
    }

    let w = width as usize;
    let h = height as usize;
    let s = stride as usize;
    let src_bytes = slice::from_raw_parts(src, (h - 1) * s + w * 4);
    let sig = slice::from_raw_parts_mut(out_sig, SIGNATURE_WIDTH * SIGNATURE_HEIGHT);

    for y in 0..SIGNATURE_HEIGHT {
        let src_y = (y * h) / SIGNATURE_HEIGHT;
        let row = &src_bytes[src_y * s..src_y * s + w * 4];
        for x in 0..SIGNATURE_WIDTH {
            let src_x = (x * w) / SIGNATURE_WIDTH;
            let p = src_x * 4;
            let val = ((row[p] as u32) + ((row[p + 1] as u32) * 2) + (row[p + 2] as u32)) / 4;
            sig[y * SIGNATURE_WIDTH + x] = val as u8;
        }
    }

    1
}

/// Mean absolute difference ratio between two 32x12 signatures.
#[no_mangle]
pub unsafe extern "C" fn essvb_difference_ratio(
    a: *const u8,
    b: *const u8,
    len: i32,
    x0: i32,
    x1: i32,
    y0: i32,
    y1: i32,
) -> f64 {
    if a.is_null() || b.is_null() || len <= 0 {
        return 1.0;
    }

    let n = len as usize;
    let sa = slice::from_raw_parts(a, n);
    let sb = slice::from_raw_parts(b, n);

    let sx0 = (x0.max(0) as usize).min(SIGNATURE_WIDTH);
    let sx1 = (x1.max(0) as usize).min(SIGNATURE_WIDTH);
    let sy0 = (y0.max(0) as usize).min(SIGNATURE_HEIGHT);
    let sy1 = (y1.max(0) as usize).min(SIGNATURE_HEIGHT);

    let mut diff_sum: u64 = 0;
    let mut count: u64 = 0;
    for y in sy0..sy1 {
        let row_off = y * SIGNATURE_WIDTH;
        for x in sx0..sx1 {
            let idx = row_off + x;
            if idx < n {
                diff_sum += sa[idx].abs_diff(sb[idx]) as u64;
                count += 1;
            }
        }
    }

    if count == 0 {
        1.0
    } else {
        (diff_sum as f64) / ((count as f64) * 255.0)
    }
}

/// Fast 2x bilinear upscale matching Frame.Resized(w*2, h*2).
#[no_mangle]
pub unsafe extern "C" fn essvb_upscale_2x(
    src: *const u8,
    dst: *mut u8,
    width: i32,
    height: i32,
    stride: i32,
) -> i32 {
    if src.is_null() || dst.is_null() || width <= 0 || height <= 0 || stride < width * 4 {
        return -1;
    }

    let w = width as usize;
    let h = height as usize;
    let s = stride as usize;
    let tw = w * 2;
    let th = h * 2;
    let src_bytes = slice::from_raw_parts(src, (h - 1) * s + w * 4);
    let dst_bytes = slice::from_raw_parts_mut(dst, th * tw * 4);

    let max_x = (w - 1) as f64;
    let max_y = (h - 1) as f64;
    let dst_stride = tw * 4;

    for ty in 0..th {
        let sy = (((ty as f64) + 0.5) * 0.5 - 0.5).clamp(0.0, max_y);
        let y0 = sy as usize;
        let y1 = (y0 + 1).min(h - 1);
        let fy = sy - (y0 as f64);

        let r0 = &src_bytes[y0 * s..y0 * s + w * 4];
        let r1 = &src_bytes[y1 * s..y1 * s + w * 4];
        let d_row = &mut dst_bytes[ty * dst_stride..(ty + 1) * dst_stride];

        for tx in 0..tw {
            let sx = (((tx as f64) + 0.5) * 0.5 - 0.5).clamp(0.0, max_x);
            let x0 = sx as usize;
            let x1 = (x0 + 1).min(w - 1);
            let fx = sx - (x0 as f64);

            let i0 = x0 * 4;
            let i1 = x1 * 4;
            let d = tx * 4;
            for c in 0..4 {
                let top = (r0[i0 + c] as f64) + (((r0[i1 + c] as f64) - (r0[i0 + c] as f64)) * fx);
                let bot = (r1[i0 + c] as f64) + (((r1[i1 + c] as f64) - (r1[i0 + c] as f64)) * fx);
                d_row[d + c] = (top + ((bot - top) * fy)).round().clamp(0.0, 255.0) as u8;
            }
        }
    }

    1
}

/// Checks if fewer than 1% of sampled pixels are non-black (used to detect GPU/DRM black PrintWindow frames).
#[no_mangle]
pub unsafe extern "C" fn essvb_is_mostly_black(
    src: *const u8,
    width: i32,
    height: i32,
    stride: i32,
) -> i32 {
    if src.is_null() || width <= 0 || height <= 0 || stride < width * 4 {
        return 1;
    }

    let w = width as usize;
    let h = height as usize;
    let s = stride as usize;
    let src_bytes = slice::from_raw_parts(src, (h - 1) * s + w * 4);
    let step_y = (h / 16).max(1);
    let step_x = (w / 16).max(1);

    let mut total: usize = 0;
    let mut non_black: usize = 0;

    let mut y = 0;
    while y < h {
        let row = &src_bytes[y * s..y * s + w * 4];
        let mut x = 0;
        while x < w {
            let i = x * 4;
            total += 1;
            if row[i] > 8 || row[i + 1] > 8 || row[i + 2] > 8 {
                non_black += 1;
            }
            x += step_x;
        }
        y += step_y;
    }

    i32::from(total > 0 && (non_black * 100) < total)
}

/// Crops a pre-clamped rectangle `(x, y, crop_w, crop_h)` from BGRA `src` into tightly packed `dst`.
#[no_mangle]
pub unsafe extern "C" fn essvb_crop_bgra(
    src: *const u8,
    src_w: i32,
    src_h: i32,
    src_stride: i32,
    x: i32,
    y: i32,
    crop_w: i32,
    crop_h: i32,
    dst: *mut u8,
) -> i32 {
    if src.is_null()
        || dst.is_null()
        || src_w <= 0
        || src_h <= 0
        || src_stride < src_w * 4
        || x < 0
        || y < 0
        || crop_w <= 0
        || crop_h <= 0
        || x + crop_w > src_w
        || y + crop_h > src_h
    {
        return -1;
    }

    let sw = src_w as usize;
    let sh = src_h as usize;
    let ss = src_stride as usize;
    let cx = x as usize;
    let cy = y as usize;
    let cw = crop_w as usize;
    let ch = crop_h as usize;
    let row_bytes = cw * 4;

    let src_bytes = slice::from_raw_parts(src, (sh - 1) * ss + sw * 4);
    let dst_bytes = slice::from_raw_parts_mut(dst, ch * row_bytes);

    for row in 0..ch {
        let s_off = (cy + row) * ss + cx * 4;
        let d_off = row * row_bytes;
        dst_bytes[d_off..d_off + row_bytes].copy_from_slice(&src_bytes[s_off..s_off + row_bytes]);
    }

    1
}

/// Converts BGRA32 frame into tightly packed 8-bit grayscale (`((r*3)+(g*6)+b)/10`), optionally inverted.
#[no_mangle]
pub unsafe extern "C" fn essvb_to_grayscale(
    src: *const u8,
    width: i32,
    height: i32,
    stride: i32,
    invert: i32,
    dst: *mut u8,
) -> i32 {
    if src.is_null() || dst.is_null() || width <= 0 || height <= 0 || stride < width * 4 {
        return -1;
    }

    let w = width as usize;
    let h = height as usize;
    let s = stride as usize;
    let inv = invert != 0;
    let src_bytes = slice::from_raw_parts(src, (h - 1) * s + w * 4);
    let dst_bytes = slice::from_raw_parts_mut(dst, w * h);

    for y in 0..h {
        let s_row = &src_bytes[y * s..y * s + w * 4];
        let d_row = &mut dst_bytes[y * w..(y + 1) * w];
        for (px, out) in s_row.chunks_exact(4).zip(d_row.iter_mut()) {
            let luma = ((px[2] as u32 * 3) + (px[1] as u32 * 6) + (px[0] as u32)) / 10;
            *out = if inv { (255 - luma) as u8 } else { luma as u8 };
        }
    }

    1
}

/// Surrounds BGRA32 frame with an opaque black border of `pad` pixels on each side.
#[no_mangle]
pub unsafe extern "C" fn essvb_add_padding(
    src: *const u8,
    width: i32,
    height: i32,
    stride: i32,
    pad: i32,
    dst: *mut u8,
) -> i32 {
    if src.is_null()
        || dst.is_null()
        || width <= 0
        || height <= 0
        || stride < width * 4
        || pad <= 0
        || width + (pad * 2) > 4096
        || height + (pad * 2) > 4096
    {
        return -1;
    }

    let w = width as usize;
    let h = height as usize;
    let s = stride as usize;
    let p = pad as usize;
    let new_w = w + (p * 2);
    let new_h = h + (p * 2);
    let dst_stride = new_w * 4;
    let row_bytes = w * 4;

    let src_bytes = slice::from_raw_parts(src, (h - 1) * s + w * 4);
    let dst_bytes = slice::from_raw_parts_mut(dst, new_h * dst_stride);

    for px in dst_bytes.chunks_exact_mut(4) {
        px[0] = 0;
        px[1] = 0;
        px[2] = 0;
        px[3] = 255;
    }

    for y in 0..h {
        let s_off = y * s;
        let d_off = (y + p) * dst_stride + (p * 4);
        dst_bytes[d_off..d_off + row_bytes].copy_from_slice(&src_bytes[s_off..s_off + row_bytes]);
    }

    1
}

fn levenshtein_slice_u16(a: &[u16], b: &[u16]) -> i32 {
    let m = b.len();
    if m <= 256 {
        let mut row = [0i32; 257];
        for (j, slot) in row[..=m].iter_mut().enumerate() {
            *slot = j as i32;
        }
        for &ac in a {
            let mut prev_diag = row[0];
            row[0] += 1;
            for (j, &bc) in b.iter().enumerate() {
                let old_top = row[j + 1];
                let cost = i32::from(ac != bc);
                row[j + 1] = (row[j] + 1).min(old_top + 1).min(prev_diag + cost);
                prev_diag = old_top;
            }
        }
        row[m]
    } else {
        let mut row: Vec<i32> = (0..=m as i32).collect();
        for &ac in a {
            let mut prev_diag = row[0];
            row[0] += 1;
            for (j, &bc) in b.iter().enumerate() {
                let old_top = row[j + 1];
                let cost = i32::from(ac != bc);
                row[j + 1] = (row[j] + 1).min(old_top + 1).min(prev_diag + cost);
                prev_diag = old_top;
            }
        }
        row[m]
    }
}

/// Computes exact Levenshtein edit distance between two UTF-16 code-unit slices (`char*`).
#[no_mangle]
pub unsafe extern "C" fn essvb_levenshtein_u16(
    a: *const u16,
    a_len: i32,
    b: *const u16,
    b_len: i32,
) -> i32 {
    if a_len < 0 || b_len < 0 {
        return -1;
    }
    if a_len == 0 {
        return b_len;
    }
    if b_len == 0 {
        return a_len;
    }
    if a.is_null() || b.is_null() {
        return -1;
    }

    let sa = slice::from_raw_parts(a, a_len as usize);
    let sb = slice::from_raw_parts(b, b_len as usize);
    levenshtein_slice_u16(sa, sb)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_version_and_black_check() {
        assert_eq!(essvb_fast_version(), 2);
        let black = vec![0u8; 64 * 64 * 4];
        unsafe {
            assert_eq!(essvb_is_mostly_black(black.as_ptr(), 64, 64, 64 * 4), 1);
        }

        let mut bright = vec![0u8; 64 * 64 * 4];
        for px in bright.chunks_exact_mut(4) {
            px[0] = 240;
            px[1] = 240;
            px[2] = 240;
            px[3] = 255;
        }
        unsafe {
            assert_eq!(essvb_is_mostly_black(bright.as_ptr(), 64, 64, 64 * 4), 0);
        }
    }

    #[test]
    fn test_shadow_text_and_signature() {
        let w = 80i32;
        let h = 40i32;
        let stride = w * 4;
        let mut src = vec![0u8; (w * h * 4) as usize];
        let mut dst = vec![0u8; (w * h * 4) as usize];

        for x in 16..=26 {
            let p = ((13 * stride) + (x * 4)) as usize;
            src[p] = 250;
            src[p + 1] = 250;
            src[p + 2] = 250;
            src[p + 3] = 255;

            let sp = ((15 * stride) + ((x + 2) * 4)) as usize;
            src[sp] = 10;
            src[sp + 1] = 10;
            src[sp + 2] = 10;
            src[sp + 3] = 255;
        }

        unsafe {
            assert_eq!(
                essvb_extract_outlined_text(src.as_ptr(), dst.as_mut_ptr(), w, h, stride, 0, 4, 195, 55, 85, 0, 1),
                1
            );
            assert_eq!(dst[((13 * stride) + (20 * 4)) as usize], 0);
            assert_eq!(dst[0], 255);

            let sig_a = [0u8; 32 * 12];
            let mut sig_b = [0u8; 32 * 12];
            assert_eq!(essvb_compute_signature(src.as_ptr(), w, h, stride, sig_b.as_mut_ptr()), 1);
            let diff = essvb_difference_ratio(sig_a.as_ptr(), sig_b.as_ptr(), 32 * 12, 0, 32, 0, 12);
            assert!(diff > 0.0);
        }
    }

    #[test]
    fn test_crop_grayscale_padding_and_levenshtein() {
        // 4x4 BGRA source with distinct pixel values
        let w = 4i32;
        let h = 4i32;
        let stride = w * 4;
        let mut src = vec![0u8; (w * h * 4) as usize];
        // Pixel at (2, 1): B=10, G=100, R=200, A=255 -> luma = (600+600+10)/10 = 121
        let idx = (1 * stride + 2 * 4) as usize;
        src[idx] = 10;
        src[idx + 1] = 100;
        src[idx + 2] = 200;
        src[idx + 3] = 255;

        let mut cropped = vec![0u8; 2 * 2 * 4];
        unsafe {
            assert_eq!(essvb_crop_bgra(src.as_ptr(), w, h, stride, 2, 1, 2, 2, cropped.as_mut_ptr()), 1);
            assert_eq!(&cropped[0..4], &[10, 100, 200, 255]);

            let mut gray = vec![0u8; (w * h) as usize];
            assert_eq!(essvb_to_grayscale(src.as_ptr(), w, h, stride, 0, gray.as_mut_ptr()), 1);
            assert_eq!(gray[1 * (w as usize) + 2], 121);

            let mut gray_inv = vec![0u8; (w * h) as usize];
            assert_eq!(essvb_to_grayscale(src.as_ptr(), w, h, stride, 1, gray_inv.as_mut_ptr()), 1);
            assert_eq!(gray_inv[1 * (w as usize) + 2], 134);

            let mut padded = vec![0u8; 6 * 6 * 4];
            assert_eq!(essvb_add_padding(src.as_ptr(), w, h, stride, 1, padded.as_mut_ptr()), 1);
            assert_eq!(&padded[0..4], &[0, 0, 0, 255]);
            let p_idx = (2 * 6 * 4) + (3 * 4);
            assert_eq!(&padded[p_idx..p_idx + 4], &[10, 100, 200, 255]);

            let s1: Vec<u16> = "çeviri".encode_utf16().collect();
            let s2: Vec<u16> = "çevir".encode_utf16().collect();
            assert_eq!(
                essvb_levenshtein_u16(s1.as_ptr(), s1.len() as i32, s2.as_ptr(), s2.len() as i32),
                1
            );
        }
    }
}
