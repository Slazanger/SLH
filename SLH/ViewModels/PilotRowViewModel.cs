using CommunityToolkit.Mvvm.ComponentModel;

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
    [ObservableProperty] private long _iskDestroyed;
    [ObservableProperty] private long _iskLost;
    [ObservableProperty] private string _shipsHint = "";
    [ObservableProperty] private string _activityRegion = "";
    [ObservableProperty] private string _intelTip = "";
    [ObservableProperty] private bool _isFriendly;
    [ObservableProperty] private int[] _activityBuckets = new int[24];

    partial void OnThreatScoreChanged(int value)
    {
        ThreatLabel = value >= 70 ? "HIGH" : value >= 40 ? "MED" : "LOW";
    }
}
