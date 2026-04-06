using CommunityToolkit.Mvvm.ComponentModel;
using SLH.Services;

namespace SLH.ViewModels;

public partial class PilotRowViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private long? _characterId;
    [ObservableProperty] private string _corpTicker = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _portraitUrl = "";
    [ObservableProperty] private int _threatScore;
    [ObservableProperty] private string _threatLabel = "LOW";
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

    /// <summary>Effective contact standing when resolved; null when unknown or not logged in.</summary>
    public float? EffectiveStanding { get; private set; }

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

    partial void OnThreatScoreChanged(int value)
    {
        ThreatLabel = value >= 70 ? "HIGH" : value >= 40 ? "MED" : "LOW";
    }

    public bool HasZkillDetail => !string.IsNullOrWhiteSpace(ZkillRatiosLine);

    public bool HasZkillCynoHint => !string.IsNullOrWhiteSpace(ZkillCynoHint);

    partial void OnZkillRatiosLineChanged(string value) => OnPropertyChanged(nameof(HasZkillDetail));

    partial void OnZkillCynoHintChanged(string value) => OnPropertyChanged(nameof(HasZkillCynoHint));
}
