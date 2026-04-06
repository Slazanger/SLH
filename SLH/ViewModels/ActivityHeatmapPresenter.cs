using System.Collections.ObjectModel;
using System.Globalization;

namespace SLH.ViewModels;

public static class ActivityHeatmapPresenter
{
    public static void Rebuild(
        ObservableCollection<ActivityHeatmapCellViewModel> cells,
        ReadOnlySpan<int> hourlyKillCounts,
        ReadOnlySpan<int> barHeightsPx,
        DateTime utcNow)
    {
        cells.Clear();
        if (hourlyKillCounts.Length != 24 || barHeightsPx.Length != 24)
            return;

        var nowHour = utcNow.Hour;
        for (var h = 0; h < 24; h++)
        {
            var k = hourlyKillCounts[h];
            cells.Add(new ActivityHeatmapCellViewModel
            {
                HourUtc = h,
                KillCount = k,
                BarHeight = barHeightsPx[h],
                IsCurrentUtcHour = h == nowHour,
                Tooltip = $"{h:D2}:00 UTC — {k} kills (zKill activity, summed by weekday)"
            });
        }
    }

    public static string BuildUtcLine(ReadOnlySpan<int> hourlyKillCounts, DateTime utcNow)
    {
        if (hourlyKillCounts.Length != 24)
            return "";

        var total = 0;
        var peak = 0;
        var peakHour = 0;
        for (var h = 0; h < 24; h++)
        {
            var c = hourlyKillCounts[h];
            total += c;
            if (c > peak)
            {
                peak = c;
                peakHour = h;
            }
        }

        var nowH = utcNow.Hour;
        var nowC = hourlyKillCounts[nowH];
        var clock = utcNow.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (total <= 0)
            return $"Now {clock} UTC. No zKill activity heatmap data for this character.";

        string windowHint;
        if (peak <= 0)
            windowHint = "";
        else if (nowC >= peak * 0.55)
            windowHint = "This UTC hour is among their stronger windows on zKill.";
        else if (nowC > 0)
            windowHint = "Some historical PvP this UTC hour on zKill.";
        else
            windowHint = "Little or no recorded PvP this UTC hour on zKill.";

        return
            $"Now {clock} UTC (bar highlighted). {windowHint} Busiest UTC hour in aggregate: {peakHour:D2}:00 ({peak} kills).";
    }
}
