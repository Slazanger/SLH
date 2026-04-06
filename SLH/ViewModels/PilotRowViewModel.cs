using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SLH.Services;

namespace SLH.ViewModels;

public partial class PilotRowViewModel : ObservableObject
{
    private CancellationTokenSource? _portraitLoadCts;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private long? _characterId;
    [ObservableProperty] private string _corpTicker = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _portraitUrl = "";
    [ObservableProperty] private Bitmap? _portraitBitmap;
    [ObservableProperty] private int _threatScore;
    [ObservableProperty] private string _threatLabel = "LOW";
    [ObservableProperty] private string _threatForeground = ThreatTierColors.ForegroundForScore(0);
    [ObservableProperty] private string _threatBadgeBackground = ThreatTierColors.BadgeBackgroundForScore(0);
    [ObservableProperty] private string _threatBadgeBorderBrush = ThreatTierColors.BadgeBorderForScore(0);
    [ObservableProperty] private int _shipsDestroyed;
    [ObservableProperty] private int _shipsLost;
    [ObservableProperty] private int _zkillSoloKills;
    [ObservableProperty] private int _zkillSoloLosses;
    [ObservableProperty] private string _zkillRatiosLine = "";
    [ObservableProperty] private string _zkillPvpSummary = "";
    [ObservableProperty] private string _zkillCynoHint = "";
    [ObservableProperty] private long _iskDestroyed;
    [ObservableProperty] private long _iskLost;
    [ObservableProperty] private string _shipsHint = "";
    [ObservableProperty] private string _activityRegion = "";
    [ObservableProperty] private string _intelTip = "";
    [ObservableProperty] private bool _isFriendly;
    [ObservableProperty] private int[] _activityBuckets = new int[24];
    /// <summary>UTC hour → kill count from zKill <c>activity</c> (summed across weekdays).</summary>
    [ObservableProperty] private int[] _activityHourCounts = new int[24];
    [ObservableProperty] private string _standingForeground = EveStandingColors.DefaultText;
    [ObservableProperty] private string _standingDisplay = "";
    [ObservableProperty] private string _rowTooltip = "";
    /// <summary>True while zKill intel is on and this row has not finished a threat fetch (success or failed).</summary>
    [ObservableProperty] private bool _showThreatPendingPlaceholder;

    /// <summary>Effective contact standing when resolved; null when unknown or not logged in.</summary>
    public float? EffectiveStanding { get; private set; }

    /// <summary>Shows tier + score badge; inverse of <see cref="ShowThreatPendingPlaceholder"/>.</summary>
    public bool ShowThreatBadgeValues => !ShowThreatPendingPlaceholder;

    public bool IsCharacterResolved => CharacterId is > 0;

    public bool IsCharacterUnresolved => CharacterId is not > 0;

    public bool HasCorpTicker => !string.IsNullOrWhiteSpace(CorpTicker);

    public bool HasStandingDisplay => !string.IsNullOrWhiteSpace(StandingDisplay);

    public double ListNameOpacity => CharacterId is > 0 ? 1.0 : 0.55;

    public void ClearStandingVisual()
    {
        EffectiveStanding = null;
        StandingForeground = EveStandingColors.DefaultText;
        StandingDisplay = "";
    }

    public void ApplyStanding(float effective)
    {
        EffectiveStanding = effective;
        StandingForeground = EveStandingColors.ForegroundForStanding(effective);
        StandingDisplay = EveStandingColors.FormatStanding(effective);
    }

    /// <summary>Cancel portrait load and dispose decoded bitmap (call when row leaves local).</summary>
    public void ReleaseResources()
    {
        _portraitLoadCts?.Cancel();
        _portraitLoadCts?.Dispose();
        _portraitLoadCts = null;
        if (PortraitBitmap is { } b)
        {
            PortraitBitmap = null;
            b.Dispose();
        }
    }

    partial void OnNameChanged(string value) => RefreshRowTooltip();

    partial void OnCorpTickerChanged(string value)
    {
        OnPropertyChanged(nameof(HasCorpTicker));
        RefreshRowTooltip();
    }

    partial void OnStandingDisplayChanged(string value)
    {
        OnPropertyChanged(nameof(HasStandingDisplay));
        RefreshRowTooltip();
    }

    partial void OnCharacterIdChanged(long? value)
    {
        OnPropertyChanged(nameof(IsCharacterResolved));
        OnPropertyChanged(nameof(IsCharacterUnresolved));
        OnPropertyChanged(nameof(ListNameOpacity));
        RefreshRowTooltip();
    }

    partial void OnShowThreatPendingPlaceholderChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowThreatBadgeValues));
        RefreshRowTooltip();
    }

    partial void OnThreatScoreChanged(int value)
    {
        ThreatLabel = value >= ThreatTierColors.HighMin ? "HIGH" : value >= ThreatTierColors.MediumMin ? "MED" : "LOW";
        ThreatForeground = ThreatTierColors.ForegroundForScore(value);
        ThreatBadgeBackground = ThreatTierColors.BadgeBackgroundForScore(value);
        ThreatBadgeBorderBrush = ThreatTierColors.BadgeBorderForScore(value);
        RefreshRowTooltip();
    }

    partial void OnZkillCynoHintChanged(string value)
    {
        OnPropertyChanged(nameof(HasZkillCynoHint));
        RefreshRowTooltip();
    }

    partial void OnPortraitUrlChanged(string value)
    {
        _portraitLoadCts?.Cancel();
        _portraitLoadCts?.Dispose();
        _portraitLoadCts = null;
        if (PortraitBitmap is { } old)
        {
            PortraitBitmap = null;
            old.Dispose();
        }

        if (string.IsNullOrWhiteSpace(value))
            return;

        var cts = new CancellationTokenSource();
        _portraitLoadCts = cts;
        var url = value;
        _ = LoadPortraitAsync(url, cts.Token);
    }

    private async Task LoadPortraitAsync(string url, CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SLH/0.1");
            await using var stream = await client.GetStreamAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            await using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            ms.Position = 0;
            bitmap = await Task.Run(() => Bitmap.DecodeToWidth(ms, 32), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            bitmap?.Dispose();
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || url != PortraitUrl)
            {
                bitmap?.Dispose();
                return;
            }

            if (PortraitBitmap is { } prev)
            {
                PortraitBitmap = null;
                prev.Dispose();
            }

            PortraitBitmap = bitmap;
        });
    }

    private void RefreshRowTooltip()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Name))
            parts.Add(Name);
        if (!string.IsNullOrWhiteSpace(CorpTicker))
            parts.Add($"[{CorpTicker}]");
        if (!string.IsNullOrWhiteSpace(StandingDisplay))
            parts.Add($"Standing {StandingDisplay}");
        if (CharacterId is not > 0)
            parts.Add("Resolving character…");
        if (ShowThreatPendingPlaceholder)
            parts.Add("Threat …");
        else
            parts.Add($"Threat {ThreatLabel} ({ThreatScore})");
        if (HasZkillCynoHint)
            parts.Add("Cyno hint (zKill)");
        RowTooltip = string.Join(" · ", parts);
    }

    public bool HasZkillDetail => !string.IsNullOrWhiteSpace(ZkillRatiosLine);

    public bool HasZkillCynoHint => !string.IsNullOrWhiteSpace(ZkillCynoHint);

    partial void OnZkillRatiosLineChanged(string value) => OnPropertyChanged(nameof(HasZkillDetail));
}
