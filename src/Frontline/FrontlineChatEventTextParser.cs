using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace ai02;

public readonly record struct FrontlineChatEventParseResult(
    BattlefieldChatEventKind Kind,
    string ActorName,
    string TargetName,
    BattlefieldTacticalSide ActorSide,
    BattlefieldTacticalSide TargetSide,
    NodeOwnership? Ownership,
    string LocationId,
    string ObjectiveName,
    int? BattleHighLevel,
    int? BattleHighDelta);

public static class FrontlineChatEventTextParser
{
    private static readonly Regex LocationIdRegex = new(@"(?<!\d)(0?[1-9]|1[0-5])(?!\d)", RegexOptions.Compiled);
    private static readonly Regex BattleHighDeltaRegex = new(@"(?:\+|＋)\s*(\d{1,3})", RegexOptions.Compiled);
    private static readonly Regex BattleHighLevelRegex = new(
        @"(?:战意(?:高扬|高昂|狂热)?|戰意(?:高揚|高昂|狂熱)?|Battle\s+(?:High|Fever))\s*(?<level>[ⅠⅡⅢⅣⅤIVX]+|[1-5])?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex[] KillRegexes =
    {
        new(@"^(?<actor>.+?)(?:击倒了|擊倒了|击败了|擊敗了|击杀了|擊殺了|杀死了|殺死了|打倒了|讨伐了|討伐了|消灭了|消滅了|战胜了|戰勝了)\s*(?<target>.+?)[。.!！]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^(?<target>.+?)(?:被|遭到)\s*(?<actor>.+?)(?:击倒|擊倒|击败|擊敗|击杀|擊殺|杀死|殺死|打倒|讨伐|討伐|消灭|消滅|战胜|戰勝)[了]?[。.!！]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^(?<actor>.+?)\s+(?:defeated|defeats|knocked out|knocks out|slew|slays|killed|kills)\s+(?<target>.+?)[。.!！]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^(?<target>.+?)\s+was\s+(?:defeated|knocked out|slain|killed)\s+by\s+(?<actor>.+?)[。.!！]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    public static bool TryParse(
        string text,
        string sender,
        BattlefieldTacticalSide sourceSide,
        BattlefieldTacticalSide targetSide,
        out FrontlineChatEventParseResult result)
    {
        text = CleanText(text);
        sender = CleanText(sender);

        if (TryParseKill(text, sender, sourceSide, targetSide, out result))
            return true;

        if (TryParseBattleHigh(text, sender, sourceSide, out result))
            return true;

        if (TryParseObjective(text, out result))
            return true;

        result = default;
        return false;
    }

    private static bool TryParseKill(
        string text,
        string sender,
        BattlefieldTacticalSide sourceSide,
        BattlefieldTacticalSide targetSide,
        out FrontlineChatEventParseResult result)
    {
        result = default;
        if (!ContainsAny(text, "击倒", "擊倒", "击败", "擊敗", "击杀", "擊殺", "杀死", "殺死", "打倒", "讨伐", "討伐", "消灭", "消滅", "战胜", "戰勝", "defeat", "knock", "slain", "killed", "kills"))
            return false;

        string actor;
        string target;
        var actorSide = sourceSide;
        var parsedTargetSide = targetSide;

        if (TryMatchSimple(text, @"^你(?:击倒了|擊倒了|击败了|擊敗了|击杀了|擊殺了|杀死了|殺死了|打倒了|讨伐了|討伐了|消灭了|消滅了|战胜了|戰勝了)\s*(?<target>.+?)[。.!！]?$", out var youKillMatch))
        {
            actor = "你";
            target = CleanName(youKillMatch.Groups["target"].Value);
            actorSide = BattlefieldTacticalSide.Friendly;
        }
        else if (TryMatchSimple(text, @"^你(?:被|遭到)\s*(?<actor>.+?)(?:击倒|擊倒|击败|擊敗|击杀|擊殺|杀死|殺死|打倒|讨伐|討伐|消灭|消滅|战胜|戰勝)[了]?[。.!！]?$", out var youDiedMatch)
                 || TryMatchSimple(text, @"^You\s+(?:were|are)\s+(?:defeated|knocked out|slain|killed)\s+by\s+(?<actor>.+?)[。.!！]?$", out youDiedMatch))
        {
            actor = CleanName(youDiedMatch.Groups["actor"].Value);
            target = "你";
            actorSide = BattlefieldTacticalSide.Enemy;
            parsedTargetSide = BattlefieldTacticalSide.Friendly;
        }
        else if (TryMatchSimple(text, @"^You\s+(?:defeat|defeated|knock out|knocked out|slay|slew|kill|killed)\s+(?<target>.+?)[。.!！]?$", out var youKillEnglishMatch))
        {
            actor = "你";
            target = CleanName(youKillEnglishMatch.Groups["target"].Value);
            actorSide = BattlefieldTacticalSide.Friendly;
        }
        else if (!string.IsNullOrWhiteSpace(sender)
                 && TryMatchSimple(text, @"^(?:击倒了|擊倒了|击败了|擊敗了|击杀了|擊殺了|杀死了|殺死了|打倒了|讨伐了|討伐了|消灭了|消滅了|战胜了|戰勝了|defeated|defeats|knocked out|kills)\s*(?<target>.+?)[。.!！]?$", out var senderKillMatch))
        {
            actor = CleanName(sender);
            target = CleanName(senderKillMatch.Groups["target"].Value);
        }
        else
        {
            var match = KillRegexes.Select(regex => regex.Match(text)).FirstOrDefault(item => item.Success);
            if (match is null || !match.Success)
                return false;

            actor = CleanName(match.Groups["actor"].Value);
            target = CleanName(match.Groups["target"].Value);
        }

        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(target) || string.Equals(actor, target, StringComparison.OrdinalIgnoreCase))
            return false;

        actorSide = RefineSideFromName(actor, actorSide);
        parsedTargetSide = RefineSideFromName(target, parsedTargetSide);
        result = new FrontlineChatEventParseResult(
            BattlefieldChatEventKind.Kill,
            actor,
            target,
            actorSide,
            parsedTargetSide,
            null,
            string.Empty,
            string.Empty,
            null,
            null);
        return true;
    }

    private static bool TryParseBattleHigh(
        string text,
        string sender,
        BattlefieldTacticalSide sourceSide,
        out FrontlineChatEventParseResult result)
    {
        result = default;
        if (!ContainsAny(text, "战意", "戰意", "Battle High", "Battle Fever"))
            return false;

        var actor = ExtractBattleHighActor(text, sender);
        var actorSide = RefineSideFromName(actor, sourceSide);
        var level = TryParseBattleHighLevel(text, out var parsedLevel) ? parsedLevel : (int?)null;
        var delta = TryParseBattleHighDelta(text, out var parsedDelta) ? parsedDelta : (int?)null;

        result = new FrontlineChatEventParseResult(
            BattlefieldChatEventKind.BattleHigh,
            actor,
            string.Empty,
            actorSide,
            BattlefieldTacticalSide.Unknown,
            null,
            string.Empty,
            string.Empty,
            level,
            delta);
        return true;
    }

    private static bool TryParseObjective(string text, out FrontlineChatEventParseResult result)
    {
        result = default;
        if (!LooksLikeObjectiveMessage(text))
            return false;

        var kind = InferObjectiveKind(text);
        if (kind == BattlefieldChatEventKind.Unknown)
            return false;

        var ownership = TryInferOwnership(text, out var parsedOwnership) ? parsedOwnership : (NodeOwnership?)null;
        var locationId = LocationIdRegex.Match(text) is { Success: true } match ? NormalizeLocationId(match.Value) : string.Empty;
        var objectiveName = InferObjectiveName(text);
        var actorName = ownership.HasValue ? OwnershipText(ownership.Value) : string.Empty;

        result = new FrontlineChatEventParseResult(
            kind,
            actorName,
            string.Empty,
            BattlefieldTacticalSide.Unknown,
            BattlefieldTacticalSide.Unknown,
            ownership,
            locationId,
            objectiveName,
            null,
            null);
        return true;
    }

    private static string ExtractBattleHighActor(string text, string sender)
    {
        if (StartsWithAny(text, "你获得", "你取得", "你的战意", "你的戰意", "You gain", "You attain", "You reach"))
            return "你";

        var match = Regex.Match(
            text,
            @"^(?<actor>.+?)(?:获得|獲得|取得|得到了|提升|上升|变为|變為|达到|達到|gains?|attains?|reaches?|is now)",
            RegexOptions.IgnoreCase);
        if (match.Success)
            return CleanName(match.Groups["actor"].Value);

        return string.IsNullOrWhiteSpace(sender) ? string.Empty : CleanName(sender);
    }

    private static bool TryParseBattleHighLevel(string text, out int level)
    {
        level = 0;
        var match = BattleHighLevelRegex.Match(text);
        if (!match.Success)
            return false;

        var value = match.Groups["level"].Value.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value
            .Replace("Ⅰ", "I")
            .Replace("Ⅱ", "II")
            .Replace("Ⅲ", "III")
            .Replace("Ⅳ", "IV")
            .Replace("Ⅴ", "V")
            .ToUpperInvariant();

        if (int.TryParse(value, out level))
            return level is >= 1 and <= 5;

        level = value switch
        {
            "I" => 1,
            "II" => 2,
            "III" => 3,
            "IV" => 4,
            "V" => 5,
            _ => 0
        };
        return level > 0;
    }

    private static bool TryParseBattleHighDelta(string text, out int delta)
    {
        var match = BattleHighDeltaRegex.Match(text);
        if (match.Success && int.TryParse(match.Groups[1].Value, out delta))
            return true;

        delta = 0;
        return false;
    }

    private static BattlefieldChatEventKind InferObjectiveKind(string text)
    {
        if (ContainsAny(text, "争夺", "爭奪", "交战", "交戰", "正在被占领", "正在被占領", "contested"))
            return BattlefieldChatEventKind.ObjectiveContested;
        if (ContainsAny(text, "失去", "失守", "丢失", "丟失", "解除", "中立", "lost", "neutralized", "released"))
            return BattlefieldChatEventKind.ObjectiveLost;
        if (ContainsAny(text, "占领", "占領", "占据", "佔據", "控制", "夺取", "奪取", "攻下", "secured", "captured", "claimed", "occupied", "controlled"))
            return BattlefieldChatEventKind.ObjectiveCaptured;

        return BattlefieldChatEventKind.ObjectiveOther;
    }

    private static bool LooksLikeObjectiveMessage(string text)
        => ContainsAny(text,
            "据点", "據點", "目标点", "目標點", "战略目标", "戰略目標", "亚拉戈石文", "亞拉戈石文",
            "无垢的大地", "無垢的大地", "tomelith", "ovo", "base", "outpost", "control point", "strategic target");

    private static string InferObjectiveName(string text)
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

        return "地图目标";
    }

    private static bool TryInferOwnership(string text, out NodeOwnership ownership)
    {
        if (ContainsAny(text, "黑涡", "黑渦", "maelstrom"))
        {
            ownership = NodeOwnership.Maelstrom;
            return true;
        }

        if (ContainsAny(text, "双蛇", "雙蛇", "twin adder", "adders"))
        {
            ownership = NodeOwnership.TwinAdder;
            return true;
        }

        if (ContainsAny(text, "恒辉", "恆輝", "immortal flames", "flames"))
        {
            ownership = NodeOwnership.ImmortalFlames;
            return true;
        }

        if (ContainsAny(text, "中立", "未占领", "未占領", "neutral", "unclaimed"))
        {
            ownership = NodeOwnership.Neutral;
            return true;
        }

        ownership = NodeOwnership.Neutral;
        return false;
    }

    private static BattlefieldTacticalSide RefineSideFromName(string name, BattlefieldTacticalSide side)
    {
        if (side != BattlefieldTacticalSide.Unknown)
            return side;

        if (string.Equals(name, "你", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "you", StringComparison.OrdinalIgnoreCase))
        {
            return BattlefieldTacticalSide.Friendly;
        }

        return BattlefieldTacticalSide.Unknown;
    }

    private static string NormalizeLocationId(string value)
    {
        if (!int.TryParse(value, out var number))
            return value;

        return number is >= 0 and < 100 ? number.ToString("D2") : value;
    }

    private static string OwnershipText(NodeOwnership ownership)
        => ownership switch
        {
            NodeOwnership.Maelstrom => "黑涡团",
            NodeOwnership.TwinAdder => "双蛇党",
            NodeOwnership.ImmortalFlames => "恒辉队",
            NodeOwnership.Neutral => "中立",
            _ => "未知阵营"
        };

    private static bool TryMatchSimple(string text, string pattern, out Match match)
    {
        match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return match.Success;
    }

    private static string CleanText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string CleanName(string text)
    {
        var cleaned = CleanText(text)
            .Trim(' ', '　', '。', '.', '!', '！', ',', '，', ':', '：', ';', '；', '“', '”', '"', '\'', '【', '】', '『', '』');

        return Regex.Replace(cleaned, @"\s+(?:获得|獲得|击倒|擊倒|击败|擊敗|defeated|kills).*$", string.Empty, RegexOptions.IgnoreCase).Trim();
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

    private static bool StartsWithAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
