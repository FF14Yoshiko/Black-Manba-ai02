using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ai02;

public sealed class StrategicArbitrationService
{
    private const float MinConfidenceToLead = 36f;
    private const float EmergencyRiskFallbackThreshold = 96f;
    private const float EmergencyActionFallbackUrgency = 94f;

    public BattlefieldDecisionSnapshot Apply(
        BattlefieldSnapshot snapshot,
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!ShouldApply(localDecision, llmDecision))
            return localDecision;

        var directive = ResolveDirective(snapshot, localDecision, llmDecision);
        if (!directive.IsResolved)
            return localDecision;

        if (ShouldKeepExtremeLocalEmergency(localDecision, directive))
            return localDecision;

        var matchedAction = ResolveMatchedAction(localDecision, directive);
        var matchedCommand = ResolveMatchedCommand(localDecision, directive, matchedAction);
        var displayText = ResolveDisplayText(llmDecision, directive, matchedAction);
        var scope = ResolveScope(directive, matchedAction);
        var aiAction = BuildAiAction(directive, llmDecision, localDecision, matchedAction, displayText, scope);
        var aiCommand = BuildAiCommand(directive, llmDecision, localDecision, matchedCommand, aiAction, displayText, scope);
        var publish = BuildAiPublish(localDecision, llmDecision, aiCommand, displayText);
        var commands = MergeCommands(localDecision.CommandSituation.Commands, aiCommand);
        var actions = MergeActions(localDecision.ActionCandidates, aiAction);
        var objectiveTarget = ResolveObjectivePriorityTarget(localDecision, directive, aiAction, llmDecision);
        var fightTarget = ResolveFightPriorityTarget(localDecision, directive, aiAction, llmDecision);

        return new BattlefieldDecisionSnapshot
        {
            IsAvailable = true,
            ObjectivePriorities = localDecision.ObjectivePriorities,
            PrimaryObjective = localDecision.PrimaryObjective,
            ObjectivePriorityTarget = objectiveTarget,
            FightPriorityTarget = fightTarget,
            RiskAssessment = localDecision.RiskAssessment,
            CommandSituation = new BattlefieldCommandSituationSnapshot
            {
                IsAvailable = true,
                Commands = commands,
                ActionCandidates = actions,
                PrimaryCommand = aiCommand,
                EmergencyCommand = localDecision.CommandSituation.EmergencyCommand,
                PrimaryAction = aiAction,
                PublishedAction = aiAction,
                IsActionHoldActive = false,
                ActionHoldRemainingSeconds = Math.Max(3, aiAction.HoldSeconds),
                ActionHoldReason = "AI 战术接管主指令",
                Publish = publish,
                SummaryText = $"AI 主导：{displayText}；{BuildReasonText(llmDecision, directive)}"
            },
            ActionCandidates = actions,
            PrimaryAction = aiAction,
            PublishedAction = aiAction,
            DecisionQuality = localDecision.DecisionQuality,
            RecommendedAction = displayText,
            SummaryText = $"AI 接管：{displayText}；本地原判：{localDecision.RecommendedAction}"
        };
    }

    private static bool ShouldApply(
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!localDecision.IsAvailable)
            return false;
        if (!llmDecision.IsAvailable || !llmDecision.IsFresh)
            return false;
        if (llmDecision.Confidence < MinConfidenceToLead)
            return false;

        return !string.IsNullOrWhiteSpace(llmDecision.RecommendedAction)
            || !string.IsNullOrWhiteSpace(llmDecision.Decision);
    }

    private static bool ShouldKeepExtremeLocalEmergency(
        BattlefieldDecisionSnapshot localDecision,
        StrategicDirective directive)
    {
        var risk = localDecision.RiskAssessment;
        if (directive.ActionType is BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase or BattlefieldActionType.Spread)
            return false;

        if (risk.OverallRisk >= EmergencyRiskFallbackThreshold
            || risk.CombatRisk >= EmergencyRiskFallbackThreshold
            || risk.LimitBreakRisk >= EmergencyRiskFallbackThreshold)
        {
            return true;
        }

        var localAction = localDecision.CommandSituation.PrimaryAction ?? localDecision.PrimaryAction;
        return localAction.HasValue
            && localAction.Value.ActionType is BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase or BattlefieldActionType.Spread
            && localAction.Value.Urgency >= EmergencyActionFallbackUrgency;
    }

    private static StrategicDirective ResolveDirective(
        BattlefieldSnapshot snapshot,
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        var rawText = ResolveDirectiveSourceText(llmDecision);
        if (string.IsNullOrWhiteSpace(rawText))
            return default;

        var normalized = Normalize(rawText);
        var actionType = ResolveActionType(normalized);
        var commandKind = ResolveCommandKind(actionType);
        var target = ResolveTarget(snapshot, localDecision, llmDecision, normalized, actionType);
        return new StrategicDirective(
            true,
            rawText,
            BuildFallbackDirectiveText(actionType, target.Name, target.EtaSeconds, target.CountdownSeconds),
            actionType,
            commandKind,
            target,
            ResolvePurpose(actionType));
    }

    private static string ResolveDirectiveSourceText(BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!string.IsNullOrWhiteSpace(llmDecision.RecommendedAction))
            return llmDecision.RecommendedAction.Trim();
        if (!string.IsNullOrWhiteSpace(llmDecision.Decision))
            return llmDecision.Decision.Trim();
        return string.Empty;
    }

    private static BattlefieldActionType ResolveActionType(string normalized)
    {
        if (ContainsAny(normalized, "回家", "回补", "回基地", "回出生"))
            return BattlefieldActionType.ReturnToBase;
        if (ContainsAny(normalized, "撤退", "回撤", "后撤", "脱离", "撤出", "拉开"))
            return BattlefieldActionType.Retreat;
        if (ContainsAny(normalized, "断摸点", "打断摸点", "打断占点", "断点"))
            return BattlefieldActionType.InterruptTouch;
        if (ContainsAny(normalized, "抢点", "抢占", "争夺"))
            return BattlefieldActionType.ContestObjective;
        if (ContainsAny(normalized, "摸点", "占点", "踩点"))
            return BattlefieldActionType.TouchObjective;
        if (ContainsAny(normalized, "守点", "控点", "防守"))
            return BattlefieldActionType.DefendObjective;
        if (ContainsAny(normalized, "放弃", "不硬抢", "别去", "弃点"))
            return BattlefieldActionType.AbandonObjective;
        if (ContainsAny(normalized, "转点", "轮转", "转去", "换点"))
            return BattlefieldActionType.Rotate;
        if (ContainsAny(normalized, "绕后", "后包", "包后"))
            return BattlefieldActionType.WrapBehind;
        if (ContainsAny(normalized, "夹击", "包夹", "侧夹"))
            return BattlefieldActionType.Flank;
        if (ContainsAny(normalized, "压后排", "切后排", "侧压", "压后"))
            return BattlefieldActionType.BacklinePressure;
        if (ContainsAny(normalized, "打第一", "压第一", "打高分", "打领头", "打高战意", "集火", "收割"))
            return BattlefieldActionType.FocusTarget;
        if (ContainsAny(normalized, "接团", "参战", "开团", "打团", "碰团"))
            return BattlefieldActionType.Engage;
        if (ContainsAny(normalized, "散开", "分散", "展开"))
            return BattlefieldActionType.Spread;
        if (ContainsAny(normalized, "靠拢", "收缩", "集结"))
            return BattlefieldActionType.Regroup;
        if (ContainsAny(normalized, "卡口", "卡住", "hold"))
            return BattlefieldActionType.Hold;
        if (ContainsAny(normalized, "等", "等待", "观望"))
            return BattlefieldActionType.Wait;
        return BattlefieldActionType.Engage;
    }

    private static BattlefieldCommandKind ResolveCommandKind(BattlefieldActionType actionType)
        => actionType switch
        {
            BattlefieldActionType.Rotate => BattlefieldCommandKind.Rotate,
            BattlefieldActionType.DefendObjective => BattlefieldCommandKind.DefendObjective,
            BattlefieldActionType.ContestObjective => BattlefieldCommandKind.ContestObjective,
            BattlefieldActionType.AbandonObjective => BattlefieldCommandKind.AbandonObjective,
            BattlefieldActionType.AttackIce => BattlefieldCommandKind.AttackObjective,
            BattlefieldActionType.TouchObjective => BattlefieldCommandKind.AttackObjective,
            BattlefieldActionType.InterruptTouch => BattlefieldCommandKind.FocusTarget,
            BattlefieldActionType.Engage => BattlefieldCommandKind.Engage,
            BattlefieldActionType.Retreat => BattlefieldCommandKind.Retreat,
            BattlefieldActionType.ReturnToBase => BattlefieldCommandKind.Disengage,
            BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => BattlefieldCommandKind.PressureSide,
            BattlefieldActionType.FocusTarget => BattlefieldCommandKind.FocusTarget,
            BattlefieldActionType.ProtectHighBattleHigh => BattlefieldCommandKind.ProtectTarget,
            BattlefieldActionType.Regroup => BattlefieldCommandKind.Regroup,
            BattlefieldActionType.Spread => BattlefieldCommandKind.Spread,
            BattlefieldActionType.Detour => BattlefieldCommandKind.Detour,
            BattlefieldActionType.Hold => BattlefieldCommandKind.Hold,
            BattlefieldActionType.Wait => BattlefieldCommandKind.Wait,
            _ => BattlefieldCommandKind.Engage
        };

    private static ResolvedTarget ResolveTarget(
        BattlefieldSnapshot snapshot,
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        string normalizedDirective,
        BattlefieldActionType actionType)
    {
        var priorityTargetText = Normalize(llmDecision.PriorityTarget);
        var objectives = localDecision.ObjectivePriorities;
        var scores = snapshot.ScoreSituation.RankedAlliances;

        var namedObjective = objectives.FirstOrDefault(objective =>
            ContainsNormalized(normalizedDirective, objective.Name)
            || ContainsNormalized(priorityTargetText, objective.Name));
        if (!string.IsNullOrWhiteSpace(namedObjective.ObjectiveId))
        {
            return new ResolvedTarget(
                namedObjective.ObjectiveId,
                namedObjective.Name,
                namedObjective.Position,
                namedObjective.MountedEtaSeconds,
                namedObjective.RemainingSeconds,
                true,
                false);
        }

        var namedAlliance = scores.FirstOrDefault(alliance =>
            !alliance.IsLocalAlliance
            && (ContainsNormalized(normalizedDirective, alliance.Name)
                || ContainsNormalized(priorityTargetText, alliance.Name)));
        if (!string.IsNullOrWhiteSpace(namedAlliance.Name))
        {
            var center = ResolveAllianceCenter(snapshot.TeamSituation, namedAlliance.Battalion);
            return new ResolvedTarget(
                $"alliance:{namedAlliance.AllianceId}",
                namedAlliance.Name,
                center,
                EstimateEtaSeconds(snapshot, center),
                null,
                false,
                true);
        }

        if (ContainsAny(normalizedDirective, "第一名", "第1", "第一", "高分", "领头"))
        {
            var enemyLeader = scores.FirstOrDefault(alliance => !alliance.IsLocalAlliance);
            if (!string.IsNullOrWhiteSpace(enemyLeader.Name))
            {
                var center = ResolveAllianceCenter(snapshot.TeamSituation, enemyLeader.Battalion);
                return new ResolvedTarget(
                    $"alliance:{enemyLeader.AllianceId}",
                    enemyLeader.Name,
                    center,
                    EstimateEtaSeconds(snapshot, center),
                    null,
                    false,
                    true);
            }
        }

        if (ContainsAny(normalizedDirective, "第二名", "第2", "第二"))
        {
            var enemySecond = scores.Where(alliance => !alliance.IsLocalAlliance).Skip(1).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(enemySecond.Name))
            {
                var center = ResolveAllianceCenter(snapshot.TeamSituation, enemySecond.Battalion);
                return new ResolvedTarget(
                    $"alliance:{enemySecond.AllianceId}",
                    enemySecond.Name,
                    center,
                    EstimateEtaSeconds(snapshot, center),
                    null,
                    false,
                    true);
            }
        }

        if (ContainsAny(normalizedDirective, "高价值点", "高分点"))
        {
            var highValue = objectives
                .Where(objective => objective.ScoreValue is >= 100)
                .OrderByDescending(objective => objective.PriorityScore)
                .ThenBy(objective => objective.MountedEtaSeconds)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(highValue.ObjectiveId))
            {
                return new ResolvedTarget(
                    highValue.ObjectiveId,
                    highValue.Name,
                    highValue.Position,
                    highValue.MountedEtaSeconds,
                    highValue.RemainingSeconds,
                    true,
                    false);
            }
        }

        if (ContainsAny(normalizedDirective, "远点"))
        {
            var farObjective = objectives
                .OrderByDescending(objective => objective.DistanceToLocal)
                .ThenByDescending(objective => objective.PriorityScore)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(farObjective.ObjectiveId))
            {
                return new ResolvedTarget(
                    farObjective.ObjectiveId,
                    farObjective.Name,
                    farObjective.Position,
                    farObjective.MountedEtaSeconds,
                    farObjective.RemainingSeconds,
                    true,
                    false);
            }
        }

        if (ContainsAny(normalizedDirective, "近点", "近侧点"))
        {
            var nearObjective = objectives
                .OrderBy(objective => objective.DistanceToLocal)
                .ThenByDescending(objective => objective.PriorityScore)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(nearObjective.ObjectiveId))
            {
                return new ResolvedTarget(
                    nearObjective.ObjectiveId,
                    nearObjective.Name,
                    nearObjective.Position,
                    nearObjective.MountedEtaSeconds,
                    nearObjective.RemainingSeconds,
                    true,
                    false);
            }
        }

        if (ContainsAny(normalizedDirective, "近侧敌团", "近敌团", "就近敌团"))
        {
            var nearCluster = snapshot.TeamSituation.EnemyClusters
                .OrderBy(cluster => cluster.DistanceToLocal)
                .FirstOrDefault();
            if (nearCluster != null)
            {
                return new ResolvedTarget(
                    $"cluster:{nearCluster.ClusterId}",
                    nearCluster.AllianceName,
                    nearCluster.Center,
                    EstimateEtaSeconds(snapshot, nearCluster.Center),
                    null,
                    false,
                    true);
            }
        }

        if (IsObjectiveAction(actionType))
        {
            var objective = localDecision.PrimaryObjective;
            if (objective.HasValue)
            {
                return new ResolvedTarget(
                    objective.Value.ObjectiveId,
                    objective.Value.Name,
                    objective.Value.Position,
                    objective.Value.MountedEtaSeconds,
                    objective.Value.RemainingSeconds,
                    true,
                    false);
            }
        }

        if (IsFightAction(actionType))
        {
            var fightTarget = localDecision.FightPriorityTarget;
            if (fightTarget.HasValue)
            {
                return new ResolvedTarget(
                    fightTarget.Value.TargetName,
                    fightTarget.Value.TargetName,
                    fightTarget.Value.Position,
                    EstimateEtaSeconds(snapshot, fightTarget.Value.Position),
                    null,
                    false,
                    true);
            }

            var mainEnemy = snapshot.TeamSituation.EnemyClusters
                .OrderByDescending(cluster => cluster.IsMainCluster)
                .ThenBy(cluster => cluster.DistanceToLocal)
                .FirstOrDefault();
            if (mainEnemy != null)
            {
                return new ResolvedTarget(
                    $"cluster:{mainEnemy.ClusterId}",
                    mainEnemy.AllianceName,
                    mainEnemy.Center,
                    EstimateEtaSeconds(snapshot, mainEnemy.Center),
                    null,
                    false,
                    true);
            }
        }

        var objectiveTarget = localDecision.ObjectivePriorityTarget;
        if (objectiveTarget.HasValue)
        {
            return new ResolvedTarget(
                objectiveTarget.Value.TargetName,
                objectiveTarget.Value.TargetName,
                objectiveTarget.Value.Position,
                EstimateEtaSeconds(snapshot, objectiveTarget.Value.Position),
                null,
                true,
                false);
        }

        return new ResolvedTarget(
            string.Empty,
            IsObjectiveAction(actionType) ? "目标点" : "敌方主团",
            Vector3.Zero,
            0,
            null,
            IsObjectiveAction(actionType),
            IsFightAction(actionType));
    }

    private static BattlefieldActionCandidateSnapshot? ResolveMatchedAction(
        BattlefieldDecisionSnapshot localDecision,
        StrategicDirective directive)
    {
        BattlefieldActionCandidateSnapshot? best = null;
        var bestScore = float.MinValue;
        foreach (var candidate in localDecision.ActionCandidates)
        {
            var score = 0f;
            if (candidate.ActionType == directive.ActionType)
                score += 55f;
            else if (candidate.CommandKind == directive.CommandKind)
                score += 36f;
            else if (IsObjectiveAction(candidate.ActionType) == IsObjectiveAction(directive.ActionType))
                score += 18f;
            else if (IsFightAction(candidate.ActionType) == IsFightAction(directive.ActionType))
                score += 18f;

            if (!string.IsNullOrWhiteSpace(directive.Target.Id)
                && string.Equals(candidate.TargetId, directive.Target.Id, StringComparison.Ordinal))
            {
                score += 35f;
            }
            else if (!string.IsNullOrWhiteSpace(directive.Target.Name)
                && (string.Equals(candidate.TargetName, directive.Target.Name, StringComparison.Ordinal)
                    || candidate.Text.Contains(directive.Target.Name, StringComparison.OrdinalIgnoreCase)
                    || candidate.DestinationName.Contains(directive.Target.Name, StringComparison.OrdinalIgnoreCase)))
            {
                score += 24f;
            }

            score += candidate.Priority * 0.06f + candidate.Confidence * 0.04f;
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = candidate;
        }

        return best;
    }

    private static BattlefieldCommandSnapshot? ResolveMatchedCommand(
        BattlefieldDecisionSnapshot localDecision,
        StrategicDirective directive,
        BattlefieldActionCandidateSnapshot? matchedAction)
    {
        if (matchedAction.HasValue && !string.IsNullOrWhiteSpace(matchedAction.Value.CommandId))
        {
            var commandFromAction = localDecision.CommandSituation.Commands
                .FirstOrDefault(command => string.Equals(command.Id, matchedAction.Value.CommandId, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(commandFromAction.Id))
                return commandFromAction;
        }

        BattlefieldCommandSnapshot? best = null;
        var bestScore = float.MinValue;
        foreach (var command in localDecision.CommandSituation.Commands)
        {
            var score = 0f;
            if (command.Kind == directive.CommandKind)
                score += 48f;
            if (!string.IsNullOrWhiteSpace(directive.Target.Name)
                && (string.Equals(command.TargetName, directive.Target.Name, StringComparison.Ordinal)
                    || command.CommandText.Contains(directive.Target.Name, StringComparison.OrdinalIgnoreCase)))
            {
                score += 28f;
            }

            score += command.Score * 0.08f + command.Urgency * 0.05f;
            if (score <= bestScore)
                continue;

            bestScore = score;
            best = command;
        }

        return best;
    }

    private static string ResolveDisplayText(
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        StrategicDirective directive,
        BattlefieldActionCandidateSnapshot? matchedAction)
    {
        if (!string.IsNullOrWhiteSpace(llmDecision.RecommendedAction))
            return llmDecision.RecommendedAction.Trim();
        if (!string.IsNullOrWhiteSpace(llmDecision.Decision) && llmDecision.Decision.Length <= 60)
            return llmDecision.Decision.Trim();
        if (matchedAction.HasValue && !string.IsNullOrWhiteSpace(matchedAction.Value.Text))
            return matchedAction.Value.Text;
        return directive.FallbackText;
    }

    private static string ResolveScope(StrategicDirective directive, BattlefieldActionCandidateSnapshot? matchedAction)
    {
        if (matchedAction.HasValue && !string.IsNullOrWhiteSpace(matchedAction.Value.Scope))
            return matchedAction.Value.Scope;

        return directive.ActionType switch
        {
            BattlefieldActionType.TouchObjective
                or BattlefieldActionType.Flank
                or BattlefieldActionType.WrapBehind
                or BattlefieldActionType.BacklinePressure => "分队",
            _ => "主团"
        };
    }

    private static BattlefieldActionCandidateSnapshot BuildAiAction(
        StrategicDirective directive,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldActionCandidateSnapshot? matchedAction,
        string displayText,
        string scope)
    {
        var template = matchedAction ?? localDecision.PrimaryAction ?? localDecision.CommandSituation.PrimaryAction;
        var reuseTemplateTarget = TemplateMatchesTarget(template, directive.Target);
        var eta = reuseTemplateTarget
            ? template?.EtaSeconds ?? directive.Target.EtaSeconds
            : directive.Target.EtaSeconds > 0 ? directive.Target.EtaSeconds : template?.EtaSeconds ?? 0;
        var countdown = reuseTemplateTarget
            ? template?.CountdownSeconds ?? directive.Target.CountdownSeconds
            : directive.Target.CountdownSeconds ?? template?.CountdownSeconds;
        var destination = reuseTemplateTarget
            ? template?.Destination ?? directive.Target.Position
            : directive.Target.Position != Vector3.Zero ? directive.Target.Position : template?.Destination ?? Vector3.Zero;
        var destinationName = reuseTemplateTarget
            ? !string.IsNullOrWhiteSpace(template?.DestinationName) ? template.Value.DestinationName : directive.Target.Name
            : !string.IsNullOrWhiteSpace(directive.Target.Name) ? directive.Target.Name : template?.DestinationName ?? string.Empty;
        var targetId = reuseTemplateTarget
            ? !string.IsNullOrWhiteSpace(template?.TargetId) ? template.Value.TargetId : directive.Target.Id
            : !string.IsNullOrWhiteSpace(directive.Target.Id) ? directive.Target.Id : template?.TargetId ?? string.Empty;
        var targetName = reuseTemplateTarget
            ? !string.IsNullOrWhiteSpace(template?.TargetName) ? template.Value.TargetName : directive.Target.Name
            : !string.IsNullOrWhiteSpace(directive.Target.Name) ? directive.Target.Name : template?.TargetName ?? string.Empty;
        var routeId = reuseTemplateTarget ? template?.RouteId ?? string.Empty : string.Empty;
        var routeText = reuseTemplateTarget ? template?.RouteText ?? string.Empty : string.Empty;
        var confidence = Math.Clamp(Math.Max(llmDecision.Confidence, template?.Confidence ?? 0f), 0f, 100f);
        var urgency = Math.Clamp(
            Math.Max(72f, template?.Urgency ?? 0f) + (directive.ActionType is BattlefieldActionType.FocusTarget or BattlefieldActionType.InterruptTouch ? 8f : 0f),
            0f,
            100f);
        var priority = Math.Clamp(Math.Max(84f, template?.Priority ?? 0f) + llmDecision.Confidence * 0.08f, 0f, 100f);
        var risk = template?.Risk ?? localDecision.RiskAssessment.OverallRisk;
        var holdSeconds = Math.Max(template?.HoldSeconds ?? 0, ResolveHoldSeconds(directive.ActionType));

        return new BattlefieldActionCandidateSnapshot(
            $"ai:action:{llmDecision.ReceivedAtTicks}:{directive.ActionType}:{SanitizeId(targetId, targetName)}",
            $"ai:command:{llmDecision.ReceivedAtTicks}:{directive.CommandKind}:{SanitizeId(targetId, targetName)}",
            directive.ActionType,
            directive.CommandKind,
            scope,
            displayText,
            priority,
            confidence,
            risk,
            urgency,
            destination,
            string.IsNullOrWhiteSpace(destinationName) ? directive.Target.Name : destinationName,
            targetId,
            string.IsNullOrWhiteSpace(targetName) ? directive.Target.Name : targetName,
            routeId,
            routeText,
            countdown,
            eta,
            holdSeconds,
            directive.PurposeText,
            BuildReasonText(llmDecision, directive),
            BuildEvidenceText(llmDecision, localDecision),
            ResolveFailureCondition(directive.ActionType));
    }

    private static bool TemplateMatchesTarget(BattlefieldActionCandidateSnapshot? template, ResolvedTarget target)
    {
        if (!template.HasValue)
            return false;

        if (string.IsNullOrWhiteSpace(target.Id) && string.IsNullOrWhiteSpace(target.Name))
            return true;

        if (!string.IsNullOrWhiteSpace(target.Id)
            && string.Equals(template.Value.TargetId, target.Id, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(target.Name)
            && string.Equals(template.Value.TargetName, target.Name, StringComparison.Ordinal);
    }

    private static BattlefieldCommandSnapshot BuildAiCommand(
        StrategicDirective directive,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldCommandSnapshot? matchedCommand,
        BattlefieldActionCandidateSnapshot aiAction,
        string displayText,
        string scope)
    {
        var targetName = !string.IsNullOrWhiteSpace(aiAction.TargetName)
            ? aiAction.TargetName
            : directive.Target.Name;
        var score = Math.Clamp(Math.Max(86f, matchedCommand?.Score ?? 0f) + llmDecision.Confidence * 0.06f, 0f, 100f);
        var urgency = Math.Clamp(Math.Max(74f, matchedCommand?.Urgency ?? aiAction.Urgency), 0f, 100f);
        return new BattlefieldCommandSnapshot(
            aiAction.CommandId,
            directive.CommandKind,
            string.IsNullOrWhiteSpace(scope) ? "主团" : scope,
            displayText,
            score,
            urgency,
            Math.Max(4, aiAction.HoldSeconds),
            aiAction.Destination,
            targetName,
            BuildReasonText(llmDecision, directive),
            BuildEvidenceText(llmDecision, localDecision));
    }

    private static BattlefieldCommandPublishSnapshot BuildAiPublish(
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        BattlefieldCommandSnapshot aiCommand,
        string displayText)
    {
        var basePublish = localDecision.CommandSituation.Publish;
        var input = localDecision.DecisionQuality.InputReliability;
        var shouldAnnounce = input.CanPublish && llmDecision.Confidence >= MinConfidenceToLead;
        return new BattlefieldCommandPublishSnapshot
        {
            ShouldAnnounce = shouldAnnounce,
            IsSuppressed = !shouldAnnounce,
            InterruptedCooldown = basePublish.InterruptedCooldown,
            Command = aiCommand,
            SpeakText = displayText,
            PriorityText = "AI 主导",
            StatusText = shouldAnnounce ? $"AI 主导发布：{displayText}" : $"AI 主导 HUD：{displayText}",
            SuppressionReason = shouldAnnounce ? string.Empty : input.GateText,
            GlobalCooldownRemainingSeconds = basePublish.GlobalCooldownRemainingSeconds,
            CommandCooldownRemainingSeconds = basePublish.CommandCooldownRemainingSeconds,
            KindCooldownRemainingSeconds = basePublish.KindCooldownRemainingSeconds,
            LastIssuedAgeMs = basePublish.LastIssuedAgeMs,
            Sequence = HashCode.Combine(aiCommand.Id, llmDecision.ReceivedAtTicks, displayText) & int.MaxValue
        };
    }

    private static BattlefieldCommandSnapshot[] MergeCommands(
        IReadOnlyList<BattlefieldCommandSnapshot> baseCommands,
        BattlefieldCommandSnapshot aiCommand)
        => baseCommands
            .Where(command => !CommandsEquivalent(command, aiCommand))
            .Prepend(aiCommand)
            .Take(10)
            .ToArray();

    private static BattlefieldActionCandidateSnapshot[] MergeActions(
        IReadOnlyList<BattlefieldActionCandidateSnapshot> baseActions,
        BattlefieldActionCandidateSnapshot aiAction)
        => baseActions
            .Where(action => !ActionsEquivalent(action, aiAction))
            .Prepend(aiAction)
            .Take(12)
            .ToArray();

    private static BattlefieldPriorityTargetSnapshot? ResolveObjectivePriorityTarget(
        BattlefieldDecisionSnapshot localDecision,
        StrategicDirective directive,
        BattlefieldActionCandidateSnapshot aiAction,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!directive.Target.IsObjective)
            return localDecision.ObjectivePriorityTarget;

        return new BattlefieldPriorityTargetSnapshot(
            "AI",
            string.IsNullOrWhiteSpace(aiAction.TargetName) ? directive.Target.Name : aiAction.TargetName,
            aiAction.Text,
            aiAction.Priority,
            aiAction.Urgency,
            aiAction.Destination,
            BuildReasonText(llmDecision, directive),
            BuildEvidenceText(llmDecision, localDecision));
    }

    private static BattlefieldPriorityTargetSnapshot? ResolveFightPriorityTarget(
        BattlefieldDecisionSnapshot localDecision,
        StrategicDirective directive,
        BattlefieldActionCandidateSnapshot aiAction,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!directive.Target.IsFightTarget)
            return localDecision.FightPriorityTarget;

        return new BattlefieldPriorityTargetSnapshot(
            "AI",
            string.IsNullOrWhiteSpace(aiAction.TargetName) ? directive.Target.Name : aiAction.TargetName,
            aiAction.Text,
            aiAction.Priority,
            aiAction.Urgency,
            aiAction.Destination,
            BuildReasonText(llmDecision, directive),
            BuildEvidenceText(llmDecision, localDecision));
    }

    private static bool CommandsEquivalent(BattlefieldCommandSnapshot left, BattlefieldCommandSnapshot right)
        => left.Kind == right.Kind
            && string.Equals(left.TargetName, right.TargetName, StringComparison.Ordinal)
            && string.Equals(left.CommandText, right.CommandText, StringComparison.Ordinal);

    private static bool ActionsEquivalent(BattlefieldActionCandidateSnapshot left, BattlefieldActionCandidateSnapshot right)
        => left.ActionType == right.ActionType
            && string.Equals(left.TargetId, right.TargetId, StringComparison.Ordinal)
            && string.Equals(left.TargetName, right.TargetName, StringComparison.Ordinal);

    private static string BuildReasonText(BattlefieldLlmStrategicDecisionSnapshot llmDecision, StrategicDirective directive)
        => !string.IsNullOrWhiteSpace(llmDecision.ShortReason)
            ? $"AI 理由：{llmDecision.ShortReason}"
            : $"AI 战术：{directive.RawText}";

    private static string BuildEvidenceText(
        BattlefieldLlmStrategicDecisionSnapshot llmDecision,
        BattlefieldDecisionSnapshot localDecision)
        => $"AI 置信 {llmDecision.Confidence:0} / 风险 {llmDecision.Risk:0}；本地原判：{localDecision.RecommendedAction}";

    private static string BuildFallbackDirectiveText(
        BattlefieldActionType actionType,
        string targetName,
        int etaSeconds,
        int? countdownSeconds)
    {
        var target = string.IsNullOrWhiteSpace(targetName) ? "目标" : targetName;
        var eta = etaSeconds > 0 ? $"，预计 {FormatDuration(etaSeconds)}" : string.Empty;
        var countdown = countdownSeconds.HasValue ? $"，倒计时 {FormatDuration(countdownSeconds.Value)}" : string.Empty;
        return actionType switch
        {
            BattlefieldActionType.Rotate => $"主团转点 {target}{eta}{countdown}",
            BattlefieldActionType.DefendObjective => $"主团守点 {target}{countdown}",
            BattlefieldActionType.ContestObjective => $"主团抢点 {target}{eta}{countdown}",
            BattlefieldActionType.AbandonObjective => $"主团放弃 {target}，改找更优战场",
            BattlefieldActionType.AttackIce => $"主团打冰 {target}{eta}{countdown}",
            BattlefieldActionType.TouchObjective => $"分队摸点 {target}{eta}{countdown}",
            BattlefieldActionType.InterruptTouch => $"控制/近战断摸点 {target}{eta}{countdown}",
            BattlefieldActionType.Engage => $"主团接团 {target}{eta}",
            BattlefieldActionType.Retreat => $"主团后撤脱战 {target}",
            BattlefieldActionType.ReturnToBase => "主团向复活会合点靠拢",
            BattlefieldActionType.Flank => $"分队夹击 {target}{eta}",
            BattlefieldActionType.WrapBehind => $"分队绕后 {target}{eta}",
            BattlefieldActionType.BacklinePressure => $"分队压后排 {target}{eta}",
            BattlefieldActionType.FocusTarget => $"主团打第一 {target}",
            BattlefieldActionType.ProtectHighBattleHigh => $"主团保护高战意 {target}",
            BattlefieldActionType.Regroup => $"主团靠拢重整 {target}",
            BattlefieldActionType.Spread => "主团横向展开，继续输出",
            BattlefieldActionType.Detour => $"主团换侧线压 {target}",
            BattlefieldActionType.Hold => $"主团卡住 {target}",
            BattlefieldActionType.Wait => $"主团等半拍看 {target}{countdown}",
            _ => $"主团处理 {target}"
        };
    }

    private static string ResolvePurpose(BattlefieldActionType actionType)
        => actionType switch
        {
            BattlefieldActionType.Rotate => "按 AI 指令提前换到更优战场",
            BattlefieldActionType.DefendObjective => "按 AI 指令稳住已有收益点",
            BattlefieldActionType.ContestObjective => "按 AI 指令强抢关键点收益",
            BattlefieldActionType.AbandonObjective => "按 AI 指令放弃低价值硬接团",
            BattlefieldActionType.AttackIce => "按 AI 指令转换资源分数",
            BattlefieldActionType.TouchObjective => "按 AI 指令低成本摸点",
            BattlefieldActionType.InterruptTouch => "按 AI 指令阻断敌方摸点节奏",
            BattlefieldActionType.Engage => "按 AI 指令接入当前主战场",
            BattlefieldActionType.Retreat => "按 AI 指令退出不利战场",
            BattlefieldActionType.ReturnToBase => "按 AI 指令回补或等复活波",
            BattlefieldActionType.Flank => "按 AI 指令从侧翼制造夹击",
            BattlefieldActionType.WrapBehind => "按 AI 指令压敌退路",
            BattlefieldActionType.BacklinePressure => "按 AI 指令切后排扰乱治疗链",
            BattlefieldActionType.FocusTarget => "按 AI 指令优先集火指定阵营或目标",
            BattlefieldActionType.ProtectHighBattleHigh => "按 AI 指令保护高价值友军",
            BattlefieldActionType.Regroup => "按 AI 指令重新压缩阵型",
            BattlefieldActionType.Spread => "按 AI 指令规避群控与极限技",
            BattlefieldActionType.Detour => "按 AI 指令改走侧线",
            BattlefieldActionType.Hold => "按 AI 指令卡位等待战机",
            BattlefieldActionType.Wait => "按 AI 指令等资源或等敌方先动",
            _ => "按 AI 指令调整当前战术"
        };

    private static string ResolveFailureCondition(BattlefieldActionType actionType)
        => actionType switch
        {
            BattlefieldActionType.Rotate => "目标消失/剩余时间不足/敌主团已先到",
            BattlefieldActionType.DefendObjective => "敌方压力解除/收益点已安全/新资源刷新",
            BattlefieldActionType.ContestObjective => "人数转劣/敌极限技威胁升高/目标收益下降",
            BattlefieldActionType.AbandonObjective => "目标风险下降或收益重新上升",
            BattlefieldActionType.AttackIce => "资源已空/敌方先手到位",
            BattlefieldActionType.TouchObjective => "摸点被断/敌主团压到",
            BattlefieldActionType.InterruptTouch => "敌方停止摸点或我方控制链不足",
            BattlefieldActionType.Engage => "人数转劣/敌爆发窗口打开/阵型散开",
            BattlefieldActionType.Retreat => "安全线恢复或敌方停止追击",
            BattlefieldActionType.ReturnToBase => "复活节奏恢复/新目标刷新",
            BattlefieldActionType.Flank => "主团脱节/侧线被封",
            BattlefieldActionType.WrapBehind => "退路变差/敌方回头包夹",
            BattlefieldActionType.BacklinePressure => "后排后撤/我方退路变差",
            BattlefieldActionType.FocusTarget => "目标无敌/防御或火力不足",
            BattlefieldActionType.ProtectHighBattleHigh => "保护目标已脱离集火",
            BattlefieldActionType.Regroup => "队形回稳并重新找到战机",
            BattlefieldActionType.Spread => "敌方爆发结束/需要重新集火",
            BattlefieldActionType.Detour => "侧线路径风险下降",
            BattlefieldActionType.Hold => "新资源刷新或比分压力要求主动出击",
            BattlefieldActionType.Wait => "敌方先手暴露或我方到齐",
            _ => "关键条件变化时重新评估"
        };

    private static int ResolveHoldSeconds(BattlefieldActionType actionType)
        => actionType switch
        {
            BattlefieldActionType.FocusTarget => 6,
            BattlefieldActionType.TouchObjective or BattlefieldActionType.InterruptTouch => 6,
            BattlefieldActionType.Engage => 10,
            BattlefieldActionType.Rotate or BattlefieldActionType.ContestObjective or BattlefieldActionType.AttackIce => 16,
            BattlefieldActionType.DefendObjective or BattlefieldActionType.Hold => 14,
            BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase => 14,
            BattlefieldActionType.Regroup => 12,
            BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => 14,
            BattlefieldActionType.Spread => 6,
            BattlefieldActionType.Wait => 10,
            _ => 8
        };

    private static Vector3 ResolveAllianceCenter(BattlefieldTeamSituationSnapshot teamSituation, byte? battalion)
    {
        var alliance = teamSituation.Alliances
            .FirstOrDefault(item => item.Battalion == battalion && item.Relation == BattlefieldPlayerRelation.Enemy);
        if (alliance != null)
        {
            if (alliance.MainPlayerCluster.HasValue)
                return alliance.MainPlayerCluster.Value.Center;
            if (alliance.MainMapVisionCluster.HasValue)
                return alliance.MainMapVisionCluster.Value.Center;
        }

        var cluster = teamSituation.EnemyClusters.FirstOrDefault(item => item.Battalion == battalion);
        return cluster?.Center ?? Vector3.Zero;
    }

    private static int EstimateEtaSeconds(BattlefieldSnapshot snapshot, Vector3 position)
    {
        if (position == Vector3.Zero)
            return 0;

        var origin = snapshot.LocalPlayer?.Position
            ?? snapshot.TeamSituation.Friendly.MainCluster?.Center
            ?? Vector3.Zero;
        if (origin == Vector3.Zero)
            return 0;

        var dx = origin.X - position.X;
        var dz = origin.Z - position.Z;
        var distance = MathF.Sqrt(dx * dx + dz * dz);
        return distance <= 0f ? 0 : (int)MathF.Ceiling(distance / 10f);
    }

    private static bool IsObjectiveAction(BattlefieldActionType actionType)
        => actionType is BattlefieldActionType.Rotate
            or BattlefieldActionType.DefendObjective
            or BattlefieldActionType.ContestObjective
            or BattlefieldActionType.AbandonObjective
            or BattlefieldActionType.AttackIce
            or BattlefieldActionType.TouchObjective
            or BattlefieldActionType.InterruptTouch;

    private static bool IsFightAction(BattlefieldActionType actionType)
        => actionType is BattlefieldActionType.Engage
            or BattlefieldActionType.Flank
            or BattlefieldActionType.WrapBehind
            or BattlefieldActionType.BacklinePressure
            or BattlefieldActionType.FocusTarget
            or BattlefieldActionType.ProtectHighBattleHigh;

    private static bool ContainsAny(string source, params string[] values)
        => values.Any(value => source.Contains(value, StringComparison.Ordinal));

    private static bool ContainsNormalized(string normalizedSource, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(normalizedSource) || string.IsNullOrWhiteSpace(rawValue))
            return false;

        return normalizedSource.Contains(Normalize(rawValue), StringComparison.Ordinal);
    }

    private static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var chars = new List<char>(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                continue;

            chars.Add(char.ToLowerInvariant(ch));
        }

        return new string(chars.ToArray());
    }

    private static string SanitizeId(string targetId, string targetName)
    {
        var value = !string.IsNullOrWhiteSpace(targetId) ? targetId : targetName;
        return string.IsNullOrWhiteSpace(value) ? "none" : Normalize(value);
    }

    private static string FormatDuration(int seconds)
    {
        seconds = Math.Max(0, seconds);
        if (seconds < 60)
            return $"{seconds}秒";

        return $"{seconds / 60}分{seconds % 60:00}秒";
    }

    private readonly record struct StrategicDirective(
        bool IsResolved,
        string RawText,
        string FallbackText,
        BattlefieldActionType ActionType,
        BattlefieldCommandKind CommandKind,
        ResolvedTarget Target,
        string PurposeText);

    private readonly record struct ResolvedTarget(
        string Id,
        string Name,
        Vector3 Position,
        int EtaSeconds,
        int? CountdownSeconds,
        bool IsObjective,
        bool IsFightTarget);
}
