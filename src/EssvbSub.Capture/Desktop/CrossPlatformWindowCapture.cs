// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using EssvbSub.Core.Models;

namespace EssvbSub.Capture.Desktop;

public sealed record WindowTarget(string ProcessName, string Title, nint Handle, int Width, int Height)
{
    public override string ToString() => $"{Title} ({ProcessName}) [{Width}x{Height}]";
}

/// <summary>
/// Cross-platform window &amp; screen region capture for Windows 11 (PrintWindow + Screen BitBlt)
/// and CachyOS Linux (X11/XWayland libX11 + Wayland grim/ffmpeg fallback).
/// </summary>
public sealed class CrossPlatformWindowCapture : IDisposable
{
    private const int MinWindowWidth = 320;
    private const int MinWindowHeight = 180;

    private readonly object _gate = new();
    private readonly int _fps;
    private WindowTarget? _targetWindow;
    private (int X, int Y, int Width, int Height)? _screenRegion;
    private Timer? _timer;
    private int _busy;
    private bool _running;
    private bool _disposed;
    private bool _loggedFirstFrame;
    private int _blackFrameStreak;

    private WindowsDibGrabber? _winGrabber;

    public CrossPlatformWindowCapture(int fps = 10)
    {
        _fps = Math.Clamp(fps, 1, 30);
    }

    public event Action<Frame>? FrameReceived;
    public event Action<string>? StateChanged;
    public event Action<string>? DiagnosticLogged;

    public void SetWindowTarget(WindowTarget target)
    {
        lock (_gate)
        {
            _targetWindow = target;
            _screenRegion = null;
            _loggedFirstFrame = false;
            _blackFrameStreak = 0;
        }
    }

    public void SetScreenRegion(int x, int y, int width, int height)
    {
        lock (_gate)
        {
            _screenRegion = (x, y, Math.Max(32, width), Math.Max(32, height));
            _targetWindow = null;
            _loggedFirstFrame = false;
            _blackFrameStreak = 0;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;
            _loggedFirstFrame = false;
            _blackFrameStreak = 0;
            _winGrabber ??= OperatingSystem.IsWindows() ? new WindowsDibGrabber() : null;
            int periodMs = 1000 / _fps;
            _timer = new Timer(_ => Tick(), null, 0, periodMs);
        }
        StateChanged?.Invoke("Yakalama Aktif");
    }

    public void Stop()
    {
        Timer? timer;
        lock (_gate)
        {
            _running = false;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        StateChanged?.Invoke("Durduruldu");
    }

    private void Tick()
    {
        if (!_running || Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            WindowTarget? win;
            (int X, int Y, int Width, int Height)? region;
            lock (_gate)
            {
                win = _targetWindow;
                region = _screenRegion;
            }

            Frame? frame = null;
            string captureMode = "Capture";
            if (OperatingSystem.IsWindows() && _winGrabber is not null)
            {
                if (region is { } r)
                {
                    frame = _winGrabber.CaptureScreenRect(r.X, r.Y, r.Width, r.Height);
                    captureMode = "Ekran ROI (BitBlt)";
                }
                else if (win is not null)
                {
                    (frame, captureMode) = _winGrabber.CaptureWindow(win.Handle);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                if (region is { } r)
                {
                    frame = LinuxCapture.CaptureScreenRect(r.X, r.Y, r.Width, r.Height);
                    captureMode = "Linux ROI";
                }
                else if (win is not null)
                {
                    frame = LinuxCapture.CaptureWindow(win.Handle);
                    captureMode = "Linux Window";
                }
            }

            if (frame is { IsConsistent: true, Width: > 1, Height: > 1 })
            {
                if (!_loggedFirstFrame)
                {
                    _loggedFirstFrame = true;
                    DiagnosticLogged?.Invoke($"Görüntü akışı alındı: {frame.Width}x{frame.Height} ({captureMode})");
                }

                if (WindowsDibGrabber.IsMostlyBlack(frame))
                {
                    _blackFrameStreak++;
                    if (_blackFrameStreak == 30)
                        DiagnosticLogged?.Invoke("Uyarı: Yakalanan görüntü siyah geliyor (DRM veya donanım hızlandırma). Altyazı Alanını Seç (ROI) veya Sistem Sesini Dinle modunu deneyin.");
                }
                else
                {
                    _blackFrameStreak = 0;
                }

                FrameReceived?.Invoke(frame);
            }
        }
        catch { }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public static IReadOnlyList<WindowTarget> ListWindows()
    {
        if (OperatingSystem.IsWindows())
            return ListWindowsWindows();
        if (OperatingSystem.IsLinux())
            return LinuxCapture.ListWindows();
        return [];
    }

    private static List<WindowTarget> ListWindowsWindows()
    {
        var results = new List<WindowTarget>();
        int selfPid = Environment.ProcessId;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd))
                return true;
            if (!Win32.GetWindowRect(hwnd, out var rect))
                return true;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w < MinWindowWidth || h < MinWindowHeight)
                return true;

            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if ((int)pid == selfPid)
                return true;

            int len = Win32.GetWindowTextLengthW(hwnd);
            if (len <= 0)
                return true;

            var sb = new StringBuilder(Math.Min(len + 1, 512));
            Win32.GetWindowTextW(hwnd, sb, sb.Capacity);
            string title = sb.ToString().Trim();
            if (title.Length == 0)
                return true;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                results.Add(new WindowTarget(proc.ProcessName, title, hwnd, w, h));
            }
            catch { }

            return true;
        }, 0);

        return [.. results.OrderBy(w => w.ProcessName).ThenBy(w => w.Title)];
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
        _winGrabber?.Dispose();
        _winGrabber = null;
    }

    private sealed class WindowsDibGrabber : IDisposable
    {
        private const uint PwRenderFullContent = 2;
        private const int SrcCopy = 0x00CC0020;

        private nint _memDc;
        private nint _bitmap;
        private nint _oldBitmap;
        private nint _bits;
        private int _width;
        private int _height;

        public (Frame? Frame, string Mode) CaptureWindow(nint hwnd)
        {
            if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd))
                return (null, "None");
            if (!GetAccurateWindowRect(hwnd, out var rect))
                return (null, "None");

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            if (w < 4 || h < 4)
                return (null, "None");

            EnsureBuffer(w, h);
            if (_memDc == 0 || _bits == 0)
                return (null, "None");

            // Firefox / Chromium hardware-accelerated video layers render blank under PrintWindow,
            // so prefer desktop BitBlt whenever the target window's subtitle band is visible on screen.
            if (IsWindowVisibleAtPoint(hwnd, rect))
            {
                CaptureScreenRegionInternal(rect.Left, rect.Top, w, h);
                var screenFrame = CopyPixels(w, h);
                if (!IsMostlyBlack(screenFrame))
                    return (screenFrame, "BitBlt (Ekran)");
            }

            if (Win32.PrintWindow(hwnd, _memDc, PwRenderFullContent))
            {
                var printed = CopyPixels(w, h);
                if (!IsMostlyBlack(printed))
                    return (printed, "PrintWindow");
            }

            CaptureScreenRegionInternal(rect.Left, rect.Top, w, h);
            return (CopyPixels(w, h), "BitBlt (Yedek)");
        }

        public Frame? CaptureScreenRect(int x, int y, int w, int h)
        {
            if (w < 4 || h < 4) return null;
            EnsureBuffer(w, h);
            if (_memDc == 0 || _bits == 0) return null;

            CaptureScreenRegionInternal(x, y, w, h);
            return CopyPixels(w, h);
        }

        private void CaptureScreenRegionInternal(int x, int y, int w, int h)
        {
            nint screenDc = Win32.GetDC(0);
            try { Win32.BitBlt(_memDc, 0, 0, w, h, screenDc, x, y, SrcCopy); }
            finally { Win32.ReleaseDC(0, screenDc); }
        }

        private static bool GetAccurateWindowRect(nint hwnd, out Win32.Rect rect)
        {
            const int DwmwaExtendedFrameBounds = 9;
            if (Win32.DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out rect, Marshal.SizeOf<Win32.Rect>()) == 0 &&
                rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                return true;
            }
            return Win32.GetWindowRect(hwnd, out rect);
        }

        private static bool IsWindowVisibleAtPoint(nint hwnd, in Win32.Rect rect)
        {
            if (Win32.GetForegroundWindow() == hwnd)
                return true;

            var pt = new Win32.Point
            {
                X = rect.Left + ((rect.Right - rect.Left) / 2),
                Y = rect.Top + (((rect.Bottom - rect.Top) * 3) / 4)
            };
            nint topHwnd = Win32.WindowFromPoint(pt);
            if (topHwnd == 0)
                return false;

            Win32.GetWindowThreadProcessId(hwnd, out uint targetPid);
            Win32.GetWindowThreadProcessId(topHwnd, out uint topPid);
            return topPid == targetPid || topPid == (uint)Environment.ProcessId;
        }

        public static bool IsMostlyBlack(Frame frame)
        {
            if (!frame.IsConsistent || frame.Width <= 0 || frame.Height <= 0)
                return true;

            if (EssvbSub.Core.Pipeline.RustFastAccel.TryIsMostlyBlack(frame, out bool fastBlack))
                return fastBlack;

            int stepY = Math.Max(1, frame.Height / 16);
            int stepX = Math.Max(1, frame.Width / 16);
            int total = 0, nonBlack = 0;

            for (int y = 0; y < frame.Height; y += stepY)
            {
                int row = y * frame.Stride;
                for (int x = 0; x < frame.Width; x += stepX)
                {
                    int i = row + (x * 4);
                    total++;
                    if (frame.Pixels[i] > 8 || frame.Pixels[i + 1] > 8 || frame.Pixels[i + 2] > 8)
                        nonBlack++;
                }
            }

            return total > 0 && (nonBlack * 100) < total;
        }

        private Frame CopyPixels(int w, int h)
        {
            var pixels = new byte[w * h * 4];
            Marshal.Copy(_bits, pixels, 0, pixels.Length);
            for (int i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;
            return new Frame(pixels, w, h, w * 4);
        }

        private void EnsureBuffer(int w, int h)
        {
            if (w == _width && h == _height && _bitmap != 0)
                return;

            Release();
            nint screen = Win32.GetDC(0);
            try
            {
                _memDc = Win32.CreateCompatibleDC(screen);
                if (_memDc == 0) return;

                var bmi = new Win32.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<Win32.BitmapInfoHeader>(),
                    Width = w,
                    Height = -h, // top-down DIB
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                };
                _bitmap = Win32.CreateDIBSection(screen, ref bmi, 0, out _bits, 0, 0);
                if (_bitmap == 0 || _bits == 0)
                {
                    Release();
                    return;
                }
                _oldBitmap = Win32.SelectObject(_memDc, _bitmap);
                _width = w;
                _height = h;
            }
            finally
            {
                Win32.ReleaseDC(0, screen);
            }
        }

        private void Release()
        {
            if (_memDc != 0 && _oldBitmap != 0) Win32.SelectObject(_memDc, _oldBitmap);
            if (_bitmap != 0) Win32.DeleteObject(_bitmap);
            if (_memDc != 0) Win32.DeleteDC(_memDc);
            _memDc = _bitmap = _oldBitmap = _bits = 0;
            _width = _height = 0;
        }

        public void Dispose() => Release();
    }

    private static class Win32
    {
        public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct BitmapInfoHeader
        {
            public uint Size;
            public int Width, Height;
            public ushort Planes, BitCount;
            public uint Compression, SizeImage;
            public int XPelsPerMeter, YPelsPerMeter;
            public uint ClrUsed, ClrImportant;
        }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, nint lParam);
        [DllImport("user32.dll")] public static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] public static extern nint WindowFromPoint(Point point);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out Rect pvAttribute, int cbAttribute);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(nint hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] public static extern bool PrintWindow(nint hwnd, nint hdc, uint flags);
        [DllImport("user32.dll")] public static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(nint hwnd, nint hdc);
        [DllImport("gdi32.dll")] public static extern nint CreateCompatibleDC(nint hdc);
        [DllImport("gdi32.dll")] public static extern nint CreateDIBSection(nint hdc, ref BitmapInfoHeader info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")] public static extern nint SelectObject(nint hdc, nint obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(nint obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(nint hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(nint dst, int x, int y, int cx, int cy, nint src, int x1, int y1, int rop);
    }

    private static class LinuxCapture
    {
        public static List<WindowTarget> ListWindows()
        {
            var list = new List<WindowTarget>();
            try
            {
                var psi = new ProcessStartInfo("wmctrl", "-lG")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) return list;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(1500);

                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = line.Split((char[]?)null, 8, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 8) continue;
                    if (!long.TryParse(parts[0].Replace("0x", "", StringComparison.OrdinalIgnoreCase), System.Globalization.NumberStyles.HexNumber, null, out long wid))
                        continue;
                    if (!int.TryParse(parts[4], out int w) || !int.TryParse(parts[5], out int h))
                        continue;
                    if (w < MinWindowWidth || h < MinWindowHeight)
                        continue;
                    list.Add(new WindowTarget("X11", parts[7].Trim(), (nint)wid, w, h));
                }
            }
            catch { }
            return list;
        }

        public static Frame? CaptureWindow(nint windowId) =>
            RunCliPpmCapture("import", $"-window 0x{windowId:x} ppm:-");

        public static Frame? CaptureScreenRect(int x, int y, int w, int h)
        {
            // Wayland (CachyOS Hyprland/KDE/Sway) supports grim; X11 supports ImageMagick import
            bool isWayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
            return isWayland
                ? RunCliPpmCapture("grim", $"-g \"{x},{y} {w}x{h}\" -t ppm -")
                : RunCliPpmCapture("import", $"-window root -crop {w}x{h}+{x}+{y} ppm:-");
        }

        private static Frame? RunCliPpmCapture(string tool, string args)
        {
            try
            {
                var psi = new ProcessStartInfo(tool, args)
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc is null) return null;
                using var ms = new MemoryStream();
                proc.StandardOutput.BaseStream.CopyTo(ms);
                proc.WaitForExit(1000);
                if (proc.ExitCode != 0) return null;
                return ParsePpmToBgra(ms.ToArray());
            }
            catch
            {
                return null;
            }
        }

        private static Frame? ParsePpmToBgra(byte[] ppm)
        {
            if (ppm.Length < 12 || ppm[0] != 'P' || ppm[1] != '6')
                return null;

            int pos = 2;
            int NextToken()
            {
                while (pos < ppm.Length)
                {
                    if (ppm[pos] == '#')
                    {
                        while (pos < ppm.Length && ppm[pos] != '\n') pos++;
                    }
                    else if (char.IsWhiteSpace((char)ppm[pos]))
                    {
                        pos++;
                    }
                    else break;
                }
                int start = pos;
                while (pos < ppm.Length && !char.IsWhiteSpace((char)ppm[pos])) pos++;
                return int.Parse(Encoding.ASCII.GetString(ppm, start, pos - start));
            }

            int w = NextToken();
            int h = NextToken();
            int maxVal = NextToken();
            if (maxVal != 255 || w <= 0 || h <= 0) return null;
            pos++; // skip single whitespace after maxVal

            int rgbLen = w * h * 3;
            if (ppm.Length - pos < rgbLen) return null;

            var bgra = new byte[w * h * 4];
            for (int i = 0, j = pos; i < bgra.Length; i += 4, j += 3)
            {
                bgra[i] = ppm[j + 2];     // B
                bgra[i + 1] = ppm[j + 1]; // G
                bgra[i + 2] = ppm[j];     // R
                bgra[i + 3] = 255;
            }
            return new Frame(bgra, w, h, w * 4);
        }
    }
}
