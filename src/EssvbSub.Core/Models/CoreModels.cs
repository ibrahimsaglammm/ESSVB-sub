// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text.Json.Serialization;

namespace EssvbSub.Core.Models;

/// <summary>Primary operating mode selected by the user.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OperatingMode>))]
public enum OperatingMode
{
    /// <summary>PS5 or PS4 Pro streaming directly to Samsung Tizen TV (or PC overlay).</summary>
    PlayStation,
    /// <summary>PC games (Steam, Epic, GOG, etc.) rendered on the transparent monitor overlay.</summary>
    PcGame,
    /// <summary>Movies &amp; Series (Netflix, Amazon Prime, Disney+) via OCR or system audio loopback.</summary>
    MediaStream
}

/// <summary>Supported PlayStation hardware targets.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConsoleKind>))]
public enum ConsoleKind
{
    PlayStation5,
    PlayStation4Pro
}

/// <summary>Supported Samsung Tizen TV hardware profiles.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SamsungTvModel>))]
public enum SamsungTvModel
{
    AutoDetect,
    Cu8500_65,
    Mu9000,
    ModernTizen,
    LegacyTizen24
}

/// <summary>Where translated subtitles are displayed.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputTarget>))]
public enum OutputTarget
{
    PcOverlay,
    SamsungTv,
    Both
}

[JsonConverter(typeof(JsonStringEnumConverter<SubtitlePlacement>))]
public enum SubtitlePlacement
{
    Cover,
    Above,
    Below,
    Fixed
}

[JsonConverter(typeof(JsonStringEnumConverter<SubtitleBackgroundShape>))]
public enum SubtitleBackgroundShape
{
    Fit,
    Band
}

[JsonConverter(typeof(JsonStringEnumConverter<SubtitleTextColor>))]
public enum SubtitleTextColor
{
    White,
    Yellow,
    Cyan
}

public sealed record SubtitleStyle(
    int FontSize = 44,
    int BottomPercent = 7,
    SubtitlePlacement Placement = SubtitlePlacement.Cover,
    int BackgroundOpacity = 88,
    bool Bold = false,
    SubtitleTextColor TextColor = SubtitleTextColor.White,
    bool StackPrevious = true,
    SubtitleBackgroundShape BackgroundShape = SubtitleBackgroundShape.Fit,
    bool SpeakerColors = true);

/// <summary>Normalized bounding box [0..1] relative to the current frame or region.</summary>
public readonly record struct TextBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Math.Max(0, Right - Left);
    public double Height => Math.Max(0, Bottom - Top);
    public double Area => Width * Height;

    public TextBox Union(TextBox other) => new(
        Math.Min(Left, other.Left),
        Math.Min(Top, other.Top),
        Math.Max(Right, other.Right),
        Math.Max(Bottom, other.Bottom));

    public TextBox PlacedIn(TextBox outer) => new(
        outer.Left + (Left * outer.Width),
        outer.Top + (Top * outer.Height),
        outer.Left + (Right * outer.Width),
        outer.Top + (Bottom * outer.Height));
}

/// <summary>Normalized subtitle scan region [0..1] on the captured frame.</summary>
public sealed record CropRegion(double Top = 0.74, double Bottom = 0.96, double Left = 0.14, double Right = 0.86)
{
    public static CropRegion Default => new();
    public static CropRegion MediaBottom => new(0.75, 0.97, 0.14, 0.86);

    public CropRegion Clamp()
    {
        var l = Math.Clamp(Left, 0.0, 0.95);
        var t = Math.Clamp(Top, 0.0, 0.95);
        var r = Math.Clamp(Right, l + 0.05, 1.0);
        var b = Math.Clamp(Bottom, t + 0.04, 1.0);
        return new CropRegion(t, b, l, r);
    }

    public TextBox AsTextBox() => new(Left, Top, Right, Bottom);
}

public sealed record TextWord(string Text, TextBox Box);

public sealed record TextLine(
    string Text,
    TextBox? Box = null,
    double? Confidence = null,
    IReadOnlyList<TextWord>? Words = null);

public sealed class Reading
{
    public static readonly Reading Empty = new([]);

    public Reading(IEnumerable<TextLine> lines)
    {
        Lines = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
        Text = string.Join('\n', Lines.Select(l => l.Text));
        Box = Lines.Where(l => l.Box.HasValue)
            .Select(l => l.Box!.Value)
            .Aggregate<TextBox, TextBox?>(null, (acc, next) => acc?.Union(next) ?? next);

        var confs = Lines.Where(l => l.Confidence.HasValue).Select(l => l.Confidence!.Value).ToList();
        Confidence = confs.Count > 0 ? confs.Average() : null;
    }

    public IReadOnlyList<TextLine> Lines { get; }
    public string Text { get; }
    public TextBox? Box { get; }
    public double? Confidence { get; }

    public static Reading FromText(string text, TextBox? box = null) =>
        string.IsNullOrWhiteSpace(text)
            ? Empty
            : new Reading(text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => new TextLine(line.Trim(), box)));

    public Reading PlacedIn(TextBox outer) =>
        Lines.Count == 0
            ? Empty
            : new Reading(Lines.Select(l => l with
            {
                Box = l.Box?.PlacedIn(outer),
                Words = l.Words?.Select(w => w with { Box = w.Box.PlacedIn(outer) }).ToList()
            }));
}

public enum SubtitleEventKind
{
    Subtitle,
    Clear,
    Style,
    StatusAlert
}

public sealed record SpeakerColor(string Name, string HexColor);

public sealed record SubtitleEvent(
    SubtitleEventKind Kind,
    string Text = "",
    string SourceText = "",
    int DurationMs = 15000,
    TextBox? Box = null,
    bool Replaces = false,
    IReadOnlyList<SpeakerColor>? Speakers = null)
{
    public static readonly SubtitleEvent Cleared = new(SubtitleEventKind.Clear);
}

public sealed record TranslationContextLine(string Source, string? Translation = null);

public sealed record TranslationOutcome(
    string Text,
    bool IsFallback = false,
    bool IsRateLimited = false,
    string? AlertMessage = null);

public interface ITextReader
{
    string EngineName { get; }
    Reading ReadLines(Frame image, bool deep);
}

public interface ITextTranslator
{
    string EngineId { get; }
    bool IsReady { get; }
    Task<string> TranslateAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        CancellationToken cancellationToken = default);
}

public interface ITranslationOutcomeTranslator : ITextTranslator
{
    Task<TranslationOutcome> TranslateDetailedAsync(
        string text,
        IReadOnlyList<TranslationContextLine> context,
        string? mediaTitle,
        IReadOnlyList<string>? characterNames,
        CancellationToken cancellationToken = default);
}

public interface ISubtitleSink
{
    void SendSubtitle(string text, string sourceText, int durationMs = 15000, TextBox? box = null, bool replaces = false);
    void Clear();
    void SendStyle(SubtitleStyle style);
    void SendAlert(string? message) { }
}
