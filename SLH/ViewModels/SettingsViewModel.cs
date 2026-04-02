using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLH.Models;
using SLH.Services;

namespace SLH.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsStore _settingsStore;
    private readonly EveConnectionService _eve;
    private readonly Action _onSaved;

    [ObservableProperty] private string _chatLogsFolder = "";
    [ObservableProperty] private bool _enableZkillIntel = true;

    public SettingsViewModel(ISettingsStore settingsStore, EveConnectionService eve, Action onSaved)
    {
        _settingsStore = settingsStore;
        _eve = eve;
        _onSaved = onSaved;
        Reload();
    }

    public void Reload()
    {
        var s = _settingsStore.Load();
        ChatLogsFolder = s.ChatLogsFolder;
        EnableZkillIntel = s.EnableZkillIntel;
    }

    [RelayCommand]
    private void Save()
    {
        _settingsStore.Save(new AppSettings
        {
            ChatLogsFolder = ChatLogsFolder.Trim(),
            EnableZkillIntel = EnableZkillIntel
        });
        _onSaved();
    }

    [RelayCommand]
    private void Logout()
    {
        _eve.Logout();
        _onSaved();
    }

}
