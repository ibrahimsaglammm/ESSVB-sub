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

    public static bool IsAvailable { get; }

    static RustFastAccel()
    {
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(RustFastAccel).Assembly, ResolveNativeLib);
            IsAvailable = essvb_fast_version() == 1;
        }
        catch
        {
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

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, file),
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
}
