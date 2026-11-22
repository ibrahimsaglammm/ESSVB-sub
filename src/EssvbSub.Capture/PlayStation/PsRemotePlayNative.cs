// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EssvbSub.Core.Models;

namespace EssvbSub.Capture.PlayStation;

internal static unsafe partial class PsRemotePlayNative
{
    private const string LibName = "essvb_rp";

    internal const int ConsolePs5 = 0;
    internal const int ConsolePs4Pro = 1;

    internal const int EventConnected = 0;
    internal const int EventQuit = 1;
    internal const int EventLoginPin = 2;

    static PsRemotePlayNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(PsRemotePlayNative).Assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != LibName)
            return 0;

        string[] candidates = OperatingSystem.IsWindows()
            ? ["essvb_rp.dll"]
            : ["libessvb_rp.so", "essvb_rp.so"];

        foreach (var file in candidates)
        {
            if (NativeLibrary.TryLoad(file, assembly, path, out nint handle))
                return handle;

            string baseCandidate = Path.Combine(AppContext.BaseDirectory, file);
            if (File.Exists(baseCandidate) && NativeLibrary.TryLoad(baseCandidate, out handle))
                return handle;
        }
        return 0;
    }

    public static bool IsNativeAvailable
    {
        get
        {
            string file = OperatingSystem.IsWindows() ? "essvb_rp.dll" : "libessvb_rp.so";
            if (NativeLibrary.TryLoad(file, typeof(PsRemotePlayNative).Assembly, null, out nint h))
            {
                NativeLibrary.Free(h);
                return true;
            }
            return File.Exists(Path.Combine(AppContext.BaseDirectory, file));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RegisteredConsoleNative
    {
        public fixed byte Nickname[0x20];
        public fixed byte RegistKey[0x10];
        public fixed byte Morning[0x10];
    }

    internal static int ToNativeKind(ConsoleKind kind) => kind == ConsoleKind.PlayStation5 ? ConsolePs5 : ConsolePs4Pro;

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int essvb_init();

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int essvb_discover(int consoleKind, nint* targets, int targetCount, int timeoutMs, nint callback, nint user);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int essvb_wake(int consoleKind, string host, ulong credential);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int essvb_register(int consoleKind, string host, byte* accountId, uint pin,
        RegisteredConsoleNative* output, uint* reason, nint log, nint user);

    [LibraryImport(LibName, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint essvb_session_start(int consoleKind, string host, byte* registKey, byte* morning,
        byte* accountId, int maxFps, int videoHeight, nint frame, nint eventCallback, nint log, nint user);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int essvb_session_set_login_pin(nint session, byte* pin, nuint pinSize);

    [LibraryImport(LibName)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void essvb_session_stop(nint session);
}
