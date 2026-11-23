// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;

namespace EssvbSub.Core.Server;

public sealed record PairingTicket(string Id, string PinCode, string ClientAddress, DateTimeOffset ExpiresAt);

/// <summary>
/// Hardened local-network SSE subtitle server for Samsung Tizen TV and OBS browser overlays.
/// Zero legacy unauthenticated bypasses: every /events and /obs stream requires a valid cryptographic token
/// obtained through explicit 6-digit PIN pairing.
/// </summary>
public sealed class SecureSubtitleServer : ISubtitleSink, IDisposable
{
    public const int DefaultPort = 8787;
    public const int MaxConcurrentConnections = 16;
    private const int MaxHeaderBytes = 16 * 1024;
    private static readonly TimeSpan HeaderReadTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PairingLifetime = TimeSpan.FromSeconds(90);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private sealed class ClientConnection(TcpClient tcp, bool isObserver)
    {
        public TcpClient Tcp { get; } = tcp;
        public bool IsObserver { get; } = isObserver;
        public Stream Stream { get; } = tcp.GetStream();
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
    }

    private enum PairingState { Pending, Approved, Rejected }

    private sealed class ActiveTicket(PairingTicket ticket)
    {
        public PairingTicket Ticket { get; } = ticket;
        public PairingState State { get; set; } = PairingState.Pending;
    }

    private readonly object _gate = new();
    private readonly List<ClientConnection> _clients = [];
    private readonly SpeakerColorBook _speakerBook = new();
    private readonly int _port;
    private readonly TimeSpan _pingInterval;

    private string _accessToken;
    private string _instanceId;
    private TcpListener? _listener;
    private Timer? _pingTimer;
    private SubtitleStyle _currentStyle = new();
    private ActiveTicket? _pendingTicket;
    private int _activeSockets;
    private bool _disposed;

    public SecureSubtitleServer(int port = DefaultPort, string? accessToken = null, string? instanceId = null, TimeSpan? pingInterval = null)
    {
        _port = port;
        _accessToken = string.IsNullOrWhiteSpace(accessToken) ? GenerateToken() : accessToken;
        _instanceId = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("D") : instanceId;
        _pingInterval = pingInterval ?? TimeSpan.FromSeconds(15);
    }

    public string AccessToken { get { lock (_gate) return _accessToken; } }
    public string InstanceId => _instanceId;
    public int ListeningPort { get; private set; }
    public bool SendsToTv { get; set; } = true;
    public SpeakerColorBook SpeakerBook => _speakerBook;

    public int ConnectedTvCount
    {
        get { lock (_gate) return _clients.Count(c => !c.IsObserver); }
    }

    public event Action<int>? TvClientsChanged;
    public event Action<SubtitleEvent>? EventBroadcast;
    public event Action<PairingTicket>? PairingRequested;

    public static string GenerateToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>
    /// Accepts only true LAN / Loopback peers (RFC1918, Loopback, ULA IPv6, Link-Local).
    /// Explicitly excludes CGNAT (100.64.0.0/10) to prevent ISP shared-subnet exposure.
    /// </summary>
    public static bool IsTrustedLanPeer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        byte[] b = address.GetAddressBytes();
        if (b.Length == 16)
        {
            // Unique Local IPv6 (fc00::/7) or Link-Local (fe80::/10)
            return (b[0] & 0xFE) == 0xFC || (b[0] == 0xFE && (b[1] & 0xC0) == 0x80);
        }

        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }

    public Task<int> StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null)
                return Task.FromResult(ListeningPort);

            var listener = new TcpListener(IPAddress.Any, _port);
            listener.Start();
            _listener = listener;
            ListeningPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            _pingTimer = new Timer(_ => BroadcastFrame("ping", new()), null, _pingInterval, _pingInterval);
            _ = AcceptLoopAsync(listener);
            return Task.FromResult(ListeningPort);
        }
    }

    public void RotateToken(string? newToken = null)
    {
        List<ClientConnection> toClose;
        lock (_gate)
        {
            _accessToken = string.IsNullOrWhiteSpace(newToken) ? GenerateToken() : newToken;
            _pendingTicket = null;
            toClose = [.. _clients];
            _clients.Clear();
        }
        foreach (var client in toClose)
            client.Tcp.Dispose();
        TvClientsChanged?.Invoke(0);
    }

    public bool ResolvePairing(string ticketId, bool approve)
    {
        lock (_gate)
        {
            if (_pendingTicket is null || _pendingTicket.Ticket.Id != ticketId)
                return false;
            if (DateTimeOffset.UtcNow > _pendingTicket.Ticket.ExpiresAt)
            {
                _pendingTicket = null;
                return false;
            }
            _pendingTicket.State = approve ? PairingState.Approved : PairingState.Rejected;
            return true;
        }
    }

    public void SendSubtitle(string text, string sourceText, int durationMs = 15000, TextBox? box = null, bool replaces = false)
    {
        var payload = new Dictionary<string, object>
        {
            ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["duration"] = durationMs,
            ["text"] = text
        };
        if (replaces) payload["replace"] = true;
        if (box is { } b)
        {
            payload["box"] = new Dictionary<string, double>
            {
                ["left"] = Math.Round(b.Left, 4),
                ["top"] = Math.Round(b.Top, 4),
                ["right"] = Math.Round(b.Right, 4),
                ["bottom"] = Math.Round(b.Bottom, 4)
            };
        }

        List<SpeakerColor>? speakers = null;
        if (_currentStyle.SpeakerColors && SpeakerPrefix.Parse(text) is { } split)
        {
            var hex = _speakerBook.GetColor(split.Name);
            speakers = [new SpeakerColor(split.Name, hex)];
            payload["speakers"] = new[] { new { name = split.Name, color = hex } };
        }

        BroadcastFrame("subtitle", payload, subtitleOnly: true);
        EventBroadcast?.Invoke(new SubtitleEvent(SubtitleEventKind.Subtitle, text, sourceText, durationMs, box, replaces, speakers));
    }

    public void Clear()
    {
        BroadcastFrame("clear", new(), subtitleOnly: true);
        EventBroadcast?.Invoke(SubtitleEvent.Cleared);
    }

    public void SendStyle(SubtitleStyle style)
    {
        lock (_gate) _currentStyle = style;
        BroadcastFrame("style", BuildStyleMap(style), subtitleOnly: true);
        EventBroadcast?.Invoke(new SubtitleEvent(SubtitleEventKind.Style));
    }

    public void SendAlert(string? message)
    {
        string text = message?.Trim() ?? "";
        BroadcastFrame("alert", new Dictionary<string, object> { ["text"] = text }, subtitleOnly: true);
        EventBroadcast?.Invoke(new SubtitleEvent(SubtitleEventKind.StatusAlert, text));
    }

    public void Stop()
    {
        List<ClientConnection> closing;
        lock (_gate)
        {
            _pingTimer?.Dispose();
            _pingTimer = null;
            _listener?.Stop();
            _listener = null;
            _pendingTicket = null;
            closing = [.. _clients];
            _clients.Clear();
        }
        foreach (var client in closing)
            client.Tcp.Dispose();
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

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (true)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            if (tcp.Client.RemoteEndPoint is not IPEndPoint remote || !IsTrustedLanPeer(remote.Address))
            {
                tcp.Dispose();
                continue;
            }

            if (Interlocked.Increment(ref _activeSockets) > MaxConcurrentConnections)
            {
                Interlocked.Decrement(ref _activeSockets);
                _ = SendQuickErrorAndDisposeAsync(tcp, "503 Service Unavailable");
                continue;
            }

            _ = HandleClientAsync(tcp, remote.Address);
        }
    }

    private async Task HandleClientAsync(TcpClient tcp, IPAddress peerAddress)
    {
        bool handedOffToStream = false;
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            var head = await ReadHttpHeadAsync(stream).ConfigureAwait(false);
            if (head is null)
            {
                await WriteResponseAsync(tcp, "400 Bad Request").ConfigureAwait(false);
                return;
            }

            var req = ParseRequest(head);
            if (req is null)
            {
                await WriteResponseAsync(tcp, "400 Bad Request").ConfigureAwait(false);
                return;
            }

            if (!IsOriginAllowed(req))
            {
                await WriteResponseAsync(tcp, "403 Forbidden").ConfigureAwait(false);
                return;
            }

            string cors = BuildCorsHeaders(req.Origin);

            if (req.Method == "OPTIONS")
            {
                await WriteResponseAsync(tcp, "204 No Content", "", cors +
                    "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                    "Access-Control-Allow-Headers: *\r\n" +
                    "Access-Control-Allow-Private-Network: true\r\n").ConfigureAwait(false);
                return;
            }

            if (req.Method == "GET" && req.Path == "/hello")
            {
                var body = JsonSerializer.Serialize(new
                {
                    app = "essvb-sub",
                    version = "1.0.0",
                    auth = "required",
                    id = _instanceId,
                    tvs = ConnectedTvCount
                });
                await WriteResponseAsync(tcp, "200 OK", body, cors, "application/json; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (req.Path == "/pair")
            {
                await HandlePairingAsync(tcp, req, peerAddress, cors).ConfigureAwait(false);
                return;
            }

            // Strict mandatory token check for /events and /obs
            if (!AuthorizeToken(req))
            {
                await WriteResponseAsync(tcp, "401 Unauthorized", "{\"error\":\"invalid_or_missing_token\"}", cors, "application/json").ConfigureAwait(false);
                return;
            }

            if (req.Method == "GET" && req.Path == "/obs")
            {
                await WriteResponseAsync(tcp, "200 OK", ObsOverlayHtml, cors, "text/html; charset=utf-8").ConfigureAwait(false);
                return;
            }

            if (req.Method == "GET" && req.Path == "/events")
            {
                bool isObserver = req.Query("viewer") == "obs";
                handedOffToStream = true;
                await OpenSseStreamAsync(tcp, isObserver, cors).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(tcp, "404 Not Found", "", cors).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
        }
        finally
        {
            if (!handedOffToStream)
                Interlocked.Decrement(ref _activeSockets);
        }
    }

    private async Task HandlePairingAsync(TcpClient tcp, ParsedHttpRequest req, IPAddress peerAddress, string cors)
    {
        if (req.Method == "POST")
        {
            PairingTicket created;
            lock (_gate)
            {
                var now = DateTimeOffset.UtcNow;
                if (_pendingTicket is not null &&
                    _pendingTicket.State == PairingState.Pending &&
                    now < _pendingTicket.Ticket.ExpiresAt)
                {
                    // Single pending ticket per server at a time to stop flood attacks
                    created = _pendingTicket.Ticket;
                }
                else
                {
                    string pin = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
                    created = new PairingTicket(
                        Guid.NewGuid().ToString("N")[..12],
                        pin,
                        peerAddress.ToString(),
                        now.Add(PairingLifetime));
                    _pendingTicket = new ActiveTicket(created);
                    PairingRequested?.Invoke(created);
                }
            }

            var json = JsonSerializer.Serialize(new { id = created.Id, code = created.PinCode });
            await WriteResponseAsync(tcp, "200 OK", json, cors, "application/json; charset=utf-8").ConfigureAwait(false);
            return;
        }

        if (req.Method == "GET")
        {
            string? ticketId = req.Query("id");
            string state = "expired";
            string? token = null;

            lock (_gate)
            {
                if (_pendingTicket is not null &&
                    _pendingTicket.Ticket.Id == ticketId &&
                    _pendingTicket.Ticket.ClientAddress == peerAddress.ToString())
                {
                    if (DateTimeOffset.UtcNow > _pendingTicket.Ticket.ExpiresAt)
                    {
                        _pendingTicket = null;
                        state = "expired";
                    }
                    else if (_pendingTicket.State == PairingState.Approved)
                    {
                        state = "approved";
                        token = _accessToken;
                        _pendingTicket = null; // One-time token consumption
                    }
                    else if (_pendingTicket.State == PairingState.Rejected)
                    {
                        state = "rejected";
                        _pendingTicket = null;
                    }
                    else
                    {
                        state = "pending";
                    }
                }
            }

            var json = JsonSerializer.Serialize(new { state, token });
            await WriteResponseAsync(tcp, "200 OK", json, cors, "application/json; charset=utf-8").ConfigureAwait(false);
            return;
        }

        await WriteResponseAsync(tcp, "405 Method Not Allowed", "", cors).ConfigureAwait(false);
    }

    private async Task OpenSseStreamAsync(TcpClient tcp, bool isObserver, string cors)
    {
        var headers = new StringBuilder()
            .Append("HTTP/1.1 200 OK\r\n")
            .Append("Content-Type: text/event-stream; charset=utf-8\r\n")
            .Append("Cache-Control: no-cache\r\n")
            .Append("Connection: keep-alive\r\n")
            .Append(cors)
            .Append("\r\n")
            .Append("retry: 2000\n\n");

        SubtitleStyle styleSnapshot;
        var conn = new ClientConnection(tcp, isObserver);
        int tvCount;

        lock (_gate)
        {
            styleSnapshot = _currentStyle;
            _clients.Add(conn);
            tvCount = _clients.Count(c => !c.IsObserver);
        }

        headers.Append(FormatFrame("style", BuildStyleMap(styleSnapshot)));
        await WriteClientAsync(conn, headers.ToString()).ConfigureAwait(false);

        if (!isObserver)
            TvClientsChanged?.Invoke(tvCount);

        var buffer = new byte[256];
        try
        {
            while (await conn.Stream.ReadAsync(buffer).ConfigureAwait(false) > 0) { }
        }
        catch { }
        finally
        {
            RemoveClient(conn);
            Interlocked.Decrement(ref _activeSockets);
        }
    }

    private void RemoveClient(ClientConnection conn)
    {
        int tvCount;
        bool removed;
        lock (_gate)
        {
            removed = _clients.Remove(conn);
            tvCount = _clients.Count(c => !c.IsObserver);
        }
        conn.Tcp.Dispose();
        if (removed && !conn.IsObserver)
            TvClientsChanged?.Invoke(tvCount);
    }

    private void BroadcastFrame(string eventName, Dictionary<string, object> data, bool subtitleOnly = false)
    {
        string frame = FormatFrame(eventName, data);
        List<ClientConnection> targets;
        lock (_gate)
        {
            targets = [.. _clients.Where(c => !subtitleOnly || SendsToTv || c.IsObserver)];
        }

        foreach (var client in targets)
        {
            _ = WriteClientAsync(client, frame).ContinueWith(_ => RemoveClient(client), TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static async Task WriteClientAsync(ClientConnection conn, string payload)
    {
        await conn.WriteLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await conn.Stream.WriteAsync(Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
            await conn.Stream.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            conn.WriteLock.Release();
        }
    }

    private static Dictionary<string, object> BuildStyleMap(SubtitleStyle style) => new()
    {
        ["fontSize"] = style.FontSize,
        ["bottomPercent"] = style.BottomPercent,
        ["placement"] = style.Placement.ToString().ToLowerInvariant(),
        ["background"] = Math.Clamp(style.BackgroundOpacity, 0, 100),
        ["backgroundShape"] = style.BackgroundShape.ToString().ToLowerInvariant(),
        ["bold"] = style.Bold,
        ["textColor"] = style.TextColor.ToString().ToLowerInvariant(),
        ["stackPrevious"] = style.StackPrevious
    };

    private static string FormatFrame(string eventName, Dictionary<string, object> data) =>
        $"event: {eventName}\ndata: {JsonSerializer.Serialize(data, JsonOptions)}\n\n";

    private bool AuthorizeToken(ParsedHttpRequest req)
    {
        string? supplied = req.Query("k");
        if (string.IsNullOrEmpty(supplied) && req.BearerToken is { Length: > 0 } bearer)
            supplied = bearer;

        if (string.IsNullOrEmpty(supplied))
            return false;

        lock (_gate)
        {
            var expectedBytes = Encoding.UTF8.GetBytes(_accessToken);
            var actualBytes = Encoding.UTF8.GetBytes(supplied);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
    }

    private static bool ContainsControlChars(string? value) =>
        value is not null && value.Any(c => c < 0x20 || c == 0x7F);

    private static bool IsOriginAllowed(ParsedHttpRequest req)
    {
        if (ContainsControlChars(req.Origin))
            return false;

        if (req.Origin is null or "null" or "file://")
            return true;

        if (Uri.TryCreate(req.Origin, UriKind.Absolute, out var originUri))
        {
            if (originUri.Scheme is "app")
                return true;
            if (originUri.Scheme is "http" && !string.IsNullOrEmpty(req.Host) &&
                Uri.TryCreate("http://" + req.Host, UriKind.Absolute, out var hostUri))
            {
                return originUri.Host.Equals(hostUri.Host, StringComparison.OrdinalIgnoreCase) &&
                       originUri.Port == hostUri.Port;
            }
        }
        return false;
    }

    private static string BuildCorsHeaders(string? origin) =>
        !ContainsControlChars(origin) &&
        (origin is "null" or "file://" || (origin is not null && origin.StartsWith("app://", StringComparison.OrdinalIgnoreCase)))
            ? $"Access-Control-Allow-Origin: {origin}\r\nVary: Origin\r\n"
            : "";

    private static async Task<string?> ReadHttpHeadAsync(Stream stream)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(HeaderReadTimeout);

        while (ms.Length < MaxHeaderBytes)
        {
            int read = await stream.ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            if (read == 0) return null;
            ms.Write(buffer, 0, read);
            string text = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (idx >= 0) return text[..idx];
        }
        return null;
    }

    private static async Task SendQuickErrorAndDisposeAsync(TcpClient tcp, string status)
    {
        try { await WriteResponseAsync(tcp, status).ConfigureAwait(false); }
        catch { tcp.Dispose(); }
    }

    private static async Task WriteResponseAsync(TcpClient tcp, string status, string body = "", string extraHeaders = "", string contentType = "text/plain; charset=utf-8")
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status}\r\n{extraHeaders}Content-Type: {contentType}\r\nCache-Control: no-store\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        if (bodyBytes.Length > 0)
            await stream.WriteAsync(bodyBytes).ConfigureAwait(false);
        tcp.Dispose();
    }

    private sealed record ParsedHttpRequest(string Method, string Target, string Path, string? Origin, string Host, string? BearerToken)
    {
        public string? Query(string key)
        {
            int q = Target.IndexOf('?');
            if (q < 0) return null;
            foreach (var part in Target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split('=', 2);
                if (Uri.UnescapeDataString(kv[0]) == key)
                    return kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "";
            }
            return null;
        }
    }

    private static ParsedHttpRequest? ParseRequest(string head)
    {
        var lines = head.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !parts[1].StartsWith('/'))
            return null;

        string? origin = null, host = null, bearer = null;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            string name = line[..colon].Trim();
            string val = line[(colon + 1)..].Trim();
            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = val;
            else if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) host = val;
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) &&
                     val.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                bearer = val[7..].Trim();
            }
        }

        string path = parts[1].Split('?')[0];
        return new ParsedHttpRequest(parts[0], parts[1], path, origin, host ?? "", bearer);
    }

    private const string ObsOverlayHtml = """
        <!doctype html>
        <html lang="tr">
        <head>
          <meta charset="utf-8">
          <title>ESSVB-sub OBS Overlay</title>
          <style>
            html, body { margin:0; width:100%; height:100%; overflow:hidden; background:transparent; font-family:"Segoe UI", Inter, sans-serif; color:#fff; }
            #wrap { position:absolute; left:8%; right:8%; bottom:7%; text-align:center; }
            #text { display:inline-block; padding:10px 24px; border-radius:12px; background:rgba(12,14,18,0.88); font-size:44px; font-weight:600; text-shadow:0 2px 4px #000; white-space:pre-line; }
            .hidden { display:none !important; }
          </style>
        </head>
        <body>
          <div id="wrap" class="hidden"><span id="text"></span></div>
          <script>
            var k = new URLSearchParams(location.search).get('k') || '';
            var es = new EventSource('/events?viewer=obs&k=' + encodeURIComponent(k));
            var wrap = document.getElementById('wrap');
            var text = document.getElementById('text');
            es.addEventListener('subtitle', function(e) {
              var d = JSON.parse(e.data);
              text.textContent = d.text || '';
              wrap.className = d.text ? '' : 'hidden';
            });
            es.addEventListener('clear', function() { wrap.className = 'hidden'; });
          </script>
        </body>
        </html>
        """;
}
