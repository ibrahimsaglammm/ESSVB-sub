// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EssvbSub.Core.Models;
using EssvbSub.Translation.Providers;

namespace EssvbSub.Translation;

public sealed record TranslationTestResult(bool Success, string Message, string? SampleTranslation = null);

/// <summary>
/// Unified, context-aware translation engine supporting Local AI (Ollama, LM Studio, Custom OpenAI),
/// Cloud AI (Gemini, Grok, OpenAI/ChatGPT, Claude, DeepL), and automatic local fallback on quota/network errors.
/// </summary>
public sealed partial class UniversalAiTranslator : ITextTranslator
{
    public const string DefaultSystemPrompt =
        "You translate English video game and movie subtitles into natural, conversational Turkish. " +
        "Use informal \"sen\" unless the dialogue is clearly formal. " +
        "Keep character names, proper nouns, and speaker prefixes like \"KRATOS:\" untranslated. " +
        "Preserve line breaks. OCR input may contain minor typos or dropped characters; infer the intended meaning cleanly. " +
        "If the line continues a previous sentence, keep grammatical continuity. " +
        "Reply with ONLY the Turkish translation string—no quotes, markdown fences, or explanations.";

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HttpClient SharedCloudHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient SharedLocalHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(18) };

    private readonly AiProviderKind _provider;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;
    private readonly string? _customInstruction;
    private readonly ITextTranslator? _fallback;
    private readonly HttpClient _http;
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public UniversalAiTranslator(
        AiProviderKind provider,
        string? apiKey = null,
        string? model = null,
        string? endpoint = null,
        string? customInstruction = null,
        ITextTranslator? fallback = null,
        HttpClient? httpClient = null)
    {
        _provider = provider;
        var descriptor = AiProviderCatalog.Get(provider);
        _apiKey = apiKey?.Trim() ?? "";
        _model = string.IsNullOrWhiteSpace(model) ? descriptor.DefaultModel : model.Trim();
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? descriptor.DefaultEndpoint : endpoint.Trim();
        _customInstruction = string.IsNullOrWhiteSpace(customInstruction) ? null : customInstruction.Trim();
        _fallback = fallback;
        _http = httpClient ?? (descriptor.IsLocal ? SharedLocalHttp : SharedCloudHttp);
    }

    public string EngineId => $"{_provider.ToString().ToLowerInvariant()}:{_model}";
    public bool IsReady
    {
        get
        {
            var desc = AiProviderCatalog.Get(_provider);
            return !desc.RequiresApiKey || !string.IsNullOrWhiteSpace(_apiKey);
        }
    }

    public event Action<string>? StatusWarning;

    public async Task<string> TranslateAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        if (_provider == AiProviderKind.LocalMarian)
        {
            return _fallback is not null
                ? await _fallback.TranslateAsync(text, context, mediaTitle, characterNames, cancellationToken).ConfigureAwait(false)
                : text;
        }

        if (!IsReady || DateTimeOffset.UtcNow < _pausedUntil)
            return await RunFallbackAsync(text, context, mediaTitle, characterNames, cancellationToken).ConfigureAwait(false);

        try
        {
            using var req = BuildHttpRequest(text, context, mediaTitle, characterNames);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            string rawBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                HandleFailureStatus(resp.StatusCode);
                return await RunFallbackAsync(text, context, mediaTitle, characterNames, cancellationToken).ConfigureAwait(false);
            }

            string extracted = ExtractResponseText(_provider, rawBody);
            string cleaned = CleanModelOutput(extracted);
            if (string.IsNullOrWhiteSpace(cleaned))
                return await RunFallbackAsync(text, context, mediaTitle, characterNames, cancellationToken).ConfigureAwait(false);

            return cleaned;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StatusWarning?.Invoke($"{_provider} bağlantı hatası ({ex.Message}); yerel yedeğe geçildi.");
            _pausedUntil = DateTimeOffset.UtcNow.AddSeconds(_provider is AiProviderKind.Ollama or AiProviderKind.LmStudio ? 5 : 20);
            return await RunFallbackAsync(text, context, mediaTitle, characterNames, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<TranslationTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!IsReady)
            return new TranslationTestResult(false, $"{AiProviderCatalog.Get(_provider).DisplayName} için API anahtarı girilmemiş.");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(45));

            using var req = BuildHttpRequest(
                "KRATOS: Stay close, boy. We have a long journey ahead.",
                [new TranslationContextLine("Where are we going?", "Nereye gidiyoruz?")],
                "God of War",
                ["KRATOS"]);

            using var resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new TranslationTestResult(false, $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})");

            string sample = CleanModelOutput(ExtractResponseText(_provider, body));
            return string.IsNullOrWhiteSpace(sample)
                ? new TranslationTestResult(false, "Model boş yanıt döndürdü.")
                : new TranslationTestResult(true, "Bağlantı başarılı.", sample);
        }
        catch (Exception ex)
        {
            return new TranslationTestResult(false, ex.Message);
        }
    }

    internal static void ValidateEndpointUri(string endpoint, bool requireHttpsForRemote)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("API uç noktası boş olamaz.");

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"Geçersiz API adresi: {endpoint}");

        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"Desteklenmeyen URI şeması ({uri.Scheme}); yalnızca http/https kullanılabilir.");

        bool isLoopback = uri.IsLoopback ||
                          uri.Host.Equals("127.0.0.1", StringComparison.Ordinal) ||
                          uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        if (requireHttpsForRemote && !isLoopback && uri.Scheme != "https")
            throw new InvalidOperationException($"Uzak bulut AI uç noktaları için HTTPS zorunludur: {uri.Host}");
    }

    internal HttpRequestMessage BuildHttpRequest(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames)
    {
        if (_provider != AiProviderKind.LocalMarian)
        {
            bool requireHttps = !AiProviderCatalog.Get(_provider).IsLocal && _provider != AiProviderKind.CustomOpenAi;
            ValidateEndpointUri(_endpoint, requireHttps);
        }

        string systemPrompt = string.IsNullOrWhiteSpace(_customInstruction)
            ? DefaultSystemPrompt
            : $"{DefaultSystemPrompt}\n\nExtra user instruction: {_customInstruction}";
        string userPrompt = BuildUserPrompt(text, context, mediaTitle, characterNames);

        return _provider switch
        {
            AiProviderKind.Ollama => BuildOllamaNativeRequest(systemPrompt, userPrompt),
            AiProviderKind.Gemini => BuildGeminiRequest(systemPrompt, userPrompt),
            AiProviderKind.Claude => BuildClaudeRequest(systemPrompt, userPrompt),
            AiProviderKind.DeepL => BuildDeepLRequest(text, context),
            _ => BuildOpenAiCompatibleRequest(systemPrompt, userPrompt)
        };
    }

    private HttpRequestMessage BuildOllamaNativeRequest(string system, string user)
    {
        Uri root = AiProviderCatalog.ResolveOllamaRootUri(_endpoint);
        var req = new HttpRequestMessage(HttpMethod.Post, root.AbsoluteUri.TrimEnd('/') + "/api/chat");
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["stream"] = false,
            ["think"] = false,
            ["keep_alive"] = "24h",
            ["options"] = new JsonObject
            {
                ["temperature"] = 0,
                ["num_predict"] = 256
            }
        };

        req.Content = new StringContent(body.ToJsonString(CompactJson), Encoding.UTF8, "application/json");
        return req;
    }

    private HttpRequestMessage BuildGeminiRequest(string system, string user)
    {
        string baseUrl = _endpoint.TrimEnd('/');
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/models/{Uri.EscapeDataString(_model)}:generateContent");
        req.Headers.Add("x-goog-api-key", _apiKey);

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = user })
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0.1,
                ["maxOutputTokens"] = 512
            }
        };

        req.Content = new StringContent(body.ToJsonString(CompactJson), Encoding.UTF8, "application/json");
        return req;
    }

    private HttpRequestMessage BuildClaudeRequest(string system, string user)
    {
        string baseUrl = _endpoint.TrimEnd('/');
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/messages");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = 512,
            ["system"] = system,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user })
        };

        req.Content = new StringContent(body.ToJsonString(CompactJson), Encoding.UTF8, "application/json");
        return req;
    }

    private HttpRequestMessage BuildDeepLRequest(string text, IReadOnlyList<TranslationContextLine> context)
    {
        bool usePro = _model.Contains("pro", StringComparison.OrdinalIgnoreCase) || !_apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
        string baseUrl = usePro ? "https://api.deepl.com/v2" : "https://api-free.deepl.com/v2";
        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/translate");
        req.Headers.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {_apiKey}");

        var body = new JsonObject
        {
            ["text"] = new JsonArray(JsonValue.Create(text)),
            ["target_lang"] = "TR"
        };
        if (context.Count > 0)
            body["context"] = string.Join(" ", context.Select(c => c.Source));

        req.Content = new StringContent(body.ToJsonString(CompactJson), Encoding.UTF8, "application/json");
        return req;
    }

    private HttpRequestMessage BuildOpenAiCompatibleRequest(string system, string user)
    {
        Uri uri = AiProviderCatalog.ResolveChatCompletionsUri(_endpoint);
        var req = new HttpRequestMessage(HttpMethod.Post, uri);
        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["temperature"] = 0,
            ["max_tokens"] = 512,
            ["stream"] = false
        };

        if (_provider == AiProviderKind.Ollama)
        {
            body["think"] = false;
            body["keep_alive"] = "24h";
        }

        req.Content = new StringContent(body.ToJsonString(CompactJson), Encoding.UTF8, "application/json");
        return req;
    }

    internal static string BuildUserPrompt(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(mediaTitle))
            parts.Add($"Title: {mediaTitle}");

        if (characterNames is { Count: > 0 })
            parts.Add($"Keep these character names untranslated: {string.Join(", ", characterNames)}");

        if (context.Count > 0)
        {
            var sb = new StringBuilder("Recent context (for reference only, do not re-translate):\n");
            foreach (var line in context)
            {
                sb.Append("- EN: ").Append(line.Source);
                if (!string.IsNullOrWhiteSpace(line.Translation))
                    sb.Append(" | TR: ").Append(line.Translation);
                sb.Append('\n');
            }
            parts.Add(sb.ToString().TrimEnd());
        }

        parts.Add($"Translate to natural Turkish:\n{text}");
        return string.Join("\n\n", parts);
    }

    internal static string ExtractResponseText(AiProviderKind provider, string json)
    {
        var root = JsonNode.Parse(json);
        return provider switch
        {
            AiProviderKind.Gemini => string.Concat(
                (root?["candidates"]?.AsArray().FirstOrDefault()?["content"]?["parts"]?.AsArray() ?? [])
                    .Where(p => p?["thought"]?.GetValueKind() is not JsonValueKind.True)
                    .Select(p => p?["text"]?.GetValue<string>() ?? "")),

            AiProviderKind.Claude => string.Concat(
                (root?["content"]?.AsArray() ?? [])
                    .Where(b => b?["type"]?.GetValue<string>() == "text")
                    .Select(b => b?["text"]?.GetValue<string>() ?? "")),

            AiProviderKind.DeepL =>
                root?["translations"]?.AsArray().FirstOrDefault()?["text"]?.GetValue<string>() ?? "",

            AiProviderKind.Ollama =>
                root?["message"]?["content"]?.GetValue<string>()
                ?? root?["choices"]?.AsArray().FirstOrDefault()?["message"]?["content"]?.GetValue<string>()
                ?? "",

            _ => root?["choices"]?.AsArray().FirstOrDefault()?["message"]?["content"]?.GetValue<string>() ?? ""
        };
    }

    internal static string CleanModelOutput(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string text = raw.Trim();

        // Strip reasoning model <think>...</think> blocks
        int thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
            text = text[(thinkEnd + "</think>".Length)..].Trim();

        // Strip common LLM control tokens
        text = ControlTokenRegex().Replace(text, "").Trim();

        // Strip markdown fences if model emitted them
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = string.Join('\n',
                text.Split('\n')
                    .Skip(1)
                    .TakeWhile(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))).Trim();
        }

        // Unwrap surrounding quotes if model wrapped the single line
        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”')))
            text = text[1..^1].Trim();

        return text;
    }

    private void HandleFailureStatus(HttpStatusCode status)
    {
        int code = (int)status;
        if (code == 429)
        {
            StatusWarning?.Invoke($"{_provider} istek limitine ulaştı (429); geçici olarak yerel yedeğe geçildi.");
            _pausedUntil = DateTimeOffset.UtcNow.AddSeconds(20);
        }
        else if (code is 401 or 403)
        {
            StatusWarning?.Invoke($"{_provider} API anahtarı geçersiz ({code}).");
            _pausedUntil = DateTimeOffset.UtcNow.AddMinutes(1);
        }
        else if (code == 404)
        {
            StatusWarning?.Invoke($"{_provider} modeli ({_model}) bulunamadı (HTTP 404); 'Modelleri Tara' ile yüklü bir model seçin.");
            _pausedUntil = DateTimeOffset.UtcNow.AddSeconds(10);
        }
        else
        {
            StatusWarning?.Invoke($"{_provider} hata döndürdü (HTTP {code}); yerel yedeğe geçildi.");
            _pausedUntil = DateTimeOffset.UtcNow.AddSeconds(15);
        }
    }

    private async Task<string> RunFallbackAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        CancellationToken ct)
    {
        if (_fallback is null)
            return text;

        return await _fallback.TranslateAsync(text, context, mediaTitle, characterNames, ct).ConfigureAwait(false);
    }

    [GeneratedRegex(@"</?s>|<\|[A-Za-z0-9_]{1,32}\|>|</?(?:start|end)_of_turn>|</?(?:eos|bos|pad)>")]
    private static partial Regex ControlTokenRegex();
}
