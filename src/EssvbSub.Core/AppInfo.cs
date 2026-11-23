// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Reflection;
using System.Runtime.InteropServices;
using EssvbSub.Core.Pipeline;
using EssvbSub.Core.Storage;

namespace EssvbSub.Core;

/// <summary>
/// Centralized build, version, runtime, and installation metadata for ESSVB-sub.
/// </summary>
public static class AppInfo
{
    public const string Brand = "ESSVB";
    public const string AppName = "ESSVB-sub";
    public const string FallbackVersion = "1.6.1";
    public const string GitHubOwner = "ibrahimsaglammm";
    public const string GitHubRepo = "ESSVB-sub";
    public const string GitHubRepoUrl = "https://github.com/ibrahimsaglammm/ESSVB-sub";
    public const string GitHubReleasesUrl = "https://github.com/ibrahimsaglammm/ESSVB-sub/releases/latest";
    public const string GitHubLatestReleaseApiUrl = "https://api.github.com/repos/ibrahimsaglammm/ESSVB-sub/releases/latest";
    public const string LicenseSummary = "ESSVB Özel Mülkiyet Lisansı (%100 Yerel • Sıfır Telemetri)";

    private static Assembly TargetAssembly
    {
        get
        {
            var entry = Assembly.GetEntryAssembly();
            string? entryName = entry?.GetName().Name;
            if (entry is not null && !string.IsNullOrWhiteSpace(entryName) &&
                (entryName.StartsWith("ESSVB", StringComparison.OrdinalIgnoreCase) ||
                 entryName.StartsWith("EssvbSub", StringComparison.OrdinalIgnoreCase)))
            {
                return entry;
            }
            return typeof(AppInfo).Assembly;
        }
    }

    private static string? RawInformationalVersion =>
        TargetAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public static string Version => ParseSemanticVersion(RawInformationalVersion);

    public static string VersionBadge => $"v{Version}";

    public static string ShortCommit => ParseShortCommit(RawInformationalVersion);

    public static string FullVersionLabel => FormatVersionLabel(RawInformationalVersion);

    public static string ExecutablePath
    {
        get
        {
            if (Environment.ProcessPath is { Length: > 0 } path)
                return path;
            return AppContext.BaseDirectory;
        }
    }

    public static string ExecutableDirectory =>
        Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    public static bool IsPackagedWindowsExecutable =>
        OperatingSystem.IsWindows() &&
        File.Exists(ExecutablePath) &&
        ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !Path.GetFileNameWithoutExtension(ExecutablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    public static string ConfigDirectory => EssvbPaths.ConfigDir;

    public static bool IsSingleFileBundle => string.IsNullOrEmpty(typeof(AppInfo).Assembly.Location);

    public static string RuntimeSummary =>
        $".NET {Environment.Version.ToString(3)} • {RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.ProcessArchitecture}) • {(IsSingleFileBundle ? "Tek Dosya (.exe)" : "Standart")}";

    public static string RustEngineSummary =>
        RustFastAccel.IsAvailable
            ? $"Aktif — Yerel Rust Motoru v{RustFastAccel.EngineVersion} (essvb_fast, SIMD + LTO)"
            : "Yedek — Saf C# Görüntü İşleme Motoru";

    public static string BuildDateLocal
    {
        get
        {
            try
            {
                string target = File.Exists(ExecutablePath) ? ExecutablePath : typeof(AppInfo).Assembly.Location;
                if (!string.IsNullOrWhiteSpace(target) && File.Exists(target))
                    return File.GetLastWriteTime(target).ToString("dd.MM.yyyy HH:mm");
            }
            catch
            {
                // Ignore IO access errors when probing assembly timestamp.
            }

            return "-";
        }
    }

    public static string ParseSemanticVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return FallbackVersion;

        int plusIdx = informationalVersion.IndexOf('+');
        string raw = (plusIdx >= 0 ? informationalVersion[..plusIdx] : informationalVersion).Trim();
        if (raw.StartsWith('v') || raw.StartsWith('V'))
            raw = raw[1..].Trim();

        return raw.Length > 0 ? raw : FallbackVersion;
    }

    public static string ParseShortCommit(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return string.Empty;

        int plusIdx = informationalVersion.IndexOf('+');
        if (plusIdx < 0 || plusIdx + 1 >= informationalVersion.Length)
            return string.Empty;

        string suffix = informationalVersion[(plusIdx + 1)..].Trim();
        int dotOrSpace = suffix.IndexOfAny(['.', ' ', '-']);
        if (dotOrSpace > 0)
            suffix = suffix[..dotOrSpace];

        return suffix.Length > 7 ? suffix[..7] : suffix;
    }

    public static string FormatVersionLabel(string? informationalVersion)
    {
        string semver = ParseSemanticVersion(informationalVersion);
        string commit = ParseShortCommit(informationalVersion);
        return string.IsNullOrEmpty(commit) ? $"v{semver}" : $"v{semver} ({commit})";
    }
}
