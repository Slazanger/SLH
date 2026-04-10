using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using SLH.Services;
using SLH.ViewModels;
using SLH.Views;
using System.IO;
using System.Linq;

namespace SLH;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false)
                .Build();

            ISettingsStore settingsStore = new SettingsStore();
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(AppPaths.AppDataDirectory));
            ISecureSessionStore secure = new DataProtectionSessionStore(dataProtection);

            var eve = new EveConnectionService(configuration, secure);
            var contactStandings = new ContactStandingIndex(eve);
            eve.ContactStandingCache = contactStandings;
            var header = new HeaderState();
            var enrichmentCache = new EnrichmentDiskCache();
            var characterTags = new CharacterTagStore();
            var zkill = new ZkillClient(configuration, enrichmentCache);
            var shipIconCache = new ShipIconCache(configuration);
            var shipTypeNameCache = new ShipTypeNameCache(configuration);
            var logWatcher = new LocalChatLogWatcher();

            var mainVm = new MainWindowViewModel(eve, contactStandings, settingsStore, header, zkill, shipIconCache,
                shipTypeNameCache, enrichmentCache, characterTags, logWatcher);

            var mainWindow = new MainWindow { DataContext = mainVm };
            mainWindow.Opened += async (_, _) => await mainVm.InitializeAsync();
            mainWindow.Closing += (_, _) => mainVm.Dispose();

            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();
        foreach (var plugin in dataValidationPluginsToRemove)
            BindingPlugins.DataValidators.Remove(plugin);
    }
}
