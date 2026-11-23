// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using EssvbSub.Core.Storage;

namespace EssvbSub.Core.Updates;

public sealed record ReleaseAssetInfo(string Name, string DownloadUrl, long SizeBytes);

public sealed record AppUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string TagName,
    string ReleaseName,
    string HtmlUrl,
    string ReleaseNotes,
    DateTimeOffset? PublishedAt,
    ReleaseAssetInfo? WindowsExeAsset,
    ReleaseAssetInfo? Sha256Asset)
{
    public bool CanInAppUpdateWindows => WindowsExeAsset is not null && Sha256Asset is not null;

    public string FormattedSize =>
        WindowsExeAsset is { SizeBytes: > 0 } asset
            ? $"{asset.SizeBytes / (1024.0 * 1024.0):0.0} MB"
            : "";

    public string PublishedDateLocal =>
        PublishedAt.HasValue ? PublishedAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm") : "-";
}

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string Message,
    AppUpdateInfo? Update = null);

public sealed record UpdateDownloadProgress(
    string StageText,
    double Percent,
    long BytesReceived,
    long? TotalBytes);

public sealed record UpdateInstallResult(
    bool Success,
    string Message,
    bool RequiresAppExit = false,
    string? DownloadedFilePath = null);

/// <summary>
/// Checks GitHub Releases for newer ESSVB-sub versions and performs user-consented,
/// HTTPS-domain-pinned, SHA-256-verified in-app updates.
/// </summary>
public static class GitHubUpdateService
{
    private const long MaxDownloadBytes = 250L * 1024 * 1024;
    private const int MaxRedirects = 5;

    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    private static readonly string ApplyUpdatePowerShellScript = """
        param(
            [int]$OldPid,
            [string]$StagedExe,
            [string]$DestExe,
            [string]$OldExe
        )
        $ErrorActionPreference = "SilentlyContinue"
        for ($i = 0; $i -lt 30; $i++) {
            if (-not (Get-Process -Id $OldPid -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }
        $destDir = Split-Path -Parent $DestExe
        if (-not (Test-Path -LiteralPath $destDir)) {
            New-Item -ItemType Directory -Force -Path $destDir | Out-Null
        }
        $copied = $false
        for ($r = 0; $r -lt 8; $r++) {
            try {
                Copy-Item -LiteralPath $StagedExe -Destination $DestExe -Force -ErrorAction Stop
                $copied = $true
                break
            }
            catch {
                Start-Sleep -Milliseconds 400
            }
        }
        if ($copied -and (Test-Path -LiteralPath $DestExe)) {
            try {
                $desktop = [Environment]::GetFolderPath("Desktop")
                $lnkPath = Join-Path $desktop "ESSVB-sub.lnk"
                if (Test-Path -LiteralPath $lnkPath) {
                    $wsh = New-Object -ComObject WScript.Shell
                    $sc = $wsh.CreateShortcut($lnkPath)
                    $curTarget = $sc.TargetPath
                    if ([string]::IsNullOrWhiteSpace($curTarget) -or
                        $curTarget.Equals($OldExe, [System.StringComparison]::OrdinalIgnoreCase) -or
                        ([System.IO.Path]::GetFileName($curTarget) -like "ESSVB-sub*.exe")) {
                        $sc.TargetPath = $DestExe
                        $sc.WorkingDirectory = $destDir
                        $sc.IconLocation = "$DestExe,0"
                        $sc.Save()
                    }
                }
            }
            catch { }
            Start-Process -FilePath $DestExe -WorkingDirectory $destDir | Out-Null
            if (-not $StagedExe.Equals($DestExe, [System.StringComparison]::OrdinalIgnoreCase)) {
                Remove-Item -LiteralPath $StagedExe -Force -ErrorAction SilentlyContinue
            }
        }
        """;

    public static int CompareVersions(string left, string right)
    {
        var l = ParseSemVer(left);
        var r = ParseSemVer(right);

        int maxCore = Math.Max(l.Core.Length, r.Core.Length);
        for (int i = 0; i < maxCore; i++)
        {
            int lv = i < l.Core.Length ? l.Core[i] : 0;
            int rv = i < r.Core.Length ? r.Core[i] : 0;
            if (lv != rv)
                return lv.CompareTo(rv);
        }

        if (l.PreRelease.Length == 0 && r.PreRelease.Length == 0)
            return 0;
        if (l.PreRelease.Length == 0)
            return 1;
        if (r.PreRelease.Length == 0)
            return -1;

        int maxPre = Math.Max(l.PreRelease.Length, r.PreRelease.Length);
        for (int i = 0; i < maxPre; i++)
        {
            if (i >= l.PreRelease.Length) return -1;
            if (i >= r.PreRelease.Length) return 1;

            string lp = l.PreRelease[i];
            string rp = r.PreRelease[i];
            bool lNum = int.TryParse(lp, NumberStyles.Integer, CultureInfo.InvariantCulture, out int li);
            bool rNum = int.TryParse(rp, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ri);

            int cmp = (lNum, rNum) switch
            {
                (true, true) => li.CompareTo(ri),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(lp, rp, StringComparison.OrdinalIgnoreCase)
            };
            if (cmp != 0)
                return cmp;
        }

        return 0;
    }

    public static bool IsNewerVersion(string currentVersion, string candidateVersion) =>
        CompareVersions(candidateVersion, currentVersion) > 0;

    public static bool IsTrustedGitHubUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri))
            return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        string host = uri.Host;
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    public static string? SanitizeAssetFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        string trimmed = name.Trim();
        if (trimmed.Contains("..", StringComparison.Ordinal) ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\'))
        {
            return null;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        return string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal)
            ? trimmed
            : null;
    }

    public static byte[]? ParseExpectedSha256(string sha256Content, string expectedFileName)
    {
        if (string.IsNullOrWhiteSpace(sha256Content) || string.IsNullOrWhiteSpace(expectedFileName))
            return null;

        string safeExpected = SanitizeAssetFileName(expectedFileName) ?? "";
        if (safeExpected.Length == 0)
            return null;

        foreach (string rawLine in sha256Content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length < 64)
                continue;

            string hexPart = line[..64];
            if (!IsHex64(hexPart))
                continue;

            string rest = line[64..].Trim();
            if (rest.Length == 0)
                continue;

            string filePart = rest.TrimStart('*').Trim();
            if (!string.Equals(filePart, safeExpected, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                return Convert.FromHexString(hexPart);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        string? currentVersion = null,
        HttpClient? httpClient = null,
        string? apiUrl = null,
        CancellationToken ct = default)
    {
        string current = NormalizeVersionLabel(currentVersion ?? AppInfo.Version);
        string targetUrl = string.IsNullOrWhiteSpace(apiUrl) ? AppInfo.GitHubLatestReleaseApiUrl : apiUrl.Trim();
        HttpClient client = httpClient ?? SharedHttp;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var req = CreateGitHubApiRequest(targetUrl, current);
            using var resp = await client.SendAsync(req, cts.Token).ConfigureAwait(false);
            string json = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if ((int)resp.StatusCode == 404)
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, "GitHub'da henüz yayınlanmış sürüm yok.");
            }

            if ((int)resp.StatusCode == 403 &&
                json.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, "GitHub API hız sınırına ulaşıldı; biraz sonra tekrar deneyin.");
            }

            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, $"GitHub sürüm kontrolü başarısız (HTTP {(int)resp.StatusCode}).");
            }

            var root = JsonNode.Parse(json);
            if (root is null)
                return new UpdateCheckResult(UpdateCheckStatus.Failed, "GitHub sürüm yanıtı okunamadı.");

            bool isDraft = root["draft"]?.GetValue<bool>() ?? false;
            bool isPre = root["prerelease"]?.GetValue<bool>() ?? false;
            if (isDraft || isPre)
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, $"Güncel sürüm kullanılıyor (v{current}).");

            string tagName = (root["tag_name"]?.GetValue<string>() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(tagName))
                return new UpdateCheckResult(UpdateCheckStatus.Failed, "GitHub sürüm etiketi okunamadı.");

            string latestVersion = NormalizeVersionLabel(tagName);

            if (!IsNewerVersion(current, latestVersion))
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, $"Program güncel (v{current}).");

            string releaseName = (root["name"]?.GetValue<string>() ?? tagName).Trim();
            string? rawHtmlUrl = root["html_url"]?.GetValue<string>();
            string htmlUrl = IsTrustedGitHubUrl(rawHtmlUrl) ? rawHtmlUrl!.Trim() : AppInfo.GitHubReleasesUrl;
            string notes = (root["body"]?.GetValue<string>() ?? "").Trim();

            DateTimeOffset? publishedAt = null;
            string? pubStr = root["published_at"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(pubStr) &&
                DateTimeOffset.TryParse(pubStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDate))
            {
                publishedAt = parsedDate;
            }

            var assets = ParseTrustedAssets(root["assets"] as JsonArray);
            ReleaseAssetInfo? winExe = SelectWindowsExeAsset(assets);
            ReleaseAssetInfo? shaAsset = winExe is not null
                ? assets.FirstOrDefault(a => string.Equals(a.Name, winExe.Name + ".sha256", StringComparison.OrdinalIgnoreCase))
                : null;

            var update = new AppUpdateInfo(
                CurrentVersion: current,
                LatestVersion: latestVersion,
                TagName: tagName,
                ReleaseName: releaseName,
                HtmlUrl: htmlUrl,
                ReleaseNotes: notes,
                PublishedAt: publishedAt,
                WindowsExeAsset: winExe,
                Sha256Asset: shaAsset);

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                $"GitHub'da yeni sürüm bulundu: v{latestVersion} (mevcut: v{current})",
                update);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, $"Güncelleme kontrolü başarısız: {ex.Message}");
        }
    }

    public static Task<UpdateInstallResult> DownloadAndInstallUpdateAsync(
        AppUpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress = null,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        return DownloadAndInstallCoreAsync(
            update,
            progress,
            httpClient,
            updatesDirectoryOverride: null,
            packagedWindowsOverride: null,
            executablePathOverride: null,
            launchUpdaterOverride: null,
            ct);
    }

    internal static async Task<UpdateInstallResult> DownloadAndInstallCoreAsync(
        AppUpdateInfo update,
        IProgress<UpdateDownloadProgress>? progress,
        HttpClient? httpClient,
        string? updatesDirectoryOverride,
        bool? packagedWindowsOverride,
        string? executablePathOverride,
        Action<string, string, string, string>? launchUpdaterOverride,
        CancellationToken ct)
    {
        if (update.WindowsExeAsset is not { } exeAsset || update.Sha256Asset is not { } shaAsset)
        {
            return new UpdateInstallResult(false, "Bu sürümde SHA-256 doğrulamalı Windows .exe paketi bulunamadı; GitHub sayfasından indirilebilir.");
        }

        string? safeExeName = SanitizeAssetFileName(exeAsset.Name);
        if (safeExeName is null || SanitizeAssetFileName(shaAsset.Name) is null)
            return new UpdateInstallResult(false, "Güvenlik denetimi: Geçersiz güncelleme dosya adı.");

        if (!IsTrustedGitHubUrl(exeAsset.DownloadUrl) || !IsTrustedGitHubUrl(shaAsset.DownloadUrl))
            return new UpdateInstallResult(false, "Güvenlik denetimi: Güvenilmeyen indirme adresi engellendi.");

        HttpClient client = httpClient ?? SharedHttp;
        string updatesDir = updatesDirectoryOverride ?? EssvbPaths.UpdatesDir;
        Directory.CreateDirectory(updatesDir);

        string partPath = Path.Combine(updatesDir, safeExeName + ".part");
        string stagedPath = Path.Combine(updatesDir, safeExeName);

        try
        {
            progress?.Report(new UpdateDownloadProgress("SHA-256 doğrulama imzası indiriliyor…", 2, 0, null));
            string shaText = await DownloadSmallTextAsync(client, shaAsset.DownloadUrl, update.CurrentVersion, ct).ConfigureAwait(false);
            byte[]? expectedHash = ParseExpectedSha256(shaText, safeExeName);
            if (expectedHash is null)
                return new UpdateInstallResult(false, "SHA-256 doğrulama dosyası geçersiz veya dosya adı eşleşmiyor.");

            if (File.Exists(partPath))
                File.Delete(partPath);

            using (var resp = await SendWithTrustedRedirectsAsync(client, exeAsset.DownloadUrl, update.CurrentVersion, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                    return new UpdateInstallResult(false, $"Güncelleme paketi indirilemedi (HTTP {(int)resp.StatusCode}).");

                long? totalBytes = resp.Content.Headers.ContentLength ?? (exeAsset.SizeBytes > 0 ? exeAsset.SizeBytes : null);
                if (totalBytes is > MaxDownloadBytes)
                    return new UpdateInstallResult(false, "İndirme boyutu güvenlik sınırını (250 MB) aşıyor.");

                await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

                byte[] buffer = new byte[81920];
                long received = 0;
                int lastReportedPct = -1;
                int read;
                while ((read = await netStream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaxDownloadBytes)
                        throw new InvalidOperationException("İndirme boyutu 250 MB üst sınırını aştı.");

                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);

                    double pct = totalBytes is > 0
                        ? Math.Clamp(5.0 + (received * 90.0 / totalBytes.Value), 5.0, 95.0)
                        : 50.0;
                    int roundedPct = (int)Math.Round(pct);
                    if (roundedPct != lastReportedPct)
                    {
                        lastReportedPct = roundedPct;
                        string sizeText = totalBytes is > 0
                            ? $"İndiriliyor… {received / (1024.0 * 1024.0):0.1} / {totalBytes.Value / (1024.0 * 1024.0):0.1} MB"
                            : $"İndiriliyor… {received / (1024.0 * 1024.0):0.1} MB";
                        progress?.Report(new UpdateDownloadProgress(sizeText, pct, received, totalBytes));
                    }
                }
            }

            progress?.Report(new UpdateDownloadProgress("SHA-256 bütünlüğü doğrulanıyor…", 96, 0, null));
            byte[] actualHash;
            await using (var verifyStream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                actualHash = await SHA256.HashDataAsync(verifyStream, ct).ConfigureAwait(false);
            }

            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                TryDeleteFile(partPath);
                return new UpdateInstallResult(false, "SHA-256 bütünlük doğrulaması başarısız! İndirilen dosya bozuk veya değiştirilmiş.");
            }

            File.Move(partPath, stagedPath, overwrite: true);
            progress?.Report(new UpdateDownloadProgress("Doğrulandı. Kurulum hazırlanıyor…", 100, 0, null));

            bool isPackagedWin = packagedWindowsOverride ?? AppInfo.IsPackagedWindowsExecutable;
            if (!isPackagedWin)
            {
                string nonPackagedMsg = OperatingSystem.IsWindows()
                    ? $"Yeni sürüm indirildi ve SHA-256 doğrulandı: {stagedPath}"
                    : "CachyOS/Linux'ta proje klasöründe `git pull && ./install-cachyos.sh` ile güncelleyin.";
                return new UpdateInstallResult(true, nonPackagedMsg, RequiresAppExit: false, DownloadedFilePath: stagedPath);
            }

            string currentExePath = executablePathOverride ?? AppInfo.ExecutablePath;
            string currentDir = Path.GetDirectoryName(currentExePath) ?? EssvbPaths.ConfigDir;
            string targetDir = IsDirectoryWritable(currentDir)
                ? currentDir
                : Path.Combine(EssvbPaths.ConfigDir, "app");
            Directory.CreateDirectory(targetDir);

            string destExePath = Path.Combine(targetDir, safeExeName);
            string psScriptPath = Path.Combine(updatesDir, "apply-update.ps1");
            await File.WriteAllTextAsync(psScriptPath, ApplyUpdatePowerShellScript, new UTF8Encoding(false), ct).ConfigureAwait(false);

            if (launchUpdaterOverride is not null)
                launchUpdaterOverride(psScriptPath, stagedPath, destExePath, currentExePath);
            else
                LaunchWindowsUpdaterScript(psScriptPath, Environment.ProcessId, stagedPath, destExePath, currentExePath);

            return new UpdateInstallResult(
                true,
                "Doğrulandı! ESSVB-sub yeni sürümle yeniden başlatılıyor…",
                RequiresAppExit: true,
                DownloadedFilePath: destExePath);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryDeleteFile(partPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFile(partPath);
            return new UpdateInstallResult(false, $"Güncelleme başarısız: {ex.Message}");
        }
    }

    private static HttpRequestMessage CreateGitHubApiRequest(string url, string currentVersion)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", $"ESSVB-sub/{currentVersion}");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return req;
    }

    private static async Task<string> DownloadSmallTextAsync(HttpClient client, string url, string currentVersion, CancellationToken ct)
    {
        using var resp = await SendWithTrustedRedirectsAsync(client, url, currentVersion, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"SHA-256 dosyası indirilemedi (HTTP {(int)resp.StatusCode}).");
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendWithTrustedRedirectsAsync(
        HttpClient client,
        string initialUrl,
        string currentVersion,
        HttpCompletionOption completionOption,
        CancellationToken ct)
    {
        string url = initialUrl;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsTrustedGitHubUrl(url))
                throw new InvalidOperationException($"Güvenilmeyen yönlendirme adresi engellendi: {url}");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", $"ESSVB-sub/{currentVersion}");

            HttpResponseMessage resp = await client.SendAsync(req, completionOption, ct).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (code is 301 or 302 or 303 or 307 or 308 && resp.Headers.Location is { } loc)
            {
                Uri resolved = loc.IsAbsoluteUri ? loc : new Uri(new Uri(url), loc);
                resp.Dispose();
                url = resolved.AbsoluteUri;
                continue;
            }

            return resp;
        }

        throw new InvalidOperationException("Çok fazla yönlendirme (redirect).");
    }

    private static List<ReleaseAssetInfo> ParseTrustedAssets(JsonArray? arr)
    {
        var list = new List<ReleaseAssetInfo>();
        if (arr is null)
            return list;

        foreach (var node in arr)
        {
            string? name = SanitizeAssetFileName(node?["name"]?.GetValue<string>());
            string? url = node?["browser_download_url"]?.GetValue<string>();
            long size = node?["size"]?.GetValue<long>() ?? 0;
            if (name is not null && IsTrustedGitHubUrl(url))
                list.Add(new ReleaseAssetInfo(name, url!.Trim(), size));
        }

        return list;
    }

    private static ReleaseAssetInfo? SelectWindowsExeAsset(IReadOnlyList<ReleaseAssetInfo> assets) =>
        assets.FirstOrDefault(a =>
            a.Name.StartsWith("ESSVB-sub-", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith("-win11-x64.exe", StringComparison.OrdinalIgnoreCase))
        ?? assets.FirstOrDefault(a =>
            a.Name.StartsWith("ESSVB-sub", StringComparison.OrdinalIgnoreCase) &&
            a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

    private static void LaunchWindowsUpdaterScript(
        string scriptPath,
        int oldPid,
        string stagedExe,
        string destExe,
        string oldExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(scriptPath);
        psi.ArgumentList.Add("-OldPid");
        psi.ArgumentList.Add(oldPid.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-StagedExe");
        psi.ArgumentList.Add(stagedExe);
        psi.ArgumentList.Add("-DestExe");
        psi.ArgumentList.Add(destExe);
        psi.ArgumentList.Add("-OldExe");
        psi.ArgumentList.Add(oldExe);

        Process.Start(psi);
    }

    private static bool IsDirectoryWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string probe = Path.Combine(dir, $".write-probe-{Guid.NewGuid():N}");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string NormalizeVersionLabel(string? raw) =>
        AppInfo.ParseSemanticVersion(raw);

    private static (int[] Core, string[] PreRelease) ParseSemVer(string? version)
    {
        string clean = NormalizeVersionLabel(version);
        int dashIdx = clean.IndexOf('-');
        string corePart = dashIdx >= 0 ? clean[..dashIdx] : clean;
        string prePart = dashIdx >= 0 && dashIdx + 1 < clean.Length ? clean[(dashIdx + 1)..] : "";

        string[] rawSegs = corePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var core = new int[rawSegs.Length];
        for (int i = 0; i < rawSegs.Length; i++)
            _ = int.TryParse(rawSegs[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out core[i]);

        string[] pre = prePart.Length > 0
            ? prePart.Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];

        return (core, pre);
    }

    private static bool IsHex64(string text)
    {
        if (text.Length != 64)
            return false;

        foreach (char c in text)
        {
            bool isHex = (c >= '0' && c <= '9') ||
                         (c >= 'a' && c <= 'f') ||
                         (c >= 'A' && c <= 'F');
            if (!isHex)
                return false;
        }
        return true;
    }
}
