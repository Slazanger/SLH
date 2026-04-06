namespace SLH.Services;

/// <summary>UI colors for zKill-derived threat tiers (score ≥70 HIGH, ≥40 MED, else LOW).</summary>
public static class ThreatTierColors
{
    public const int HighMin = 70;
    public const int MediumMin = 40;

    public static string ForegroundForScore(int score) =>
        score >= HighMin ? "#EF5350"
        : score >= MediumMin ? "#FFCA28"
        : "#66BB6A";

    public static string BadgeBackgroundForScore(int score) =>
        score >= HighMin ? "#3D1818"
        : score >= MediumMin ? "#353010"
        : "#152B20";

    public static string BadgeBorderForScore(int score) =>
        score >= HighMin ? "#C62828"
        : score >= MediumMin ? "#F9A825"
        : "#2E7D32";
}
