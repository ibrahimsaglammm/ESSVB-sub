// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;

namespace EssvbSub.Capture.Ocr;

public static class CrossPlatformOcrFactory
{
    public static ITextReader CreateBestAvailable(string language = "en")
    {
        if (OperatingSystem.IsWindows())
        {
            if (WinRtOcrReader.TryCreate(language) is { } winRt)
                return winRt;
        }

        if (TesseractOcrReader.TryCreate(language) is { } tess)
            return tess;

        return new NullOcrReader();
    }
}

internal sealed class NullOcrReader : ITextReader
{
    public string EngineName => "OCR Kurulumu Eksik";
    public Reading ReadLines(Frame image, bool deep) => Reading.Empty;
}

/// <summary>
/// Cross-platform Tesseract OCR reader via native libtesseract P/Invoke (CachyOS Linux &amp; optional Windows DLL).
/// Combines ShadowText + WhiteTextOnly preprocessing for accurate game subtitle recognition.
/// </summary>
public sealed unsafe partial class TesseractOcrReader : ITextReader, IDisposable
{
    private readonly string _tessLang;
    private readonly ConcurrentBag<nint> _pool = [];
    private readonly SemaphoreSlim _semaphore = new(2, 2);
    private bool _disposed;

    private TesseractOcrReader(string tessLang, nint initialHandle)
    {
        _tessLang = tessLang;
        _pool.Add(initialHandle);
    }

    public string EngineName => "Tesseract OCR";

    public static TesseractOcrReader? TryCreate(string language = "en")
    {
        string lang = MapLang(language);
        nint handle = InitEngine(lang);
        if (handle == 0 && lang != "eng")
            handle = InitEngine("eng");
        return handle == 0 ? null : new TesseractOcrReader(lang, handle);
    }

    private static string MapLang(string lang) => lang.ToLowerInvariant() switch
    {
        "en" => "eng",
        "ja" => "jpn",
        "de" => "deu",
        "fr" => "fra",
        "es" => "spa",
        "it" => "ita",
        "pt" => "por",
        "ru" => "rus",
        _ => "eng"
    };

    private static nint InitEngine(string lang)
    {
        try
        {
            nint h = TessApi.TessBaseAPICreate();
            if (h == 0) return 0;
            if (TessApi.TessBaseAPIInit3(h, null, lang) != 0)
            {
                TessApi.TessBaseAPIDelete(h);
                return 0;
            }
            TessApi.TessBaseAPISetPageSegMode(h, 6); // PSM_SINGLE_BLOCK
            TessApi.TessBaseAPISetVariable(h, "debug_file", OperatingSystem.IsWindows() ? "NUL" : "/dev/null");
            return h;
        }
        catch
        {
            return 0;
        }
    }

    public Reading ReadLines(Frame image, bool deep)
    {
        if (!image.IsConsistent || image.Width < 32 || image.Height < 20)
            return Reading.Empty;

        Frame normalized = OcrPreprocessor.AutoLevel(image);
        var shadow = RecognizeOnce(OcrPreprocessor.ToGrayscale(OcrPreprocessor.ShadowText(normalized), invert: false), normalized.Width, normalized.Height);
        if (shadow.Lines.Count > 0)
            return shadow;

        var white = RecognizeOnce(OcrPreprocessor.ToGrayscale(OcrPreprocessor.WhiteTextOnly(normalized), invert: false), normalized.Width, normalized.Height);
        if (white.Lines.Count > 0 || !deep)
            return white;

        return RecognizeOnce(OcrPreprocessor.ToGrayscale(normalized, invert: true), normalized.Width, normalized.Height);
    }

    private Reading RecognizeOnce(byte[] gray, int width, int height)
    {
        if (_disposed) return Reading.Empty;
        _semaphore.Wait();
        nint engine = 0;
        try
        {
            if (!_pool.TryTake(out engine))
                engine = InitEngine(_tessLang);
            if (engine == 0) return Reading.Empty;

            fixed (byte* p = gray)
                TessApi.TessBaseAPISetImage(engine, p, width, height, 1, width);
            TessApi.TessBaseAPISetSourceResolution(engine, 150);
            if (TessApi.TessBaseAPIRecognize(engine, 0) != 0)
                return Reading.Empty;

            nint ptr = TessApi.TessBaseAPIGetUTF8Text(engine);
            if (ptr == 0) return Reading.Empty;
            string text = Marshal.PtrToStringUTF8(ptr) ?? "";
            TessApi.TessDeleteText(ptr);
            return Reading.FromText(text);
        }
        finally
        {
            if (engine != 0)
            {
                TessApi.TessBaseAPIClear(engine);
                _pool.Add(engine);
            }
            _semaphore.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        while (_pool.TryTake(out nint h))
            TessApi.TessBaseAPIDelete(h);
        _semaphore.Dispose();
    }

    private static partial class TessApi
    {
        private const string Lib = "tesseract";

        static TessApi() => NativeLibrary.SetDllImportResolver(typeof(TessApi).Assembly, Resolve);

        private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
        {
            if (name != Lib) return 0;
            foreach (var candidate in new[] { "libtesseract.so.5", "libtesseract.so.4", "libtesseract.so", "libtesseract-5.dll", "tesseract" })
                if (NativeLibrary.TryLoad(candidate, assembly, path, out nint h)) return h;
            return 0;
        }

        [LibraryImport(Lib)] internal static partial nint TessBaseAPICreate();
        [LibraryImport(Lib)] internal static partial void TessBaseAPIDelete(nint handle);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] internal static partial int TessBaseAPIInit3(nint handle, string? dataPath, string language);
        [LibraryImport(Lib)] internal static partial void TessBaseAPISetPageSegMode(nint handle, int mode);
        [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] internal static partial int TessBaseAPISetVariable(nint handle, string name, string value);
        [LibraryImport(Lib)] internal static partial void TessBaseAPISetImage(nint handle, byte* data, int width, int height, int bpp, int stride);
        [LibraryImport(Lib)] internal static partial void TessBaseAPISetSourceResolution(nint handle, int ppi);
        [LibraryImport(Lib)] internal static partial int TessBaseAPIRecognize(nint handle, nint monitor);
        [LibraryImport(Lib)] internal static partial void TessBaseAPIClear(nint handle);
        [LibraryImport(Lib)] internal static partial nint TessBaseAPIGetUTF8Text(nint handle);
        [LibraryImport(Lib)] internal static partial void TessDeleteText(nint text);
    }
}

/// <summary>
/// Zero-dependency Windows 11 built-in OCR via WinRT COM ABI (pure net10.0, no Windows TFM lock-in).
/// </summary>
public sealed unsafe class WinRtOcrReader : ITextReader, IDisposable
{
    private readonly nint _ocrEngine;
    private readonly nint _sbFactory;
    private readonly nint _cryptStatics;
    private readonly object _gate = new();
    private bool _disposed;

    private WinRtOcrReader(nint ocrEngine, nint sbFactory, nint cryptStatics)
    {
        _ocrEngine = ocrEngine;
        _sbFactory = sbFactory;
        _cryptStatics = cryptStatics;
    }

    public string EngineName => "Windows 11 Built-in OCR";

    public static WinRtOcrReader? TryCreate(string language = "en")
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            RoInitialize(1); // RO_INIT_MULTITHREADED

            var iidOcrStatics = new Guid("5BFFA85A-3384-3540-9940-699120D428A8");
            var iidLangFactory = new Guid("9B0252AC-0C27-44F8-B792-9793FB66C63E");
            var iidSbFactory = new Guid("C99FEB69-2D62-4D47-A6B3-4FDB6A07FDF8");
            var iidCryptStatics = new Guid("320B7E22-3CB0-4CDF-8663-1D28910065EB");

            nint ocrStatics = GetActivationFactory("Windows.Media.Ocr.OcrEngine", iidOcrStatics);
            nint langFactory = GetActivationFactory("Windows.Globalization.Language", iidLangFactory);
            nint sbFactory = GetActivationFactory("Windows.Graphics.Imaging.SoftwareBitmap", iidSbFactory);
            nint cryptStatics = GetActivationFactory("Windows.Security.Cryptography.CryptographicBuffer", iidCryptStatics);

            if (ocrStatics == 0 || sbFactory == 0 || cryptStatics == 0)
            {
                ReleaseAll(ocrStatics, langFactory, sbFactory, cryptStatics);
                return null;
            }

            nint engine = 0;
            if (langFactory != 0)
            {
                nint hLang = CreateHString(language);
                try
                {
                    var createLang = (delegate* unmanaged[Stdcall]<nint, nint, out nint, int>)(*(nint**)langFactory)[6];
                    if (createLang(langFactory, hLang, out nint langObj) == 0 && langObj != 0)
                    {
                        var tryFromLang = (delegate* unmanaged[Stdcall]<nint, nint, out nint, int>)(*(nint**)ocrStatics)[9];
                        tryFromLang(ocrStatics, langObj, out engine);
                        Marshal.Release(langObj);
                    }
                }
                finally
                {
                    WindowsDeleteString(hLang);
                }
            }

            if (engine == 0)
            {
                var tryFromProfile = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)ocrStatics)[10];
                tryFromProfile(ocrStatics, out engine);
            }

            ReleaseAll(ocrStatics, langFactory);
            if (engine == 0)
            {
                ReleaseAll(sbFactory, cryptStatics);
                return null;
            }

            return new WinRtOcrReader(engine, sbFactory, cryptStatics);
        }
        catch
        {
            return null;
        }
    }

    public Reading ReadLines(Frame image, bool deep)
    {
        if (_disposed || !image.IsConsistent || image.Width < 18 || image.Height < 18 || image.Width > 4096 || image.Height > 4096)
            return Reading.Empty;

        Frame work = (image.Height < 180 || image.Width < 900) ? OcrPreprocessor.Upscale2x(image) : image;
        work = OcrPreprocessor.AddPadding(work, 16);
        Frame level = OcrPreprocessor.AutoLevel(work);
        var candidates = new Frame[]
        {
            level,
            OcrPreprocessor.ShadowText(level),
            OcrPreprocessor.WhiteTextOnly(level)
        };

        Reading best = Reading.Empty;
        int bestScore = 0;

        foreach (var candidate in candidates)
        {
            var reading = RecognizeOnce(candidate);
            int score = OcrNoise.Score(reading.Text);
            if (score > bestScore)
            {
                best = reading;
                bestScore = score;
            }
            if (!deep && bestScore >= 12)
                break;
        }

        return best;
    }

    private Reading RecognizeOnce(Frame frame)
    {
        lock (_gate)
        {
            if (_disposed) return Reading.Empty;

            nint buffer = 0, bitmap = 0, asyncOp = 0, asyncInfo = 0, result = 0;
            try
            {
                // Ensure tightly packed BGRA buffer
                byte[] packed = frame.Stride == frame.Width * 4
                    ? frame.Pixels
                    : frame.Crop(0, 0, frame.Width, frame.Height).Pixels;

                fixed (byte* pBytes = packed)
                {
                    var createFromBytes = (delegate* unmanaged[Stdcall]<nint, uint, byte*, out nint, int>)(*(nint**)_cryptStatics)[9];
                    if (createFromBytes(_cryptStatics, (uint)packed.Length, pBytes, out buffer) != 0 || buffer == 0)
                        return Reading.Empty;
                }

                // BitmapPixelFormat.Bgra8 = 87, BitmapAlphaMode.Premultiplied = 0
                var createBitmap = (delegate* unmanaged[Stdcall]<nint, int, int, int, int, out nint, int>)(*(nint**)_sbFactory)[7];
                if (createBitmap(_sbFactory, 87, frame.Width, frame.Height, 0, out bitmap) != 0 || bitmap == 0)
                    return Reading.Empty;

                var copyFromBuffer = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)bitmap)[17];
                if (copyFromBuffer(bitmap, buffer) != 0)
                    return Reading.Empty;

                var recognizeAsync = (delegate* unmanaged[Stdcall]<nint, nint, out nint, int>)(*(nint**)_ocrEngine)[6];
                if (recognizeAsync(_ocrEngine, bitmap, out asyncOp) != 0 || asyncOp == 0)
                    return Reading.Empty;

                var iidAsyncInfo = new Guid("00000036-0000-0000-C000-000000000046");
                if (Marshal.QueryInterface(asyncOp, in iidAsyncInfo, out asyncInfo) != 0)
                    return Reading.Empty;

                var getStatus = (delegate* unmanaged[Stdcall]<nint, out int, int>)(*(nint**)asyncInfo)[7];
                int status = 0;
                long deadline = Environment.TickCount64 + 3000;
                while (Environment.TickCount64 < deadline)
                {
                    if (getStatus(asyncInfo, out status) != 0 || status != 0) // 0 = Started
                        break;
                    Thread.Sleep(2);
                }

                if (status != 1) // 1 = Completed
                    return Reading.Empty;

                var getResults = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)asyncOp)[8];
                if (getResults(asyncOp, out result) != 0 || result == 0)
                    return Reading.Empty;

                var getText = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)result)[8];
                if (getText(result, out nint hText) != 0 || hText == 0)
                    return Reading.Empty;

                try
                {
                    string? raw = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(hText, out _));
                    return string.IsNullOrWhiteSpace(raw) ? Reading.Empty : Reading.FromText(raw);
                }
                finally
                {
                    WindowsDeleteString(hText);
                }
            }
            catch
            {
                return Reading.Empty;
            }
            finally
            {
                ReleaseAll(result, asyncInfo, asyncOp, bitmap, buffer);
            }
        }
    }

    private static nint GetActivationFactory(string className, in Guid iid)
    {
        nint hStr = CreateHString(className);
        try
        {
            return RoGetActivationFactory(hStr, in iid, out nint factory) == 0 ? factory : 0;
        }
        finally
        {
            WindowsDeleteString(hStr);
        }
    }

    private static nint CreateHString(string value)
    {
        WindowsCreateString(value, (uint)value.Length, out nint hStr);
        return hStr;
    }

    private static void ReleaseAll(params nint[] ptrs)
    {
        foreach (nint p in ptrs)
            if (p != 0) Marshal.Release(p);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseAll(_ocrEngine, _sbFactory, _cryptStatics);
        }
    }

    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")] private static extern int RoInitialize(int initType);
    [DllImport("api-ms-win-core-winrt-l1-1-0.dll")] private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string sourceString, uint length, out nint hString);
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")] private static extern int WindowsDeleteString(nint hString);
    [DllImport("api-ms-win-core-winrt-string-l1-1-0.dll")] private static extern nint WindowsGetStringRawBuffer(nint hString, out uint length);
}
