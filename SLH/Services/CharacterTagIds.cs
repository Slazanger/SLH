namespace SLH.Services;

/// <summary>Predefined custom tags for pilots (Local list). Persisted by <see cref="CharacterTagStore"/>.</summary>
public static class CharacterTagIds
{
    public const string Fc = "FC";
    public const string CloakyCamper = "Cloaky Camper";
    public const string JfHunter = "JF Hunter";
    public const string Ganker = "Ganker";

    public static readonly IReadOnlyList<string> All = new[] { Fc, CloakyCamper, JfHunter, Ganker };

    public static bool IsKnown(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        foreach (var k in All)
        {
            if (string.Equals(k, tag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
