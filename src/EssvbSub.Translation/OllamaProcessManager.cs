// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Diagnostics;
using EssvbSub.Translation.Providers;

namespace EssvbSub.Translation;

public enum OllamaEnsureStatus
{
    AlreadyRunning,
    StartedSuccessfully,
    NotLocalEndpoint,
    ExecutableNotFound,
    StartFailed,
    TimedOut
}

public sealed record OllamaEnsureResult(
    OllamaEnsureStatus Status,
    string Message,
    string? ExecutablePath = null,
    int? ProcessId = null)
{
    public bool IsReady => Status is OllamaEnsureStatus.AlreadyRunning or OllamaEnsureStatus.StartedSuccessfully;
}

internal sealed record SpawnedOllamaProcess(
    int? ProcessId,
    Func<bool> HasExited,
    Func<int?> GetExitCode);

/// <summary>
/// Probes local Ollama availability and automatically launches <c>ollama serve</c>
/// hidden in the background when the user triggers translation, test connection, or model scan.
/// </summary>
public static class OllamaProcessManager
{
    private static readonly SemaphoreSlim StartGate = new(1, 1);

    private static readonly HttpClient ProbeHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(2)
    };

    public static bool IsLoopbackOllamaEndpoint(string? endpoint)
    {
        try
        {
            Uri root = AiProviderCatalog.ResolveOllamaRootUri(endpoint ?? "");
            if (root.IsLoopback)
                return true;

            string host = root.Host;
            return host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                   host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                   host.Equals("::1", StringComparison.Ordinal) ||
                   host.Equals("0.0.0.0", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static string? FindOllamaExecutable(
        Func<string, bool>? fileExists = null,
        string? pathEnv = null,
        bool? isWindowsOverride = null)
    {
        bool isWindows = isWindowsOverride ?? OperatingSystem.IsWindows();
        Func<string, bool> exists = fileExists ?? File.Exists;
        string binaryName = isWindows ? "ollama.exe" : "ollama";

        string rawPath = pathEnv ?? Environment.GetEnvironmentVariable("PATH") ?? "";
        char pathSep = isWindows ? ';' : ':';
        foreach (string rawDir in rawPath.Split(pathSep, StringSplitOptions.RemoveEmptyEntries))
        {
            string dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0)
                continue;

            string candidate = Path.Combine(dir, binaryName);
            if (exists(candidate))
                return candidate;
        }

        if (isWindows)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            string?[] winCandidates =
            [
                string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "Programs", "Ollama", "ollama.exe"),
                string.IsNullOrEmpty(userProfile) ? null : Path.Combine(userProfile, "AppData", "Local", "Programs", "Ollama", "ollama.exe"),
                string.IsNullOrEmpty(programFiles) ? null : Path.Combine(programFiles, "Ollama", "ollama.exe"),
                string.IsNullOrEmpty(programFilesX86) ? null : Path.Combine(programFilesX86, "Ollama", "ollama.exe")
            ];

            foreach (string? path in winCandidates)
            {
                if (!string.IsNullOrEmpty(path) && exists(path))
                    return path;
            }
        }
        else
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string?[] linuxCandidates =
            [
                "/usr/bin/ollama",
                "/usr/local/bin/ollama",
                string.IsNullOrEmpty(userProfile) ? null : Path.Combine(userProfile, ".local", "bin", "ollama")
            ];

            foreach (string? path in linuxCandidates)
            {
                if (!string.IsNullOrEmpty(path) && exists(path))
                    return path;
            }
        }

        return null;
    }

    public static async Task<bool> IsReachableAsync(
        string? endpoint,
        HttpClient? httpClient = null,
        CancellationToken ct = default)
    {
        try
        {
            Uri root = AiProviderCatalog.ResolveOllamaRootUri(endpoint ?? "");
            string url = root.AbsoluteUri.TrimEnd('/') + "/api/tags";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));

            HttpClient client = httpClient ?? ProbeHttp;
            using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static Task<OllamaEnsureResult> EnsureRunningAsync(
        string? endpoint = null,
        Action<string>? onProgress = null,
        HttpClient? httpClient = null,
        TimeSpan? readyTimeout = null,
        CancellationToken ct = default)
    {
        return EnsureRunningCoreAsync(
            endpoint,
            onProgress,
            probeAsync: token => IsReachableAsync(endpoint, httpClient, token),
            findExecutable: () => FindOllamaExecutable(),
            startServer: StartOllamaServeProcess,
            readyTimeout: readyTimeout ?? TimeSpan.FromSeconds(15),
            pollInterval: TimeSpan.FromMilliseconds(350),
            ct: ct);
    }

    internal static async Task<OllamaEnsureResult> EnsureRunningCoreAsync(
        string? endpoint,
        Action<string>? onProgress,
        Func<CancellationToken, Task<bool>> probeAsync,
        Func<string?> findExecutable,
        Func<string, Uri, SpawnedOllamaProcess> startServer,
        TimeSpan readyTimeout,
        TimeSpan pollInterval,
        CancellationToken ct = default)
    {
        if (!IsLoopbackOllamaEndpoint(endpoint))
        {
            return new OllamaEnsureResult(
                OllamaEnsureStatus.NotLocalEndpoint,
                "Uzak Ollama adresi kullanılıyor; otomatik yerel başlatma yalnızca localhost / 127.0.0.1 içindir.");
        }

        if (await probeAsync(ct).ConfigureAwait(false))
        {
            return new OllamaEnsureResult(
                OllamaEnsureStatus.AlreadyRunning,
                "Ollama yerel sunucusu zaten çalışıyor.");
        }

        await StartGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check inside lock in case another caller just brought Ollama online
            if (await probeAsync(ct).ConfigureAwait(false))
            {
                return new OllamaEnsureResult(
                    OllamaEnsureStatus.AlreadyRunning,
                    "Ollama yerel sunucusu hazır.");
            }

            string? exePath = findExecutable();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return new OllamaEnsureResult(
                    OllamaEnsureStatus.ExecutableNotFound,
                    "Ollama CLI bulunamadı. Lütfen Ollama'yı kurun (https://ollama.com) veya Bulut AI / farklı bir sağlayıcı seçin.");
            }

            Uri root = AiProviderCatalog.ResolveOllamaRootUri(endpoint ?? "");
            onProgress?.Invoke("Ollama arka planda otomatik başlatılıyor…");

            SpawnedOllamaProcess spawned;
            try
            {
                spawned = startServer(exePath, root);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new OllamaEnsureResult(
                    OllamaEnsureStatus.StartFailed,
                    $"Ollama CLI çalıştırılamadı ({ex.Message}).",
                    ExecutablePath: exePath);
            }

            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < readyTimeout)
            {
                ct.ThrowIfCancellationRequested();

                if (await probeAsync(ct).ConfigureAwait(false))
                {
                    return new OllamaEnsureResult(
                        OllamaEnsureStatus.StartedSuccessfully,
                        "Ollama arka planda otomatik başlatıldı.",
                        ExecutablePath: exePath,
                        ProcessId: spawned.ProcessId);
                }

                if (spawned.HasExited())
                {
                    // Handle race where Windows tray/service claimed the port just before our serve process
                    await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                    if (await probeAsync(ct).ConfigureAwait(false))
                    {
                        return new OllamaEnsureResult(
                            OllamaEnsureStatus.StartedSuccessfully,
                            "Ollama servisi arka planda hazırlandı.",
                            ExecutablePath: exePath);
                    }

                    int? exitCode = spawned.GetExitCode();
                    string codeSuffix = exitCode.HasValue ? $" (çıkış kodu: {exitCode.Value})" : "";
                    return new OllamaEnsureResult(
                        OllamaEnsureStatus.StartFailed,
                        $"Ollama arka planda başlatılamadı{codeSuffix}. Port {root.Port} başka bir süreç tarafından engelleniyor olabilir.",
                        ExecutablePath: exePath,
                        ProcessId: spawned.ProcessId);
                }

                TimeSpan delay = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromMilliseconds(50);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            return new OllamaEnsureResult(
                OllamaEnsureStatus.TimedOut,
                $"Ollama {readyTimeout.TotalSeconds:0} saniye içinde yanıt vermedi ({root.Authority}).",
                ExecutablePath: exePath,
                ProcessId: spawned.ProcessId);
        }
        finally
        {
            StartGate.Release();
        }
    }

    private static SpawnedOllamaProcess StartOllamaServeProcess(string exePath, Uri root)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        psi.ArgumentList.Add("serve");

        if (root.Port > 0 && root.Port != 11434)
            psi.Environment["OLLAMA_HOST"] = $"{root.Host}:{root.Port}";

        Process proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start null döndürdü.");

        int? pid = null;
        try { pid = proc.Id; } catch { }

        return new SpawnedOllamaProcess(
            pid,
            HasExited: () =>
            {
                try { return proc.HasExited; }
                catch { return true; }
            },
            GetExitCode: () =>
            {
                try { return proc.HasExited ? proc.ExitCode : null; }
                catch { return null; }
            });
    }
}
