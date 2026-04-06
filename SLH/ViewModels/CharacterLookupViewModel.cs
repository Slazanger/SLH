using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLH.Services;

namespace SLH.ViewModels;

public partial class CharacterLookupViewModel : ObservableObject
{
    private readonly EveConnectionService _eve;
    private readonly ZkillClient _zkill;
    private readonly ISettingsStore _settings;

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
    [ObservableProperty] private int[] _activityBuckets = new int[24];
    [ObservableProperty] private Bitmap? _portraitBitmap;

    public bool ShowStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasResult => CharacterId is > 0;

    public bool HasZkillDetail => !string.IsNullOrWhiteSpace(ZkillRatiosLine);

    public bool HasZkillCynoHint => !string.IsNullOrWhiteSpace(ZkillCynoHint);

    partial void OnZkillRatiosLineChanged(string value) => OnPropertyChanged(nameof(HasZkillDetail));

    partial void OnZkillCynoHintChanged(string value) => OnPropertyChanged(nameof(HasZkillCynoHint));

    public CharacterLookupViewModel(EveConnectionService eve, ZkillClient zkill, ISettingsStore settings)
    {
        _eve = eve;
        _zkill = zkill;
        _settings = settings;
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
            PortraitBitmap?.Dispose();
            PortraitBitmap = null;
        });

        try
        {
            _eve.InitializeApi();
            var bulk = await _eve.Api.Universe.BulkNamesToIdsAsync(new List<string> { q }).WaitAsync(cancellationToken).ConfigureAwait(false);
            var match = bulk.Model?.Characters?.FirstOrDefault(c => c.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Status = "No exact character match from ESI.");
                return;
            }

            var info = await _eve.Api.Character.GetCharacterPublicInfoAsync(match.Id).WaitAsync(cancellationToken).ConfigureAwait(false);
            string corpName = "", corpTicker = "";
            if (info.Model != null)
            {
                var corp = await _eve.Api.Corporation.GetCorporationInfoAsync(info.Model.CorporationId).WaitAsync(cancellationToken).ConfigureAwait(false);
                corpName = corp.Model?.Name ?? "";
                corpTicker = corp.Model?.Ticker ?? "";
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
            if (_settings.Load().EnableZkillIntel)
            {
                var stats = await _zkill.GetCharacterStatsAsync(match.Id, cancellationToken).ConfigureAwait(false);
                if (stats != null)
                {
                    shipsDestroyed = stats.ShipsDestroyed;
                    shipsLost = stats.ShipsLost;
                    threatScore = stats.ThreatScore;
                    threatLabel = stats.ThreatLabel;
                    buckets = stats.ActivityBuckets.ToArray();
                    zkillSoloKills = stats.SoloKills;
                    zkillSoloLosses = stats.SoloLosses;
                    zkillRatiosLine = ZkillIntelHeuristics.BuildRatiosLine(stats);
                    zkillPvpSummary = ZkillIntelHeuristics.BuildPvpSummary(stats);
                    zkillCynoHint = ZkillIntelHeuristics.BuildCynoHint(stats) ?? "";
                }
            }

            var portraitUrl = $"https://images.evetech.net/characters/{match.Id}/portrait?tenant=tranquility&size=256";
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
                CharacterId = match.Id;
                ResolvedName = match.Name;
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
                PortraitBitmap?.Dispose();
                PortraitBitmap = portrait;
                Status = "";
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => Status = ex.Message);
        }
    }
}
