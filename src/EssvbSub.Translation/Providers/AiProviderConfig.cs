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

public sealed record AiProviderDescriptor(
    AiProviderKind Kind,
    string DisplayName,
    bool IsLocal,
    string DefaultEndpoint,
    string DefaultModel,
    IReadOnlyList<string> SuggestedModels,
    bool RequiresApiKey);

public static class AiProviderCatalog
{
    public static IReadOnlyList<AiProviderDescriptor> All { get; } =
    [
        new(AiProviderKind.Ollama, "Ollama (Yerel AI)", true, "http://127.0.0.1:11434/v1", "qwen2.5:7b",
            ["qwen2.5:7b", "gemma3:4b", "gemma3:12b", "llama3.1:8b", "qwen3:8b"], false),

        new(AiProviderKind.LmStudio, "LM Studio (Yerel AI)", true, "http://127.0.0.1:1234/v1", "qwen2.5-7b-instruct",
            ["qwen2.5-7b-instruct", "gemma-3-4b-it", "mistral-7b-instruct"], false),

        new(AiProviderKind.LocalMarian, "Çevrimdışı Hızlı Model (CTranslate2)", true, "", "opus-mt-en-tr",
            ["opus-mt-en-tr"], false),

        new(AiProviderKind.Gemini, "Google Gemini (Yüksek AI)", false, "https://generativelanguage.googleapis.com/v1beta", "gemini-2.5-flash",
            ["gemini-2.5-flash", "gemini-2.5-flash-lite", "gemini-2.5-pro", "gemini-2.0-flash"], true),

        new(AiProviderKind.Grok, "xAI Grok (Yüksek AI)", false, "https://api.x.ai/v1", "grok-3-mini",
            ["grok-3-mini", "grok-3", "grok-3-fast", "grok-2-latest"], true),

        new(AiProviderKind.OpenAi, "OpenAI ChatGPT (Yüksek AI)", false, "https://api.openai.com/v1", "gpt-4o-mini",
            ["gpt-4o-mini", "gpt-4.1-mini", "gpt-4.1", "gpt-4o"], true),

        new(AiProviderKind.Claude, "Anthropic Claude (Yüksek AI)", false, "https://api.anthropic.com/v1", "claude-3-5-haiku-latest",
            ["claude-3-5-haiku-latest", "claude-3-7-sonnet-latest", "claude-sonnet-4-20250514"], true),

        new(AiProviderKind.DeepL, "DeepL (Bulut Çeviri)", false, "https://api-free.deepl.com/v2", "deepl-free",
            ["deepl-free", "deepl-pro"], true),

        new(AiProviderKind.CustomOpenAi, "Özel OpenAI Uyumlu Sunucu / Hesap Proxy", false, "http://127.0.0.1:8080/v1", "gpt-4o-mini",
            ["gpt-4o-mini", "qwen2.5:7b"], false),
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
