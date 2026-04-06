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
    /// <summary>Bar heights (px) for the 24h strip, derived from <see cref="ActivityHourCounts"/>.</summary>
    public IReadOnlyList<int> ActivityBuckets { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Per-hour UTC kill counts from zKill <c>activity</c> (summed across weekdays). Length 24 when present.
    /// </summary>
    public IReadOnlyList<int> ActivityHourCounts { get; init; } = Array.Empty<int>();

    /// <summary>zKill <c>activity.max</c> (peak count in any single day/hour cell).</summary>
    public int ActivityGridMax { get; init; }
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
                    var (hourCounts, gridMax, buckets) = BuildActivityFromZkill(root);

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
                        ActivityBuckets = buckets,
                        ActivityHourCounts = hourCounts,
                        ActivityGridMax = gridMax
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

    /// <summary>
    /// Parses zKill <c>activity</c>: weekday index (0=Sun … 6=Sat) → hour (0–23 UTC) → kill count.
    /// Aggregates to 24 hourly totals and scales bar heights for the UI.
    /// </summary>
    private static (int[] HourCounts, int GridMax, int[] BarHeights) BuildActivityFromZkill(JsonElement root)
    {
        var hourly = new int[24];
        var gridMax = 0;

        if (root.TryGetProperty("activity", out var act) && act.ValueKind == JsonValueKind.Object)
        {
            gridMax = ReadInt(act, "max");
            foreach (var dayProp in act.EnumerateObject())
            {
                if (string.Equals(dayProp.Name, "max", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(dayProp.Name, "days", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (dayProp.Value.ValueKind != JsonValueKind.Object)
                    continue;
                if (!int.TryParse(dayProp.Name, out var dayIdx) || dayIdx is < 0 or > 6)
                    continue;

                foreach (var hourProp in dayProp.Value.EnumerateObject())
                {
                    if (!int.TryParse(hourProp.Name, out var hour) || hour is < 0 or > 23)
                        continue;
                    hourly[hour] += ReadInt(hourProp.Value);
                }
            }
        }

        var peakHourly = 0;
        for (var i = 0; i < 24; i++)
        {
            if (hourly[i] > peakHourly)
                peakHourly = hourly[i];
        }

        var barHeights = new int[24];
        if (peakHourly <= 0)
        {
            for (var i = 0; i < 24; i++)
                barHeights[i] = 4;
            return (hourly, gridMax, barHeights);
        }

        for (var i = 0; i < 24; i++)
            barHeights[i] = Math.Max(2, (int)Math.Round(2 + hourly[i] / (double)peakHourly * 38));

        return (hourly, gridMax, barHeights);
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
