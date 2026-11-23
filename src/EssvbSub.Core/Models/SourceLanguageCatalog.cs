// Copyright (c) 2026 ESSVB. All Rights Reserved.

namespace EssvbSub.Core.Models;

/// <summary>
/// Supported source subtitle languages mapped to Windows OCR BCP-47 tags, DeepL codes, and Tesseract packs.
/// </summary>
public sealed record SourceLanguageOption(
    string Code,
    string DisplayName,
    string EnglishName,
    string DeepLCode,
    string TesseractCode,
    bool IsLatinScript)
{
    public override string ToString() => DisplayName;
}

public static class SourceLanguageCatalog
{
    public static IReadOnlyList<SourceLanguageOption> All { get; } =
    [
        new("auto", "Otomatik Algıla (Tüm Diller)", "the source language automatically", "", "eng", true),
        new("en", "İngilizce (English)", "English", "EN", "eng", true),
        new("ja", "Japonca (日本語)", "Japanese", "JA", "jpn", false),
        new("ko", "Korece (한국어)", "Korean", "KO", "kor", false),
        new("zh-Hans", "Çince - Basitleştirilmiş (中文)", "Simplified Chinese", "ZH", "chi_sim", false),
        new("zh-Hant", "Çince - Geleneksel (繁體中文)", "Traditional Chinese", "ZH", "chi_tra", false),
        new("ru", "Rusça (Русский)", "Russian", "RU", "rus", false),
        new("de", "Almanca (Deutsch)", "German", "DE", "deu", true),
        new("fr", "Fransızca (Français)", "French", "FR", "fra", true),
        new("es", "İspanyolca (Español)", "Spanish", "ES", "spa", true),
        new("it", "İtalyanca (Italiano)", "Italian", "IT", "ita", true),
        new("pt", "Portekizce (Português)", "Portuguese", "PT", "por", true),
        new("pl", "Lehçe (Polski)", "Polish", "PL", "pol", true),
        new("uk", "Ukraynaca (Українська)", "Ukrainian", "UK", "ukr", false),
        new("ar", "Arapça (العربية)", "Arabic", "AR", "ara", false)
    ];

    public static SourceLanguageOption Get(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return All[0];

        return All.FirstOrDefault(x => string.Equals(x.Code, code.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? All[0];
    }
}
