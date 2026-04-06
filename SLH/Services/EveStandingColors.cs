namespace SLH.Services;

/// <summary>EVE client–style standing tier colors (foreground hex for UI).</summary>
public static class EveStandingColors
{
    public const string DefaultText = "#e0e6ec";

    /// <summary>Maps effective standing to a text color similar to the EVE client tiers.</summary>
    public static string ForegroundForStanding(float standing)
    {
        if (standing >= 10f)
            return "#2B9FD9";
        if (standing >= 5f)
            return "#5CB3D9";
        if (standing > 0f)
            return "#C8D8E4";
        if (standing == 0f)
            return "#8A9AAA";
        if (standing > -5f)
            return "#D9A441";
        if (standing > -10f)
            return "#E07A2E";
        return "#C62828";
    }

    public static string FormatStanding(float standing)
    {
        if (standing > 0f)
            return $"+{standing:0.##}";
        return standing.ToString("0.##");
    }
}
