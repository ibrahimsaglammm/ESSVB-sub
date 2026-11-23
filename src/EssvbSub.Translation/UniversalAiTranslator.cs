// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;
using EssvbSub.Core.Storage;
using EssvbSub.Translation.Providers;

namespace EssvbSub.Translation;

public sealed record TranslationTestResult(bool Success, string Message, string? SampleTranslation = null);

public sealed record RateLimitInfo(int DelaySeconds, bool IsDailyQuota, int ReportedRpmQuota);

/// <summary>
/// Unified, multi-language, genre-aware translation engine supporting Local AI (Ollama, LM Studio, Custom OpenAI),
/// Cloud AI (Gemini, Grok, OpenAI/ChatGPT, Claude, DeepL), proactive RPM budgeting, structured 429 backoff,
/// Gemini safetySettings=BLOCK_NONE, and safe local Ollama backup without ever poisoning caches.
/// </summary>
public sealed partial class UniversalAiTranslator : ITranslationOutcomeTranslator
{
    public const string DefaultSystemPrompt =
        "You translate video game and movie subtitles into natural, conversational Turkish. " +
        "Use informal \"sen\" unless the dialogue is clearly formal. " +
        "Keep character names, proper nouns, and speaker prefixes like \"KRATOS:\" untranslated. " +
        "Preserve line breaks. OCR input may contain minor typos or dropped characters; infer the intended meaning cleanly. " +
        "If the line continues a previous sentence, keep grammatical continuity. " +
        "Reply with ONLY the Turkish translation string—no quotes, markdown fences, or explanations.";

    public static readonly string[] GeminiSafetyCategories =
    [
        "HARM_CATEGORY_HARASSMENT",
        "HARM_CATEGORY_HATE_SPEECH",
        "HARM_CATEGORY_SEXUALLY_EXPLICIT",
        "HARM_CATEGORY_DANGEROUS_CONTENT",
        "HARM_CATEGORY_CIVIC_INTEGRITY"
    ];

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HttpClient SharedCloudHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HttpClient SharedLocalHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(15) };

    private readonly AiProviderKind _provider;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;
    private readonly string? _customInstruction;
    private readonly string _sourceLanguage;
    private readonly string _genrePresetId;
    private readonly UserGlossaryStore? _userGlossary;
    private readonly ITextTranslator? _fallback;
    private readonly HttpClient _http;
    private readonly RequestBudget _budget;

    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private string? _pauseAlertMessage;
    private bool _pauseWasRateLimit;
    private string? _lastEmittedAlert;

    public UniversalAiTranslator(
        AiProviderKind provider,
        string? apiKey = null,
        string? model = null,
        string? endpoint = null,
        string? customInstruction = null,
        ITextTranslator? fallback = null,
        HttpClient? httpClient = null,
        string sourceLanguage = "auto",
        string genrePresetId = "general",
        UserGlossaryStore? userGlossary = null,
        int maxRequestsPerMinute = -1)
    {
        _provider = provider;
        var descriptor = AiProviderCatalog.Get(provider);
        _apiKey = apiKey?.Trim() ?? "";
        _model = string.IsNullOrWhiteSpace(model) ? descriptor.DefaultModel : model.Trim();
        _endpoint = string.IsNullOrWhiteSpace(endpoint) ? descriptor.DefaultEndpoint : endpoint.Trim();
        _customInstruction = string.IsNullOrWhiteSpace(customInstruction) ? null : customInstruction.Trim();
        _sourceLanguage = string.IsNullOrWhiteSpace(sourceLanguage) ? "auto" : sourceLanguage.Trim();
        _genrePresetId = string.IsNullOrWhiteSpace(genrePresetId) ? "general" : genrePresetId.Trim();
        _userGlossary = userGlossary;
        _fallback = fallback;
        _http = httpClient ?? (descriptor.IsLocal ? SharedLocalHttp : SharedCloudHttp);

        int rpm = maxRequestsPerMinute >= 0
            ? maxRequestsPerMinute
            : RequestBudget.DefaultLimitFor(provider, _model);
        _budget = new RequestBudget(rpm);
    }

    public string EngineId => $"{_provider.ToString().ToLowerInvariant()}:{_model}";

    public string CacheFingerprint
    {
        get
        {
            string raw = $"{_sourceLanguage}\0{_genrePresetId}\0{_customInstruction ?? ""}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8].ToLowerInvariant();
        }
    }

    public bool IsReady
    {
        get
        {
            var desc = AiProviderCatalog.Get(_provider);
            return !desc.RequiresApiKey || !string.IsNullOrWhiteSpace(_apiKey);
        }
    }

    public event Action<string>? StatusWarning;
    public event Action<string?>? AlertChanged;

    public async Task<string> TranslateAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        CancellationToken cancellationToken = default)
    {
        var outcome = await TranslateDetailedAsync(text, context, mediaTitle, characterNames, cancellationToken)
            .ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(outcome.Text) ? text : outcome.Text;
    }

    public async Task<TranslationOutcome> TranslateDetailedAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationOutcome("");

        if (_provider == AiProviderKind.LocalMarian)
            return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, isRateLimited: false,
                baseAlert: "⚠ Çevrimdışı CTranslate2 modeli kurulu değil — Ollama veya AI API seçin", cancellationToken).ConfigureAwait(false);

        if (!IsReady)
        {
            string keyMsg = $"⚠ {AiProviderCatalog.Get(_provider).DisplayName} için API anahtarı girilmemiş";
            return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, isRateLimited: false, keyMsg, cancellationToken)
                .ConfigureAwait(false);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < _pausedUntil)
        {
            int remSec = Math.Max(1, (int)Math.Ceiling((_pausedUntil - now).TotalSeconds));
            string pausedMsg = _pauseAlertMessage is { Length: > 0 }
                ? $"{_pauseAlertMessage} ({remSec} sn)"
                : $"⚠ {AiProviderCatalog.Get(_provider).DisplayName} beklemede ({remSec} sn)";
            return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, _pauseWasRateLimit, pausedMsg, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!_budget.TryConsume(out int budgetWait))
        {
            string rpmMsg = $"⚠ {AiProviderCatalog.Get(_provider).DisplayName} dakikalık kota sınırına yaklaştı ({budgetWait} sn)";
            SetPause(TimeSpan.FromSeconds(Math.Min(budgetWait, 15)), isRateLimited: true, rpmMsg);
            return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, isRateLimited: true, rpmMsg, cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using var req = BuildHttpRequest(text, context, mediaTitle, characterNames);
            using var resp = await _http.SendAsync(req, cancellationToken).ConfigureAwait(false);
            string rawBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var (alert, isRateLimited) = HandleFailureResponse(resp.StatusCode, resp.Headers, rawBody);
                return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, isRateLimited, alert, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (TryDetectBlockedResponse(_provider, rawBody, out string blockReason))
            {
                string blockAlert = $"⚠ {_provider} güvenlik engeli ({blockReason})";
                StatusWarning?.Invoke(blockAlert);
                return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, isRateLimited: false, blockAlert, cancellationToken)
                    .ConfigureAwait(false);
            }

            string extracted = ExtractResponseText(_provider, rawBody);
            string cleaned = CleanModelOutput(extracted);
            if (string.IsNullOrWhiteSpace(cleaned) || TextMatch.IsUntranslatedEcho(text, cleaned))
            {
                string emptyAlert = string.IsNullOrWhiteSpace(cleaned)
                    ? $"⚠ {_provider} boş yanıt döndürdü"
                    : $"⚠ {_provider} çeviriyi İngilizce bıraktı";
                return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, isRateLimited: false, emptyAlert, cancellationToken)
                    .ConfigureAwait(false);
            }

            EmitAlert(null);
            return new TranslationOutcome(cleaned, IsFallback: false, IsRateLimited: false, AlertMessage: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            string errAlert = $"⚠ {_provider} bağlantı hatası ({ex.Message})";
            StatusWarning?.Invoke(errAlert);
            int pauseSec = _provider is AiProviderKind.Ollama or AiProviderKind.LmStudio ? 4 : 10;
            SetPause(TimeSpan.FromSeconds(pauseSec), isRateLimited: false, errAlert);
            return await RunFallbackOutcomeAsync(text, context, mediaTitle, characterNames, isRateLimited: false, errAlert, cancellationToken)
                .ConfigureAwait(false);
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
            {
                string detail = TryExtractApiErrorMessage(body);
                return new TranslationTestResult(false, string.IsNullOrWhiteSpace(detail)
                    ? $"HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})"
                    : $"HTTP {(int)resp.StatusCode}: {detail}");
            }

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

        string systemPrompt = BuildSystemPrompt();
        var preset = GameGenreCatalog.Get(_genrePresetId);
        IReadOnlyDictionary<string, string>? userTerms = null;
        IReadOnlyList<string>? userKeep = null;
        if (_userGlossary is not null)
            (userTerms, userKeep) = _userGlossary.GetSnapshot();
        var matchedGlossary = GameGenreCatalog.MatchTermsForLine(preset, text, userTerms, userKeep);
        string userPrompt = BuildUserPrompt(text, context, mediaTitle, characterNames, matchedGlossary);

        return _provider switch
        {
            AiProviderKind.Ollama => BuildOllamaNativeRequest(systemPrompt, userPrompt),
            AiProviderKind.Gemini => BuildGeminiRequest(systemPrompt, userPrompt),
            AiProviderKind.Claude => BuildClaudeRequest(systemPrompt, userPrompt),
            AiProviderKind.DeepL => BuildDeepLRequest(text, context),
            _ => BuildOpenAiCompatibleRequest(systemPrompt, userPrompt)
        };
    }

    internal string BuildSystemPrompt()
    {
        var langOpt = SourceLanguageCatalog.Get(_sourceLanguage);
        string langRule = langOpt.Code == "auto"
            ? "Detect the subtitle language automatically (English, Japanese, Korean, Chinese, Russian, German, French, Spanish, Arabic, etc.) and translate it into natural, conversational Turkish."
            : $"Translate {langOpt.EnglishName} video game and movie subtitles into natural, conversational Turkish.";

        var sb = new StringBuilder();
        sb.Append(langRule).Append(' ');
        sb.Append("Use informal \"sen\" unless the dialogue is clearly formal. ");
        sb.Append("Keep character names, proper nouns, and speaker prefixes like \"KRATOS:\" untranslated. ");
        sb.Append("Preserve line breaks. OCR input may contain minor typos or dropped characters; infer the intended meaning cleanly. ");
        sb.Append("If the line continues a previous sentence, keep grammatical continuity. ");
        sb.Append("Reply with ONLY the Turkish translation string—no quotes, markdown fences, or explanations.");

        var preset = GameGenreCatalog.Get(_genrePresetId);
        if (!string.IsNullOrWhiteSpace(preset.PromptGuidance))
            sb.Append("\n\n").Append(preset.PromptGuidance);

        if (!string.IsNullOrWhiteSpace(_customInstruction))
            sb.Append("\n\nExtra user instruction: ").Append(_customInstruction);

        return sb.ToString();
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

        var safetyArray = new JsonArray();
        foreach (string category in GeminiSafetyCategories)
        {
            safetyArray.Add(new JsonObject
            {
                ["category"] = category,
                ["threshold"] = "BLOCK_NONE"
            });
        }

        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = user })
            }),
            ["safetySettings"] = safetyArray,
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
            ["target_lang"] = "TR",
            ["formality"] = "prefer_less",
            ["preserve_formatting"] = true
        };

        var langOpt = SourceLanguageCatalog.Get(_sourceLanguage);
        if (!string.IsNullOrEmpty(langOpt.DeepLCode))
            body["source_lang"] = langOpt.DeepLCode;

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
        IReadOnlyList<string>? characterNames) =>
        BuildUserPrompt(text, context, mediaTitle, characterNames, null);

    internal static string BuildUserPrompt(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        MatchedGlossaryContext? glossary)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(mediaTitle))
            parts.Add($"Title: {mediaTitle}");

        var keepAll = new List<string>();
        var keepSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (characterNames is not null)
        {
            foreach (var n in characterNames)
            {
                if (!string.IsNullOrWhiteSpace(n) && keepSeen.Add(n))
                    keepAll.Add(n);
            }
        }
        if (glossary?.KeepNames is not null)
        {
            foreach (var n in glossary.KeepNames)
            {
                if (!string.IsNullOrWhiteSpace(n) && keepSeen.Add(n))
                    keepAll.Add(n);
            }
        }
        if (keepAll.Count > 0)
            parts.Add($"Keep these character names untranslated: {string.Join(", ", keepAll)}");

        if (glossary?.Terms is { Count: > 0 } terms)
            parts.Add($"Glossary (preferred Turkish terms): {string.Join("; ", terms.Select(t => $"{t.Source} = {t.Target}"))}");

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

        int thinkEnd = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (thinkEnd >= 0)
            text = text[(thinkEnd + "</think>".Length)..].Trim();

        text = ControlTokenRegex().Replace(text, "").Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            text = string.Join('\n',
                text.Split('\n')
                    .Skip(1)
                    .TakeWhile(l => !l.TrimStart().StartsWith("```", StringComparison.Ordinal))).Trim();
        }

        if (text.Length >= 2 && ((text[0] == '"' && text[^1] == '"') || (text[0] == '“' && text[^1] == '”')))
            text = text[1..^1].Trim();

        return text;
    }

    internal static RateLimitInfo ParseRateLimitDetails(HttpResponseHeaders? headers, string responseBody)
    {
        int headerSeconds = 0;
        if (headers?.RetryAfter is { } ra)
        {
            if (ra.Delta is { } delta)
                headerSeconds = Math.Max(0, (int)Math.Ceiling(delta.TotalSeconds));
            else if (ra.Date is { } date)
                headerSeconds = Math.Max(0, (int)Math.Ceiling((date - DateTimeOffset.UtcNow).TotalSeconds));
        }

        int rpcDelaySeconds = 0;
        bool isDaily = false;
        int reportedRpm = 0;

        try
        {
            var root = JsonNode.Parse(responseBody);
            var errObj = root?["error"];
            string errMsg = errObj?["message"]?.GetValue<string>() ?? "";
            if (errMsg.Contains("per day", StringComparison.OrdinalIgnoreCase) ||
                errMsg.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
                errMsg.Contains("credit_balance", StringComparison.OrdinalIgnoreCase))
            {
                isDaily = true;
            }

            if (errObj?["details"]?.AsArray() is { } details)
            {
                foreach (var detail in details)
                {
                    string type = detail?["@type"]?.GetValue<string>() ?? "";
                    if (type.Contains("RetryInfo", StringComparison.OrdinalIgnoreCase))
                    {
                        string? delayStr = detail?["retryDelay"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(delayStr))
                        {
                            string numPart = delayStr.Trim().TrimEnd('s', 'S');
                            if (double.TryParse(numPart, NumberStyles.Float, CultureInfo.InvariantCulture, out double sec))
                                rpcDelaySeconds = Math.Max(rpcDelaySeconds, (int)Math.Ceiling(sec));
                        }
                    }
                    else if (type.Contains("QuotaFailure", StringComparison.OrdinalIgnoreCase) &&
                             detail?["violations"]?.AsArray() is { } violations)
                    {
                        foreach (var v in violations)
                        {
                            string qId = v?["quotaId"]?.GetValue<string>() ?? "";
                            if (qId.Contains("PerDay", StringComparison.OrdinalIgnoreCase))
                                isDaily = true;

                            string qVal = v?["quotaValue"]?.ToString() ?? "";
                            if (int.TryParse(qVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out int qNum) &&
                                qId.Contains("PerMinute", StringComparison.OrdinalIgnoreCase))
                            {
                                reportedRpm = qNum;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        int effectiveDelay = Math.Max(headerSeconds, rpcDelaySeconds);
        if (effectiveDelay <= 0)
            effectiveDelay = isDaily ? 60 : 20;

        effectiveDelay = Math.Clamp(effectiveDelay, 2, 120);
        return new RateLimitInfo(effectiveDelay, isDaily, reportedRpm);
    }

    private (string Alert, bool IsRateLimited) HandleFailureResponse(HttpStatusCode status, HttpResponseHeaders headers, string body)
    {
        int code = (int)status;
        if (code == 429 || code == 402)
        {
            var rl = ParseRateLimitDetails(headers, body);
            if (rl.ReportedRpmQuota > 0)
                _budget.LowerLimit(rl.ReportedRpmQuota);

            string alert = rl.IsDailyQuota || code == 402
                ? $"⚠ {_provider} günlük/ücretli kota doldu — Ollama veya başka model seçin"
                : $"⚠ {_provider} dakikalık kota doldu (429) — {rl.DelaySeconds} sn bekleniyor";

            SetPause(TimeSpan.FromSeconds(rl.DelaySeconds), isRateLimited: true, alert);
            StatusWarning?.Invoke(alert);
            return (alert, true);
        }

        if (code is 401 or 403)
        {
            string alert = $"⚠ {_provider} API anahtarı geçersiz ({code})";
            SetPause(TimeSpan.FromMinutes(1), isRateLimited: false, alert);
            StatusWarning?.Invoke(alert);
            return (alert, false);
        }

        if (code == 404)
        {
            string alert = $"⚠ {_provider} modeli ({_model}) bulunamadı (404) — 'Modelleri Tara' yapın";
            SetPause(TimeSpan.FromSeconds(15), isRateLimited: false, alert);
            StatusWarning?.Invoke(alert);
            return (alert, false);
        }

        string genericAlert = $"⚠ {_provider} hata (HTTP {code})";
        SetPause(TimeSpan.FromSeconds(10), isRateLimited: false, genericAlert);
        StatusWarning?.Invoke(genericAlert);
        return (genericAlert, false);
    }

    private void SetPause(TimeSpan duration, bool isRateLimited, string alertMessage)
    {
        _pausedUntil = DateTimeOffset.UtcNow + duration;
        _pauseWasRateLimit = isRateLimited;
        _pauseAlertMessage = alertMessage;
    }

    private void EmitAlert(string? message)
    {
        string? normalized = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        if (string.Equals(_lastEmittedAlert, normalized, StringComparison.Ordinal))
            return;

        _lastEmittedAlert = normalized;
        AlertChanged?.Invoke(normalized);
    }

    private async Task<TranslationOutcome> RunFallbackOutcomeAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        bool isRateLimited,
        string baseAlert,
        CancellationToken ct)
    {
        if (_fallback is not null &&
            !(_fallback is LocalMarianTranslator lm && !lm.CanTranslate(text)))
        {
            try
            {
                string fb = await _fallback.TranslateAsync(text, context, mediaTitle, characterNames, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(fb) && !TextMatch.IsUntranslatedEcho(text, fb))
                {
                    string backupAlert = $"{baseAlert} · Yerel yedek aktif";
                    EmitAlert(backupAlert);
                    return new TranslationOutcome(fb, IsFallback: true, IsRateLimited: isRateLimited, AlertMessage: backupAlert);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch { }
        }

        EmitAlert(baseAlert);
        return new TranslationOutcome("", IsFallback: true, IsRateLimited: isRateLimited, AlertMessage: baseAlert);
    }

    private static bool TryDetectBlockedResponse(AiProviderKind provider, string json, out string reason)
    {
        reason = "";
        if (provider != AiProviderKind.Gemini)
            return false;

        try
        {
            var root = JsonNode.Parse(json);
            string? promptBlock = root?["promptFeedback"]?["blockReason"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(promptBlock))
            {
                reason = promptBlock;
                return true;
            }

            string? finish = root?["candidates"]?.AsArray().FirstOrDefault()?["finishReason"]?.GetValue<string>();
            if (finish is "SAFETY" or "PROHIBITED_CONTENT" or "RECITATION")
            {
                reason = finish;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static string TryExtractApiErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            string? msg = node?["error"]?["message"]?.GetValue<string>()
                       ?? node?["message"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(msg))
                return "";
            msg = msg.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return msg.Length > 200 ? msg[..200] + "…" : msg;
        }
        catch
        {
            return "";
        }
    }

    [GeneratedRegex(@"</?s>|<\|[A-Za-z0-9_]{1,32}\|>|</?(?:start|end)_of_turn>|</?(?:eos|bos|pad)>")]
    private static partial Regex ControlTokenRegex();
}
