// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text.RegularExpressions;

namespace EssvbSub.Capture.TvInstall;

public enum SamsungInstallProblem
{
    None,
    ConnectionLost,
    NotEnoughSpace,
    CertificateMismatch,
    CertificateRejected,
    TvClockWrong,
    PackageBlocked,
    Incompatible,
    Other
}

/// <summary>
/// Parses Samsung TV SDB <c>vd_appinstall</c> console output and <c>0 vd_applist</c> listings
/// so silent Tizen failures are caught, classified, recovered, and surfaced clearly in Turkish.
/// </summary>
public static partial class SamsungInstallDiagnostics
{
    private const StringComparison IC = StringComparison.OrdinalIgnoreCase;

    public static bool Failed(string? output) =>
        Has(output, "install failed") ||
        Has(output, "val[fail]") ||
        Has(output, "not enough space") ||
        Has(output, "download failed[116]") ||
        IsConnectionLost(output);

    public static SamsungInstallProblem Classify(string? output)
    {
        if (!Failed(output))
            return SamsungInstallProblem.None;

        if (IsConnectionLost(output))
            return SamsungInstallProblem.ConnectionLost;

        if (Has(output, "install failed[118, -4]") ||
            Has(output, "install failed[118004]") ||
            Has(output, "install failed[118, -19]") ||
            Has(output, "install failed[118019]"))
        {
            return SamsungInstallProblem.Incompatible;
        }

        if (Has(output, "download failed[116]") || Has(output, "not enough space"))
            return SamsungInstallProblem.NotEnoughSpace;

        bool certCode = Has(output, "install failed[118, -12]") ||
                        Has(output, "install failed[118012]") ||
                        Has(output, "install failed[118, -11]") ||
                        Has(output, "install failed[118011]");

        if (certCode && Has(output, "Check certificate error") && !Has(output, "same id and different certificate"))
        {
            return Has(output, "not valid yet")
                ? SamsungInstallProblem.TvClockWrong
                : SamsungInstallProblem.CertificateRejected;
        }

        if (certCode || Has(output, "Author certificate not match"))
            return SamsungInstallProblem.CertificateMismatch;

        if (Has(output, "install failed[118, -22]") || Has(output, "install failed[118022]"))
            return SamsungInstallProblem.PackageBlocked;

        return SamsungInstallProblem.Other;
    }

    public static bool RemovingOldCopyMayHelp(SamsungInstallProblem problem) =>
        problem is SamsungInstallProblem.CertificateMismatch
            or SamsungInstallProblem.PackageBlocked
            or SamsungInstallProblem.Other;

    public static string ExtractErrorSummary(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "TV kurulum yanıtı boş döndü.";

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var keyLines = lines
            .Where(l => l.Contains("fail", IC) ||
                        l.Contains("error", IC) ||
                        l.Contains("reason", IC) ||
                        l.Contains("not match", IC) ||
                        l.Contains("space", IC) ||
                        l.Contains("valid", IC))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToList();

        string joined = keyLines.Count > 0
            ? string.Join(" | ", keyLines)
            : string.Join(" | ", lines.TakeLast(3));
        return joined.Length <= 360 ? joined : joined[..360] + "…";
    }

    public static string BuildTurkishErrorSummary(SamsungInstallProblem problem, string? rawOutput)
    {
        string detail = ExtractErrorSummary(rawOutput);
        string prefix = problem switch
        {
            SamsungInstallProblem.ConnectionLost =>
                "Kurulum sırasında TV SDB bağlantısı koptu. TV açık kalmalı ve Developer Mode > Host PC IP adresi bu bilgisayara eşit olmalıdır.",
            SamsungInstallProblem.NotEnoughSpace =>
                "TV hafızasında yeterli boş alan yok ([116]). TV'den kullanılmayan 1-2 uygulama silip tekrar deneyin.",
            SamsungInstallProblem.CertificateMismatch =>
                "TV'de farklı sertifika ile kurulmuş eski bir kopya veya sertifika imza uyumsuzluğu var ([118, -12]).",
            SamsungInstallProblem.CertificateRejected =>
                "TV geliştirici sertifikasını reddetti. Kayıtlı sertifika sıfırlandı; 'TV'ye Otomatik Kur & Aç' butonuna tekrar basıp Samsung hesabıyla yeni sertifika alın.",
            SamsungInstallProblem.TvClockWrong =>
                "TV tarih/saat ayarı geride olduğu için sertifika henüz geçerli görünmüyor. TV Ayarları > Genel > Sistem Yöneticisi > Saat'i otomatik yapın.",
            SamsungInstallProblem.PackageBlocked =>
                "TV güvenlik yöneticisi paket kimliğini engelledi ([118, -22]). TV'yi prizden/kumandadan tamamen yeniden başlatıp deneyin.",
            SamsungInstallProblem.Incompatible =>
                "Paket bu Tizen sürümüyle uyumlu bulunmadı ([118, -4/-19]).",
            _ =>
                "TV uygulama kurulumunu kabul etmedi."
        };
        return $"{prefix} ({detail})";
    }

    public static (bool Installed, string? Version, string? Title) ParseInstalledApp(
        string? vdAppListOutput,
        string appId,
        string packageId)
    {
        if (string.IsNullOrWhiteSpace(vdAppListOutput))
            return (false, null, null);

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        (bool Installed, string? Version, string? Title)? TryMatchBlock()
        {
            string? blockAppId = fields.GetValueOrDefault("app_tizen_id") ?? fields.GetValueOrDefault("app_id");
            string? altAppId = fields.GetValueOrDefault("app_id");
            bool isMatch =
                MatchesId(blockAppId, appId, packageId) ||
                MatchesId(altAppId, appId, packageId);

            if (!isMatch)
            {
                fields.Clear();
                return null;
            }

            string? ver = fields.GetValueOrDefault("app_version");
            string? title = fields.GetValueOrDefault("app_title");
            fields.Clear();
            return (true, string.IsNullOrWhiteSpace(ver) ? null : ver, string.IsNullOrWhiteSpace(title) ? null : title);
        }

        foreach (string rawLine in vdAppListOutput.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
        {
            if (!rawLine.Contains('=', StringComparison.Ordinal) && rawLine.Contains("----", StringComparison.Ordinal))
            {
                if (TryMatchBlock() is { } matched)
                    return matched;
                continue;
            }

            var match = AppListFieldRegex().Match(rawLine);
            if (match.Success)
            {
                string key = match.Groups["key"].Value.Trim();
                string val = match.Groups["value"].Value.Trim();
                if (fields.ContainsKey(key) && TryMatchBlock() is { } matched)
                    return matched;
                fields[key] = val;
            }
        }

        return TryMatchBlock() ?? (false, null, null);
    }

    private static bool MatchesId(string? id, string appId, string packageId) =>
        !string.IsNullOrWhiteSpace(id) &&
        (id.Trim().Equals(appId, IC) || id.Trim().Equals(packageId, IC));

    private static bool IsConnectionLost(string? output) =>
        Has(output, "transport connection") ||
        Has(output, "connection reset") ||
        Has(output, "broken pipe") ||
        Has(output, "SocketException");

    private static bool Has(string? text, string needle) =>
        !string.IsNullOrEmpty(text) && text.Contains(needle, IC);

    [GeneratedRegex(@"^[\s-]*(?<key>[A-Za-z0-9_]+)\s*=\s*(?<value>.*?)(?:-+\s*|\s*)$")]
    private static partial Regex AppListFieldRegex();
}
