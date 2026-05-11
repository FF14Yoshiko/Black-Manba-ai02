using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.STD;

namespace ai02;

public sealed unsafe class FrontlineAnnouncementReader : IDisposable
{
    private const int SampleIntervalMs = 1500;
    private const int FullAddonScanIntervalMs = 8000;
    private const int AnnouncementTtlMs = 180000;
    private const int DuplicateWindowMs = 30000;
    private const int MaxTextNodesPerAddon = 128;
    private const int MaxAtkValuesToInspect = 64;
    private const double SlowSampleThresholdMs = 8d;
    private const long SlowSampleLogCooldownMs = 15000;

    private static readonly Regex CountdownRegex = new(@"(?<!\d)(\d{1,2}):([0-5]\d)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SecondsRegex = new(@"(?<!\d)(\d{1,3})\s*秒", RegexOptions.Compiled);
    private static readonly Regex VochesterLocationRegex = new(@"(?<!\d)(0?[1-9]|1[0-2])(?!\d)", RegexOptions.Compiled);

    private static readonly string[] AnnouncementAddonNames =
    {
        "PvPFrontlineInfo",
        "PvPAnnounce",
        "PvPAnnouncement",
        "PvPNotice",
        "PvPInformation",
        "PvPFrontlineAnnounce",
        "PvPFrontlineAnnouncement",
        "PvPFrontlineNotice",
        "PvPFrontlineInformation",
        "PvPFrontlineGauge",
        "_ToDoList",
        "ContentsInfo",
        "ContentsInfoDetail",
        "BattleTalk",
        "_BattleTalk",
        "ScreenText",
        "_ScreenText",
        "AreaText",
        "_AreaText",
        "EventFade",
        "EventMessage",
        "_EventMessage",
    };

    private readonly IGameGui gameGui;
    private readonly IPluginLog log;
    private readonly List<AnnouncementEntry> entries = new();
    private readonly Dictionary<string, long> lastSeenByKey = new(StringComparer.Ordinal);

    private BattlefieldAnnouncementSituationSnapshot latestSnapshot = new();
    private long lastSampleTicks;
    private long lastFullAddonScanTicks = -FullAddonScanIntervalMs;
    private long lastSlowSampleLogTicks;
    private string? lastActiveAddonName;
    private bool disposed;

    public FrontlineAnnouncementReader(IGameGui gameGui, IPluginLog log)
    {
        this.gameGui = gameGui;
        this.log = log;
    }

    public BattlefieldAnnouncementSituationSnapshot GetSnapshot(bool isInFrontline, FrontlineMapType mapType)
    {
        if (disposed)
            return new BattlefieldAnnouncementSituationSnapshot { SummaryText = "战场通告读取已停止" };

        var now = Environment.TickCount64;
        if (now - lastSampleTicks >= SampleIntervalMs)
        {
            lastSampleTicks = now;
            Sample(isInFrontline, mapType, now);
            latestSnapshot = BuildSnapshot(now);
        }

        return latestSnapshot;
    }

    public string[] GetAddonDebugLines()
    {
        if (disposed)
            return Array.Empty<string>();

        var lines = new List<string>(AnnouncementAddonNames.Length);
        foreach (var addonName in AnnouncementAddonNames)
        {
            try
            {
                var addon = TryGetAddon(addonName);
                if (addon == null)
                    continue;

                var sizeText = addon->RootNode == null
                    ? "root=null"
                    : $"size={addon->RootNode->Width}x{addon->RootNode->Height}";
                lines.Add($"{addonName}: visible={addon->IsVisible} pos=({addon->X},{addon->Y}) {sizeText}");
            }
            catch (Exception ex)
            {
                log.Verbose(ex, "[FrontlineAnnouncementReader] Failed to inspect addon {Name}", addonName);
            }
        }

        if (lines.Count == 0)
            lines.Add("未找到候选战场通告 Addon。");

        return lines.ToArray();
    }

    public void Dispose()
    {
        disposed = true;
        entries.Clear();
        lastSeenByKey.Clear();
        latestSnapshot = new BattlefieldAnnouncementSituationSnapshot { SummaryText = "战场通告读取已停止" };
    }

    private void Sample(bool isInFrontline, FrontlineMapType mapType, long now)
    {
        if (!isInFrontline && mapType == FrontlineMapType.Unknown)
        {
            Prune(now);
            return;
        }

        var startedTimestamp = Stopwatch.GetTimestamp();
        var sawVisibleAddon = false;
        var sawAnnouncement = false;
        var sawAddonAnnouncement = false;

        if (TrySampleStructuredSources(now))
            sawAnnouncement = true;

        if (TrySampleAddon(lastActiveAddonName, now, ref sawVisibleAddon))
            sawAddonAnnouncement = true;

        if (!sawAddonAnnouncement && now - lastFullAddonScanTicks >= FullAddonScanIntervalMs)
        {
            lastFullAddonScanTicks = now;
            foreach (var addonName in AnnouncementAddonNames)
            {
                if (string.Equals(addonName, lastActiveAddonName, StringComparison.Ordinal))
                    continue;

                if (TrySampleAddon(addonName, now, ref sawVisibleAddon))
                {
                    sawAddonAnnouncement = true;
                    break;
                }
            }
        }

        if (sawAddonAnnouncement)
            sawAnnouncement = true;

        if (!sawVisibleAddon && !sawAddonAnnouncement)
            lastActiveAddonName = null;

        Prune(now);
        LogSlowSample(now, startedTimestamp, sawVisibleAddon, sawAnnouncement);
    }

    private bool TrySampleStructuredSources(long now)
    {
        var matched = false;

        try
        {
            if (TrySampleToDoListArray(now))
                matched = true;
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[FrontlineAnnouncementReader] Failed to sample ToDoListArray announcements.");
        }

        try
        {
            if (TrySampleDirectorTodos(now))
                matched = true;
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[FrontlineAnnouncementReader] Failed to sample DirectorTodo announcements.");
        }

        return matched;
    }

    private bool TrySampleToDoListArray(long now)
    {
        var strings = ToDoListStringArray.Instance();
        if (strings == null)
            return false;

        var texts = new List<TextEntry>(16);
        AddStructuredText(texts, 0xE000, strings->ActiveDutyTitle.ToString());
        AddStructuredText(texts, 0xE001, strings->DutyTitleText.ToString());
        AddStructuredText(texts, 0xE002, strings->DutyTimer.ToString());

        var numbers = ToDoListNumberArray.Instance();
        var count = numbers == null ? 10 : Math.Clamp(numbers->DutyObjectiveCount, 0, 10);
        var objectiveTexts = strings->DutyObjectives;
        var objectiveTimers = strings->DutyTimers;
        for (var i = 0; i < count; i++)
        {
            var text = CleanText(objectiveTexts[i].ToString());
            var timer = CleanText(objectiveTimers[i].ToString());
            AddStructuredText(texts, (uint)(0xE010 + i), text);
            AddStructuredText(texts, (uint)(0xE030 + i), $"{text} {timer}".Trim());
        }

        return TryCollectAnnouncementsFromTexts("ToDoListArray", texts, now);
    }

    private bool TrySampleDirectorTodos(long now)
    {
        var framework = EventFramework.Instance();
        if (framework == null)
            return false;

        var matched = false;
        var contentDirector = framework->GetContentDirector();
        if (contentDirector != null)
            matched |= TrySampleDirectorTodoVector("ContentDirector", contentDirector->GetDirectorTodos(), now);

        var instanceDirector = framework->GetInstanceContentDirector();
        if (instanceDirector != null && (nint)instanceDirector != (nint)contentDirector)
            matched |= TrySampleDirectorTodoVector("InstanceContentDirector", instanceDirector->GetDirectorTodos(), now);

        return matched;
    }

    private bool TrySampleDirectorTodoVector(string source, StdVector<DirectorTodo>* todos, long now)
    {
        if (todos == null)
            return false;

        var texts = new List<TextEntry>(16);
        var span = todos->AsSpan();
        var count = Math.Clamp(todos->Count, 0, 32);
        for (var i = 0; i < count && i < span.Length; i++)
        {
            ref var todo = ref span[i];
            if (!todo.Enabled)
                continue;

            var text = CleanText(todo.Text.ToString());
            var valueText = todo.NeededCount > 0
                ? $"{text} {todo.CurrentCount}/{todo.NeededCount}"
                : text;
            AddStructuredText(texts, (uint)i, valueText);
        }

        return TryCollectAnnouncementsFromTexts(source, texts, now);
    }

    private AtkUnitBase* TryGetAddon(string addonName)
    {
        var addonPtr = gameGui.GetAddonByName(addonName, 1);
        if (addonPtr == IntPtr.Zero)
            addonPtr = gameGui.GetAddonByName(addonName, 0);

        return addonPtr == IntPtr.Zero ? null : (AtkUnitBase*)addonPtr.Address;
    }

    private void AddAnnouncement(BattlefieldAnnouncementSnapshot announcement, long now)
    {
        var key = NormalizeForDedup(announcement.Text);
        if (lastSeenByKey.TryGetValue(key, out var lastSeen) && now - lastSeen < DuplicateWindowMs)
            return;

        lastSeenByKey[key] = now;
        entries.Add(new AnnouncementEntry(announcement.Text, announcement.Source, announcement.Kind, announcement.Weather, announcement.WeatherName, announcement.LocationId, announcement.RankName, announcement.Ownership, announcement.CountdownSeconds, now));
    }

    private void Prune(long now)
    {
        entries.RemoveAll(entry => now - entry.ObservedAtTicks > AnnouncementTtlMs);
        List<string>? expiredKeys = null;
        foreach (var pair in lastSeenByKey)
        {
            if (now - pair.Value <= AnnouncementTtlMs)
                continue;

            expiredKeys ??= new List<string>();
            expiredKeys.Add(pair.Key);
        }

        if (expiredKeys == null)
            return;

        foreach (var key in expiredKeys)
            lastSeenByKey.Remove(key);
    }

    private bool TrySampleAddon(string? addonName, long now, ref bool sawVisibleAddon)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return false;

        try
        {
            var addon = TryGetAddon(addonName);
            if (addon == null || !addon->IsVisible)
                return false;

            sawVisibleAddon = true;
            if (!TryCollectAnnouncements(addonName, addon, now))
                return false;

            lastActiveAddonName = addonName;
            return true;
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[FrontlineAnnouncementReader] Failed to sample addon {Name}", addonName);
            return false;
        }
    }

    private bool TryCollectAnnouncements(string addonName, AtkUnitBase* addon, long now)
    {
        var texts = new List<TextEntry>(64);
        CollectAddonTexts(addon, texts);

        return TryCollectAnnouncementsFromTexts(addonName, texts, now);
    }

    private bool TryCollectAnnouncementsFromTexts(string source, IReadOnlyList<TextEntry> texts, long now)
    {
        HashSet<string>? seenTexts = null;
        var matched = false;
        for (var i = 0; i < texts.Count; i++)
        {
            var rawText = CleanText(texts[i].Text);
            if (rawText.Length == 0)
                continue;

            seenTexts ??= new HashSet<string>(StringComparer.Ordinal);
            if (!seenTexts.Add(rawText))
                continue;

            if (!TryCreateAnnouncement(source, rawText, now, out var announcement))
                continue;

            matched = true;
            AddAnnouncement(announcement, now);
        }

        return matched;
    }

    private void LogSlowSample(long now, long startedTimestamp, bool sawVisibleAddon, bool sawAnnouncement)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        if (elapsedMs < SlowSampleThresholdMs || now - lastSlowSampleLogTicks < SlowSampleLogCooldownMs)
            return;

        lastSlowSampleLogTicks = now;
        log.Debug(
            "[FrontlineAnnouncementReader] Sampling took {ElapsedMs:F1} ms (visible={SawVisibleAddon}, matched={SawAnnouncement}, active={ActiveAddon}).",
            elapsedMs,
            sawVisibleAddon,
            sawAnnouncement,
            lastActiveAddonName ?? "<none>");
    }

    private BattlefieldAnnouncementSituationSnapshot BuildSnapshot(long now)
    {
        var recentList = new List<BattlefieldAnnouncementSnapshot>(Math.Min(entries.Count, 16));
        for (var i = entries.Count - 1; i >= 0 && recentList.Count < 16; i--)
            recentList.Add(entries[i].ToSnapshot(now));

        var recent = recentList.ToArray();
        var latest = recent.Length > 0 ? recent[0] : (BattlefieldAnnouncementSnapshot?)null;
        BattlefieldAnnouncementSnapshot? latestWeather = null;
        BattlefieldAnnouncementSnapshot? latestObjective = null;
        foreach (var item in recent)
        {
            if (!latestWeather.HasValue && item.Weather != BattlefieldWeatherKind.Unknown)
                latestWeather = item;
            if (!latestObjective.HasValue
                && item.Kind is BattlefieldAnnouncementKind.ObjectiveWarning
                    or BattlefieldAnnouncementKind.ObjectiveAvailable
                    or BattlefieldAnnouncementKind.ObjectiveControlled
                    or BattlefieldAnnouncementKind.ObjectiveReleased
                    or BattlefieldAnnouncementKind.ObjectiveOther)
            {
                latestObjective = item;
            }
        }

        var (currentWeather, currentWeatherName, weatherStateText, weatherRemaining) = ResolveWeatherState(latestWeather, now);
        return new BattlefieldAnnouncementSituationSnapshot
        {
            IsAvailable = recent.Length > 0,
            RecentAnnouncements = recent,
            LatestAnnouncement = latest,
            LatestWeatherAnnouncement = latestWeather,
            LatestObjectiveAnnouncement = latestObjective,
            CurrentWeather = currentWeather,
            CurrentWeatherName = currentWeatherName,
            WeatherStateText = weatherStateText,
            WeatherRemainingSeconds = weatherRemaining,
            SourceText = recent.Length > 0 ? string.Join(", ", recent.Select(item => item.Source).Distinct().Take(3)) : "战场通告窗口",
            SummaryText = BuildSummaryText(recent, latestWeather, latestObjective, weatherStateText)
        };
    }

    private static bool TryCreateAnnouncement(string source, string text, long now, out BattlefieldAnnouncementSnapshot announcement)
        => FrontlineAnnouncementTextParser.TryParse(source, text, now, out announcement);

    private static bool LooksLikeBattleAnnouncement(string text)
        => ContainsAny(text,
            "战场通告", "戰場通告", "玩家对战", "玩家對戰",
            "战略目标", "戰略目標", "目标点", "目標點", "据点", "據點",
            "亚拉戈石文", "亞拉戈石文", "石文", "冰封", "冰柱", "冰块", "冰塊",
            "无垢的大地", "無垢的大地", "无垢", "無垢", "契约", "契約",
            "天气", "天候", "小雪", "极光", "極光", "控制", "占领", "占領", "失效",
            "PvPFrontlineInfo", "frontline info", "battlefield", "strategic target", "tomelith", "ovoo", "weather", "aurora", "snow");

    private static BattlefieldAnnouncementKind InferKind(string text, BattlefieldWeatherKind weather, NodeOwnership? ownership, string locationId)
    {
        if (weather != BattlefieldWeatherKind.Unknown)
        {
            if (ContainsAny(text, "即将", "將", "将", "预告", "預告", "倒计时", "倒計時", "soon", "about to"))
                return BattlefieldAnnouncementKind.WeatherWarning;
            if (ContainsAny(text, "结束", "結束", "消失", "停止", "恢复", "恢復", "ended", "cleared"))
                return BattlefieldAnnouncementKind.WeatherEnded;
            return BattlefieldAnnouncementKind.WeatherStarted;
        }

        var mentionsObjective = ContainsAny(text,
            "战略目标", "戰略目標", "目标点", "目標點", "据点", "據點",
            "亚拉戈石文", "亞拉戈石文", "石文", "冰封", "冰柱", "冰块", "冰塊",
            "无垢的大地", "無垢的大地", "无垢", "無垢", "契约", "契約",
            "strategic target", "tomelith", "ovoo", "objective");
        if (!mentionsObjective)
            return BattlefieldAnnouncementKind.Unknown;

        if (ContainsAny(text, "失效", "停止", "解除", "结束", "結束", "消失", "耗尽", "耗盡", "破坏", "破壞", "deactivated", "ended", "destroyed"))
            return BattlefieldAnnouncementKind.ObjectiveReleased;

        var availableCue = ContainsAny(text,
            "可控制", "可占领", "可占領", "可抢占", "可搶占", "可契约", "可契約",
            "已启动", "启动", "啟動", "激活", "出现", "出現", "active", "available");
        var controlledCue = ContainsAny(text, "控制", "占领", "占領", "契约", "契約", "secured", "controlled", "claimed");
        if (controlledCue
            && !availableCue
            && (ownership.HasValue || ContainsAny(text, "已", "成功", "被", "secured", "controlled", "claimed")))
        {
            return BattlefieldAnnouncementKind.ObjectiveControlled;
        }

        if (availableCue)
            return BattlefieldAnnouncementKind.ObjectiveAvailable;
        if (ContainsAny(text, "即将", "將", "将", "过渡", "轉換", "转换", "预告", "預告", "倒计时", "倒計時", "soon", "will"))
            return BattlefieldAnnouncementKind.ObjectiveWarning;

        return string.IsNullOrWhiteSpace(locationId)
            ? BattlefieldAnnouncementKind.ObjectiveOther
            : BattlefieldAnnouncementKind.ObjectiveOther;
    }

    private static (BattlefieldWeatherKind Kind, string Name, string StateText, int? RemainingSeconds) ResolveWeatherState(BattlefieldAnnouncementSnapshot? latestWeather, long now)
    {
        if (!latestWeather.HasValue)
            return (BattlefieldWeatherKind.Unknown, string.Empty, "天气通告尚未读取", null);

        var item = latestWeather.Value;
        var ageSeconds = Math.Max(0, (int)(item.AgeMs / 1000));
        var duration = item.Weather switch
        {
            BattlefieldWeatherKind.Snow => 300,
            BattlefieldWeatherKind.Aurora => 150,
            _ => 0
        };

        return item.Kind switch
        {
            BattlefieldAnnouncementKind.WeatherWarning => (
                item.Weather,
                item.WeatherName,
                $"{item.WeatherName}即将开始",
                item.CountdownSeconds.HasValue ? Math.Max(0, item.CountdownSeconds.Value - ageSeconds) : null),
            BattlefieldAnnouncementKind.WeatherStarted => (
                item.Weather,
                item.WeatherName,
                $"{item.WeatherName}进行中",
                duration > 0 ? Math.Max(0, duration - ageSeconds) : null),
            BattlefieldAnnouncementKind.WeatherEnded => (
                BattlefieldWeatherKind.Unknown,
                string.Empty,
                $"{item.WeatherName}已结束",
                null),
            _ => (item.Weather, item.WeatherName, $"{item.WeatherName}状态未知", null),
        };
    }

    private static string BuildSummaryText(
        IReadOnlyList<BattlefieldAnnouncementSnapshot> recent,
        BattlefieldAnnouncementSnapshot? latestWeather,
        BattlefieldAnnouncementSnapshot? latestObjective,
        string weatherStateText)
    {
        if (recent.Count == 0)
            return "战场通告尚未读取；如果通告窗口未出现，会等待下一条天气/目标事件。";

        var parts = new List<string> { weatherStateText };
        if (latestObjective.HasValue)
            parts.Add($"最新目标：{latestObjective.Value.SummaryText}");

        parts.Add($"最近通告 {recent.Count} 条");
        return string.Join("；", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildAnnouncementSummary(
        BattlefieldAnnouncementKind kind,
        string weatherName,
        string locationId,
        string rankName,
        NodeOwnership? ownership,
        int? countdown,
        string text)
    {
        var target = InferObjectiveDisplayName(text);
        if (!string.IsNullOrWhiteSpace(rankName))
            target = $"{rankName}级{target}";

        return kind switch
        {
            BattlefieldAnnouncementKind.WeatherWarning => $"{weatherName}预告{FormatCountdown(countdown)}",
            BattlefieldAnnouncementKind.WeatherStarted => $"{weatherName}开始",
            BattlefieldAnnouncementKind.WeatherEnded => $"{weatherName}结束",
            BattlefieldAnnouncementKind.ObjectiveWarning => $"{target}即将刷新{FormatCountdown(countdown)}",
            BattlefieldAnnouncementKind.ObjectiveAvailable => $"{target}可控制",
            BattlefieldAnnouncementKind.ObjectiveControlled => $"{OwnershipText(ownership)}控制{target}",
            BattlefieldAnnouncementKind.ObjectiveReleased => $"{target}失效/解除",
            _ => text
        };
    }

    private static string InferObjectiveDisplayName(string text)
    {
        if (ContainsAny(text, "亚拉戈石文", "亞拉戈石文", "tomelith"))
            return "亚拉戈石文";
        if (ContainsAny(text, "无垢的大地", "無垢的大地", "ovo"))
            return "无垢的大地";
        if (ContainsAny(text, "战略目标", "戰略目標", "strategic target"))
            return "战略目标点";
        if (ContainsAny(text, "目标点", "目標點", "control point"))
            return "目标点";
        if (ContainsAny(text, "据点", "據點", "base", "outpost"))
            return "据点";

        return "目标点";
    }

    private static string FormatCountdown(int? seconds)
        => seconds.HasValue ? $"（{seconds.Value}秒）" : string.Empty;

    private static BattlefieldWeatherKind InferWeather(string text)
    {
        if (ContainsAny(text, "小雪", "snow"))
            return BattlefieldWeatherKind.Snow;
        if (ContainsAny(text, "极光", "極光", "aurora"))
            return BattlefieldWeatherKind.Aurora;

        return BattlefieldWeatherKind.Unknown;
    }

    private static string WeatherName(BattlefieldWeatherKind weather)
        => weather switch
        {
            BattlefieldWeatherKind.Snow => "小雪",
            BattlefieldWeatherKind.Aurora => "极光",
            _ => string.Empty
        };

    private static bool TryParseLocationId(string text, out string locationId)
    {
        var match = VochesterLocationRegex.Match(text);
        locationId = match.Success ? NormalizeLocationId(match.Groups[1].Value) : string.Empty;
        return match.Success;
    }

    private static string NormalizeLocationId(string value)
    {
        if (!int.TryParse(value, out var number))
            return value;

        return number is >= 0 and < 100 ? number.ToString("D2") : value;
    }

    private static string InferRankName(string text)
    {
        if (ContainsRank(text, "S"))
            return "S";
        if (ContainsRank(text, "A"))
            return "A";
        if (ContainsRank(text, "B"))
            return "B";

        return string.Empty;
    }

    private static bool ContainsRank(string text, string rank)
        => ContainsAny(text, $"{rank}级", $"{rank} 级", $"{rank}級", $"{rank} 級", $"等级{rank}", $"等級{rank}", $"rank {rank}");

    private static NodeOwnership? InferOwnership(string text)
    {
        if (ContainsAny(text, "黑涡", "黑渦", "maelstrom"))
            return NodeOwnership.Maelstrom;
        if (ContainsAny(text, "双蛇", "雙蛇", "twin adder", "adders"))
            return NodeOwnership.TwinAdder;
        if (ContainsAny(text, "恒辉", "恆輝", "immortal flames", "flames"))
            return NodeOwnership.ImmortalFlames;
        if (ContainsAny(text, "中立", "未占领", "未占領", "neutral"))
            return NodeOwnership.Neutral;

        return null;
    }

    private static string OwnershipText(NodeOwnership? ownership)
        => ownership switch
        {
            NodeOwnership.Maelstrom => "黑涡团",
            NodeOwnership.TwinAdder => "双蛇党",
            NodeOwnership.ImmortalFlames => "恒辉队",
            NodeOwnership.Neutral => "中立",
            _ => "未知阵营"
        };

    private static bool TryParseCountdownSeconds(string text, out int seconds)
    {
        var countdownMatch = CountdownRegex.Match(text);
        if (countdownMatch.Success)
        {
            seconds = int.Parse(countdownMatch.Groups[1].Value) * 60 + int.Parse(countdownMatch.Groups[2].Value);
            return true;
        }

        var secondsMatch = SecondsRegex.Match(text);
        if (secondsMatch.Success && int.TryParse(secondsMatch.Groups[1].Value, out seconds))
            return true;

        seconds = 0;
        return false;
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string NormalizeForDedup(string text)
        => CleanText(text)
            .Replace(" ", string.Empty)
            .Replace("\u3000", string.Empty)
            .Replace("，", ",")
            .Replace("。", ".")
            .ToLowerInvariant();

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void AddStructuredText(List<TextEntry> texts, uint nodeId, string? text)
    {
        var cleaned = CleanText(text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(cleaned))
            texts.Add(new TextEntry(nodeId, cleaned));
    }

    private void CollectAddonTexts(AtkUnitBase* addon, List<TextEntry> texts)
    {
        var visitedNodes = new HashSet<nint>();

        if (addon->RootNode != null)
            CollectTextNodes(addon->RootNode, texts, visitedNodes, 0);

        CollectTextNodesFromUldManager(&addon->UldManager, texts, visitedNodes, 0);
        CollectAtkValueTexts(addon, texts);
    }

    private void CollectTextNodes(
        AtkResNode* node,
        List<TextEntry> texts,
        HashSet<nint> visitedNodes,
        int depth)
    {
        if (node == null || depth > 16 || texts.Count >= MaxTextNodesPerAddon)
            return;

        if (!visitedNodes.Add((nint)node))
            return;

        if (node->Type == NodeType.Text)
        {
            try
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    texts.Add(new TextEntry(node->NodeId, text));
            }
            catch (Exception ex)
            {
                log.Verbose(ex, "[FrontlineAnnouncementReader] Failed to read text node.");
            }
        }
        else if (node->Type == NodeType.Component)
        {
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component != null)
                CollectTextNodesFromUldManager(&componentNode->Component->UldManager, texts, visitedNodes, depth + 1);
        }

        var child = node->ChildNode;
        var guard = 0;
        while (child != null && guard++ < 512 && texts.Count < MaxTextNodesPerAddon)
        {
            CollectTextNodes(child, texts, visitedNodes, depth + 1);
            child = child->NextSiblingNode;
        }
    }

    private void CollectTextNodesFromUldManager(
        AtkUldManager* uldManager,
        List<TextEntry> texts,
        HashSet<nint> visitedNodes,
        int depth)
    {
        if (uldManager == null || uldManager->NodeList == null || depth > 16)
            return;

        var count = Math.Min((int)uldManager->NodeListCount, MaxTextNodesPerAddon);
        for (var i = 0; i < count && texts.Count < MaxTextNodesPerAddon; i++)
        {
            var childNode = uldManager->NodeList[i];
            if (childNode != null)
                CollectTextNodes(childNode, texts, visitedNodes, depth + 1);
        }
    }

    private static void CollectAtkValueTexts(AtkUnitBase* addon, List<TextEntry> texts)
    {
        if (addon->AtkValues == null)
            return;

        var count = Math.Min((int)addon->AtkValuesCount, MaxAtkValuesToInspect);
        for (var i = 0; i < count && texts.Count < MaxTextNodesPerAddon; i++)
        {
            var value = addon->AtkValues[i];
            if (TryReadAtkValueString(value, out var text) && !string.IsNullOrWhiteSpace(text))
                texts.Add(new TextEntry((uint)(0xF000 + i), text));
        }
    }

    private static bool TryReadAtkValueString(AtkValue value, out string text)
    {
        var type = value.Type & AtkValueType.TypeMask;
        if (type is not (AtkValueType.String or AtkValueType.WideString or AtkValueType.String8))
        {
            text = string.Empty;
            return false;
        }

        try
        {
            text = value.GetValueAsString();
            return true;
        }
        catch
        {
            text = string.Empty;
            return false;
        }
    }

    private readonly record struct TextEntry(uint NodeId, string Text);

    private readonly record struct AnnouncementEntry(
        string Text,
        string Source,
        BattlefieldAnnouncementKind Kind,
        BattlefieldWeatherKind Weather,
        string WeatherName,
        string LocationId,
        string RankName,
        NodeOwnership? Ownership,
        int? CountdownSeconds,
        long ObservedAtTicks)
    {
        public BattlefieldAnnouncementSnapshot ToSnapshot(long now)
        {
            var ageMs = Math.Max(0, now - ObservedAtTicks);
            var remaining = CountdownSeconds.HasValue
                ? Math.Max(0, CountdownSeconds.Value - (int)(ageMs / 1000))
                : (int?)null;
            return new BattlefieldAnnouncementSnapshot(
                ObservedAtTicks,
                ageMs,
                Source,
                Text,
                Kind,
                Weather,
                WeatherName,
                LocationId,
                RankName,
                Ownership,
                CountdownSeconds,
                remaining,
                BuildAnnouncementSummary(Kind, WeatherName, LocationId, RankName, Ownership, remaining ?? CountdownSeconds, Text));
        }
    }
}
