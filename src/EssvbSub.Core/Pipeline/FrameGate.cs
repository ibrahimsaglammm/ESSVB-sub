// Copyright (c) 2026 ESSVB. All Rights Reserved.

namespace EssvbSub.Core.Pipeline;

/// <summary>
/// Single-flight frame admission gate with monotonic token ownership and a 15-second stuck-OCR watchdog.
/// Optionally buffers the latest skipped frame so static-window pauses still process the final frame.
/// </summary>
public sealed class FrameGate
{
    public const int DefaultStuckAfterMs = 15_000;

    private readonly object _gate = new();
    private readonly int _stuckAfterMs;
    private bool _busy;
    private long _enteredAt;
    private long _currentToken;

    public FrameGate(int stuckAfterMs = DefaultStuckAfterMs)
    {
        _stuckAfterMs = Math.Max(1000, stuckAfterMs);
    }

    public bool TryEnter() => TryEnter(out _);

    public bool TryEnter(out long token)
    {
        lock (_gate)
        {
            if (_busy)
            {
                token = 0;
                return false;
            }

            _busy = true;
            _enteredAt = Environment.TickCount64;
            token = ++_currentToken;
            return true;
        }
    }

    public bool IsStuck(long? nowTick = null)
    {
        lock (_gate)
        {
            if (!_busy) return false;
            long now = nowTick ?? Environment.TickCount64;
            return (now - _enteredAt) >= _stuckAfterMs;
        }
    }

    public void Exit()
    {
        lock (_gate)
        {
            _busy = false;
        }
    }

    public void Exit(long token)
    {
        lock (_gate)
        {
            if (token == _currentToken)
                _busy = false;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _busy = false;
            _currentToken++;
        }
    }
}

public readonly record struct ReadPlan(bool ShouldRead, bool Deep);

/// <summary>
/// Decides whether the current frame changed enough to warrant running OCR,
/// skipping redundant work on static scenes while forcing periodic safety refreshes.
/// </summary>
public sealed class ReadScheduler
{
    public const double DefaultChangeThreshold = 0.015;
    private const int EmptyRefreshEveryFrames = 18;

    private readonly double _threshold;
    private readonly int _deepEveryUnchanged;
    private readonly bool _alwaysRead;
    private byte[]? _lastSignature;
    private string _lastText = "";
    private int _unchangedCount;

    public ReadScheduler(double threshold = DefaultChangeThreshold, int deepEveryUnchanged = 6, bool alwaysRead = false)
    {
        _threshold = threshold;
        _deepEveryUnchanged = Math.Max(1, deepEveryUnchanged);
        _alwaysRead = alwaysRead;
    }

    public static ReadScheduler Always() => new(alwaysRead: true);

    public ReadPlan Plan(byte[] signature)
    {
        if (_alwaysRead || _lastSignature is null)
        {
            _lastSignature = signature;
            _unchangedCount = 0;
            return new ReadPlan(ShouldRead: true, Deep: true);
        }

        double diff = RegionCropper.DifferenceRatio(_lastSignature, signature);
        if (diff >= _threshold)
        {
            _lastSignature = signature;
            _unchangedCount = 0;
            return new ReadPlan(ShouldRead: true, Deep: true);
        }

        _unchangedCount++;
        bool shouldPeriodicDeep = !string.IsNullOrEmpty(_lastText)
            ? (_unchangedCount % _deepEveryUnchanged == 0)
            : (_unchangedCount % EmptyRefreshEveryFrames == 0);

        return new ReadPlan(ShouldRead: shouldPeriodicDeep, Deep: shouldPeriodicDeep && string.IsNullOrEmpty(_lastText));
    }

    public void Record(string recognizedText) => _lastText = recognizedText;

    public void Reset()
    {
        _lastSignature = null;
        _lastText = "";
        _unchangedCount = 0;
    }
}
