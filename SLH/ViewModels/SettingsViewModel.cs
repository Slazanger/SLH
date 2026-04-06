using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLH.Models;
using SLH.Services;

namespace SLH.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly EveConnectionService _eve;
    private readonly EnrichmentDiskCache _enrichmentCache;
    private readonly Action _onSaved;
    private readonly Action<string>? _onLoginFailed;

    [ObservableProperty] private string _chatLogsFolder = "";
    [ObservableProperty] private bool _enableZkillIntel = true;
    [ObservableProperty] private bool _filterOutStandingPlus5Or10 = true;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        EveConnectionService eve,
        EnrichmentDiskCache enrichmentCache,
        Action onSaved,
        Action<string>? onLoginFailed = null)
    {
        _settingsStore = settingsStore;
        _eve = eve;
        _enrichmentCache = enrichmentCache;
        _onSaved = onSaved;
        _onLoginFailed = onLoginFailed;
        Reload();
    }

    public void Reload()
    {
        var s = _settingsStore.Load();
        ChatLogsFolder = s.ChatLogsFolder;
        EnableZkillIntel = s.EnableZkillIntel;
        FilterOutStandingPlus5Or10 = s.FilterOutStandingPlus5Or10 is not false;
    }

    [RelayCommand]
    private void Save()
    {
        _settingsStore.Save(new AppSettings
        {
            ChatLogsFolder = ChatLogsFolder.Trim(),
            EnableZkillIntel = EnableZkillIntel,
            FilterOutStandingPlus5Or10 = FilterOutStandingPlus5Or10
        });
        _onSaved();
    }

    [RelayCommand]
    private void Logout()
    {
        _eve.Logout();
        _onSaved();
    }

    [RelayCommand]
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _eve.LoginWithBrowserAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(_onSaved);
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => _onLoginFailed?.Invoke(ex.Message));
        }
    }

    [RelayCommand]
    private void ClearEnrichmentCache()
    {
        _enrichmentCache.ClearAll();
    }

}
