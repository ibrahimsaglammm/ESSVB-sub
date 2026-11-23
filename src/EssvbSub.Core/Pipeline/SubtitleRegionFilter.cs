// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text.RegularExpressions;
using EssvbSub.Core.Models;

namespace EssvbSub.Core.Pipeline;

/// <summary>
/// Spatial &amp; behavioral filter that isolates bottom-center dialogue subtitles while discarding
/// left/right/top HUD counters, button prompts, menus, signs, and sticky static screen overlays.
/// </summary>
public sealed partial class SubtitleRegionFilter
{
    private const long StickyAfterMs = 8000;
    private const long ForgetStaleMs = 15000;

    private readonly Dictionary<string, (long FirstSeenMs, long LastSeenMs)> _seenLines = new(StringComparer.Ordinal);

    [GeneratedRegex(@"^\s*(?:Press|Hold|Tap)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ActionVerbPromptRegex();

    [GeneratedRegex(@"^(?:L[1-3]|R[1-3]|LB|RB|LT|RT|X|Y|A|B|O|Tab|Esc|Enter|Shift|Ctrl|Alt|Space)(?:\s*\+\s*(?:L[1-3]|R[1-3]|LB|RB|LT|RT|X|Y|A|B|O))?(?:\s+\S.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ControllerPromptRegex();

    [GeneratedRegex(@"^\s*[\d\.\,\:/xX%+\-mMsSkKgG]+\s*$")]
    private static partial Regex NumericHudRegex();

    public Reading Filter(Reading reading, bool strictBottomCenter, long nowMs, string sourceLanguage = "auto")
    {
        if (reading.Lines.Count == 0)
        {
            PruneSeen(nowMs);
            return Reading.Empty;
        }

        // Clean and drop obvious prompt / numeric / garbage lines first
        var candidates = new List<TextLine>(reading.Lines.Count);
        foreach (var line in reading.Lines)
        {
            string cleaned = OcrNoise.Clean(line.Text, sourceLanguage);
            if (string.IsNullOrWhiteSpace(cleaned) || OcrNoise.Score(cleaned) <= 0)
                continue;
            if (IsHudOrPromptLine(cleaned, line.Box, reading.Lines.Count))
                continue;
            if (OcrNoise.LooksGarbled(cleaned, sourceLanguage))
                continue;

            candidates.Add(line with { Text = cleaned });
        }

        if (candidates.Count == 0)
        {
            PruneSeen(nowMs);
            return Reading.Empty;
        }

        // Exclude menu columns (>=3 vertically aligned short unpunctuated lines along left/right edges)
        if (IsMenuColumn(candidates))
        {
            PruneSeen(nowMs);
            return Reading.Empty;
        }

        // Track line longevity to strip sticky static HUD overlays (e.g., "CAUTION MIND YOUR STEP", YouTube badges)
        candidates = FilterStickyStaticHud(candidates, nowMs);
        if (candidates.Count == 0)
            return Reading.Empty;

        // If bounding boxes are available, enforce bottom-center spatial clustering
        bool hasBoxes = candidates.All(c => c.Box.HasValue);
        if (hasBoxes)
        {
            candidates = SelectBottomCenterBlock(candidates, strictBottomCenter);
            if (candidates.Count == 0)
                return Reading.Empty;
        }

        return new Reading(candidates);
    }

    public void Reset() => _seenLines.Clear();

    private List<TextLine> SelectBottomCenterBlock(List<TextLine> lines, bool strictBottomCenter)
    {
        var filtered = new List<TextLine>(lines.Count);
        foreach (var line in lines)
        {
            var b = line.Box!.Value;
            double cx = (b.Left + b.Right) * 0.5;
            double cy = (b.Top + b.Bottom) * 0.5;

            if (strictBottomCenter)
            {
                // Reject lines wholly confined to left margin, right margin, or top strip
                if (b.Right < 0.22 || cx < 0.18)
                    continue;
                if (b.Left > 0.78 || cx > 0.82)
                    continue;
                if (b.Bottom < 0.26 && cy < 0.20 && lines.Any(o => o.Box!.Value.Bottom >= 0.35))
                    continue;
                if (cy < 0.22 && !EndsOrHasDialogue(line.Text))
                    continue;
            }
            filtered.Add(line);
        }

        if (filtered.Count == 0)
            return [];

        if (filtered.Count == 1)
            return filtered;

        // Cluster vertically contiguous lines with horizontal overlap into candidate subtitle blocks
        var ordered = filtered.OrderBy(l => l.Box!.Value.Top).ThenBy(l => l.Box!.Value.Left).ToList();
        var blocks = new List<List<TextLine>>();

        foreach (var line in ordered)
        {
            var lb = line.Box!.Value;
            List<TextLine>? targetBlock = null;
            foreach (var blk in blocks)
            {
                var last = blk[^1].Box!.Value;
                double avgH = Math.Max(0.03, (last.Height + lb.Height) * 0.5);
                double gap = lb.Top - last.Bottom;
                bool verticallyClose = gap >= -avgH * 0.35 && gap <= avgH * 1.45;
                bool overlapsHoriz = HorizontalOverlapRatio(last, lb) >= 0.25 ||
                                     Math.Abs(((last.Left + last.Right) * 0.5) - ((lb.Left + lb.Right) * 0.5)) <= 0.18;
                if (verticallyClose && overlapsHoriz)
                {
                    targetBlock = blk;
                    break;
                }
            }

            if (targetBlock is not null)
                targetBlock.Add(line);
            else
                blocks.Add([line]);
        }

        if (blocks.Count == 1)
            return blocks[0];

        // Score blocks: prefer bottom-most, centered, longer/cleaner dialogue over corner/upper HUD
        List<TextLine> bestBlock = blocks[0];
        double bestScore = double.NegativeInfinity;

        foreach (var blk in blocks)
        {
            var box = blk.Select(l => l.Box!.Value).Aggregate((a, b) => a.Union(b));
            double cx = (box.Left + box.Right) * 0.5;
            double cy = (box.Top + box.Bottom) * 0.5;
            double distCenter = Math.Abs(cx - 0.5);

            if (strictBottomCenter && distCenter > 0.30)
                continue;

            int textScore = blk.Sum(l => OcrNoise.Score(l.Text));
            bool hasSpeaker = SpeakerPrefix.Parse(blk[0].Text) is not null;
            double score = (cy * 45.0) - (distCenter * 38.0) + Math.Min(30.0, textScore * 0.6) + (hasSpeaker ? 15.0 : 0.0);

            if (score > bestScore)
            {
                bestScore = score;
                bestBlock = blk;
            }
        }

        return bestBlock;
    }

    private List<TextLine> FilterStickyStaticHud(List<TextLine> lines, long nowMs)
    {
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in lines)
        {
            string norm = TextMatch.Normalize(l.Text);
            if (norm.Length == 0) continue;
            activeKeys.Add(norm);
            if (_seenLines.TryGetValue(norm, out var times))
                _seenLines[norm] = (times.FirstSeenMs, nowMs);
            else
                _seenLines[norm] = (nowMs, nowMs);
        }

        PruneSeen(nowMs);

        if (lines.Count <= 1)
            return lines;

        var kept = new List<TextLine>(lines.Count);
        foreach (var l in lines)
        {
            string norm = TextMatch.Normalize(l.Text);
            bool isSticky = norm.Length <= 48 &&
                            _seenLines.TryGetValue(norm, out var t) &&
                            (nowMs - t.FirstSeenMs) >= StickyAfterMs;

            bool anotherFreshLineExists = lines.Any(o =>
            {
                string on = TextMatch.Normalize(o.Text);
                return on != norm && _seenLines.TryGetValue(on, out var ot) && (nowMs - ot.FirstSeenMs) < StickyAfterMs;
            });

            if (isSticky && anotherFreshLineExists)
                continue;

            kept.Add(l);
        }

        return kept.Count > 0 ? kept : lines;
    }

    private void PruneSeen(long nowMs)
    {
        var expired = _seenLines.Where(kv => (nowMs - kv.Value.LastSeenMs) > ForgetStaleMs).Select(kv => kv.Key).ToList();
        foreach (var k in expired)
            _seenLines.Remove(k);
    }

    private static bool IsHudOrPromptLine(string text, TextBox? box, int totalLines)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        if (NumericHudRegex().IsMatch(trimmed)) return true;

        if (ActionVerbPromptRegex().IsMatch(trimmed) && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 6)
            return true;

        if (ControllerPromptRegex().IsMatch(trimmed) && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4)
            return true;

        // Short unpunctuated ALL-CAPS environmental sign / badge (e.g., "CAUTION MIND YOUR STEP", "CALL NOW TO BOOK")
        int words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        int letters = trimmed.Count(char.IsLetter);
        bool allCaps = letters >= 4 && trimmed.Where(char.IsLetter).All(char.IsUpper);
        bool hasSentenceEnd = ".!?…".Contains(trimmed[^1]);
        bool isSpeakerLine = SpeakerPrefix.Parse(trimmed) is not null;

        if (allCaps && !isSpeakerLine && words <= 4 && !hasSentenceEnd && totalLines > 1)
            return true;

        // Edge-hugging tiny badge alongside dialogue
        if (totalLines > 1 && box is { } b && words <= 3 && !hasSentenceEnd &&
            (b.CenterY() < 0.25 || b.Right < 0.24 || b.Left > 0.76))
        {
            return true;
        }

        return false;
    }

    private static bool IsMenuColumn(List<TextLine> lines)
    {
        if (lines.Count < 3 || !lines.All(l => l.Box.HasValue))
            return false;

        int shortUnpunctuated = lines.Count(l =>
        {
            string t = l.Text.Trim();
            int w = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            return w <= 3 && !".!?…:".Contains(t[^1]);
        });

        if (shortUnpunctuated < 3)
            return false;

        double firstLeft = lines[0].Box!.Value.Left;
        bool leftAligned = lines.All(l => Math.Abs(l.Box!.Value.Left - firstLeft) <= 0.025);
        return leftAligned;
    }

    private static bool EndsOrHasDialogue(string text)
    {
        string t = text.Trim();
        if (t.Length == 0) return false;
        return ".!?…".Contains(t[^1]) || SpeakerPrefix.Parse(t) is not null || t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 4;
    }

    private static double HorizontalOverlapRatio(TextBox a, TextBox b)
    {
        double overlap = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left));
        double minW = Math.Max(0.01, Math.Min(a.Width, b.Width));
        return overlap / minW;
    }
}

internal static class TextBoxExtensions
{
    public static double CenterY(this TextBox box) => (box.Top + box.Bottom) * 0.5;
}
