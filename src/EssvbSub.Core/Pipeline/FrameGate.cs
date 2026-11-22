// Copyright (c) 2026 ESSVB. All Rights Reserved.

namespace EssvbSub.Core.Pipeline;

/// <summary>
/// Single-flight frame admission gate preventing OCR backlogs on slower CPUs.
/// If OCR is still busy with a previous frame, incoming frames are dropped cleanly.
/// </summary>
public sealed class FrameGate
{
    private int _busy;

    public bool TryEnter() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _busy, 0);

    public void Reset() => Volatile.Write(ref _busy, 0);
}

public readonly record struct ReadPlan(bool ShouldRead, bool Deep);

/// <summary>
/// Decides whether the current frame changed enough to warrant running OCR,
/// skipping redundant work on static scenes while occasionally forcing a deep read.
/// </summary>
public sealed class ReadScheduler
{
    public const double DefaultChangeThreshold = 0.018;
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
        bool shouldPeriodicDeep = !string.IsNullOrEmpty(_lastText) && (_unchangedCount % _deepEveryUnchanged == 0);
        return new ReadPlan(ShouldRead: shouldPeriodicDeep, Deep: false);
    }

    public void Record(string recognizedText) => _lastText = recognizedText;

    public void Reset()
    {
        _lastSignature = null;
        _lastText = "";
        _unchangedCount = 0;
    }
}
