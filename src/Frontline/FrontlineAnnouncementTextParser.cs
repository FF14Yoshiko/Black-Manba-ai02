using System;
using System.Text.RegularExpressions;

namespace ai02;

public static class FrontlineAnnouncementTextParser
{
    private static readonly Regex CountdownRegex = new(@"(?<!\d)(\d{1,2}):([0-5]\d)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SecondsRegex = new(@"(?<!\d)(\d{1,3})\s*秒", RegexOptions.Compiled);
    private static readonly Regex VochesterLocationRegex = new(@"(?<!\d)(0?[1-9]|1[0-2])(?!\d)", RegexOptions.Compiled);

    public static bool TryParse(string source, string text, long observedAtTicks, out BattlefieldAnnouncementSnapshot announcement)
    {
        announcement = default;
        text = CleanText(text);
        if (!LooksLikeBattleAnnouncement(text))
            return false;

        var weather = InferWeather(text);
        var weatherName = WeatherName(weather);
        var locationId = TryParseLocationId(text, out var parsedLocationId) ? parsedLocationId : string.Empty;
        var rankName = InferRankName(text);
        var ownership = InferOwnership(text);
        var countdown = TryParseCountdownSeconds(text, out var seconds) ? seconds : (int?)null;
        var kind = InferKind(text, weather, ownership, locationId);
        if (kind == BattlefieldAnnouncementKind.Unknown)
            return false;

        if (countdown == null && kind == BattlefieldAnnouncementKind.WeatherWarning)
            countdown = 15;
        if (countdown == null && kind == BattlefieldAnnouncementKind.ObjectiveWarning)
            countdown = 30;

        announcement = new BattlefieldAnnouncementSnapshot(
            observedAtTicks,
            0,
            source,
            text,
            kind,
            weather,
            weatherName,
            locationId,
            rankName,
            ownership,
            countdown,
            countdown,
            BuildAnnouncementSummary(kind, weatherName, rankName, ownership, countdown, text));
        return true;
    }

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

    private static string BuildAnnouncementSummary(
        BattlefieldAnnouncementKind kind,
        string weatherName,
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
        if (ContainsAny(text, "无垢的大地", "無垢的大地", "ovoo"))
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

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
