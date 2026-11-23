// Copyright (c) 2026 ESSVB. All Rights Reserved.

using System.Text.Json;

namespace EssvbSub.Core.Storage;

/// <summary>
/// Hot-reloads optional user-defined terminology glossaries from %LOCALAPPDATA%\ESSVB-sub\glossaries\*.json.
/// </summary>
public sealed class UserGlossaryStore
{
    private const int ReloadIntervalMs = 5000;
    public static UserGlossaryStore Default { get; } = new(EssvbPaths.GlossariesDir);

    private readonly string _directory;
    private readonly object _gate = new();
    private Dictionary<string, string> _terms = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _keep = [];
    private long _nextCheckTick;

    public UserGlossaryStore(string directory)
    {
        _directory = directory;
    }

    public (IReadOnlyDictionary<string, string> Terms, IReadOnlyList<string> KeepNames) GetSnapshot()
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            if (now >= _nextCheckTick)
            {
                _nextCheckTick = now + ReloadIntervalMs;
                ReloadFromDisk();
            }
            return (_terms, _keep);
        }
    }

    private void ReloadFromDisk()
    {
        var mergedTerms = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mergedKeep = new List<string>();
        var keepSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!Directory.Exists(_directory))
            {
                _terms = mergedTerms;
                _keep = mergedKeep;
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;

                    if (root.TryGetProperty("terms", out var termsObj) && termsObj.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in termsObj.EnumerateObject())
                        {
                            string? val = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(prop.Name) && !string.IsNullOrWhiteSpace(val))
                                mergedTerms[prop.Name.Trim()] = val.Trim();
                        }
                    }

                    if (root.TryGetProperty("keep", out var keepArr) && keepArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in keepArr.EnumerateArray())
                        {
                            string? val = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(val) && keepSeen.Add(val.Trim()))
                                mergedKeep.Add(val.Trim());
                        }
                    }
                }
                catch { }
            }
        }
        catch { }

        _terms = mergedTerms;
        _keep = mergedKeep;
    }
}
