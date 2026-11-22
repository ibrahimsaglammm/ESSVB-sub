// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using EssvbSub.Core.Models;

namespace EssvbSub.Capture.PlayStation;

public sealed record DiscoveredConsole(
    string Host,
    string Name,
    bool IsAwake,
    ConsoleKind Kind,
    string? RunningTitleId = null,
    string? RunningAppName = null);

public sealed record RegisteredPlayStation(
    ConsoleKind Kind,
    string Host,
    string Name,
    byte[] RegistKey,
    byte[] Morning)
{
    public ulong? WakeCredential
    {
        get
        {
            string hex = Encoding.ASCII.GetString(RegistKey).TrimEnd('\0');
            return ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong val) ? val : null;
        }
    }
}

/// <summary>
/// Manages PS5 and PS4 Pro discovery, PIN registration, wake-on-LAN, and background H.264 frame streaming
/// via libchiaki (essvb_rp native library).
/// </summary>
public sealed unsafe class PsRemotePlaySession : IDisposable
{
    private readonly RegisteredPlayStation _console;
    private readonly byte[] _accountId;
    private readonly int _maxFps;
    private readonly int _videoHeight;
    private readonly object _gate = new();

    private nint _nativeSession;
    private GCHandle _selfHandle;
    private bool _running;
    private bool _disposed;

    public PsRemotePlaySession(RegisteredPlayStation console, byte[] accountId, int maxFps = 12, int videoHeight = 1080)
    {
        if (accountId.Length != 8)
            throw new ArgumentException("PSN Account ID must be 8 bytes.", nameof(accountId));
        _console = console;
        _accountId = (byte[])accountId.Clone();
        _maxFps = Math.Clamp(maxFps, 2, 20);
        _videoHeight = videoHeight == 720 ? 720 : 1080;
    }

    public event Action<Frame>? FrameReceived;
    public event Action<string>? StateChanged;
    public event Action<string>? DiagnosticLogged;

    public static bool IsNativeAvailable => PsRemotePlayNative.IsNativeAvailable;

    public static Task<IReadOnlyList<DiscoveredConsole>> DiscoverAsync(ConsoleKind kind, string? targetHost = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        int durationMs = (int)(timeout ?? TimeSpan.FromSeconds(2.5)).TotalMilliseconds;
        return Task.Factory.StartNew(() => DiscoverSync(kind, targetHost, durationMs), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static IReadOnlyList<DiscoveredConsole> DiscoverSync(ConsoleKind kind, string? targetHost, int timeoutMs)
    {
        if (!IsNativeAvailable)
            return [];

        PsRemotePlayNative.essvb_init();
        var targets = string.IsNullOrWhiteSpace(targetHost) ? GetLocalLanBroadcastTargets() : [targetHost];
        var list = new Dictionary<string, DiscoveredConsole>(StringComparer.Ordinal);
        var handle = GCHandle.Alloc(list);
        var ptrs = new nint[targets.Count];

        try
        {
            for (int i = 0; i < targets.Count; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(targets[i]);

            delegate* unmanaged[Cdecl]<byte*, byte*, int, int, byte*, byte*, nint, void> cb = &OnDiscover;
            fixed (nint* pTargets = ptrs)
            {
                PsRemotePlayNative.essvb_discover(
                    PsRemotePlayNative.ToNativeKind(kind),
                    pTargets,
                    ptrs.Length,
                    timeoutMs,
                    (nint)cb,
                    GCHandle.ToIntPtr(handle));
            }
        }
        finally
        {
            foreach (var p in ptrs) Marshal.FreeCoTaskMem(p);
            handle.Free();
        }

        lock (list)
            return [.. list.Values.OrderBy(c => c.Host, StringComparer.Ordinal)];
    }

    public static Task<RegisteredPlayStation> RegisterAsync(ConsoleKind kind, string host, byte[] accountId, string pinText, CancellationToken ct = default)
    {
        string digits = new(pinText.Where(char.IsDigit).ToArray());
        if (digits.Length != 8 || !uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out uint pin))
            throw new ArgumentException("Eşleştirme kodu 8 haneli olmalıdır.", nameof(pinText));
        if (accountId.Length != 8)
            throw new ArgumentException("PSN hesap kimliği geçersiz.", nameof(accountId));
        if (!IsNativeAvailable)
            throw new DllNotFoundException("essvb_rp yerel kütüphanesi bulunamadı. CMake ile derleyin veya Pencere Yakalama modunu kullanın.");

        return Task.Factory.StartNew(() =>
        {
            PsRemotePlayNative.essvb_init();
            PsRemotePlayNative.RegisteredConsoleNative outReg;
            uint reason = 0;
            int res;

            fixed (byte* pAcc = accountId)
            {
                res = PsRemotePlayNative.essvb_register(
                    PsRemotePlayNative.ToNativeKind(kind),
                    host,
                    pAcc,
                    pin,
                    &outReg,
                    &reason,
                    0,
                    0);
            }

            if (res != 0)
                throw new InvalidOperationException($"Konsol eşleştirme başarısız (kod={res}, sebep=0x{reason:x8}).");

            string nick = Marshal.PtrToStringUTF8((nint)outReg.Nickname) ?? (kind == ConsoleKind.PlayStation5 ? "PS5" : "PS4 Pro");
            var regKey = new ReadOnlySpan<byte>(outReg.RegistKey, 0x10).ToArray();
            var morning = new ReadOnlySpan<byte>(outReg.Morning, 0x10).ToArray();
            return new RegisteredPlayStation(kind, host, nick, regKey, morning);
        }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running) return;
            _running = true;
        }

        if (!IsNativeAvailable)
        {
            StateChanged?.Invoke("Yerel libchiaki kütüphanesi (essvb_rp) yok — PS Remote Play / chiaki-ng penceresi kullanın.");
            return;
        }

        StateChanged?.Invoke($"{_console.Name} bağlanıyor…");
        _ = Task.Run(StartNativeSession);
    }

    private void StartNativeSession()
    {
        try
        {
            PsRemotePlayNative.essvb_init();
            if (_console.WakeCredential is { } cred)
                PsRemotePlayNative.essvb_wake(PsRemotePlayNative.ToNativeKind(_console.Kind), _console.Host, cred);

            lock (_gate)
            {
                if (!_running || _disposed) return;
                _selfHandle = GCHandle.Alloc(this);
                delegate* unmanaged[Cdecl]<byte*, int, int, int, nint, void> frameCb = &OnFrame;
                delegate* unmanaged[Cdecl]<int, int, byte*, nint, void> eventCb = &OnEvent;
                delegate* unmanaged[Cdecl]<int, byte*, nint, void> logCb = &OnLog;

                fixed (byte* pReg = _console.RegistKey)
                fixed (byte* pMorn = _console.Morning)
                fixed (byte* pAcc = _accountId)
                {
                    _nativeSession = PsRemotePlayNative.essvb_session_start(
                        PsRemotePlayNative.ToNativeKind(_console.Kind),
                        _console.Host,
                        pReg,
                        pMorn,
                        pAcc,
                        _maxFps,
                        _videoHeight,
                        (nint)frameCb,
                        (nint)eventCb,
                        (nint)logCb,
                        GCHandle.ToIntPtr(_selfHandle));
                }

                if (_nativeSession == 0)
                {
                    _selfHandle.Free();
                    _selfHandle = default;
                    StateChanged?.Invoke("PlayStation oturumu başlatılamadı.");
                }
            }
        }
        catch (Exception ex)
        {
            StateChanged?.Invoke($"PlayStation bağlantı hatası: {ex.Message}");
        }
    }

    public void Stop()
    {
        nint sess;
        GCHandle handle;
        lock (_gate)
        {
            _running = false;
            sess = _nativeSession;
            _nativeSession = 0;
            handle = _selfHandle;
            _selfHandle = default;
        }

        if (sess != 0)
            PsRemotePlayNative.essvb_session_stop(sess);
        if (handle.IsAllocated)
            handle.Free();

        StateChanged?.Invoke("Durduruldu");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Stop();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDiscover(byte* host, byte* name, int awake, int isPs5, byte* titleId, byte* appName, nint user)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not Dictionary<string, DiscoveredConsole> dict) return;
            string? addr = Marshal.PtrToStringUTF8((nint)host);
            if (string.IsNullOrEmpty(addr)) return;
            string devName = Marshal.PtrToStringUTF8((nint)name) ?? (isPs5 != 0 ? "PS5" : "PS4 Pro");
            string? tId = Marshal.PtrToStringUTF8((nint)titleId);
            string? app = Marshal.PtrToStringUTF8((nint)appName);
            lock (dict)
            {
                dict[addr] = new DiscoveredConsole(addr, devName, awake != 0, isPs5 != 0 ? ConsoleKind.PlayStation5 : ConsoleKind.PlayStation4Pro, tId, app);
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFrame(byte* bgra, int width, int height, int stride, nint user)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not PsRemotePlaySession self) return;
            var frame = Frame.CopyFromPointer((nint)bgra, width, height, stride);
            self.FrameReceived?.Invoke(frame);
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnEvent(int ev, int quitReason, byte* reasonText, nint user)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not PsRemotePlaySession self) return;
            if (ev == PsRemotePlayNative.EventConnected)
            {
                self.StateChanged?.Invoke("Bağlı (Akış Aktif)");
            }
            else if (ev == PsRemotePlayNative.EventQuit)
            {
                string detail = Marshal.PtrToStringUTF8((nint)reasonText) ?? $"Kod {quitReason}";
                self.StateChanged?.Invoke($"Bağlantı kesildi: {detail}");
            }
        }
        catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnLog(int level, byte* msg, nint user)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not PsRemotePlaySession self) return;
            string? text = Marshal.PtrToStringUTF8((nint)msg);
            if (text is not null)
                self.DiagnosticLogged?.Invoke($"[rp-{level}] {text}");
        }
        catch { }
    }

    private static List<string> GetLocalLanBroadcastTargets()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var uni in nic.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(uni.Address))
                        continue;
                    byte[] ip = uni.Address.GetAddressBytes();
                    byte[] mask = uni.IPv4Mask?.GetAddressBytes() ?? [];
                    if (mask.Length == 4)
                    {
                        var bcast = new IPAddress([
                            (byte)(ip[0] | ~mask[0]),
                            (byte)(ip[1] | ~mask[1]),
                            (byte)(ip[2] | ~mask[2]),
                            (byte)(ip[3] | ~mask[3])
                        ]);
                        list.Add(bcast.ToString());
                    }
                }
            }
        }
        catch { }
        list.Add("255.255.255.255");
        return [.. list.Distinct()];
    }
}
