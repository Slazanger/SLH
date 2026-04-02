using System.Text.Json;
using SLH.Models;

namespace SLH.Services;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsPath))
                return CreateDefault();
            var json = File.ReadAllText(AppPaths.SettingsPath);
            var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return s ?? CreateDefault();
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static AppSettings CreateDefault()
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var eveLogs = Path.Combine(docs, "EVE", "logs", "ChatLogs");
        return new AppSettings
        {
            ChatLogsFolder = Directory.Exists(eveLogs) ? eveLogs : docs,
            CallbackPort = 49157
        };
    }
}
