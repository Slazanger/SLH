namespace SLH.Models;

public sealed class AppSettings
{
    /// <summary>Allowed range for <see cref="UiScale"/> when persisting or applying UI (0.75–1.5).</summary>
    public const double UiScaleMin = 0.75;
    public const double UiScaleMax = 1.5;

    public string ChatLogsFolder { get; set; } = "";
    public bool EnableZkillIntel { get; set; } = true;

    /// <summary>
    /// When true (default), pilots with contact standing +5 or +10 are hidden from the local list.
    /// Nullable so older settings files without this key still default to filtering on.
    /// </summary>
    public bool? FilterOutStandingPlus5Or10 { get; set; } = true;

    /// <summary>Uniform UI scale for the main window content. Clamped to <see cref="UiScaleMin"/>–<see cref="UiScaleMax"/> when loaded/saved.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Primary sort order for pilots on the Local tab.</summary>
    public LocalPilotSortMode LocalPilotSort { get; set; } = LocalPilotSortMode.Name;
}
