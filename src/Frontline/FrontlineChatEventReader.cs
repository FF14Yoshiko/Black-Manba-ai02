using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class FrontlineChatEventReader : IDisposable
{
    private const int EventTtlMs = 180000;
    private const int RecentWindowMs = 30000;
    private const int DuplicateWindowMs = 2000;

    private static readonly HashSet<uint> FrontlineTerritoryIds = new()
    {
        376, 431, 554, 888, 1273, 1313
    };

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

    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly List<ChatEventEntry> entries = new();
    private readonly Dictionary<string, long> lastSeenByKey = new(StringComparer.Ordinal);
    private bool disposed;

    public FrontlineChatEventReader(IChatGui chatGui, IClientState clientState, IPluginLog log)
    {
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.log = log;

        chatGui.ChatMessage += OnChatMessage;
    }

    public BattlefieldChatEventSituationSnapshot GetSnapshot(bool isInFrontline, byte? localBattalion)
    {
        if (disposed)
            return new BattlefieldChatEventSituationSnapshot { SummaryText = "聊天事件读取已停止" };

        var now = Environment.TickCount64;
        if (!isInFrontline && !IsKnownFrontlineTerritory(clientState.TerritoryType))
        {
            Prune(now);
            return BuildSnapshot(now, localBattalion, "等待前线聊天事件");
        }

        Prune(now);
        return BuildSnapshot(now, localBattalion, "IChatGui/ChatMessage");
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        chatGui.ChatMessage -= OnChatMessage;
        entries.Clear();
        lastSeenByKey.Clear();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (disposed || !IsKnownFrontlineTerritory(clientState.TerritoryType))
            return;

        try
        {
            var now = Environment.TickCount64;
            var sender = CleanText(message.Sender.TextValue);
            var text = CleanText(message.Message.TextValue);
            if (string.IsNullOrWhiteSpace(text))
                return;

            if (!TryCreateEvent(
                    text,
                    sender,
                    message.LogKind,
                    message.SourceKind,
                    message.TargetKind,
                    now,
                    out var entry))
            {
                return;
            }

            AddEvent(entry, now);
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[FrontlineChatEventReader] Failed to parse chat message.");
        }
    }

    private void AddEvent(ChatEventEntry entry, long now)
    {
        var key = NormalizeForDedup($"{entry.Kind}:{entry.Text}:{entry.ActorName}:{entry.TargetName}:{entry.LocationId}");
        if (lastSeenByKey.TryGetValue(key, out var lastSeen) && now - lastSeen < DuplicateWindowMs)
            return;

        lastSeenByKey[key] = now;
        entries.Add(entry);
        Prune(now);
    }

    private void Prune(long now)
    {
        entries.RemoveAll(entry => now - entry.ObservedAtTicks > EventTtlMs);
        foreach (var key in lastSeenByKey.Keys.ToArray())
        {
            if (now - lastSeenByKey[key] > EventTtlMs)
                lastSeenByKey.Remove(key);
        }
    }

    private BattlefieldChatEventSituationSnapshot BuildSnapshot(long now, byte? localBattalion, string sourceText)
    {
        if (entries.Count == 0)
        {
            return new BattlefieldChatEventSituationSnapshot
            {
                SourceText = sourceText,
                SummaryText = BuildSummaryText(Array.Empty<BattlefieldChatEventSnapshot>(), Array.Empty<BattlefieldChatEventSnapshot>(), sourceText)
            };
        }

        var recentList = new List<BattlefieldChatEventSnapshot>(Math.Min(entries.Count, 24));
        for (var i = entries.Count - 1; i >= 0 && recentList.Count < 24; i--)
            recentList.Add(entries[i].ToSnapshot(now, localBattalion));

        var recent = recentList.ToArray();
        BattlefieldChatEventSnapshot? latestKill = null;
        BattlefieldChatEventSnapshot? latestBattleHigh = null;
        BattlefieldChatEventSnapshot? latestObjective = null;
        var windowList = new List<BattlefieldChatEventSnapshot>(recent.Length);
        foreach (var item in recent)
        {
            if (!latestKill.HasValue && item.Kind == BattlefieldChatEventKind.Kill)
                latestKill = item;
            if (!latestBattleHigh.HasValue && item.Kind == BattlefieldChatEventKind.BattleHigh)
                latestBattleHigh = item;
            if (!latestObjective.HasValue && IsObjectiveEvent(item.Kind))
                latestObjective = item;
            if (item.AgeMs <= RecentWindowMs)
                windowList.Add(item);
        }

        var window = windowList.ToArray();

        return new BattlefieldChatEventSituationSnapshot
        {
            IsAvailable = recent.Length > 0,
            RecentEvents = recent,
            LatestKillEvent = latestKill,
            LatestBattleHighEvent = latestBattleHigh,
            LatestObjectiveEvent = latestObjective,
            FriendlyKillsRecent = window.Count(item => item.Kind == BattlefieldChatEventKind.Kill && item.ActorSide == BattlefieldTacticalSide.Friendly),
            FriendlyDeathsRecent = window.Count(item => item.Kind == BattlefieldChatEventKind.Kill && item.TargetSide == BattlefieldTacticalSide.Friendly),
            EnemyKillsRecent = window.Count(item => item.Kind == BattlefieldChatEventKind.Kill && item.ActorSide == BattlefieldTacticalSide.Enemy),
            EnemyDeathsRecent = window.Count(item => item.Kind == BattlefieldChatEventKind.Kill && item.TargetSide == BattlefieldTacticalSide.Enemy),
            BattleHighEventsRecent = window.Count(item => item.Kind == BattlefieldChatEventKind.BattleHigh),
            ObjectiveEventsRecent = window.Count(item => IsObjectiveEvent(item.Kind)),
            SourceText = sourceText,
            SummaryText = BuildSummaryText(recent, window, sourceText)
        };
    }

    private static string BuildSummaryText(
        IReadOnlyList<BattlefieldChatEventSnapshot> recent,
        IReadOnlyList<BattlefieldChatEventSnapshot> window,
        string sourceText)
    {
        if (recent.Count == 0)
            return $"{sourceText}：尚未捕获击杀/战意/据点事件。";

        var friendlyKills = window.Count(item => item.Kind == BattlefieldChatEventKind.Kill && item.ActorSide == BattlefieldTacticalSide.Friendly);
        var friendlyDeaths = window.Count(item => item.Kind == BattlefieldChatEventKind.Kill && item.TargetSide == BattlefieldTacticalSide.Friendly);
        var battleHigh = window.Count(item => item.Kind == BattlefieldChatEventKind.BattleHigh);
        var objectives = window.Count(item => IsObjectiveEvent(item.Kind));

        return $"聊天事件近30秒：我方击杀 {friendlyKills}，我方阵亡 {friendlyDeaths}，战意 {battleHigh}，据点/目标 {objectives}。";
    }

    private static bool TryCreateEvent(
        string text,
        string sender,
        XivChatType logKind,
        XivChatRelationKind sourceKind,
        XivChatRelationKind targetKind,
        long now,
        out ChatEventEntry entry)
    {
        if (TryParseKill(text, sender, sourceKind, targetKind, now, logKind, out entry))
            return true;

        if (TryParseBattleHigh(text, sender, sourceKind, now, logKind, out entry))
            return true;

        if (TryParseObjective(text, now, logKind, out entry))
            return true;

        entry = default;
        return false;
    }

    private static bool TryParseKill(
        string text,
        string sender,
        XivChatRelationKind sourceKind,
        XivChatRelationKind targetKind,
        long now,
        XivChatType logKind,
        out ChatEventEntry entry)
    {
        entry = default;
        if (!ContainsAny(text, "击倒", "擊倒", "击败", "擊敗", "击杀", "擊殺", "杀死", "殺死", "打倒", "讨伐", "討伐", "消灭", "消滅", "战胜", "戰勝", "defeat", "knock", "slain", "killed", "kills"))
            return false;

        string actor;
        string target;
        var actorSide = InferSide(sourceKind);
        var targetSide = InferSide(targetKind);

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
            targetSide = BattlefieldTacticalSide.Friendly;
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
            var match = KillRegexes.Select(regex => regex.Match(text)).FirstOrDefault(match => match.Success);
            if (match == null)
                return false;

            actor = CleanName(match.Groups["actor"].Value);
            target = CleanName(match.Groups["target"].Value);
        }

        if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(target) || string.Equals(actor, target, StringComparison.OrdinalIgnoreCase))
            return false;

        actorSide = RefineSideFromName(actor, actorSide);
        targetSide = RefineSideFromName(target, targetSide);
        entry = new ChatEventEntry(
            now,
            $"聊天/{logKind}",
            text,
            BattlefieldChatEventKind.Kill,
            actor,
            target,
            actorSide,
            targetSide,
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
        XivChatRelationKind sourceKind,
        long now,
        XivChatType logKind,
        out ChatEventEntry entry)
    {
        entry = default;
        if (!ContainsAny(text, "战意", "戰意", "Battle High", "Battle Fever"))
            return false;

        var actor = ExtractBattleHighActor(text, sender);
        var sourceSide = RefineSideFromName(actor, InferSide(sourceKind));
        var level = TryParseBattleHighLevel(text, out var parsedLevel) ? parsedLevel : (int?)null;
        var delta = TryParseBattleHighDelta(text, out var parsedDelta) ? parsedDelta : (int?)null;

        entry = new ChatEventEntry(
            now,
            $"聊天/{logKind}",
            text,
            BattlefieldChatEventKind.BattleHigh,
            actor,
            string.Empty,
            sourceSide,
            BattlefieldTacticalSide.Unknown,
            null,
            string.Empty,
            string.Empty,
            level,
            delta);
        return true;
    }

    private static bool TryParseObjective(
        string text,
        long now,
        XivChatType logKind,
        out ChatEventEntry entry)
    {
        entry = default;
        if (!LooksLikeObjectiveMessage(text))
            return false;

        var kind = InferObjectiveKind(text);
        if (kind == BattlefieldChatEventKind.Unknown)
            return false;

        var ownership = TryInferOwnership(text, out var parsedOwnership) ? parsedOwnership : (NodeOwnership?)null;
        var locationId = LocationIdRegex.Match(text) is { Success: true } match ? NormalizeLocationId(match.Value) : string.Empty;
        var objectiveName = InferObjectiveName(text);
        var actorName = ownership.HasValue ? OwnershipText(ownership.Value) : string.Empty;

        entry = new ChatEventEntry(
            now,
            $"聊天/{logKind}",
            text,
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

    private static BattlefieldTacticalSide InferSide(XivChatRelationKind relation)
        => relation switch
        {
            XivChatRelationKind.LocalPlayer
                or XivChatRelationKind.PartyMember
                or XivChatRelationKind.AllianceMember
                or XivChatRelationKind.PetOrCompanion
                or XivChatRelationKind.PetOrCompanionParty
                or XivChatRelationKind.PetOrCompanionAlliance
                => BattlefieldTacticalSide.Friendly,
            XivChatRelationKind.EngagedEnemy
                or XivChatRelationKind.UnengagedEnemy
                => BattlefieldTacticalSide.Enemy,
            _ => BattlefieldTacticalSide.Unknown
        };

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

    private static BattlefieldTacticalSide InferSideFromOwnership(NodeOwnership? ownership, byte? localBattalion)
    {
        if (!ownership.HasValue || ownership.Value == NodeOwnership.Neutral || !localBattalion.HasValue)
            return BattlefieldTacticalSide.Unknown;

        var battalion = ownership.Value switch
        {
            NodeOwnership.Maelstrom => (byte?)0,
            NodeOwnership.TwinAdder => (byte?)1,
            NodeOwnership.ImmortalFlames => (byte?)2,
            _ => null
        };

        if (!battalion.HasValue)
            return BattlefieldTacticalSide.Unknown;

        return battalion.Value == localBattalion.Value ? BattlefieldTacticalSide.Friendly : BattlefieldTacticalSide.Enemy;
    }

    private static string BuildEventSummary(ChatEventEntry entry, BattlefieldTacticalSide actorSide)
        => entry.Kind switch
        {
            BattlefieldChatEventKind.Kill => $"{SideText(entry.ActorSide)} {entry.ActorName} 击倒 {SideText(entry.TargetSide)} {entry.TargetName}".Trim(),
            BattlefieldChatEventKind.BattleHigh => BuildBattleHighSummary(entry),
            BattlefieldChatEventKind.ObjectiveCaptured => $"{SideText(actorSide)} {OwnershipText(entry.Ownership)}占领{FormatObjective(entry)}",
            BattlefieldChatEventKind.ObjectiveLost => $"{OwnershipText(entry.Ownership)}失去/解除{FormatObjective(entry)}",
            BattlefieldChatEventKind.ObjectiveContested => $"{FormatObjective(entry)}争夺中",
            BattlefieldChatEventKind.ObjectiveOther => $"{FormatObjective(entry)}事件",
            _ => entry.Text
        };

    private static string BuildBattleHighSummary(ChatEventEntry entry)
    {
        var actor = string.IsNullOrWhiteSpace(entry.ActorName) ? "未知玩家" : entry.ActorName;
        var parts = new List<string> { $"{SideText(entry.ActorSide)} {actor} 战意" };
        if (entry.BattleHighLevel.HasValue)
            parts.Add($"等级 {entry.BattleHighLevel.Value}");
        if (entry.BattleHighDelta.HasValue)
            parts.Add($"+{entry.BattleHighDelta.Value}");
        return string.Join(" ", parts).Trim();
    }

    private static string FormatObjective(ChatEventEntry entry)
    {
        var name = string.IsNullOrWhiteSpace(entry.ObjectiveName) ? "目标" : entry.ObjectiveName;
        return name;
    }

    private static string NormalizeLocationId(string value)
    {
        if (!int.TryParse(value, out var number))
            return value;

        return number is >= 0 and < 100 ? number.ToString("D2") : value;
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

    private static string SideText(BattlefieldTacticalSide side)
        => side switch
        {
            BattlefieldTacticalSide.Friendly => "我方",
            BattlefieldTacticalSide.Enemy => "敌方",
            _ => "未知"
        };

    private static bool IsObjectiveEvent(BattlefieldChatEventKind kind)
        => kind is BattlefieldChatEventKind.ObjectiveCaptured
            or BattlefieldChatEventKind.ObjectiveLost
            or BattlefieldChatEventKind.ObjectiveContested
            or BattlefieldChatEventKind.ObjectiveOther;

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
            .Trim(' ', '　', '。', '.', '!', '！', ',', '，', ':', '：', ';', '；', '“', '”', '"', '\'', '「', '」', '『', '』');

        return Regex.Replace(cleaned, @"\s+(?:获得|獲得|击倒|擊倒|击败|擊敗|defeated|kills).*$", string.Empty, RegexOptions.IgnoreCase).Trim();
    }

    private static string NormalizeForDedup(string text)
        => CleanText(text)
            .Replace(" ", string.Empty)
            .Replace("　", string.Empty)
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

    private static bool StartsWithAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (text.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsKnownFrontlineTerritory(uint territoryType)
        => FrontlineTerritoryIds.Contains(territoryType);

    private readonly record struct ChatEventEntry(
        long ObservedAtTicks,
        string Source,
        string Text,
        BattlefieldChatEventKind Kind,
        string ActorName,
        string TargetName,
        BattlefieldTacticalSide ActorSide,
        BattlefieldTacticalSide TargetSide,
        NodeOwnership? Ownership,
        string LocationId,
        string ObjectiveName,
        int? BattleHighLevel,
        int? BattleHighDelta)
    {
        public BattlefieldChatEventSnapshot ToSnapshot(long now, byte? localBattalion)
        {
            var ageMs = Math.Max(0, now - ObservedAtTicks);
            var actorSide = ActorSide;
            if (actorSide == BattlefieldTacticalSide.Unknown && IsObjectiveEvent(Kind))
                actorSide = InferSideFromOwnership(Ownership, localBattalion);

            return new BattlefieldChatEventSnapshot(
                ObservedAtTicks,
                ageMs,
                Source,
                Text,
                Kind,
                ActorName,
                TargetName,
                actorSide,
                TargetSide,
                Ownership,
                LocationId,
                ObjectiveName,
                BattleHighLevel,
                BattleHighDelta,
                BuildEventSummary(this, actorSide));
        }
    }
}
