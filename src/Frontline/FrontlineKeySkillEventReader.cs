using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class FrontlineKeySkillEventReader : IDisposable
{
    private const int RawLineTtlMs = 30000;
    private const int RecentWindowMs = 30000;
    private const int DuplicateWindowMs = 1200;
    private const int HookConfirmationWindowMs = 8000;
    private const int CrossSourceDuplicateWindowMs = 2000;

    private static readonly HashSet<uint> FrontlineTerritoryIds = new()
    {
        376, 431, 554, 888, 1273, 1313
    };

    private static readonly Regex[] ActorSkillRegexes =
    {
        new(@"^(?<actor>.+?)(?:发动了|登场了|使用了|施放了|咏唱了|开始咏唱)\s*(?<skill>.+?)[。.!?？]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^(?<actor>.+?)\s+(?:uses|used|casts|cast|begins casting)\s+(?<skill>.+?)[。.!?？]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new(@"^(?<skill>.+?)(?:发动|登场|使用|施放|命中|生效)[。.!?？]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    private readonly IChatGui chatGui;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly CombatEventService combatEventService;
    private readonly IPluginLog log;
    private readonly List<RawCombatLine> rawLines = new();
    private readonly Dictionary<string, long> lastSeenByKey = new(StringComparer.Ordinal);
    private BattlefieldKeySkillUseSnapshot[] cachedMergedEvents = Array.Empty<BattlefieldKeySkillUseSnapshot>();
    private CachedRuleEntry[] cachedRuleEntries = Array.Empty<CachedRuleEntry>();
    private IReadOnlyList<FrontlineKeySkillRuleSnapshot>? cachedRuleSource;
    private bool cachedHasHookEvents;
    private bool cachedHasChatEvents;
    private long lastCombatEventVersion = -1;
    private int lastRawLineCount = -1;
    private bool disposed;

    public FrontlineKeySkillEventReader(
        IChatGui chatGui,
        IClientState clientState,
        IObjectTable objectTable,
        CombatEventService combatEventService,
        IPluginLog log)
    {
        this.chatGui = chatGui;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.combatEventService = combatEventService;
        this.log = log;

        chatGui.ChatMessage += OnChatMessage;
    }

    public BattlefieldKeySkillLogEventSituationSnapshot GetSnapshot(bool isInFrontline, FrontlineKnowledgeSnapshot knowledge)
    {
        if (disposed)
        {
            return new BattlefieldKeySkillLogEventSituationSnapshot
            {
                SourceText = "战斗事件读取已停止",
                SummaryText = "战斗事件读取已停止"
            };
        }

        var now = Environment.TickCount64;
        if (!isInFrontline && !IsKnownFrontlineTerritory(clientState.TerritoryType))
        {
            Prune(now);
            return new BattlefieldKeySkillLogEventSituationSnapshot
            {
                SourceText = "等待前线战斗事件",
                SummaryText = "等待进入纷争前线后再读取关键技能事件"
            };
        }

        Prune(now);
        if (lastCombatEventVersion >= -1)
        {
            RefreshCachedEvents(knowledge, now);
            return BuildProjectedSnapshot(knowledge, now);
        }

        var hookEvents = BuildHookEvents(knowledge, now);
        var chatEvents = BuildChatEvents(knowledge, now);
        var recent = MergeRecentEvents(hookEvents, chatEvents);
        var windowCount = recent.Count(item => item.AgeMs <= RecentWindowMs);

        var sourceText = hookEvents.Length switch
        {
            > 0 when chatEvents.Length > 0 => "战斗事件Hook/IChatGui兜底",
            > 0 => "战斗事件Hook",
            _ when chatEvents.Length > 0 => "IChatGui战斗日志",
            _ => "战斗事件Hook/IChatGui"
        };

        var summaryText = knowledge.KeySkillRules.Length == 0
            ? "关键技能规则尚未加载"
            : recent.Length == 0
                ? "尚未捕获到关键技能使用事件"
                : $"近30秒捕获 {windowCount} 条关键技能事件，当前缓存 {recent.Length} 条";

        return new BattlefieldKeySkillLogEventSituationSnapshot
        {
            IsAvailable = recent.Length > 0,
            RecentEvents = recent,
            RecentEventCount = windowCount,
            SourceText = sourceText,
            SummaryText = summaryText
        };
    }

    public bool TryMatchKeySkillRule(
        CombatActionEvent item,
        FrontlineKnowledgeSnapshot knowledge,
        out FrontlineKeySkillRuleSnapshot rule)
    {
        rule = default;
        var rules = GetRuleEntries(knowledge.KeySkillRules);
        if (rules.Length == 0)
            return false;

        rule = FindMatchingSkillRule(rules, item.ActionName, item.EvidenceText);
        return !string.IsNullOrWhiteSpace(rule.SkillName);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        chatGui.ChatMessage -= OnChatMessage;
        rawLines.Clear();
        lastSeenByKey.Clear();
        cachedMergedEvents = Array.Empty<BattlefieldKeySkillUseSnapshot>();
        cachedRuleEntries = Array.Empty<CachedRuleEntry>();
        cachedRuleSource = null;
        cachedHasHookEvents = false;
        cachedHasChatEvents = false;
        lastCombatEventVersion = -1;
        lastRawLineCount = -1;
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (disposed || !IsKnownFrontlineTerritory(clientState.TerritoryType))
            return;

        try
        {
            var text = CleanText(message.Message.TextValue);
            if (string.IsNullOrWhiteSpace(text) || !LooksLikeCombatSkillLine(text))
                return;

            var now = Environment.TickCount64;
            var sender = CleanText(message.Sender.TextValue);
            var key = NormalizeForDedup($"{message.LogKind}:{message.SourceKind}:{message.TargetKind}:{sender}:{text}");
            if (lastSeenByKey.TryGetValue(key, out var lastSeen) && now - lastSeen < DuplicateWindowMs)
                return;

            lastSeenByKey[key] = now;
            rawLines.Add(new RawCombatLine(now, sender, text, message.LogKind, message.SourceKind, message.TargetKind));
            Prune(now);
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[FrontlineKeySkillEventReader] 解析聊天战斗日志失败");
        }
    }

    private void RefreshCachedEvents(FrontlineKnowledgeSnapshot knowledge, long now)
    {
        var combatEventVersion = combatEventService.SnapshotVersion;
        var rawLineCount = rawLines.Count;
        var rulesChanged = !ReferenceEquals(cachedRuleSource, knowledge.KeySkillRules);
        var rules = GetRuleEntries(knowledge.KeySkillRules);
        if (!rulesChanged
            && combatEventVersion == lastCombatEventVersion
            && rawLineCount == lastRawLineCount)
        {
            return;
        }

        var hookEvents = BuildHookEvents(rules, now);
        var chatEvents = BuildChatEvents(rules, now);
        cachedMergedEvents = MergeRecentEvents(hookEvents, chatEvents);
        cachedHasHookEvents = hookEvents.Length > 0;
        cachedHasChatEvents = chatEvents.Length > 0;
        lastCombatEventVersion = combatEventService.SnapshotVersion;
        lastRawLineCount = rawLines.Count;
    }

    private BattlefieldKeySkillLogEventSituationSnapshot BuildProjectedSnapshot(FrontlineKnowledgeSnapshot knowledge, long now)
    {
        var recent = ProjectRecentEvents(now, out var windowCount);
        return new BattlefieldKeySkillLogEventSituationSnapshot
        {
            IsAvailable = recent.Length > 0,
            RecentEvents = recent,
            RecentEventCount = windowCount,
            SourceText = BuildSourceText(),
            SummaryText = BuildSummaryText(knowledge.KeySkillRules.Length, recent.Length, windowCount)
        };
    }

    private BattlefieldKeySkillUseSnapshot[] ProjectRecentEvents(long now, out int windowCount)
    {
        if (cachedMergedEvents.Length == 0)
        {
            windowCount = 0;
            return Array.Empty<BattlefieldKeySkillUseSnapshot>();
        }

        var projected = new List<BattlefieldKeySkillUseSnapshot>(Math.Min(cachedMergedEvents.Length, 32));
        windowCount = 0;
        foreach (var item in cachedMergedEvents)
        {
            var ageMs = Math.Max(0, now - item.ObservedAtTicks);
            if (ageMs > RecentWindowMs)
                continue;

            projected.Add(item with { AgeMs = ageMs });
            windowCount++;
            if (projected.Count >= 32)
                break;
        }

        return projected.Count == 0 ? Array.Empty<BattlefieldKeySkillUseSnapshot>() : projected.ToArray();
    }

    private string BuildSourceText()
        => cachedHasHookEvents switch
        {
            true when cachedHasChatEvents => "战斗事件Hook/IChatGui兜底",
            true => "战斗事件Hook",
            _ when cachedHasChatEvents => "IChatGui战斗日志",
            _ => "战斗事件Hook/IChatGui"
        };

    private static string BuildSummaryText(int ruleCount, int recentCount, int windowCount)
        => ruleCount == 0
            ? "关键技能规则尚未加载"
            : recentCount == 0
                ? "尚未捕获到关键技能使用事件"
                : $"近30秒捕获 {windowCount} 条关键技能事件，当前缓存 {recentCount} 条";

    private CachedRuleEntry[] GetRuleEntries(IReadOnlyList<FrontlineKeySkillRuleSnapshot> rules)
    {
        if (ReferenceEquals(cachedRuleSource, rules))
            return cachedRuleEntries;

        cachedRuleSource = rules;
        cachedRuleEntries = BuildCachedRuleEntries(rules);
        return cachedRuleEntries;
    }

    private BattlefieldKeySkillUseSnapshot[] BuildHookEvents(FrontlineKnowledgeSnapshot knowledge, long now)
        => BuildHookEvents(GetRuleEntries(knowledge.KeySkillRules), now);

    private BattlefieldKeySkillUseSnapshot[] BuildChatEvents(FrontlineKnowledgeSnapshot knowledge, long now)
        => BuildChatEvents(GetRuleEntries(knowledge.KeySkillRules), now);

    private BattlefieldKeySkillUseSnapshot[] BuildHookEvents(CachedRuleEntry[] rules, long now)
    {
        if (rules.Length == 0)
            return Array.Empty<BattlefieldKeySkillUseSnapshot>();

        var events = combatEventService.GetRecentEvents(now, RecentWindowMs);
        if (events.Length == 0)
            return Array.Empty<BattlefieldKeySkillUseSnapshot>();

        var actionEffects = new List<CombatActionEvent>(events.Length);
        foreach (var item in events)
        {
            if (item.Kind == CombatEventKind.ActionEffect)
                actionEffects.Add(item);
        }
        var results = new List<BattlefieldKeySkillUseSnapshot>(events.Length);

        foreach (var item in events)
        {
            if (item.Kind == CombatEventKind.StartCast && HasConfirmedActionEffect(actionEffects, item))
                continue;

            if (!TryCreateHookEvent(item, rules, now, out var snapshot))
                continue;

            results.Add(snapshot);
        }

        return results.ToArray();
    }

    private BattlefieldKeySkillUseSnapshot[] BuildChatEvents(CachedRuleEntry[] rules, long now)
    {
        if (rules.Length == 0)
            return Array.Empty<BattlefieldKeySkillUseSnapshot>();

        if (rawLines.Count == 0)
            return Array.Empty<BattlefieldKeySkillUseSnapshot>();

        var results = new List<BattlefieldKeySkillUseSnapshot>(Math.Min(rawLines.Count, 32));
        for (var i = rawLines.Count - 1; i >= 0 && results.Count < 32; i--)
        {
            if (TryCreateChatEvent(rawLines[i], rules, now, out var item) && item.ObservedAtTicks > 0)
                results.Add(item);
        }

        return results.ToArray();
    }

    private static BattlefieldKeySkillUseSnapshot[] MergeRecentEvents(
        IReadOnlyList<BattlefieldKeySkillUseSnapshot> hookEvents,
        IReadOnlyList<BattlefieldKeySkillUseSnapshot> chatEvents)
    {
        var candidates = new List<MergedEventCandidate>(hookEvents.Count + chatEvents.Count);
        foreach (var item in hookEvents)
            candidates.Add(new MergedEventCandidate(item, 0));
        foreach (var item in chatEvents)
            candidates.Add(new MergedEventCandidate(item, 1));

        candidates.Sort(static (left, right) =>
        {
            var observedCompare = right.Snapshot.ObservedAtTicks.CompareTo(left.Snapshot.ObservedAtTicks);
            return observedCompare != 0 ? observedCompare : left.Priority.CompareTo(right.Priority);
        });

        var merged = new List<BattlefieldKeySkillUseSnapshot>(32);
        foreach (var candidate in candidates)
        {
            if (IsDuplicate(merged, candidate.Snapshot))
                continue;

            merged.Add(candidate.Snapshot);
            if (merged.Count >= 32)
                break;
        }

        return merged.ToArray();
    }

    private bool TryCreateHookEvent(
        CombatActionEvent item,
        CachedRuleEntry[] rules,
        long now,
        out BattlefieldKeySkillUseSnapshot snapshot)
    {
        snapshot = default;
        var rule = FindMatchingSkillRule(rules, item.ActionName, item.EvidenceText);
        if (string.IsNullOrWhiteSpace(rule.SkillName))
            return false;

        var localPlayer = objectTable.LocalPlayer;
        var relation = ResolveRelation(item.SourceEntityId, item.SourceGameObjectId, item.SourceName, localPlayer);
        var actorName = ResolveActorDisplayName(item.SourceName, localPlayer, relation);
        var targetName = ResolveTargetDisplayName(item.PrimaryTargetName, item.Targets.Length);
        var classJobId = item.SourceClassJobId != 0 ? item.SourceClassJobId : rule.ClassJobId ?? 0;
        var jobName = !string.IsNullOrWhiteSpace(item.SourceJobName) ? item.SourceJobName : rule.JobName;
        var sourceText = item.Kind == CombatEventKind.ActionEffect ? "战斗事件Hook/技能生效" : "战斗事件Hook/开始读条";
        var evidenceText = BuildHookEvidenceText(item, rule, targetName);

        snapshot = new BattlefieldKeySkillUseSnapshot(
            item.ObservedAtTicks,
            Math.Max(0, now - item.ObservedAtTicks),
            item.SourceGameObjectId,
            actorName,
            relation,
            null,
            string.Empty,
            classJobId,
            jobName,
            rule.SkillName,
            rule.Kind,
            targetName,
            sourceText,
            evidenceText);
        return true;
    }

    private bool TryCreateChatEvent(
        RawCombatLine line,
        CachedRuleEntry[] rules,
        long now,
        out BattlefieldKeySkillUseSnapshot snapshot)
    {
        snapshot = default;
        if (rules.Length == 0)
            return false;

        var actor = line.Sender;
        var skillText = line.Text;
        foreach (var regex in ActorSkillRegexes)
        {
            var match = regex.Match(line.Text);
            if (!match.Success)
                continue;

            if (match.Groups["actor"].Success)
                actor = match.Groups["actor"].Value;
            if (match.Groups["skill"].Success)
                skillText = match.Groups["skill"].Value;
            break;
        }

        var rule = FindMatchingSkillRule(rules, skillText, line.Text);
        if (string.IsNullOrWhiteSpace(rule.SkillName))
            return false;

        var localPlayer = objectTable.LocalPlayer;
        var relation = ResolveRelation(0, 0, actor, localPlayer);
        var actorName = ResolveActorDisplayName(actor, localPlayer, relation);
        snapshot = new BattlefieldKeySkillUseSnapshot(
            line.ObservedAtTicks,
            Math.Max(0, now - line.ObservedAtTicks),
            0,
            actorName,
            relation,
            null,
            string.Empty,
            rule.ClassJobId ?? 0,
            rule.JobName,
            rule.SkillName,
            rule.Kind,
            string.Empty,
            $"IChatGui/战斗日志/{line.LogKind}",
            $"{line.Text}；规则来源：{rule.SourceName}");
        return true;
    }

    private static bool HasConfirmedActionEffect(
        IReadOnlyList<CombatActionEvent> actionEffects,
        CombatActionEvent castEvent)
    {
        foreach (var effect in actionEffects)
        {
            if (effect.ActionId != castEvent.ActionId)
                continue;

            if (!IsSameActor(effect, castEvent))
                continue;

            var delta = effect.ObservedAtTicks - castEvent.ObservedAtTicks;
            if (delta < 0 || delta > HookConfirmationWindowMs)
                continue;

            if (TargetsConflict(effect, castEvent))
                continue;

            return true;
        }

        return false;
    }

    private static bool TargetsConflict(CombatActionEvent left, CombatActionEvent right)
    {
        if (left.PrimaryTargetEntityId != 0 && right.PrimaryTargetEntityId != 0)
            return left.PrimaryTargetEntityId != right.PrimaryTargetEntityId;

        if (left.PrimaryTargetObjectId != 0 && right.PrimaryTargetObjectId != 0)
            return left.PrimaryTargetObjectId != right.PrimaryTargetObjectId;

        var leftName = NormalizeActorName(left.PrimaryTargetName);
        var rightName = NormalizeActorName(right.PrimaryTargetName);
        return leftName.Length > 0 && rightName.Length > 0 && !string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameActor(CombatActionEvent left, CombatActionEvent right)
    {
        if (left.SourceEntityId != 0 && right.SourceEntityId != 0)
            return left.SourceEntityId == right.SourceEntityId;

        if (left.SourceGameObjectId != 0 && right.SourceGameObjectId != 0)
            return left.SourceGameObjectId == right.SourceGameObjectId;

        var leftName = NormalizeActorName(left.SourceName);
        var rightName = NormalizeActorName(right.SourceName);
        return leftName.Length > 0 && string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDuplicate(
        IReadOnlyList<BattlefieldKeySkillUseSnapshot> existing,
        BattlefieldKeySkillUseSnapshot candidate)
    {
        var candidateActor = NormalizeActorName(candidate.Name);
        var candidateSkill = NormalizeForMatch(candidate.SkillName);
        var candidateTarget = NormalizeActorName(candidate.TargetName);

        foreach (var item in existing)
        {
            if (Math.Abs(item.ObservedAtTicks - candidate.ObservedAtTicks) > CrossSourceDuplicateWindowMs)
                continue;

            if (!string.Equals(NormalizeActorName(item.Name), candidateActor, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.Equals(NormalizeForMatch(item.SkillName), candidateSkill, StringComparison.OrdinalIgnoreCase))
                continue;

            var itemTarget = NormalizeActorName(item.TargetName);
            if (itemTarget.Length > 0 && candidateTarget.Length > 0
                && !string.Equals(itemTarget, candidateTarget, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static CachedRuleEntry[] BuildCachedRuleEntries(IReadOnlyList<FrontlineKeySkillRuleSnapshot> rules)
        => rules
            .OrderByDescending(rule => rule.SkillName.Length)
            .Select(rule =>
            {
                var aliases = BuildSkillAliases(rule.SkillName)
                    .Select(NormalizeForMatch)
                    .Where(alias => alias.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new CachedRuleEntry(rule, aliases);
            })
            .Where(entry => entry.Aliases.Length > 0)
            .ToArray();

    private static FrontlineKeySkillRuleSnapshot FindMatchingSkillRule(
        IReadOnlyList<CachedRuleEntry> rules,
        string skillText,
        string fullText)
    {
        var normalizedSkillText = NormalizeForMatch(skillText);
        var normalizedFullText = NormalizeForMatch(fullText);

        foreach (var entry in rules)
        {
            foreach (var alias in entry.Aliases)
            {
                if (normalizedSkillText.Contains(alias, StringComparison.OrdinalIgnoreCase)
                    || normalizedFullText.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Rule;
                }
            }
        }

        return default;
    }

    private static string[] BuildSkillAliases(string skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return Array.Empty<string>();

        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            skillName.Trim()
        };

        foreach (var alias in skillName.Split(new[] { '/', '／', '|', '、' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var value = alias.Trim();
            if (value.Length >= 2)
                aliases.Add(value);
        }

        return aliases.ToArray();
    }

    private static bool ContainsNormalized(string haystack, string needle)
    {
        var normalizedHaystack = NormalizeForMatch(haystack);
        var normalizedNeedle = NormalizeForMatch(needle);
        return normalizedNeedle.Length > 0 && normalizedHaystack.Contains(normalizedNeedle, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeForMatch(string text)
        => Regex.Replace(text ?? string.Empty, @"[\s\u3000""'‘’“”\(\)\[\]【】<>《》:：,，。.!！？?·]", string.Empty);

    private static string NormalizeForDedup(string text)
        => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();

    private static bool LooksLikeCombatSkillLine(string text)
        => text.Contains("使用", StringComparison.OrdinalIgnoreCase)
            || text.Contains("发动", StringComparison.OrdinalIgnoreCase)
            || text.Contains("施放", StringComparison.OrdinalIgnoreCase)
            || text.Contains("咏唱", StringComparison.OrdinalIgnoreCase)
            || text.Contains("开始咏唱", StringComparison.OrdinalIgnoreCase)
            || text.Contains("uses", StringComparison.OrdinalIgnoreCase)
            || text.Contains("casts", StringComparison.OrdinalIgnoreCase)
            || text.Contains("begins casting", StringComparison.OrdinalIgnoreCase);

    private void Prune(long now)
    {
        rawLines.RemoveAll(line => now - line.ObservedAtTicks > RawLineTtlMs);
        foreach (var key in lastSeenByKey.Keys.ToArray())
        {
            if (now - lastSeenByKey[key] > RawLineTtlMs)
                lastSeenByKey.Remove(key);
        }
    }

    private static bool IsKnownFrontlineTerritory(uint territoryType)
        => FrontlineTerritoryIds.Contains(territoryType);

    private static BattlefieldPlayerRelation ResolveRelation(
        uint sourceEntityId,
        ulong sourceGameObjectId,
        string sourceName,
        IPlayerCharacter? localPlayer)
    {
        if (localPlayer == null)
            return BattlefieldPlayerRelation.Unknown;

        if (sourceEntityId != 0 && sourceEntityId == localPlayer.EntityId)
            return BattlefieldPlayerRelation.LocalPlayer;

        if (sourceGameObjectId != 0 && sourceGameObjectId == localPlayer.GameObjectId)
            return BattlefieldPlayerRelation.LocalPlayer;

        var localName = NormalizeActorName(localPlayer.Name.TextValue);
        var actorName = NormalizeActorName(sourceName);
        return actorName.Length > 0 && string.Equals(actorName, localName, StringComparison.OrdinalIgnoreCase)
            ? BattlefieldPlayerRelation.LocalPlayer
            : BattlefieldPlayerRelation.Unknown;
    }

    private static string ResolveActorDisplayName(
        string rawActorName,
        IPlayerCharacter? localPlayer,
        BattlefieldPlayerRelation relation)
    {
        if (relation == BattlefieldPlayerRelation.LocalPlayer)
            return CleanText(localPlayer?.Name.TextValue) is { Length: > 0 } localName ? localName : "你";

        var actorName = CleanText(rawActorName);
        if (actorName is "你" or "您" or "You" or "you")
            return CleanText(localPlayer?.Name.TextValue) is { Length: > 0 } localName ? localName : "你";

        return actorName;
    }

    private static string ResolveTargetDisplayName(string targetName, int targetCount)
    {
        var cleaned = CleanText(targetName);
        if (!string.IsNullOrWhiteSpace(cleaned))
            return cleaned;

        return targetCount > 1 ? "多目标" : string.Empty;
    }

    private static string BuildHookEvidenceText(
        CombatActionEvent item,
        FrontlineKeySkillRuleSnapshot rule,
        string targetName)
    {
        var parts = new List<string>
        {
            $"动作：{item.ActionName}({item.ActionId})",
            $"规则来源：{rule.SourceName}"
        };

        if (!string.IsNullOrWhiteSpace(targetName))
            parts.Add($"目标：{targetName}");

        if (item.Kind == CombatEventKind.StartCast)
        {
            if (item.CastTime > 0f)
                parts.Add($"读条：{item.CastTime:0.0}s");
        }
        else
        {
            parts.Add($"生效目标数：{item.Targets.Length}");
            if (item.ActionAnimationId > 0)
                parts.Add($"动画：{item.ActionAnimationId}");
        }

        return string.Join("；", parts);
    }

    private static string CleanText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        return Regex.Replace(raw, @"\s+", " ").Trim();
    }

    private static string NormalizeActorName(string? name)
    {
        var normalized = CleanText(name);
        if (normalized.Length == 0)
            return string.Empty;

        var worldSeparator = normalized.IndexOf('@');
        if (worldSeparator > 0)
            normalized = normalized[..worldSeparator];

        return NormalizeForMatch(normalized);
    }

    private readonly record struct RawCombatLine(
        long ObservedAtTicks,
        string Sender,
        string Text,
        XivChatType LogKind,
        XivChatRelationKind SourceKind,
        XivChatRelationKind TargetKind);

    private readonly record struct CachedRuleEntry(
        FrontlineKeySkillRuleSnapshot Rule,
        string[] Aliases);

    private readonly record struct MergedEventCandidate(
        BattlefieldKeySkillUseSnapshot Snapshot,
        int Priority);
}
