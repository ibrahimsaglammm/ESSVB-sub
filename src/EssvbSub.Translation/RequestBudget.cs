// Copyright (c) 2026 ESSVB. All Rights Reserved.

using EssvbSub.Translation.Providers;

namespace EssvbSub.Translation;

/// <summary>
/// Proactive 60-second sliding-window request budget to prevent flooding cloud rate limits (RPM)
/// and coordinate cooldown windows when the upstream server lowers quota caps.
/// </summary>
public sealed class RequestBudget
{
    private readonly Queue<DateTimeOffset> _timestamps = new();
    private readonly object _gate = new();
    private int _limitPerMinute;

    public RequestBudget(int limitPerMinute)
    {
        _limitPerMinute = limitPerMinute;
    }

    public int LimitPerMinute
    {
        get { lock (_gate) return _limitPerMinute; }
    }

    public static int DefaultLimitFor(AiProviderKind provider, string model)
    {
        if (provider != AiProviderKind.Gemini)
            return 0;

        string m = model.Trim().ToLowerInvariant();
        if (m.Contains("pro", StringComparison.Ordinal))
            return 5;
        if (m.Contains("flash-lite", StringComparison.Ordinal))
            return 15;
        if (m.Contains("flash", StringComparison.Ordinal))
            return 10;

        return 10;
    }

    public bool TryConsume(out int retryAfterSeconds, DateTimeOffset? nowOverride = null)
    {
        lock (_gate)
        {
            if (_limitPerMinute <= 0)
            {
                retryAfterSeconds = 0;
                return true;
            }

            DateTimeOffset now = nowOverride ?? DateTimeOffset.UtcNow;
            DateTimeOffset cutoff = now.AddMinutes(-1);

            while (_timestamps.Count > 0 && _timestamps.Peek() <= cutoff)
                _timestamps.Dequeue();

            if (_timestamps.Count < _limitPerMinute)
            {
                _timestamps.Enqueue(now);
                retryAfterSeconds = 0;
                return true;
            }

            DateTimeOffset oldest = _timestamps.Peek();
            int waitSec = (int)Math.Ceiling((oldest.AddMinutes(1) - now).TotalSeconds);
            retryAfterSeconds = Math.Clamp(waitSec, 1, 60);
            return false;
        }
    }

    public void LowerLimit(int reportedLimit)
    {
        if (reportedLimit <= 0)
            return;

        lock (_gate)
        {
            if (_limitPerMinute <= 0 || reportedLimit < _limitPerMinute)
                _limitPerMinute = reportedLimit;
        }
    }
}
