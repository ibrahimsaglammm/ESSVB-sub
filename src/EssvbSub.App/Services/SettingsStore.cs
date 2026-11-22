// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text.Json;
using EssvbSub.Capture.PlayStation;
using EssvbSub.Core.Models;
using EssvbSub.Core.Storage;
using EssvbSub.Translation.Providers;

namespace EssvbSub.App.Services;

public sealed class EssvbSettings
{
    public OperatingMode Mode { get; set; } = OperatingMode.PlayStation;
    public ConsoleKind ConsoleType { get; set; } = ConsoleKind.PlayStation5;
    public OutputTarget Output { get; set; } = OutputTarget.SamsungTv;
    public AiProviderKind Provider { get; set; } = AiProviderKind.Ollama;
    public string Model { get; set; } = "qwen2.5:7b";
    public string Endpoint { get; set; } = "http://127.0.0.1:11434/v1";
    public string CustomInstruction { get; set; } = "";
    public string MediaTitle { get; set; } = "";
    public string SourceLanguage { get; set; } = "en";
    public bool MediaUseAudioLoopback { get; set; }
    public bool AudioUseGemini { get; set; } = true;
    public string WhisperCliPath { get; set; } = "";
    public string WhisperModelPath { get; set; } = "";
    public string SamsungTvIp { get; set; } = "";
    public SamsungTvModel TvModel { get; set; } = SamsungTvModel.AutoDetect;
    public bool AutoLaunchTvApp { get; set; } = true;
    public int HdmiPort { get; set; } = 1;
    public string PlayStationIp { get; set; } = "";
    public bool ClickThroughOverlay { get; set; } = true;
    public double OverlayLeft { get; set; } = 160;
    public double OverlayTop { get; set; } = 820;
    public double OverlayWidth { get; set; } = 1100;
    public bool UseScreenRegion { get; set; }
    public int ScreenX { get; set; }
    public int ScreenY { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public CropRegion ScanRegion { get; set; } = CropRegion.Default;
    public SubtitleStyle Style { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static EssvbSettings Load()
    {
        try
        {
            if (File.Exists(EssvbPaths.SettingsFile))
            {
                return JsonSerializer.Deserialize<EssvbSettings>(File.ReadAllText(EssvbPaths.SettingsFile)) ?? new EssvbSettings();
            }
        }
        catch { }
        return new EssvbSettings();
    }

    public static void Save(EssvbSettings settings)
    {
        try
        {
            Directory.CreateDirectory(EssvbPaths.ConfigDir);
            string tmp = EssvbPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, EssvbPaths.SettingsFile, overwrite: true);
        }
        catch { }
    }

    public static string GetApiKey(AiProviderKind provider) =>
        SecureVault.Default.Load<string>($"apikey:{provider}") ?? "";

    public static void SaveApiKey(AiProviderKind provider, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            SecureVault.Default.Remove($"apikey:{provider}");
        else
            SecureVault.Default.Save($"apikey:{provider}", key.Trim());
    }

    public static string GetOrCreateServerToken()
    {
        var token = SecureVault.Default.Load<string>("server:token");
        if (!string.IsNullOrWhiteSpace(token))
            return token;
        token = Core.Server.SecureSubtitleServer.GenerateToken();
        SecureVault.Default.Save("server:token", token);
        return token;
    }

    public static string GetOrCreateInstanceId()
    {
        var id = SecureVault.Default.Load<string>("server:instanceId");
        if (!string.IsNullOrWhiteSpace(id))
            return id;
        id = Guid.NewGuid().ToString("D");
        SecureVault.Default.Save("server:instanceId", id);
        return id;
    }

    public static byte[]? LoadPsnAccountId() => SecureVault.Default.Load<byte[]>("psn:accountId");
    public static void SavePsnAccountId(byte[] id) => SecureVault.Default.Save("psn:accountId", id);

    public static RegisteredPlayStation? LoadRegisteredConsole(ConsoleKind kind) =>
        SecureVault.Default.Load<RegisteredPlayStation>($"ps:registered:{kind}");

    public static void SaveRegisteredConsole(RegisteredPlayStation console) =>
        SecureVault.Default.Save($"ps:registered:{console.Kind}", console);
}
