// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text;
using System.Text.RegularExpressions;
using EssvbSub.Core.Models;

namespace EssvbSub.Core.Pipeline;

public sealed record SpeakerSplit(string Name, string Body);

public static partial class SpeakerPrefix
{
    public static SpeakerSplit? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var trimmed = text.TrimStart();
        int colon = trimmed.IndexOf(':');
        if (colon <= 1 || colon > 30)
            return null;

        var candidate = trimmed[..colon].Trim();
        if (candidate.Length < 2 || candidate.Any(c => c is '/' or '\\' or '@' or '.'))
            return null;

        // Avoid timestamps like "12:30" or protocols like "http:"
        if (candidate.All(char.IsDigit) || candidate.Equals("http", StringComparison.OrdinalIgnoreCase) || candidate.Equals("https", StringComparison.OrdinalIgnoreCase))
            return null;

        int letters = candidate.Count(char.IsLetter);
        if (letters < 2 || candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3)
            return null;

        var body = trimmed[(colon + 1)..].TrimStart();
        return body.Length == 0 ? null : new SpeakerSplit(candidate, body);
    }

    public static string AttachIfEnabled(string translatedBody, string originalSource, bool showSpeakerNames)
    {
        if (!showSpeakerNames)
            return translatedBody;

        var origSplit = Parse(originalSource);
        if (origSplit is null)
            return translatedBody;

        var transSplit = Parse(translatedBody);
        if (transSplit is not null)
            return $"{origSplit.Name}: {transSplit.Body}";

        return $"{origSplit.Name}: {translatedBody}";
    }
}

public static partial class OcrNoise
{
    private static readonly HashSet<string> ShortValidWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "i", "o", "an", "am", "as", "at", "be", "by", "do", "go", "ha", "he", "hi",
        "if", "in", "is", "it", "me", "mr", "ms", "my", "no", "of", "oh", "ok", "on", "or",
        "so", "to", "tv", "uh", "um", "up", "us", "we", "ya", "ye"
    };

    [GeneratedRegex(@"\[(?:[A-Z0-9]{1,3}|Hold|Press|Tap)\s*[A-Z0-9]*\]|\((?:L1|L2|L3|R1|R2|R3|X|O|Triangle|Square|Cross|Circle)\)", RegexOptions.IgnoreCase)]
    private static partial Regex ButtonPromptRegex();

    [GeneratedRegex(@"[~`|\\^_=]{2,}|[^\p{L}\p{N}\s\.,!\?;:'""\-\(\)\[\]…&%$€£+/]")]
    private static partial Regex GarbageTokenRegex();

    public static string WithoutButtonPrompts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = ButtonPromptRegex().Replace(text, " ");
        return Regex.Replace(cleaned, @"[ \t]{2,}", " ").Trim();
    }

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var lines = text.Split('\n')
            .Select(l => WithoutButtonPrompts(l))
            .Select(l => GarbageTokenRegex().Replace(l, "").Trim())
            .Where(l => Score(l) > 0);
        return string.Join('\n', lines);
    }

    public static int Score(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        int score = 0;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim('.', ',', '!', '?', '…', ':', ';', '"', '\'', '“', '”', '‘', '’', '(', ')');
            if (word.Length == 0) continue;
            int letters = word.Count(char.IsLetter);
            int nonAlphaNum = word.Count(c => !char.IsLetterOrDigit(c) && c is not ('\'' or '’' or '-'));
            if (nonAlphaNum > 0 || letters == 0)
            {
                score -= 2;
                continue;
            }
            if (word.Length <= 2 && !ShortValidWords.Contains(word))
                score -= 1;
            else
                score += word.Length;
        }
        return Math.Max(0, score);
    }

    public static bool LooksClean(string raw)
    {
        var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;

        foreach (var token in tokens)
        {
            var word = token.Trim('.', ',', '!', '?', '…', ':', ';', '"', '\'', '“', '”', '‘', '’', '(', ')');
            if (word.Length == 0) return false;
            if (!word.All(c => char.IsLetterOrDigit(c) || c is '\'' or '’' or '-')) return false;
            if (word.Length <= 2 && word.All(char.IsLetter) && !ShortValidWords.Contains(word)) return false;
        }
        return true;
    }

    public static bool LooksGarbled(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        int visible = raw.Count(c => !char.IsWhiteSpace(c));
        int valid = raw.Count(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or ',' or '!' or '?' or '\'' or '’' or '-' or ':' or ';');
        return visible >= 4 && valid * 10 < visible * 7;
    }
}

public static class TextMatch
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        bool prevSpace = false;
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                prevSpace = false;
            }
            else if (char.IsWhiteSpace(c) && !prevSpace && sb.Length > 0)
            {
                sb.Append(' ');
                prevSpace = true;
            }
        }
        return sb.ToString().TrimEnd();
    }

    public static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        int maxLen = Math.Max(a.Length, b.Length);
        int dist = Levenshtein(a, b);
        return 1.0 - ((double)dist / maxLen);
    }

    public static bool IsSimilar(string a, string b, double threshold = 0.85)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        return Similarity(na, nb) >= threshold;
    }

    public static bool IsSameSubtitle(string normalizedA, string normalizedB)
    {
        if (normalizedA == normalizedB) return true;
        if (normalizedA.Length == 0 || normalizedB.Length == 0) return false;
        return Similarity(normalizedA, normalizedB) >= 0.85;
    }

    public static bool IsWorseReread(string candidateRaw, string sentRaw)
    {
        var candNorm = Normalize(candidateRaw);
        var sentNorm = Normalize(sentRaw);
        if (candNorm.Length == 0 || sentNorm.Length == 0) return false;
        return Similarity(candNorm, sentNorm) >= 0.72 && OcrNoise.Score(candidateRaw) <= OcrNoise.Score(sentRaw);
    }

    private static int Levenshtein(string s, string t)
    {
        int n = s.Length, m = t.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;

        for (int i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[m];
    }
}

public static class SubtitleWrap
{
    public readonly record struct Result(string Text, double Scale);

    public static Result Fit(string text, int maxCharsPerLine = 44, int maxLines = 3)
    {
        text = text.Trim();
        if (text.Length <= maxCharsPerLine || text.Contains('\n'))
            return new Result(text, 1.0);

        var words = TokenizeKeepingSpeaker(text);
        if (words.Count <= 1)
            return new Result(text, 1.0);

        int linesNeeded = Math.Min(maxLines, Math.Max(2, (int)Math.Ceiling((double)text.Length / maxCharsPerLine)));
        var wrapped = Balance(words, linesNeeded);
        double longest = wrapped.Split('\n').Max(l => l.Length);
        double scale = longest <= maxCharsPerLine ? 1.0 : Math.Max(0.76, maxCharsPerLine / longest);
        return new Result(wrapped, scale);
    }

    private static List<string> TokenizeKeepingSpeaker(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        for (int i = 0; i < Math.Min(3, words.Count - 1); i++)
        {
            if (words[i].EndsWith(':') || words[i].EndsWith('\uFF1A'))
            {
                words[0] = string.Join(' ', words.Take(i + 2));
                words.RemoveRange(1, i + 1);
                break;
            }
        }
        return words;
    }

    private static string Balance(List<string> words, int targetLines)
    {
        if (targetLines <= 1 || words.Count <= 1)
            return string.Join(' ', words);

        int totalLen = words.Sum(w => w.Length) + words.Count - 1;
        int targetLen = totalLen / targetLines;

        var lines = new List<string>(targetLines);
        var current = new StringBuilder();
        int remainingLines = targetLines;

        for (int i = 0; i < words.Count; i++)
        {
            if (current.Length > 0)
            {
                int remainingWords = words.Count - i;
                if (remainingLines > 1 && remainingWords >= remainingLines &&
                    current.Length + 1 + words[i].Length > targetLen + 4)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                    remainingLines--;
                }
                else
                {
                    current.Append(' ');
                }
            }
            current.Append(words[i]);
        }

        if (current.Length > 0)
            lines.Add(current.ToString());

        return string.Join('\n', lines);
    }
}

/// <summary>Deterministic high-contrast speaker color palette.</summary>
public sealed class SpeakerColorBook
{
    private static readonly string[] Palette =
    [
        "#FFD166", // Amber Gold
        "#38BDF8", // Cyan Sky
        "#6EE7B7", // Mint Emerald
        "#F472B6", // Rose
        "#A78BFA", // Lavender
        "#FB923C", // Coral Orange
        "#FACC15", // Bright Yellow
        "#2DD4BF", // Teal
    ];

    private readonly Dictionary<string, string> _assigned = new(StringComparer.OrdinalIgnoreCase);

    public string GetColor(string speakerName)
    {
        var key = speakerName.Trim();
        if (_assigned.TryGetValue(key, out var color))
            return color;

        color = Palette[_assigned.Count % Palette.Length];
        _assigned[key] = color;
        return color;
    }

    public void Reset() => _assigned.Clear();
}
