namespace SLH.Models;

public sealed class AppSettings
{
    public string EveClientId { get; set; } = "";
    public int CallbackPort { get; set; } = 49157;
    public string ChatLogsFolder { get; set; } = "";
    public bool EnableZkillIntel { get; set; } = true;
}
