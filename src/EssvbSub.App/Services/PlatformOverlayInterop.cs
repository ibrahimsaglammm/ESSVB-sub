// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace EssvbSub.App.Services;

/// <summary>
/// Applies native click-through and non-activating always-on-top flags to the transparent overlay window
/// on Windows 11 (Win32) and CachyOS Linux (X11/XWayland XShape).
/// </summary>
public static class PlatformOverlayInterop
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;

    public static void SetClickThrough(Window window, bool clickThrough)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == 0)
            return;

        if (OperatingSystem.IsWindows())
        {
            ApplyWindows(handle.Handle, clickThrough);
        }
        else if (OperatingSystem.IsLinux())
        {
            ApplyLinuxX11(handle.Handle, clickThrough, (int)Math.Max(1, window.Bounds.Width), (int)Math.Max(1, window.Bounds.Height));
        }
    }

    private static void ApplyWindows(nint hwnd, bool clickThrough)
    {
        long style = (long)GetWindowLongPtrW(hwnd, GwlExStyle);
        long target = style | WsExToolWindow | WsExNoActivate;
        target = clickThrough ? (target | WsExTransparent) : (target & ~WsExTransparent);
        if (target != style)
            SetWindowLongPtrW(hwnd, GwlExStyle, (nint)target);
    }

    private static void ApplyLinuxX11(nint windowId, bool clickThrough, int width, int height)
    {
        try
        {
            nint display = XOpenDisplay(0);
            if (display == 0) return;
            try
            {
                const int shapeInput = 2;
                const int shapeSet = 0;
                if (clickThrough)
                {
                    // Empty input shape makes pointer events pass straight through to the game below
                    XShapeCombineRectangles(display, windowId, shapeInput, 0, 0, 0, 0, shapeSet, 0);
                }
                else
                {
                    var rect = new XRectangle { x = 0, y = 0, width = (ushort)width, height = (ushort)height };
                    XShapeCombineRectangles(display, windowId, shapeInput, 0, 0, ref rect, 1, shapeSet, 0);
                }
                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            // Pure Wayland compositor or missing Xext: graceful no-op
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XRectangle
    {
        public short x, y;
        public ushort width, height;
    }

    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRectangles(nint dpy, nint dest, int destKind, int xOff, int yOff, ref XRectangle rects, int nRects, int op, int ordering);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRectangles(nint dpy, nint dest, int destKind, int xOff, int yOff, nint rects, int nRects, int op, int ordering);
}
