namespace SLH.Services;

/// <summary>
/// Tunable text hints from zKill stats. Ship group IDs are CCP SDE <c>invGroups</c>, verified against
/// <see href="https://everef.net/groups/{id}">EVE Ref</see> (Hauler 28, Deep Space Transport 380,
/// Blockade Runner 1202, Covert Ops 830, Expedition Frigate 1283).
/// </summary>
public static class ZkillIntelHeuristics
{
    public const string Disclaimer = "Heuristic only — verify on zKill.";

    private static readonly int[] CynoSupportHullGroupIds =
    {
        28, // Hauler
        380, // Deep Space Transport
        1202, // Blockade Runner
        830, // Covert Ops
        1283 // Expedition Frigate
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

    /// <summary>Returns null when the cyno/support signal should not be shown.</summary>
    public static string? BuildCynoHint(ZkillStats s)
    {
        if (s.ShipsLost < MinLossesForCyno)
            return null;

        var supportLost = 0;
        foreach (var gid in CynoSupportHullGroupIds)
        {
            if (s.GroupShipsLost.TryGetValue(gid, out var lost))
                supportLost += lost;
        }

        var share = supportLost / (double)Math.Max(1, s.ShipsLost);
        if (share < CynoSupportLossShare || s.GangRatio < CynoGangRatioMin || s.SoloKills > CynoSoloKillsMax)
            return null;

        return
            $"Possible fleet cyno / support-hull pattern (~{share * 100:0}% of losses in hauler/covert transport groups, high gang context).";
    }
}
