// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;

namespace EssvbSub.Capture.Ocr;

public static class CrossPlatformOcrFactory
{
    public static ITextReader CreateBestAvailable(string language = "auto")
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
/// Supports all languages in SourceLanguageCatalog via parameterized tessdata mapping.
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

    public static TesseractOcrReader? TryCreate(string language = "auto")
    {
        string lang = MapLang(language);
        nint handle = InitEngine(lang);
        if (handle == 0 && lang != "eng")
            handle = InitEngine("eng");
        return handle == 0 ? null : new TesseractOcrReader(lang, handle);
    }

    private static string MapLang(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang) || lang.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "eng";

        var matched = SourceLanguageCatalog.All.FirstOrDefault(
            x => x.Code.Equals(lang, StringComparison.OrdinalIgnoreCase));
        if (matched is not null)
            return matched.TesseractCode;

        return lang.Trim().ToLowerInvariant();
    }

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
/// Extracts real per-line and per-word BoundingRect coordinates and splits horizontal HUD gaps.
/// </summary>
public sealed unsafe class WinRtOcrReader : ITextReader, IDisposable
{
    private const int PaddingPx = 16;

    [StructLayout(LayoutKind.Sequential)]
    private struct WinRtRect
    {
        public float X;
        public float Y;
        public float Width;
        public float Height;
    }

    private readonly nint _ocrEngine;
    private readonly nint _sbFactory;
    private readonly nint _cryptStatics;
    private readonly object _gate = new();
    private bool _disposed;

    private WinRtOcrReader(nint ocrEngine, nint sbFactory, nint cryptStatics, string activeLanguage, bool requestedMatched)
    {
        _ocrEngine = ocrEngine;
        _sbFactory = sbFactory;
        _cryptStatics = cryptStatics;
        ActiveLanguage = activeLanguage;
        RequestedLanguageMatched = requestedMatched;
    }

    public string EngineName => $"Windows 11 OCR ({ActiveLanguage})";
    public string ActiveLanguage { get; }
    public bool RequestedLanguageMatched { get; }

    public static WinRtOcrReader? TryCreate(string language = "auto")
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
            bool isAuto = string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase);
            bool requestedMatched = false;
            string activeLang = isAuto ? "auto" : language.Trim();

            if (!isAuto && langFactory != 0)
            {
                engine = TryCreateForTag(ocrStatics, langFactory, activeLang);
                if (engine != 0)
                {
                    requestedMatched = true;
                }
                else
                {
                    string? altTag = activeLang.ToLowerInvariant() switch
                    {
                        "en" => "en-US",
                        "zh-hans" => "zh-CN",
                        "zh-hant" => "zh-TW",
                        _ => null
                    };
                    if (altTag is not null)
                    {
                        engine = TryCreateForTag(ocrStatics, langFactory, altTag);
                        if (engine != 0)
                        {
                            requestedMatched = true;
                            activeLang = altTag;
                        }
                    }
                }
            }

            if (engine == 0 && isAuto)
            {
                // Prefer user profile languages in Auto mode, then en-US if available
                var tryFromProfile = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)ocrStatics)[10];
                tryFromProfile(ocrStatics, out engine);
                if (engine != 0)
                {
                    requestedMatched = true;
                    activeLang = "profil/auto";
                }
                else if (langFactory != 0)
                {
                    engine = TryCreateForTag(ocrStatics, langFactory, "en-US");
                    if (engine != 0) { requestedMatched = true; activeLang = "en-US"; }
                }
            }

            if (engine == 0 && langFactory != 0)
            {
                engine = TryCreateForTag(ocrStatics, langFactory, "en-US");
                if (engine == 0)
                    engine = TryCreateForTag(ocrStatics, langFactory, "en");
                if (engine != 0)
                    activeLang = "en";
            }

            if (engine == 0)
            {
                var tryFromProfile = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)ocrStatics)[10];
                tryFromProfile(ocrStatics, out engine);
                if (engine != 0)
                    activeLang = "sistem";
            }

            ReleaseAll(ocrStatics, langFactory);
            if (engine == 0)
            {
                ReleaseAll(sbFactory, cryptStatics);
                return null;
            }

            return new WinRtOcrReader(engine, sbFactory, cryptStatics, activeLang, requestedMatched);
        }
        catch
        {
            return null;
        }
    }

    private static nint TryCreateForTag(nint ocrStatics, nint langFactory, string langTag)
    {
        nint hLang = CreateHString(langTag);
        nint langObj = 0;
        try
        {
            var createLang = (delegate* unmanaged[Stdcall]<nint, nint, out nint, int>)(*(nint**)langFactory)[6];
            if (createLang(langFactory, hLang, out langObj) == 0 && langObj != 0)
            {
                var isSupported = (delegate* unmanaged[Stdcall]<nint, nint, out byte, int>)(*(nint**)ocrStatics)[8];
                if (isSupported(ocrStatics, langObj, out byte ok) == 0 && ok != 0)
                {
                    var tryFromLang = (delegate* unmanaged[Stdcall]<nint, nint, out nint, int>)(*(nint**)ocrStatics)[9];
                    tryFromLang(ocrStatics, langObj, out nint engine);
                    return engine;
                }
            }
        }
        finally
        {
            if (langObj != 0) Marshal.Release(langObj);
            WindowsDeleteString(hLang);
        }
        return 0;
    }

    public Reading ReadLines(Frame image, bool deep)
    {
        if (_disposed || !image.IsConsistent || image.Width < 18 || image.Height < 18 || image.Width > 4096 || image.Height > 4096)
            return Reading.Empty;

        int scale = (image.Height < 180 || image.Width < 900) ? 2 : 1;
        Frame work = scale == 2 ? OcrPreprocessor.Upscale2x(image) : image;
        int innerW = work.Width;
        int innerH = work.Height;
        work = OcrPreprocessor.AddPadding(work, PaddingPx);
        Frame level = OcrPreprocessor.AutoLevel(work);

        var candidates = new Frame[]
        {
            level,
            OcrPreprocessor.ShadowText(level, includeColored: false),
            OcrPreprocessor.WhiteTextOnly(level),
            OcrPreprocessor.ShadowText(level, includeColored: true)
        };

        Reading best = Reading.Empty;
        int bestScore = 0;
        string? coloredSpeakerSource = null;

        for (int i = 0; i < candidates.Length; i++)
        {
            var reading = RecognizeOnce(candidates[i], PaddingPx, innerW, innerH);
            int score = OcrNoise.Score(reading.Text);

            if (SpeakerPrefix.Parse(reading.Text) is not null)
                coloredSpeakerSource ??= reading.Text;

            if (score > bestScore)
            {
                best = reading;
                bestScore = score;
            }
            if (!deep && i >= 1 && bestScore >= 14 && SpeakerPrefix.Parse(best.Text) is not null)
                break;
        }

        // If white-text candidate beat colored/shadow candidates on body quality but lost a colored speaker label, restore it
        if (bestScore > 0 && SpeakerPrefix.Parse(best.Text) is null && coloredSpeakerSource is not null &&
            SpeakerPrefix.Parse(coloredSpeakerSource) is { } sp &&
            TextMatch.IsSameSubtitle(TextMatch.Normalize(best.Text), TextMatch.Normalize(sp.Body)))
        {
            var lines = best.Lines.ToList();
            if (lines.Count > 0)
            {
                lines[0] = lines[0] with { Text = $"{sp.Name}: {lines[0].Text}" };
                return new Reading(lines);
            }
        }

        return best;
    }

    private Reading RecognizeOnce(Frame frame, int padPx, int innerW, int innerH)
    {
        lock (_gate)
        {
            if (_disposed) return Reading.Empty;

            nint buffer = 0, bitmap = 0, asyncOp = 0, asyncInfo = 0, result = 0;
            try
            {
                byte[] packed = frame.Stride == frame.Width * 4
                    ? frame.Pixels
                    : frame.Crop(0, 0, frame.Width, frame.Height).Pixels;

                fixed (byte* pBytes = packed)
                {
                    var createFromBytes = (delegate* unmanaged[Stdcall]<nint, uint, byte*, out nint, int>)(*(nint**)_cryptStatics)[9];
                    if (createFromBytes(_cryptStatics, (uint)packed.Length, pBytes, out buffer) != 0 || buffer == 0)
                        return Reading.Empty;
                }

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
                    if (getStatus(asyncInfo, out status) != 0 || status != 0)
                        break;
                    Thread.Sleep(2);
                }

                if (status != 1)
                    return Reading.Empty;

                var getResults = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)asyncOp)[8];
                if (getResults(asyncOp, out result) != 0 || result == 0)
                    return Reading.Empty;

                var structured = ExtractStructuredLines(result, padPx, innerW, innerH);
                if (structured.Lines.Count > 0)
                    return structured;

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

    private static Reading ExtractStructuredLines(nint ocrResult, int padPx, int innerW, int innerH)
    {
        nint linesVec = 0;
        try
        {
            var getLines = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)ocrResult)[6];
            if (getLines(ocrResult, out linesVec) != 0 || linesVec == 0)
                return Reading.Empty;

            var getLineCount = (delegate* unmanaged[Stdcall]<nint, out uint, int>)(*(nint**)linesVec)[7];
            var getLineAt = (delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)(*(nint**)linesVec)[6];
            if (getLineCount(linesVec, out uint lineCount) != 0 || lineCount == 0)
                return Reading.Empty;

            var extractedLines = new List<TextLine>((int)lineCount);
            double invW = 1.0 / Math.Max(1, innerW);
            double invH = 1.0 / Math.Max(1, innerH);

            for (uint i = 0; i < lineCount; i++)
            {
                nint lineObj = 0, wordsVec = 0;
                try
                {
                    if (getLineAt(linesVec, i, out lineObj) != 0 || lineObj == 0)
                        continue;

                    var getWords = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)lineObj)[6];
                    if (getWords(lineObj, out wordsVec) != 0 || wordsVec == 0)
                    {
                        var getLineText = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)lineObj)[7];
                        if (getLineText(lineObj, out nint hLine) == 0 && hLine != 0)
                        {
                            try
                            {
                                string? lineTxt = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(hLine, out _));
                                if (!string.IsNullOrWhiteSpace(lineTxt))
                                    extractedLines.Add(new TextLine(lineTxt.Trim()));
                            }
                            finally { WindowsDeleteString(hLine); }
                        }
                        continue;
                    }

                    var getWordCount = (delegate* unmanaged[Stdcall]<nint, out uint, int>)(*(nint**)wordsVec)[7];
                    var getWordAt = (delegate* unmanaged[Stdcall]<nint, uint, out nint, int>)(*(nint**)wordsVec)[6];
                    if (getWordCount(wordsVec, out uint wordCount) != 0 || wordCount == 0)
                        continue;

                    var rowWords = new List<TextWord>((int)wordCount);
                    for (uint w = 0; w < wordCount; w++)
                    {
                        nint wordObj = 0;
                        try
                        {
                            if (getWordAt(wordsVec, w, out wordObj) != 0 || wordObj == 0)
                                continue;

                            var getRect = (delegate* unmanaged[Stdcall]<nint, out WinRtRect, int>)(*(nint**)wordObj)[6];
                            var getWordText = (delegate* unmanaged[Stdcall]<nint, out nint, int>)(*(nint**)wordObj)[7];

                            if (getRect(wordObj, out WinRtRect rect) != 0 || getWordText(wordObj, out nint hWord) != 0 || hWord == 0)
                                continue;

                            string? wText;
                            try { wText = Marshal.PtrToStringUni(WindowsGetStringRawBuffer(hWord, out _)); }
                            finally { WindowsDeleteString(hWord); }

                            if (string.IsNullOrWhiteSpace(wText))
                                continue;

                            double left = Math.Clamp((rect.X - padPx) * invW, 0.0, 1.0);
                            double top = Math.Clamp((rect.Y - padPx) * invH, 0.0, 1.0);
                            double right = Math.Clamp((rect.X - padPx + rect.Width) * invW, left + 0.001, 1.0);
                            double bottom = Math.Clamp((rect.Y - padPx + rect.Height) * invH, top + 0.001, 1.0);
                            rowWords.Add(new TextWord(wText.Trim(), new TextBox(left, top, right, bottom)));
                        }
                        finally
                        {
                            if (wordObj != 0) Marshal.Release(wordObj);
                        }
                    }

                    SplitByHorizontalGap(rowWords, extractedLines);
                }
                finally
                {
                    ReleaseAll(wordsVec, lineObj);
                }
            }

            return new Reading(extractedLines);
        }
        finally
        {
            if (linesVec != 0) Marshal.Release(linesVec);
        }
    }

    private static void SplitByHorizontalGap(List<TextWord> words, List<TextLine> destination)
    {
        if (words.Count == 0)
            return;

        var segment = new List<TextWord> { words[0] };
        for (int i = 1; i < words.Count; i++)
        {
            var prev = words[i - 1].Box;
            var curr = words[i].Box;
            double gap = curr.Left - prev.Right;
            double lineHeight = Math.Max(0.015, (prev.Height + curr.Height) * 0.5);

            // Wide horizontal gap isolates corner/edge HUD labels from center dialogue on the same scanline
            if (gap > Math.Max(0.12, lineHeight * 4.0))
            {
                AddSegmentAsLine(segment, destination);
                segment = [words[i]];
            }
            else
            {
                segment.Add(words[i]);
            }
        }
        AddSegmentAsLine(segment, destination);
    }

    private static void AddSegmentAsLine(List<TextWord> words, List<TextLine> destination)
    {
        if (words.Count == 0) return;
        bool useSpace = words.Any(w => w.Text.Any(c => c <= '\u024F'));
        string joined = string.Join(useSpace ? " " : "", words.Select(w => w.Text));
        TextBox box = words.Select(w => w.Box).Aggregate((a, b) => a.Union(b));
        destination.Add(new TextLine(joined, box, Words: words.ToList()));
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
