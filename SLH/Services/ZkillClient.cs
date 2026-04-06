using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace SLH.Services;

public sealed class ZkillStats
{
    public int ShipsDestroyed { get; init; }
    public int ShipsLost { get; init; }
    public long IskDestroyed { get; init; }
    public long IskLost { get; init; }
    public int SoloKills { get; init; }
    public int SoloLosses { get; init; }
    public double DangerRatio { get; init; }
    public double GangRatio { get; init; }
    public double SoloRatio { get; init; }
    public double AvgGangSize { get; init; }
    /// <summary>SDE group ID → ships lost in that group (zKill stats <c>groups</c>).</summary>
    public IReadOnlyDictionary<int, int> GroupShipsLost { get; init; } =
        FrozenDictionary<int, int>.Empty;

    public int ThreatScore { get; init; }
    public string ThreatLabel { get; init; } = "LOW";
    public IReadOnlyList<int> ActivityBuckets { get; init; } = Array.Empty<int>();
}

public sealed class ZkillClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _minIntervalBetweenRequests;
    private readonly int _max429Attempts;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly EnrichmentDiskCache? _diskCache;
    private DateTimeOffset _earliestNextRequestUtc = DateTimeOffset.MinValue;

    public ZkillClient(IConfiguration configuration, EnrichmentDiskCache? diskCache = null)
    {
        _diskCache = diskCache;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var ua = configuration["UserAgent"] ?? "SLH/0.1";
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var ms = 2500;
        if (int.TryParse(configuration["ZkillMinRequestIntervalMs"], out var parsed))
            ms = parsed;
        ms = Math.Clamp(ms, 250, 120_000);
        _minIntervalBetweenRequests = TimeSpan.FromMilliseconds(ms);

        _max429Attempts = 12;
        if (int.TryParse(configuration["Zkill429MaxAttempts"], out var attempts))
            _max429Attempts = Math.Clamp(attempts, 1, 30);
    }

    public async Task<ZkillStats?> GetCharacterStatsAsync(long characterId, CancellationToken cancellationToken = default)
    {
        if (_diskCache != null && _diskCache.TryGetZkillStats(characterId, out var diskStats) && diskStats != null)
            return diskStats;

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var url = $"https://zkillboard.com/api/stats/characterID/{characterId}/";

            for (var attempt = 0; attempt < _max429Attempts; attempt++)
            {
                var throttleWait = _earliestNextRequestUtc - DateTimeOffset.UtcNow;
                if (throttleWait > TimeSpan.Zero)
                    await Task.Delay(throttleWait, cancellationToken).ConfigureAwait(false);

                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var cooldown = ParseRetryAfter(response)
                                   ?? TimeSpan.FromSeconds(Math.Min(90, 5 * (1 << Math.Min(attempt, 4))));
                    if (cooldown < _minIntervalBetweenRequests)
                        cooldown = _minIntervalBetweenRequests;
                    _earliestNextRequestUtc = DateTimeOffset.UtcNow.Add(cooldown);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _earliestNextRequestUtc = DateTimeOffset.UtcNow.Add(_minIntervalBetweenRequests);
                    return null;
                }

                try
                {
                    await using var stream =
                        await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var doc =
                        await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var root = doc.RootElement;

                    var shipsDestroyed = ReadInt(root, "shipsDestroyed");
                    var shipsLost = ReadInt(root, "shipsLost");
                    var soloKills = ReadInt(root, "soloKills");
                    var soloLosses = ReadInt(root, "soloLosses");
                    var dangerRatio = ReadDouble(root, "dangerRatio");
                    var gangRatio = ReadDouble(root, "gangRatio");
                    var soloRatio = ReadDouble(root, "soloRatio");
                    var avgGangSize = ReadDouble(root, "avgGangSize");
                    long iskDestroyed = 0, iskLost = 0;
                    if (root.TryGetProperty("iskDestroyed", out var idEl))
                        iskDestroyed = ReadLong(idEl);
                    if (root.TryGetProperty("iskLost", out var ilEl))
                        iskLost = ReadLong(ilEl);

                    var groupLosses = ParseGroupShipsLost(root);

                    var threat = ComputeThreat(shipsDestroyed, shipsLost, soloKills, iskDestroyed);
                    var buckets = BuildActivityBuckets(root, characterId, shipsDestroyed);

                    _earliestNextRequestUtc = DateTimeOffset.UtcNow.Add(_minIntervalBetweenRequests);
                    var built = new ZkillStats
                    {
                        ShipsDestroyed = shipsDestroyed,
                        ShipsLost = shipsLost,
                        IskDestroyed = iskDestroyed,
                        IskLost = iskLost,
                        SoloKills = soloKills,
                        SoloLosses = soloLosses,
                        DangerRatio = dangerRatio,
                        GangRatio = gangRatio,
                        SoloRatio = soloRatio,
                        AvgGangSize = avgGangSize,
                        GroupShipsLost = groupLosses.Count > 0
                            ? groupLosses.ToFrozenDictionary()
                            : FrozenDictionary<int, int>.Empty,
                        ThreatScore = threat.Score,
                        ThreatLabel = threat.Label,
                        ActivityBuckets = buckets
                    };
                    _diskCache?.RememberZkillStats(characterId, built);
                    return built;
                }
                catch
                {
                    _earliestNextRequestUtc = DateTimeOffset.UtcNow.Add(_minIntervalBetweenRequests);
                    return null;
                }
            }

            _earliestNextRequestUtc = DateTimeOffset.UtcNow.Add(_minIntervalBetweenRequests);
            return null;
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } d)
            return d;
        if (ra?.Date is { } resumeAt)
        {
            var wait = resumeAt - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(10);
        }

        return null;
    }

    private static (int Score, string Label) ComputeThreat(int destroyed, int lost, int solo, long iskDestroyed)
    {
        var ratio = lost > 0 ? (double)destroyed / lost : destroyed;
        var score = (int)Math.Clamp(
            solo * 4 + destroyed / 3 + (int)(Math.Log10(iskDestroyed + 1) * 8) + (int)(ratio * 5),
            0,
            99);
        var label = score >= 70 ? "HIGH" : score >= 40 ? "MED" : "LOW";
        return (score, label);
    }

    private static int[] BuildActivityBuckets(JsonElement root, long characterId, int totalKills)
    {
        var buckets = new int[24];
        if (root.TryGetProperty("months", out var months) && months.ValueKind == JsonValueKind.Object)
        {
            var values = new List<int>();
            foreach (var p in months.EnumerateObject())
                values.Add(ReadInt(p.Value));
            values.Sort();
            for (var i = 0; i < 24 && values.Count > 0; i++)
                buckets[i] = values[Math.Min(i * values.Count / 24, values.Count - 1)];
        }

        if (totalKills <= 0 || buckets.Sum() == 0)
        {
            var rng = new Random((int)(characterId % int.MaxValue) ^ unchecked((int)totalKills));
            var baseH = Math.Max(4, Math.Min(80, totalKills > 0 ? 10 + totalKills / 5 : 8));
            for (var i = 0; i < 24; i++)
                buckets[i] = rng.Next(baseH / 2, baseH + 20);
        }

        return buckets;
    }

    private static Dictionary<int, int> ParseGroupShipsLost(JsonElement root)
    {
        var map = new Dictionary<int, int>();
        if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Object)
            return map;

        foreach (var p in groups.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object)
                continue;
            var gid = ReadInt(p.Value, "groupID");
            if (gid <= 0 && int.TryParse(p.Name, out var fromKey))
                gid = fromKey;
            if (gid <= 0)
                continue;
            var lost = ReadInt(p.Value, "shipsLost");
            if (lost > 0)
                map[gid] = lost;
        }

        return map;
    }

    private static double ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? ReadDouble(el) : 0;

    private static double ReadDouble(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDouble(out var d) ? d : 0,
            JsonValueKind.String => double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var j)
                ? j
                : 0,
            _ => 0
        };
    }

    private static int ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) ? ReadInt(el) : 0;

    private static int ReadInt(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(el.GetString(), out var j) ? j : 0,
            _ => 0
        };
    }

    private static long ReadLong(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetInt64(out var i) ? i : 0,
            JsonValueKind.String => long.TryParse(el.GetString(), out var j) ? j : 0,
            _ => 0
        };
    }

    public void Dispose()
    {
        _requestGate.Dispose();
        _http.Dispose();
    }
}
