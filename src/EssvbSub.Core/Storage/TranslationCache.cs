// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EssvbSub.Core.Pipeline;

namespace EssvbSub.Core.Storage;

/// <summary>
/// Persistent per-engine JSONL LRU cache so repeated lines translate in 0 ms without extra API/compute usage.
/// Rejects untranslated echo / fallback entries on write and auto-scrubs poisoned entries on load.
/// </summary>
public sealed class TranslationCache
{
    public const int DefaultLimit = 25_000;
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly ConcurrentDictionary<string, TranslationCache> Instances = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? _filePath;
    private readonly int _limit;
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Value, long Tick)> _entries = new(StringComparer.Ordinal);
    private long _tick;
    private bool _loaded;
    private int _fileLines;
    private int _scrubbedOnLoad;

    public TranslationCache(string? filePath = null, int limit = DefaultLimit, string? engineId = null)
    {
        _filePath = filePath;
        _limit = Math.Max(1, limit);
        EngineId = engineId;
    }

    public string? EngineId { get; }

    public static TranslationCache ForEngine(string directory, string engineId, string sourceLang, string targetLang)
    {
        string raw = $"{engineId}\0{sourceLang}\0{targetLang}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant();
        string path = Path.Combine(directory, $"v1-{hash}.jsonl");
        return Instances.GetOrAdd(path, p => new TranslationCache(p, DefaultLimit, engineId));
    }

    public int Count
    {
        get { lock (_gate) { EnsureLoaded(); return _entries.Count; } }
    }

    public static bool IsPoisonedEntry(string sourceKey, string translation)
    {
        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(translation))
            return true;

        int sep = sourceKey.LastIndexOf('\u001f');
        string rawSource = sep >= 0 && sep + 1 < sourceKey.Length ? sourceKey[(sep + 1)..] : sourceKey;
        return TextMatch.IsUntranslatedEcho(rawSource, translation);
    }

    public int ScrubPoisonedEntries()
    {
        lock (_gate)
        {
            EnsureLoaded();
            int removed = _scrubbedOnLoad;
            var badKeys = _entries.Where(kv => IsPoisonedEntry(kv.Key, kv.Value.Value)).Select(kv => kv.Key).ToList();
            if (badKeys.Count > 0)
            {
                foreach (var k in badKeys)
                    _entries.Remove(k);
                Compact();
                removed += badKeys.Count;
            }
            return removed;
        }
    }

    public bool TryGet(string key, out string translation)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries.TryGetValue(key, out var entry) && !IsPoisonedEntry(key, entry.Value))
            {
                _entries[key] = (entry.Value, ++_tick);
                translation = entry.Value;
                return true;
            }
            translation = "";
            return false;
        }
    }

    public void Store(string key, string translation)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(translation))
            return;

        if (IsPoisonedEntry(key, translation))
            return;

        lock (_gate)
        {
            EnsureLoaded();
            if (_entries.TryGetValue(key, out var existing) && existing.Value == translation)
            {
                _entries[key] = (translation, ++_tick);
                return;
            }

            _entries[key] = (translation, ++_tick);
            EvictIfNeeded();
            AppendLine(key, translation);
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        if (_filePath is null || !File.Exists(_filePath)) return;

        int scrubbed = 0;
        try
        {
            foreach (var line in File.ReadLines(_filePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                _fileLines++;
                try
                {
                    var pair = JsonSerializer.Deserialize<string[]>(line);
                    if (pair is { Length: 2 } && !string.IsNullOrEmpty(pair[0]) && !string.IsNullOrEmpty(pair[1]))
                    {
                        if (IsPoisonedEntry(pair[0], pair[1]))
                        {
                            scrubbed++;
                            continue;
                        }
                        _entries[pair[0]] = (pair[1], ++_tick);
                    }
                }
                catch (JsonException)
                {
                    scrubbed++;
                }
            }

            _scrubbedOnLoad = scrubbed;
            EvictIfNeeded();
            if (scrubbed > 0 || _fileLines > _entries.Count + (_limit / 2))
                Compact();
        }
        catch { }
    }

    private void EvictIfNeeded()
    {
        if (_entries.Count <= _limit) return;
        int toDrop = _entries.Count - _limit + (_limit / 10);
        foreach (var k in _entries.OrderBy(kv => kv.Value.Tick).Take(toDrop).Select(kv => kv.Key).ToList())
            _entries.Remove(k);
    }

    private void AppendLine(string key, string value)
    {
        if (_filePath is null) return;
        if (_fileLines >= _limit * 2)
        {
            Compact();
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.AppendAllText(_filePath, JsonSerializer.Serialize(new[] { key, value }) + "\n", Utf8NoBom);
            _fileLines++;
        }
        catch { }
    }

    private void Compact()
    {
        if (_filePath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var sb = new StringBuilder();
            foreach (var kv in _entries.OrderBy(x => x.Value.Tick))
                sb.Append(JsonSerializer.Serialize(new[] { kv.Key, kv.Value.Value })).Append('\n');

            string tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Utf8NoBom);
            File.Move(tmp, _filePath, overwrite: true);
            _fileLines = _entries.Count;
        }
        catch { }
    }
}
