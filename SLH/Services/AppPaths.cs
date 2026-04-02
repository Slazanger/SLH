namespace SLH.Services;

public static class AppPaths
{
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SLH");

    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    public static string TokenPath => Path.Combine(AppDataDirectory, "session.protected");
}
