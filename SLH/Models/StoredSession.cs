namespace SLH.Models;

public sealed class StoredSession
{
    public long CharacterId { get; set; }
    public string CharacterName { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string ScopesJoined { get; set; } = "";
}
