// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
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

public enum OllamaShutdownStatus
{
    Stopped,
    NotRunning,
    NotLocalEndpoint,
    Failed
}

public sealed record OllamaShutdownResult(
    OllamaShutdownStatus Status,
    int ModelsUnloaded,
    int ProcessesStopped,
    string Message);

internal sealed record SpawnedOllamaProcess(
    int? ProcessId,
    Func<bool> HasExited,
    Func<int?> GetExitCode);

/// <summary>
/// Probes local Ollama availability and automatically launches <c>ollama serve</c>
/// hidden in the background when the user triggers translation, test connection, or model scan,
/// and unloads VRAM / stops local Ollama cleanly on exit.
/// </summary>
public static class OllamaProcessManager
{
    private static readonly SemaphoreSlim StartGate = new(1, 1);
    private static readonly ConcurrentDictionary<int, byte> SpawnedPids = new();

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
        try
        {
            pid = proc.Id;
            SpawnedPids[pid.Value] = 0;
        }
        catch { }

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

    public static Task<OllamaShutdownResult> ShutdownLocalOllamaAsync(
        string? endpoint = null,
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        HttpClient client = httpClient ?? ProbeHttp;
        return ShutdownLocalOllamaCoreAsync(
            endpoint,
            listRunningModelsAsync: (root, token) => ListRunningModelsHttpAsync(root, client, token),
            unloadModelAsync: (root, model, token) => UnloadModelHttpAsync(root, model, client, token),
            stopLocalProcesses: StopLocalOllamaProcesses,
            timeout: timeout ?? TimeSpan.FromSeconds(3),
            ct: ct);
    }

    public static OllamaShutdownResult ShutdownLocalOllamaSync(
        string? endpoint = null,
        TimeSpan? timeout = null)
    {
        TimeSpan effectiveTimeout = timeout ?? TimeSpan.FromSeconds(3);
        try
        {
            // Run off UI SynchronizationContext to prevent deadlocks during window/app teardown.
            return Task.Run(() => ShutdownLocalOllamaAsync(endpoint, timeout: effectiveTimeout))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            return new OllamaShutdownResult(
                OllamaShutdownStatus.Failed,
                0,
                0,
                $"Ollama kapatma hatası: {ex.Message}");
        }
    }

    internal static async Task<OllamaShutdownResult> ShutdownLocalOllamaCoreAsync(
        string? endpoint,
        Func<Uri, CancellationToken, Task<IReadOnlyList<string>>> listRunningModelsAsync,
        Func<Uri, string, CancellationToken, Task<bool>> unloadModelAsync,
        Func<int> stopLocalProcesses,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!IsLoopbackOllamaEndpoint(endpoint))
        {
            return new OllamaShutdownResult(
                OllamaShutdownStatus.NotLocalEndpoint,
                0,
                0,
                "Uzak Ollama adresi kullanılıyor; yalnızca yerel Ollama durdurulur.");
        }

        Uri root = AiProviderCatalog.ResolveOllamaRootUri(endpoint ?? "");
        TimeSpan effectiveTimeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(3);
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budgetCts.CancelAfter(effectiveTimeout);

        int modelsUnloaded = 0;
        try
        {
            using var psCts = CancellationTokenSource.CreateLinkedTokenSource(budgetCts.Token);
            psCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            IReadOnlyList<string> loadedModels = await listRunningModelsAsync(root, psCts.Token).ConfigureAwait(false);

            foreach (string rawName in loadedModels)
            {
                if (budgetCts.IsCancellationRequested)
                    break;

                string modelName = rawName.Trim();
                if (modelName.Length == 0)
                    continue;

                using var unloadCts = CancellationTokenSource.CreateLinkedTokenSource(budgetCts.Token);
                unloadCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                try
                {
                    if (await unloadModelAsync(root, modelName, unloadCts.Token).ConfigureAwait(false))
                        modelsUnloaded++;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Time budget exhausted; continue straight to process termination so VRAM is still freed.
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Ollama /api/ps unavailable or timed out; still clean up any local Ollama processes.
        }
        catch
        {
            // Swallow HTTP errors when Ollama is already down before proceeding to process cleanup.
        }

        int processesStopped = 0;
        try
        {
            processesStopped = stopLocalProcesses();
        }
        catch (Exception ex)
        {
            return new OllamaShutdownResult(
                OllamaShutdownStatus.Failed,
                modelsUnloaded,
                0,
                $"Yerel Ollama süreçleri durdurulamadı: {ex.Message}");
        }

        if (modelsUnloaded == 0 && processesStopped == 0)
        {
            return new OllamaShutdownResult(
                OllamaShutdownStatus.NotRunning,
                0,
                0,
                "Çalışan yerel Ollama süreci veya VRAM modeli bulunamadı.");
        }

        return new OllamaShutdownResult(
            OllamaShutdownStatus.Stopped,
            modelsUnloaded,
            processesStopped,
            $"Yerel Ollama durduruldu ({modelsUnloaded} model VRAM'den boşaltıldı, {processesStopped} süreç kapatıldı).");
    }

    private static async Task<IReadOnlyList<string>> ListRunningModelsHttpAsync(
        Uri root,
        HttpClient client,
        CancellationToken ct)
    {
        string url = root.AbsoluteUri.TrimEnd('/') + "/api/ps";
        using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return Array.Empty<string>();

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<string>();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var modelsElem) ||
            modelsElem.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>();
        foreach (var item in modelsElem.EnumerateArray())
        {
            string? name = null;
            if (item.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String)
                name = nProp.GetString();
            else if (item.TryGetProperty("model", out var mProp) && mProp.ValueKind == JsonValueKind.String)
                name = mProp.GetString();

            if (!string.IsNullOrWhiteSpace(name))
                result.Add(name.Trim());
        }

        return result;
    }

    private static async Task<bool> UnloadModelHttpAsync(
        Uri root,
        string modelName,
        HttpClient client,
        CancellationToken ct)
    {
        string url = root.AbsoluteUri.TrimEnd('/') + "/api/generate";
        string payload = JsonSerializer.Serialize(new { model = modelName, keep_alive = 0 });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await client.PostAsync(url, content, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    private static int StopLocalOllamaProcesses()
    {
        var stoppedIds = new HashSet<int>();
        int selfPid = Environment.ProcessId;

        foreach (int pid in SpawnedPids.Keys.ToArray())
        {
            SpawnedPids.TryRemove(pid, out _);
            if (pid == selfPid)
                continue;

            try
            {
                using var proc = Process.GetProcessById(pid);
                TryTerminateProcess(proc, stoppedIds);
            }
            catch
            {
                // Process already exited or inaccessible.
            }
        }

        Process[] allProcs;
        try
        {
            allProcs = Process.GetProcesses();
        }
        catch
        {
            return stoppedIds.Count;
        }

        foreach (var proc in allProcs)
        {
            try
            {
                if (proc.Id != selfPid && IsLocalOllamaProcessName(proc.ProcessName))
                    TryTerminateProcess(proc, stoppedIds);
            }
            catch
            {
                // Ignore transient inspection errors for exiting processes.
            }
            finally
            {
                proc.Dispose();
            }
        }

        return stoppedIds.Count;
    }

    private static bool IsLocalOllamaProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return name.Equals("ollama", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ollama app", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ollama_llama_server", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("ollama_llama_server", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryTerminateProcess(Process proc, HashSet<int> stoppedIds)
    {
        try
        {
            if (proc.HasExited)
                return;

            int pid = proc.Id;
            proc.Kill(entireProcessTree: true);
            proc.WaitForExit(600);
            stoppedIds.Add(pid);
        }
        catch
        {
            // Ignore race if process exited concurrently or lacks elevation.
        }
    }
}
