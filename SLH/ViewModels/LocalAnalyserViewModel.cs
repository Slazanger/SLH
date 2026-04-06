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
    private readonly LocalChatLogWatcher _watcher;
    private readonly Dictionary<string, PilotRowViewModel> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _enrichGate = new(1, 1);
    private CancellationTokenSource? _enrichDebounce;
    private int _lastLocalCount;

    public ObservableCollection<PilotRowViewModel> Pilots { get; } = new();

    [ObservableProperty] private PilotRowViewModel? _selectedPilot;
    [ObservableProperty] private string _pasteText = "";
    [ObservableProperty] private bool _localThreatsExpanded = true;
    [ObservableProperty] private string _detailNotes = "";

    public LocalAnalyserViewModel(
        EveConnectionService eve,
        ContactStandingIndex contactStandings,
        ISettingsStore settings,
        HeaderState header,
        ZkillClient zkill,
        LocalChatLogWatcher watcher)
    {
        _eve = eve;
        _contactStandings = contactStandings;
        _settings = settings;
        _header = header;
        _zkill = zkill;
        _watcher = watcher;
        _watcher.NewLines += OnLogLines;
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

    [RelayCommand]
    private void ApplyPaste()
    {
        foreach (var name in LocalChatParser.ParseNameList(PasteText))
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
            for (var offset = 0; offset < ids.Count; offset += AffiliationBatchSize)
            {
                var take = Math.Min(AffiliationBatchSize, ids.Count - offset);
                var chunk = ids.GetRange(offset, take);
                var aff = await _eve.Api.Character.AffiliationAsync(chunk).WaitAsync(cancellationToken).ConfigureAwait(false);
                var affList = aff.Model;
                if (affList == null)
                    continue;
                foreach (var a in affList)
                {
                    corpByChar[a.CharacterId] = a.CorporationId;
                    if (a.AllianceId is { } aid && aid > 0)
                        allianceByChar[a.CharacterId] = aid;
                }
            }

            var corpIds = corpByChar.Values.Distinct().ToList();
            var corpTicker = new Dictionary<long, string>();
            foreach (var cid in corpIds)
            {
                try
                {
                    var corp = await _eve.Api.Corporation.GetCorporationInfoAsync(cid).WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (corp.Model?.Ticker != null)
                        corpTicker[cid] = corp.Model.Ticker;
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
            return;

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
        });
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
        if (value == null)
        {
            DetailNotes = "";
            return;
        }

        DetailNotes = string.Join(Environment.NewLine, new[]
        {
            string.IsNullOrEmpty(value.StandingDisplay) ? null : $"Standing (contacts): {value.StandingDisplay}",
            $"Recent kills: {value.ShipsDestroyed} — Recent losses: {value.ShipsLost}",
            value.ShipsHint,
            value.IntelTip
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
    }

    public void Dispose()
    {
        _watcher.NewLines -= OnLogLines;
        _watcher.Dispose();
        _enrichDebounce?.Cancel();
        _enrichDebounce?.Dispose();
        _enrichGate.Dispose();
    }
}
