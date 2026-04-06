using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EVEStandard.Models;
using SLH.Services;

namespace SLH.ViewModels;

public partial class LocalAnalyserViewModel : ObservableObject, IDisposable
{
    /// <summary>ESI POST /universe/names/ rejects oversized bodies; keep batches conservative.</summary>
    private const int BulkNamesBatchSize = 500;

    /// <summary>ESI POST /characters/affiliation/ accepts at most 1000 character IDs per call.</summary>
    private const int AffiliationBatchSize = 1000;

    private readonly EveConnectionService _eve;
    private readonly ContactStandingIndex _contactStandings;
    private readonly ISettingsStore _settings;
    private readonly HeaderState _header;
    private readonly ZkillClient _zkill;
    private readonly EnrichmentDiskCache _diskCache;
    private readonly LocalChatLogWatcher _watcher;
    private readonly Dictionary<string, PilotRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _enrichGate = new(1, 1);
    private CancellationTokenSource? _enrichDebounce;
    private CancellationTokenSource? _selectionRefreshCts;
    private static readonly TimeSpan SelectedPilotDiskCacheRefreshAge = TimeSpan.FromDays(1);
    private int _lastLocalCount;

    public ObservableCollection<PilotRowViewModel> Pilots { get; } = new();

    public ObservableCollection<ActivityHeatmapCellViewModel> ActivityHeatmap { get; } = new();

    [ObservableProperty] private PilotRowViewModel? _selectedPilot;
    [ObservableProperty] private string _detailNotes = "";
    [ObservableProperty] private string _activityHeatmapUtcLine = "";

    private readonly DispatcherTimer _activityUtcTimer;

    public LocalAnalyserViewModel(
        EveConnectionService eve,
        ContactStandingIndex contactStandings,
        ISettingsStore settings,
        HeaderState header,
        ZkillClient zkill,
        EnrichmentDiskCache diskCache,
        LocalChatLogWatcher watcher)
    {
        _eve = eve;
        _contactStandings = contactStandings;
        _settings = settings;
        _header = header;
        _zkill = zkill;
        _diskCache = diskCache;
        _watcher = watcher;
        _watcher.NewLines += OnLogLines;

        _activityUtcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _activityUtcTimer.Tick += OnActivityUtcTick;
    }

    private void OnActivityUtcTick(object? sender, EventArgs e)
    {
        if (SelectedPilot != null)
            RebuildActivityHeatmap();
    }

    private void RebuildActivityHeatmap()
    {
        var row = SelectedPilot;
        if (row == null)
        {
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            return;
        }

        var counts = row.ActivityHourCounts;
        var bars = row.ActivityBuckets;
        if (counts is not { Length: 24 } || bars is not { Length: 24 })
        {
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            return;
        }

        var utc = DateTime.UtcNow;
        ActivityHeatmapPresenter.Rebuild(ActivityHeatmap, counts, bars, utc);
        ActivityHeatmapUtcLine = ActivityHeatmapPresenter.BuildUtcLine(counts, utc);
    }

    public void ClearPilotStandingVisuals()
    {
        foreach (var row in _rows.Values)
            row.ClearStandingVisual();
    }

    public void RefreshWatcherPath()
    {
        var s = _settings.Load();
        var filter = _eve.CharacterName;
        _watcher.WatchFolder(s.ChatLogsFolder, string.IsNullOrWhiteSpace(filter) ? null : filter);
    }

    /// <summary>Parses pasted local text (names or log lines) and merges into the list, same as the former Apply action.</summary>
    public void ApplyLocalText(string? text)
    {
        foreach (var name in LocalChatParser.ParseNameList(text ?? ""))
            AddOrKeepPilot(name);
        ScheduleEnrich();
        UpdateHeaderCount();
    }

    [RelayCommand]
    private void ClearLocal()
    {
        _rows.Clear();
        Pilots.Clear();
        SelectedPilot = null;
        UpdateHeaderCount();
    }

    private void OnLogLines(object? sender, IReadOnlyList<string> lines)
    {
        _ = Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var line in lines)
            {
                var parsed = LocalChatParser.ParseLine(line);
                if (parsed == null)
                    continue;
                var (name, joined) = parsed.Value;
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                if (joined == true)
                    AddOrKeepPilot(name);
                else if (joined == false)
                    RemovePilot(name);
            }

            ScheduleEnrich();
            UpdateHeaderCount();
        });
    }

    private void AddOrKeepPilot(string name)
    {
        if (_rows.ContainsKey(name))
            return;
        var row = new PilotRowViewModel { Name = name, Subtitle = name };
        _rows[name] = row;
        Pilots.Add(row);
    }

    private void RemovePilot(string name)
    {
        if (!_rows.TryGetValue(name, out var row))
            return;
        _rows.Remove(name);
        Pilots.Remove(row);
        if (SelectedPilot == row)
            SelectedPilot = null;
    }

    private void ScheduleEnrich()
    {
        _enrichDebounce?.Cancel();
        _enrichDebounce?.Dispose();
        _enrichDebounce = new CancellationTokenSource();
        var token = _enrichDebounce.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(600, token).ConfigureAwait(false);
                await EnrichPendingAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // ignored
            }
        });
    }

    private async Task EnrichPendingAsync(CancellationToken cancellationToken)
    {
        await _enrichGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _eve.InitializeApi();
            var pendingNames = _rows.Values.Where(r => r.CharacterId is null or 0).Select(r => r.Name).Distinct().ToList();
            foreach (var nm in pendingNames)
            {
                if (!_diskCache.TryGetCharacterId(nm, out var cachedId))
                    continue;
                if (!_rows.TryGetValue(nm, out var row))
                    continue;
                row.CharacterId = cachedId;
                row.PortraitUrl = $"https://images.evetech.net/characters/{cachedId}/portrait?tenant=tranquility&size=64";
            }

            var pending = _rows.Values.Where(r => r.CharacterId is null or 0).Select(r => r.Name).Distinct().ToList();
            if (pending.Count > 0)
            {
                for (var offset = 0; offset < pending.Count; offset += BulkNamesBatchSize)
                {
                    var take = Math.Min(BulkNamesBatchSize, pending.Count - offset);
                    var chunk = pending.GetRange(offset, take);
                    var bulk = await _eve.Api.Universe.BulkNamesToIdsAsync(chunk).WaitAsync(cancellationToken).ConfigureAwait(false);
                    var model = bulk.Model;
                    if (model?.Characters == null)
                        continue;
                    foreach (var c in model.Characters)
                    {
                        if (!_rows.TryGetValue(c.Name, out var row))
                            continue;
                        row.CharacterId = c.Id;
                        row.PortraitUrl = $"https://images.evetech.net/characters/{c.Id}/portrait?tenant=tranquility&size=64";
                        _diskCache.RememberCharacterId(c.Name, c.Id);
                    }
                }
            }

            var ids = _rows.Values.Where(r => r.CharacterId is > 0).Select(r => r.CharacterId!.Value).Distinct().ToList();
            if (ids.Count == 0)
            {
                foreach (var row in _rows.Values)
                    row.ClearStandingVisual();
                return;
            }

            var corpByChar = new Dictionary<long, long>();
            var allianceByChar = new Dictionary<long, long>();
            var needAffiliation = new List<long>();
            foreach (var id in ids)
            {
                if (_diskCache.TryGetAffiliation(id, out var cCorp, out var cAlliance))
                {
                    corpByChar[id] = cCorp;
                    if (cAlliance is { } ca && ca > 0)
                        allianceByChar[id] = ca;
                }
                else
                {
                    needAffiliation.Add(id);
                }
            }

            for (var offset = 0; offset < needAffiliation.Count; offset += AffiliationBatchSize)
            {
                var take = Math.Min(AffiliationBatchSize, needAffiliation.Count - offset);
                var chunk = needAffiliation.GetRange(offset, take);
                var aff = await _eve.Api.Character.AffiliationAsync(chunk).WaitAsync(cancellationToken).ConfigureAwait(false);
                var affList = aff.Model;
                if (affList == null)
                    continue;
                foreach (var a in affList)
                {
                    corpByChar[a.CharacterId] = a.CorporationId;
                    if (a.AllianceId is { } aid && aid > 0)
                        allianceByChar[a.CharacterId] = aid;
                    _diskCache.RememberAffiliation(a.CharacterId, a.CorporationId, a.AllianceId);
                }
            }

            var corpIds = corpByChar.Values.Distinct().ToList();
            var corpTicker = new Dictionary<long, string>();
            foreach (var cid in corpIds)
            {
                if (_diskCache.TryGetCorporation(cid, out var cachedTicker, out _))
                {
                    corpTicker[cid] = cachedTicker;
                    continue;
                }

                try
                {
                    var corp = await _eve.Api.Corporation.GetCorporationInfoAsync(cid).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (corp.Model?.Ticker != null)
                    {
                        corpTicker[cid] = corp.Model.Ticker;
                        _diskCache.RememberCorporation(cid, corp.Model.Ticker, corp.Model.Name);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            foreach (var row in _rows.Values)
            {
                if (row.CharacterId is not { } charId)
                    continue;
                if (!corpByChar.TryGetValue(charId, out var coid))
                    continue;
                if (corpTicker.TryGetValue(coid, out var tick))
                {
                    row.CorpTicker = tick;
                    row.Subtitle = $"{row.Name} [{tick}]";
                }
                else
                    row.Subtitle = row.Name;
            }

            if (_eve.IsAuthenticated)
            {
                try
                {
                    await _contactStandings.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // ESI or token — fall back to neutral until next refresh
                }

                foreach (var row in _rows.Values)
                {
                    if (row.CharacterId is not { } charIdForStand)
                    {
                        row.ClearStandingVisual();
                        continue;
                    }

                    if (!corpByChar.TryGetValue(charIdForStand, out var pilotCorpId))
                    {
                        row.ClearStandingVisual();
                        continue;
                    }

                    long? pilotAlliance = allianceByChar.TryGetValue(charIdForStand, out var pa) ? pa : null;
                    if (pilotAlliance is 0 or null)
                        pilotAlliance = null;

                    var effective = _contactStandings.GetEffectiveStanding(charIdForStand, pilotCorpId, pilotAlliance);
                    row.ApplyStanding(effective);
                }
            }
            else
            {
                foreach (var row in _rows.Values)
                    row.ClearStandingVisual();
            }

            if (_settings.Load().EnableZkillIntel)
            {
                foreach (var row in _rows.Values.Where(r => r.CharacterId is > 0))
                    await EnrichPilotAsync(row, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _enrichGate.Release();
        }
    }

    private async Task EnrichPilotAsync(PilotRowViewModel row, CancellationToken cancellationToken)
    {
        if (row.CharacterId is not { } id || id <= 0)
            return;
        if (!_settings.Load().EnableZkillIntel)
            return;

        var stats = await _zkill.GetCharacterStatsAsync(id, cancellationToken).ConfigureAwait(false);
        if (stats == null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ClearZkillRowFields(row);
                if (ReferenceEquals(SelectedPilot, row))
                    RebuildActivityHeatmap();
            });
            return;
        }

        var cyno = ZkillIntelHeuristics.BuildCynoHint(stats);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            row.ThreatScore = stats.ThreatScore;
            row.ShipsDestroyed = stats.ShipsDestroyed;
            row.ShipsLost = stats.ShipsLost;
            row.IskDestroyed = stats.IskDestroyed;
            row.IskLost = stats.IskLost;
            row.IsFriendly = stats.ThreatScore < 15;
            row.ActivityRegion = "Recent activity (zKill aggregates)";
            row.IntelTip = stats.SoloKills > 10
                ? "TIP: High solo activity on zKill — expect aggressive solo pilots."
                : "TIP: Review loss patterns on zKill for ship preferences.";
            row.ActivityBuckets = stats.ActivityBuckets.ToArray();
            row.ShipsHint = $"Ships destroyed / lost: {stats.ShipsDestroyed} / {stats.ShipsLost} (zKill)";
            row.ZkillSoloKills = stats.SoloKills;
            row.ZkillSoloLosses = stats.SoloLosses;
            row.ZkillRatiosLine = ZkillIntelHeuristics.BuildRatiosLine(stats);
            row.ZkillPvpSummary = ZkillIntelHeuristics.BuildPvpSummary(stats);
            row.ZkillCynoHint = cyno ?? "";
            row.ActivityHourCounts = CopyActivity24(stats.ActivityHourCounts);
            if (ReferenceEquals(SelectedPilot, row))
                RebuildActivityHeatmap();
        });
    }

    private static int[] CopyActivity24(IReadOnlyList<int> src)
    {
        var a = new int[24];
        for (var i = 0; i < 24 && i < src.Count; i++)
            a[i] = src[i];
        return a;
    }

    private static void ClearZkillRowFields(PilotRowViewModel row)
    {
        row.ThreatScore = 0;
        row.ShipsDestroyed = 0;
        row.ShipsLost = 0;
        row.IskDestroyed = 0;
        row.IskLost = 0;
        row.IsFriendly = false;
        row.ActivityRegion = "";
        row.IntelTip = "";
        row.ActivityBuckets = new int[24];
        row.ActivityHourCounts = new int[24];
        row.ShipsHint = "";
        row.ZkillSoloKills = 0;
        row.ZkillSoloLosses = 0;
        row.ZkillRatiosLine = "";
        row.ZkillPvpSummary = "";
        row.ZkillCynoHint = "";
    }

    private void UpdateHeaderCount()
    {
        var n = Pilots.Count;
        var delta = n - _lastLocalCount;
        _lastLocalCount = n;
        var deltaStr = delta == 0 ? "" : delta > 0 ? $"(+{delta})" : $"({delta})";
        _header.LocalLine = string.IsNullOrEmpty(deltaStr) ? $"Local: {n}" : $"Local: {n} {deltaStr}";
    }

    partial void OnSelectedPilotChanged(PilotRowViewModel? value)
    {
        _selectionRefreshCts?.Cancel();
        _selectionRefreshCts?.Dispose();
        _selectionRefreshCts = null;

        if (value == null)
        {
            _activityUtcTimer.Stop();
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            DetailNotes = "";
            return;
        }

        DetailNotes = string.IsNullOrWhiteSpace(value.StandingDisplay)
            ? ""
            : $"Standing (contacts): {value.StandingDisplay}";

        RebuildActivityHeatmap();
        _activityUtcTimer.Start();

        if (value.CharacterId is not > 0)
            return;

        _selectionRefreshCts = new CancellationTokenSource();
        var token = _selectionRefreshCts.Token;
        var row = value;
        _ = Task.Run(() => RefreshSelectedPilotIfDiskStaleAsync(row, token), token);
    }

    private async Task RefreshSelectedPilotIfDiskStaleAsync(PilotRowViewModel row, CancellationToken cancellationToken)
    {
        try
        {
            var id = row.CharacterId;
            if (id is not > 0)
                return;

            var needZkill = _settings.Load().EnableZkillIntel && _diskCache.IsZkillDiskCacheStale(id.Value, SelectedPilotDiskCacheRefreshAge);
            var needAff = _diskCache.IsAffiliationDiskCacheStale(id.Value, SelectedPilotDiskCacheRefreshAge);
            if (!needZkill && !needAff)
                return;

            _eve.InitializeApi();

            if (needAff)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var aff = await _eve.Api.Character.AffiliationAsync(new List<long> { id.Value }).WaitAsync(cancellationToken).ConfigureAwait(false);
                var entry = aff.Model?.FirstOrDefault(a => a.CharacterId == id.Value);
                if (entry != null)
                {
                    long? alliance = entry.AllianceId is { } aid && aid > 0 ? aid : null;
                    _diskCache.RememberAffiliation(id.Value, entry.CorporationId, entry.AllianceId);
                    await ApplyAffiliationToRowAsync(row, id.Value, entry.CorporationId, alliance, cancellationToken).ConfigureAwait(false);
                }
            }

            if (needZkill)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _diskCache.InvalidateZkillStats(id.Value);
                await EnrichPilotAsync(row, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // selection changed or dispose
        }
        catch
        {
            // ESI / zKill — leave existing row data
        }
    }

    private async Task ApplyAffiliationToRowAsync(PilotRowViewModel row, long charId, long corpId, long? allianceId,
        CancellationToken cancellationToken)
    {
        string ticker;
        if (_diskCache.TryGetCorporation(corpId, out var cachedTicker, out _) && !string.IsNullOrWhiteSpace(cachedTicker))
        {
            ticker = cachedTicker;
        }
        else
        {
            ticker = "";
            try
            {
                var corp = await _eve.Api.Corporation.GetCorporationInfoAsync(corpId).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (corp.Model?.Ticker != null)
                {
                    ticker = corp.Model.Ticker;
                    _diskCache.RememberCorporation(corpId, corp.Model.Ticker, corp.Model.Name);
                }
            }
            catch
            {
                // ignore
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(SelectedPilot, row))
                return;

            if (!string.IsNullOrWhiteSpace(ticker))
            {
                row.CorpTicker = ticker;
                row.Subtitle = $"{row.Name} [{ticker}]";
            }
            else
            {
                row.Subtitle = row.Name;
            }

            if (_eve.IsAuthenticated)
            {
                var effective = _contactStandings.GetEffectiveStanding(charId, corpId, allianceId);
                row.ApplyStanding(effective);
                DetailNotes = string.IsNullOrWhiteSpace(row.StandingDisplay)
                    ? ""
                    : $"Standing (contacts): {row.StandingDisplay}";
            }
            else
            {
                row.ClearStandingVisual();
                DetailNotes = "";
            }
        });
    }

    public void Dispose()
    {
        _watcher.NewLines -= OnLogLines;
        _watcher.Dispose();
        _enrichDebounce?.Cancel();
        _enrichDebounce?.Dispose();
        _selectionRefreshCts?.Cancel();
        _selectionRefreshCts?.Dispose();
        _activityUtcTimer.Stop();
        _activityUtcTimer.Tick -= OnActivityUtcTick;
        _enrichGate.Dispose();
    }
}
