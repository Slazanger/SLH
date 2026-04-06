using System.Collections.ObjectModel;
using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLH.Services;

namespace SLH.ViewModels;

public partial class CharacterLookupViewModel : ObservableObject, IDisposable
{
    private readonly EveConnectionService _eve;
    private readonly ZkillClient _zkill;
    private readonly ISettingsStore _settings;
    private readonly EnrichmentDiskCache _diskCache;
    private readonly DispatcherTimer _activityUtcTimer;

    public ObservableCollection<ActivityHeatmapCellViewModel> ActivityHeatmap { get; } = new();

    [ObservableProperty] private string _searchName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private long? _characterId;
    [ObservableProperty] private string _resolvedName = "";
    [ObservableProperty] private string _corpName = "";
    [ObservableProperty] private string _corpTicker = "";
    [ObservableProperty] private string _portraitUrl = "";
    [ObservableProperty] private int _shipsDestroyed;
    [ObservableProperty] private int _shipsLost;
    [ObservableProperty] private int _zkillSoloKills;
    [ObservableProperty] private int _zkillSoloLosses;
    [ObservableProperty] private string _zkillRatiosLine = "";
    [ObservableProperty] private string _zkillPvpSummary = "";
    [ObservableProperty] private string _zkillCynoHint = "";
    [ObservableProperty] private int _threatScore;
    [ObservableProperty] private string _threatLabel = "";
    [ObservableProperty] private string _threatForeground = "#8a9aaa";
    [ObservableProperty] private int[] _activityBuckets = new int[24];
    [ObservableProperty] private int[] _activityHourCounts = new int[24];
    [ObservableProperty] private string _activityHeatmapUtcLine = "";
    [ObservableProperty] private Bitmap? _portraitBitmap;

    public bool ShowStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasResult => CharacterId is > 0;

    public bool HasZkillDetail => !string.IsNullOrWhiteSpace(ZkillRatiosLine);

    public bool HasZkillCynoHint => !string.IsNullOrWhiteSpace(ZkillCynoHint);

    partial void OnZkillRatiosLineChanged(string value) => OnPropertyChanged(nameof(HasZkillDetail));

    partial void OnZkillCynoHintChanged(string value) => OnPropertyChanged(nameof(HasZkillCynoHint));

    partial void OnThreatScoreChanged(int value) => RefreshThreatForeground();

    partial void OnThreatLabelChanged(string value) => RefreshThreatForeground();

    private void RefreshThreatForeground()
    {
        ThreatForeground = string.IsNullOrWhiteSpace(ThreatLabel) && ThreatScore == 0
            ? "#8a9aaa"
            : ThreatTierColors.ForegroundForScore(ThreatScore);
    }

    public CharacterLookupViewModel(EveConnectionService eve, ZkillClient zkill, ISettingsStore settings,
        EnrichmentDiskCache diskCache)
    {
        _eve = eve;
        _zkill = zkill;
        _settings = settings;
        _diskCache = diskCache;

        _activityUtcTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _activityUtcTimer.Tick += OnActivityUtcTick;
    }

    private void OnActivityUtcTick(object? sender, EventArgs e)
    {
        if (HasResult)
            RebuildActivityHeatmap();
    }

    private void RebuildActivityHeatmap()
    {
        if (!HasResult)
        {
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            return;
        }

        if (ActivityHourCounts is not { Length: 24 } || ActivityBuckets is not { Length: 24 })
        {
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            return;
        }

        var utc = DateTime.UtcNow;
        ActivityHeatmapPresenter.Rebuild(ActivityHeatmap, ActivityHourCounts, ActivityBuckets, utc);
        ActivityHeatmapUtcLine = ActivityHeatmapPresenter.BuildUtcLine(ActivityHourCounts, utc);
    }

    public void Dispose()
    {
        _activityUtcTimer.Stop();
        _activityUtcTimer.Tick -= OnActivityUtcTick;
    }

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(ShowStatus));
    partial void OnCharacterIdChanged(long? value) => OnPropertyChanged(nameof(HasResult));

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
            CharacterId = null;
            ResolvedName = "";
            CorpName = "";
            CorpTicker = "";
            PortraitUrl = "";
            ThreatScore = 0;
            ThreatLabel = "";
            ShipsDestroyed = 0;
            ShipsLost = 0;
            ZkillSoloKills = 0;
            ZkillSoloLosses = 0;
            ZkillRatiosLine = "";
            ZkillPvpSummary = "";
            ZkillCynoHint = "";
            ActivityBuckets = new int[24];
            ActivityHourCounts = new int[24];
            ActivityHeatmap.Clear();
            ActivityHeatmapUtcLine = "";
            _activityUtcTimer.Stop();
            PortraitBitmap?.Dispose();
            PortraitBitmap = null;
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
            var resolvedDisplayName = string.IsNullOrWhiteSpace(info.Model?.Name) ? q : info.Model.Name;
            _diskCache.RememberCharacterId(resolvedDisplayName, resolvedId);

            string corpName = "", corpTicker = "";
            if (info.Model != null)
            {
                var corpId = info.Model.CorporationId;
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

            var shipsDestroyed = 0;
            var shipsLost = 0;
            var threatScore = 0;
            var threatLabel = "";
            var buckets = new int[24];
            var zkillSoloKills = 0;
            var zkillSoloLosses = 0;
            var zkillRatiosLine = "";
            var zkillPvpSummary = "";
            var zkillCynoHint = "";
            var hourCounts = new int[24];
            if (_settings.Load().EnableZkillIntel)
            {
                var stats = await _zkill.GetCharacterStatsAsync(resolvedId, cancellationToken).ConfigureAwait(false);
                if (stats != null)
                {
                    shipsDestroyed = stats.ShipsDestroyed;
                    shipsLost = stats.ShipsLost;
                    threatScore = stats.ThreatScore;
                    threatLabel = stats.ThreatLabel;
                    buckets = stats.ActivityBuckets.ToArray();
                    for (var i = 0; i < 24 && i < stats.ActivityHourCounts.Count; i++)
                        hourCounts[i] = stats.ActivityHourCounts[i];
                    zkillSoloKills = stats.SoloKills;
                    zkillSoloLosses = stats.SoloLosses;
                    zkillRatiosLine = ZkillIntelHeuristics.BuildRatiosLine(stats);
                    zkillPvpSummary = ZkillIntelHeuristics.BuildPvpSummary(stats);
                    zkillCynoHint = ZkillIntelHeuristics.BuildCynoHint(stats) ?? "";
                }
            }

            var portraitUrl = $"https://images.evetech.net/characters/{resolvedId}/portrait?tenant=tranquility&size=256";
            Bitmap? portrait = null;
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SLH/0.1");
                await using var stream = await client.GetStreamAsync(new Uri(portraitUrl), cancellationToken).ConfigureAwait(false);
                await using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                ms.Position = 0;
                portrait = await Task.Run(() => Bitmap.DecodeToWidth(ms, 256), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                portrait?.Dispose();
                portrait = null;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                CharacterId = resolvedId;
                ResolvedName = resolvedDisplayName;
                PortraitUrl = portraitUrl;
                CorpName = corpName;
                CorpTicker = corpTicker;
                ShipsDestroyed = shipsDestroyed;
                ShipsLost = shipsLost;
                ZkillSoloKills = zkillSoloKills;
                ZkillSoloLosses = zkillSoloLosses;
                ZkillRatiosLine = zkillRatiosLine;
                ZkillPvpSummary = zkillPvpSummary;
                ZkillCynoHint = zkillCynoHint;
                ThreatScore = threatScore;
                ThreatLabel = threatLabel;
                ActivityBuckets = buckets;
                ActivityHourCounts = hourCounts;
                PortraitBitmap?.Dispose();
                PortraitBitmap = portrait;
                Status = "";
                RebuildActivityHeatmap();
                _activityUtcTimer.Start();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = ex.Message);
        }
    }
}
