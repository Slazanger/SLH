using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SLH.Services;

/// <summary>
/// Persists ESI/zKill enrichment results under %AppData%\SLH so later sessions avoid repeat network calls.
/// </summary>
public sealed class EnrichmentDiskCache : IDisposable
{
    private const int FileVersion = 1;
    private static readonly TimeSpan ZkillTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan AffiliationTtl = TimeSpan.FromDays(7);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<string, long> _namesToId = new(StringComparer.Ordinal);
    private Dictionary<long, CorpCacheRecord> _corpById = new();
    private Dictionary<long, AllianceCacheRecord> _allianceById = new();
    private Dictionary<long, AffiliationCacheRecord> _affByChar = new();
    private Dictionary<long, ZkillCacheRecord> _zkillByChar = new();
    private Timer? _saveTimer;
    private readonly object _flushSync = new();
    private bool _loaded;

    public EnrichmentDiskCache(string? path = null)
    {
        _path = path ?? AppPaths.EnrichmentCachePath;
    }

    public bool TryGetCharacterId(string name, out long characterId)
    {
        EnsureLoaded();
        characterId = 0;
        var key = NormalizeName(name);
        if (key.Length == 0)
            return false;
        lock (_gate)
            return _namesToId.TryGetValue(key, out characterId) && characterId > 0;
    }

    public void RememberCharacterId(string name, long characterId)
    {
        if (characterId <= 0)
            return;
        var key = NormalizeName(name);
        if (key.Length == 0)
            return;
        lock (_gate)
        {
            _namesToId[key] = characterId;
        }

        ScheduleSave();
    }

    /// <summary>Returns cached ticker (and name when present). Name may be empty if only ticker was stored.</summary>
    public bool TryGetCorporation(long corporationId, out string ticker, out string name)
    {
        EnsureLoaded();
        ticker = "";
        name = "";
        if (corporationId <= 0)
            return false;
        lock (_gate)
        {
            if (!_corpById.TryGetValue(corporationId, out var rec))
                return false;
            ticker = rec.Ticker ?? "";
            name = rec.Name ?? "";
            return ticker.Length > 0;
        }
    }

    public void RememberCorporation(long corporationId, string ticker, string? name = null)
    {
        if (corporationId <= 0 || string.IsNullOrWhiteSpace(ticker))
            return;
        lock (_gate)
        {
            if (_corpById.TryGetValue(corporationId, out var existing))
            {
                var mergedName = string.IsNullOrWhiteSpace(name) ? existing.Name : name;
                _corpById[corporationId] = new CorpCacheRecord { Ticker = ticker, Name = mergedName ?? "" };
            }
            else
            {
                _corpById[corporationId] = new CorpCacheRecord { Ticker = ticker, Name = name ?? "" };
            }
        }

        ScheduleSave();
    }

    /// <summary>Returns cached alliance ticker (and name when present).</summary>
    public bool TryGetAlliance(long allianceId, out string ticker, out string name)
    {
        EnsureLoaded();
        ticker = "";
        name = "";
        if (allianceId <= 0)
            return false;
        lock (_gate)
        {
            if (!_allianceById.TryGetValue(allianceId, out var rec))
                return false;
            ticker = rec.Ticker ?? "";
            name = rec.Name ?? "";
            return ticker.Length > 0;
        }
    }

    public void RememberAlliance(long allianceId, string ticker, string? name = null)
    {
        if (allianceId <= 0 || string.IsNullOrWhiteSpace(ticker))
            return;
        lock (_gate)
        {
            if (_allianceById.TryGetValue(allianceId, out var existing))
            {
                var mergedName = string.IsNullOrWhiteSpace(name) ? existing.Name : name;
                _allianceById[allianceId] = new AllianceCacheRecord { Ticker = ticker, Name = mergedName ?? "" };
            }
            else
            {
                _allianceById[allianceId] = new AllianceCacheRecord { Ticker = ticker, Name = name ?? "" };
            }
        }

        ScheduleSave();
    }

    public bool TryGetAffiliation(long characterId, out long corporationId, out long? allianceId)
    {
        EnsureLoaded();
        corporationId = 0;
        allianceId = null;
        if (characterId <= 0)
            return false;
        lock (_gate)
        {
            if (!_affByChar.TryGetValue(characterId, out var rec))
                return false;
            if (DateTimeOffset.UtcNow - rec.CachedAt > AffiliationTtl)
                return false;
            corporationId = rec.CorporationId;
            allianceId = rec.AllianceId is > 0 ? rec.AllianceId : null;
            return corporationId > 0;
        }
    }

    public void RememberAffiliation(long characterId, long corporationId, long? allianceId)
    {
        if (characterId <= 0 || corporationId <= 0)
            return;
        lock (_gate)
        {
            _affByChar[characterId] = new AffiliationCacheRecord
            {
                CachedAt = DateTimeOffset.UtcNow,
                CorporationId = corporationId,
                AllianceId = allianceId is > 0 ? allianceId : null
            };
        }

        ScheduleSave();
    }

    public bool TryGetZkillStats(long characterId, out ZkillStats? stats)
    {
        EnsureLoaded();
        stats = null;
        if (characterId <= 0)
            return false;
        lock (_gate)
        {
            if (!_zkillByChar.TryGetValue(characterId, out var rec))
                return false;
            if (DateTimeOffset.UtcNow - rec.CachedAt > ZkillTtl)
                return false;
            stats = ZkillStatsSerializer.FromDto(rec.Stats);
            return stats != null;
        }
    }

    public void RememberZkillStats(long characterId, ZkillStats stats)
    {
        if (characterId <= 0)
            return;
        var dto = ZkillStatsSerializer.ToDto(stats);
        lock (_gate)
        {
            _zkillByChar[characterId] = new ZkillCacheRecord { CachedAt = DateTimeOffset.UtcNow, Stats = dto };
        }

        ScheduleSave();
    }

    /// <summary>True if there is no zKill disk entry or it is older than <paramref name="maxAge"/>.</summary>
    public bool IsZkillDiskCacheStale(long characterId, TimeSpan maxAge)
    {
        if (characterId <= 0)
            return false;
        EnsureLoaded();
        lock (_gate)
        {
            if (!_zkillByChar.TryGetValue(characterId, out var rec))
                return true;
            return DateTimeOffset.UtcNow - rec.CachedAt > maxAge;
        }
    }

    /// <summary>True if there is no affiliation disk entry or it is older than <paramref name="maxAge"/>.</summary>
    public bool IsAffiliationDiskCacheStale(long characterId, TimeSpan maxAge)
    {
        if (characterId <= 0)
            return false;
        EnsureLoaded();
        lock (_gate)
        {
            if (!_affByChar.TryGetValue(characterId, out var rec))
                return true;
            return DateTimeOffset.UtcNow - rec.CachedAt > maxAge;
        }
    }

    /// <summary>Removes zKill cache for this character so the next fetch goes to the network.</summary>
    public void InvalidateZkillStats(long characterId)
    {
        if (characterId <= 0)
            return;
        lock (_gate)
        {
            _zkillByChar.Remove(characterId);
        }

        ScheduleSave();
    }

    /// <summary>Clears all cached enrichment data from memory and overwrites the cache file on disk.</summary>
    public void ClearAll()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _saveTimer;
            _saveTimer = null;
            _namesToId = new Dictionary<string, long>(StringComparer.Ordinal);
            _corpById.Clear();
            _allianceById.Clear();
            _affByChar.Clear();
            _zkillByChar.Clear();
        }

        timer?.Dispose();
        Flush();
    }

    public void Dispose()
    {
        Timer? timer;
        lock (_gate)
        {
            timer = _saveTimer;
            _saveTimer = null;
        }

        timer?.Dispose();
        Flush();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
            return;
        lock (_gate)
        {
            if (_loaded)
                return;
            LoadUnlocked();
            _loaded = true;
        }
    }

    private void LoadUnlocked()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var json = File.ReadAllText(_path);
            var doc = JsonSerializer.Deserialize<EnrichmentCacheFileDto>(json, JsonOptions);
            if (doc?.Version != FileVersion)
                return;
            if (doc.NamesToCharacterId != null)
            {
                foreach (var kv in doc.NamesToCharacterId)
                {
                    var k = NormalizeName(kv.Key);
                    if (k.Length > 0 && kv.Value > 0)
                        _namesToId[k] = kv.Value;
                }
            }

            if (doc.Corporations != null)
            {
                foreach (var kv in doc.Corporations)
                {
                    if (kv.Value == null || kv.Key <= 0)
                        continue;
                    if (!string.IsNullOrWhiteSpace(kv.Value.Ticker))
                        _corpById[kv.Key] = kv.Value;
                }
            }

            if (doc.Alliances != null)
            {
                foreach (var kv in doc.Alliances)
                {
                    if (kv.Value == null || kv.Key <= 0)
                        continue;
                    if (!string.IsNullOrWhiteSpace(kv.Value.Ticker))
                        _allianceById[kv.Key] = kv.Value;
                }
            }

            if (doc.Affiliations != null)
            {
                foreach (var kv in doc.Affiliations)
                {
                    if (kv.Value == null || kv.Key <= 0)
                        continue;
                    _affByChar[kv.Key] = kv.Value;
                }
            }

            if (doc.Zkill != null)
            {
                foreach (var kv in doc.Zkill)
                {
                    if (kv.Value?.Stats == null || kv.Key <= 0)
                        continue;
                    _zkillByChar[kv.Key] = kv.Value;
                }
            }
        }
        catch
        {
            // ignore corrupt cache
        }
    }

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_saveTimer == null)
            {
                _saveTimer = new Timer(
                    _ => Flush(),
                    null,
                    TimeSpan.FromMilliseconds(750),
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _saveTimer.Change(TimeSpan.FromMilliseconds(750), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void Flush()
    {
        EnrichmentCacheFileDto snapshot;
        lock (_gate)
        {
            snapshot = new EnrichmentCacheFileDto
            {
                Version = FileVersion,
                NamesToCharacterId = new Dictionary<string, long>(_namesToId, StringComparer.Ordinal),
                Corporations = new Dictionary<long, CorpCacheRecord>(_corpById),
                Alliances = new Dictionary<long, AllianceCacheRecord>(_allianceById),
                Affiliations = new Dictionary<long, AffiliationCacheRecord>(_affByChar),
                Zkill = new Dictionary<long, ZkillCacheRecord>(_zkillByChar)
            };
        }

        lock (_flushSync)
        {
            try
            {
                Directory.CreateDirectory(AppPaths.AppDataDirectory);
                var tmp = _path + ".tmp";
                var json = JsonSerializer.Serialize(snapshot, JsonOptions);
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(_path + ".tmp");
                }
                catch
                {
                    // ignored
                }
            }
        }
    }

    private static string NormalizeName(string name) => name.Trim().ToLowerInvariant();

    private sealed class EnrichmentCacheFileDto
    {
        public int Version { get; set; }
        public Dictionary<string, long>? NamesToCharacterId { get; set; }
        public Dictionary<long, CorpCacheRecord>? Corporations { get; set; }
        public Dictionary<long, AllianceCacheRecord>? Alliances { get; set; }
        public Dictionary<long, AffiliationCacheRecord>? Affiliations { get; set; }
        public Dictionary<long, ZkillCacheRecord>? Zkill { get; set; }
    }

    private sealed class CorpCacheRecord
    {
        public string Ticker { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class AllianceCacheRecord
    {
        public string Ticker { get; set; } = "";
        public string Name { get; set; } = "";
    }

    private sealed class AffiliationCacheRecord
    {
        public DateTimeOffset CachedAt { get; set; }
        public long CorporationId { get; set; }
        public long? AllianceId { get; set; }
    }

    private sealed class ZkillCacheRecord
    {
        public DateTimeOffset CachedAt { get; set; }
        public ZkillStatsDto Stats { get; set; } = new();
    }

    private sealed class ZkillStatsDto
    {
        public int ShipsDestroyed { get; set; }
        public int ShipsLost { get; set; }
        public long IskDestroyed { get; set; }
        public long IskLost { get; set; }
        public int SoloKills { get; set; }
        public int SoloLosses { get; set; }
        public double DangerRatio { get; set; }
        public double GangRatio { get; set; }
        public double SoloRatio { get; set; }
        public double AvgGangSize { get; set; }
        public Dictionary<int, int>? GroupShipsLost { get; set; }
        public int ThreatScore { get; set; }
        public string ThreatLabel { get; set; } = "LOW";
        public int[]? ActivityBuckets { get; set; }
        public int[]? ActivityHourCounts { get; set; }
        public int ActivityGridMax { get; set; }
        public bool MonitorInTopShips { get; set; }
    }

    private static class ZkillStatsSerializer
    {
        public static ZkillStatsDto ToDto(ZkillStats s)
        {
            Dictionary<int, int>? groups = null;
            if (s.GroupShipsLost.Count > 0)
            {
                groups = new Dictionary<int, int>();
                foreach (var kv in s.GroupShipsLost)
                    groups[kv.Key] = kv.Value;
            }

            return new ZkillStatsDto
            {
                ShipsDestroyed = s.ShipsDestroyed,
                ShipsLost = s.ShipsLost,
                IskDestroyed = s.IskDestroyed,
                IskLost = s.IskLost,
                SoloKills = s.SoloKills,
                SoloLosses = s.SoloLosses,
                DangerRatio = s.DangerRatio,
                GangRatio = s.GangRatio,
                SoloRatio = s.SoloRatio,
                AvgGangSize = s.AvgGangSize,
                GroupShipsLost = groups,
                ThreatScore = s.ThreatScore,
                ThreatLabel = s.ThreatLabel,
                ActivityBuckets = s.ActivityBuckets is int[] arr ? (int[])arr.Clone() : s.ActivityBuckets.ToArray(),
                ActivityHourCounts = s.ActivityHourCounts.Count >= 24
                    ? s.ActivityHourCounts.Take(24).ToArray()
                    : null,
                ActivityGridMax = s.ActivityGridMax,
                MonitorInTopShips = s.MonitorInTopShips
            };
        }

        public static ZkillStats? FromDto(ZkillStatsDto? d)
        {
            if (d == null)
                return null;
            IReadOnlyDictionary<int, int> frozen = FrozenDictionary<int, int>.Empty;
            if (d.GroupShipsLost is { Count: > 0 })
                frozen = d.GroupShipsLost.ToFrozenDictionary();

            var buckets = d.ActivityBuckets is { Length: 24 }
                ? (int[])d.ActivityBuckets.Clone()
                : new int[24];

            var hourCounts = d.ActivityHourCounts is { Length: 24 }
                ? (int[])d.ActivityHourCounts.Clone()
                : new int[24];

            return new ZkillStats
            {
                ShipsDestroyed = d.ShipsDestroyed,
                ShipsLost = d.ShipsLost,
                IskDestroyed = d.IskDestroyed,
                IskLost = d.IskLost,
                SoloKills = d.SoloKills,
                SoloLosses = d.SoloLosses,
                DangerRatio = d.DangerRatio,
                GangRatio = d.GangRatio,
                SoloRatio = d.SoloRatio,
                AvgGangSize = d.AvgGangSize,
                GroupShipsLost = frozen,
                ThreatScore = d.ThreatScore,
                ThreatLabel = string.IsNullOrEmpty(d.ThreatLabel) ? "LOW" : d.ThreatLabel,
                ActivityBuckets = buckets,
                ActivityHourCounts = hourCounts,
                ActivityGridMax = d.ActivityGridMax,
                MonitorInTopShips = d.MonitorInTopShips
            };
        }
    }
}
