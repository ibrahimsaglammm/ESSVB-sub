// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EssvbSub.Core.Models;
using EssvbSub.Core.Storage;

namespace EssvbSub.Translation;

/// <summary>
/// Lightweight offline sentence translator supporting an optional local CTranslate2 OPUS-MT worker process
/// (with zero network calls) and built-in game UI fallback dictionary.
/// Throws when no local dictionary match or CTranslate2 model exists instead of silently echoing English.
/// </summary>
public sealed class LocalMarianTranslator : ITextTranslator, IDisposable
{
    private static readonly Dictionary<string, string> BuiltInPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Press any button"] = "Herhangi bir tuşa bas",
        ["Press any key"] = "Herhangi bir tuşa bas",
        ["Continue"] = "Devam Et",
        ["New Game"] = "Yeni Oyun",
        ["Load Game"] = "Oyun Yükle",
        ["Settings"] = "Ayarlar",
        ["Options"] = "Seçenekler",
        ["Pause"] = "Duraklat",
        ["Resume"] = "Sürdür",
        ["Quit"] = "Çıkış",
        ["Save"] = "Kaydet",
        ["Loading..."] = "Yükleniyor...",
        ["Mission Failed"] = "Görev Başarısız",
        ["Objective Complete"] = "Hedef Tamamlandı",
        ["Follow me"] = "Beni takip et",
        ["Stay here"] = "Burada kal",
        ["Let's go"] = "Hadi gidelim",
        ["Watch out!"] = "Dikkat et!",
        ["Look out!"] = "Dikkat et!",
        ["Be careful."] = "Dikkatli ol.",
        ["Wait a second."] = "Bir saniye bekle.",
        ["What happened?"] = "Ne oldu?",
        ["I don't know."] = "Bilmiyorum.",
        ["Thank you."] = "Teşekkürler.",
    };

    private readonly string _runtimeDir;
    private readonly string _modelDir;
    private readonly object _gate = new();
    private Process? _worker;
    private bool _workerFailed;
    private bool _disposed;

    public LocalMarianTranslator(string? runtimeDir = null, string? modelDir = null)
    {
        _runtimeDir = runtimeDir
            ?? Environment.GetEnvironmentVariable("ESSVB_CT2_RUNTIME")
            ?? Path.Combine(AppContext.BaseDirectory, "ct2");

        _modelDir = modelDir
            ?? Path.Combine(EssvbPaths.DataDir, "models", "opus-mt-en-tr");
    }

    public string EngineId => "local:marian";
    public bool HasCTranslateRuntime =>
        File.Exists(Path.Combine(_runtimeDir, OperatingSystem.IsWindows() ? "python.exe" : "python3")) &&
        File.Exists(Path.Combine(_runtimeDir, "worker.py")) &&
        Directory.Exists(_modelDir);

    public bool IsReady => true;

    public bool CanTranslate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        return BuiltInPhrases.ContainsKey(trimmed) || (HasCTranslateRuntime && !_workerFailed);
    }

    public Task<string> TranslateAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
            return Task.FromResult("");

        if (BuiltInPhrases.TryGetValue(trimmed, out var exact))
            return Task.FromResult(exact);

        if (HasCTranslateRuntime && !_workerFailed)
            return Task.Run(() => TranslateWithWorker(trimmed, cancellationToken), cancellationToken);

        throw new InvalidOperationException("Çevrimdışı CTranslate2 modeli kurulu değil.");
    }

    private string TranslateWithWorker(string text, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                _worker ??= StartWorker();
                var payload = JsonSerializer.Serialize(new { text });
                _worker.StandardInput.WriteLine(payload);
                _worker.StandardInput.Flush();

                ct.ThrowIfCancellationRequested();
                string? line = _worker.StandardOutput.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                    throw new IOException("CTranslate2 worker closed output.");

                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("translation", out var val) && val.GetString() is { Length: > 0 } res)
                    return res;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                _workerFailed = true;
                StopWorker();
            }

            throw new InvalidOperationException("Çevrimdışı CTranslate2 işçisi yanıt vermedi.");
        }
    }

    private Process StartWorker()
    {
        string py = Path.Combine(_runtimeDir, OperatingSystem.IsWindows() ? "python.exe" : "python3");
        string script = Path.Combine(_runtimeDir, "worker.py");
        var psi = new ProcessStartInfo(py)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = _runtimeDir
        };
        psi.ArgumentList.Add("-u");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(_modelDir);

        var proc = Process.Start(psi) ?? throw new IOException("Failed to start local CTranslate2 process.");
        string? readyLine = proc.StandardOutput.ReadLine();
        if (readyLine is null || !readyLine.Contains("ready", StringComparison.OrdinalIgnoreCase))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            proc.Dispose();
            throw new IOException("Local CTranslate2 worker failed handshake.");
        }
        return proc;
    }

    private void StopWorker()
    {
        if (_worker is null) return;
        try
        {
            if (!_worker.HasExited)
                _worker.Kill(entireProcessTree: true);
        }
        catch { }
        finally
        {
            _worker.Dispose();
            _worker = null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopWorker();
        }
    }
}
