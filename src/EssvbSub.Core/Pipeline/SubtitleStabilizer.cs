// Copyright (c) 2026 ESSVB. All Rights Reserved.

using EssvbSub.Core.Models;

namespace EssvbSub.Core.Pipeline;

public enum StabilizerActionKind
{
    None,
    Translate,
    Clear,
    Restore
}

public readonly record struct StabilizerAction(StabilizerActionKind Kind, string? Text = null, bool IsRevision = false)
{
    public static readonly StabilizerAction None = new(StabilizerActionKind.None);
    public static readonly StabilizerAction Clear = new(StabilizerActionKind.Clear);
    public static StabilizerAction Translate(string text, bool isRevision = false) => new(StabilizerActionKind.Translate, text, isRevision);
    public static StabilizerAction Restore(string text) => new(StabilizerActionKind.Restore, text);
}

/// <summary>
/// Filters frame-by-frame OCR output to eliminate typewriter flicker, bright-background OCR dropouts,
/// recent duplicate variants, and overlapping fade noise before triggering translation.
/// </summary>
public sealed class SubtitleStabilizer
{
    public const int DefaultClearAfterMs = 750;
    public const int HoldUnchangedMs = 3800;
    public const int RestoreWithinMs = 1500;
    public const int GarbledStableMs = 700;
    public const int RetryAfterMs = 1200;
    public const int RecentDedupWindowMs = 6000;
    private const int HistoryCapacity = 6;
    private const int RecentSentCapacity = 4;

    private readonly double _similarity;
    private readonly int _stableFrames;
    private readonly int _clearAfterMs;
    private readonly Func<long> _clock;
    private readonly List<(string Normalized, string Raw, long LeftAt)> _history = [];
    private readonly List<(string Normalized, string Raw, long SentAt)> _recentSent = [];

    private string _candidateNorm = "";
    private string _candidateRaw = "";
    private int _candidateCount;
    private long _candidateSince;

    private string _sentNorm = "";
    private string _sentRaw = "";
    private int _sentReads;

    private long? _emptySince;
    private long? _goneSince;
    private (byte[] Signature, TextBox Box)? _lastSeen;

    private string _forgottenNorm = "";
    private long _retryAt;
    private bool _clearPending;

    public SubtitleStabilizer(
        double similarity = 0.85,
        int stableFrames = 2,
        int clearAfterMs = DefaultClearAfterMs,
        Func<long>? clock = null)
    {
        _similarity = similarity;
        _stableFrames = Math.Max(1, stableFrames);
        _clearAfterMs = Math.Max(100, clearAfterMs);
        _clock = clock ?? (() => Environment.TickCount64);
    }

    public string SourceLanguage { get; set; } = "auto";

    public StabilizerAction Feed(string rawText, byte[]? signature = null, TextBox? box = null)
    {
        long now = _clock();
        string norm = TextMatch.Normalize(rawText);

        if (norm.Length < 3)
            return HandleEmpty(now, signature);

        _emptySince = null;
        _goneSince = null;

        if (TextMatch.Similarity(norm, _candidateNorm) >= _similarity ||
            TextMatch.IsSameSubtitle(norm, _candidateNorm))
        {
            _candidateCount++;
            if (OcrNoise.Score(rawText) > OcrNoise.Score(_candidateRaw) ||
                (rawText.Trim().Length > _candidateRaw.Length && OcrNoise.Score(rawText) >= OcrNoise.Score(_candidateRaw)))
            {
                _candidateRaw = rawText.Trim();
            }
        }
        else
        {
            _candidateNorm = norm;
            _candidateRaw = rawText.Trim();
            _candidateCount = 1;
            _candidateSince = now;
        }

        if (IsSameActive(norm, rawText))
        {
            Remember(signature, box);
            if (TryBuildRevision(norm, rawText, out var revised))
                return StabilizerAction.Translate(revised, isRevision: true);
            return StabilizerAction.None;
        }

        bool garbled = OcrNoise.LooksGarbled(rawText, SourceLanguage);
        bool ready = garbled
            ? _candidateCount >= _stableFrames && (now - _candidateSince) >= GarbledStableMs
            : _candidateCount >= _stableFrames || (EndsSentence(rawText) && OcrNoise.LooksClean(rawText));

        if (!ready)
            return StabilizerAction.None;

        if (_sentRaw.Length > 0 && TextMatch.IsWorseReread(rawText, _sentRaw))
        {
            Remember(signature, box);
            return StabilizerAction.None;
        }

        int restoredIdx = _history.FindLastIndex(h => (now - h.LeftAt) <= RestoreWithinMs && TextMatch.IsSameSubtitle(norm, h.Normalized));
        if (restoredIdx >= 0)
        {
            var restored = _history[restoredIdx];
            _history.RemoveAt(restoredIdx);
            ArchiveActive(now);
            _sentNorm = restored.Normalized;
            _sentRaw = restored.Raw;
            _sentReads = 1;
            Remember(signature, box);
            return StabilizerAction.Restore(restored.Raw);
        }

        // Suppress duplicate flickering variants of a recently sent subtitle unless meaning changed
        _recentSent.RemoveAll(r => (now - r.SentAt) > RecentDedupWindowMs);
        foreach (var prev in _recentSent)
        {
            if (TextMatch.IsSameSubtitle(norm, prev.Normalized) &&
                !TextMatch.AddsMeaning(prev.Normalized, norm) &&
                !TextMatch.IsGrowth(prev.Normalized, norm))
            {
                Remember(signature, box);
                return StabilizerAction.None;
            }
        }

        if (_forgottenNorm.Length > 0 && now < _retryAt && TextMatch.IsSameSubtitle(norm, _forgottenNorm))
            return StabilizerAction.None;

        bool isExtension = _sentRaw.Length > 0 && TextMatch.IsGrowth(_sentNorm, norm);
        ArchiveActive(now);
        _forgottenNorm = "";
        _sentRaw = _candidateRaw;
        _sentNorm = TextMatch.Normalize(_sentRaw);
        _sentReads = 1;
        RecordRecentSent(_sentNorm, _sentRaw, now);
        Remember(signature, box);
        return StabilizerAction.Translate(_sentRaw, isRevision: isExtension);
    }

    private StabilizerAction HandleEmpty(long now, byte[]? signature)
    {
        _candidateNorm = "";
        _candidateRaw = "";
        _candidateCount = 0;
        _emptySince ??= now;

        if (_sentNorm.Length == 0 && !_clearPending)
            return StabilizerAction.None;

        if (_goneSince is null && !LooksUnchanged(signature))
            _goneSince = now;

        if ((_goneSince is { } gone && (now - gone) >= _clearAfterMs) || (now - _emptySince.Value) >= HoldUnchangedMs)
        {
            ArchiveActive(now);
            return StabilizerAction.Clear;
        }

        return StabilizerAction.None;
    }

    private bool IsSameActive(string norm, string rawText)
    {
        if (_sentNorm.Length == 0)
            return false;

        if (TextMatch.IsSameSubtitle(norm, _sentNorm))
            return true;

        if (SpeakerPrefix.Parse(rawText) is null && SpeakerPrefix.Parse(_sentRaw) is { } named)
            return TextMatch.IsSameSubtitle(norm, TextMatch.Normalize(named.Body));

        return false;
    }

    private bool TryBuildRevision(string norm, string rawText, out string revised)
    {
        revised = "";
        if (_sentReads <= 0)
            return false;

        string trimmed = rawText.Trim();
        if (norm == _sentNorm)
        {
            _sentReads++;
            return false;
        }

        bool meaningful = TextMatch.AddsMeaning(_sentNorm, norm);
        bool significantFix = _candidateCount >= _stableFrames &&
                              OcrNoise.Score(trimmed) > OcrNoise.Score(_sentRaw) + 4;

        if (meaningful || significantFix)
        {
            _sentRaw = trimmed;
            _sentNorm = norm;
            _sentReads = 0; // Revise at most once per active line
            RecordRecentSent(_sentNorm, _sentRaw, _clock());
            revised = trimmed;
            return true;
        }

        return false;
    }

    public void ForgetSent()
    {
        if (_sentNorm.Length > 0)
        {
            _forgottenNorm = _sentNorm;
            string activeNorm = _sentNorm;
            _recentSent.RemoveAll(r => r.Normalized == activeNorm);
            _clearPending = true;
        }
        _retryAt = _clock() + RetryAfterMs;
        _sentNorm = "";
        _sentRaw = "";
        _candidateNorm = "";
        _candidateCount = 0;
    }

    public StabilizerAction Reset()
    {
        _candidateNorm = "";
        _candidateRaw = "";
        _candidateCount = 0;
        _emptySince = null;
        _goneSince = null;
        _lastSeen = null;
        _history.Clear();
        _recentSent.Clear();
        _forgottenNorm = "";
        bool shouldClear = _sentNorm.Length > 0 || _clearPending;
        _sentNorm = "";
        _sentRaw = "";
        _sentReads = 0;
        _clearPending = false;
        return shouldClear ? StabilizerAction.Clear : StabilizerAction.None;
    }

    private void RecordRecentSent(string norm, string raw, long now)
    {
        if (norm.Length == 0) return;
        _recentSent.RemoveAll(r => r.Normalized == norm);
        _recentSent.Add((norm, raw, now));
        while (_recentSent.Count > RecentSentCapacity)
            _recentSent.RemoveAt(0);
    }

    private void ArchiveActive(long now)
    {
        if (_sentNorm.Length > 0)
        {
            _history.Add((_sentNorm, _sentRaw, now));
            if (_history.Count > HistoryCapacity)
                _history.RemoveAt(0);
        }
        _sentNorm = "";
        _sentRaw = "";
        _sentReads = 0;
        _lastSeen = null;
        _clearPending = false;
    }

    private void Remember(byte[]? signature, TextBox? box) =>
        _lastSeen = signature is not null && box is { } b ? (signature, b) : null;

    private bool LooksUnchanged(byte[]? signature) =>
        signature is not null && _lastSeen is { } seen &&
        RegionCropper.DifferenceRatio(seen.Signature, signature, seen.Box) < ReadScheduler.DefaultChangeThreshold;

    public static bool EndsSentence(string raw)
    {
        var trimmed = raw.Trim().TrimEnd('"', '\'', '”', '’', '»', ')', ']');
        return trimmed.Length > 0 && ".!?…".Contains(trimmed[^1]);
    }
}
