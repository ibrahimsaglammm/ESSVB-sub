// Copyright (c) 2026 ESSVB. All Rights Reserved.

using EssvbSub.Core.Models;
using EssvbSub.Core.Storage;

namespace EssvbSub.Core.Pipeline;

public sealed record PipelineMetrics(int OcrMs, int TranslationMs, int TotalLatencyMs, bool FromCache);

/// <summary>
/// Orchestrates Frame -> ROI Crop -> Change Signature -> OCR -> Spatial Filter -> Stabilization -> Context Translation -> Output Sink.
/// Strictly prevents untranslated echoes or fallback errors from entering context history or disk cache.
/// </summary>
public sealed class SubtitlePipeline : IDisposable
{
    public const int LateResultMs = 4500;
    public const int MaxContextLines = 3;
    public const int MaxQueuedRequests = 2;

    private readonly ITextReader _reader;
    private readonly ITextTranslator _translator;
    private readonly ISubtitleSink _sink;
    private readonly ReadScheduler _scheduler;
    private readonly TranslationCache? _cache;
    private readonly Func<long> _clock;
    private readonly SubtitleStabilizer _stabilizer;
    private readonly SubtitleRegionFilter _regionFilter = new();
    private readonly object _gate = new();

    private readonly List<TranslationContextLine> _contextHistory = [];
    private readonly HashSet<string> _characterNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<TranslationJob> _queue = new();

    private int _session;
    private bool _runningJob;
    private CancellationTokenSource? _activeJobCts;
    private long? _activeLineLeftAt;
    private string _activeSource = "";
    private string _activeTranslation = "";
    private string? _mediaTitle;
    private string _sourceLanguage = "auto";
    private Reading _lastReading = Reading.Empty;
    private int _lastOcrMs;

    private sealed record TranslationJob(
        int Session,
        string Source,
        TextBox? Box,
        bool Replaces,
        long CapturedAt,
        int OcrMs);

    public SubtitlePipeline(
        ITextReader reader,
        ITextTranslator translator,
        ISubtitleSink sink,
        ReadScheduler? scheduler = null,
        TranslationCache? cache = null,
        Func<long>? clock = null)
    {
        _reader = reader;
        _translator = translator;
        _sink = sink;
        _scheduler = scheduler ?? new ReadScheduler();
        _cache = cache;
        _clock = clock ?? (() => Environment.TickCount64);
        _stabilizer = new SubtitleStabilizer(clock: _clock);
    }

    public bool ShowSpeakerNames { get; set; } = true;
    public bool StrictBottomOnly { get; set; } = true;
    public string GenrePresetId { get; set; } = "general";
    public string CacheSalt { get; set; } = "";

    public string SourceLanguage
    {
        get => _sourceLanguage;
        set
        {
            _sourceLanguage = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim();
            lock (_gate) _stabilizer.SourceLanguage = _sourceLanguage;
        }
    }

    public string? MediaTitle
    {
        get => _mediaTitle;
        set
        {
            lock (_gate)
            {
                if (!string.Equals(_mediaTitle, value, StringComparison.OrdinalIgnoreCase))
                {
                    _contextHistory.Clear();
                    _characterNames.Clear();
                }
                _mediaTitle = value;
            }
        }
    }

    public event Action<string>? OcrTextObserved;
    public event Action<string, string>? SubtitleTranslated;
    public event Action<PipelineMetrics>? MetricsUpdated;
    public event Action<string>? DiagnosticLogged;

    public async Task ProcessFrameAsync(Frame frame, CropRegion region, long? capturedAt = null)
    {
        if (!frame.IsConsistent || frame.Width < 16 || frame.Height < 16)
            return;

        long captured = capturedAt ?? _clock();
        int startedSession;
        lock (_gate) startedSession = _session;

        var (reading, ocrMs, signature) = await Task.Run(() => RunOcr(frame, region, startedSession));

        StabilizerAction action;
        TextBox? screenBox = null;

        lock (_gate)
        {
            if (_session != startedSession)
                return;

            var filtered = _regionFilter.Filter(reading, StrictBottomOnly, _clock(), _sourceLanguage);
            string cleaned = OcrNoise.Clean(filtered.Text, _sourceLanguage);
            screenBox = filtered.Box?.PlacedIn(region.AsTextBox());
            action = _stabilizer.Feed(cleaned, signature, filtered.Box);

            switch (action.Kind)
            {
                case StabilizerActionKind.Clear:
                    _activeLineLeftAt = _clock();
                    break;

                case StabilizerActionKind.Restore:
                    _activeLineLeftAt = null;
                    if (!string.IsNullOrEmpty(_activeTranslation) &&
                        TextMatch.IsSameSubtitle(TextMatch.Normalize(action.Text!), TextMatch.Normalize(_activeSource)))
                    {
                        _sink.SendSubtitle(_activeTranslation, _activeSource, box: screenBox, replaces: true);
                    }
                    return;

                case StabilizerActionKind.Translate:
                    _activeLineLeftAt = null;
                    if (action.IsRevision)
                        CancelInFlightForGrowth();
                    EnqueueTranslation(startedSession, action.Text!, screenBox, action.IsRevision, captured, ocrMs);
                    return;
            }
        }

        if (action.Kind == StabilizerActionKind.Clear)
            _sink.Clear();
    }

    /// <summary>
    /// Feeds already-transcribed text (e.g., from Audio Loopback / Whisper in MediaStream mode)
    /// directly through the context-aware translator and display sink.
    /// </summary>
    public Task TranslateTranscriptAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        int startedSession;
        lock (_gate)
        {
            startedSession = _session;
            _activeLineLeftAt = null;
            EnqueueTranslation(startedSession, text.Trim(), box: null, replaces: false, _clock(), ocrMs: 0);
        }
        return Task.CompletedTask;
    }

    public void Reset()
    {
        StabilizerAction clearAction;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _session++;
            _queue.Clear();
            cts = _activeJobCts;
            _activeJobCts = null;
            _scheduler.Reset();
            _regionFilter.Reset();
            clearAction = _stabilizer.Reset();
            _contextHistory.Clear();
            _characterNames.Clear();
            _activeSource = "";
            _activeTranslation = "";
            _activeLineLeftAt = null;
            _lastReading = Reading.Empty;
            _lastOcrMs = 0;
        }
        cts?.Cancel();
        _sink.SendAlert(null);
        if (clearAction.Kind == StabilizerActionKind.Clear)
            _sink.Clear();
    }

    private void CancelInFlightForGrowth()
    {
        try { _activeJobCts?.Cancel(); } catch { }
        _queue.Clear();
    }

    private (Reading Reading, int OcrMs, byte[] Signature) RunOcr(Frame frame, CropRegion region, int startedSession)
    {
        var cropped = RegionCropper.Crop(frame, region);
        var signature = RegionCropper.ComputeSignature(cropped);

        ReadPlan plan;
        Reading cachedReading;
        int cachedMs;
        lock (_gate)
        {
            if (_session != startedSession)
                return (Reading.Empty, 0, signature);
            plan = _scheduler.Plan(signature);
            cachedReading = _lastReading;
            cachedMs = _lastOcrMs;
        }

        if (!plan.ShouldRead)
            return (cachedReading, cachedMs, signature);

        long start = _clock();
        var reading = _reader.ReadLines(cropped, plan.Deep);
        int elapsed = (int)Math.Clamp(_clock() - start, 0, int.MaxValue);

        lock (_gate)
        {
            if (_session == startedSession)
            {
                _scheduler.Record(reading.Text);
                _lastReading = reading;
                _lastOcrMs = elapsed;
            }
        }

        return (reading, elapsed, signature);
    }

    private void EnqueueTranslation(int session, string source, TextBox? box, bool replaces, long capturedAt, int ocrMs)
    {
        // Avoid sending UI elements that are already written in Turkish to an AI translator
        string sourceBody = SpeakerPrefix.Parse(source)?.Body ?? source;
        if (TurkishText.LooksAlreadyTurkish(sourceBody))
            return;

        OcrTextObserved?.Invoke(source);

        if (SpeakerPrefix.Parse(source) is { } split)
            _characterNames.Add(split.Name);

        // Cache hit: show instantaneously with 0 ms translation latency
        string cacheKey = BuildCacheKey(source);
        if (_cache is not null && _cache.TryGet(cacheKey, out var cached) &&
            !TextMatch.IsUntranslatedEcho(source, cached))
        {
            ShowResult(session, source, cached, box, replaces, capturedAt, ocrMs, transMs: 0, fromCache: true, recordContext: true, cacheable: false);
            return;
        }

        while (_queue.Count >= MaxQueuedRequests)
            _queue.Dequeue();

        _queue.Enqueue(new TranslationJob(session, source, box, replaces, capturedAt, ocrMs));

        if (!_runningJob)
        {
            _runningJob = true;
            _ = ProcessQueueAsync();
        }
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            TranslationJob job;
            List<TranslationContextLine> context;
            List<string> names;
            string? title;
            CancellationTokenSource cts;

            lock (_gate)
            {
                if (_queue.Count == 0)
                {
                    _runningJob = false;
                    return;
                }

                job = _queue.Dequeue();
                if (job.Session != _session)
                    continue;

                if (_activeLineLeftAt is { } preLeftAt && (_clock() - preLeftAt) > LateResultMs)
                    continue;

                context = [.. _contextHistory];
                names = [.. _characterNames];
                title = MediaTitle;
                _activeJobCts?.Dispose();
                _activeJobCts = new CancellationTokenSource();
                cts = _activeJobCts;
            }

            try
            {
                long transStart = _clock();
                var split = SpeakerPrefix.Parse(job.Source);
                string bodyToTranslate = split?.Body ?? job.Source;

                TranslationOutcome outcome;
                if (_translator is ITranslationOutcomeTranslator detailed)
                {
                    outcome = await detailed.TranslateDetailedAsync(
                        bodyToTranslate,
                        context,
                        title,
                        names,
                        cts.Token).ConfigureAwait(false);
                }
                else
                {
                    string rawText = await _translator.TranslateAsync(
                        bodyToTranslate,
                        context,
                        title,
                        names,
                        cts.Token).ConfigureAwait(false);
                    outcome = new TranslationOutcome(rawText);
                }

                int transMs = (int)Math.Clamp(_clock() - transStart, 0, int.MaxValue);
                string finalTranslation = string.IsNullOrWhiteSpace(outcome.Text)
                    ? ""
                    : SpeakerPrefix.AttachIfEnabled(outcome.Text.Trim(), job.Source, ShowSpeakerNames);

                _sink.SendAlert(outcome.AlertMessage);

                bool isUntranslated = string.IsNullOrWhiteSpace(finalTranslation) ||
                                      TextMatch.IsUntranslatedEcho(job.Source, finalTranslation);

                if (isUntranslated)
                {
                    // Do NOT push untranslated English or empty strings into cache, context, or overlay!
                    lock (_gate)
                    {
                        if (job.Session == _session)
                            _stabilizer.ForgetSent();
                    }
                    if (!string.IsNullOrWhiteSpace(outcome.AlertMessage))
                        DiagnosticLogged?.Invoke(outcome.AlertMessage);
                    continue;
                }

                lock (_gate)
                {
                    if (job.Session != _session)
                        continue;

                    if (_activeLineLeftAt is { } leftAt && (_clock() - leftAt) > LateResultMs)
                    {
                        DiagnosticLogged?.Invoke($"Dropped stale translation ({_clock() - leftAt}ms late): {job.Source}");
                        continue;
                    }

                    bool allowCacheAndContext = !outcome.IsFallback;
                    if (allowCacheAndContext)
                        _cache?.Store(BuildCacheKey(job.Source), finalTranslation);

                    ShowResult(job.Session, job.Source, finalTranslation, job.Box, job.Replaces, job.CapturedAt, job.OcrMs, transMs,
                        fromCache: false, recordContext: allowCacheAndContext, cacheable: allowCacheAndContext);
                }
            }
            catch (OperationCanceledException)
            {
                // Pipeline reset or superseded by newer growth line
            }
            catch (Exception ex)
            {
                DiagnosticLogged?.Invoke($"Translation error: {ex.Message}");
                _sink.SendAlert($"⚠ Çeviri hatası: {ex.Message}");
                lock (_gate)
                {
                    if (job.Session == _session)
                        _stabilizer.ForgetSent();
                }
            }
        }
    }

    private void ShowResult(
        int session,
        string source,
        string translation,
        TextBox? box,
        bool replaces,
        long capturedAt,
        int ocrMs,
        int transMs,
        bool fromCache,
        bool recordContext,
        bool cacheable)
    {
        if (session != _session)
            return;

        _activeSource = source;
        _activeTranslation = translation;

        if (recordContext)
        {
            _contextHistory.Add(new TranslationContextLine(source, translation));
            while (_contextHistory.Count > MaxContextLines)
                _contextHistory.RemoveAt(0);
        }

        _sink.SendSubtitle(translation, source, box: box, replaces: replaces);
        SubtitleTranslated?.Invoke(source, translation);

        int totalMs = (int)Math.Clamp(_clock() - capturedAt, 0, int.MaxValue);
        MetricsUpdated?.Invoke(new PipelineMetrics(ocrMs, transMs, totalMs, fromCache && !cacheable ? false : fromCache));
    }

    private string BuildCacheKey(string source)
    {
        string salt = string.IsNullOrWhiteSpace(CacheSalt) ? "" : $"{CacheSalt}\u001f";
        return string.IsNullOrWhiteSpace(MediaTitle) ? $"{salt}{source}" : $"{salt}{MediaTitle}\u001f{source}";
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            _session++;
            _queue.Clear();
            cts = _activeJobCts;
            _activeJobCts = null;
        }
        cts?.Cancel();
        cts?.Dispose();
    }
}
