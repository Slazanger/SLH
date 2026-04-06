namespace SLH.Models;

public sealed class AppSettings
{
    public string ChatLogsFolder { get; set; } = "";
    public bool EnableZkillIntel { get; set; } = true;

    /// <summary>
    /// When true (default), pilots with contact standing +5 or +10 are hidden from the local list.
    /// Nullable so older settings files without this key still default to filtering on.
    /// </summary>
    public bool? FilterOutStandingPlus5Or10 { get; set; } = true;
}
