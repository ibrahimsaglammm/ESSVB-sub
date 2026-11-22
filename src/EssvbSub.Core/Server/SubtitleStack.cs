// Copyright (c) 2026 ESSVB. All Rights Reserved.

using EssvbSub.Core.Pipeline;

namespace EssvbSub.Core.Server;

/// <summary>
/// Synchronizes stacked rapid-dialogue lines (up to 3 visible lines: Older, Previous, Current)
/// with consistent fade/hold durations across both PC overlay and Samsung Tizen TV.
/// </summary>
public sealed class SubtitleStack
{
    public const double StackWithinSec = 1.5;
    public const double PreviousHoldSec = 1.5;
    public const double PreviousFadeSec = 0.4;
    public const double MinVisibleSec = 1.2;
    public const double HoldMaxSec = 5.0;

    public enum Role { Current, Previous, Older }

    public sealed record Line(int Id, string Text, Role Role, bool Fading)
    {
        public SubtitleWrap.Result Wrapped => SubtitleWrap.Fit(Text);
    }

    private sealed record CurrentSlot(int Id, string Text, double ShownAt, double HideAt, double HoldUntil);

    private sealed record StackedSlot(int Id, string Text, double ShownAt)
    {
        public double FadeAt => ShownAt + PreviousHoldSec;
        public double HideAt => FadeAt + PreviousFadeSec;
    }

    public bool StackPrevious { get; set; } = true;

    private CurrentSlot? _current;
    private StackedSlot? _previous;
    private StackedSlot? _older;
    private int _nextId;

    public void Show(string text, int durationMs, double nowSec, bool replaces = false)
    {
        Prune(nowSec);

        if (replaces && _current is not null)
        {
            // Keep existing upper stacked lines intact when revising current line
        }
        else if (StackPrevious && _current is { } active &&
                 (nowSec - active.ShownAt) < StackWithinSec &&
                 !TextMatch.IsSimilar(active.Text, text, 0.85))
        {
            PushPrevious(active.Text, nowSec);
        }
        else
        {
            _previous = null;
            _older = null;
        }

        double readingSec = 2.0 + (text.Length * 0.06);
        double visibleSec = durationMs > 0 ? Math.Max(MinVisibleSec, durationMs / 1000.0) : Math.Min(8.0, readingSec);
        _current = new CurrentSlot(++_nextId, text, nowSec, nowSec + visibleSec, nowSec + Math.Min(HoldMaxSec, readingSec));
    }

    public void Clear(double nowSec)
    {
        Prune(nowSec);
        if (_current is not { } active)
            return;

        if (active.HoldUntil <= nowSec)
            Hide();
        else
            _current = active with { HideAt = active.HoldUntil };
    }

    public void Hide()
    {
        _current = null;
        _previous = null;
        _older = null;
    }

    public IReadOnlyList<Line> Lines(double nowSec)
    {
        if (_current is not { } active || nowSec >= active.HideAt)
            return [];

        var list = new List<Line>(3);
        if (_older is { } older && nowSec < older.HideAt)
            list.Add(new Line(older.Id, older.Text, Role.Older, nowSec >= older.FadeAt));
        if (_previous is { } prev && nowSec < prev.HideAt)
            list.Add(new Line(prev.Id, prev.Text, Role.Previous, nowSec >= prev.FadeAt));

        list.Add(new Line(active.Id, active.Text, Role.Current, Fading: false));
        return list;
    }

    public double? NextChange(double nowSec)
    {
        if (_current is not { } active || nowSec >= active.HideAt)
            return null;

        double earliest = active.HideAt;
        if (_previous is { } prev)
        {
            if (prev.FadeAt > nowSec) earliest = Math.Min(earliest, prev.FadeAt);
            if (prev.HideAt > nowSec) earliest = Math.Min(earliest, prev.HideAt);
        }
        if (_older is { } older)
        {
            if (older.FadeAt > nowSec) earliest = Math.Min(earliest, older.FadeAt);
            if (older.HideAt > nowSec) earliest = Math.Min(earliest, older.HideAt);
        }

        return earliest > nowSec ? earliest : null;
    }

    private void PushPrevious(string text, double nowSec)
    {
        _older = _previous is { } prev && nowSec < prev.FadeAt && !TextMatch.IsSimilar(prev.Text, text, 0.85)
            ? new StackedSlot(++_nextId, prev.Text, nowSec)
            : null;
        _previous = new StackedSlot(++_nextId, text, nowSec);
    }

    private void Prune(double nowSec)
    {
        if (_current is { } active && nowSec >= active.HideAt)
            Hide();
        if (_previous is { } prev && nowSec >= prev.HideAt)
            _previous = null;
        if (_older is { } older && nowSec >= older.HideAt)
            _older = null;
    }
}
