// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EssvbSub.Capture.PlayStation;

/// <summary>
/// Exchanges a Sony OAuth authorization code for the numeric 8-byte PSN Account ID
/// needed by PS5 / PS4 Pro Remote Play device registration. No session tokens are stored.
/// </summary>
public static class PsnAuthHelper
{
    private const string ClientId = "ba495a24-818c-472b-b12d-ff231c1b5745";
    private const string ClientSecret = "mvaiZkRsAsI1IBkY";
    private const string TokenEndpoint = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token";
    private const string Scope = "psn:clientapp referenceDataService:countryConfig.read pushNotification:webSocket.desktop.connect sessionManager:remotePlaySession.system.update";
    public const string RedirectPrefix = "https://remoteplay.dl.playstation.net/remoteplay/redirect";

    private static readonly HttpClient Http = new(new SocketsHttpHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    public static Uri BuildAuthorizeUrl()
    {
        string query = BuildQuery(
        [
            ("service_entity", "urn:service-entity:psn"),
            ("response_type", "code"),
            ("client_id", ClientId),
            ("redirect_uri", RedirectPrefix),
            ("scope", Scope),
            ("request_locale", "en_US"),
            ("ui", "pr"),
            ("service_logo", "ps"),
            ("layout_type", "popup"),
            ("smcid", "remoteplay"),
            ("prompt", "always"),
            ("PlatformPrivacyWs1", "minimal")
        ]);
        return new Uri("https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/authorize?" + query);
    }

    public static Uri? ExtractRedirectUri(string? pastedText)
    {
        if (string.IsNullOrWhiteSpace(pastedText))
            return null;

        int start = pastedText.IndexOf(RedirectPrefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return null;

        int end = start;
        while (end < pastedText.Length && !char.IsWhiteSpace(pastedText[end]) && pastedText[end] is not ('"' or '\'' or '<' or '>'))
            end++;

        string candidate = RedirectPrefix + pastedText[(start + RedirectPrefix.Length)..end];
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ? uri : null;
    }

    public static string? ExtractCode(Uri redirectUri)
    {
        if (!redirectUri.AbsoluteUri.StartsWith(RedirectPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        foreach (var pair in redirectUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0) continue;
            if (Uri.UnescapeDataString(pair[..eq]) == "code")
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return null;
    }

    public static async Task<byte[]> ExchangeRedirectForAccountIdAsync(string pastedRedirect, CancellationToken ct = default)
    {
        var uri = ExtractRedirectUri(pastedRedirect)
            ?? throw new InvalidOperationException("Geçerli Sony yönlendirme bağlantısı bulunamadı.");

        string code = ExtractCode(uri)
            ?? throw new InvalidOperationException("Yönlendirme bağlantısında yetkilendirme kodu (code) yok.");

        using var tokenReq = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint);
        tokenReq.Headers.Authorization = CreateBasicAuth();
        tokenReq.Content = new StringContent(
            BuildQuery(
            [
                ("grant_type", "authorization_code"),
                ("code", code),
                ("scope", Scope),
                ("redirect_uri", RedirectPrefix)
            ]),
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using var tokenResp = await Http.SendAsync(tokenReq, ct).ConfigureAwait(false);
        string tokenJson = await tokenResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!tokenResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PSN token alınamadı (HTTP {(int)tokenResp.StatusCode}).");

        using var tokenDoc = JsonDocument.Parse(tokenJson);
        if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessProp) ||
            accessProp.GetString() is not { Length: > 0 } accessToken)
        {
            throw new InvalidOperationException("PSN yanıtında access_token bulunamadı.");
        }

        using var infoReq = new HttpRequestMessage(HttpMethod.Get, $"{TokenEndpoint}/{Uri.EscapeDataString(accessToken)}");
        infoReq.Headers.Authorization = CreateBasicAuth();
        using var infoResp = await Http.SendAsync(infoReq, ct).ConfigureAwait(false);
        string infoJson = await infoResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!infoResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PSN hesap bilgisi alınamadı (HTTP {(int)infoResp.StatusCode}).");

        using var infoDoc = JsonDocument.Parse(infoJson);
        if (!infoDoc.RootElement.TryGetProperty("user_id", out var uidProp))
            throw new InvalidOperationException("PSN hesap kimliği (user_id) bulunamadı.");

        string uid = uidProp.ValueKind == JsonValueKind.Number ? uidProp.GetRawText() : uidProp.GetString() ?? "";
        if (!ulong.TryParse(uid, NumberStyles.None, CultureInfo.InvariantCulture, out ulong numericId))
            throw new InvalidOperationException("PSN hesap kimliği sayısal değil.");

        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, numericId);
        return bytes;
    }

    private static AuthenticationHeaderValue CreateBasicAuth() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}")));

    private static string BuildQuery(IEnumerable<(string Key, string Value)> items) =>
        string.Join("&", items.Select(kv => Uri.EscapeDataString(kv.Key) + "=" + Uri.EscapeDataString(kv.Value)));
}
