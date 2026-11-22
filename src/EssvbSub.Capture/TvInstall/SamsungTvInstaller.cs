// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Formats.Asn1;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EssvbSub.Core.Models;
using EssvbSub.Core.Storage;
using TizenSdb;
using TizenSdb.SdbClient;

namespace EssvbSub.Capture.TvInstall;

public sealed record SamsungTvDeviceInfo(
    string Host,
    string Name,
    string? ModelName,
    string? DeveloperMode,
    string? DeveloperIp,
    bool SdbReachable = false);

public sealed record SamsungAccountToken(string AccessToken, string UserId, string Email);

/// <summary>
/// Discovers Samsung Tizen TVs (including 65" CU8500 Tizen 7.0, MU9000 Tizen 3.0, and Tizen 2.4+ models),
/// handles Samsung Developer Certificate enrollment, pushes device-profile.xml for Tizen 2.4-4.0,
/// and installs + launches ESSVB-sub TV widget directly over SDB (port 26101).
/// </summary>
public sealed class SamsungTvInstaller
{
    public const int SdbPort = 26101;
    public const int TvApiPort = 8001;
    public const string AppId = "EssvbSubT.EssvbSub";
    public const string PackageId = "EssvbSubT";

    private const string AuthorCertUrl = "https://svdca.samsungqbe.com/apis/v3/authors";
    private const string DistProfileUrl = "https://svdca.samsungqbe.com/apis/v1/distributors";
    private const string DistCertUrl = "https://svdca.samsungqbe.com/apis/v3/distributors";

    private static readonly HttpClient LanHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(3) };

    private static readonly HttpClient CloudHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<SamsungTvDeviceInfo?> ProbeTvAsync(string host, CancellationToken ct = default)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMilliseconds(1500));
            string json = await LanHttp.GetStringAsync($"http://{host}:{TvApiPort}/api/v2/", limit.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("device", out var dev))
                return null;

            string? Str(string prop) => dev.TryGetProperty(prop, out var v) ? v.ToString().Trim() : null;
            return new SamsungTvDeviceInfo(
                host,
                Str("name") ?? "Samsung TV",
                Str("modelName"),
                Str("developerMode"),
                Str("developerIP"));
        }
        catch
        {
            return null;
        }
    }

    public static async Task<IReadOnlyList<SamsungTvDeviceInfo>> DiscoverTvsOnLanAsync(CancellationToken ct = default)
    {
        var candidates = EnumerateLanCandidateIps();
        if (candidates.Count == 0)
            return [];

        var found = new List<SamsungTvDeviceInfo>();
        var listGate = new object();
        using var throttle = new SemaphoreSlim(48);

        var tasks = candidates.Select(async ip =>
        {
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var info = await ProbeTvAsync(ip, ct).ConfigureAwait(false);
                if (info is null)
                    return;

                bool sdb = await IsSdbReachableAsync(ip, ct).ConfigureAwait(false);
                lock (listGate)
                    found.Add(info with { SdbReachable = sdb });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        return
        [
            .. found
                .OrderByDescending(d => d.ModelName?.Contains("CU8500", StringComparison.OrdinalIgnoreCase) == true
                                     || d.ModelName?.Contains("MU9000", StringComparison.OrdinalIgnoreCase) == true)
                .ThenByDescending(d => d.SdbReachable || d.DeveloperMode == "1")
                .ThenBy(d => d.Host, StringComparer.Ordinal)
        ];
    }

    private static List<string> EnumerateLanCandidateIps()
    {
        var prefixes = new List<(string Prefix, byte SelfOctet)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;

                    byte[] b = ua.Address.GetAddressBytes();
                    bool isPrivate = b[0] == 10
                        || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                        || (b[0] == 192 && b[1] == 168);
                    if (!isPrivate)
                        continue;

                    string prefix = $"{b[0]}.{b[1]}.{b[2]}.";
                    if (prefixes.All(p => p.Prefix != prefix))
                        prefixes.Add((prefix, b[3]));
                }
            }
        }
        catch { }

        var ips = new List<string>(prefixes.Count * 253);
        foreach (var (prefix, selfOctet) in prefixes.Take(3))
        {
            for (int i = 1; i <= 254; i++)
            {
                if (i != selfOctet)
                    ips.Add(prefix + i);
            }
        }
        return ips;
    }

    public static async Task<bool> IsSdbReachableAsync(string host, CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await tcp.ConnectAsync(host, SdbPort, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string BuildSafeLaunchCommand(string? serverUrl, string? serverToken, int hdmiPort)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(serverToken))
            return $"0 was_execute {AppId}";

        string json = JsonSerializer.Serialize(new
        {
            server = serverUrl.Trim(),
            token = serverToken.Trim(),
            hdmi = Math.Clamp(hdmiPort, 1, 4)
        });
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return $"0 was_execute {AppId} PAYLOAD b64:{b64}";
    }

    public static async Task<SamsungAccountToken> SignInSamsungAccountAsync(Action<string> openBrowser, TimeSpan timeout, CancellationToken ct = default)
    {
        const int callbackPort = 4794;
        const string callbackPath = "/signin/callback";
        const string clientId = "v285zxnl3h";
        string state = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

        string authUrl = $"https://account.samsung.com/accounts/be1dce529476c1a6d407c4c7578c31bd/signInGate" +
                         $"?locale=&clientId={clientId}&redirect_uri={Uri.EscapeDataString($"http://localhost:{callbackPort}{callbackPath}")}" +
                         $"&state={state}&tokenType=TOKEN";

        var listener = new TcpListener(IPAddress.Loopback, callbackPort);
        listener.Start();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            openBrowser(authUrl);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token).ConfigureAwait(false);
                await using var stream = client.GetStream();
                var (method, path, body) = await ReadCallbackHttpAsync(stream, cts.Token).ConfigureAwait(false);
                bool valid = method == "POST" && path.StartsWith(callbackPath, StringComparison.Ordinal);
                var token = valid ? ParseCallbackPayload(body, state) : null;

                string html = token is not null
                    ? "<!doctype html><meta charset=utf-8><title>ESSVB-sub</title><body style='font-family:sans-serif;padding:3em;background:#0e1117;color:#f3eee4'>" +
                      "<h2 style='color:#f5b83d'>ESSVB-sub Samsung Girişi Tamamlandı</h2><p>Bu sekmeyi kapatıp uygulamaya dönebilirsiniz.</p></body>"
                    : "Not found";
                byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
                string statusLine = token is not null ? "200 OK" : "404 Not Found";
                string hdr = $"HTTP/1.1 {statusLine}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {htmlBytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(hdr), cts.Token).ConfigureAwait(false);
                await stream.WriteAsync(htmlBytes, cts.Token).ConfigureAwait(false);

                if (token is not null)
                    return token;
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    public static async Task InstallAndLaunchAsync(
        string host,
        string tvTizenFolder,
        string profilesDirectory,
        Func<Action<string>, Task<SamsungAccountToken>>? signInHandler,
        string? serverUrl,
        string? serverToken,
        int hdmiPort,
        Action<string>? log = null,
        SamsungTvModel selectedModel = SamsungTvModel.AutoDetect,
        CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(host, out var ip))
            throw new ArgumentException($"Geçersiz Samsung TV IP adresi: {host}", nameof(host));

        var tvInfo = await ProbeTvAsync(host, ct).ConfigureAwait(false);
        if (tvInfo is not null)
            log?.Invoke($"TV Algılandı: {tvInfo.Name} ({tvInfo.ModelName ?? "Model belirtilmedi"})");

        log?.Invoke($"Samsung TV SDB bağlantısı kuruluyor: {host}:{SdbPort}...");
        await using var device = new SdbTcpDevice(ip, SdbPort);
        await device.ConnectAsync(ct).ConfigureAwait(false);

        var caps = await device.CapabilityAsync(ct).ConfigureAwait(false);
        string detectedPlatform = caps.GetValueOrDefault("platform_version", "").Trim();
        string platform = TizenPackageAdapter.ResolvePlatformVersion(detectedPlatform, selectedModel, tvInfo?.ModelName);
        string toolPath = caps.GetValueOrDefault("sdk_toolpath")?.Trim() is { Length: > 0 } tp ? tp : "/opt/usr/apps/tmp";
        log?.Invoke($"TV Tizen Sürümü: {platform}{(detectedPlatform.Length == 0 ? " (model profilinden)" : "")}, toolPath={toolPath}");

        string duid = (await device.ShellCommandAsync("0 getduid", ct).ConfigureAwait(false)).Split('\n')[0].Trim();
        if (string.IsNullOrWhiteSpace(duid))
            throw new InvalidOperationException("TV kimliği (DUID) okunamadı.");

        string certDir = Path.Combine(profilesDirectory, SanitizeFileName(duid));
        string authorP12 = Path.Combine(certDir, "author.p12");
        string distP12 = Path.Combine(certDir, "distributor.p12");
        string legacyPassFile = Path.Combine(certDir, "password.txt");
        string profileXml = Path.Combine(certDir, "device-profile.xml");
        string vaultKey = $"tizen:certpass:{duid}";

        string? password = SecureVault.Default.Load<string>(vaultKey);
        if (string.IsNullOrWhiteSpace(password) && File.Exists(legacyPassFile))
        {
            password = (await File.ReadAllTextAsync(legacyPassFile, ct).ConfigureAwait(false)).Trim();
            if (!string.IsNullOrWhiteSpace(password))
            {
                SecureVault.Default.Save(vaultKey, password);
                try { File.Delete(legacyPassFile); } catch { }
            }
        }

        if (!File.Exists(authorP12) || !File.Exists(distP12) || string.IsNullOrWhiteSpace(password))
        {
            if (signInHandler is null)
                throw new InvalidOperationException("Samsung geliştirici sertifikası yok ve oturum açma işleyicisi verilmedi.");

            log?.Invoke("Samsung hesabıyla giriş bekleniyor...");
            var account = await signInHandler(url =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch { }
            }).ConfigureAwait(false);

            log?.Invoke("Samsung TV sertifikası üretiliyor...");
            password = await EnrollCertificatesAsync(duid, account, certDir, vaultKey, ct).ConfigureAwait(false);
        }

        // MU9000 (Tizen 3.0) and Tizen 2.4-4.0 require device-profile.xml to be pushed before install
        if (TizenPackageAdapter.RequiresDeviceProfile(platform) && File.Exists(profileXml))
        {
            log?.Invoke($"Tizen {platform}: device-profile.xml TV'ye gönderiliyor...");
            await using var fs = File.OpenRead(profileXml);
            await device.PushAsync(fs, $"{toolPath}/device-profile.xml", ct: ct).ConfigureAwait(false);
        }

        string stagedWgt = Path.Combine(Path.GetTempPath(), $"essvb-sub-{Guid.NewGuid():N}.wgt");
        try
        {
            TizenPackageAdapter.PrepareAndSignWgtForDevice(
                tvTizenFolder, stagedWgt, platform, authorP12, distP12, password);

            log?.Invoke($"ESSVB-sub TV paketi imzalandı ({TizenPackageAdapter.GetRecommendedDigest(platform)}) ve kuruluyor...");
            await new TizenInstaller(stagedWgt, device, toolPath).InstallApp().ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(stagedWgt)) File.Delete(stagedWgt); } catch { }
        }

        log?.Invoke("TV uygulaması başlatılıyor...");
        string cmd = BuildSafeLaunchCommand(serverUrl, serverToken, hdmiPort);
        string launchReply = await device.ShellCommandAsync(cmd, ct).ConfigureAwait(false);
        log?.Invoke($"Başlatma yanıtı: {launchReply.Trim()}");
    }

    public static async Task<bool> LaunchAsync(
        string host,
        string? serverUrl = null,
        string? serverToken = null,
        int hdmiPort = 1,
        CancellationToken ct = default)
    {
        if (!IPAddress.TryParse(host, out var ip)) return false;
        try
        {
            await using var device = new SdbTcpDevice(ip, SdbPort);
            await device.ConnectAsync(ct).ConfigureAwait(false);
            string cmd = BuildSafeLaunchCommand(serverUrl, serverToken, hdmiPort);
            string reply = await device.ShellCommandAsync(cmd, ct).ConfigureAwait(false);
            return reply.Contains("launched", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> EnrollCertificatesAsync(
        string duid,
        SamsungAccountToken account,
        string certDir,
        string vaultKey,
        CancellationToken ct)
    {
        using var rsa = RSA.Create(2048);
        byte[] authorCsr = BuildAuthorCsr(rsa);
        byte[] distCsr = BuildDistributorCsr(rsa, duid, account.Email);

        byte[] authorDer = await PostCsrAsync(AuthorCertUrl, account, authorCsr, "author.csr", isDist: false, ct).ConfigureAwait(false);
        byte[] devProfile = await PostCsrAsync(DistProfileUrl, account, distCsr, "distributor.csr", isDist: true, ct).ConfigureAwait(false);
        byte[] distDer = await PostCsrAsync(DistCertUrl, account, distCsr, "distributor.csr", isDist: true, ct).ConfigureAwait(false);

        Directory.CreateDirectory(certDir);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        using var authorCert = LoadCert(authorDer);
        using var distCert = LoadCert(distDer);
        await File.WriteAllBytesAsync(Path.Combine(certDir, "author.p12"), ExportPkcs12(authorCert, rsa, password), ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(certDir, "distributor.p12"), ExportPkcs12(distCert, rsa, password), ct).ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(certDir, "device-profile.xml"), devProfile, ct).ConfigureAwait(false);
        SecureVault.Default.Save(vaultKey, password);
        return password;
    }

    private static byte[] BuildAuthorCsr(RSA rsa)
    {
        var req = new CertificateRequest("CN=ESSVB", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return Encoding.ASCII.GetBytes(req.CreateSigningRequestPem());
    }

    private static byte[] BuildDistributorCsr(RSA rsa, string duid, string email)
    {
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName("TizenSDK");
        if (!string.IsNullOrWhiteSpace(email)) dn.AddEmailAddress(email);

        var req = new CertificateRequest(dn.Build(), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            foreach (string uri in new[] { "URN:tizen:packageid=", $"URN:tizen:deviceid={duid}" })
                writer.WriteCharacterString(UniversalTagNumber.IA5String, uri, new Asn1Tag(TagClass.ContextSpecific, 6));
        }
        req.CertificateExtensions.Add(new X509Extension("2.5.29.17", writer.Encode(), critical: false));
        return Encoding.ASCII.GetBytes(req.CreateSigningRequestPem());
    }

    private static async Task<byte[]> PostCsrAsync(string url, SamsungAccountToken account, byte[] csr, string fileName, bool isDist, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(account.AccessToken), "access_token" },
            { new StringContent(account.UserId), "user_id" },
            { new StringContent("VD"), "platform" }
        };
        if (isDist)
        {
            form.Add(new StringContent("public"), "privilege_level");
            form.Add(new StringContent("Individual"), "developer_type");
        }
        form.Add(new ByteArrayContent(csr), "csr", fileName);

        using var resp = await CloudHttp.PostAsync(url, form, ct).ConfigureAwait(false);
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Samsung sertifika sunucusu reddetti (HTTP {(int)resp.StatusCode}).");
        return bytes;
    }

    private static X509Certificate2 LoadCert(byte[] bytes)
    {
        string ascii = Encoding.ASCII.GetString(bytes);
        return ascii.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(ascii)
            : X509CertificateLoader.LoadCertificate(bytes);
    }

    private static byte[] ExportPkcs12(X509Certificate2 leaf, RSA key, string password)
    {
        using var withKey = leaf.CopyWithPrivateKey(key);
        var chain = new X509Certificate2Collection { withKey };
        foreach (var ca in LoadEmbeddedCaCerts())
        {
            if (ca.Subject == leaf.Issuer && ca.Subject != ca.Issuer)
                chain.Add(ca);
            else
                ca.Dispose();
        }
        var pbe = new PbeParameters(PbeEncryptionAlgorithm.TripleDes3KeyPkcs12, HashAlgorithmName.SHA1, 2048);
        return chain.ExportPkcs12(pbe, password);
    }

    private static IEnumerable<X509Certificate2> LoadEmbeddedCaCerts()
    {
        var asm = typeof(SamsungTvInstaller).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("EssvbCa.", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) continue;
            using var reader = new StreamReader(s);
            yield return X509Certificate2.CreateFromPem(reader.ReadToEnd());
        }
    }

    private static SamsungAccountToken? ParseCallbackPayload(string body, string expectedState)
    {
        string? state = null, codeJson = null;
        foreach (var part in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0] == "state") state = WebUtility.UrlDecode(kv[1]);
            if (kv[0] == "code") codeJson = Uri.UnescapeDataString(kv[1]);
        }

        if (string.IsNullOrEmpty(state) || string.IsNullOrWhiteSpace(codeJson))
            return null;
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState)))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(codeJson);
            string? token = doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            string? uid = doc.RootElement.TryGetProperty("userId", out var u) ? u.GetString() : null;
            string email = doc.RootElement.TryGetProperty("inputEmailID", out var e) ? e.GetString() ?? "" : "";
            return !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(uid)
                ? new SamsungAccountToken(token, uid, email)
                : null;
        }
        catch { return null; }
    }

    private static async Task<(string Method, string Path, string Body)> ReadCallbackHttpAsync(NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        using var ms = new MemoryStream();
        int headerEnd = -1;
        while (ms.Length < 256 * 1024)
        {
            int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) return ("", "", "");
            ms.Write(buf, 0, n);
            string s = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            headerEnd = s.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd >= 0) break;
        }
        if (headerEnd < 0) return ("", "", "");

        byte[] raw = ms.ToArray();
        string head = Encoding.ASCII.GetString(raw, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        int contentLength = 0;
        foreach (var l in lines)
            if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(l[15..].Trim(), out contentLength);

        using var bodyMs = new MemoryStream();
        bodyMs.Write(raw, headerEnd + 4, raw.Length - headerEnd - 4);
        while (bodyMs.Length < contentLength)
        {
            int n = await stream.ReadAsync(buf, ct).ConfigureAwait(false);
            if (n <= 0) break;
            bodyMs.Write(buf, 0, n);
        }

        return (first.ElementAtOrDefault(0) ?? "", first.ElementAtOrDefault(1) ?? "", Encoding.UTF8.GetString(bodyMs.ToArray()));
    }

    private static string SanitizeFileName(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
}
