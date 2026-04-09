namespace SLH.Services;

public static class AppPaths
{
    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SLH");

    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    public static string TokenPath => Path.Combine(AppDataDirectory, "session.protected");

    public static string EnrichmentCachePath => Path.Combine(AppDataDirectory, "enrichment-cache.json");

    public static string CharacterTagsPath => Path.Combine(AppDataDirectory, "character-tags.json");

    public static string ShipTypeNamesPath => Path.Combine(AppDataDirectory, "ship-type-names.json");
}
