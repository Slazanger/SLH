namespace SLH.Services;

public static class DscanClassifier
{
    public static string Classify(string typeName)
    {
        var t = typeName.ToLowerInvariant();
        if (t.Contains("citadel") || t.Contains("fortizar") || t.Contains("keepstar") || t.Contains("astrahus")
            || t.Contains("engineering") || t.Contains("refinery") || t.Contains("jump gate")
            || t.Contains("stargate") || t.Contains("station") || t.Contains("structure"))
            return "Structure";

        if (t.Contains("warp disruptor") || t.Contains("bubble") || t.Contains("mobile observatory"))
            return "Deployable";

        if (t.Contains("recon") || t.Contains("force recon"))
            return "Recon";

        if (t.Contains("covert ops") || t.Contains("stealth bomber"))
            return "Covert";

        if (t.Contains("interceptor") || t.Contains("interdictor"))
            return "Tackle";

        if (t.Contains("dreadnought") || t.Contains("carrier") || t.Contains("supercarrier") || t.Contains("titan")
            || t.Contains("force auxiliary") || t.Contains("fax"))
            return "Capital";

        if (t.Contains("battleship"))
            return "Battleship";

        if (t.Contains("battlecruiser") || t.Contains("command ship"))
            return "Battlecruiser";

        if (t.Contains("cruiser") || t.Contains("strategic cruiser") || t.Contains("t3 cruiser"))
            return "Cruiser";

        if (t.Contains("destroyer"))
            return "Destroyer";

        if (t.Contains("frigate") || t.Contains("assault frigate"))
            return "Frigate";

        if (t.Contains("industrial") || t.Contains("hauler") || t.Contains("freighter") || t.Contains("jump freighter"))
            return "Industrial";

        if (t.Contains("drone") || t.Contains("fighter"))
            return "Drone/Fighter";

        return "Other";
    }
}
