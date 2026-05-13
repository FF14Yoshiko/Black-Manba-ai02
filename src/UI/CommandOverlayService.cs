using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;

namespace ai02;

public sealed class CommandOverlayService : IDisposable
{
    private const int HudPrimaryMaxChars = 56;
    private const int OverlayDisplayMaxChars = 84;
    private const int HudContextMaxChars = 42;
    private const int HudAlertMaxChars = 36;
    private const int HudTargetMaxChars = 8;
    private const long ObjectivePinHoldMs = 16000;
    private const float ObjectivePinSwitchMargin = 12f;

    private readonly Configuration configuration;
    private readonly IUiBuilder uiBuilder;
    private BattlefieldPriorityTargetSnapshot? pinnedObjectiveTarget;
    private long pinnedObjectiveUpdatedAtTicks = -1;
    private CommandOverlayDirectiveDisplaySnapshot? heldAiDirectiveDisplay;
    private long heldAiDirectiveTicks = -1;
    private IFontHandle? objectiveFontHandle;
    private IFontHandle? battleFontHandle;
    private IFontHandle? threatFontHandle;
    private int objectiveFontSizePx;
    private int battleFontSizePx;
    private int threatFontSizePx;
    private bool disposed;

    public CommandOverlayService(Configuration configuration, IUiBuilder uiBuilder)
    {
        this.configuration = configuration;
        this.uiBuilder = uiBuilder;
    }

    public void DrawOverlay(BattlefieldSnapshot snapshot)
    {
        if (disposed)
            return;

        var config = configuration.CommandOverlay;
        if (!config.Enabled)
            return;

        config.Normalize();
        if (config.OnlyInFrontline && !snapshot.IsInFrontline)
            return;

        EnsureOverlayFonts(config);

        var now = Environment.TickCount64;
        if (!TryResolveDisplayContent(snapshot, now, config, out var content))
            return;

        DrawTextWindow(content, config);
    }

    public void Dispose()
    {
        disposed = true;
        pinnedObjectiveTarget = null;
        pinnedObjectiveUpdatedAtTicks = -1;
        heldAiDirectiveDisplay = null;
        heldAiDirectiveTicks = -1;
        DisposeOverlayFonts();
    }

    private bool TryResolveDisplayContent(
        BattlefieldSnapshot snapshot,
        long now,
        CommandOverlayConfiguration config,
        out OverlayDisplayContent content)
    {
        var hasFreshAiText = TryResolveFreshAiDisplay(snapshot.LlmStrategicDecision, out var aiPrimaryLine, out var aiSecondaryLine);
        var directives = hasFreshAiText
            ? new CommandOverlayDirectiveDisplaySnapshot(
                aiPrimaryLine,
                aiSecondaryLine,
                true)
            : new CommandOverlayDirectiveDisplaySnapshot(
                ResolveOverlayPrimaryCommandText(snapshot.Decision),
                BuildOverlayCurrentActionText(snapshot.Decision),
                CommandOverlayAiDisplayPolicy.IsAiLead(snapshot.Decision));
        directives = CommandOverlayAiDisplayPolicy.ResolveDisplay(
            directives,
            now,
            config.AiLeadHoldSeconds,
            ref heldAiDirectiveDisplay,
            ref heldAiDirectiveTicks);

        content = new OverlayDisplayContent(
            directives.PrimaryCommandLine,
            directives.CurrentActionLine,
            BuildOverlayThreatText(snapshot),
            directives.IsAiLead);
        return true;
    }

    private static bool TryResolveFreshAiDisplay(
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        out string primaryLine,
        out string secondaryLine)
    {
        primaryLine = string.Empty;
        secondaryLine = string.Empty;
        if (!llmDecision.IsAvailable || !llmDecision.IsFresh)
            return false;

        var primaryText = LlmStrategicTextResolver.ResolvePrimaryDisplayText(llmDecision);
        if (string.IsNullOrWhiteSpace(primaryText))
            return false;

        primaryLine = CleanOverlayAiDirectiveText(primaryText);
        secondaryLine = ResolveAiSecondaryDisplayText(llmDecision, primaryLine);
        return true;
    }

    private static string ResolveOverlayPrimaryCommandText(BattlefieldDecisionSnapshot decision)
    {
        var commands = decision.CommandSituation;
        if (commands.PrimaryCommand.HasValue && !string.IsNullOrWhiteSpace(commands.PrimaryCommand.Value.CommandText))
        {
            return StripOverlayTimingDetails(SimplifyOverlayCommandText(commands.PrimaryCommand.Value));
        }
        if (commands.Publish.ShouldAnnounce && !string.IsNullOrWhiteSpace(commands.Publish.SpeakText))
            return StripOverlayTimingDetails(CleanOverlayDirectiveText(commands.Publish.SpeakText));
        if (decision.PrimaryAction.HasValue && !string.IsNullOrWhiteSpace(decision.PrimaryAction.Value.Text))
            return StripOverlayTimingDetails(SimplifyOverlayActionText(decision.PrimaryAction.Value));
        if (commands.PrimaryAction.HasValue && !string.IsNullOrWhiteSpace(commands.PrimaryAction.Value.Text))
            return StripOverlayTimingDetails(SimplifyOverlayActionText(commands.PrimaryAction.Value));
        if (!string.IsNullOrWhiteSpace(decision.RecommendedAction))
            return StripOverlayTimingDetails(CleanOverlayDirectiveText(decision.RecommendedAction));

        return "暂无主指令，主团跟我压进";
    }

    private static string BuildOverlayCurrentActionText(BattlefieldDecisionSnapshot decision)
    {
        if (decision.PrimaryAction.HasValue && !string.IsNullOrWhiteSpace(decision.PrimaryAction.Value.Text))
            return StripOverlayTimingDetails(SimplifyOverlayActionText(decision.PrimaryAction.Value));
        if (decision.PublishedAction.HasValue && !string.IsNullOrWhiteSpace(decision.PublishedAction.Value.Text))
            return StripOverlayTimingDetails(SimplifyOverlayActionText(decision.PublishedAction.Value));
        if (decision.CommandSituation.PrimaryAction.HasValue && !string.IsNullOrWhiteSpace(decision.CommandSituation.PrimaryAction.Value.Text))
            return StripOverlayTimingDetails(SimplifyOverlayActionText(decision.CommandSituation.PrimaryAction.Value));
        if (!string.IsNullOrWhiteSpace(decision.RecommendedAction))
            return StripOverlayTimingDetails(CleanOverlayDirectiveText(decision.RecommendedAction));

        return "战场态势不足，继续寻找目标";
    }

    private static string BuildOverlayThreatText(BattlefieldSnapshot snapshot)
    {
        var alert = BuildHudAlertLine(snapshot);
        if (!string.IsNullOrWhiteSpace(alert))
            return CleanOverlayDirectiveText(alert);

        var risk = snapshot.Decision.RiskAssessment;
        var advanced = snapshot.TeamSituation.AdvancedTactics;

        if (advanced.IsThirdPartyPincerLikely || risk.ThirdPartyPincerRisk >= 68f || risk.EncirclementRisk >= 72f)
            return "第三方夹击靠近，别压太深";
        if (advanced.IsHighGroundDropPrepLikely || risk.HighGroundDropRisk >= 62f)
            return "高台威胁在看，低地横拉";
        if (advanced.IsChokeBlockedLikely || risk.ChokeBlockRisk >= 70f)
            return "前方卡口压力高，别直线穿口";
        if (risk.SkillThreatRisk >= 66f || risk.LimitBreakRisk >= 66f)
            return "敌方爆发还在，先骗技能";
        if (risk.FlankRisk >= 68f)
            return "侧翼可能来人，别压太偏";
        if (risk.NumberDisadvantageRisk >= 68f)
            return "局部人数劣势，先等人齐";

        return "暂无致命威胁";
    }

    private static string SimplifyOverlayCommandText(BattlefieldCommandSnapshot command)
        => TrimOverlayMicroDetails(SanitizeOverlayDirectiveText(command.CommandText), command.Kind, command.Id);

    private static string SimplifyOverlayActionText(BattlefieldActionCandidateSnapshot action)
        => TrimOverlayMicroDetails(SanitizeOverlayDirectiveText(action.Text), action.CommandKind, action.CommandId);

    private static string TrimOverlayMicroDetails(string text, BattlefieldCommandKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var parts = text
            .Split('；', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim())
            .Select(part => TrimOverlayMicroClause(part, kind, id))
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        if (parts.Count == 0)
            return PreserveOverlayText(NormalizeOverlayDirectiveText(text), OverlayDisplayMaxChars);

        var value = NormalizeOverlayDirectiveText(string.Join("；", parts));
        return PreserveOverlayText(value, OverlayDisplayMaxChars);
    }

    private static string TrimOverlayMicroClause(string part, BattlefieldCommandKind kind, string id)
    {
        if (string.IsNullOrWhiteSpace(part))
            return string.Empty;

        var value = part.Trim();

        if (kind == BattlefieldCommandKind.FocusTarget || id.StartsWith("target:", StringComparison.Ordinal))
        {
            value = Regex.Replace(value, @"^集火标记目标\s+[^，；,;]+", "集火标记目标");
            value = Regex.Replace(value, @"^集火\s+[^，；,;]+", "集火当前目标");
            value = Regex.Replace(value, @"^收\s+[^，；,;]+", "收低血/被控");
        }

        if (id.StartsWith("target:control-skill", StringComparison.Ordinal)
            || value.StartsWith("盯 ", StringComparison.Ordinal))
        {
            value = Regex.Replace(
                value,
                @"^盯\s+[^，；,;]+(?:，\s*别让他打\s+[^，；,;]+)?",
                "提防关键技能位，别硬吃爆发");
        }

        value = Regex.Replace(value, @"控制补\s+[^，；,;]+", "控制补上");
        value = value.Replace("远程别换目标", string.Empty, StringComparison.Ordinal);
        value = value.Replace("远程准备转同一目标", string.Empty, StringComparison.Ordinal);
        value = value.Replace("AOE往人最多处打", string.Empty, StringComparison.Ordinal);
        value = value.Replace("AOE往人最多处落", string.Empty, StringComparison.Ordinal);
        value = value.Replace("AOE打人最多处", string.Empty, StringComparison.Ordinal);
        value = value.Replace("AOE人最多处", string.Empty, StringComparison.Ordinal);
        value = value.Replace("远程打人最多处", string.Empty, StringComparison.Ordinal);
        value = value.Replace("打人最多处", string.Empty, StringComparison.Ordinal);
        value = value.Replace("往人最多处落", string.Empty, StringComparison.Ordinal);

        return NormalizeOverlayDirectiveText(value);
    }

    private static string NormalizeOverlayDirectiveText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = Regex.Replace(value, @"\s*[，,]\s*[，,]\s*", "，");
        value = Regex.Replace(value, @"\s*[；;]\s*[；;]\s*", "；");
        value = Regex.Replace(value, @"\s*[，,]\s*[；;]\s*", "；");
        value = Regex.Replace(value, @"\s*[；;]\s*[，,]\s*", "；");
        value = Regex.Replace(value, @"\s+", " ");
        return value.Trim('，', ',', '；', ';', '：', ':', ' ');
    }

    private static string CleanOverlayDirectiveText(string? text)
    {
        var value = SanitizeOverlayDirectiveText(text);
        return PreserveOverlayText(value, OverlayDisplayMaxChars);
    }

    private static string CleanOverlayAiDirectiveText(string? text)
        => PreserveOverlayText(text, OverlayDisplayMaxChars);

    private static string ResolveAiSecondaryDisplayText(
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        string primaryLine)
    {
        if (!string.IsNullOrWhiteSpace(llmDecision.ShortReason))
            return CleanOverlayAiDirectiveText(llmDecision.ShortReason);

        return primaryLine;
    }

    private static string StripOverlayTimingDetails(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = text.Trim();
        value = Regex.Replace(value, @"[，,；;]?\s*(?:预计|倒计时|剩余|可契约时间|契约时间|可争夺时间)\s*[^，,；;]*", string.Empty);
        value = Regex.Replace(value, @"[，,；;]?\s*(?:可契约|可争夺)\s*\d{1,2}:\d{2}", string.Empty);
        value = Regex.Replace(value, @"[，,；;]?\s*(?:距离\s*)?(?:可契约|可争夺)?状态还有[:：]?\s*\d{1,2}:\d{2}", string.Empty);
        value = Regex.Replace(value, @"[，,；;]?\s*(?:还有|剩余)[:：]?\s*\d{1,2}:\d{2}", string.Empty);
        value = Regex.Replace(value, @"\s+", " ");
        return PreserveOverlayText(NormalizeOverlayDirectiveText(value), OverlayDisplayMaxChars);
    }

    private static string SanitizeOverlayDirectiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = Regex.Replace(text.Trim(), @"\s+", " ");
        value = StripOverlayLeadLabel(value);
        value = Regex.Replace(value, @"[，,]?\s*(预计|倒计时|剩余)\s*\d{1,2}:\d{2}", string.Empty);
        value = Regex.Replace(value, @"[，,]?\s*(预计|倒计时|剩余)\s*\d+\s*分(?:\d+\s*秒)?", string.Empty);
        value = Regex.Replace(value, @"[，,]?\s*(预计|倒计时|剩余)\s*\d+\s*分钟?", string.Empty);
        value = Regex.Replace(value, @"[，,]?\s*(预计|倒计时|剩余)\s*\d+\s*秒", string.Empty);
        value = Regex.Replace(value, @"[，,；;]?\s*(?:目标)?剩余(?:情报|情报值)?\s*[:：]?\s*\d+(?:\.\d+)?%?", string.Empty);
        value = Regex.Replace(value, @"[，,；;]?\s*情报值剩余\s*[:：]?\s*\d+(?:\.\d+)?%?", string.Empty);
        return NormalizeOverlayDirectiveText(value);
    }

    private static string StripOverlayLeadLabel(string value)
    {
        var colon = value.IndexOfAny(new[] { '：', ':' });
        if (colon <= 0 || colon >= value.Length - 1 || colon > 8)
            return value;

        var prefix = value[..colon].Trim();
        if (prefix.Length == 0)
            return value[(colon + 1)..].Trim();

        return prefix.All(ch => char.IsLetterOrDigit(ch) || ch is >= '\u4e00' and <= '\u9fff')
            ? value[(colon + 1)..].Trim()
            : value;
    }

    private BattlefieldPriorityTargetSnapshot? ResolvePinnedObjective(
        BattlefieldDecisionSnapshot decision,
        long now)
    {
        var current = decision.ObjectivePriorityTarget;
        if (!current.HasValue || string.IsNullOrWhiteSpace(current.Value.TargetName))
        {
            if (pinnedObjectiveTarget.HasValue
                && pinnedObjectiveUpdatedAtTicks >= 0
                && now - pinnedObjectiveUpdatedAtTicks <= ObjectivePinHoldMs)
                return pinnedObjectiveTarget;

            pinnedObjectiveTarget = null;
            pinnedObjectiveUpdatedAtTicks = -1;
            return null;
        }

        if (!pinnedObjectiveTarget.HasValue)
        {
            pinnedObjectiveTarget = current.Value;
            pinnedObjectiveUpdatedAtTicks = now;
            return pinnedObjectiveTarget;
        }

        var pinned = pinnedObjectiveTarget.Value;
        if (IsSamePinnedObjective(pinned, current.Value))
        {
            pinnedObjectiveTarget = current.Value;
            pinnedObjectiveUpdatedAtTicks = now;
            return pinnedObjectiveTarget;
        }

        var pinExpired = pinnedObjectiveUpdatedAtTicks < 0 || now - pinnedObjectiveUpdatedAtTicks > ObjectivePinHoldMs;
        var currentClearlyBetter = current.Value.Priority >= pinned.Priority + ObjectivePinSwitchMargin
            || current.Value.Urgency >= pinned.Urgency + ObjectivePinSwitchMargin;
        if (pinExpired || currentClearlyBetter)
        {
            pinnedObjectiveTarget = current.Value;
            pinnedObjectiveUpdatedAtTicks = now;
        }

        return pinnedObjectiveTarget;
    }

    private static bool IsSamePinnedObjective(
        BattlefieldPriorityTargetSnapshot left,
        BattlefieldPriorityTargetSnapshot right)
    {
        if (!string.IsNullOrWhiteSpace(left.TargetName)
            && string.Equals(left.TargetName, right.TargetName, StringComparison.Ordinal))
            return true;

        return left.Position.LengthSquared() > 1f
            && right.Position.LengthSquared() > 1f
            && Vector3.Distance(left.Position, right.Position) <= 28f;
    }

    private static string BuildPinnedObjectiveLine(BattlefieldPriorityTargetSnapshot objective)
    {
        var target = string.IsNullOrWhiteSpace(objective.TargetName) ? "下一目标" : objective.TargetName.Trim();
        var action = string.IsNullOrWhiteSpace(objective.ActionText) ? string.Empty : Regex.Replace(objective.ActionText.Trim(), @"\s+", " ");
        if (string.IsNullOrWhiteSpace(action))
            return $"拿点：{target}";
        if (action.Contains(target, StringComparison.Ordinal))
            return $"拿点：{action}";

        return $"拿点：{target}｜{action}";
    }

    private string BuildHudPrimaryLine(
        BattlefieldSnapshot snapshot,
        long now,
        CommandOverlayConfiguration config)
    {
        _ = now;
        var decision = snapshot.Decision;
        var commands = decision.CommandSituation;
        var aiDisplayText = ResolveFreshAiHudText(snapshot.LlmStrategicDecision);
        if (!string.IsNullOrWhiteSpace(aiDisplayText))
            return aiDisplayText;

        if (commands.EmergencyCommand.HasValue)
            return PreserveOverlayText(commands.EmergencyCommand.Value.CommandText, HudPrimaryMaxChars);

        if (commands.PrimaryCommand.HasValue && IsHudDirectiveCommand(commands.PrimaryCommand.Value))
            return PreserveOverlayText(commands.PrimaryCommand.Value.CommandText, HudPrimaryMaxChars);

        var combatCommand = ResolveCombatExecutionCommand(commands);
        if (combatCommand.HasValue)
            return PreserveOverlayText(combatCommand.Value.CommandText, HudPrimaryMaxChars);

        if (decision.PrimaryAction.HasValue)
            return PreserveOverlayText(decision.PrimaryAction.Value.Text, HudPrimaryMaxChars);
        if (commands.PrimaryAction.HasValue)
            return PreserveOverlayText(commands.PrimaryAction.Value.Text, HudPrimaryMaxChars);
        if (decision.PublishedAction.HasValue)
            return PreserveOverlayText(decision.PublishedAction.Value.Text, HudPrimaryMaxChars);
        if (commands.PublishedAction.HasValue)
            return PreserveOverlayText(commands.PublishedAction.Value.Text, HudPrimaryMaxChars);

        if (commands.PrimaryCommand.HasValue)
            return PreserveOverlayText(commands.PrimaryCommand.Value.CommandText, HudPrimaryMaxChars);

        if (!string.IsNullOrWhiteSpace(decision.RecommendedAction))
            return PreserveOverlayText(decision.RecommendedAction, HudPrimaryMaxChars);

        return config.ShowPrimaryWhenIdle ? "主团跟我，等下一条指令" : string.Empty;
    }

    private static string ResolveFreshAiHudText(BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!llmDecision.IsAvailable || !llmDecision.IsFresh)
            return string.Empty;

        return PreserveOverlayText(LlmStrategicTextResolver.ResolvePrimaryDisplayText(llmDecision), HudPrimaryMaxChars);
    }

    private static string BuildHudContextLine(
        BattlefieldSnapshot snapshot,
        CommandOverlayConfiguration config)
    {
        var parts = new List<string>(2);

        if (parts.Count < 2 && config.ShowReason)
        {
            var reason = ResolveHudReason(snapshot.Decision);
            if (!string.IsNullOrWhiteSpace(reason))
                parts.Add(reason);
        }

        if (parts.Count == 0)
        {
            var objective = BuildHudObjectiveCue(snapshot.Decision);
            if (!string.IsNullOrWhiteSpace(objective))
                parts.Add(objective);
        }

        return CompactHudText(string.Join(" | ", parts.Take(2)), HudContextMaxChars);
    }

    private static string BuildHudAlertLine(BattlefieldSnapshot snapshot)
    {
        var risk = snapshot.Decision.RiskAssessment;
        var advanced = snapshot.TeamSituation.AdvancedTactics;

        if ((advanced.IsThirdPartyPincerLikely || risk.ThirdPartyPincerRisk >= 72f || risk.EncirclementRisk >= 78f)
            && risk.OverallRisk >= 58f)
            return "警：两家夹角成形，别深追";
        if (advanced.IsHighGroundDropPrepLikely || risk.HighGroundDropRisk >= 70f)
            return "警：高台有人，低地横向散开";
        if (risk.SkillThreatRisk >= 82f || risk.LimitBreakRisk >= 82f)
            return "警：敌爆发窗口，横拉骗完再打";
        if (advanced.IsChokeBlockedLikely || risk.ChokeBlockRisk >= 78f)
            return "警：前方卡口，别直线穿口";
        if (risk.NumberDisadvantageRisk >= 78f)
            return "警：人数劣势，先收住重整";

        return string.Empty;
    }

    private static string BuildHudCommandText(
        BattlefieldCommandSnapshot command,
        BattlefieldDecisionSnapshot decision)
    {
        _ = decision;
        return PreserveOverlayText(command.CommandText, HudPrimaryMaxChars);
    }

    private static string BuildHudActionText(
        BattlefieldActionCandidateSnapshot action,
        BattlefieldDecisionSnapshot decision)
    {
        _ = decision;
        return PreserveOverlayText(action.Text, HudPrimaryMaxChars);
    }

    private static string BuildHudEnemyCue(BattlefieldSnapshot snapshot)
    {
        var movement = snapshot.TeamSituation.EnemyMainGroupMovement;
        if (!movement.HasMainGroup)
            return string.Empty;

        var direction = CompactHudText(movement.DirectionText, 12);
        if (string.IsNullOrWhiteSpace(direction)
            || string.Equals(direction, "原地", StringComparison.Ordinal)
            || string.Equals(direction, "unknown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(direction, "动向不明", StringComparison.Ordinal))
            return string.Empty;

        return $"敌{movement.PlayerCount}人 {direction}";
    }

    private static string BuildHudObjectiveCue(BattlefieldDecisionSnapshot decision)
    {
        if (!decision.ObjectivePriorityTarget.HasValue)
            return string.Empty;

        var target = ShortTargetName(decision.ObjectivePriorityTarget.Value.TargetName, HudTargetMaxChars);
        return string.IsNullOrWhiteSpace(target) ? string.Empty : $"目标 {target}";
    }

    private static string ResolveHudReason(BattlefieldDecisionSnapshot decision)
    {
        var reason = string.Empty;
        if (decision.CommandSituation.PrimaryCommand.HasValue)
            reason = decision.CommandSituation.PrimaryCommand.Value.ReasonText;
        if (string.IsNullOrWhiteSpace(reason) && decision.PrimaryAction.HasValue)
            reason = decision.PrimaryAction.Value.ReasonText;
        return BuildHudReasonCue(reason);
    }

    private static bool IsHudDirectiveCommand(BattlefieldCommandSnapshot command)
        => command.Id.StartsWith("doctrine:", StringComparison.Ordinal)
            || command.Id.StartsWith("tempo:", StringComparison.Ordinal)
            || command.Kind is BattlefieldCommandKind.Retreat
                or BattlefieldCommandKind.Disengage
                or BattlefieldCommandKind.Spread
                or BattlefieldCommandKind.Hold
                or BattlefieldCommandKind.Wait
                or BattlefieldCommandKind.AbandonObjective;

    private static string BuildHudIntentText(string id, string rawText, string target, int? countdownSeconds)
    {
        if (string.IsNullOrWhiteSpace(id))
            return string.Empty;

        if (id.StartsWith("fight:decision:kite", StringComparison.Ordinal))
            return "拉出夹角，回头反打";
        if (id.StartsWith("micro:focus-countdown", StringComparison.Ordinal))
        {
            var countdown = ResolveHudCountdownText(rawText, countdownSeconds);
            return $"敌方大团，{countdown}，AOE打人最多处";
        }

        if (id.StartsWith("micro:group-countdown", StringComparison.Ordinal))
        {
            var countdown = ResolveHudCountdownText(rawText, countdownSeconds);
            return $"敌方大团，{countdown}，AOE打人最多处";
        }

        if (id.StartsWith("doctrine:aoe-countdown", StringComparison.Ordinal))
            return "三秒倒数打敌方大团，AOE打人最多处";
        if (id.StartsWith("fight:decision", StringComparison.Ordinal))
        {
            var countdown = ExtractCountdownText(rawText);
            if (!string.IsNullOrWhiteSpace(countdown))
                return $"敌方大团，{countdown}，AOE打人最多处";

            var method = ExtractFightMethodText(rawText);
            if (!string.IsNullOrWhiteSpace(method))
                return BuildHudTargetText("压 ", target, $"，{method}", $"先压敌主团，{method}");

            return BuildHudTargetText("压 ", target, "，打人最多处", "压敌方大团，打人最多处");
        }

        if (id.StartsWith("micro:focus-target", StringComparison.Ordinal))
            return BuildHudTargetText("收 ", target, "，只打低血/被控", "收低血/被控，别追散人");
        if (id.StartsWith("micro:fight-method", StringComparison.Ordinal))
        {
            var method = ExtractFightMethodText(rawText);
            if (string.IsNullOrWhiteSpace(method))
                method = "前排开路";
            return BuildHudTargetText("压 ", target, $"，{method}", $"{method}，远程打人最多处");
        }

        if (id.StartsWith("doctrine:return-reset", StringComparison.Ordinal))
            return "能返回就返回，重整再接";
        if (id.StartsWith("doctrine:spread-precast", StringComparison.Ordinal))
            return "横向散开，等技能交完";
        if (id.StartsWith("doctrine:interrupt-touch", StringComparison.Ordinal))
            return BuildHudTargetText("先去摸 ", target, "，别让白拿", "先去摸点，别让白拿");
        if (id.StartsWith("doctrine:cover-touch", StringComparison.Ordinal))
            return BuildHudTargetText("前压掩护 ", target, string.Empty, "前压掩护摸点");
        if (id.StartsWith("doctrine:big-ice-all-in", StringComparison.Ordinal))
            return BuildHudTargetText("全力 ", target, "，别分火", "全力大冰，别分火");
        if (id.StartsWith("doctrine:feint-bait", StringComparison.Ordinal))
            return "假压骗技能，上去就撤";
        if (id.StartsWith("doctrine:hit-and-run", StringComparison.Ordinal))
            return "打一套就走，别深追";
        if (id.StartsWith("doctrine:single-target", StringComparison.Ordinal))
            return "压敌方大团边缘，别追散人";
        if (id.StartsWith("doctrine:split-scout", StringComparison.Ordinal))
            return BuildHudTargetText("1-4人去 ", target, "，主团别散", "1-4人摸副点，主团别散");
        if (id.StartsWith("doctrine:losing-kill-all-in", StringComparison.Ordinal))
            return "局势落后，就地收人头";

        if (id.StartsWith("tempo:kill-win-now", StringComparison.Ordinal))
            return "差人头能赢，直接收人";
        if (id.StartsWith("tempo:final-minute-all-in", StringComparison.Ordinal))
            return "最后1分钟，贴脸抢分";
        if (id.StartsWith("tempo:last-three-minutes-force-fight", StringComparison.Ordinal))
            return "决赛圈抢分，能开就开";
        if (id.StartsWith("tempo:third-place-score-first", StringComparison.Ordinal))
            return BuildHudTargetText("老三先补 ", target, "，别打工", "老三先补分，别打工");
        if (id.StartsWith("tempo:leader-exit-crossfire", StringComparison.Ordinal))
            return "领先出夹角，等反打";
        if (id.StartsWith("tempo:stop-after-profit", StringComparison.Ordinal))
            return "收益够了，出夹角";
        if (id.StartsWith("tempo:stop-farming-score", StringComparison.Ordinal))
            return BuildHudTargetText("转去 ", target, string.Empty, "转去下一目标点");
        if (id.StartsWith("tempo:opening-probe", StringComparison.Ordinal))
            return "开局接一波，留撤退线";
        if (id.StartsWith("tempo:early-battle-high-farm", StringComparison.Ordinal))
            return "前期找架，优先滚战意";
        if (id.StartsWith("tempo:early-pick-window", StringComparison.Ordinal))
            return "抓落单残血，收人滚战意";
        if (id.StartsWith("tempo:push-after-kill", StringComparison.Ordinal))
            return "刚出击杀，前压别过深";
        if (id.StartsWith("tempo:must-score", StringComparison.Ordinal))
            return "时间不多，先抢分";
        if (id.StartsWith("tempo:leave-expiring", StringComparison.Ordinal))
            return "尾分别缠，提前带下波";
        if (id.StartsWith("tempo:leading-pressure", StringComparison.Ordinal))
            return "领先控关键点，压追分家";

        return string.Empty;
    }

    private static string ResolveHudCountdownText(string rawText, int? countdownSeconds)
    {
        if (countdownSeconds is > 0)
            return $"{Math.Clamp(countdownSeconds.Value, 1, 3)}秒倒数";

        var extracted = ExtractCountdownText(rawText);
        return string.IsNullOrWhiteSpace(extracted) ? "3秒倒数" : extracted;
    }

    private static string BuildHudTargetText(string prefix, string target, string suffix, string fallback)
        => string.IsNullOrWhiteSpace(target) ? fallback : $"{prefix}{target}{suffix}";

    private static string BuildHudReasonCue(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return string.Empty;

        var value = reason.Trim();
        if (value.Contains("团簇", StringComparison.Ordinal) && value.Contains("技能已交", StringComparison.Ordinal))
            return "团距够近，敌技能已交";
        if (value.Contains("倒数条件不足", StringComparison.Ordinal))
            return "倒数条件不足，先压敌方大团边缘";
        if (value.Contains("返回", StringComparison.Ordinal) || value.Contains("重整", StringComparison.Ordinal))
            return "死亡波次不齐，先重整";
        if (value.Contains("打断", StringComparison.Ordinal) || value.Contains("白拿", StringComparison.Ordinal))
            return "资源要结算，先断摸点";
        if (value.Contains("夹角", StringComparison.Ordinal) || value.Contains("第三方", StringComparison.Ordinal) || value.Contains("两家", StringComparison.Ordinal))
            return "第三方靠近，留撤退线";
        if (value.Contains("高台", StringComparison.Ordinal) || value.Contains("空降", StringComparison.Ordinal))
            return "高台有威胁，低地别站桩";
        if (value.Contains("战意", StringComparison.Ordinal))
            return "先滚战意，再转资源";
        if (value.Contains("补分", StringComparison.Ordinal) || value.Contains("抢分", StringComparison.Ordinal))
            return "比分压力高，先拿分";
        if (value.Contains("资源", StringComparison.Ordinal) || value.Contains("目标", StringComparison.Ordinal))
            return "围绕资源打，别追散兵";

        return CompactHudText(value, 18);
    }

    private static string CompactHudText(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = StripHudPrefix(text.Trim())
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("立刻", string.Empty, StringComparison.Ordinal)
            .Replace("继续", string.Empty, StringComparison.Ordinal)
            .Replace("全体", string.Empty, StringComparison.Ordinal)
            .Replace("主团", string.Empty, StringComparison.Ordinal)
            .Replace("；", "，", StringComparison.Ordinal)
            .Replace(";", "，", StringComparison.Ordinal)
            .Replace("。", string.Empty, StringComparison.Ordinal)
            .Replace("、", "，", StringComparison.Ordinal)
            .Replace("|", "，", StringComparison.Ordinal)
            .Trim();

        value = string.Join(" ", value.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim('，', ',', '：', ':', '(', '（', ')', '）', ' ');
        if (maxChars > 0 && value.Length > maxChars)
        {
            var cut = FindHudCutIndex(value, maxChars);
            if (cut <= 0)
                cut = Math.Min(maxChars, value.Length);
            value = value[..cut].Trim('，', ',', '：', ':', '(', '（', ')', '）', ' ');
        }

        return value;
    }

    private static string StripHudPrefix(string value)
    {
        var colon = value.IndexOfAny(new[] { '：', ':' });
        if (colon <= 0 || colon >= value.Length - 1)
            return value;

        var prefix = value[..colon].Trim();
        var suffix = value[(colon + 1)..].Trim();
        if (prefix.Contains("指挥", StringComparison.Ordinal)
            || prefix.Contains("语义", StringComparison.Ordinal)
            || prefix.Length >= 8
            || suffix.Contains("标一", StringComparison.Ordinal)
            || suffix.Contains("先", StringComparison.Ordinal)
            || suffix.Contains("压", StringComparison.Ordinal))
        {
            return suffix;
        }

        return value;
    }

    private static int FindHudCutIndex(string value, int maxChars)
    {
        var cut = -1;
        var minUseful = Math.Min(10, Math.Max(1, maxChars / 2));
        for (var i = 0; i < value.Length && i <= maxChars; i++)
        {
            if (i < minUseful)
                continue;
            if (value[i] is '，' or ',' or '：' or ':' or ' ')
                cut = i;
        }

        return cut;
    }

    private static string BuildActionLine(BattlefieldDecisionSnapshot decision)
    {
        var text = ResolveNonCombatActionText(decision);
        if (string.IsNullOrWhiteSpace(text))
            text = "我带主团，等战斗指令";

        return $"行动：{text}";
    }

    private static string ResolveNonCombatActionText(BattlefieldDecisionSnapshot decision)
    {
        if (decision.ObjectivePriorityTarget.HasValue
            && !string.IsNullOrWhiteSpace(decision.ObjectivePriorityTarget.Value.ActionText))
        {
            return decision.ObjectivePriorityTarget.Value.ActionText;
        }

        if (decision.PrimaryAction.HasValue && !IsCombatAction(decision.PrimaryAction.Value))
            return decision.PrimaryAction.Value.Text;
        if (decision.PublishedAction.HasValue && !IsCombatAction(decision.PublishedAction.Value))
            return decision.PublishedAction.Value.Text;
        if (decision.CommandSituation.PrimaryCommand.HasValue
            && !IsCombatExecutionCommand(decision.CommandSituation.PrimaryCommand.Value))
        {
            return decision.CommandSituation.PrimaryCommand.Value.CommandText;
        }

        return string.Empty;
    }

    private static bool IsCombatAction(BattlefieldActionCandidateSnapshot action)
    {
        if (action.CommandId.StartsWith("fight:", StringComparison.Ordinal)
            || action.CommandId.StartsWith("micro:", StringComparison.Ordinal)
            || action.CommandId.StartsWith("engage:", StringComparison.Ordinal)
            || action.CommandId.StartsWith("emergency:", StringComparison.Ordinal)
            || action.CommandKind is BattlefieldCommandKind.Engage
                or BattlefieldCommandKind.FocusTarget
                or BattlefieldCommandKind.PressureSide
                or BattlefieldCommandKind.Retreat
                or BattlefieldCommandKind.Disengage
                or BattlefieldCommandKind.Spread)
        {
            return true;
        }

        return action.ActionType is BattlefieldActionType.Engage
            or BattlefieldActionType.FocusTarget
            or BattlefieldActionType.Flank
            or BattlefieldActionType.WrapBehind
            or BattlefieldActionType.BacklinePressure
            or BattlefieldActionType.ProtectHighBattleHigh
            or BattlefieldActionType.Spread;
    }

    private static string BuildActionLineLegacy(BattlefieldDecisionSnapshot decision)
    {
        var text = ResolveNonCombatActionText(decision);

        if (string.IsNullOrWhiteSpace(text))
            text = "等我下一条指令";

        return $"行动：{text}";
    }

    private static string BuildCommandLine(
        BattlefieldDecisionSnapshot decision,
        long now,
        CommandOverlayConfiguration config)
    {
        var commands = decision.CommandSituation;
        if (!config.ShowPrimaryWhenIdle)
            return string.Empty;

        var combatCommand = ResolveCombatExecutionCommand(commands);
        if (combatCommand.HasValue)
            return $"指令：{BuildCombatCommandDisplayText(combatCommand.Value, decision)}";
        if (decision.PrimaryAction.HasValue && IsCombatAction(decision.PrimaryAction.Value))
            return $"指令：{BuildCombatActionDisplayText(decision.PrimaryAction.Value, decision)}";
        if (commands.PrimaryAction.HasValue && IsCombatAction(commands.PrimaryAction.Value))
            return $"指令：{BuildCombatActionDisplayText(commands.PrimaryAction.Value, decision)}";
        if (decision.PublishedAction.HasValue && IsCombatAction(decision.PublishedAction.Value))
            return $"指令：{BuildCombatActionDisplayText(decision.PublishedAction.Value, decision)}";
        if (commands.PublishedAction.HasValue && IsCombatAction(commands.PublishedAction.Value))
            return $"指令：{BuildCombatActionDisplayText(commands.PublishedAction.Value, decision)}";
        var combatAction = ResolveCombatActionCandidate(decision);
        if (combatAction.HasValue)
            return $"指令：{BuildCombatActionDisplayText(combatAction.Value, decision)}";
        if (decision.FightPriorityTarget.HasValue
            && !string.IsNullOrWhiteSpace(decision.FightPriorityTarget.Value.ActionText))
        {
            return $"指令：{CompactOverlayText(decision.FightPriorityTarget.Value.ActionText, 34)}";
        }
        if (commands.PrimaryCommand.HasValue)
            return $"指令：{BuildCombatCommandDisplayText(commands.PrimaryCommand.Value, decision)}";
        if (commands.PrimaryAction.HasValue)
            return $"指令：{BuildCombatActionDisplayText(commands.PrimaryAction.Value, decision)}";
        return "指令：主团跟我压进";
    }

    private static BattlefieldCommandSnapshot? ResolveCombatExecutionCommand(BattlefieldCommandSituationSnapshot commands)
    {
        return commands.Commands
            .Where(IsCombatExecutionCommand)
            .OrderByDescending(CombatExecutionPriority)
            .ThenByDescending(command => command.Urgency)
            .ThenByDescending(command => command.Score)
            .Select(command => (BattlefieldCommandSnapshot?)command)
            .FirstOrDefault();
    }

    private static BattlefieldActionCandidateSnapshot? ResolveCombatActionCandidate(BattlefieldDecisionSnapshot decision)
    {
        return decision.ActionCandidates
            .Concat(decision.CommandSituation.ActionCandidates)
            .Where(IsCombatAction)
            .OrderByDescending(action => action.Priority)
            .ThenByDescending(action => action.Urgency)
            .ThenByDescending(action => action.Confidence)
            .Select(action => (BattlefieldActionCandidateSnapshot?)action)
            .FirstOrDefault();
    }

    private static bool IsCombatExecutionCommand(BattlefieldCommandSnapshot command)
        => command.Id.StartsWith("fight:decision", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:focus-countdown", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:group-countdown", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:fight-method", StringComparison.Ordinal)
            || command.Kind is BattlefieldCommandKind.FocusTarget
                or BattlefieldCommandKind.Engage
                or BattlefieldCommandKind.PressureSide;

    private static float CombatExecutionPriority(BattlefieldCommandSnapshot command)
    {
        if (command.Id.StartsWith("fight:decision", StringComparison.Ordinal))
            return 1000f + command.Score;
        if (command.Id.StartsWith("micro:group-countdown", StringComparison.Ordinal))
            return 940f + command.Score;
        if (command.Id.StartsWith("micro:focus-countdown", StringComparison.Ordinal))
            return 920f + command.Score;
        if (command.Id.StartsWith("micro:fight-method", StringComparison.Ordinal))
            return 880f + command.Score;
        if (command.Kind == BattlefieldCommandKind.FocusTarget)
            return 820f + command.Score;
        if (command.Kind == BattlefieldCommandKind.Engage)
            return 760f + command.Score;
        if (command.Kind == BattlefieldCommandKind.PressureSide)
            return 720f + command.Score;

        return command.Score;
    }

    private static string BuildCombatCommandDisplayText(
        BattlefieldCommandSnapshot command,
        BattlefieldDecisionSnapshot decision)
    {
        var fallbackTarget = decision.FightPriorityTarget?.TargetName;
        var target = ShortTargetName(command.TargetName, 10);
        if (string.IsNullOrWhiteSpace(target))
            target = ShortTargetName(fallbackTarget, 10);

        if (command.Id.StartsWith("fight:decision:kite", StringComparison.Ordinal)
            || command.Kind == BattlefieldCommandKind.Disengage
            || command.Kind == BattlefieldCommandKind.Retreat)
        {
            return BuildKiteCommandText(command.CommandText, target);
        }

        var countdown = ExtractCountdownText(command.CommandText);
        var method = ExtractFightMethodText(command.CommandText);
        var targetText = BuildTargetText(command.Kind, target);

        if (command.Id.StartsWith("micro:group-countdown", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:focus-countdown", StringComparison.Ordinal)
            || (!string.IsNullOrWhiteSpace(countdown) && command.Id.StartsWith("fight:decision", StringComparison.Ordinal)))
            return JoinOverlayParts(countdown, "打敌方大团", "AOE人最多处");

        if (command.Id.StartsWith("micro:focus-countdown", StringComparison.Ordinal))
            return JoinOverlayParts(targetText, countdown);

        if (command.Id.StartsWith("micro:fight-method", StringComparison.Ordinal))
            return JoinOverlayParts(method, targetText);

        if (command.Id.StartsWith("fight:decision", StringComparison.Ordinal))
            return JoinOverlayParts(targetText, countdown, method);

        return JoinOverlayParts(BuildCompactCommandText(command, target), method);
    }

    private static string BuildCombatActionDisplayText(
        BattlefieldActionCandidateSnapshot action,
        BattlefieldDecisionSnapshot decision)
    {
        var fallbackTarget = decision.FightPriorityTarget?.TargetName;
        var target = ShortTargetName(action.TargetName, 10);
        if (string.IsNullOrWhiteSpace(target))
            target = ShortTargetName(fallbackTarget, 10);

        return JoinOverlayParts(BuildCompactActionText(action, target), ExtractFightMethodText(action.Text));
    }

    private static string BuildTargetText(BattlefieldCommandKind kind, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return kind switch
            {
                BattlefieldCommandKind.FocusTarget => "收低血/被控",
                BattlefieldCommandKind.Engage => "正面开团",
                BattlefieldCommandKind.PressureSide => "侧压接团",
                _ => "主团跟我压进"
            };
        }

        return kind switch
        {
            BattlefieldCommandKind.FocusTarget => $"收 {target}",
            BattlefieldCommandKind.Engage => $"压 {target}",
            BattlefieldCommandKind.PressureSide => $"侧压 {target}",
            _ => $"打 {target}"
        };
    }

    private static string BuildKiteCommandText(string text, string target)
    {
        var method = ExtractFightMethodText(text);
        if (string.IsNullOrWhiteSpace(method))
            method = "先拉开";

        var reengage = string.IsNullOrWhiteSpace(target) ? "等脱节反打" : $"反打 {target}";
        return JoinOverlayParts(method, reengage);
    }

    private static string ExtractCountdownText(string text)
    {
        if (text.Contains("1秒倒数", StringComparison.Ordinal))
            return "1秒倒数";
        if (text.Contains("2秒倒数", StringComparison.Ordinal))
            return "2秒倒数";
        if (text.Contains("3秒倒数", StringComparison.Ordinal))
            return "3秒倒数";
        if (text.Contains("倒数", StringComparison.Ordinal))
            return "倒数爆发";
        return string.Empty;
    }

    private static string ExtractFightMethodText(string text)
    {
        if (text.Contains("横拉5秒", StringComparison.Ordinal))
            return "横拉5秒";
        if (text.Contains("不进夹角", StringComparison.Ordinal))
            return "不进夹角";
        if (text.Contains("横向拉开骗爆发", StringComparison.Ordinal)
            || text.Contains("横向拉开骗技能", StringComparison.Ordinal))
            return "骗爆发";
        if (text.Contains("慢退到火力线", StringComparison.Ordinal))
            return "退火力线";
        if (text.Contains("目标残血可收", StringComparison.Ordinal))
            return "现在收人";
        if (text.Contains("控场窗口", StringComparison.Ordinal))
            return "控场压一波";
        if (text.Contains("敌方分散", StringComparison.Ordinal))
            return "抓分散";
        if (text.Contains("战意/极限技窗口", StringComparison.Ordinal))
            return "开团窗口";
        if (text.Contains("卡住路口", StringComparison.Ordinal)
            || text.Contains("卡住路口打", StringComparison.Ordinal))
            return "卡口打";
        if (text.Contains("压到技能距离", StringComparison.Ordinal))
            return "压技能距离";
        if (text.Contains("正面推进", StringComparison.Ordinal))
            return "正面推";
        if (text.Contains("边进", StringComparison.Ordinal) && text.Contains("边打", StringComparison.Ordinal))
            return "边进点边打";
        if (text.Contains("打出人数差", StringComparison.Ordinal))
            return "打人数差";
        if (text.Contains("前排开路", StringComparison.Ordinal))
            return "前排开路";

        return string.Empty;
    }

    private static string JoinOverlayParts(params string[] parts)
    {
        var compact = parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => CompactPhrase(part.Trim(), 12))
            .Distinct(StringComparer.Ordinal)
            .Take(3)
            .ToArray();
        return string.Join("｜", compact);
    }

    private static string CompactPhrase(string text, int maxChars)
    {
        var value = text.Trim();
        var split = value.IndexOfAny(new[] { '；', ';', '。' });
        if (split > 0)
            value = value[..split].Trim();

        value = value
            .Replace("主团", string.Empty, StringComparison.Ordinal)
            .Replace("全体", string.Empty, StringComparison.Ordinal)
            .Replace("继续", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (maxChars > 0 && value.Length > maxChars)
        {
            var cut = FindHudCutIndex(value, maxChars);
            if (cut <= 0)
                cut = Math.Min(maxChars, value.Length);
            value = value[..cut].Trim('，', ',', '：', ':', '(', '（', ')', '）', ' ');
        }

        return value;
    }

    private string BuildCommandLineLegacy(
        BattlefieldDecisionSnapshot decision,
        long now,
        CommandOverlayConfiguration config)
    {
        _ = now;
        var commands = decision.CommandSituation;

        if (!config.ShowPrimaryWhenIdle)
            return string.Empty;

        if (commands.PrimaryCommand.HasValue)
            return $"指令：{commands.PrimaryCommand.Value.CommandText}";
        if (commands.PrimaryAction.HasValue)
            return $"指令：{commands.PrimaryAction.Value.Text}";
        return "指令：主团跟我压进";
    }

    private string BuildBattleLine(
        BattlefieldDecisionSnapshot decision,
        long now,
        CommandOverlayConfiguration config)
    {
        _ = now;
        var commands = decision.CommandSituation;
        var fightTarget = ShortTargetName(decision.FightPriorityTarget?.TargetName, 10);

        foreach (var command in commands.Commands)
        {
            if (!IsCombatCommand(command))
                continue;

            return BuildBattleLineText(BuildCompactCommandText(command, fightTarget), fightTarget);
        }

        if (!config.ShowPrimaryWhenIdle)
            return string.Empty;

        if (commands.PrimaryCommand.HasValue && IsCombatCommand(commands.PrimaryCommand.Value))
            return BuildBattleLineText(BuildCompactCommandText(commands.PrimaryCommand.Value, fightTarget), fightTarget);
        if (commands.PrimaryAction.HasValue)
            return BuildBattleLineText(BuildCompactActionText(commands.PrimaryAction.Value, fightTarget), fightTarget);
        return "战斗：主团跟我压进";
    }

    private static string BuildBattleLineText(string actionText, string fightTarget)
    {
        var action = CompactOverlayText(actionText, 18);
        if (string.IsNullOrWhiteSpace(action))
            action = "主团跟我压进";

        return $"战斗：{action}";
    }

    private static string BuildCompactCommandText(BattlefieldCommandSnapshot command, string fallbackTarget)
    {
        if (command.Id.StartsWith("tempo:stop-farming-score", StringComparison.Ordinal))
            return CompactOverlayText(command.CommandText, 24);

        var target = ShortTargetName(command.TargetName, 10);
        if (string.IsNullOrWhiteSpace(target))
            target = ShortTargetName(fallbackTarget, 10);

        return command.Kind switch
        {
            BattlefieldCommandKind.FocusTarget => string.IsNullOrWhiteSpace(target) ? "收低血/被控" : $"收 {target}",
            BattlefieldCommandKind.Engage => string.IsNullOrWhiteSpace(target) ? "正面开团" : $"压 {target}",
            BattlefieldCommandKind.PressureSide => BuildPressureText(command, target),
            BattlefieldCommandKind.ProtectTarget => string.IsNullOrWhiteSpace(target) ? "保高战意" : $"保 {target}",
            BattlefieldCommandKind.Spread => "散开防爆",
            BattlefieldCommandKind.Hold => command.Id.StartsWith("objective:hold:", StringComparison.Ordinal) ? "挂边观察" : "卡住反打",
            BattlefieldCommandKind.Split => string.IsNullOrWhiteSpace(target) ? "分队绕后" : $"分队 {target}",
            BattlefieldCommandKind.Regroup => "主团跟我压",
            BattlefieldCommandKind.Detour => "换线压进",
            _ => CompactOverlayText(command.CommandText, 18)
        };
    }

    private static string BuildCompactActionText(BattlefieldActionCandidateSnapshot action, string fallbackTarget)
    {
        var target = ShortTargetName(action.TargetName, 10);
        if (string.IsNullOrWhiteSpace(target))
            target = ShortTargetName(fallbackTarget, 10);

        return action.ActionType switch
        {
            BattlefieldActionType.FocusTarget => string.IsNullOrWhiteSpace(target) ? "收低血/被控" : $"收 {target}",
            BattlefieldActionType.Engage => string.IsNullOrWhiteSpace(target) ? "正面开团" : $"压 {target}",
            BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind => string.IsNullOrWhiteSpace(target) ? "绕后夹击" : $"绕后 {target}",
            BattlefieldActionType.BacklinePressure => "压后排",
            BattlefieldActionType.ProtectHighBattleHigh => "保高战意",
            BattlefieldActionType.Spread => "散开防爆",
            BattlefieldActionType.Hold => "挂边观察",
            _ => CompactOverlayText(action.Text, 18)
        };
    }

    private static string BuildPressureText(BattlefieldCommandSnapshot command, string target)
    {
        if (command.Id.Contains("behind", StringComparison.Ordinal) || command.Id.Contains("wrap", StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(target) ? "绕后夹击" : $"绕后 {target}";
        if (command.Id.Contains("flank", StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(target) ? "侧压" : $"侧压 {target}";
        return string.IsNullOrWhiteSpace(target) ? "侧压逼退" : $"侧压 {target}";
    }

    private static string BuildCompactObjectiveText(BattlefieldPriorityTargetSnapshot? objective)
    {
        if (!objective.HasValue)
            return string.Empty;

        var item = objective.Value;
        var target = ShortTargetName(item.TargetName, 10);
        var action = CompactOverlayText(item.ActionText, 14);
        if (string.IsNullOrWhiteSpace(target))
            return action;
        if (string.IsNullOrWhiteSpace(action) || action.Contains(target, StringComparison.Ordinal))
            return target;
        if (action.Contains("挂边", StringComparison.Ordinal) || action.Contains("观察", StringComparison.Ordinal))
            return "挂边观察";
        return $"{action} {target}";
    }

    private static string ShortTargetName(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = text.Trim();
        value = value.Replace("敌方", "敌", StringComparison.Ordinal)
            .Replace("主团", "团", StringComparison.Ordinal)
            .Replace("当前", string.Empty, StringComparison.Ordinal)
            .Replace("目标", string.Empty, StringComparison.Ordinal);
        return CompactOverlayText(value, maxChars);
    }

    private static string PreserveOverlayText(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = Regex.Replace(text.Trim(), @"\s+", " ");
        if (maxChars > 0 && value.Length > maxChars)
        {
            var cut = FindHudCutIndex(value, maxChars);
            if (cut <= 0)
                cut = Math.Min(maxChars, value.Length);
            value = value[..cut].Trim('，', ',', '：', ':', '(', '（', ')', '）', ' ');
        }

        return value;
    }

    private static string CompactOverlayText(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = text.Trim();
        var split = value.IndexOfAny(new[] { '；', ';', '，', ',', '。', '：', ':' });
        if (split > 0)
            value = value[..split].Trim();

        value = value.Replace("主团", "团", StringComparison.Ordinal)
            .Replace("立刻", string.Empty, StringComparison.Ordinal)
            .Replace("继续", string.Empty, StringComparison.Ordinal)
            .Replace("预计", string.Empty, StringComparison.Ordinal)
            .Trim();
        if (maxChars > 0 && value.Length > maxChars)
        {
            var cut = FindHudCutIndex(value, maxChars);
            if (cut <= 0)
                cut = Math.Min(maxChars, value.Length);
            value = value[..cut].Trim('，', ',', '：', ':', '(', '（', ')', '）', ' ');
        }

        return value;
    }

    private static string BuildObjectiveMacroTargetLine(BattlefieldDecisionSnapshot decision)
    {
        var objective = BuildCompactObjectiveText(decision.ObjectivePriorityTarget);
        var macro = ResolveMacroDecisionText(decision);
        var compactText = string.IsNullOrWhiteSpace(objective)
            ? macro
            : $"{macro}｜{objective}";
        return $"大决策：{CompactOverlayText(compactText, 26)}";
    }

    private static string ResolveMacroDecisionText(BattlefieldDecisionSnapshot decision)
    {
        if (decision.PrimaryAction.HasValue)
        {
            return decision.PrimaryAction.Value.ActionType switch
            {
                BattlefieldActionType.ContestObjective or BattlefieldActionType.TouchObjective or BattlefieldActionType.InterruptTouch => "抢点压人",
                BattlefieldActionType.AttackIce => "打冰接团",
                BattlefieldActionType.Engage => "正面开团",
                BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => "正压分绕",
                BattlefieldActionType.Rotate => "提前压位",
                BattlefieldActionType.DefendObjective or BattlefieldActionType.Hold => "控边等机会",
                BattlefieldActionType.ProtectHighBattleHigh => "保高战意",
                BattlefieldActionType.Spread => "散开防爆",
                BattlefieldActionType.Detour => "换线推进",
                _ => "围绕目标推进"
            };
        }

        if (decision.CommandSituation.PrimaryCommand.HasValue)
        {
            return decision.CommandSituation.PrimaryCommand.Value.Kind switch
            {
                BattlefieldCommandKind.FocusTarget => "收割造差",
                BattlefieldCommandKind.Engage => "正面开团",
                BattlefieldCommandKind.PressureSide => "侧压逼退",
                BattlefieldCommandKind.ContestObjective => "抢点压人",
                BattlefieldCommandKind.AttackObjective => "拿分转火",
                BattlefieldCommandKind.ProtectTarget => "保护反打",
                BattlefieldCommandKind.Hold or BattlefieldCommandKind.Wait => "挂边观察",
                _ => "围绕目标推进"
            };
        }

        return "围绕目标推进";
    }

    private static string BuildEnemyMovementLine(BattlefieldSnapshot snapshot)
    {
        var risk = snapshot.Decision.RiskAssessment;
        var advanced = snapshot.TeamSituation.AdvancedTactics;
        var movement = snapshot.TeamSituation.EnemyMainGroupMovement;
        var parts = new List<string>(3);

        if (movement.HasMainGroup)
        {
            var name = string.IsNullOrWhiteSpace(movement.AllianceName) ? "敌团" : movement.AllianceName;
            var direction = string.IsNullOrWhiteSpace(movement.DirectionText) ? "动向不明" : movement.DirectionText;
            parts.Add($"{name}{movement.PlayerCount}人 {direction}");
        }

        var highGroundInsight = FindInsight(advanced, BattlefieldAdvancedTacticalInsightKind.HighGroundDropPrep);
        if (advanced.IsHighGroundDropPrepLikely || risk.HighGroundDropRisk >= 62f || highGroundInsight?.Severity >= 55f)
        {
            var source = highGroundInsight?.Label;
            if (string.IsNullOrWhiteSpace(source))
            {
                var zone = FindHighGroundThreatZone(snapshot.MapTactics.TopZones);
                source = string.IsNullOrWhiteSpace(zone?.Label) ? "高台" : zone.Value.Label;
            }

            parts.Add($"{source}有人准备下压");
        }

        if (advanced.IsThirdPartyPincerLikely || risk.ThirdPartyPincerRisk >= 68f)
            parts.Add("第三方靠近夹击");

        if (advanced.IsChokeBlockedLikely || risk.ChokeBlockRisk >= 70f)
            parts.Add("前方卡口有人堵路");

        if (movement.IsEnemySplit || snapshot.TeamSituation.IsEnemySplit)
            parts.Add("敌方分兵");

        if (parts.Count == 0)
            parts.Add("暂无明显动向");
        if (parts.Count > 3)
            parts.RemoveRange(3, parts.Count - 3);

        return $"敌军动向：{string.Join("；", parts)}";
    }

    private static string BuildThreatLine(BattlefieldSnapshot snapshot)
    {
        var risk = snapshot.Decision.RiskAssessment;
        var advanced = snapshot.TeamSituation.AdvancedTactics;
        var parts = new List<string>(4);

        var highGroundInsight = FindInsight(advanced, BattlefieldAdvancedTacticalInsightKind.HighGroundDropPrep);
        if (advanced.IsHighGroundDropPrepLikely || risk.HighGroundDropRisk >= 62f || highGroundInsight?.Severity >= 55f)
        {
            var source = highGroundInsight?.Label;
            if (string.IsNullOrWhiteSpace(source))
            {
                var zone = FindHighGroundThreatZone(snapshot.MapTactics.TopZones);
                source = string.IsNullOrWhiteSpace(zone?.Label) ? "高台" : zone.Value.Label;
            }

            parts.Add($"高台空降 {source}，低地别站桩，横向拉开");
        }

        if (advanced.IsThirdPartyPincerLikely || risk.ThirdPartyPincerRisk >= 68f)
            parts.Add("第三方夹击靠近，别压太深");

        if (advanced.IsChokeBlockedLikely || risk.ChokeBlockRisk >= 70f)
            parts.Add("前方卡口压力高，别直线穿口");

        var topEnemyLb = snapshot.TeamSituation.LimitBreakThreats.TopEnemyThreats.Length > 0
            ? snapshot.TeamSituation.LimitBreakThreats.TopEnemyThreats[0]
            : default;
        if (topEnemyLb.GameObjectId != 0 && (risk.LimitBreakRisk >= 66f || topEnemyLb.ThreatLevel is BattlefieldLimitBreakThreatLevel.High or BattlefieldLimitBreakThreatLevel.Critical))
            parts.Add($"敌方极限技 {topEnemyLb.Name}({topEnemyLb.JobName}) {topEnemyLb.EstimatedPercent:0}%");

        var topEnemySkill = snapshot.TeamSituation.KeySkillThreats.TopEnemyThreats.Length > 0
            ? snapshot.TeamSituation.KeySkillThreats.TopEnemyThreats[0]
            : default;
        if (topEnemySkill.GameObjectId != 0 && (risk.SkillThreatRisk >= 66f || topEnemySkill.ThreatLevel is BattlefieldLimitBreakThreatLevel.High or BattlefieldLimitBreakThreatLevel.Critical))
            parts.Add($"关键技能 {topEnemySkill.Name} {topEnemySkill.SkillName}");

        if (parts.Count == 0 && advanced.TopInsight.HasValue)
            parts.Add($"{advanced.TopInsight.Value.Label}，{advanced.TopInsight.Value.Recommendation}");

        if (parts.Count == 0)
            parts.Add("暂无致命威胁，主团跟我并看侧翼");

        if (parts.Count > 2)
            parts.RemoveRange(2, parts.Count - 2);
        for (var i = 0; i < parts.Count; i++)
            parts[i] = CompactOverlayText(parts[i], 14);

        return $"威胁：{string.Join("｜", parts)}";
    }

    private static BattlefieldAdvancedTacticalInsightSnapshot? FindInsight(
        BattlefieldAdvancedTacticalSituationSnapshot advanced,
        BattlefieldAdvancedTacticalInsightKind kind)
    {
        foreach (var insight in advanced.Insights)
        {
            if (insight.Kind == kind)
                return insight;
        }

        return null;
    }

    private static BattlefieldMapTacticalZoneSnapshot? FindHighGroundThreatZone(IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones)
    {
        foreach (var zone in zones)
        {
            if (zone.Kind == MapAnnotationKind.HighGround && zone.TotalRisk >= 58f)
                return zone;
        }

        return null;
    }

    private static bool IsCombatCommand(BattlefieldCommandSnapshot command)
        => command.Kind is BattlefieldCommandKind.Engage
            or BattlefieldCommandKind.FocusTarget
            or BattlefieldCommandKind.PressureSide
            or BattlefieldCommandKind.ProtectTarget
            or BattlefieldCommandKind.Spread
            or BattlefieldCommandKind.Hold
            or BattlefieldCommandKind.Split
            || command.Id.StartsWith("fight:", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:", StringComparison.Ordinal)
            || command.Id.StartsWith("engage:", StringComparison.Ordinal)
            || command.Id.StartsWith("target:", StringComparison.Ordinal)
            || command.Id.StartsWith("intent:", StringComparison.Ordinal)
            || command.Id.StartsWith("role:", StringComparison.Ordinal)
            || command.Id.StartsWith("tempo:early", StringComparison.Ordinal);

    private void DrawTextWindow(
        OverlayDisplayContent content,
        CommandOverlayConfiguration config)
    {
        var position = ResolvePosition(config);
        var size = new Vector2(config.Width, config.Height);
        var flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoInputs;
        if (!config.ShowBackground)
            flags |= ImGuiWindowFlags.NoBackground;

        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(config.ShowBackground ? config.BackgroundAlpha : 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 10f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        if (ImGui.Begin("##ai02CommandOverlay", flags))
        {
            var width = MathF.Max(120f, config.Width - 32f);
            var defaultTextColor = new Vector4(config.TextColorR, config.TextColorG, config.TextColorB, 1f);
            var aiTextColor = new Vector4(config.AiTextColorR, config.AiTextColorG, config.AiTextColorB, 1f);
            var textColor = content.IsAiLead ? aiTextColor : defaultTextColor;
            var currentActionColor = content.IsAiLead
                ? BlendColor(aiTextColor, new Vector4(0.60f, 1f, 0.84f, 1f), 0.34f)
                : new Vector4(0.48f, 0.90f, 0.62f, 1f);
            var strokeColor = new Vector4(config.StrokeColorR, config.StrokeColorG, config.StrokeColorB, 1f);
            if (content.IsAiLead)
                DrawAiLeadBadge(config, aiTextColor, strokeColor);
            DrawWrappedText(content.PrimaryCommandLine, Math.Clamp(config.FontScale * 0.72f, 1.0f, 3.2f), width, textColor, strokeColor, config.ShowStroke, objectiveFontHandle);
            ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new Vector2(0f, 2f));
            DrawWrappedText(content.CurrentActionLine, Math.Clamp(config.FontScale * 0.44f, 0.76f, 2.0f), width, currentActionColor, strokeColor, config.ShowStroke, threatFontHandle);
            ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new Vector2(0f, 2f));
            DrawWrappedText(content.ThreatLine, Math.Clamp(config.FontScale * 0.38f, 0.70f, 1.8f), width, new Vector4(1f, 0.42f, 0.30f, 1f), strokeColor, config.ShowStroke, battleFontHandle);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private void DrawAiLeadBadge(
        CommandOverlayConfiguration config,
        Vector4 aiTextColor,
        Vector4 strokeColor)
    {
        const string label = "AI";
        IFontHandle? badgeFont = null;
        var badgeScale = Math.Clamp(config.FontScale * 0.34f, 0.76f, 1.15f);
        var textSize = MeasureOverlayText(label, badgeScale, badgeFont);
        var padding = new Vector2(6f, 2f);
        var badgeSize = textSize + padding * 2f;
        var windowPos = ImGui.GetWindowPos();
        var badgePos = windowPos + new Vector2(
            MathF.Max(10f, config.Width - badgeSize.X - 14f),
            10f);
        var backgroundColor = BlendColor(aiTextColor, new Vector4(0.04f, 0.08f, 0.12f, 0.96f), 0.58f);
        var borderColor = BlendColor(aiTextColor, Vector4.One, 0.28f);
        var drawList = ImGui.GetWindowDrawList();
        drawList.AddRectFilled(badgePos, badgePos + badgeSize, ImGui.ColorConvertFloat4ToU32(backgroundColor), 5f);
        drawList.AddRect(badgePos, badgePos + badgeSize, ImGui.ColorConvertFloat4ToU32(borderColor), 5f, ImDrawFlags.None, 1.2f);
        DrawOverlayBadgeText(label, badgePos + padding, badgeScale, aiTextColor, strokeColor, config.ShowStroke, badgeFont);
    }

    private static Vector2 ResolvePosition(CommandOverlayConfiguration config)
    {
        var display = ImGui.GetIO().DisplaySize;
        var x = Math.Clamp(config.X, 0f, MathF.Max(0f, display.X - 40f));
        var y = Math.Clamp(config.Y, 0f, MathF.Max(0f, display.Y - 40f));
        return new Vector2(x, y);
    }

    private void DrawWrappedText(
        string text,
        float fontScale,
        float wrapWidth,
        Vector4 textColor,
        Vector4 strokeColor,
        bool showStroke,
        IFontHandle? explicitHandle)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var fontHandle = explicitHandle;
        var push = fontHandle?.Push();
        var useScaledFallback = push is null;
        if (useScaledFallback)
            ImGui.SetWindowFontScale(fontScale);

        var displayText = text;
        var start = ImGui.GetCursorScreenPos();
        var stroke = MathF.Max(1f, fontScale);
        if (showStroke)
        {
            DrawWrappedTextAt(displayText, start + new Vector2(-stroke, 0f), wrapWidth, strokeColor, fontHandle);
            DrawWrappedTextAt(displayText, start + new Vector2(stroke, 0f), wrapWidth, strokeColor, fontHandle);
            DrawWrappedTextAt(displayText, start + new Vector2(0f, -stroke), wrapWidth, strokeColor, fontHandle);
            DrawWrappedTextAt(displayText, start + new Vector2(0f, stroke), wrapWidth, strokeColor, fontHandle);
            DrawWrappedTextAt(displayText, start + new Vector2(-stroke, -stroke), wrapWidth, strokeColor, fontHandle);
            DrawWrappedTextAt(displayText, start + new Vector2(stroke, -stroke), wrapWidth, strokeColor, fontHandle);
            DrawWrappedTextAt(displayText, start + new Vector2(-stroke, stroke), wrapWidth, strokeColor, fontHandle);
            DrawWrappedTextAt(displayText, start + new Vector2(stroke, stroke), wrapWidth, strokeColor, fontHandle);
        }

        DrawWrappedTextAt(displayText, start, wrapWidth, textColor, fontHandle);
        if (useScaledFallback)
            ImGui.SetWindowFontScale(1f);

        push?.Dispose();
    }

    private static void DrawWrappedTextAt(
        string text,
        Vector2 position,
        float wrapWidth,
        Vector4 color,
        IFontHandle? fontHandle)
    {
        ImGui.SetCursorScreenPos(position);
        ImGui.PushTextWrapPos(position.X + wrapWidth);
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        var push = fontHandle?.Push();
        ImGui.TextWrapped(text);
        push?.Dispose();
        ImGui.PopStyleColor();
        ImGui.PopTextWrapPos();
    }

    private static Vector2 MeasureOverlayText(string text, float fontScale, IFontHandle? fontHandle)
    {
        var push = fontHandle?.Push();
        var useScaledFallback = push is null;
        if (useScaledFallback)
            ImGui.SetWindowFontScale(fontScale);

        var size = ImGui.CalcTextSize(text);

        if (useScaledFallback)
            ImGui.SetWindowFontScale(1f);

        push?.Dispose();
        return size;
    }

    private static void DrawOverlayBadgeText(
        string text,
        Vector2 position,
        float fontScale,
        Vector4 textColor,
        Vector4 strokeColor,
        bool showStroke,
        IFontHandle? fontHandle)
    {
        var push = fontHandle?.Push();
        var useScaledFallback = push is null;
        if (useScaledFallback)
            ImGui.SetWindowFontScale(fontScale);

        var drawList = ImGui.GetWindowDrawList();
        if (showStroke)
        {
            var stroke = MathF.Max(1f, fontScale);
            DrawOverlayBadgeTextAt(drawList, text, position + new Vector2(-stroke, 0f), strokeColor);
            DrawOverlayBadgeTextAt(drawList, text, position + new Vector2(stroke, 0f), strokeColor);
            DrawOverlayBadgeTextAt(drawList, text, position + new Vector2(0f, -stroke), strokeColor);
            DrawOverlayBadgeTextAt(drawList, text, position + new Vector2(0f, stroke), strokeColor);
        }

        DrawOverlayBadgeTextAt(drawList, text, position, textColor);

        if (useScaledFallback)
            ImGui.SetWindowFontScale(1f);

        push?.Dispose();
    }

    private static void DrawOverlayBadgeTextAt(ImDrawListPtr drawList, string text, Vector2 position, Vector4 color)
        => drawList.AddText(position, ImGui.ColorConvertFloat4ToU32(color), text);

    private static Vector4 BlendColor(Vector4 source, Vector4 target, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return new Vector4(
            source.X + (target.X - source.X) * amount,
            source.Y + (target.Y - source.Y) * amount,
            source.Z + (target.Z - source.Z) * amount,
            source.W + (target.W - source.W) * amount);
    }

    private void EnsureOverlayFonts(CommandOverlayConfiguration config)
    {
        var basePx = UiBuilder.DefaultFontSizePx;
        var desiredObjectivePx = Math.Clamp((int)MathF.Round(basePx * config.FontScale * 0.72f), 18, 84);
        var desiredBattlePx = Math.Clamp((int)MathF.Round(basePx * config.FontScale * 0.38f), 14, 48);
        var desiredThreatPx = Math.Clamp((int)MathF.Round(basePx * config.FontScale * 0.44f), 15, 54);
        if (desiredObjectivePx == objectiveFontSizePx
            && desiredBattlePx == battleFontSizePx
            && desiredThreatPx == threatFontSizePx
            && objectiveFontHandle != null
            && battleFontHandle != null
            && threatFontHandle != null)
            return;

        DisposeOverlayFonts();
        objectiveFontHandle = CreateDalamudDefaultFontHandle(desiredObjectivePx);
        battleFontHandle = CreateDalamudDefaultFontHandle(desiredBattlePx);
        threatFontHandle = CreateDalamudDefaultFontHandle(desiredThreatPx);
        objectiveFontSizePx = desiredObjectivePx;
        battleFontSizePx = desiredBattlePx;
        threatFontSizePx = desiredThreatPx;
    }

    private IFontHandle CreateDalamudDefaultFontHandle(int sizePx)
        => uiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            tk.AddDalamudDefaultFont(sizePx);
        }));

    private void DisposeOverlayFonts()
    {
        objectiveFontHandle?.Dispose();
        battleFontHandle?.Dispose();
        threatFontHandle?.Dispose();
        objectiveFontHandle = null;
        battleFontHandle = null;
        threatFontHandle = null;
        objectiveFontSizePx = 0;
        battleFontSizePx = 0;
        threatFontSizePx = 0;
    }

    private readonly record struct OverlayDisplayContent(
        string PrimaryCommandLine,
        string CurrentActionLine,
        string ThreatLine,
        bool IsAiLead);
}
