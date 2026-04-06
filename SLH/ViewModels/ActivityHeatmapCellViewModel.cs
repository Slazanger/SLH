using CommunityToolkit.Mvvm.ComponentModel;

namespace SLH.ViewModels;

/// <summary>One column in the zKill UTC hourly activity strip (from stats <c>activity</c>).</summary>
public partial class ActivityHeatmapCellViewModel : ObservableObject
{
    [ObservableProperty] private int _hourUtc;
    [ObservableProperty] private int _killCount;
    [ObservableProperty] private double _barHeight;
    [ObservableProperty] private bool _isCurrentUtcHour;
    [ObservableProperty] private string _tooltip = "";
}
