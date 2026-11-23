// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EssvbSub.Capture.Ocr;
using EssvbSub.Capture.TvInstall;
using EssvbSub.Core;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;
using EssvbSub.Core.Server;
using EssvbSub.Core.Storage;
using EssvbSub.Core.Updates;
using EssvbSub.Translation;
using EssvbSub.Translation.Providers;

namespace EssvbSub.Tests;

public sealed class EssvbSubTests
{
    [Fact]
    public void Stabilizer_WaitsForTypewriterThenEmitsCleanLineAndClears()
    {
        long now = 0;
        var stabilizer = new SubtitleStabilizer(stableFrames: 2, clearAfterMs: 750, clock: () => now);

        // Partial typewriter fragments without punctuation do not trigger immediately
        Assert.Equal(StabilizerActionKind.None, stabilizer.Feed("Let's get").Kind);
        now += 150;
        Assert.Equal(StabilizerActionKind.None, stabilizer.Feed("Let's get out of here").Kind);
        now += 150;

        // Clean punctuated line triggers on fast path
        var action = stabilizer.Feed("Let's get out of here.");
        Assert.Equal(StabilizerActionKind.Translate, action.Kind);
        Assert.Equal("Let's get out of here.", action.Text);

        // Empty frames clear after threshold
        now += 200;
        Assert.Equal(StabilizerActionKind.None, stabilizer.Feed("").Kind);
        now += 800;
        Assert.Equal(StabilizerActionKind.Clear, stabilizer.Feed("").Kind);

        // Flicker back within RestoreWithinMs restores without re-translation
        now += 200;
        var restored = stabilizer.Feed("Let's get out of here.");
        Assert.Equal(StabilizerActionKind.Restore, restored.Kind);
    }

    [Fact]
    public void OcrPreprocessor_DetectsWhiteShadowTextAndSignatureDiff()
    {
        var frameA = Frame.Blank(80, 40);
        var frameB = Frame.Blank(80, 40);

        // Draw horizontal white glyph strokes with bottom-right dark shadows so 32x12 signature sampling hits them
        foreach (int y in new[] { 13, 16 })
        {
            for (int x = 16; x <= 26; x++)
            {
                int gp = (y * frameB.Stride) + (x * 4);
                frameB.Pixels[gp] = 250; frameB.Pixels[gp + 1] = 250; frameB.Pixels[gp + 2] = 250; frameB.Pixels[gp + 3] = 255;

                int sp = ((y + 2) * frameB.Stride) + ((x + 2) * 4);
                frameB.Pixels[sp] = 10; frameB.Pixels[sp + 1] = 10; frameB.Pixels[sp + 2] = 10; frameB.Pixels[sp + 3] = 255;
            }
        }

        int p = (13 * frameB.Stride) + (20 * 4);
        var sigA = RegionCropper.ComputeSignature(frameA);
        var sigB = RegionCropper.ComputeSignature(frameB);
        Assert.True(RegionCropper.DifferenceRatio(sigA, sigB) > 0);
        Assert.Equal(0.0, RegionCropper.DifferenceRatio(sigA, sigA));

        var shadowOut = OcrPreprocessor.ShadowText(frameB);
        Assert.Equal(0, shadowOut.Pixels[p]); // Extracted white glyph turns black on white canvas
        Assert.Equal(255, shadowOut.Pixels[0]);
    }

    [Fact]
    public void SecureServer_ExcludesCgnatAndRejectsUnauthenticatedStreams()
    {
        Assert.True(SecureSubtitleServer.IsTrustedLanPeer(IPAddress.Loopback));
        Assert.True(SecureSubtitleServer.IsTrustedLanPeer(IPAddress.Parse("192.168.1.45")));
        Assert.True(SecureSubtitleServer.IsTrustedLanPeer(IPAddress.Parse("10.0.0.12")));
        Assert.False(SecureSubtitleServer.IsTrustedLanPeer(IPAddress.Parse("100.64.0.10"))); // CGNAT excluded
        Assert.False(SecureSubtitleServer.IsTrustedLanPeer(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public async Task SecureServer_EnforcesMandatoryPinPairingFlow()
    {
        using var server = new SecureSubtitleServer(port: 0, accessToken: "test-secret-token-123456");
        int port = await server.StartAsync();
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // 1. Unauthenticated /events MUST return 401 Unauthorized (no legacy bypass)
        var unauth = await http.GetAsync("/events");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        // 2. /hello never leaks token
        string helloJson = await http.GetStringAsync("/hello");
        Assert.Contains("essvb-sub", helloJson);
        Assert.DoesNotContain("test-secret-token", helloJson);

        // 3. POST /pair generates a 6-digit PIN ticket
        string? ticketId = null;
        server.PairingRequested += t => ticketId = t.Id;
        var pairResp = await http.PostAsync("/pair", null);
        Assert.Equal(HttpStatusCode.OK, pairResp.StatusCode);
        using var pairDoc = JsonDocument.Parse(await pairResp.Content.ReadAsStringAsync());
        string id = pairDoc.RootElement.GetProperty("id").GetString()!;
        string code = pairDoc.RootElement.GetProperty("code").GetString()!;
        Assert.Equal(6, code.Length);
        Assert.Equal(id, ticketId);

        // 4. Before approval, polling returns pending and null token
        using var pendingDoc = JsonDocument.Parse(await http.GetStringAsync($"/pair?id={id}"));
        Assert.Equal("pending", pendingDoc.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, pendingDoc.RootElement.GetProperty("token").ValueKind);

        // 5. After approval, polling returns token once (single-use consumption) and /events?k=token opens SSE
        Assert.True(server.ResolvePairing(id, approve: true));
        using var approvedDoc = JsonDocument.Parse(await http.GetStringAsync($"/pair?id={id}"));
        Assert.Equal("approved", approvedDoc.RootElement.GetProperty("state").GetString());
        string token = approvedDoc.RootElement.GetProperty("token").GetString()!;
        Assert.Equal("test-secret-token-123456", token);

        using var replayDoc = JsonDocument.Parse(await http.GetStringAsync($"/pair?id={id}"));
        Assert.Equal("expired", replayDoc.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, replayDoc.RootElement.GetProperty("token").ValueKind);

        using var sseResp = await http.GetAsync($"/events?k={Uri.EscapeDataString(token)}", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        Assert.Equal("text/event-stream", sseResp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public void SecureVault_And_TranslationCache_RoundTrip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"essvb-test-{Guid.NewGuid():N}");
        try
        {
            var vault = new SecureVault(Path.Combine(tempDir, "vault.dat"));
            vault.Save("apikey:Gemini", "secret-gemini-key-xyz");
            Assert.Equal("secret-gemini-key-xyz", vault.Load<string>("apikey:Gemini"));
            vault.Remove("apikey:Gemini");
            Assert.Null(vault.Load<string>("apikey:Gemini"));

            string cacheFile = Path.Combine(tempDir, "cache.jsonl");
            var cache1 = new TranslationCache(cacheFile, engineId: "grok:grok-3-mini");
            cache1.Store("Stay close.", "Yakında kal.");
            Assert.True(cache1.TryGet("Stay close.", out var hit1));
            Assert.Equal("Yakında kal.", hit1);

            var cache2 = new TranslationCache(cacheFile, engineId: "grok:grok-3-mini");
            Assert.True(cache2.TryGet("Stay close.", out var hit2));
            Assert.Equal("Yakında kal.", hit2);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void AiTranslator_BuildsContextPrompt_CleansThinkBlocks_And_ConfiguresGrok()
    {
        var prompt = UniversalAiTranslator.BuildUserPrompt(
            "ATREUS: Watch out!",
            [new TranslationContextLine("Where is he?", "O nerede?")],
            "God of War",
            ["ATREUS", "KRATOS"]);

        Assert.Contains("Title: God of War", prompt);
        Assert.Contains("ATREUS, KRATOS", prompt);
        Assert.Contains("EN: Where is he? | TR: O nerede?", prompt);

        string cleaned = UniversalAiTranslator.CleanModelOutput("<think>reasoning step</think>\n```text\n\"ATREUS: Dikkat et!\"\n```<|im_end|>");
        Assert.Equal("ATREUS: Dikkat et!", cleaned);

        var grok = new UniversalAiTranslator(AiProviderKind.Grok, apiKey: "xai-test", model: "grok-3-mini");
        using var req = grok.BuildHttpRequest("Hello", [], null, null);
        Assert.Equal("https://api.x.ai/v1/chat/completions", req.RequestUri!.AbsoluteUri);

        // Endpoint URI validation rejects non-HTTP(S) schemes and non-HTTPS remote cloud endpoints
        var badScheme = new UniversalAiTranslator(AiProviderKind.CustomOpenAi, endpoint: "file:///etc/passwd");
        Assert.Throws<InvalidOperationException>(() => badScheme.BuildHttpRequest("Hi", [], null, null));
        var plainRemote = new UniversalAiTranslator(AiProviderKind.OpenAi, apiKey: "sk-test", endpoint: "http://api.openai.com/v1");
        Assert.Throws<InvalidOperationException>(() => plainRemote.BuildHttpRequest("Hi", [], null, null));
    }

    [Fact]
    public void Gemini_CatalogDefaultsToPricePerformanceModel_And_DiscoveryFiltersMediaModels()
    {
        var gemini = AiProviderCatalog.Get(AiProviderKind.Gemini);
        Assert.Equal("gemini-3.5-flash-lite", gemini.DefaultModel);
        Assert.Contains(gemini.RecommendedPresets, p => p.Id == "gemini-3.5-flash-lite" && p.Label.Contains("F/P"));
        Assert.Contains(gemini.RecommendedPresets, p => p.Id == "gemini-3.8-flash");

        var grok = AiProviderCatalog.Get(AiProviderKind.Grok);
        Assert.Equal("grok-3-mini", grok.DefaultModel);

        const string geminiListJson = """
            {
              "models": [
                { "name": "models/gemini-3.5-flash-lite", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-2.5-flash", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.1-flash-image", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.5-transcribe", "supportedGenerationMethods": ["generateContent"] },
                { "name": "models/gemini-3.8-flash", "supportedGenerationMethods": ["generateContent"] }
              ]
            }
            """;

        var parsed = ModelDiscoveryService.ParseGeminiModelsJson(geminiListJson);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("gemini-3.5-flash-lite", parsed[0].Id);
        Assert.Equal("gemini-3.8-flash", parsed[1].Id);
    }

    [Fact]
    public void RustFastAccel_MatchesExpectedOcrPreprocessingAndSignatureOutputs()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "essvb_fast.dll" : "libessvb_fast.so")))
            Assert.True(RustFastAccel.IsAvailable);

        var frame = Frame.Blank(160, 80);
        for (int x = 20; x <= 60; x++)
        {
            int p = (30 * frame.Stride) + (x * 4);
            frame.Pixels[p] = 248; frame.Pixels[p + 1] = 248; frame.Pixels[p + 2] = 248; frame.Pixels[p + 3] = 255;
            int sp = (32 * frame.Stride) + ((x + 2) * 4);
            frame.Pixels[sp] = 12; frame.Pixels[sp + 1] = 12; frame.Pixels[sp + 2] = 12; frame.Pixels[sp + 3] = 255;
        }

        var shadow = OcrPreprocessor.ShadowText(frame);
        Assert.Equal(0, shadow.Pixels[(30 * frame.Stride) + (30 * 4)]);
        Assert.Equal(255, shadow.Pixels[0]);

        var white = OcrPreprocessor.WhiteTextOnly(frame);
        Assert.Equal(0, white.Pixels[(30 * frame.Stride) + (30 * 4)]);

        var up = OcrPreprocessor.Upscale2x(frame);
        Assert.Equal(320, up.Width);
        Assert.Equal(160, up.Height);

        var sig = RegionCropper.ComputeSignature(frame);
        Assert.Equal(RegionCropper.SignatureWidth * RegionCropper.SignatureHeight, sig.Length);
        Assert.Equal(0.0, RegionCropper.DifferenceRatio(sig, sig));
    }

    [Fact]
    public void TizenPackageAdapter_SupportsCu8500_Mu9000_AndSafeBase64Payload()
    {
        Assert.Equal("7.0", TizenPackageAdapter.ResolvePlatformVersion(null, SamsungTvModel.Cu8500_65));
        Assert.Equal("7.0", TizenPackageAdapter.ResolvePlatformVersion(null, SamsungTvModel.AutoDetect, "UE65CU8500UXTK"));
        Assert.Equal("3.0", TizenPackageAdapter.ResolvePlatformVersion(null, SamsungTvModel.AutoDetect, "UE55MU9000"));
        Assert.Equal(TizenSignatureDigest.Sha512, TizenPackageAdapter.GetRecommendedDigest("7.0"));
        Assert.Equal(TizenSignatureDigest.Sha512, TizenPackageAdapter.GetRecommendedDigest("3.0"));
        Assert.Equal(TizenSignatureDigest.Sha256, TizenPackageAdapter.GetRecommendedDigest("2.4"));
        Assert.False(TizenPackageAdapter.RequiresDeviceProfile("7.0"));
        Assert.False(TizenPackageAdapter.RequiresLegacyAdaptation("7.0"));

        string cmd = SamsungTvInstaller.BuildSafeLaunchCommand("http://192.168.1.10:8787", "tok-123", 2);
        Assert.StartsWith("0 was_execute EssvbSubTv.EssvbSub PAYLOAD b64:", cmd);
        string b64 = cmd["0 was_execute EssvbSubTv.EssvbSub PAYLOAD b64:".Length..];
        using var payloadDoc = JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        Assert.Equal("http://192.168.1.10:8787", payloadDoc.RootElement.GetProperty("server").GetString());
        Assert.Equal("tok-123", payloadDoc.RootElement.GetProperty("token").GetString());
        Assert.Equal(2, payloadDoc.RootElement.GetProperty("hdmi").GetInt32());
    }

    [Fact]
    public void TizenPackageAdapter_SupportsMu9000AndLegacy24_WithXmlDsig()
    {
        Assert.True(TizenPackageAdapter.RequiresDeviceProfile("3.0"));   // MU9000 Tizen 3.0
        Assert.True(TizenPackageAdapter.RequiresDeviceProfile("2.4"));
        Assert.False(TizenPackageAdapter.RequiresDeviceProfile("5.0"));
        Assert.False(TizenPackageAdapter.RequiresLegacyAdaptation("3.0"));
        Assert.True(TizenPackageAdapter.RequiresLegacyAdaptation("2.4"));

        const string sampleXml = """
            <widget>
              <tizen:metadata key="http://tizen.org/metadata/app_ui_type/base_screen_resolution" value="extensive"/>
              <tizen:privilege name="http://developer.samsung.com/privilege/network.public"/>
              <tizen:privilege name="http://tizen.org/privilege/tv.window"/>
            </widget>
            """;
        string adapted = TizenPackageAdapter.AdaptConfigXml(sampleXml);
        Assert.DoesNotContain("network.public", adapted);
        Assert.DoesNotContain("base_screen_resolution", adapted);
        Assert.Contains("tv.window", adapted);

        // Verify W3C Widget XML-DSig generation inside .wgt archive
        using var rsa = RSA.Create(2048);
        var certReq = new CertificateRequest("CN=ESSVB-Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        byte[] unsignedWgt;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var s = zip.CreateEntry("config.xml").Open();
                s.Write(System.Text.Encoding.UTF8.GetBytes("<widget/>"));
            }
            unsignedWgt = ms.ToArray();
        }

        byte[] signedWgt = TizenPackageAdapter.SignWgtBytes(
            unsignedWgt, rsa, [cert.RawData], rsa, [cert.RawData], TizenSignatureDigest.Sha512);

        using var checkZip = new ZipArchive(new MemoryStream(signedWgt), ZipArchiveMode.Read);
        Assert.NotNull(checkZip.GetEntry("author-signature.xml"));
        Assert.NotNull(checkZip.GetEntry("signature1.xml"));
        Assert.NotNull(checkZip.GetEntry("config.xml"));
    }

    [Fact]
    public async Task Ollama_UsesNativeApiChatWithThinkDisabled_And_ParsesMessageContent()
    {
        var ollama = new UniversalAiTranslator(AiProviderKind.Ollama, endpoint: "http://127.0.0.1:11434/v1", model: "gemma4-12b-64k:latest");
        using var req = ollama.BuildHttpRequest("Stay close.", [], null, null);
        Assert.Equal("http://127.0.0.1:11434/api/chat", req.RequestUri!.AbsoluteUri);

        string payload = await req.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(payload);
        Assert.Equal("gemma4-12b-64k:latest", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("think").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());

        const string nativeResp = """{"model":"gemma4-12b-64k:latest","message":{"role":"assistant","content":"Yakın dur."},"done":true}""";
        string extracted = UniversalAiTranslator.ExtractResponseText(AiProviderKind.Ollama, nativeResp);
        Assert.Equal("Yakın dur.", UniversalAiTranslator.CleanModelOutput(extracted));
    }

    [Fact]
    public void ModelDiscovery_FiltersEmbeddingModels_And_FormatsOllamaLabels()
    {
        const string tagsJson = """
            {
              "models": [
                {
                  "name": "gemma4-12b-64k:latest",
                  "details": { "parameter_size": "11.9B", "quantization_level": "Q4_K_M" },
                  "capabilities": ["completion", "vision", "thinking"]
                },
                {
                  "name": "bge-m3:latest",
                  "details": { "parameter_size": "566.70M", "quantization_level": "F16" },
                  "capabilities": ["embedding"]
                },
                {
                  "name": "qwen3:14b",
                  "details": { "parameter_size": "14.8B", "quantization_level": "Q4_K_M" },
                  "capabilities": ["completion", "thinking"]
                }
              ]
            }
            """;

        var models = ModelDiscoveryService.ParseOllamaTagsJson(tagsJson);
        Assert.Equal(2, models.Count);
        Assert.Equal("gemma4-12b-64k:latest", models[0].Id);
        Assert.Contains("11.9B", models[0].DisplayLabel);
        Assert.Contains("Q4_K_M", models[0].DisplayLabel);
        Assert.Equal("qwen3:14b", models[1].Id);
    }

    [Fact]
    public void WindowsOcr_CanInitializeWhenRunningOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var reader = WinRtOcrReader.TryCreate("en");
        Assert.NotNull(reader);
        var blank = Frame.Blank(100, 60);
        var reading = reader.ReadLines(blank, deep: false);
        Assert.Empty(reading.Lines);

        // Render a crisp small-height white-on-black YouTube-style subtitle strip (height=34px)
        var ytStrip = RenderBitmapTextFrame("HELLO TITLE", scale: 3, width: 340, height: 34);
        var ytReading = reader.ReadLines(ytStrip, deep: false);
        Assert.Contains("HELLO", ytReading.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TITLE", ytReading.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(ytReading.Box.HasValue);
        Assert.NotNull(ytReading.Lines[0].Words);
        Assert.True(ytReading.Lines[0].Words!.Count >= 2);
    }

    [Fact]
    public void AppInfo_ParsesSemanticVersionAndShortCommitCorrectly()
    {
        Assert.Equal("1.4.0", AppInfo.ParseSemanticVersion("1.4.0+8b56851f2938178939"));
        Assert.Equal("8b56851", AppInfo.ParseShortCommit("1.4.0+8b56851f2938178939"));
        Assert.Equal("v1.4.0 (8b56851)", AppInfo.FormatVersionLabel("1.4.0+8b56851f2938178939"));

        Assert.Equal("1.4.0", AppInfo.ParseSemanticVersion("1.4.0"));
        Assert.Equal(string.Empty, AppInfo.ParseShortCommit("1.4.0"));
        Assert.Equal("v1.4.0", AppInfo.FormatVersionLabel("1.4.0"));

        Assert.Equal("1.6.0", AppInfo.ParseSemanticVersion(null));
        Assert.Equal("v1.6.0", AppInfo.FormatVersionLabel(null));

        Assert.Equal("1.6.0", AppInfo.Version);
        Assert.Equal("v1.6.0", AppInfo.VersionBadge);
    }

    [Fact]
    public void TranslationCache_AutoScrubsPoisonedUntranslatedEntriesOnLoadAndRejectsStore()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"essvb-scrub-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string cachePath = Path.Combine(tempDir, "test-cache.jsonl");
            File.WriteAllLines(cachePath,
            [
                JsonSerializer.Serialize(new[] { "Stay close, boy.", "Yakınımda kal, oğlum." }),
                JsonSerializer.Serialize(new[] { "Okay, so we're comin' up on our spot, boys!", "Okay, so we're comin' up on our spot, boys!" }),
                JsonSerializer.Serialize(new[] { "Kenton: Let's go right now.", "Kenton: Let's go right now." })
            ]);

            var cache = new TranslationCache(cachePath, engineId: "gemini:gemini-3.5-flash-lite");
            int scrubbed = cache.ScrubPoisonedEntries();
            Assert.Equal(2, scrubbed);
            Assert.Equal(1, cache.Count);
            Assert.True(cache.TryGet("Stay close, boy.", out var validTr));
            Assert.Equal("Yakınımda kal, oğlum.", validTr);
            Assert.False(cache.TryGet("Okay, so we're comin' up on our spot, boys!", out _));

            // Attempting to store an untranslated echo must be rejected
            cache.Store("I just put a hole in the boat.", "I just put a hole in the boat.");
            Assert.False(cache.TryGet("I just put a hole in the boat.", out _));
            Assert.Equal(1, cache.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SubtitleRegionFilter_KeepsBottomCenterSubtitleAndDropsLeftRightTopHudAndSigns()
    {
        var filter = new SubtitleRegionFilter();
        var reading = new Reading(
        [
            new TextLine("HP 100%", new TextBox(0.02, 0.05, 0.14, 0.10)),
            new TextLine("MAP NORTH", new TextBox(0.84, 0.04, 0.97, 0.10)),
            new TextLine("CAUTION MIND YOUR STEP", new TextBox(0.32, 0.12, 0.68, 0.17)),
            new TextLine("PRESS [E] TO OPEN", new TextBox(0.40, 0.48, 0.60, 0.53)),
            new TextLine("Kenton: We should be down there soon.", new TextBox(0.24, 0.78, 0.76, 0.86))
        ]);

        var kept = filter.Filter(reading, strictBottomCenter: true, nowMs: 1000, sourceLanguage: "en");
        Assert.Single(kept.Lines);
        Assert.Equal("Kenton: We should be down there soon.", kept.Text);
    }

    [Fact]
    public void TextMatch_DeduplicatesNoisyOcrRereads_PreservesAddsMeaning_AndDetectsUntranslatedEcho()
    {
        // High word overlap catches noisy OCR jitter on the same line
        string n1 = TextMatch.Normalize("Okay, so we're comin' up on our spot, boys!");
        string n2 = TextMatch.Normalize("Okay, so we're cornin' up on our spöft, boys!");
        Assert.True(TextMatch.IsSameSubtitle(n1, n2));

        // Negation flip MUST NOT be treated as duplicate
        string pos = TextMatch.Normalize("I can help you now.");
        string neg = TextMatch.Normalize("I can't help you now.");
        Assert.True(TextMatch.AddsMeaning(pos, neg));
        Assert.False(TextMatch.IsSameSubtitle(pos, neg));

        // Untranslated echo vs real Turkish translation
        Assert.True(TextMatch.IsUntranslatedEcho("Wait a second, we need to go now.", "Wait a second, we need to go now."));
        Assert.False(TextMatch.IsUntranslatedEcho("Wait a second, we need to go now.", "Bir saniye bekle, şimdi gitmemiz gerek."));
    }

    [Fact]
    public async Task UniversalAiTranslator_IncludesGeminiSafetySettings_ParsesRetryDelayAndQuota_AndMultiLangPrompt()
    {
        var tr = new UniversalAiTranslator(
            AiProviderKind.Gemini,
            apiKey: "test-key",
            model: "gemini-3.5-flash-lite",
            sourceLanguage: "ja",
            genrePresetId: "rpg_fantasy");

        string sys = tr.BuildSystemPrompt();
        Assert.Contains("Japanese", sys);
        Assert.Contains("RPG", sys);

        using var req = tr.BuildHttpRequest("Let's upgrade our Skill Tree before facing Kratos.", [], "God of War", null);
        string body = await req.Content!.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var safety = doc.RootElement.GetProperty("safetySettings");
        Assert.Equal(5, safety.GetArrayLength());
        Assert.Equal("BLOCK_NONE", safety[0].GetProperty("threshold").GetString());

        string userText = doc.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains("Skill Tree = Yetenek Ağacı", userText);

        const string errJson = """
            {
              "error": {
                "code": 429,
                "message": "Quota exceeded.",
                "details": [
                  { "@type": "type.googleapis.com/google.rpc.QuotaFailure", "violations": [{ "quotaId": "GenerateRequestsPerMinutePerProjectPerModel", "quotaValue": "15" }] },
                  { "@type": "type.googleapis.com/google.rpc.RetryInfo", "retryDelay": "23.4s" }
                ]
              }
            }
            """;
        var rl = UniversalAiTranslator.ParseRateLimitDetails(null, errJson);
        Assert.Equal(24, rl.DelaySeconds);
        Assert.False(rl.IsDailyQuota);
        Assert.Equal(15, rl.ReportedRpmQuota);
    }

    [Fact]
    public void GameGenreCatalog_MatchesTermsForLineAndSupportsMultiLanguage()
    {
        var preset = GameGenreCatalog.Get("action_street");
        var matched = GameGenreCatalog.MatchTermsForLine(preset, "The Ripperdoc told V to lay low until the Wanted Level drops.");
        Assert.Contains(matched.Terms, t => t.Source == "Ripperdoc" && t.Target == "Siber Doktor");
        Assert.Contains(matched.Terms, t => t.Source == "Wanted Level" && t.Target == "Aranma Seviyesi");
        Assert.Contains("V", matched.KeepNames);

        Assert.True(SourceLanguageCatalog.All.Count >= 15);
        Assert.Equal("JA", SourceLanguageCatalog.Get("ja").DeepLCode);
        Assert.Equal("kor", SourceLanguageCatalog.Get("ko").TesseractCode);
    }

    [Fact]
    public async Task OllamaProcessManager_DetectsLoopback_FindsExecutable_AndStartsViaCoreSeam()
    {
        Assert.True(OllamaProcessManager.IsLoopbackOllamaEndpoint(null));
        Assert.True(OllamaProcessManager.IsLoopbackOllamaEndpoint("http://127.0.0.1:11434/v1"));
        Assert.True(OllamaProcessManager.IsLoopbackOllamaEndpoint("http://localhost:11434/v1"));
        Assert.False(OllamaProcessManager.IsLoopbackOllamaEndpoint("http://192.168.1.50:11434/v1"));

        string fakeBinDir = Path.Combine(Path.GetTempPath(), "essvb-ollama-bin");
        string fakeExe = Path.Combine(fakeBinDir, "ollama.exe");
        Assert.Equal(fakeExe, OllamaProcessManager.FindOllamaExecutable(
            p => string.Equals(p, fakeExe, StringComparison.OrdinalIgnoreCase),
            pathEnv: $"C:\\NonExistent;{fakeBinDir}",
            isWindowsOverride: true));
        Assert.Equal("/usr/bin/ollama", OllamaProcessManager.FindOllamaExecutable(
            p => string.Equals(p, "/usr/bin/ollama", StringComparison.Ordinal),
            pathEnv: "/usr/local/sbin:/usr/bin",
            isWindowsOverride: false));

        // Remote endpoint -> NotLocalEndpoint without spawning
        bool spawnedRemote = false;
        var remoteRes = await OllamaProcessManager.EnsureRunningCoreAsync(
            "http://192.168.1.50:11434/v1",
            onProgress: null,
            probeAsync: _ => Task.FromResult(false),
            findExecutable: () => "ollama",
            startServer: (_, _) => { spawnedRemote = true; return new SpawnedOllamaProcess(1, () => false, () => null); },
            readyTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(10));
        Assert.Equal(OllamaEnsureStatus.NotLocalEndpoint, remoteRes.Status);
        Assert.False(spawnedRemote);

        // Already running -> AlreadyRunning without spawning
        bool spawnedAlready = false;
        var alreadyRes = await OllamaProcessManager.EnsureRunningCoreAsync(
            "http://127.0.0.1:11434/v1",
            onProgress: null,
            probeAsync: _ => Task.FromResult(true),
            findExecutable: () => "ollama",
            startServer: (_, _) => { spawnedAlready = true; return new SpawnedOllamaProcess(1, () => false, () => null); },
            readyTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(10));
        Assert.Equal(OllamaEnsureStatus.AlreadyRunning, alreadyRes.Status);
        Assert.True(alreadyRes.IsReady);
        Assert.False(spawnedAlready);

        // Missing executable -> ExecutableNotFound
        var missingRes = await OllamaProcessManager.EnsureRunningCoreAsync(
            "http://127.0.0.1:11434/v1",
            onProgress: null,
            probeAsync: _ => Task.FromResult(false),
            findExecutable: () => null,
            startServer: (_, _) => new SpawnedOllamaProcess(1, () => false, () => null),
            readyTimeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(10));
        Assert.Equal(OllamaEnsureStatus.ExecutableNotFound, missingRes.Status);
        Assert.False(missingRes.IsReady);

        // Probe fails twice then succeeds after background start
        int probeCount = 0;
        string? progressSeen = null;
        var startedRes = await OllamaProcessManager.EnsureRunningCoreAsync(
            "http://127.0.0.1:11434/v1",
            onProgress: m => progressSeen = m,
            probeAsync: _ => Task.FromResult(++probeCount >= 3),
            findExecutable: () => fakeExe,
            startServer: (_, _) => new SpawnedOllamaProcess(4242, () => false, () => null),
            readyTimeout: TimeSpan.FromSeconds(3),
            pollInterval: TimeSpan.FromMilliseconds(15));
        Assert.Equal(OllamaEnsureStatus.StartedSuccessfully, startedRes.Status);
        Assert.True(startedRes.IsReady);
        Assert.Equal(4242, startedRes.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(progressSeen));

        // Early non-zero exit -> StartFailed
        var failedRes = await OllamaProcessManager.EnsureRunningCoreAsync(
            "http://127.0.0.1:11434/v1",
            onProgress: null,
            probeAsync: _ => Task.FromResult(false),
            findExecutable: () => fakeExe,
            startServer: (_, _) => new SpawnedOllamaProcess(5151, () => true, () => 1),
            readyTimeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(10));
        Assert.Equal(OllamaEnsureStatus.StartFailed, failedRes.Status);
        Assert.False(failedRes.IsReady);

        // Timed out -> TimedOut
        var timedOutRes = await OllamaProcessManager.EnsureRunningCoreAsync(
            "http://127.0.0.1:11434/v1",
            onProgress: null,
            probeAsync: _ => Task.FromResult(false),
            findExecutable: () => fakeExe,
            startServer: (_, _) => new SpawnedOllamaProcess(6161, () => false, () => null),
            readyTimeout: TimeSpan.FromMilliseconds(60),
            pollInterval: TimeSpan.FromMilliseconds(15));
        Assert.Equal(OllamaEnsureStatus.TimedOut, timedOutRes.Status);

        string formattedErr = UniversalAiTranslator.FormatConnectionError(
            AiProviderKind.Ollama,
            new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));
        Assert.Contains("Ollama sunucusu kapalı", formattedErr);
    }

    [Fact]
    public async Task GitHubUpdateService_ComparesSemVer_ValidatesUrlsAndSha256_AndParsesRelease()
    {
        Assert.True(GitHubUpdateService.IsNewerVersion("1.4.0", "v1.5.0"));
        Assert.False(GitHubUpdateService.IsNewerVersion("1.5.0", "1.5.0"));
        Assert.False(GitHubUpdateService.IsNewerVersion("1.5.0", "1.4.9"));
        Assert.True(GitHubUpdateService.IsNewerVersion("1.5.0-beta.1", "1.5.0"));
        Assert.True(GitHubUpdateService.IsNewerVersion("1.9.0", "1.10.0"));

        Assert.True(GitHubUpdateService.IsTrustedGitHubUrl("https://github.com/ibrahimsaglammm/ESSVB-sub/releases/tag/v1.5.0"));
        Assert.True(GitHubUpdateService.IsTrustedGitHubUrl("https://objects.githubusercontent.com/github-production-release-asset-1/test.exe"));
        Assert.False(GitHubUpdateService.IsTrustedGitHubUrl("http://github.com/ibrahimsaglammm/ESSVB-sub"));
        Assert.False(GitHubUpdateService.IsTrustedGitHubUrl("https://evil-github.com/test.exe"));
        Assert.False(GitHubUpdateService.IsTrustedGitHubUrl("https://example.com/test.exe"));

        Assert.Null(GitHubUpdateService.SanitizeAssetFileName("../evil.exe"));
        Assert.Null(GitHubUpdateService.SanitizeAssetFileName("dir/evil.exe"));
        Assert.Equal("ESSVB-sub-v1.6.0-win11-x64.exe", GitHubUpdateService.SanitizeAssetFileName("ESSVB-sub-v1.6.0-win11-x64.exe"));

        byte[] goodPayload = "valid-essvb-release-binary"u8.ToArray();
        string goodHex = Convert.ToHexString(SHA256.HashData(goodPayload)).ToLowerInvariant();
        string validShaDoc = $"{goodHex}  ESSVB-sub-v1.6.0-win11-x64.exe\n";
        Assert.NotNull(GitHubUpdateService.ParseExpectedSha256(validShaDoc, "ESSVB-sub-v1.6.0-win11-x64.exe"));
        Assert.Null(GitHubUpdateService.ParseExpectedSha256(validShaDoc, "wrong-name.exe"));
        Assert.Null(GitHubUpdateService.ParseExpectedSha256("not-a-hex  ESSVB-sub-v1.6.0-win11-x64.exe", "ESSVB-sub-v1.6.0-win11-x64.exe"));

        const string releaseJson = """
            {
              "tag_name": "v1.6.0",
              "name": "ESSVB-sub v1.6.0",
              "html_url": "https://github.com/ibrahimsaglammm/ESSVB-sub/releases/tag/v1.6.0",
              "draft": false,
              "prerelease": false,
              "published_at": "2026-11-24T12:00:00Z",
              "body": "Otomatik Ollama baslatma ve guncelleyici",
              "assets": [
                {
                  "name": "ESSVB-sub-v1.6.0-win11-x64.exe",
                  "size": 26,
                  "browser_download_url": "https://github.com/ibrahimsaglammm/ESSVB-sub/releases/download/v1.6.0/ESSVB-sub-v1.6.0-win11-x64.exe"
                },
                {
                  "name": "ESSVB-sub-v1.6.0-win11-x64.exe.sha256",
                  "size": 97,
                  "browser_download_url": "https://github.com/ibrahimsaglammm/ESSVB-sub/releases/download/v1.6.0/ESSVB-sub-v1.6.0-win11-x64.exe.sha256"
                }
              ]
            }
            """;

        bool serveTampered = true;
        using var handler = new StubHttpMessageHandler(req =>
        {
            string url = req.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(releaseJson, Encoding.UTF8, "application/json")
                };
            }
            if (url.EndsWith(".sha256", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(validShaDoc, Encoding.UTF8, "text/plain")
                };
            }
            if (url.EndsWith(".exe", StringComparison.Ordinal))
            {
                byte[] bytes = serveTampered ? "tampered-payload"u8.ToArray() : goodPayload;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(bytes)
                };
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var mockHttp = new HttpClient(handler);

        var checkNew = await GitHubUpdateService.CheckForUpdateAsync("1.5.0", mockHttp);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, checkNew.Status);
        Assert.NotNull(checkNew.Update);
        Assert.True(checkNew.Update!.CanInAppUpdateWindows);
        Assert.Equal("1.6.0", checkNew.Update.LatestVersion);
        Assert.Equal("https://github.com/ibrahimsaglammm/ESSVB-sub/releases/download/v1.6.0/ESSVB-sub-v1.6.0-win11-x64.exe", checkNew.Update.WindowsExeAsset!.DownloadUrl);

        var checkSame = await GitHubUpdateService.CheckForUpdateAsync("1.6.0", mockHttp);
        Assert.Equal(UpdateCheckStatus.UpToDate, checkSame.Status);
        Assert.Null(checkSame.Update);

        string tempDir = Path.Combine(Path.GetTempPath(), "essvb-upd-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            string stagedDir = Path.Combine(tempDir, "updates");
            var tamperedRes = await GitHubUpdateService.DownloadAndInstallCoreAsync(
                checkNew.Update,
                progress: null,
                httpClient: mockHttp,
                updatesDirectoryOverride: stagedDir,
                packagedWindowsOverride: true,
                executablePathOverride: Path.Combine(tempDir, "ESSVB-sub-v1.5.0-win11-x64.exe"),
                launchUpdaterOverride: (_, _, _, _) => { },
                CancellationToken.None);
            Assert.False(tamperedRes.Success);
            Assert.Contains("SHA-256", tamperedRes.Message);
            Assert.False(File.Exists(Path.Combine(stagedDir, "ESSVB-sub-v1.6.0-win11-x64.exe.part")));

            serveTampered = false;
            string? capturedScript = null;
            string? capturedDest = null;
            var validRes = await GitHubUpdateService.DownloadAndInstallCoreAsync(
                checkNew.Update,
                progress: null,
                httpClient: mockHttp,
                updatesDirectoryOverride: stagedDir,
                packagedWindowsOverride: true,
                executablePathOverride: Path.Combine(tempDir, "ESSVB-sub-v1.5.0-win11-x64.exe"),
                launchUpdaterOverride: (script, _, dest, _) => { capturedScript = script; capturedDest = dest; },
                CancellationToken.None);
            Assert.True(validRes.Success);
            Assert.True(validRes.RequiresAppExit);
            Assert.True(File.Exists(capturedScript));
            Assert.Equal(Path.Combine(tempDir, "ESSVB-sub-v1.6.0-win11-x64.exe"), capturedDest);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RustFastAccel_V2Kernels_MatchCSharpReferenceOutputs()
    {
        if (!RustFastAccel.IsAvailable || RustFastAccel.EngineVersion < 2)
            return;

        var frame = Frame.Blank(8, 6);
        // Set (3, 2) to B=10, G=100, R=200, A=255 -> luma = (600 + 600 + 10) / 10 = 121
        int idx = (2 * frame.Stride) + (3 * 4);
        frame.Pixels[idx] = 10;
        frame.Pixels[idx + 1] = 100;
        frame.Pixels[idx + 2] = 200;
        frame.Pixels[idx + 3] = 255;

        Assert.True(RustFastAccel.TryCropBgra(frame, 3, 2, 2, 2, out var cropped));
        Assert.Equal(2, cropped.Width);
        Assert.Equal(2, cropped.Height);
        Assert.Equal(10, cropped.Pixels[0]);
        Assert.Equal(200, cropped.Pixels[2]);

        Assert.True(RustFastAccel.TryToGrayscale(frame, invert: false, out var gray));
        Assert.Equal(121, gray[(2 * 8) + 3]);

        Assert.True(RustFastAccel.TryToGrayscale(frame, invert: true, out var grayInv));
        Assert.Equal(134, grayInv[(2 * 8) + 3]);

        Assert.True(RustFastAccel.TryAddPadding(frame, pad: 2, out var padded));
        Assert.Equal(12, padded.Width);
        Assert.Equal(10, padded.Height);
        Assert.Equal(255, padded.Pixels[3]);
        int padIdx = (4 * padded.Stride) + (5 * 4);
        Assert.Equal(200, padded.Pixels[padIdx + 2]);

        Assert.True(RustFastAccel.TryLevenshtein("çeviri", "çevir", out int dist1));
        Assert.Equal(1, dist1);
        Assert.True(RustFastAccel.TryLevenshtein("kitten", "sitting", out int dist2));
        Assert.Equal(3, dist2);
    }

    [Fact]
    public void SamsungInstallDiagnostics_ClassifiesErrors_AndParsesVdAppList()
    {
        Assert.Equal(SamsungInstallProblem.None, SamsungInstallDiagnostics.Classify("end_process(ok)"));
        Assert.Equal(SamsungInstallProblem.CertificateMismatch, SamsungInstallDiagnostics.Classify("install failed[118, -12], reason: same id and different certificate"));
        Assert.Equal(SamsungInstallProblem.TvClockWrong, SamsungInstallDiagnostics.Classify("install failed[118, -12], reason: Check certificate error : not valid yet"));
        Assert.Equal(SamsungInstallProblem.CertificateRejected, SamsungInstallDiagnostics.Classify("install failed[118, -12], reason: Check certificate error : expired"));
        Assert.Equal(SamsungInstallProblem.NotEnoughSpace, SamsungInstallDiagnostics.Classify("download failed[116]"));
        Assert.Equal(SamsungInstallProblem.PackageBlocked, SamsungInstallDiagnostics.Classify("install failed[118, -22]"));
        Assert.Equal(SamsungInstallProblem.Incompatible, SamsungInstallDiagnostics.Classify("install failed[118, -4]"));
        Assert.Equal(SamsungInstallProblem.Other, SamsungInstallDiagnostics.Classify("val[fail] unknown reason"));

        const string sampleAppList = """
            get app list...
            apps count: 2
            ---------------------------------------------------------------------------------------------
            --------------app_id                                =11101200001-------------
            --------------app_title                             =Netflix-------------
            --------------app_version                           =1.0.0-------------
            ---------------------------------------------------------------------------------------------
            --------------app_tizen_id                          =EssvbSubTv.EssvbSub-------------
            --------------app_id                                =EssvbSubTv-------------
            --------------app_title                             =ESSVB-sub-------------
            --------------app_version                           =1.6.0-------------
            ---------------------------------------------------------------------------------------------
            """;

        var found = SamsungInstallDiagnostics.ParseInstalledApp(sampleAppList, SamsungTvInstaller.AppId, SamsungTvInstaller.PackageId);
        Assert.True(found.Installed);
        Assert.Equal("1.6.0", found.Version);
        Assert.Equal("ESSVB-sub", found.Title);

        var absent = SamsungInstallDiagnostics.ParseInstalledApp("apps count: 1\n--------------app_id = 11101200001-------------", SamsungTvInstaller.AppId, SamsungTvInstaller.PackageId);
        Assert.False(absent.Installed);
        Assert.Null(absent.Version);

        // Verify RepairPkcs12ChainIfNeeded deduplicates 3-cert PKCS#12 down to 2 certs while preserving the private key
        using var rootKey = RSA.Create(2048);
        using var caKey1 = RSA.Create(2048);
        using var caKey2 = RSA.Create(2048);
        using var leafKey = RSA.Create(2048);

        var rootReq = new CertificateRequest("CN=Test Samsung Root CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var rootCa = rootReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-10), DateTimeOffset.UtcNow.AddYears(10));

        var caReq1 = new CertificateRequest("CN=Test Samsung CA", caKey1, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq1.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var oldCaOnly = caReq1.Create(rootCa, DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(1), [10, 1]);

        var caReq2 = new CertificateRequest("CN=Test Samsung CA", caKey2, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq2.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var newCaOnly = caReq2.Create(rootCa, DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow.AddYears(5), [10, 2]);
        using var newCaWithKey = newCaOnly.CopyWithPrivateKey(caKey2);

        var leafReq = new CertificateRequest("CN=TizenSDK", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var leafOnly = leafReq.Create(newCaWithKey, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365), [1, 2, 3, 4]);
        using var leafWithKey = leafOnly.CopyWithPrivateKey(leafKey);

        string p12Dir = Path.Combine(Path.GetTempPath(), $"essvb-p12-{Guid.NewGuid():N}");
        Directory.CreateDirectory(p12Dir);
        try
        {
            string p12Path = Path.Combine(p12Dir, "distributor.p12");
            const string pw = "test-pass-123";
            var bloated = new X509Certificate2Collection { leafWithKey, oldCaOnly, newCaOnly };
            var pbe = new PbeParameters(PbeEncryptionAlgorithm.TripleDes3KeyPkcs12, HashAlgorithmName.SHA1, 2048);
            File.WriteAllBytes(p12Path, bloated.ExportPkcs12(pbe, pw));

            Assert.True(SamsungTvInstaller.RepairPkcs12ChainIfNeeded(p12Path, pw));
            var after = X509CertificateLoader.LoadPkcs12CollectionFromFile(p12Path, pw, X509KeyStorageFlags.EphemeralKeySet);
            Assert.Equal(2, after.Count);
            Assert.Contains(after, c => c.HasPrivateKey);
            Assert.Contains(after, c => !c.HasPrivateKey && c.Thumbprint == newCaOnly.Thumbprint);
            Assert.False(SamsungTvInstaller.RepairPkcs12ChainIfNeeded(p12Path, pw));
        }
        finally
        {
            try { if (Directory.Exists(p12Dir)) Directory.Delete(p12Dir, recursive: true); } catch { }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    private static Frame RenderBitmapTextFrame(string text, int scale, int width, int height)
    {
        var frame = Frame.Blank(width, height);
        for (int y = 0; y < height; y++)
        {
            int row = y * frame.Stride;
            for (int x = 0; x < width; x++)
            {
                int i = row + (x * 4);
                frame.Pixels[i] = 16;
                frame.Pixels[i + 1] = 16;
                frame.Pixels[i + 2] = 16;
                frame.Pixels[i + 3] = 255;
            }
        }

        // Minimal 5x7 pixel font for H, E, L, O, T, I, space
        static string[] Glyph(char ch) => ch switch
        {
            'H' => ["10001", "10001", "10001", "11111", "10001", "10001", "10001"],
            'E' => ["11111", "10000", "10000", "11110", "10000", "10000", "11111"],
            'L' => ["10000", "10000", "10000", "10000", "10000", "10000", "11111"],
            'O' => ["01110", "10001", "10001", "10001", "10001", "10001", "01110"],
            'T' => ["11111", "00100", "00100", "00100", "00100", "00100", "00100"],
            'I' => ["01110", "00100", "00100", "00100", "00100", "00100", "01110"],
            _   => ["00000", "00000", "00000", "00000", "00000", "00000", "00000"]
        };

        int cursorX = 16;
        int startY = Math.Max(2, (height - (7 * scale)) / 2);
        foreach (char ch in text)
        {
            var rows = Glyph(ch);
            for (int r = 0; r < 7; r++)
            {
                for (int c = 0; c < 5; c++)
                {
                    if (rows[r][c] != '1') continue;
                    for (int dy = 0; dy < scale; dy++)
                    {
                        for (int dx = 0; dx < scale; dx++)
                        {
                            int px = cursorX + (c * scale) + dx;
                            int py = startY + (r * scale) + dy;
                            if (px >= 0 && px < width && py >= 0 && py < height)
                            {
                                int idx = (py * frame.Stride) + (px * 4);
                                frame.Pixels[idx] = 250;
                                frame.Pixels[idx + 1] = 250;
                                frame.Pixels[idx + 2] = 250;
                                frame.Pixels[idx + 3] = 255;
                            }
                        }
                    }
                }
            }
            cursorX += 6 * scale;
        }

        return frame;
    }
}
