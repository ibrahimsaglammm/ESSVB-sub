// Copyright (c) 2026 ESSVB. All Rights Reserved.

namespace EssvbSub.Core.Models;

/// <summary>
/// Immutable BGRA32 raw image frame used across capture, preprocessing, and OCR stages.
/// </summary>
public sealed class Frame
{
    public Frame(byte[] pixels, int width, int height, int stride)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        Stride = stride;
        IsConsistent = ValidateLayout(pixels, width, height, stride);
    }

    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public bool IsConsistent { get; }

    public string LayoutDescription => $"{Width}x{Height}, stride={Stride}, bytes={Pixels?.Length ?? 0}";

    private static bool ValidateLayout(byte[]? pixels, int width, int height, int stride)
    {
        if (pixels is null || width < 0 || height < 0 || stride < 0)
            return false;
        if (width == 0 || height == 0)
            return true;

        long minRowBytes = (long)width * 4;
        long requiredBytes = ((long)(height - 1) * stride) + minRowBytes;
        return stride >= minRowBytes && pixels.LongLength >= requiredBytes;
    }

    public static Frame Blank(int width, int height)
    {
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        int stride = width * 4;
        return new Frame(new byte[stride * height], width, height, stride);
    }

    /// <summary>
    /// Copies unmanaged BGRA buffer into a managed Frame safely.
    /// </summary>
    public static unsafe Frame CopyFromPointer(IntPtr bgraPtr, int width, int height, int sourceStride)
    {
        if (bgraPtr == IntPtr.Zero || width <= 0 || height <= 0 || sourceStride < width * 4)
            return Blank(0, 0);

        int destStride = width * 4;
        var pixels = new byte[destStride * height];
        var src = (byte*)bgraPtr;

        fixed (byte* dst = pixels)
        {
            for (int y = 0; y < height; y++)
            {
                Buffer.MemoryCopy(
                    src + ((long)y * sourceStride),
                    dst + ((long)y * destStride),
                    destStride,
                    destStride);
            }
        }

        return new Frame(pixels, width, height, destStride);
    }

    public Frame Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width);
        y = Math.Clamp(y, 0, Height);
        width = Math.Clamp(width, 0, Width - x);
        height = Math.Clamp(height, 0, Height - y);

        var cropped = Blank(width, height);
        if (!IsConsistent || width == 0 || height == 0)
            return cropped;

        if (EssvbSub.Core.Pipeline.RustFastAccel.TryCropBgra(this, x, y, width, height, out var fastCropped))
            return fastCropped;

        int rowBytes = width * 4;
        for (int row = 0; row < height; row++)
        {
            Buffer.BlockCopy(
                Pixels,
                ((y + row) * Stride) + (x * 4),
                cropped.Pixels,
                row * cropped.Stride,
                rowBytes);
        }

        return cropped;
    }

    public Frame Scaled(int factor)
    {
        if (factor <= 1 || !IsConsistent || Width == 0 || Height == 0)
            return factor == 1 ? this : Blank(Width * Math.Max(1, factor), Height * Math.Max(1, factor));

        return Resized(Width * factor, Height * factor);
    }

    public Frame Resized(int targetWidth, int targetHeight)
    {
        var output = Blank(targetWidth, targetHeight);
        if (!IsConsistent || Width == 0 || Height == 0 || targetWidth <= 0 || targetHeight <= 0)
            return output;

        if (EssvbSub.Core.Pipeline.RustFastAccel.TryResizeBgra(this, targetWidth, targetHeight, out var fastResized))
            return fastResized;

        double scaleX = (double)Width / targetWidth;
        double scaleY = (double)Height / targetHeight;

        for (int ty = 0; ty < targetHeight; ty++)
        {
            double sy = Math.Clamp(((ty + 0.5) * scaleY) - 0.5, 0, Height - 1.0);
            int y0 = (int)sy;
            int y1 = Math.Min(y0 + 1, Height - 1);
            double fy = sy - y0;

            for (int tx = 0; tx < targetWidth; tx++)
            {
                double sx = Math.Clamp(((tx + 0.5) * scaleX) - 0.5, 0, Width - 1.0);
                int x0 = (int)sx;
                int x1 = Math.Min(x0 + 1, Width - 1);
                double fx = sx - x0;

                int i00 = (y0 * Stride) + (x0 * 4);
                int i10 = (y0 * Stride) + (x1 * 4);
                int i01 = (y1 * Stride) + (x0 * 4);
                int i11 = (y1 * Stride) + (x1 * 4);
                int dst = (ty * output.Stride) + (tx * 4);

                for (int c = 0; c < 4; c++)
                {
                    double top = Pixels[i00 + c] + ((Pixels[i10 + c] - Pixels[i00 + c]) * fx);
                    double bot = Pixels[i01 + c] + ((Pixels[i11 + c] - Pixels[i01 + c]) * fx);
                    output.Pixels[dst + c] = (byte)Math.Clamp(Math.Round(top + ((bot - top) * fy)), 0, 255);
                }
            }
        }

        return output;
    }
}
