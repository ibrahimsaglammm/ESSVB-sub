// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Reflection;
using System.Runtime.InteropServices;
using EssvbSub.Core.Models;

namespace EssvbSub.Core.Pipeline;

/// <summary>
/// Zero-copy P/Invoke bridge to native Rust library (essvb_fast.dll / libessvb_fast.so)
/// for SIMD/LTO-accelerated frame preprocessing and change detection, with seamless C# fallback.
/// </summary>
public static unsafe class RustFastAccel
{
    private const string LibName = "essvb_fast";

    public static uint EngineVersion { get; }
    public static bool IsAvailable { get; }

    static RustFastAccel()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(RustFastAccel).Assembly, ResolveNativeLib);
            EngineVersion = essvb_fast_version();
            IsAvailable = EngineVersion >= 1;
        }
        catch
        {
            EngineVersion = 0;
            IsAvailable = false;
        }
    }

    private static nint ResolveNativeLib(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != LibName)
            return 0;

        string file = OperatingSystem.IsWindows()
            ? "essvb_fast.dll"
            : OperatingSystem.IsMacOS() ? "libessvb_fast.dylib" : "libessvb_fast.so";

        string procDir = Path.GetDirectoryName(Environment.ProcessPath) ?? string.Empty;
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, file),
            string.IsNullOrEmpty(procDir) ? file : Path.Combine(procDir, file),
            file,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "native", "essvb_fast", "bin", file))
        ];

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out nint handle))
                return handle;
        }

        return NativeLibrary.TryLoad(name, assembly, path, out nint fallback) ? fallback : 0;
    }

    public static bool TryAutoLevel(Frame image, out Frame result)
    {
        result = image;
        if (!IsAvailable || !image.IsConsistent || image.Width <= 0 || image.Height <= 0)
            return false;

        var output = Frame.Blank(image.Width, image.Height);
        fixed (byte* src = image.Pixels)
        fixed (byte* dst = output.Pixels)
        {
            int status = essvb_auto_level(src, dst, image.Width, image.Height, image.Stride);
            if (status < 0) return false;
            result = status == 0 ? image : output;
            return true;
        }
    }

    public static bool TryExtractOutlinedText(
        Frame image,
        int leftPad,
        int bottomRightPad,
        int minBright,
        int maxSpread,
        int darkLuma,
        bool includeColored,
        bool directionalShadow,
        out Frame result)
    {
        result = image;
        if (!IsAvailable || !image.IsConsistent || image.Width <= 0 || image.Height <= 0)
            return false;

        var output = Frame.Blank(image.Width, image.Height);
        fixed (byte* src = image.Pixels)
        fixed (byte* dst = output.Pixels)
        {
            int status = essvb_extract_outlined_text(
                src, dst, image.Width, image.Height, image.Stride,
                leftPad, bottomRightPad, minBright, maxSpread, darkLuma,
                includeColored ? 1 : 0, directionalShadow ? 1 : 0);
            if (status != 1) return false;
            result = output;
            return true;
        }
    }

    public static bool TryUpscale2x(Frame image, out Frame result)
    {
        result = image;
        if (!IsAvailable || !image.IsConsistent || image.Width <= 0 || image.Height <= 0 ||
            image.Width * 2 > 4096 || image.Height * 2 > 4096)
        {
            return false;
        }

        var output = Frame.Blank(image.Width * 2, image.Height * 2);
        fixed (byte* src = image.Pixels)
        fixed (byte* dst = output.Pixels)
        {
            int status = essvb_upscale_2x(src, dst, image.Width, image.Height, image.Stride);
            if (status != 1) return false;
            result = output;
            return true;
        }
    }

    public static bool TryComputeSignature(Frame regionImage, byte[] outSignature)
    {
        if (!IsAvailable || !regionImage.IsConsistent || regionImage.Width <= 0 || regionImage.Height <= 0 ||
            outSignature.Length < RegionCropper.SignatureWidth * RegionCropper.SignatureHeight)
        {
            return false;
        }

        fixed (byte* src = regionImage.Pixels)
        fixed (byte* sig = outSignature)
        {
            return essvb_compute_signature(src, regionImage.Width, regionImage.Height, regionImage.Stride, sig) == 1;
        }
    }

    public static bool TryDifferenceRatio(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int x0, int x1, int y0, int y1, out double ratio)
    {
        ratio = 1.0;
        if (!IsAvailable || a.Length != b.Length || a.Length == 0)
            return false;

        fixed (byte* pa = a)
        fixed (byte* pb = b)
        {
            ratio = essvb_difference_ratio(pa, pb, a.Length, x0, x1, y0, y1);
            return true;
        }
    }

    public static bool TryIsMostlyBlack(Frame frame, out bool isBlack)
    {
        isBlack = true;
        if (!IsAvailable || !frame.IsConsistent || frame.Width <= 0 || frame.Height <= 0)
            return false;

        fixed (byte* src = frame.Pixels)
        {
            int res = essvb_is_mostly_black(src, frame.Width, frame.Height, frame.Stride);
            isBlack = res != 0;
            return true;
        }
    }

    public static bool TryCropBgra(Frame image, int x, int y, int cropWidth, int cropHeight, out Frame cropped)
    {
        cropped = Frame.Blank(0, 0);
        if (!IsAvailable || EngineVersion < 2 || !image.IsConsistent ||
            image.Width <= 0 || image.Height <= 0 ||
            x < 0 || y < 0 || cropWidth <= 0 || cropHeight <= 0 ||
            x + cropWidth > image.Width || y + cropHeight > image.Height)
        {
            return false;
        }

        var output = Frame.Blank(cropWidth, cropHeight);
        fixed (byte* src = image.Pixels)
        fixed (byte* dst = output.Pixels)
        {
            if (essvb_crop_bgra(src, image.Width, image.Height, image.Stride, x, y, cropWidth, cropHeight, dst) != 1)
                return false;
            cropped = output;
            return true;
        }
    }

    public static bool TryToGrayscale(Frame image, bool invert, out byte[] grayscale)
    {
        grayscale = [];
        if (!IsAvailable || EngineVersion < 2 || !image.IsConsistent || image.Width <= 0 || image.Height <= 0)
            return false;

        var buf = new byte[image.Width * image.Height];
        fixed (byte* src = image.Pixels)
        fixed (byte* dst = buf)
        {
            if (essvb_to_grayscale(src, image.Width, image.Height, image.Stride, invert ? 1 : 0, dst) != 1)
                return false;
            grayscale = buf;
            return true;
        }
    }

    public static bool TryAddPadding(Frame image, int pad, out Frame padded)
    {
        padded = image;
        if (!IsAvailable || EngineVersion < 2 || !image.IsConsistent ||
            pad <= 0 || image.Width <= 0 || image.Height <= 0 ||
            image.Width + (pad * 2) > 4096 || image.Height + (pad * 2) > 4096)
        {
            return false;
        }

        var output = Frame.Blank(image.Width + (pad * 2), image.Height + (pad * 2));
        fixed (byte* src = image.Pixels)
        fixed (byte* dst = output.Pixels)
        {
            if (essvb_add_padding(src, image.Width, image.Height, image.Stride, pad, dst) != 1)
                return false;
            padded = output;
            return true;
        }
    }

    public static bool TryLevenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b, out int distance)
    {
        distance = 0;
        if (!IsAvailable || EngineVersion < 2)
            return false;
        if (a.Length == 0)
        {
            distance = b.Length;
            return true;
        }
        if (b.Length == 0)
        {
            distance = a.Length;
            return true;
        }

        fixed (char* pa = a)
        fixed (char* pb = b)
        {
            int res = essvb_levenshtein_u16((ushort*)pa, a.Length, (ushort*)pb, b.Length);
            if (res < 0)
                return false;
            distance = res;
            return true;
        }
    }

    public static bool TryCopyOpaqueBgra(nint srcPtr, byte[] dst, int pixelCount)
    {
        if (!IsAvailable || EngineVersion < 3 || srcPtr == 0 || pixelCount <= 0 ||
            dst.Length < pixelCount * 4)
        {
            return false;
        }

        fixed (byte* pDst = dst)
        {
            return essvb_copy_opaque_bgra((byte*)srcPtr, pDst, pixelCount) == 1;
        }
    }

    public static bool TryResizeBgra(Frame image, int targetWidth, int targetHeight, out Frame resized)
    {
        resized = image;
        if (!IsAvailable || EngineVersion < 3 || !image.IsConsistent ||
            image.Width <= 0 || image.Height <= 0 ||
            targetWidth <= 0 || targetHeight <= 0 ||
            targetWidth > 4096 || targetHeight > 4096)
        {
            return false;
        }

        var output = Frame.Blank(targetWidth, targetHeight);
        fixed (byte* src = image.Pixels)
        fixed (byte* dst = output.Pixels)
        {
            if (essvb_resize_bgra(src, image.Width, image.Height, image.Stride, dst, targetWidth, targetHeight) != 1)
                return false;
            resized = output;
            return true;
        }
    }

    public static bool TryWordOverlap(ReadOnlySpan<char> normA, ReadOnlySpan<char> normB, out double overlap)
    {
        overlap = 0.0;
        if (!IsAvailable || EngineVersion < 3)
            return false;
        if (normA.IsEmpty || normB.IsEmpty)
            return true;

        fixed (char* pa = normA)
        fixed (char* pb = normB)
        {
            overlap = essvb_word_overlap_u16((ushort*)pa, normA.Length, (ushort*)pb, normB.Length);
            return true;
        }
    }

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint essvb_fast_version();

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_auto_level(byte* src, byte* dst, int width, int height, int stride);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_extract_outlined_text(
        byte* src, byte* dst, int width, int height, int stride,
        int leftPad, int brPad, int minBright, int maxSpread, int darkLuma,
        int includeColored, int directionalShadow);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_compute_signature(byte* src, int width, int height, int stride, byte* outSig);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern double essvb_difference_ratio(byte* a, byte* b, int len, int x0, int x1, int y0, int y1);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_upscale_2x(byte* src, byte* dst, int width, int height, int stride);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_is_mostly_black(byte* src, int width, int height, int stride);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_crop_bgra(byte* src, int srcW, int srcH, int srcStride, int x, int y, int cropW, int cropH, byte* dst);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_to_grayscale(byte* src, int width, int height, int stride, int invert, byte* dst);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_add_padding(byte* src, int width, int height, int stride, int pad, byte* dst);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_levenshtein_u16(ushort* a, int aLen, ushort* b, int bLen);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_copy_opaque_bgra(byte* src, byte* dst, int pixelCount);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int essvb_resize_bgra(byte* src, int srcW, int srcH, int srcStride, byte* dst, int dstW, int dstH);

    [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
    private static extern double essvb_word_overlap_u16(ushort* a, int aLen, ushort* b, int bLen);
}
