// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text.Json.Nodes;
using EssvbSub.Translation.Providers;

namespace EssvbSub.Translation;

public sealed record DiscoveredModel(string Id, string DisplayLabel, bool IsLocal = false)
{
    public override string ToString() => DisplayLabel;
}

public sealed record ModelDiscoveryResult(IReadOnlyList<DiscoveredModel> Models, bool LiveQueried, string StatusText);

/// <summary>
/// Automatically scans installed local models (Ollama /api/tags, LM Studio /v1/models)
/// and enumerates curated + live cloud AI models for quick dropdown selection.
/// </summary>
public static class ModelDiscoveryService
{
    private static readonly HttpClient LocalHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static readonly HttpClient CloudHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromSeconds(6)
    };

    private static readonly string[] IgnoredModelFragments =
    [
        "bge-", "nomic-embed", "mxbai-embed", "all-minilm", "embedding",
        "whisper", "tts", "dall-e", "moderation", "sora", "realtime"
    ];

    public static async Task<ModelDiscoveryResult> DiscoverModelsAsync(
        AiProviderKind provider,
        string? endpoint = null,
        string? apiKey = null,
        CancellationToken ct = default)
    {
        var desc = AiProviderCatalog.Get(provider);
        var curated = desc.SuggestedModels.Select(id => new DiscoveredModel(id, id, desc.IsLocal)).ToList();

        if (provider == AiProviderKind.LocalMarian)
            return new ModelDiscoveryResult(curated, false, "Çevrimdışı yerleşik model");

        try
        {
            if (provider == AiProviderKind.Ollama)
            {
                Uri root = AiProviderCatalog.ResolveOllamaRootUri(string.IsNullOrWhiteSpace(endpoint) ? desc.DefaultEndpoint : endpoint);
                string url = root.AbsoluteUri.TrimEnd('/') + "/api/tags";
                string json = await LocalHttp.GetStringAsync(url, ct).ConfigureAwait(false);
                var found = ParseOllamaTagsJson(json);
                if (found.Count > 0)
                    return new ModelDiscoveryResult(found, true, $"Ollama'da {found.Count} sohbet modeli bulundu");

                return new ModelDiscoveryResult(curated, true, "Ollama çalışıyor ama sohbet modeli yok");
            }

            if (provider == AiProviderKind.LmStudio)
            {
                string effective = string.IsNullOrWhiteSpace(endpoint) ? desc.DefaultEndpoint : endpoint;
                Uri chatUri = AiProviderCatalog.ResolveChatCompletionsUri(effective);
                string modelsUrl = chatUri.AbsoluteUri[..^"/chat/completions".Length] + "/models";
                string json = await LocalHttp.GetStringAsync(modelsUrl, ct).ConfigureAwait(false);
                var found = ParseOpenAiModelsJson(json, isLocal: true);
                if (found.Count > 0)
                    return new ModelDiscoveryResult(found, true, $"LM Studio'da {found.Count} model bulundu");

                return new ModelDiscoveryResult(curated, true, "LM Studio çalışıyor ama model yüklü değil");
            }

            if (provider is AiProviderKind.Gemini or AiProviderKind.Grok or AiProviderKind.OpenAi or AiProviderKind.Claude or AiProviderKind.CustomOpenAi)
            {
                string key = apiKey?.Trim() ?? "";
                if (string.IsNullOrEmpty(key) && provider != AiProviderKind.CustomOpenAi)
                    return new ModelDiscoveryResult(curated, false, "Hazır model listesi (canlı tarama için API anahtarı girin)");

                var live = await FetchCloudModelsAsync(provider, string.IsNullOrWhiteSpace(endpoint) ? desc.DefaultEndpoint : endpoint, key, ct).ConfigureAwait(false);
                var merged = MergeUnique(curated, live);
                return live.Count > 0
                    ? new ModelDiscoveryResult(merged, true, $"{desc.DisplayName}: {live.Count} canlı model listelendi")
                    : new ModelDiscoveryResult(curated, false, "Hazır önerilen modeller gösteriliyor");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            string reason = desc.IsLocal ? "yerel sunucu kapalı / erişilemiyor" : ex.Message;
            return new ModelDiscoveryResult(curated, false, $"Önerilen liste ({reason})");
        }

        return new ModelDiscoveryResult(curated, false, "Hazır model listesi");
    }

    internal static IReadOnlyList<DiscoveredModel> ParseOllamaTagsJson(string json)
    {
        var list = new List<DiscoveredModel>();
        var root = JsonNode.Parse(json);
        if (root?["models"] is not JsonArray arr)
            return list;

        foreach (var node in arr)
        {
            string? name = node?["name"]?.GetValue<string>() ?? node?["model"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (IsEmbeddingOrNonChatOllamaModel(node, name))
                continue;

            string? paramSize = node?["details"]?["parameter_size"]?.GetValue<string>();
            string? quant = node?["details"]?["quantization_level"]?.GetValue<string>();

            string label = (!string.IsNullOrWhiteSpace(paramSize), !string.IsNullOrWhiteSpace(quant)) switch
            {
                (true, true) => $"{name} ({paramSize} · {quant})",
                (true, false) => $"{name} ({paramSize})",
                (false, true) => $"{name} ({quant})",
                _ => name
            };

            list.Add(new DiscoveredModel(name, label, IsLocal: true));
        }

        return list;
    }

    internal static IReadOnlyList<DiscoveredModel> ParseOpenAiModelsJson(string json, bool isLocal)
    {
        var list = new List<DiscoveredModel>();
        var root = JsonNode.Parse(json);
        if (root?["data"] is not JsonArray arr)
            return list;

        foreach (var node in arr)
        {
            string? id = node?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || IsIgnoredModelName(id))
                continue;

            list.Add(new DiscoveredModel(id, id, isLocal));
        }

        return list;
    }

    private static async Task<IReadOnlyList<DiscoveredModel>> FetchCloudModelsAsync(
        AiProviderKind provider,
        string endpoint,
        string apiKey,
        CancellationToken ct)
    {
        string baseUrl = endpoint.Trim().TrimEnd('/');
        HttpRequestMessage req;

        if (provider == AiProviderKind.Gemini)
        {
            req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            req.Headers.Add("x-goog-api-key", apiKey);
        }
        else if (provider == AiProviderKind.Claude)
        {
            req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
        }
        else
        {
            Uri chatUri = AiProviderCatalog.ResolveChatCompletionsUri(baseUrl);
            string modelsUrl = chatUri.AbsoluteUri[..^"/chat/completions".Length] + "/models";
            req = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
            if (!string.IsNullOrWhiteSpace(apiKey))
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
        }

        using (req)
        using (var resp = await CloudHttp.SendAsync(req, ct).ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode)
                return [];

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return provider == AiProviderKind.Gemini
                ? ParseGeminiModelsJson(body)
                : ParseOpenAiModelsJson(body, isLocal: false);
        }
    }

    private static IReadOnlyList<DiscoveredModel> ParseGeminiModelsJson(string json)
    {
        var list = new List<DiscoveredModel>();
        var root = JsonNode.Parse(json);
        if (root?["models"] is not JsonArray arr)
            return list;

        foreach (var node in arr)
        {
            string? rawName = node?["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(rawName))
                continue;

            var methods = node?["supportedGenerationMethods"]?.AsArray()
                .Select(m => m?.GetValue<string>())
                .ToList() ?? [];
            if (methods.Count > 0 && !methods.Contains("generateContent", StringComparer.OrdinalIgnoreCase))
                continue;

            string id = rawName.StartsWith("models/", StringComparison.OrdinalIgnoreCase)
                ? rawName["models/".Length..]
                : rawName;
            if (IsIgnoredModelName(id))
                continue;

            list.Add(new DiscoveredModel(id, id, IsLocal: false));
        }

        return list;
    }

    private static bool IsEmbeddingOrNonChatOllamaModel(JsonNode? node, string name)
    {
        if (IsIgnoredModelName(name))
            return true;

        if (node?["capabilities"] is JsonArray caps && caps.Count > 0)
        {
            bool hasCompletion = caps.Any(c => string.Equals(c?.GetValue<string>(), "completion", StringComparison.OrdinalIgnoreCase));
            bool hasEmbedding = caps.Any(c => string.Equals(c?.GetValue<string>(), "embedding", StringComparison.OrdinalIgnoreCase));
            if (hasEmbedding && !hasCompletion)
                return true;
        }

        return false;
    }

    private static bool IsIgnoredModelName(string id) =>
        IgnoredModelFragments.Any(f => id.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static List<DiscoveredModel> MergeUnique(IReadOnlyList<DiscoveredModel> primary, IReadOnlyList<DiscoveredModel> secondary)
    {
        var result = new List<DiscoveredModel>(primary.Count + secondary.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var m in primary.Concat(secondary))
        {
            if (seen.Add(m.Id))
                result.Add(m);
        }

        return result;
    }
}
