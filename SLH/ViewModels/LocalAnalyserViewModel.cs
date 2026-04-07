using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private readonly CharacterTagStore _characterTags;
    private readonly LocalChatLogWatcher _watcher;
    private readonly Dictionary<string, PilotRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pilotOrder = new();
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
    [ObservableProperty] private bool _showLocalEmptyPlaceholder = true;

    private readonly DispatcherTimer _activityUtcTimer;

    public LocalAnalyserViewModel(
        EveConnectionService eve,
        ContactStandingIndex contactStandings,
        ISettingsStore settings,
        HeaderState header,
        ZkillClient zkill,
        EnrichmentDiskCache diskCache,
        CharacterTagStore characterTags,
        LocalChatLogWatcher watcher)
    {
        _eve = eve;
        _contactStandings = contactStandings;
        _settings = settings;
        _header = header;
        _zkill = zkill;
        _diskCache = diskCache;
        _characterTags = characterTags;
        _watcher = watcher;
        _watcher.NewLines += OnLogLines;

        _activityUtcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _activityUtcTimer.Tick += OnActivityUtcTick;

        Pilots.CollectionChanged += OnPilotsCollectionChanged;
    }

    public void SyncPilotTagsFromStore(PilotRowViewModel row)
    {
        if (row.CharacterId is not { } id || id <= 0)
        {
            row.SetCustomTags(new HashSet<string>());
            return;
        }

        row.SetCustomTags(_characterTags.GetTags(id));
    }

    public bool PilotHasTag(PilotRowViewModel? row, string tagId)
    {
        if (row?.CharacterId is not { } id || id <= 0 || !CharacterTagIds.IsKnown(tagId))
            return false;
        return _characterTags.HasTag(id, tagId);
    }

    public void SetPilotTag(PilotRowViewModel row, string tagId, bool enabled)
    {
        if (row.CharacterId is not { } id || id <= 0 || !CharacterTagIds.IsKnown(tagId))
            return;
        _characterTags.SetTag(id, tagId, enabled);
        row.SetCustomTags(_characterTags.GetTags(id));
    }

    private void OnPilotsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        ShowLocalEmptyPlaceholder = Pilots.Count == 0;

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
        RebuildVisiblePilotsList();
    }

    /// <summary>Re-applies the +5/+10 hide filter after standings refresh or settings change.</summary>
    public void RebuildVisiblePilotsList()
    {
        Pilots.Clear();
        foreach (var name in _pilotOrder)
        {
            if (!_rows.TryGetValue(name, out var row))
                continue;
            if (ShouldHidePilot(row))
                continue;
            Pilots.Add(row);
        }

        if (SelectedPilot != null && !Pilots.Contains(SelectedPilot))
            SelectedPilot = null;
        UpdateHeaderCount();
    }

    private bool ShouldHidePilot(PilotRowViewModel row)
    {
        if (_settings.Load().FilterOutStandingPlus5Or10 is false)
            return false;
        return row.EffectiveStanding is >= 5f;
    }

    /// <summary>
    /// When corporation id is unknown, only character-level contacts (+ logged-in self) apply.
    /// When corp (and optional alliance) are known from in-memory maps or disk cache, full effective standing is used.
    /// </summary>
    private float ComputeEffectiveStandingForPilot(long characterId,
        IReadOnlyDictionary<long, long>? corpByChar,
        IReadOnlyDictionary<long, long>? allianceByChar)
    {
        long corpId = 0;
        long? allianceId = null;

        if (corpByChar != null && corpByChar.TryGetValue(characterId, out var c) && c > 0)
        {
            corpId = c;
            if (allianceByChar != null && allianceByChar.TryGetValue(characterId, out var a) && a > 0)
                allianceId = a;
        }
        else if (_diskCache.TryGetAffiliation(characterId, out var dc, out var dall) && dc > 0)
        {
            corpId = dc;
            if (dall is { } da && da > 0)
                allianceId = da;
        }
        else
            return _contactStandings.GetQuickStandingForCharacter(characterId);

        long? al = allianceId is 0 or null ? null : allianceId;
        return _contactStandings.GetEffectiveStanding(characterId, corpId, al);
    }

    private void ApplyStandingsForAllPilotsWithIds(IReadOnlyDictionary<long, long>? corpByChar,
        IReadOnlyDictionary<long, long>? allianceByChar)
    {
        if (_eve.IsAuthenticated)
        {
            foreach (var row in _rows.Values)
            {
                if (row.CharacterId is not { } id)
                    continue;
                row.ApplyStanding(ComputeEffectiveStandingForPilot(id, corpByChar, allianceByChar));
            }
        }
        else
        {
            foreach (var row in _rows.Values)
                row.ClearStandingVisual();
        }
    }

    private void ReapplyStandingsForAllPilotsWithIdsAndRebuild(IReadOnlyDictionary<long, long>? corpByChar,
        IReadOnlyDictionary<long, long>? allianceByChar)
    {
        ApplyStandingsForAllPilotsWithIds(corpByChar, allianceByChar);
        RebuildVisiblePilotsList();
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
        TryApplyDiskCacheStandingsAndRebuild();
        ScheduleEnrich(debounceMs: 0);
    }

    [RelayCommand]
    private void ClearLocal()
    {
        foreach (var row in _rows.Values)
            row.ReleaseResources();
        _rows.Clear();
        _pilotOrder.Clear();
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

            TryApplyDiskCacheStandingsAndRebuild();
            ScheduleEnrich();
        });
    }

    /// <summary>
    /// Uses on-disk character/affiliation/corp cache and in-memory contact standings (no ESI) so +5/+10 filtering
    /// can apply immediately after paste or log lines, before the debounced full enrich run.
    /// </summary>
    private void TryApplyDiskCacheStandingsAndRebuild()
    {
        try
        {
            _eve.InitializeApi();

            foreach (var row in _rows.Values)
            {
                if (row.CharacterId is > 0)
                    continue;
                if (!_diskCache.TryGetCharacterId(row.Name, out var cachedId))
                    continue;
                row.CharacterId = cachedId;
                row.PortraitUrl = $"https://images.evetech.net/characters/{cachedId}/portrait?tenant=tranquility&size=64";
                SyncPilotTagsFromStore(row);
            }

            var ids = _rows.Values.Where(r => r.CharacterId is > 0).Select(r => r.CharacterId!.Value).Distinct().ToList();
            var corpByChar = new Dictionary<long, long>();
            var allianceByChar = new Dictionary<long, long>();
            foreach (var id in ids)
            {
                if (!_diskCache.TryGetAffiliation(id, out var cCorp, out var cAlliance))
                    continue;
                corpByChar[id] = cCorp;
                if (cAlliance is { } ca && ca > 0)
                    allianceByChar[id] = ca;
            }

            foreach (var row in _rows.Values)
            {
                if (row.CharacterId is not { } charId)
                    continue;
                if (!corpByChar.TryGetValue(charId, out var coid))
                    continue;
                if (_diskCache.TryGetCorporation(coid, out var tick, out var corpNm) && !string.IsNullOrWhiteSpace(tick))
                {
                    row.CorpTicker = tick;
                    row.CorpName = corpNm ?? "";
                    if (allianceByChar.TryGetValue(charId, out var alId) && alId > 0 &&
                        _diskCache.TryGetAlliance(alId, out var aTick, out var allyNm) && !string.IsNullOrWhiteSpace(aTick))
                    {
                        row.AllianceTicker = aTick;
                        row.AllianceName = allyNm ?? "";
                    }
                    else
                    {
                        row.AllianceTicker = "";
                        row.AllianceName = "";
                    }

                    UpdatePilotSubtitle(row);
                }
            }

            ApplyStandingsForAllPilotsWithIds(corpByChar, allianceByChar);
        }
        catch
        {
            // ESI init / disk — leave rows as-is
        }

        RebuildVisiblePilotsList();
    }

    private void AddOrKeepPilot(string name)
    {
        if (_rows.ContainsKey(name))
            return;
        var row = new PilotRowViewModel { Name = name, Subtitle = name };
        row.ShowThreatPendingPlaceholder = _settings.Load().EnableZkillIntel;
        _rows[name] = row;
        _pilotOrder.Add(name);
        if (!ShouldHidePilot(row))
            Pilots.Add(row);
    }

    private static void UpdatePilotSubtitle(PilotRowViewModel row)
    {
        var tail = "";
        if (!string.IsNullOrWhiteSpace(row.CorpTicker))
            tail += $" [{row.CorpTicker}]";
        if (!string.IsNullOrWhiteSpace(row.AllianceTicker))
            tail += $" [{row.AllianceTicker}]";
        row.Subtitle = tail.Length == 0 ? row.Name : $"{row.Name}{tail}";
    }

    private void RemovePilot(string name)
    {
        if (!_rows.TryGetValue(name, out var row))
            return;
        row.ReleaseResources();
        _rows.Remove(name);
        _pilotOrder.Remove(name);
        Pilots.Remove(row);
        if (SelectedPilot == row)
            SelectedPilot = null;
        UpdateHeaderCount();
    }

    private void ScheduleEnrich(int debounceMs = 600)
    {
        _enrichDebounce?.Cancel();
        _enrichDebounce?.Dispose();
        _enrichDebounce = new CancellationTokenSource();
        var token = _enrichDebounce.Token;
        debounceMs = Math.Clamp(debounceMs, 0, 60_000);
        _ = Task.Run(async () =>
        {
            try
            {
                if (debounceMs > 0)
                    await Task.Delay(debounceMs, token).ConfigureAwait(false);
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
                SyncPilotTagsFromStore(row);
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
                        SyncPilotTagsFromStore(row);
                    }
                }
            }

            var ids = _rows.Values.Where(r => r.CharacterId is > 0).Select(r => r.CharacterId!.Value).Distinct().ToList();
            if (ids.Count == 0)
            {
                foreach (var row in _rows.Values)
                    row.ClearStandingVisual();
                await Dispatcher.UIThread.InvokeAsync(RebuildVisiblePilotsList);
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

            if (_eve.IsAuthenticated)
            {
                try
                {
                    await _contactStandings.EnsureFreshAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // ESI or token — fall back until next refresh
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
                ReapplyStandingsForAllPilotsWithIdsAndRebuild(corpByChar, allianceByChar));

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

            var allianceIds = allianceByChar.Values.Where(a => a > 0).Distinct().ToList();
            var allianceTicker = new Dictionary<long, string>();
            foreach (var aid in allianceIds)
            {
                if (_diskCache.TryGetAlliance(aid, out var cachedAt, out _) && !string.IsNullOrWhiteSpace(cachedAt))
                {
                    allianceTicker[aid] = cachedAt;
                    continue;
                }

                try
                {
                    var all = await _eve.Api.Alliance.GetAllianceInfoAsync(aid).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (all.Model?.Ticker != null)
                    {
                        allianceTicker[aid] = all.Model.Ticker;
                        _diskCache.RememberAlliance(aid, all.Model.Ticker, all.Model.Name);
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
                    _diskCache.TryGetCorporation(coid, out _, out var cn);
                    row.CorpName = cn ?? "";
                }
                else
                {
                    row.CorpTicker = "";
                    row.CorpName = "";
                }

                if (allianceByChar.TryGetValue(charId, out var alId) && alId > 0 && allianceTicker.TryGetValue(alId, out var atick))
                {
                    row.AllianceTicker = atick;
                    _diskCache.TryGetAlliance(alId, out _, out var an);
                    row.AllianceName = an ?? "";
                }
                else
                {
                    row.AllianceTicker = "";
                    row.AllianceName = "";
                }

                UpdatePilotSubtitle(row);
            }

            var zkillTargets = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ReapplyStandingsForAllPilotsWithIdsAndRebuild(corpByChar, allianceByChar);
                if (!_settings.Load().EnableZkillIntel)
                {
                    foreach (var r in _rows.Values)
                        r.ShowThreatPendingPlaceholder = false;
                    return (IReadOnlyList<PilotRowViewModel>)Array.Empty<PilotRowViewModel>();
                }

                return _rows.Values
                    .Where(r => r.CharacterId is > 0 && !ShouldHidePilot(r))
                    .ToList();
            });

            foreach (var row in zkillTargets)
                await EnrichPilotAsync(row, cancellationToken).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(RebuildVisiblePilotsList);
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
            row.ShowThreatPendingPlaceholder = false;
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
        row.ShowThreatPendingPlaceholder = false;
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

            if (needZkill && !ShouldHidePilot(row))
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
        var corpDisplayName = "";
        if (_diskCache.TryGetCorporation(corpId, out var cachedTicker, out var cachedCorpName) &&
            !string.IsNullOrWhiteSpace(cachedTicker))
        {
            ticker = cachedTicker;
            corpDisplayName = cachedCorpName ?? "";
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
                    corpDisplayName = corp.Model.Name ?? "";
                    _diskCache.RememberCorporation(corpId, corp.Model.Ticker, corp.Model.Name);
                }
            }
            catch
            {
                // ignore
            }
        }

        var allyTicker = "";
        var allyDisplayName = "";
        if (allianceId is { } aid && aid > 0)
        {
            if (_diskCache.TryGetAlliance(aid, out var cachedAlly, out var cachedAllyName) &&
                !string.IsNullOrWhiteSpace(cachedAlly))
            {
                allyTicker = cachedAlly;
                allyDisplayName = cachedAllyName ?? "";
            }
            else
            {
                try
                {
                    var all = await _eve.Api.Alliance.GetAllianceInfoAsync(aid).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (all.Model?.Ticker != null)
                    {
                        allyTicker = all.Model.Ticker;
                        allyDisplayName = all.Model.Name ?? "";
                        _diskCache.RememberAlliance(aid, all.Model.Ticker, all.Model.Name);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(SelectedPilot, row))
                return;

            row.CorpTicker = ticker;
            row.CorpName = corpDisplayName;
            row.AllianceTicker = allyTicker;
            row.AllianceName = allyDisplayName;
            UpdatePilotSubtitle(row);

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

        if (!cancellationToken.IsCancellationRequested)
            await Dispatcher.UIThread.InvokeAsync(RebuildVisiblePilotsList);
    }

    public void Dispose()
    {
        Pilots.CollectionChanged -= OnPilotsCollectionChanged;
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
