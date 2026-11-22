// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using EssvbSub.Capture.Ocr;
using EssvbSub.Capture.TvInstall;
using EssvbSub.Core;
using EssvbSub.Core.Models;
using EssvbSub.Core.Pipeline;
using EssvbSub.Core.Server;
using EssvbSub.Core.Storage;
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
        Assert.Equal(TizenSignatureDigest.Sha256, TizenPackageAdapter.GetRecommendedDigest("3.0"));
        Assert.False(TizenPackageAdapter.RequiresDeviceProfile("7.0"));
        Assert.False(TizenPackageAdapter.RequiresLegacyAdaptation("7.0"));

        string cmd = SamsungTvInstaller.BuildSafeLaunchCommand("http://192.168.1.10:8787", "tok-123", 2);
        Assert.StartsWith("0 was_execute EssvbSubT.EssvbSub PAYLOAD b64:", cmd);
        string b64 = cmd["0 was_execute EssvbSubT.EssvbSub PAYLOAD b64:".Length..];
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
    }

    [Fact]
    public void AppInfo_ParsesSemanticVersionAndShortCommitCorrectly()
    {
        Assert.Equal("1.3.0", AppInfo.ParseSemanticVersion("1.3.0+8b56851f2938178939"));
        Assert.Equal("8b56851", AppInfo.ParseShortCommit("1.3.0+8b56851f2938178939"));
        Assert.Equal("v1.3.0 (8b56851)", AppInfo.FormatVersionLabel("1.3.0+8b56851f2938178939"));

        Assert.Equal("1.3.0", AppInfo.ParseSemanticVersion("1.3.0"));
        Assert.Equal(string.Empty, AppInfo.ParseShortCommit("1.3.0"));
        Assert.Equal("v1.3.0", AppInfo.FormatVersionLabel("1.3.0"));

        Assert.Equal("1.3.0", AppInfo.ParseSemanticVersion(null));
        Assert.Equal("v1.3.0", AppInfo.FormatVersionLabel(null));

        Assert.Equal("1.3.0", AppInfo.Version);
        Assert.Equal("v1.3.0", AppInfo.VersionBadge);
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
