namespace SLH;

/// <summary>
/// EVE SSO / ESI application settings compiled into the build. Edit values here and recompile to change.
/// At developers.eveonline.com, the callback must be http://127.0.0.1:{CallbackPort}/callback/ (PKCE, no secret).
/// </summary>
public static class EsiBuildSettings
{
    public const string EveClientId = "";

    public const int CallbackPort = 9645;

    /// <summary>
    /// Max character names per ESI <c>POST /universe/names/</c> request when bulk-resolving pasted local.
    /// ESI enforces a low cap; adjust here if CCP changes limits.
    /// </summary>
    public const int UniverseNamesBulkBatchSize = 100;
}
