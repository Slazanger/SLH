using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SLH.Services;

namespace SLH.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly EveConnectionService _eve;
    private readonly ISettingsStore _settings;
    private readonly ZkillClient _zkill;
    private readonly LocalChatLogWatcher _logWatcher;
    private readonly DispatcherTimer _locationTimer;

    public HeaderState Header { get; }
    public LocalAnalyserViewModel Local { get; }
    public DscanViewModel Dscan { get; }
    public CharacterLookupViewModel Lookup { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty] private int _selectedTabIndex;

    public MainWindowViewModel(
        EveConnectionService eve,
        ISettingsStore settings,
        HeaderState header,
        ZkillClient zkill,
        LocalChatLogWatcher logWatcher)
    {
        _eve = eve;
        _settings = settings;
        _zkill = zkill;
        _logWatcher = logWatcher;
        Header = header;

        Local = new LocalAnalyserViewModel(eve, settings, header, zkill, logWatcher);
        Dscan = new DscanViewModel();
        Lookup = new CharacterLookupViewModel(eve, zkill, settings);
        Settings = new SettingsViewModel(settings, eve, OnSettingsSaved);

        _locationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _locationTimer.Tick += OnLocationTick;
    }

    private async void OnLocationTick(object? sender, EventArgs e) => await RefreshLocationAsync();

    public async Task InitializeAsync()
    {
        try
        {
            await _eve.RestoreSessionAsync().ConfigureAwait(false);
        }
        catch
        {
            // ignore corrupt session
        }

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            Header.CharacterDisplayName = _eve.CharacterName;
            Header.PortraitUrl = _eve.PortraitUrl;
            Header.EsiConnected = _eve.IsAuthenticated;
            Local.RefreshWatcherPath();
            await RefreshLocationAsync();
            _locationTimer.Start();
        });
    }

    private void OnSettingsSaved()
    {
        Header.CharacterDisplayName = _eve.CharacterName;
        Header.PortraitUrl = _eve.PortraitUrl;
        Header.EsiConnected = _eve.IsAuthenticated;
        Local.RefreshWatcherPath();
        Settings.Reload();
        _ = RefreshLocationAsync();
    }

    [RelayCommand]
    private async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _eve.LoginWithBrowserAsync(cancellationToken).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                Header.CharacterDisplayName = _eve.CharacterName;
                Header.PortraitUrl = _eve.PortraitUrl;
                Header.EsiConnected = true;
                Local.RefreshWatcherPath();
                await RefreshLocationAsync();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Header.SystemLine = $"Login failed: {ex.Message}";
            });
        }
    }

    [RelayCommand]
    private void OpenSettingsTab() => SelectedTabIndex = 3;

    [RelayCommand]
    private async Task RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        await RefreshLocationAsync();
    }

    private async Task RefreshLocationAsync()
    {
        if (!_eve.IsAuthenticated)
        {
            Header.SystemLine = "System: — (log in for ESI location)";
            Header.IntelConnected = false;
            return;
        }

        try
        {
            await _eve.EnsureFreshAccessAsync().ConfigureAwait(false);
            var auth = _eve.Auth;
            var loc = await _eve.Api.Location.GetCharacterLocationAsync(auth).ConfigureAwait(false);
            var solarSystemId = loc.Model?.SolarSystemId ?? 0;
            if (solarSystemId == 0)
            {
                Header.SystemLine = "System: —";
                return;
            }

            var sys = await _eve.Api.Universe.GetSolarSystemInfoAsync(solarSystemId).ConfigureAwait(false);
            var sec = sys.Model?.SecurityStatus ?? 0;
            var name = sys.Model?.Name ?? $"System {solarSystemId}";
            Header.SystemLine = $"System: {name} ({sec:0.0})";

            var online = await _eve.Api.Location.GetCharacterOnlineAsync(auth).ConfigureAwait(false);
            Header.IntelConnected = online.Model?.Online ?? false;
        }
        catch
        {
            Header.SystemLine = "System: — (ESI error)";
        }
    }

    public void Dispose()
    {
        _locationTimer.Stop();
        _locationTimer.Tick -= OnLocationTick;
        Local.Dispose();
        _zkill.Dispose();
    }
}
