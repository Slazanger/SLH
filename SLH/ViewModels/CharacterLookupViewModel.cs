using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLH.Services;

namespace SLH.ViewModels;

public partial class CharacterLookupViewModel : ObservableObject, IDisposable, IPilotDetailPanelHost
{
    private readonly EveConnectionService _eve;
    private readonly ZkillClient _zkill;
    private readonly ISettingsStore _settings;
    private readonly EnrichmentDiskCache _diskCache;
    private readonly ShipIconCache _shipIconCache;
    private readonly ShipTypeNameCache _shipTypeNameCache;
    private readonly CharacterTagStore _characterTags;
    private readonly DispatcherTimer _activityUtcTimer;

    public ObservableCollection<ActivityHeatmapCellViewModel> ActivityHeatmap { get; } = new();

    [ObservableProperty] private string _searchName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private PilotRowViewModel? _pilotDetail;
    [ObservableProperty] private string _activityHeatmapUtcLine = "";

    public string DetailNotes => "";

    public bool ShowPilotDetailNotes => false;

    public bool ShowEmptyPilotHint => false;

    public bool ShowStatus => !string.IsNullOrWhiteSpace(Status);

    public bool HasResult => PilotDetail?.CharacterId is > 0;

    public CharacterLookupViewModel(
        EveConnectionService eve,
        ZkillClient zkill,
        ISettingsStore settings,
        EnrichmentDiskCache diskCache,
        ShipIconCache shipIconCache,
        ShipTypeNameCache shipTypeNameCache,
        CharacterTagStore characterTags)
    {
        _eve = eve;
        _zkill = zkill;
        _settings = settings;
        _diskCache = diskCache;
        _shipIconCache = shipIconCache;
        _shipTypeNameCache = shipTypeNameCache;
        _characterTags = characterTags;

        _activityUtcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _activityUtcTimer.Tick += OnActivityUtcTick;
    }

    private void OnActivityUtcTick(object? sender, EventArgs e)
    {
        if (PilotDetail != null)
            RebuildActivityHeatmap();
    }

    private void RebuildActivityHeatmap()
    {
        if (PilotDetail == null)
        {
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            return;
        }

        var counts = PilotDetail.ActivityHourCounts;
        var bars = PilotDetail.ActivityBuckets;
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

    public void Dispose()
    {
        _activityUtcTimer.Stop();
        _activityUtcTimer.Tick -= OnActivityUtcTick;
        PilotDetail?.ReleaseResources();
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(ShowStatus));

    partial void OnPilotDetailChanged(PilotRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasResult));
        if (value == null)
        {
            _activityUtcTimer.Stop();
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            return;
        }

        RebuildActivityHeatmap();
        _activityUtcTimer.Start();
    }

    [RelayCommand]
    private async Task LookupAsync(CancellationToken cancellationToken = default)
    {
        var q = SearchName.Trim();
        if (string.IsNullOrWhiteSpace(q))
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = "Enter a character name.");
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Status = "Resolving…";
            PilotDetail?.ReleaseResources();
            PilotDetail = null;
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            _activityUtcTimer.Stop();
        });

        try
        {
            _eve.InitializeApi();
            long resolvedId;
            if (_diskCache.TryGetCharacterId(q, out var cachedCharId))
            {
                resolvedId = cachedCharId;
            }
            else
            {
                var bulk = await _eve.Api.Universe.BulkNamesToIdsAsync(new List<string> { q }).WaitAsync(cancellationToken).ConfigureAwait(false);
                var match = bulk.Model?.Characters?.FirstOrDefault(c => c.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Status = "No exact character match from ESI.");
                    return;
                }

                resolvedId = match.Id;
                _diskCache.RememberCharacterId(match.Name, match.Id);
            }

            var info = await _eve.Api.Character.GetCharacterPublicInfoAsync(resolvedId).WaitAsync(cancellationToken).ConfigureAwait(false);
            var resolvedDisplayName = info.Model is { Name: { } nm } && !string.IsNullOrWhiteSpace(nm) ? nm : q;
            _diskCache.RememberCharacterId(resolvedDisplayName, resolvedId);

            string corpName = "", corpTicker = "";
            long corpId = 0;
            if (info.Model != null)
            {
                corpId = info.Model.CorporationId;
                if (_diskCache.TryGetCorporation(corpId, out var t, out var n)
                    && !string.IsNullOrWhiteSpace(t)
                    && !string.IsNullOrWhiteSpace(n))
                {
                    corpTicker = t;
                    corpName = n;
                }
                else
                {
                    var corp = await _eve.Api.Corporation.GetCorporationInfoAsync(corpId).WaitAsync(cancellationToken).ConfigureAwait(false);
                    corpName = corp.Model?.Name ?? "";
                    corpTicker = corp.Model?.Ticker ?? "";
                    if (!string.IsNullOrWhiteSpace(corpTicker))
                        _diskCache.RememberCorporation(corpId, corpTicker, corpName);
                }
            }

            string allianceName = "", allianceTicker = "";
            try
            {
                var aff = await _eve.Api.Character.AffiliationAsync(new List<long> { resolvedId }).WaitAsync(cancellationToken).ConfigureAwait(false);
                var a = aff.Model?.FirstOrDefault(x => x.CharacterId == resolvedId);
                if (a != null)
                {
                    _diskCache.RememberAffiliation(a.CharacterId, a.CorporationId, a.AllianceId);
                    if (a.AllianceId is { } aid && aid > 0)
                    {
                        if (_diskCache.TryGetAlliance(aid, out var at, out var an) && !string.IsNullOrWhiteSpace(at))
                        {
                            allianceTicker = at;
                            allianceName = an ?? "";
                        }
                        else
                        {
                            var all = await _eve.Api.Alliance.GetAllianceInfoAsync(aid).WaitAsync(cancellationToken).ConfigureAwait(false);
                            allianceTicker = all.Model?.Ticker ?? "";
                            allianceName = all.Model?.Name ?? "";
                            if (!string.IsNullOrWhiteSpace(allianceTicker))
                                _diskCache.RememberAlliance(aid, allianceTicker, allianceName);
                        }
                    }
                }
            }
            catch
            {
                // ESI — alliance optional
            }

            ZkillStats? stats = null;
            if (_settings.Load().EnableZkillIntel)
                stats = await _zkill.GetCharacterStatsAsync(resolvedId, cancellationToken).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var row = new PilotRowViewModel(_shipIconCache, _shipTypeNameCache)
                {
                    Name = resolvedDisplayName,
                    CharacterId = resolvedId,
                    Subtitle = resolvedDisplayName,
                    CorpName = corpName,
                    CorpTicker = corpTicker,
                    AllianceName = allianceName,
                    AllianceTicker = allianceTicker,
                    PortraitUrl = EveImageUrls.CharacterPortrait(resolvedId),
                    ShowThreatPendingPlaceholder = _settings.Load().EnableZkillIntel
                };
                row.SetCustomTags(_characterTags.GetTags(resolvedId));

                if (_settings.Load().EnableZkillIntel)
                {
                    if (stats != null)
                        row.ApplyZkillStats(stats);
                    else
                        row.ClearZkillIntel();
                }
                else
                    row.ShowThreatPendingPlaceholder = false;

                PilotDetail = row;
                Status = "";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = ex.Message);
        }
    }
}
