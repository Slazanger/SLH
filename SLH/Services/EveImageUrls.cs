namespace SLH.Services;

/// <summary>EVE Image Server CDN URLs (<see href="https://developers.eveonline.com/docs/services/image-server/">docs</see>).</summary>
public static class EveImageUrls
{
    /// <summary>Requested pixel size for character portraits (decode to match for crisp HiDPI UI).</summary>
    public const int CharacterPortraitSize = 256;

    public static string CharacterPortrait(long characterId) =>
        $"https://images.evetech.net/characters/{characterId}/portrait?tenant=tranquility&size={CharacterPortraitSize}";
}
