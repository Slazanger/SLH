namespace SLH.Services;

/// <summary>
/// Tunable text hints from zKill stats. Ship group IDs are CCP SDE <c>invGroups</c>, verified against
/// <see href="https://everef.net/groups/{id}">EVE Ref</see>.
/// Cyno-related hulls counted for the fleet cyno loss pattern: Force Recon, stealth bombers, and Black Ops.
/// </summary>
public static class ZkillIntelHeuristics
{

    private static readonly int[] CynoPatternHullGroupIds =
    {
        833, // Force Recon Ship
        834, // Stealth Bomber
        898 // Black Ops
    };

    private const int MinEngagementsForStyle = 10;
    private const int MinLossesForCyno = 5;
    private const double FleetHeavyGangRatio = 70;
    private const double FleetHeavySoloRatioCap = 20;
    private const double SoloHeavySoloRatio = 40;
    private const int SoloHeavySoloKills = 10;
    private const double CynoSupportLossShare = 0.25;
    private const double CynoGangRatioMin = 75;
    private const int CynoSoloKillsMax = 5;
    private const int ActivePvpDangerRatio = 40;
    private const int ActivePvpShipsDestroyed = 30;

    public static string BuildRatiosLine(ZkillStats s)
    {
        return
            $"Danger {s.DangerRatio:0.#} · Gang {s.GangRatio:0.#}% · Solo {s.SoloRatio:0.#}% · Avg gang {s.AvgGangSize:0.#}";
    }

    /// <summary>Short PvP profile line for the UI.</summary>
    public static string BuildPvpSummary(ZkillStats s)
    {
        var n = s.ShipsDestroyed + s.ShipsLost;
        if (n < MinEngagementsForStyle)
            return "Limited PvP history on zKill.";

        var activity = (s.DangerRatio >= ActivePvpDangerRatio || s.ShipsDestroyed >= ActivePvpShipsDestroyed)
            ? "Active PVPer"
            : "PvP participant";

        string style;
        if (s.GangRatio >= FleetHeavyGangRatio && s.SoloRatio <= FleetHeavySoloRatioCap)
            style = "fleet-heavy";
        else if (s.SoloRatio >= SoloHeavySoloRatio || s.SoloKills >= SoloHeavySoloKills)
            style = "solo-heavy";
        else
            style = "mixed fleet / solo";

        return $"{activity} · {style} PvP on zKill.";
    }

    /// <summary>Returns null when the fleet cyno hull pattern signal should not be shown.</summary>
    public static string? BuildCynoHint(ZkillStats s)
    {
        if (s.ShipsLost < MinLossesForCyno)
            return null;

        var cynoHullLosses = 0;
        foreach (var gid in CynoPatternHullGroupIds)
        {
            if (s.GroupShipsLost.TryGetValue(gid, out var lost))
                cynoHullLosses += lost;
        }

        var share = cynoHullLosses / (double)Math.Max(1, s.ShipsLost);
        if (share < CynoSupportLossShare || s.GangRatio < CynoGangRatioMin || s.SoloKills > CynoSoloKillsMax)
            return null;

        return
            $"Possible fleet cyno pattern (~{share * 100:0}% of losses on Force Recon, stealth bombers, or Black Ops hulls, high gang context).";
    }
}
