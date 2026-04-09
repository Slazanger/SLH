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
    private readonly HeaderState _header;
    /// <summary>Arg: reload this view model from disk (use after login/logout); false when we already have the latest edits.</summary>
    private readonly Action<bool> _onSettingsApplied;
    private readonly Action<string>? _onLoginFailed;
    private bool _suppressPersist;
    private readonly DispatcherTimer _folderPersistDebounce;
    private readonly DispatcherTimer _uiScalePersistDebounce;

    [ObservableProperty] private string _chatLogsFolder = "";
    [ObservableProperty] private bool _enableZkillIntel = true;
    [ObservableProperty] private bool _filterOutStandingPlus5Or10 = true;
    [ObservableProperty] private double _uiScale = 1.0;
    [ObservableProperty] private string _loggedInCharacterLine = "";

    public SettingsViewModel(
        ISettingsStore settingsStore,
        EveConnectionService eve,
        EnrichmentDiskCache enrichmentCache,
        HeaderState header,
        Action<bool> onSettingsApplied,
        Action<string>? onLoginFailed = null)
    {
        _settingsStore = settingsStore;
        _eve = eve;
        _enrichmentCache = enrichmentCache;
        _header = header;
        _onSettingsApplied = onSettingsApplied;
        _onLoginFailed = onLoginFailed;

        _header.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(HeaderState.CharacterDisplayName) or nameof(HeaderState.EsiConnected))
                RefreshLoggedInCharacterLine();
        };

        _folderPersistDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _folderPersistDebounce.Tick += (_, _) =>
        {
            _folderPersistDebounce.Stop();
            if (!_suppressPersist)
                PersistToDisk(reloadSettingsView: false);
        };

        _uiScalePersistDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _uiScalePersistDebounce.Tick += (_, _) =>
        {
            _uiScalePersistDebounce.Stop();
            if (!_suppressPersist)
                PersistToDisk(reloadSettingsView: false);
        };

        Reload();
    }

    private void RefreshLoggedInCharacterLine()
    {
        LoggedInCharacterLine = _header.EsiConnected && !string.IsNullOrWhiteSpace(_header.CharacterDisplayName)
            ? _header.CharacterDisplayName
            : "Not logged in";
    }

    public void Reload()
    {
        _suppressPersist = true;
        try
        {
            var s = _settingsStore.Load();
            ChatLogsFolder = s.ChatLogsFolder;
            EnableZkillIntel = s.EnableZkillIntel;
            FilterOutStandingPlus5Or10 = s.FilterOutStandingPlus5Or10 is not false;
            UiScale = ClampUiScale(s.UiScale);
        }
        finally
        {
            _suppressPersist = false;
        }

        RefreshLoggedInCharacterLine();
    }

    private void PersistToDisk(bool reloadSettingsView)
    {
        _settingsStore.Save(new AppSettings
        {
            ChatLogsFolder = ChatLogsFolder.Trim(),
            EnableZkillIntel = EnableZkillIntel,
            FilterOutStandingPlus5Or10 = FilterOutStandingPlus5Or10,
            UiScale = ClampUiScale(UiScale)
        });
        _onSettingsApplied(reloadSettingsView);
    }

    partial void OnChatLogsFolderChanged(string value)
    {
        if (_suppressPersist)
            return;
        _folderPersistDebounce.Stop();
        _folderPersistDebounce.Start();
    }

    partial void OnEnableZkillIntelChanged(bool value)
    {
        if (_suppressPersist)
            return;
        PersistToDisk(reloadSettingsView: false);
    }

    partial void OnFilterOutStandingPlus5Or10Changed(bool value)
    {
        if (_suppressPersist)
            return;
        PersistToDisk(reloadSettingsView: false);
    }

    partial void OnUiScaleChanged(double value)
    {
        if (_suppressPersist)
            return;
        _uiScalePersistDebounce.Stop();
        _uiScalePersistDebounce.Start();
    }

    private static double ClampUiScale(double value) =>
        Math.Clamp(value, AppSettings.UiScaleMin, AppSettings.UiScaleMax);

    [RelayCommand]
    private void Logout()
    {
        _eve.Logout();
        _onSettingsApplied(true);
    }

    [RelayCommand]
    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _eve.LoginWithBrowserAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => _onSettingsApplied(true));
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
