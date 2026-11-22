// Copyright (c) 2026 ESSVB. All Rights Reserved.

using EssvbSub.Core.Models;

namespace EssvbSub.Core.Pipeline;

/// <summary>
/// High-speed image preprocessing helpers for improving OCR accuracy over bright game scenes
/// and low-contrast HDR streams without external image processing dependencies.
/// </summary>
public static class OcrPreprocessor
{
    /// <summary>
    /// Normalizes dim frames (HDR tone-mapped or dark scenes) so top 0.5% luminance reaches near-white.
    /// </summary>
    public static Frame AutoLevel(Frame image)
    {
        if (!image.IsConsistent || image.Width == 0 || image.Height == 0)
            return image;

        Span<int> histogram = stackalloc int[256];
        for (int y = 0; y < image.Height; y++)
        {
            int row = y * image.Stride;
            for (int x = 0; x < image.Width; x++)
            {
                int i = row + (x * 4);
                int luma = ((image.Pixels[i + 2] * 3) + (image.Pixels[i + 1] * 6) + image.Pixels[i]) / 10;
                histogram[Math.Clamp(luma, 0, 255)]++;
            }
        }

        double limit = image.Width * image.Height * 0.005;
        int top = 255;
        int count = 0;
        for (; top > 0; top--)
        {
            count += histogram[top];
            if (count >= limit) break;
        }

        if (top >= 235 || top < 60)
            return image;

        double scale = 250.0 / top;
        Span<byte> lut = stackalloc byte[256];
        for (int v = 0; v < 256; v++)
            lut[v] = (byte)Math.Min(255, (int)(v * scale));

        var output = Frame.Blank(image.Width, image.Height);
        for (int y = 0; y < image.Height; y++)
        {
            int srcRow = y * image.Stride;
            int dstRow = y * output.Stride;
            for (int x = 0; x < image.Width; x++)
            {
                int s = srcRow + (x * 4);
                int d = dstRow + (x * 4);
                output.Pixels[d] = lut[image.Pixels[s]];
                output.Pixels[d + 1] = lut[image.Pixels[s + 1]];
                output.Pixels[d + 2] = lut[image.Pixels[s + 2]];
                output.Pixels[d + 3] = 255;
            }
        }

        return output;
    }

    /// <summary>
    /// Extracts bright subtitle pixels surrounded by dark outlines/shadows into black text on white background.
    /// </summary>
    public static Frame WhiteTextOnly(Frame image, int radius = 5) =>
        ExtractOutlinedText(image, radius, radius, minBright: 200, maxSpread: 45, darkLuma: 110, includeColored: false, directionalShadow: false);

    /// <summary>
    /// Directional drop-shadow text filter: looks for dark shadow pixels only to the bottom-right,
    /// preventing bright backgrounds (sky, snow, sand) from merging with white glyphs.
    /// </summary>
    public static Frame ShadowText(Frame image, int radius = 4, int minBright = 195, int maxSpread = 55, int darkLuma = 85, bool includeColored = false) =>
        ExtractOutlinedText(image, 0, radius, minBright, maxSpread, darkLuma, includeColored, directionalShadow: true);

    private static Frame ExtractOutlinedText(
        Frame image,
        int leftPad,
        int bottomRightPad,
        int minBright,
        int maxSpread,
        int darkLuma,
        bool includeColored,
        bool directionalShadow)
    {
        if (!image.IsConsistent || image.Width == 0 || image.Height == 0)
            return image;

        int w = image.Width;
        int h = image.Height;
        var bright = new bool[w * h];
        var darkIntegral = new int[(w + 1) * (h + 1)];

        for (int y = 0; y < h; y++)
        {
            int rowSum = 0;
            int row = y * image.Stride;
            for (int x = 0; x < w; x++)
            {
                int i = row + (x * 4);
                int b = image.Pixels[i];
                int g = image.Pixels[i + 1];
                int r = image.Pixels[i + 2];
                int min = Math.Min(r, Math.Min(g, b));
                int max = Math.Max(r, Math.Max(g, b));
                int luma = ((r * 3) + (g * 6) + b) / 10;

                bool isBrightNeutral = min >= minBright && (max - min) <= maxSpread;
                bool isBrightColored = includeColored && max >= 170 && (max - min) >= 60 && luma >= 120;
                bright[(y * w) + x] = isBrightNeutral || isBrightColored;

                rowSum += luma < darkLuma ? 1 : 0;
                darkIntegral[((y + 1) * (w + 1)) + x + 1] = darkIntegral[(y * (w + 1)) + x + 1] + rowSum;
            }
        }

        var output = Frame.Blank(w, h);
        for (int y = 0; y < h; y++)
        {
            int top = directionalShadow ? y : Math.Max(0, y - leftPad);
            int bottom = Math.Min(h, y + bottomRightPad + 1);
            int dstRow = y * output.Stride;

            for (int x = 0; x < w; x++)
            {
                bool isText = false;
                if (bright[(y * w) + x])
                {
                    int left = directionalShadow ? x : Math.Max(0, x - leftPad);
                    int right = Math.Min(w, x + bottomRightPad + 1);
                    int darkCount = darkIntegral[(bottom * (w + 1)) + right]
                                  - darkIntegral[(top * (w + 1)) + right]
                                  - darkIntegral[(bottom * (w + 1)) + left]
                                  + darkIntegral[(top * (w + 1)) + left];
                    isText = darkCount > 0;
                }

                byte pixel = (byte)(isText ? 0 : 255);
                int d = dstRow + (x * 4);
                output.Pixels[d] = pixel;
                output.Pixels[d + 1] = pixel;
                output.Pixels[d + 2] = pixel;
                output.Pixels[d + 3] = 255;
            }
        }

        return output;
    }

    public static byte[] ToGrayscale(Frame image, bool invert)
    {
        var gray = new byte[image.Width * image.Height];
        if (!image.IsConsistent || image.Width == 0 || image.Height == 0)
            return gray;

        for (int y = 0; y < image.Height; y++)
        {
            int row = y * image.Stride;
            for (int x = 0; x < image.Width; x++)
            {
                int i = row + (x * 4);
                int luma = ((image.Pixels[i + 2] * 3) + (image.Pixels[i + 1] * 6) + image.Pixels[i]) / 10;
                gray[(y * image.Width) + x] = (byte)(invert ? 255 - luma : luma);
            }
        }

        return gray;
    }
}

public static class RegionCropper
{
    public const int SignatureWidth = 32;
    public const int SignatureHeight = 12;

    public static Frame Crop(Frame image, CropRegion region)
    {
        var clamped = region.Clamp();
        int left = (int)Math.Floor(image.Width * clamped.Left);
        int top = (int)Math.Floor(image.Height * clamped.Top);
        int right = (int)Math.Ceiling(image.Width * clamped.Right);
        int bottom = (int)Math.Ceiling(image.Height * clamped.Bottom);
        return image.Crop(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Generates a compact 32x12 grayscale perceptual signature for ultra-fast change detection.
    /// </summary>
    public static byte[] ComputeSignature(Frame regionImage)
    {
        var signature = new byte[SignatureWidth * SignatureHeight];
        if (!regionImage.IsConsistent || regionImage.Width == 0 || regionImage.Height == 0)
            return signature;

        for (int y = 0; y < SignatureHeight; y++)
        {
            int srcY = y * regionImage.Height / SignatureHeight;
            int row = srcY * regionImage.Stride;
            for (int x = 0; x < SignatureWidth; x++)
            {
                int srcX = x * regionImage.Width / SignatureWidth;
                int p = row + (srcX * 4);
                signature[(y * SignatureWidth) + x] = (byte)(
                    (regionImage.Pixels[p] + (regionImage.Pixels[p + 1] * 2) + regionImage.Pixels[p + 2]) / 4);
            }
        }

        return signature;
    }

    /// <summary>
    /// Computes normalized mean absolute difference [0..1] between two signatures, optionally restricted to a bounding box.
    /// </summary>
    public static double DifferenceRatio(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, TextBox? box = null)
    {
        if (a.Length != b.Length || a.Length == 0)
            return 1.0;

        int x0 = 0, x1 = SignatureWidth, y0 = 0, y1 = SignatureHeight;
        if (box is { } region && a.Length == SignatureWidth * SignatureHeight)
        {
            x0 = Math.Clamp((int)Math.Floor(region.Left * SignatureWidth), 0, SignatureWidth - 1);
            x1 = Math.Clamp((int)Math.Ceiling(region.Right * SignatureWidth), x0 + 1, SignatureWidth);
            y0 = Math.Clamp((int)Math.Floor(region.Top * SignatureHeight), 0, SignatureHeight - 1);
            y1 = Math.Clamp((int)Math.Ceiling(region.Bottom * SignatureHeight), y0 + 1, SignatureHeight);
        }

        long diffSum = 0;
        int count = 0;
        for (int y = y0; y < y1; y++)
        {
            int offset = y * SignatureWidth;
            for (int x = x0; x < x1; x++)
            {
                int idx = offset + x;
                diffSum += Math.Abs(a[idx] - b[idx]);
                count++;
            }
        }

        return count == 0 ? 1.0 : diffSum / (count * 255.0);
    }
}
