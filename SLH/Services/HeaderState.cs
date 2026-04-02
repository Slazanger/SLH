using CommunityToolkit.Mvvm.ComponentModel;

namespace SLH.Services;

public partial class HeaderState : ObservableObject
{
    [ObservableProperty] private string _systemLine = "System: —";
    [ObservableProperty] private string _localLine = "Local: 0";
    [ObservableProperty] private bool _esiConnected;
    [ObservableProperty] private bool _intelConnected;
    [ObservableProperty] private string _characterDisplayName = "";
    [ObservableProperty] private string _portraitUrl = "";
}
