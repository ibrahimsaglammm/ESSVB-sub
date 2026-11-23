// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text;
using System.Text.RegularExpressions;
using EssvbSub.Core.Models;

namespace EssvbSub.Core.Pipeline;

public sealed record SpeakerSplit(string Name, string Body);

public static partial class SpeakerPrefix
{
    private static readonly HashSet<string> NotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "note", "warning", "caution", "objective", "mission", "quest",
        "tip", "hint", "error", "system", "status", "alert", "notice",
        "hedef", "görev", "uyarı", "not", "ipucu", "http", "https"
    };

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

        if (candidate.All(char.IsDigit) || NotNames.Contains(candidate))
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

public static class TurkishText
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ve", "bir", "bu", "şu", "o", "için", "değil", "ama", "fakat", "ancak",
        "ile", "gibi", "kadar", "olarak", "yeni", "tümü", "ayarlar", "abone",
        "paylaş", "lütfen", "şimdi", "burada", "orada", "hayır", "evet", "tamam",
        "nasıl", "neden", "niye", "ben", "sen", "biz", "siz", "onlar", "var", "yok",
        "çok", "daha", "en", "hem", "veya", "ya", "da", "de", "ki", "mi", "mı", "mu", "mü",
        "göre", "önce", "sonra", "artık", "bile", "belki", "sadece", "yani", "işte",
        "ekranı", "yukarı", "aşağı", "kaydırın", "görüntüle", "yorumlar"
    };

    public static bool ContainsUniqueTurkishLetters(string text)
    {
        foreach (char c in text)
        {
            if (c is 'ğ' or 'Ğ' or 'ş' or 'Ş' or 'ı' or 'İ')
                return true;
        }
        return false;
    }

    public static bool ContainsAnyTurkishDiacritic(string text)
    {
        foreach (char c in text)
        {
            if (c is 'ğ' or 'Ğ' or 'ş' or 'Ş' or 'ı' or 'İ' or 'ç' or 'Ç' or 'ö' or 'Ö' or 'ü' or 'Ü')
                return true;
        }
        return false;
    }

    public static bool LooksAlreadyTurkish(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        int uniqueTr = 0;
        foreach (char c in text)
        {
            if (c is 'ğ' or 'Ğ' or 'ş' or 'Ş' or 'ı' or 'İ')
                uniqueTr++;
        }

        int wordHits = 0;
        int totalWords = 0;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string w = raw.Trim('.', ',', '!', '?', '…', ':', ';', '"', '\'', '(', ')');
            if (w.Length == 0) continue;
            totalWords++;
            if (CommonWords.Contains(w))
                wordHits++;
        }

        if (totalWords == 0)
            return false;

        return (uniqueTr >= 1 && wordHits >= 1) || wordHits >= 2 || uniqueTr >= 3;
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

    [GeneratedRegex(@"^\s*(?:\d{1,3}[mM]\s+|\d{1,2}:\d{2}\s+)(?=\p{L})")]
    private static partial Regex LeadingHudNumberRegex();

    [GeneratedRegex(@"[~`|\\^_=]{2,}|[^\p{L}\p{N}\s\.,!\?;:'""\-\(\)\[\]…&%$€£+/]")]
    private static partial Regex GarbageTokenRegex();

    [GeneratedRegex(@"(?:(?<=\s)|^)[l|](?='(?:m|ll|ve|d)\b)|(?:(?<=\s)|^)\|[a-z]")]
    private static partial Regex MisreadIRegex();

    public static string WithoutButtonPrompts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var cleaned = ButtonPromptRegex().Replace(text, " ");
        return Regex.Replace(cleaned, @"[ \t]{2,}", " ").Trim();
    }

    public static string Clean(string text) => Clean(text, "auto");

    public static string Clean(string text, string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        bool isEnglish = string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase);

        var cleanedLines = new List<string>();
        foreach (var rawLine in text.Split('\n'))
        {
            string line = WithoutButtonPrompts(rawLine);
            line = LeadingHudNumberRegex().Replace(line, "");
            line = GarbageTokenRegex().Replace(line, "").Trim();

            if (isEnglish)
            {
                line = line.Replace('İ', 'I').Replace('ı', 'i');
                line = MisreadIRegex().Replace(line, m => "I" + m.Value[1..]);
            }

            if (Score(line) > 0)
                cleanedLines.Add(line);
        }
        return string.Join('\n', cleanedLines);
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
            if (word.Length <= 2 && !ShortValidWords.Contains(word) && word.All(IsBasicLatinLetter))
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
            if (word.Length <= 2 && word.All(IsBasicLatinLetter) && !ShortValidWords.Contains(word)) return false;
        }
        return true;
    }

    public static bool LooksGarbled(string raw) => LooksGarbled(raw, "en");

    public static bool LooksGarbled(string raw, string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;

        int visible = raw.Count(c => !char.IsWhiteSpace(c));
        int valid = raw.Count(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or ',' or '!' or '?' or '\'' or '’' or '-' or ':' or ';');
        if (visible >= 4 && valid * 10 < visible * 7)
            return true;

        bool isEnglish = string.Equals(sourceLanguage, "en", StringComparison.OrdinalIgnoreCase);
        if (!isEnglish)
            return false;

        var tokens = raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int badTokens = 0;
        int totalTokens = 0;

        foreach (var tok in tokens)
        {
            var word = tok.Trim('.', ',', '!', '?', '…', ':', ';', '"', '\'', '“', '”', '‘', '’', '(', ')');
            if (word.Length < 2) continue;
            totalTokens++;

            bool hasUnexpectedDiacritic = word.Any(c => c is 'ö' or 'Ö' or 'ü' or 'Ü' or 'ä' or 'Ä' or 'ß' or 'é' or 'å' or 'ø' or 'æ');
            bool hasMixedDigitInWord = word.Any(char.IsDigit) && word.Any(char.IsLetter) && word.Length >= 5;
            bool hasMidWordUpperGarbage = IsSuspiciousMixedCase(word);

            if (hasUnexpectedDiacritic || hasMixedDigitInWord || hasMidWordUpperGarbage)
                badTokens++;
        }

        return totalTokens >= 2 && badTokens * 5 > totalTokens * 2;
    }

    private static bool IsBasicLatinLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    private static bool IsSuspiciousMixedCase(string word)
    {
        if (word.Length < 4 || word.All(char.IsUpper))
            return false;

        int internalUpper = 0;
        for (int i = 1; i < word.Length; i++)
        {
            if (char.IsUpper(word[i]) && char.IsLower(word[i - 1]))
                internalUpper++;
        }
        return internalUpper >= 2;
    }
}

public static class TextMatch
{
    private static readonly HashSet<string> NegationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "not", "no", "never", "neither", "nor", "nothing", "nobody", "nowhere",
        "cant", "wont", "dont", "didnt", "isnt", "arent", "wasnt", "werent",
        "couldnt", "wouldnt", "shouldnt", "havent", "hasnt", "hadnt", "aint", "musnt"
    };

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

    public static bool IsGrowth(string olderNorm, string newerNorm) =>
        olderNorm.Length >= 4 &&
        newerNorm.Length >= olderNorm.Length + 4 &&
        newerNorm.StartsWith(olderNorm, StringComparison.Ordinal);

    public static bool AddsMeaning(string olderNorm, string newerNorm)
    {
        var olderTokens = olderNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var newerTokens = newerNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int olderNeg = olderTokens.Count(NegationTokens.Contains);
        int newerNeg = newerTokens.Count(NegationTokens.Contains);
        if (olderNeg != newerNeg)
            return true;

        var olderNums = olderTokens.Where(t => t.All(char.IsDigit)).ToList();
        var newerNums = newerTokens.Where(t => t.All(char.IsDigit)).ToList();
        if (olderNums.Count != newerNums.Count || !olderNums.SequenceEqual(newerNums))
            return true;

        return false;
    }

    public static double WordOverlap(string normA, string normB)
    {
        var wordsA = normA.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var wordsB = normB.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (wordsA.Length == 0 || wordsB.Length == 0)
            return 0.0;

        var setB = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in wordsB)
            setB[w] = setB.TryGetValue(w, out int c) ? c + 1 : 1;

        int matches = 0;
        foreach (var w in wordsA)
        {
            if (setB.TryGetValue(w, out int rem) && rem > 0)
            {
                matches++;
                setB[w] = rem - 1;
            }
        }

        return (double)matches / Math.Max(wordsA.Length, wordsB.Length);
    }

    public static bool IsSameSubtitle(string normalizedA, string normalizedB)
    {
        if (normalizedA == normalizedB) return true;
        if (normalizedA.Length == 0 || normalizedB.Length == 0) return false;
        if (IsGrowth(normalizedA, normalizedB) || IsGrowth(normalizedB, normalizedA))
            return false;
        if (AddsMeaning(normalizedA, normalizedB))
            return false;

        if (Similarity(normalizedA, normalizedB) >= 0.80)
            return true;

        int wordCountA = normalizedA.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int wordCountB = normalizedB.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCountA >= 4 && wordCountB >= 4 && WordOverlap(normalizedA, normalizedB) >= 0.70;
    }

    public static bool IsWorseReread(string candidateRaw, string sentRaw)
    {
        var candNorm = Normalize(candidateRaw);
        var sentNorm = Normalize(sentRaw);
        if (candNorm.Length == 0 || sentNorm.Length == 0) return false;
        if (IsGrowth(sentNorm, candNorm) || AddsMeaning(sentNorm, candNorm))
            return false;

        int sentWords = sentNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        bool closeMatch = Similarity(candNorm, sentNorm) >= 0.72 ||
            (sentWords >= 4 && WordOverlap(candNorm, sentNorm) >= 0.65);

        return closeMatch && OcrNoise.Score(candidateRaw) <= OcrNoise.Score(sentRaw);
    }

    public static bool IsUntranslatedEcho(string source, string translation)
    {
        string srcBody = SpeakerPrefix.Parse(source)?.Body ?? source;
        string trBody = SpeakerPrefix.Parse(translation)?.Body ?? translation;

        string normSrc = Normalize(srcBody);
        string normTr = Normalize(trBody);
        if (normSrc.Length == 0 || normTr.Length == 0)
            return false;

        if (TurkishText.LooksAlreadyTurkish(srcBody))
            return false;

        int letterCount = srcBody.Count(char.IsLetter);
        int wordCount = normSrc.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (wordCount < 2 || letterCount < 10)
            return false;

        if (!TurkishText.ContainsAnyTurkishDiacritic(srcBody) && TurkishText.ContainsAnyTurkishDiacritic(trBody))
            return false;

        if (normSrc == normTr || Similarity(normSrc, normTr) >= 0.90)
            return true;

        return wordCount >= 4 && WordOverlap(normSrc, normTr) >= 0.85;
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
