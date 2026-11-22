// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text.Json.Serialization;

namespace EssvbSub.Translation.Providers;

[JsonConverter(typeof(JsonStringEnumConverter<AiProviderKind>))]
public enum AiProviderKind
{
    Ollama,
    LmStudio,
    LocalMarian,
    Gemini,
    Grok,
    OpenAi,
    Claude,
    DeepL,
    CustomOpenAi
}

public sealed record ModelPreset(string Id, string Label);

public sealed record AiProviderDescriptor(
    AiProviderKind Kind,
    string DisplayName,
    bool IsLocal,
    string DefaultEndpoint,
    string DefaultModel,
    IReadOnlyList<ModelPreset> RecommendedPresets,
    bool RequiresApiKey)
{
    public IReadOnlyList<string> SuggestedModels => RecommendedPresets.Select(p => p.Id).ToList();
}

public static class AiProviderCatalog
{
    public static IReadOnlyList<AiProviderDescriptor> All { get; } =
    [
        new(AiProviderKind.Ollama, "Ollama (Yerel AI)", true, "http://127.0.0.1:11434/v1", "gemma3:4b-it-q8_0",
            [
                new("gemma3:4b-it-q8_0", "gemma3:4b-it-q8_0  ★ Hızlı Yerel F/P"),
                new("qwen2.5:7b", "qwen2.5:7b  (Dengeli Türkçe)"),
                new("gemma3:4b", "gemma3:4b  (Hafif GPU)"),
                new("llama3.1:8b", "llama3.1:8b  (Genel)"),
                new("qwen3:8b", "qwen3:8b  (Akıl Yürütme)")
            ], false),

        new(AiProviderKind.LmStudio, "LM Studio (Yerel AI)", true, "http://127.0.0.1:1234/v1", "qwen2.5-7b-instruct",
            [
                new("qwen2.5-7b-instruct", "qwen2.5-7b-instruct  ★ Önerilen"),
                new("gemma-3-4b-it", "gemma-3-4b-it  (Hızlı)"),
                new("mistral-7b-instruct", "mistral-7b-instruct  (Genel)")
            ], false),

        new(AiProviderKind.LocalMarian, "Çevrimdışı Hızlı Model (CTranslate2)", true, "", "opus-mt-en-tr",
            [
                new("opus-mt-en-tr", "opus-mt-en-tr  ★ İnternetsiz CPU Çevirisi")
            ], false),

        new(AiProviderKind.Gemini, "Google Gemini (Yüksek AI)", false, "https://generativelanguage.googleapis.com/v1beta", "gemini-3.5-flash-lite",
            [
                new("gemini-3.5-flash-lite", "gemini-3.5-flash-lite  ★ En Ucuz & Hızlı (F/P Önerilen)"),
                new("gemini-flash-lite-latest", "gemini-flash-lite-latest  (Otomatik Güncel Ekonomik)"),
                new("gemini-3.8-flash", "gemini-3.8-flash  ★ Dengeli Yüksek Kalite"),
                new("gemini-3-flash-preview", "gemini-3-flash-preview  (Yeni Nesil Hızlı)"),
                new("gemini-3.1-flash-lite", "gemini-3.1-flash-lite  (Ekonomik Alternatif)"),
                new("gemini-flash-latest", "gemini-flash-latest  (Otomatik Güncel Flash)"),
                new("gemini-3.1-pro-preview", "gemini-3.1-pro-preview  (En Yüksek Edebi Kalite)"),
                new("gemma-4-26b-a4b-it", "gemma-4-26b-a4b-it  (Google Gemma 4)")
            ], true),

        new(AiProviderKind.Grok, "xAI Grok (Yüksek AI)", false, "https://api.x.ai/v1", "grok-3-mini",
            [
                new("grok-3-mini", "grok-3-mini  ★ En İyi F/P (Doğal Oyun Dili)"),
                new("grok-3-fast", "grok-3-fast  (Düşük Gecikme)"),
                new("grok-3", "grok-3  (En Yüksek Kalite)"),
                new("grok-2-latest", "grok-2-latest  (Alternatif)")
            ], true),

        new(AiProviderKind.OpenAi, "OpenAI ChatGPT (Yüksek AI)", false, "https://api.openai.com/v1", "gpt-4.1-mini",
            [
                new("gpt-4.1-mini", "gpt-4.1-mini  ★ En İyi F/P"),
                new("gpt-4o-mini", "gpt-4o-mini  (En Ekonomik)"),
                new("gpt-4.1", "gpt-4.1  (En Yüksek Kalite)"),
                new("gpt-4o", "gpt-4o  (Dengeli)")
            ], true),

        new(AiProviderKind.Claude, "Anthropic Claude (Yüksek AI)", false, "https://api.anthropic.com/v1", "claude-3-5-haiku-latest",
            [
                new("claude-3-5-haiku-latest", "claude-3-5-haiku-latest  ★ En Hızlı & Ekonomik (F/P)"),
                new("claude-3-7-sonnet-latest", "claude-3-7-sonnet-latest  (Yüksek Edebi Kalite)"),
                new("claude-sonnet-4-20250514", "claude-sonnet-4-20250514  (Yeni Nesil Sonnet)")
            ], true),

        new(AiProviderKind.DeepL, "DeepL (Bulut Çeviri)", false, "https://api-free.deepl.com/v2", "deepl-free",
            [
                new("deepl-free", "deepl-free  ★ Ücretsiz Kota (500K karakter/ay)"),
                new("deepl-pro", "deepl-pro  (Pro Hesap)")
            ], true),

        new(AiProviderKind.CustomOpenAi, "Özel OpenAI Uyumlu Sunucu / Hesap Proxy", false, "http://127.0.0.1:8080/v1", "gpt-4.1-mini",
            [
                new("gpt-4.1-mini", "gpt-4.1-mini  ★ Dengeli F/P"),
                new("qwen2.5:7b", "qwen2.5:7b  (Yerel OpenAI Köprüsü)")
            ], false),
    ];

    public static AiProviderDescriptor Get(AiProviderKind kind) =>
        All.First(p => p.Kind == kind);

    public static Uri ResolveOllamaRootUri(string baseUrl)
    {
        string trimmed = string.IsNullOrWhiteSpace(baseUrl)
            ? "http://127.0.0.1:11434"
            : baseUrl.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;

        foreach (var suffix in new[] { "/v1/chat/completions", "/chat/completions", "/api/chat", "/api/tags", "/v1" })
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[..^suffix.Length].TrimEnd('/');
                break;
            }
        }

        var builder = new UriBuilder(trimmed);
        if (string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            builder.Host = "127.0.0.1";
        return builder.Uri;
    }

    public static Uri ResolveChatCompletionsUri(string baseUrl)
    {
        string trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = "http://" + trimmed;

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^"/chat/completions".Length].TrimEnd('/');

        var builder = new UriBuilder(trimmed);
        if (string.Equals(builder.Host, "localhost", StringComparison.OrdinalIgnoreCase))
            builder.Host = "127.0.0.1"; // Avoid Windows IPv6 localhost resolution delay

        if (builder.Path is "" or "/")
            builder.Path = "/v1";

        string normalizedBase = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return new Uri(normalizedBase + "/chat/completions");
    }
}
