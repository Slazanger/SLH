using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using SLH.Services;

namespace SLH.ViewModels;

public partial class PilotRowViewModel : ObservableObject
{
    private const int PortraitDecodeWidth = EveImageUrls.CharacterPortraitSize;
    private const int TopShipIconDecodeWidth = 128;

    private readonly ShipIconCache _shipIconCache;
    private readonly ShipTypeNameCache _shipTypeNameCache;
    private CancellationTokenSource? _portraitLoadCts;
    private CancellationTokenSource? _topShipIconsCts;
    private int _topShipIconLoadGen;

    public PilotRowViewModel(ShipIconCache shipIconCache, ShipTypeNameCache shipTypeNameCache)
    {
        _shipIconCache = shipIconCache;
        _shipTypeNameCache = shipTypeNameCache;
    }

    public void ApplyZkillStats(ZkillStats stats)
    {
        ShowThreatPendingPlaceholder = false;
        ThreatScore = stats.ThreatScore;
        ShipsDestroyed = stats.ShipsDestroyed;
        ShipsLost = stats.ShipsLost;
        IskDestroyed = stats.IskDestroyed;
        IskLost = stats.IskLost;
        IsFriendly = stats.ThreatScore < 15;
        ActivityRegion = "Recent activity (zKill aggregates)";
        IntelTip = stats.SoloKills > 10
            ? "TIP: High solo activity on zKill — expect aggressive solo pilots."
            : "TIP: Review loss patterns on zKill for ship preferences.";
        ActivityBuckets = stats.ActivityBuckets.ToArray();
        ShipsHint = $"Ships destroyed / lost: {stats.ShipsDestroyed} / {stats.ShipsLost} (zKill)";
        ZkillSoloKills = stats.SoloKills;
        ZkillSoloLosses = stats.SoloLosses;
        ZkillRatiosLine = ZkillIntelHeuristics.BuildRatiosLine(stats);
        ZkillPvpSummary = ZkillIntelHeuristics.BuildPvpSummary(stats);
        ZkillCynoHint = ZkillIntelHeuristics.BuildCynoHint(stats) ?? "";
        TagFcFromZkill = stats.MonitorInTopShips;
        ActivityHourCounts = CopyActivityHourCounts24(stats.ActivityHourCounts);
        ApplyTopShipsFromZkill(stats.TopShipTypeIds, stats.TopShipKills);
    }

    public void ClearZkillIntel()
    {
        ShowThreatPendingPlaceholder = false;
        ThreatScore = 0;
        ShipsDestroyed = 0;
        ShipsLost = 0;
        IskDestroyed = 0;
        IskLost = 0;
        IsFriendly = false;
        ActivityRegion = "";
        IntelTip = "";
        ActivityBuckets = new int[24];
        ActivityHourCounts = new int[24];
        ShipsHint = "";
        ZkillSoloKills = 0;
        ZkillSoloLosses = 0;
        ZkillRatiosLine = "";
        ZkillPvpSummary = "";
        ZkillCynoHint = "";
        TagFcFromZkill = false;
        ApplyTopShipsFromZkill(Array.Empty<int>(), Array.Empty<int>());
    }

    private static int[] CopyActivityHourCounts24(IReadOnlyList<int> src)
    {
        var a = new int[24];
        for (var i = 0; i < 24 && i < src.Count; i++)
            a[i] = src[i];
        return a;
    }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private long? _characterId;
    /// <summary>ESI corporation id when affiliation (or public info) is known; null until resolved.</summary>
    [ObservableProperty] private long? _corporationId;
    /// <summary>ESI alliance id when in an alliance; null if none or not yet resolved.</summary>
    [ObservableProperty] private long? _allianceId;
    [ObservableProperty] private string _corpTicker = "";
    [ObservableProperty] private string _corpName = "";
    [ObservableProperty] private string _allianceTicker = "";
    [ObservableProperty] private string _allianceName = "";
    [ObservableProperty] private string _corpDetailLine = "";
    [ObservableProperty] private string _allianceDetailLine = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _portraitUrl = "";
    [ObservableProperty] private Bitmap? _portraitBitmap;
    [ObservableProperty] private int _threatScore;
    [ObservableProperty] private string _threatLabel = "LOW";
    [ObservableProperty] private string _threatForeground = ThreatTierColors.ForegroundForScore(0);
    [ObservableProperty] private string _threatBadgeBackground = ThreatTierColors.BadgeBackgroundForScore(0);
    [ObservableProperty] private string _threatBadgeBorderBrush = ThreatTierColors.BadgeBorderForScore(0);
    [ObservableProperty] private int _shipsDestroyed;
    [ObservableProperty] private int _shipsLost;
    [ObservableProperty] private int _zkillSoloKills;
    [ObservableProperty] private int _zkillSoloLosses;
    [ObservableProperty] private string _zkillRatiosLine = "";
    [ObservableProperty] private string _zkillPvpSummary = "";
    [ObservableProperty] private string _zkillCynoHint = "";
    [ObservableProperty] private long _iskDestroyed;
    [ObservableProperty] private long _iskLost;
    [ObservableProperty] private string _shipsHint = "";
    [ObservableProperty] private string _activityRegion = "";
    [ObservableProperty] private string _intelTip = "";
    [ObservableProperty] private bool _isFriendly;
    [ObservableProperty] private int[] _activityBuckets = new int[24];
    /// <summary>UTC hour → kill count from zKill <c>activity</c> (summed across weekdays).</summary>
    [ObservableProperty] private int[] _activityHourCounts = new int[24];
    [ObservableProperty] private string _standingForeground = EveStandingColors.DefaultText;
    [ObservableProperty] private string _standingDisplay = "";
    [ObservableProperty] private string _rowTooltip = "";
    /// <summary>True while zKill intel is on and this row has not finished a threat fetch (success or failed).</summary>
    [ObservableProperty] private bool _showThreatPendingPlaceholder;

    /// <summary>ESI name-to-ID lookup could not resolve this pilot name.</summary>
    [ObservableProperty] private bool _nameResolutionFailed;

    [ObservableProperty] private bool _tagFc;
    /// <summary>FC suggested by zKill (Monitor in top ships); not persisted in <see cref="CharacterTagStore"/>.</summary>
    [ObservableProperty] private bool _tagFcFromZkill;
    [ObservableProperty] private bool _tagCloakyCamper;
    [ObservableProperty] private bool _tagJfHunter;
    [ObservableProperty] private bool _tagGanker;

    [ObservableProperty] private Bitmap? _topShipBitmap1;
    [ObservableProperty] private Bitmap? _topShipBitmap2;
    [ObservableProperty] private Bitmap? _topShipBitmap3;
    [ObservableProperty] private string _topShipTooltip1 = "";
    [ObservableProperty] private string _topShipTooltip2 = "";
    [ObservableProperty] private string _topShipTooltip3 = "";

    /// <summary>True when any zKill top-hull icon is loaded (detail panel).</summary>
    public bool HasTopShipDetail =>
        TopShipBitmap1 != null || TopShipBitmap2 != null || TopShipBitmap3 != null;

    /// <summary>Effective contact standing when resolved; null when unknown or not logged in.</summary>
    public float? EffectiveStanding { get; private set; }

    /// <summary>Shows tier + score badge; inverse of <see cref="ShowThreatPendingPlaceholder"/>.</summary>
    public bool ShowThreatBadgeValues => !ShowThreatPendingPlaceholder;

    public bool IsCharacterResolved => CharacterId is > 0;

    public bool IsCharacterUnresolved => CharacterId is not > 0;

    public Uri? ZkillCharacterUri =>
        CharacterId is { } zid && zid > 0 ? EveIntelWebUrls.ZkillCharacter(zid) : null;

    public Uri? EveWhoCharacterUri =>
        CharacterId is { } eid && eid > 0 ? EveIntelWebUrls.EveWhoCharacter(eid) : null;

    public Uri? DotlanCorporationUri =>
        CorporationId is { } cid && cid > 0 ? EveIntelWebUrls.DotlanCorporation(cid) : null;

    public Uri? DotlanAllianceUri =>
        AllianceId is { } aid && aid > 0 ? EveIntelWebUrls.DotlanAlliance(aid) : null;

    public bool HasCorpTicker => !string.IsNullOrWhiteSpace(CorpTicker);

    public bool HasAllianceTicker => !string.IsNullOrWhiteSpace(AllianceTicker);

    public bool HasCorpDetail => !string.IsNullOrWhiteSpace(CorpDetailLine);

    public bool HasAllianceDetail => !string.IsNullOrWhiteSpace(AllianceDetailLine);

    public bool HasStandingDisplay => !string.IsNullOrWhiteSpace(StandingDisplay);

    public double ListNameOpacity => CharacterId is > 0 ? 1.0 : 0.55;

    public bool HasAnyCustomTag => TagFc || TagFcFromZkill || TagCloakyCamper || TagJfHunter || TagGanker;

    /// <summary>FC badge: manual tag or zKill Monitor heuristic.</summary>
    public bool ShowFcBadge => TagFc || TagFcFromZkill;

    /// <summary>Comma-separated tags for the detail panel (canonical order).</summary>
    public string CustomTagsLine
    {
        get
        {
            var parts = new List<string>(4);
            if (TagFc || TagFcFromZkill)
                parts.Add(CharacterTagIds.Fc);
            if (TagCloakyCamper)
                parts.Add(CharacterTagIds.CloakyCamper);
            if (TagJfHunter)
                parts.Add(CharacterTagIds.JfHunter);
            if (TagGanker)
                parts.Add(CharacterTagIds.Ganker);
            return string.Join(", ", parts);
        }
    }

    public void SetCustomTags(IReadOnlySet<string> tags)
    {
        TagFc = tags.Contains(CharacterTagIds.Fc);
        TagCloakyCamper = tags.Contains(CharacterTagIds.CloakyCamper);
        TagJfHunter = tags.Contains(CharacterTagIds.JfHunter);
        TagGanker = tags.Contains(CharacterTagIds.Ganker);
    }

    public void ClearStandingVisual()
    {
        EffectiveStanding = null;
        StandingForeground = EveStandingColors.DefaultText;
        StandingDisplay = "";
    }

    public void ApplyStanding(float effective)
    {
        EffectiveStanding = effective;
        StandingForeground = EveStandingColors.ForegroundForStanding(effective);
        StandingDisplay = EveStandingColors.FormatStanding(effective);
    }

    /// <summary>Cancel portrait load and dispose decoded bitmap (call when row leaves local).</summary>
    public void ReleaseResources()
    {
        _portraitLoadCts?.Cancel();
        _portraitLoadCts?.Dispose();
        _portraitLoadCts = null;
        if (PortraitBitmap is { } b)
        {
            PortraitBitmap = null;
            b.Dispose();
        }

        _topShipIconsCts?.Cancel();
        _topShipIconsCts?.Dispose();
        _topShipIconsCts = null;
        _topShipIconLoadGen++;
        ClearTopShipIconSlots();
    }

    /// <summary>Loads top-hull icons from zKill type IDs (uses <see cref="ShipIconCache"/>).</summary>
    public void ApplyTopShipsFromZkill(IReadOnlyList<int> typeIds, IReadOnlyList<int> kills)
    {
        _topShipIconsCts?.Cancel();
        _topShipIconsCts?.Dispose();
        _topShipIconsCts = null;
        _topShipIconLoadGen++;
        ClearTopShipIconSlots();

        if (typeIds is not { Count: > 0 })
            return;

        var gen = _topShipIconLoadGen;
        var cts = new CancellationTokenSource();
        _topShipIconsCts = cts;
        var ids = typeIds.Take(3).ToArray();
        _ = LoadTopShipIconsAsync(ids, kills ?? Array.Empty<int>(), gen, cts.Token);
    }

    private void ClearTopShipIconSlots()
    {
        TopShipTooltip1 = "";
        TopShipTooltip2 = "";
        TopShipTooltip3 = "";
        if (TopShipBitmap1 is { } a)
        {
            TopShipBitmap1 = null;
            a.Dispose();
        }

        if (TopShipBitmap2 is { } b2)
        {
            TopShipBitmap2 = null;
            b2.Dispose();
        }

        if (TopShipBitmap3 is { } b3)
        {
            TopShipBitmap3 = null;
            b3.Dispose();
        }
    }

    private async Task LoadTopShipIconsAsync(int[] typeIds, IReadOnlyList<int> kills, int gen, CancellationToken ct)
    {
        for (var slot = 0; slot < typeIds.Length && slot < 3; slot++)
        {
            var typeId = typeIds[slot];
            if (typeId <= 0)
                continue;

            var killCount = kills.Count > slot ? kills[slot] : 0;

            string? typeName;
            byte[]? bytes;
            try
            {
                var nameTask = _shipTypeNameCache.GetOrLoadAsync(typeId, ct);
                var bytesTask = _shipIconCache.GetOrLoadBytesAsync(typeId, ct);
                await Task.WhenAll(nameTask, bytesTask).ConfigureAwait(false);
                typeName = await nameTask.ConfigureAwait(false);
                bytes = await bytesTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (bytes == null || bytes.Length == 0)
                continue;
            if (ct.IsCancellationRequested || gen != _topShipIconLoadGen)
                return;

            var label = string.IsNullOrWhiteSpace(typeName) ? $"Type {typeId}" : typeName.Trim();
            var tooltip = killCount > 0
                ? $"{label} — {killCount} kills (zKill top hulls)"
                : $"{label} (zKill top hulls)";

            Bitmap? bitmap = null;
            try
            {
                var copy = bytes.ToArray();
                await using var ms = new MemoryStream(copy, writable: false);
                bitmap = await Task.Run(() => Bitmap.DecodeToWidth(ms, TopShipIconDecodeWidth), ct).ConfigureAwait(false);
            }
            catch
            {
                bitmap?.Dispose();
                continue;
            }

            if (ct.IsCancellationRequested || gen != _topShipIconLoadGen)
            {
                bitmap.Dispose();
                return;
            }

            var capturedSlot = slot;
            var capturedTip = tooltip;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested || gen != _topShipIconLoadGen)
                {
                    bitmap?.Dispose();
                    return;
                }

                switch (capturedSlot)
                {
                    case 0:
                        if (TopShipBitmap1 is { } o0)
                        {
                            TopShipBitmap1 = null;
                            o0.Dispose();
                        }

                        TopShipTooltip1 = capturedTip;
                        TopShipBitmap1 = bitmap;
                        break;
                    case 1:
                        if (TopShipBitmap2 is { } o1)
                        {
                            TopShipBitmap2 = null;
                            o1.Dispose();
                        }

                        TopShipTooltip2 = capturedTip;
                        TopShipBitmap2 = bitmap;
                        break;
                    default:
                        if (TopShipBitmap3 is { } o2)
                        {
                            TopShipBitmap3 = null;
                            o2.Dispose();
                        }

                        TopShipTooltip3 = capturedTip;
                        TopShipBitmap3 = bitmap;
                        break;
                }
            });
        }
    }

    partial void OnTopShipBitmap1Changed(Bitmap? value) => OnPropertyChanged(nameof(HasTopShipDetail));

    partial void OnTopShipBitmap2Changed(Bitmap? value) => OnPropertyChanged(nameof(HasTopShipDetail));

    partial void OnTopShipBitmap3Changed(Bitmap? value) => OnPropertyChanged(nameof(HasTopShipDetail));

    partial void OnNameChanged(string value) => RefreshRowTooltip();

    partial void OnCorpTickerChanged(string value)
    {
        OnPropertyChanged(nameof(HasCorpTicker));
        RefreshOrgDetailLines();
        RefreshRowTooltip();
    }

    partial void OnCorpNameChanged(string value)
    {
        RefreshOrgDetailLines();
        RefreshRowTooltip();
    }

    partial void OnAllianceTickerChanged(string value)
    {
        OnPropertyChanged(nameof(HasAllianceTicker));
        RefreshOrgDetailLines();
        RefreshRowTooltip();
    }

    partial void OnAllianceNameChanged(string value)
    {
        RefreshOrgDetailLines();
        RefreshRowTooltip();
    }

    partial void OnCorpDetailLineChanged(string value) => OnPropertyChanged(nameof(HasCorpDetail));

    partial void OnAllianceDetailLineChanged(string value) => OnPropertyChanged(nameof(HasAllianceDetail));

    private void RefreshOrgDetailLines()
    {
        CorpDetailLine = BuildOrgDetailLine(CorpName, CorpTicker);
        AllianceDetailLine = BuildOrgDetailLine(AllianceName, AllianceTicker);
    }

    private static string BuildOrgDetailLine(string fullName, string ticker)
    {
        var t = ticker.Trim();
        var n = fullName.Trim();
        if (n.Length > 0 && t.Length > 0)
            return $"{n} [{t}]";
        if (t.Length > 0)
            return $"[{t}]";
        return n.Length > 0 ? n : "";
    }

    partial void OnStandingDisplayChanged(string value)
    {
        OnPropertyChanged(nameof(HasStandingDisplay));
        RefreshRowTooltip();
    }

    partial void OnTagFcChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFcBadge));
        OnPropertyChanged(nameof(HasAnyCustomTag));
        OnPropertyChanged(nameof(CustomTagsLine));
        RefreshRowTooltip();
    }

    partial void OnTagFcFromZkillChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFcBadge));
        OnPropertyChanged(nameof(HasAnyCustomTag));
        OnPropertyChanged(nameof(CustomTagsLine));
        RefreshRowTooltip();
    }

    partial void OnTagCloakyCamperChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAnyCustomTag));
        OnPropertyChanged(nameof(CustomTagsLine));
        RefreshRowTooltip();
    }

    partial void OnTagJfHunterChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAnyCustomTag));
        OnPropertyChanged(nameof(CustomTagsLine));
        RefreshRowTooltip();
    }

    partial void OnTagGankerChanged(bool value)
    {
        OnPropertyChanged(nameof(HasAnyCustomTag));
        OnPropertyChanged(nameof(CustomTagsLine));
        RefreshRowTooltip();
    }

    partial void OnCharacterIdChanged(long? value)
    {
        if (value is > 0)
            NameResolutionFailed = false;
        OnPropertyChanged(nameof(IsCharacterResolved));
        OnPropertyChanged(nameof(IsCharacterUnresolved));
        OnPropertyChanged(nameof(ListNameOpacity));
        OnPropertyChanged(nameof(ZkillCharacterUri));
        OnPropertyChanged(nameof(EveWhoCharacterUri));
        RefreshRowTooltip();
    }

    partial void OnCorporationIdChanged(long? value) => OnPropertyChanged(nameof(DotlanCorporationUri));

    partial void OnAllianceIdChanged(long? value) => OnPropertyChanged(nameof(DotlanAllianceUri));

    partial void OnShowThreatPendingPlaceholderChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowThreatBadgeValues));
        RefreshRowTooltip();
    }

    partial void OnThreatScoreChanged(int value)
    {
        ThreatLabel = value >= ThreatTierColors.HighMin ? "HIGH" : value >= ThreatTierColors.MediumMin ? "MED" : "LOW";
        ThreatForeground = ThreatTierColors.ForegroundForScore(value);
        ThreatBadgeBackground = ThreatTierColors.BadgeBackgroundForScore(value);
        ThreatBadgeBorderBrush = ThreatTierColors.BadgeBorderForScore(value);
        RefreshRowTooltip();
    }

    partial void OnZkillCynoHintChanged(string value)
    {
        OnPropertyChanged(nameof(HasZkillCynoHint));
        RefreshRowTooltip();
    }

    partial void OnPortraitUrlChanged(string value)
    {
        _portraitLoadCts?.Cancel();
        _portraitLoadCts?.Dispose();
        _portraitLoadCts = null;
        if (PortraitBitmap is { } old)
        {
            PortraitBitmap = null;
            old.Dispose();
        }

        if (string.IsNullOrWhiteSpace(value))
            return;

        var cts = new CancellationTokenSource();
        _portraitLoadCts = cts;
        var url = value;
        _ = LoadPortraitAsync(url, cts.Token);
    }

    private async Task LoadPortraitAsync(string url, CancellationToken cancellationToken)
    {
        Bitmap? bitmap = null;
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SLH/0.1");
            await using var stream = await client.GetStreamAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
            await using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            ms.Position = 0;
            bitmap = await Task.Run(() => Bitmap.DecodeToWidth(ms, PortraitDecodeWidth), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            bitmap?.Dispose();
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (cancellationToken.IsCancellationRequested || url != PortraitUrl)
            {
                bitmap?.Dispose();
                return;
            }

            if (PortraitBitmap is { } prev)
            {
                PortraitBitmap = null;
                prev.Dispose();
            }

            PortraitBitmap = bitmap;
        });
    }

    private void RefreshRowTooltip()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Name))
            parts.Add(Name);
        if (!string.IsNullOrWhiteSpace(CorpTicker))
            parts.Add($"[{CorpTicker}]");
        if (!string.IsNullOrWhiteSpace(AllianceTicker))
            parts.Add($"[{AllianceTicker}]");
        if (!string.IsNullOrWhiteSpace(StandingDisplay))
            parts.Add($"Standing {StandingDisplay}");
        if (CharacterId is not > 0)
            parts.Add("Resolving character…");
        if (ShowThreatPendingPlaceholder)
            parts.Add("Threat …");
        else
            parts.Add($"Threat {ThreatLabel} ({ThreatScore})");
        if (HasZkillCynoHint)
            parts.Add("Cyno hint (zKill)");
        if (HasAnyCustomTag)
            parts.Add($"Tags: {CustomTagsLine}");
        RowTooltip = string.Join(Environment.NewLine, parts);
    }

    public bool HasZkillDetail => !string.IsNullOrWhiteSpace(ZkillRatiosLine);

    public bool HasZkillCynoHint => !string.IsNullOrWhiteSpace(ZkillCynoHint);

    partial void OnZkillRatiosLineChanged(string value) => OnPropertyChanged(nameof(HasZkillDetail));
}
