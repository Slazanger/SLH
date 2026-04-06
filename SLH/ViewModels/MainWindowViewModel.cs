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
    private readonly EnrichmentDiskCache _enrichmentCache;
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
        ContactStandingIndex contactStandings,
        ISettingsStore settings,
        HeaderState header,
        ZkillClient zkill,
        EnrichmentDiskCache enrichmentCache,
        LocalChatLogWatcher logWatcher)
    {
        _eve = eve;
        _settings = settings;
        _zkill = zkill;
        _enrichmentCache = enrichmentCache;
        _logWatcher = logWatcher;
        Header = header;

        Local = new LocalAnalyserViewModel(eve, contactStandings, settings, header, zkill, enrichmentCache, logWatcher);
        Dscan = new DscanViewModel();
        Lookup = new CharacterLookupViewModel(eve, zkill, settings, enrichmentCache);
        Settings = new SettingsViewModel(settings, eve, enrichmentCache, OnSettingsApplied,
            msg => Header.SystemLine = $"Login failed: {msg}");

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

    /// <param name="reloadSettingsView">Reload settings fields from disk (login/logout); false after auto-save so the chat folder text box is not reset mid-edit.</param>
    private void OnSettingsApplied(bool reloadSettingsView)
    {
        Header.CharacterDisplayName = _eve.CharacterName;
        Header.PortraitUrl = _eve.PortraitUrl;
        Header.EsiConnected = _eve.IsAuthenticated;
        Local.RefreshWatcherPath();
        if (!_eve.IsAuthenticated)
            Local.ClearPilotStandingVisuals();
        else
            Local.RebuildVisiblePilotsList();
        if (reloadSettingsView)
            Settings.Reload();
        _ = RefreshLocationAsync();
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
        Lookup.Dispose();
        _enrichmentCache.Dispose();
        _zkill.Dispose();
    }
}
