namespace SLH.Services;

/// <summary>External intel / map sites opened from the pilot detail panel.</summary>
public static class EveIntelWebUrls
{
    public static Uri ZkillCharacter(long characterId) =>
        new($"https://zkillboard.com/character/{characterId}/", UriKind.Absolute);

    public static Uri EveWhoCharacter(long characterId) =>
        new($"https://evewho.com/character/{characterId}", UriKind.Absolute);

    public static Uri DotlanCorporation(long corporationId) =>
        new($"https://evemaps.dotlan.net/corp/{corporationId}", UriKind.Absolute);

    public static Uri DotlanAlliance(long allianceId) =>
        new($"https://evemaps.dotlan.net/alliance/{allianceId}", UriKind.Absolute);
}
