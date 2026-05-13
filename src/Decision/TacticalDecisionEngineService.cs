using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ai02;

public sealed class TacticalDecisionEngineService
{
    private const float MountedYalmsPerSecond = 10.0f;
    private const int DefaultGlobalCommandCooldownSeconds = 4;
    private const int EmergencyGlobalCommandCooldownSeconds = 2;
    private const int CommandHistoryExpirySeconds = 90;
    private const float ActionSwitchPriorityMargin = 16f;
    private const float SynchronizedCountdownClusterDistanceYalms = 22f;
    private const long CountdownRecentEnemySkillWindowMs = 16000;
    private const float MinPublishInputReliability = 55f;
    private const float MinPublishActionConfidence = 60f;
    private const float MinCriticalInputReliability = 42f;

    private readonly Dictionary<string, CommandIssueState> commandIssueStateById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandIssueState> commandIssueStateByFamily = new(StringComparer.Ordinal);
    private CommandIssueState? lastIssuedCommand;
    private ActionHoldState? heldAction;
    private int commandIssueSequence;

    public BattlefieldDecisionSnapshot Analyze(
        BattlefieldPlayerSnapshot? localPlayer,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        IReadOnlyList<BattlefieldFieldMarkerSnapshot> fieldMarkers,
        IReadOnlyList<BattlefieldTargetMarkerSnapshot> targetMarkers,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldAnnouncementSituationSnapshot announcements,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        FrontlineKnowledgeSnapshot knowledge,
        IReadOnlyList<BattlefieldCommandEffectivenessSnapshot> commandEffectiveness)
    {
        var knowledgeContext = BuildKnowledgeDecisionContext(knowledge, timeSituation, announcements);
        var mapTemplate = ResolveMapDecisionTemplate(scoreSituation.MapType);
        var announcementContext = BuildAnnouncementDecisionContext(announcements, objectives, scoreSituation);
        var inputReliability = BuildInputReliabilitySnapshot(localPlayer, objectives, teamSituation, scoreSituation, timeSituation, announcements, mapTactics, chatEvents, playerFrameEvents);
        var risk = BuildRiskAssessment(teamSituation, scoreSituation, mapTactics, objectives, chatEvents, playerFrameEvents, announcementContext);
        var priorities = objectives
            .Where(IsObjectiveActionable)
            .Select(objective => ScoreObjective(objective, objectives, localPlayer, teamSituation, scoreSituation, timeSituation, mapTactics, risk, mapTemplate, announcementContext, knowledgeContext))
            .OrderByDescending(priority => priority.PriorityScore)
            .ThenBy(priority => priority.MountedEtaSeconds)
            .Take(12)
            .ToArray();

        BattlefieldObjectivePrioritySnapshot? primary = priorities.Length > 0 ? priorities[0] : null;
        if (primary.HasValue && ShouldPreferAlternateObjective(primary.Value, timeSituation))
        {
            var alternate = priorities
                .Where(item => !ShouldPreferAlternateObjective(item, timeSituation))
                .OrderByDescending(item => item.PriorityScore)
                .ThenBy(item => item.MountedEtaSeconds)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(alternate.ObjectiveId)
                && alternate.PriorityScore >= Math.Max(42f, primary.Value.PriorityScore - 18f))
            {
                primary = alternate;
            }
        }

        var intentPredictions = BuildEnemyIntentPredictions(teamSituation, objectives, mapTactics, risk, timeSituation, announcementContext);
        var roleInsights = BuildTeamRoleInsights(teamSituation, playerFrameEvents, risk);
        var quality = BuildDecisionQualitySnapshot(mapTemplate, inputReliability, commandEffectiveness, intentPredictions, roleInsights);
        var engagement = ResolveEngagementOpportunity(teamSituation, risk, chatEvents, playerFrameEvents);
        var fightPlan = BuildStrategicFightPlan(scoreSituation, timeSituation, teamSituation, risk, primary, knowledgeContext);
        var commandSituation = BuildCommandSituation(localPlayer, priorities, fieldMarkers, targetMarkers, teamSituation, scoreSituation, timeSituation, announcementContext, mapTactics, risk, primary, chatEvents, playerFrameEvents, quality, engagement, fightPlan, knowledgeContext, inputReliability);
        var objectivePriorityTarget = BuildObjectivePriorityTarget(primary, timeSituation);
        var fightPriorityTarget = BuildFightPriorityTarget(commandSituation, fightPlan, teamSituation, scoreSituation, localPlayer?.Position ?? teamSituation.Friendly.MainCluster?.Center ?? Vector3.Zero);
        var dualTargetSummary = BuildDualPriorityTargetSummary(objectivePriorityTarget, fightPriorityTarget);
        var action = ResolveGlobalAction(primary, risk, mapTactics, fightPlan, engagement, knowledgeContext);
        if (commandSituation.PrimaryAction.HasValue)
            action = commandSituation.PrimaryAction.Value.Text;
        else if (commandSituation.PrimaryCommand.HasValue)
            action = commandSituation.PrimaryCommand.Value.CommandText;
        var summary = primary.HasValue
            ? $"目标评分：首选 {primary.Value.Name}，优先级 {primary.Value.PriorityScore:0}，风险 {primary.Value.RiskScore:0}；{dualTargetSummary}；{commandSituation.SummaryText}；{risk.SummaryText}"
            : $"目标评分：暂无可行动目标；{dualTargetSummary}；{commandSituation.SummaryText}；{risk.SummaryText}";

        return new BattlefieldDecisionSnapshot
        {
            IsAvailable = priorities.Length > 0 || mapTactics.IsAvailable || commandSituation.IsAvailable,
            ObjectivePriorities = priorities,
            PrimaryObjective = primary,
            ObjectivePriorityTarget = objectivePriorityTarget,
            FightPriorityTarget = fightPriorityTarget,
            RiskAssessment = risk,
            CommandSituation = commandSituation,
            ActionCandidates = commandSituation.ActionCandidates,
            PrimaryAction = commandSituation.PrimaryAction,
            PublishedAction = commandSituation.PublishedAction,
            DecisionQuality = quality,
            RecommendedAction = action,
            SummaryText = $"{summary}；{quality.SummaryText}"
        };
    }

    private static BattlefieldObjectivePrioritySnapshot ScoreObjective(
        BattlefieldMapObjectiveSnapshot objective,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> allObjectives,
        BattlefieldPlayerSnapshot? localPlayer,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot globalRisk,
        BattlefieldMapDecisionTemplateSnapshot mapTemplate,
        AnnouncementDecisionContext announcementContext,
        KnowledgeDecisionContext knowledgeContext)
    {
        var distance = localPlayer.HasValue ? Distance2D(localPlayer.Value.Position, objective.Position) : 0f;
        var eta = EstimateEtaSeconds(distance, MountedYalmsPerSecond);
        var announcementModifier = ResolveAnnouncementObjectiveModifier(objective, announcementContext, eta);
        var knowledgeAdjustment = ResolveKnowledgeObjectiveAdjustment(objective, allObjectives, teamSituation, scoreSituation, timeSituation, knowledgeContext, distance, eta);
        var reward = Math.Clamp(ResolveRewardScore(objective, scoreSituation, knowledgeContext) + knowledgeAdjustment.RewardBonus, 0f, 100f);
        var timing = Math.Clamp(ResolveTimingScore(objective, timeSituation, eta) + announcementModifier.TimingBonus + knowledgeAdjustment.TimingBonus, 0f, 100f);
        var distanceScore = ResolveDistanceScore(distance, eta, objective);
        var pressure = Math.Clamp(ResolvePressureScore(objective) + announcementModifier.PressureBonus, 0f, 100f);
        var teamAdvantage = ResolveTeamAdvantageScore(objective, teamSituation);
        var terrain = ResolveTerrainScore(objective, mapTactics);
        var risk = Math.Clamp(ResolveObjectiveRisk(objective, mapTactics, teamSituation, globalRisk, distance, eta) + announcementModifier.RiskAdjustment + knowledgeAdjustment.RiskAdjustment, 0f, 100f);
        var highValue = IsHighValueObjective(objective);
        var positional = ResolveObjectivePositioning(objective, localPlayer, teamSituation, mapTactics, globalRisk, highValue);
        var fatalFight = IsFatalFightState(globalRisk, teamSituation);
        var effectiveRisk = highValue && !fatalFight ? MathF.Min(risk, 24f) : risk;
        var positionalRisk = positional.EnemyDoorstepPenalty * 0.45f
            + positional.CrossfirePenalty * 0.65f
            + positional.RouteBlockPenalty * 0.52f
            + positional.LongTravelPenalty * 0.58f
            - Math.Max(0f, positional.HomeSideScore - 50f) * 0.20f;
        effectiveRisk = Math.Clamp(effectiveRisk + positionalRisk, 0f, 100f);
        var highValueBonus = highValue
            ? objective.State == BattlefieldMapObjectiveState.Warning || objective.State == BattlefieldMapObjectiveState.Active || objective.State == BattlefieldMapObjectiveState.Contested
                ? 28f
                : 18f
            : 0f;
        var refreshBonus = objective.RemainingSeconds is >= 0 and <= 10 ? 18f : 0f;
        var positionalScore = Math.Clamp(
            (positional.HomeSideScore - 50f) * 0.42f
            - positional.EnemyDoorstepPenalty * 0.72f
            - positional.CrossfirePenalty * 0.92f
            - positional.RouteBlockPenalty * 0.66f
            - positional.LongTravelPenalty * 0.84f,
            -42f,
            16f);

        var priority = reward * mapTemplate.RewardWeight
            + timing * mapTemplate.TimingWeight
            + distanceScore * mapTemplate.DistanceWeight
            + pressure * mapTemplate.PressureWeight
            + teamAdvantage * mapTemplate.TeamAdvantageWeight
            + terrain * mapTemplate.TerrainWeight
            + highValueBonus
            + refreshBonus
            + announcementModifier.PriorityBonus
            + knowledgeAdjustment.PriorityBonus
            + positionalScore
            - effectiveRisk * mapTemplate.RiskPenaltyWeight;
        priority = Math.Clamp(priority, 0f, 100f);

        var action = ResolveObjectiveAction(objective, priority, risk, eta, teamAdvantage, pressure, terrain, positional, knowledgeAdjustment.ActionOverride);
        var evidence = BuildEvidence(objective, reward, timing, distanceScore, pressure, teamAdvantage, terrain, risk, distance, eta, positional, announcementModifier.EvidenceText, knowledgeAdjustment.EvidenceText);

        return new BattlefieldObjectivePrioritySnapshot(
            objective.Id,
            string.IsNullOrWhiteSpace(objective.Name) ? CategoryText(objective.Category) : objective.Name,
            objective.Category,
            objective.State,
            objective.Position,
            objective.Ownership,
            objective.OwnershipText,
            objective.ScoreValue,
            objective.RemainingSeconds,
            distance,
            eta,
            reward,
            timing,
            distanceScore,
            pressure,
            teamAdvantage,
            terrain,
            risk,
            positional.HomeSideScore,
            positional.EnemyDoorstepPenalty,
            positional.CrossfirePenalty,
            positional.RouteBlockPenalty,
            positional.LongTravelPenalty,
            positional.ShouldHoldInstead,
            priority,
            action,
            evidence);
    }

    private static BattlefieldMapDecisionTemplateSnapshot ResolveMapDecisionTemplate(FrontlineMapType mapType)
        => mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => new BattlefieldMapDecisionTemplateSnapshot(
                mapType,
                "阵地战：主动控点压人",
                0.34f,
                0.21f,
                0.10f,
                0.20f,
                0.16f,
                0.13f,
                0.08f,
                "阵地战优先主动控点、卡口反打和压制追分家"),
            FrontlineMapType.SealRock => new BattlefieldMapDecisionTemplateSnapshot(
                mapType,
                "争夺战：高价值点强抢",
                0.39f,
                0.26f,
                0.11f,
                0.20f,
                0.14f,
                0.10f,
                0.07f,
                "争夺战优先高价值石点、先手站位和直接反抢"),
            FrontlineMapType.FieldsOfHonor => new BattlefieldMapDecisionTemplateSnapshot(
                mapType,
                "碎冰战：大冰强转火",
                0.41f,
                0.28f,
                0.14f,
                0.18f,
                0.12f,
                0.08f,
                0.06f,
                "碎冰战优先大冰、小冰补分和围绕冰点接团"),
            FrontlineMapType.OnsalHakair => new BattlefieldMapDecisionTemplateSnapshot(
                mapType,
                "竞争战：敖龙点压进",
                0.39f,
                0.27f,
                0.11f,
                0.20f,
                0.14f,
                0.10f,
                0.07f,
                "竞争战优先提前压点、打断摸点和主动反抢"),
            FrontlineMapType.Vochester => new BattlefieldMapDecisionTemplateSnapshot(
                mapType,
                "演习战：战略点连压",
                0.37f,
                0.25f,
                0.12f,
                0.19f,
                0.14f,
                0.10f,
                0.08f,
                "演习战优先战略点连压、先手转线和压第一名发育"),
            _ => new BattlefieldMapDecisionTemplateSnapshot(
                mapType,
                "通用前线：进攻拿分模型",
                0.36f,
                0.24f,
                0.12f,
                0.18f,
                0.14f,
                0.10f,
                0.08f,
                "未知地图默认以目标收益、比分压力和主动接团为核心"),
        };

    private static BattlefieldRiskAssessmentSnapshot BuildRiskAssessment(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        AnnouncementDecisionContext announcementContext)
    {
        var announcementRisk = ResolveAnnouncementRiskModifier(announcementContext);
        return TacticalRiskAssessmentService.Build(
            teamSituation,
            mapTactics,
            objectives,
            chatEvents,
            playerFrameEvents,
            ResolveScorePressure(scoreSituation),
            announcementRisk.ObjectiveRiskBonus,
            announcementRisk.TerrainRiskBonus,
            announcementRisk.LimitBreakRiskBonus,
            announcementRisk.EvidenceText);
    }

    private static int ResolveComparableEnemyCount(
        BattlefieldTeamSituationSnapshot teamSituation,
        int friendlyAlive,
        int rawEnemyAlive)
        => TacticalRiskAssessmentService.ResolveComparableEnemyCount(teamSituation, friendlyAlive, rawEnemyAlive);

    private static bool IsFatalRisk(BattlefieldRiskAssessmentSnapshot risk)
        => TacticalRiskAssessmentService.IsFatalRisk(risk);

    private static bool IsFatalFightState(BattlefieldRiskAssessmentSnapshot risk, BattlefieldTeamSituationSnapshot teamSituation)
        => TacticalRiskAssessmentService.IsFatalFightState(risk, teamSituation);
    private static bool IsObjectiveActionable(BattlefieldMapObjectiveSnapshot objective)
    {
        if (string.IsNullOrWhiteSpace(objective.Id) && string.IsNullOrWhiteSpace(objective.Name))
            return false;

        return objective.State is BattlefieldMapObjectiveState.Warning
            or BattlefieldMapObjectiveState.Active
            or BattlefieldMapObjectiveState.Contested
            or BattlefieldMapObjectiveState.Controlled
            or BattlefieldMapObjectiveState.Unknown;
    }

    private static bool IsHighValueObjective(BattlefieldMapObjectiveSnapshot objective)
    {
        if (objective.RankName.Contains("S", StringComparison.OrdinalIgnoreCase))
            return true;
        if (objective.ScoreValue is >= 150)
            return true;
        if (objective.Category == BattlefieldMapObjectiveCategory.StrategicPoint
            && (objective.RankName.Contains("S", StringComparison.OrdinalIgnoreCase) || objective.ScoreValue is >= 100))
            return true;
        return objective.Category == BattlefieldMapObjectiveCategory.Ice && objective.ScoreValue is >= 150;
    }

    private static bool IsHighValueObjective(BattlefieldObjectivePrioritySnapshot objective)
        => objective.RewardScore >= 88f
            || objective.ScoreValue is >= 150
            || objective.Name.Contains("S", StringComparison.OrdinalIgnoreCase);

    private static bool IsEndgameAllIn(BattlefieldTimeSituationSnapshot timeSituation)
        => timeSituation.MatchTimeRemainingSeconds is > 0 and <= 180;

    private static bool IsFinalMinuteAllIn(BattlefieldTimeSituationSnapshot timeSituation)
        => timeSituation.MatchTimeRemainingSeconds is > 0 and <= 60;

    private static float ResolveRewardScore(
        BattlefieldMapObjectiveSnapshot objective,
        BattlefieldScoreSituationSnapshot scoreSituation,
        KnowledgeDecisionContext knowledgeContext)
    {
        var scoreValue = objective.ScoreValue ?? 0;
        var baseReward = objective.Category switch
        {
            BattlefieldMapObjectiveCategory.Tomelith => RankScore(objective.RankName, 52f, 72f, 92f),
            BattlefieldMapObjectiveCategory.Ovoo => RankScore(objective.RankName, 50f, 70f, 90f),
            BattlefieldMapObjectiveCategory.Ice => scoreValue >= 150 ? 88f : scoreValue >= 50 ? 58f : 52f,
            BattlefieldMapObjectiveCategory.StrategicPoint => 74f,
            BattlefieldMapObjectiveCategory.Base => 48f,
            BattlefieldMapObjectiveCategory.Monster => 42f,
            _ => 40f
        };

        var knowledgeScore = ResolveKnowledgeObjectiveScoreValue(objective, knowledgeContext.MapKnowledge);
        var maxKnowledgeScore = knowledgeContext.MapKnowledge is null
            ? 0
            : knowledgeContext.MapKnowledge.ObjectiveRankScores.Select(item => item.TotalScore).DefaultIfEmpty(0).Max();
        if (knowledgeScore > 0)
        {
            if (maxKnowledgeScore > 0)
            {
                var knowledgeReward = Math.Clamp(40f + (float)knowledgeScore / maxKnowledgeScore * 52f, 40f, 92f);
                baseReward = MathF.Max(baseReward, knowledgeReward);
            }
            else
            {
                baseReward = MathF.Max(baseReward, Math.Clamp(knowledgeScore / 2.2f, 35f, 95f));
            }
        }

        if (scoreValue > 0)
            baseReward = MathF.Max(baseReward, Math.Clamp(scoreValue / 2.2f, 35f, 95f));

        if (scoreSituation.FriendlyAlliance.HasValue && scoreSituation.FriendlyAlliance.Value.RankIndex > 1)
            baseReward += 6f;
        if (scoreSituation.FriendlyAlliance.HasValue && scoreSituation.FriendlyAlliance.Value.IsLeading)
            baseReward -= 4f;

        return Math.Clamp(baseReward, 0f, 100f);
    }

    private static float ResolveTimingScore(BattlefieldMapObjectiveSnapshot objective, BattlefieldTimeSituationSnapshot timeSituation, int etaSeconds)
    {
        var score = objective.State switch
        {
            BattlefieldMapObjectiveState.Contested => 86f,
            BattlefieldMapObjectiveState.Active => 76f,
            BattlefieldMapObjectiveState.Warning => 48f,
            BattlefieldMapObjectiveState.Controlled => 24f,
            _ => 45f
        };

        if (objective.RemainingSeconds.HasValue)
        {
            var remaining = objective.RemainingSeconds.Value;
            if (objective.State == BattlefieldMapObjectiveState.Warning)
            {
                score = remaining <= etaSeconds + 20 ? 82f : remaining <= etaSeconds + 60 ? 66f : 42f;
            }
            else if (remaining <= etaSeconds + 8)
            {
                score -= 34f;
            }
            else if (remaining <= etaSeconds + 25)
            {
                score -= 14f;
            }
            else if (remaining <= 90)
            {
                score += 8f;
            }
        }

        if (timeSituation.MatchTimeRemainingSeconds is > 0 and <= 180)
            score += 8f;

        return Math.Clamp(score, 0f, 100f);
    }

    private static float ResolveDistanceScore(float distance, int etaSeconds, BattlefieldMapObjectiveSnapshot objective)
    {
        var score = 100f - Math.Clamp(distance / 4f, 0f, 75f);
        if (distance > 160f)
            score -= Math.Min(16f, (distance - 160f) * 0.14f);
        if (distance > 220f)
            score -= Math.Min(16f, (distance - 220f) * 0.12f);
        if (objective.RemainingSeconds.HasValue && objective.RemainingSeconds.Value <= etaSeconds)
            score -= 35f;
        return Math.Clamp(score, 0f, 100f);
    }

    private static float ResolvePressureScore(BattlefieldMapObjectiveSnapshot objective)
    {
        var score = 45f;
        if (objective.State == BattlefieldMapObjectiveState.Contested)
            score += 20f;
        if (objective.EnemyAttackerCount > 0)
            score += Math.Min(24f, objective.EnemyAttackerCount * 6f);
        if (objective.FriendlyAttackerCount > 0)
            score += Math.Min(16f, objective.FriendlyAttackerCount * 4f);
        if (objective.IsBeingFocused)
            score += 8f;
        if (objective.Category == BattlefieldMapObjectiveCategory.Ice && objective.RecentHpLossPerSecond > 0f)
            score += Math.Min(15f, objective.RecentHpLossPerSecond / 5000f);
        return Math.Clamp(score, 0f, 100f);
    }

    private static float ResolveTeamAdvantageScore(BattlefieldMapObjectiveSnapshot objective, BattlefieldTeamSituationSnapshot teamSituation)
    {
        var friendlyLocal = teamSituation.Friendly.NearCount + teamSituation.Friendly.MidCount;
        var enemyLocal = teamSituation.Enemy.NearCount + teamSituation.Enemy.MidCount;
        var friendly = Math.Max(Math.Max(friendlyLocal, objective.FriendlyAttackerCount), teamSituation.Friendly.MainCluster?.PlayerCount ?? 0);
        var enemy = Math.Max(Math.Max(enemyLocal, objective.EnemyAttackerCount), ResolveEnemyMainGroupCount(teamSituation));
        if (friendly <= 0)
            friendly = teamSituation.Friendly.AliveCount;
        if (enemy <= 0)
            enemy = ResolveComparableEnemyCount(teamSituation, teamSituation.Friendly.AliveCount, teamSituation.Enemy.AliveCount);

        var score = 56f + (friendly - enemy * 0.80f) * 4.5f;
        score += teamSituation.LimitBreakThreats.FriendlyHighThreatCount * 5f;
        score += teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount * 3f;
        score -= teamSituation.LimitBreakThreats.EnemyHighThreatCount * 3f;
        score += teamSituation.Enemy.CrowdControlledCount * 3f;
        score += teamSituation.Friendly.BattleFeverCount * 6f;
        score -= Math.Max(0, teamSituation.Friendly.DeadCount - 3) * 2f;
        return Math.Clamp(score, 0f, 100f);
    }

    private static float ResolveTerrainScore(BattlefieldMapObjectiveSnapshot objective, BattlefieldMapTacticsSnapshot mapTactics)
    {
        if (!mapTactics.IsAvailable)
            return 50f;

        var nearbyZones = mapTactics.TopZones
            .Where(zone => Distance2D(zone.Position, objective.Position) <= MathF.Max(zone.Radius, 22f) + 28f)
            .ToArray();
        if (nearbyZones.Length == 0)
            return 50f;

        var score = 50f;
        score += nearbyZones.Where(zone => zone.Kind == MapAnnotationKind.HighGround).Select(zone => 18f).DefaultIfEmpty(0f).Max();
        score += nearbyZones.Where(zone => zone.Kind == MapAnnotationKind.Choke && zone.Recommendation.Contains("接团", StringComparison.Ordinal)).Select(zone => 12f).DefaultIfEmpty(0f).Max();
        score -= nearbyZones.Where(zone => zone.Kind is MapAnnotationKind.LowGround or MapAnnotationKind.Underpass or MapAnnotationKind.Danger).Select(zone => MathF.Min(32f, zone.TotalRisk * 0.35f)).DefaultIfEmpty(0f).Max();
        score -= nearbyZones.Where(zone => zone.Recommendation.Contains("绕", StringComparison.Ordinal)).Select(zone => 18f).DefaultIfEmpty(0f).Max();
        return Math.Clamp(score, 0f, 100f);
    }

    private static ObjectivePositioningAssessment ResolveObjectivePositioning(
        BattlefieldMapObjectiveSnapshot objective,
        BattlefieldPlayerSnapshot? localPlayer,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot globalRisk,
        bool highValue)
    {
        var friendlyCenter = ResolveAllianceCenter(teamSituation.FriendlyAlliance)
            ?? localPlayer?.Position
            ?? teamSituation.Friendly.MainCluster?.Center
            ?? Vector3.Zero;
        var routeStart = localPlayer?.Position ?? friendlyCenter;

        var enemyAnchors = new List<ObjectiveEnemyAnchor>(2);
        AddEnemyAnchor(enemyAnchors, teamSituation.EnemyAlliance1);
        AddEnemyAnchor(enemyAnchors, teamSituation.EnemyAlliance2);
        if (enemyAnchors.Count == 0)
        {
            foreach (var cluster in teamSituation.EnemyClusters
                         .Where(cluster => cluster.Count >= 4)
                         .OrderByDescending(cluster => cluster.Count)
                         .Take(2))
            {
                enemyAnchors.Add(new ObjectiveEnemyAnchor(cluster.Center, cluster.Count));
            }
        }

        var friendlyDistance = IsMeaningfulPosition(friendlyCenter)
            ? Distance2D(objective.Position, friendlyCenter)
            : IsMeaningfulPosition(routeStart) ? Distance2D(objective.Position, routeStart) : 0f;
        var nearestEnemyDistance = enemyAnchors.Count > 0
            ? enemyAnchors.Min(anchor => Distance2D(objective.Position, anchor.Center))
            : friendlyDistance + 90f;

        var homeSideScore = 54f;
        if (IsMeaningfulPosition(friendlyCenter))
            homeSideScore += Math.Clamp((110f - friendlyDistance) * 0.16f, -14f, 14f);
        if (enemyAnchors.Count > 0)
            homeSideScore += Math.Clamp((nearestEnemyDistance - friendlyDistance) * 0.18f, -24f, 24f);
        homeSideScore = Math.Clamp(homeSideScore, 0f, 100f);

        var longTravelPenalty = friendlyDistance switch
        {
            >= 280f => 34f,
            >= 235f => 26f,
            >= 190f => 18f,
            >= 150f => 10f,
            _ => 0f
        };
        if (nearestEnemyDistance <= friendlyDistance + 12f)
            longTravelPenalty += 10f;
        else if (nearestEnemyDistance <= friendlyDistance + 30f)
            longTravelPenalty += 6f;
        if (objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested
            && friendlyDistance >= 185f
            && nearestEnemyDistance <= friendlyDistance + 18f)
        {
            longTravelPenalty += 8f;
        }
        if (highValue)
            longTravelPenalty *= 0.72f;

        var enemyDoorstepPenalty = 0f;
        foreach (var anchor in enemyAnchors)
        {
            var enemyDistance = Distance2D(objective.Position, anchor.Center);
            var penalty = enemyDistance switch
            {
                <= 55f => 40f,
                <= 80f => 30f,
                <= 110f => 20f,
                <= 145f => 10f,
                _ => 0f
            };

            if (friendlyDistance > enemyDistance + 18f)
                penalty += 6f;
            if (anchor.Weight >= 8 && enemyDistance <= 90f)
                penalty += 6f;

            enemyDoorstepPenalty = Math.Max(enemyDoorstepPenalty, penalty);
        }

        var crossfirePenalty = 0f;
        if (enemyAnchors.Count >= 2)
        {
            var first = enemyAnchors[0];
            var second = enemyAnchors[1];
            var firstDistance = Distance2D(objective.Position, first.Center);
            var secondDistance = Distance2D(objective.Position, second.Center);
            var span = Distance2D(first.Center, second.Center);
            var laneDistance = DistancePointToSegment2D(objective.Position, first.Center, second.Center, out var along);
            if (span >= 70f && along >= 0.18f && along <= 0.82f)
            {
                crossfirePenalty += laneDistance switch
                {
                    <= 18f => 40f,
                    <= 30f => 30f,
                    <= 46f => 18f,
                    _ => 0f
                };

                if (firstDistance <= 130f && secondDistance <= 130f)
                    crossfirePenalty += 12f;
                if (friendlyDistance >= MathF.Min(firstDistance, secondDistance) - 12f)
                    crossfirePenalty += 10f;
                if (friendlyDistance >= (firstDistance + secondDistance) * 0.50f)
                    crossfirePenalty += 8f;
                if (globalRisk.ThirdPartyPincerRisk >= 65f)
                    crossfirePenalty += globalRisk.ThirdPartyPincerRisk * 0.12f;
            }
        }

        var routeBlockPenalty = 0f;
        if (IsMeaningfulPosition(routeStart) && Distance2D(routeStart, objective.Position) >= 24f)
        {
            foreach (var cluster in teamSituation.EnemyClusters
                         .Where(cluster => cluster.Count >= 3)
                         .OrderByDescending(cluster => cluster.Count)
                         .Take(6))
            {
                var laneDistance = DistancePointToSegment2D(cluster.Center, routeStart, objective.Position, out var along);
                if (along < 0.08f || along > 0.94f)
                    continue;

                var width = Math.Max(cluster.Radius, 18f) + 10f;
                if (laneDistance > width)
                    continue;

                var block = Math.Clamp(cluster.Count * 5.5f, 12f, 34f);
                if (along <= 0.72f)
                    block += 4f;
                if (cluster.IsMainCluster)
                    block += 6f;

                routeBlockPenalty = Math.Max(routeBlockPenalty, block);
            }

            foreach (var anchor in enemyAnchors)
            {
                var laneDistance = DistancePointToSegment2D(anchor.Center, routeStart, objective.Position, out var along);
                if (along < 0.12f || along > 0.88f || laneDistance > 28f)
                    continue;

                routeBlockPenalty = Math.Max(routeBlockPenalty, 18f + Math.Min(18f, anchor.Weight * 1.8f));
            }

            if (mapTactics.IsAvailable)
            {
                foreach (var zone in mapTactics.TopZones)
                {
                    var laneDistance = DistancePointToSegment2D(zone.Position, routeStart, objective.Position, out var along);
                    if (along < 0.10f || along > 0.92f)
                        continue;

                    var width = Math.Max(zone.Radius, 18f) + 8f;
                    if (laneDistance > width)
                        continue;

                    if (zone.Kind is MapAnnotationKind.Choke or MapAnnotationKind.Danger or MapAnnotationKind.Underpass or MapAnnotationKind.LowGround)
                    {
                        var zonePenalty = Math.Min(24f, zone.TotalRisk * 0.28f + (zone.IsMandatoryChoke ? 12f : 0f));
                        routeBlockPenalty = Math.Max(routeBlockPenalty, zonePenalty);
                        if (friendlyDistance >= 170f && zone.TotalRisk >= 62f)
                            longTravelPenalty = Math.Max(longTravelPenalty, zonePenalty * 0.75f);
                    }

                    if (zone.Kind == MapAnnotationKind.Spawn)
                        enemyDoorstepPenalty = Math.Max(enemyDoorstepPenalty, Math.Min(32f, zone.TotalRisk * 0.36f));
                }
            }
        }

        enemyDoorstepPenalty = Math.Clamp(enemyDoorstepPenalty, 0f, 56f);
        crossfirePenalty = Math.Clamp(crossfirePenalty, 0f, 62f);
        routeBlockPenalty = Math.Clamp(routeBlockPenalty, 0f, 60f);
        longTravelPenalty = Math.Clamp(longTravelPenalty, 0f, 42f);

        var hardNoGo = crossfirePenalty >= 42f
            || (enemyDoorstepPenalty >= 38f && routeBlockPenalty >= 26f)
            || (enemyDoorstepPenalty >= 34f && crossfirePenalty >= 30f)
            || (longTravelPenalty >= 26f && crossfirePenalty >= 24f)
            || (longTravelPenalty >= 30f && routeBlockPenalty >= 20f);
        var softNoGo = !highValue
            && homeSideScore <= 58f
            && (enemyDoorstepPenalty >= 28f || crossfirePenalty >= 28f || routeBlockPenalty >= 24f || longTravelPenalty >= 22f);

        return new ObjectivePositioningAssessment(
            homeSideScore,
            enemyDoorstepPenalty,
            crossfirePenalty,
            routeBlockPenalty,
            longTravelPenalty,
            hardNoGo || softNoGo);
    }

    private static bool ShouldHoldObjectivePosition(
        BattlefieldObjectivePrioritySnapshot objective,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (!objective.ShouldHoldInstead)
            return false;

        if (IsFinalMinuteAllIn(timeSituation)
            && IsHighValueObjective(objective)
            && objective.EnemyDoorstepPenalty < 34f
            && objective.CrossfirePenalty < 42f
            && objective.RouteBlockPenalty < 36f
            && objective.LongTravelPenalty < 18f)
        {
            return false;
        }

        return true;
    }

    private static bool ShouldPreferAlternateObjective(
        BattlefieldObjectivePrioritySnapshot objective,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (ShouldHoldObjectivePosition(objective, timeSituation))
            return true;

        if (IsFinalMinuteAllIn(timeSituation) && IsHighValueObjective(objective))
            return false;

        if (IsHighValueObjective(objective))
            return false;

        return objective.CrossfirePenalty >= 24f
            || (objective.CrossfirePenalty >= 18f && objective.EnemyDoorstepPenalty >= 18f)
            || (objective.CrossfirePenalty >= 18f && objective.RouteBlockPenalty >= 20f)
            || (objective.CrossfirePenalty >= 14f && objective.LongTravelPenalty >= 20f)
            || (objective.EnemyDoorstepPenalty >= 26f && objective.LongTravelPenalty >= 18f);
    }

    private static float ResolveObjectiveRisk(
        BattlefieldMapObjectiveSnapshot objective,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot globalRisk,
        float distance,
        int etaSeconds)
    {
        var risk = 12f + objective.EnemyAttackerCount * 4f - objective.FriendlyAttackerCount * 4f;
        if (objective.State == BattlefieldMapObjectiveState.Contested)
            risk += 6f;
        if (objective.RemainingSeconds.HasValue && objective.RemainingSeconds.Value <= etaSeconds + 8)
            risk += 8f;
        if (distance > 220f)
            risk += 4f;

        if (mapTactics.IsAvailable)
        {
            var zoneRisk = mapTactics.TopZones
                .Where(zone => Distance2D(zone.Position, objective.Position) <= MathF.Max(zone.Radius, 22f) + 36f)
                .Select(zone => zone.TotalRisk)
                .DefaultIfEmpty(0f)
                .Max();
            var heatRisk = mapTactics.HeatPoints
                .Where(heat => Distance2D(heat.Position, objective.Position) <= heat.Radius + 36f)
                .Select(heat => heat.Intensity)
                .DefaultIfEmpty(0f)
                .Max();
            var passabilityRisk = mapTactics.Routes
                .Where(route => route.CrossesDangerZone || route.CrossesMandatoryChoke || route.TotalRisk >= 78f)
                .Select(route => route.TotalRisk * 0.12f)
                .DefaultIfEmpty(0f)
                .Max();
            passabilityRisk += Math.Min(6f, mapTactics.MandatoryChokeCount * 1.4f);
            passabilityRisk += Math.Min(4f, mapTactics.OneWayPassageCount * 1.0f);
            risk += zoneRisk * 0.20f + heatRisk * 0.18f + passabilityRisk;
        }

        risk += globalRisk.LimitBreakRisk * 0.08f;
        risk += globalRisk.SkillThreatRisk * 0.06f;
        risk += globalRisk.BattleHighRisk * 0.04f;
        risk += globalRisk.EncirclementRisk * 0.08f;
        risk += globalRisk.RetreatRouteRisk * 0.04f;
        if (teamSituation.IsEnemySplit)
            risk += 3f;

        var friendlyComparable = Math.Max(teamSituation.Friendly.NearCount + teamSituation.Friendly.MidCount, teamSituation.Friendly.MainCluster?.PlayerCount ?? 0);
        var enemyComparable = ResolveComparableEnemyCount(teamSituation, teamSituation.Friendly.AliveCount, teamSituation.Enemy.AliveCount);
        if (friendlyComparable > 0 && enemyComparable > 0 && friendlyComparable >= enemyComparable * 0.80f)
            risk -= 12f;
        if (IsFatalFightState(globalRisk, teamSituation))
            risk += 28f;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static string ResolveObjectiveAction(
        BattlefieldMapObjectiveSnapshot objective,
        float priority,
        float risk,
        int etaSeconds,
        float teamAdvantage,
        float pressure,
        float terrain,
        ObjectivePositioningAssessment positioning,
        string knowledgeOverride)
    {
        if (positioning.ShouldHoldInstead)
        {
            if (positioning.CrossfirePenalty >= 36f)
                return "不进两家中间";
            if (positioning.EnemyDoorstepPenalty >= 32f)
                return "不压敌方门口";
            if (positioning.LongTravelPenalty >= 24f)
                return "别赶远点送位";
            if (positioning.RouteBlockPenalty >= 26f)
                return "先清挡团";
            return "挂边观察";
        }

        if (!string.IsNullOrWhiteSpace(knowledgeOverride))
            return knowledgeOverride;

        if (priority >= 74f)
            return objective.State == BattlefieldMapObjectiveState.Warning ? "提前压位" : "主团强抢";
        if (pressure >= 70f || objective.State == BattlefieldMapObjectiveState.Contested)
            return "压点反抢";
        if (teamAdvantage >= 56f || risk <= 78f)
            return priority >= 58f ? "主动转点" : "顺路压点";
        if (terrain >= 66f)
            return "占地形压";
        if (etaSeconds > 45 && objective.RemainingSeconds is > 0 and < 75)
            return "分队摸点";
        if (risk >= 86f && teamAdvantage < 38f)
            return "侧压牵制";
        return "补分目标";
    }

    private static string ResolveGlobalAction(
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldMapTacticsSnapshot mapTactics,
        StrategicFightPlan fightPlan,
        EngagementOpportunity engagement,
        KnowledgeDecisionContext knowledgeContext)
    {
        var scoreTarget = fightPlan.IsAvailable ? fightPlan.TargetName : "高分家";
        var fatalRisk = IsFatalRisk(risk);
        if (primary.HasValue
            && IsLockedObjectiveCategory(primary.Value.Category, knowledgeContext)
            && primary.Value.PriorityScore >= 64f)
        {
            return $"{primary.Value.RecommendedAction}：{primary.Value.Name}，击杀只服务摸点/打断/掩护";
        }
        if (primary.HasValue
            && knowledgeContext.AuroraActive
            && primary.Value.Category == BattlefieldMapObjectiveCategory.StrategicPoint
            && primary.Value.PriorityScore >= 62f)
        {
            return $"极光窗口先抢 {primary.Value.Name}，极限技和打断都往点上转化";
        }
        if (fightPlan.IsAvailable && engagement.ShouldCounterEngage)
            return $"{fightPlan.GoalText}：接团反打 {fightPlan.TargetName}，优先低血/被控目标；{engagement.EvidenceText}";
        if (fightPlan.IsAvailable && engagement.CanTakeFight)
            return $"{fightPlan.GoalText}：围绕 {fightPlan.TargetName} 找击杀和目标收益；{fightPlan.EvidenceText}";
        if (fightPlan.IsAvailable && primary.HasValue && primary.Value.PriorityScore >= 58f)
            return $"{fightPlan.GoalText}：先拿 {primary.Value.Name}，同时别把火力浪费在无关家";
        if (fatalRisk && risk.ThirdPartyPincerRisk >= 84f)
            return $"第三方夹击成形，先撤出夹角后回头打 {scoreTarget}：第三方 {risk.ThirdPartyPincerRisk:0}，被包 {risk.EncirclementRisk:0}";
        if (fatalRisk && risk.AmbushRisk >= 88f)
            return $"疑似满爆发埋伏，收住深追，清侧翼后继续打 {scoreTarget}：追击陷阱 {risk.AmbushRisk:0}，极限技 {risk.LimitBreakRisk:0}";
        if (risk.HighGroundDropRisk >= 72f)
            return $"高台空降风险高，横向拉开继续打，前排顶住后排集火：高台 {risk.HighGroundDropRisk:0}，地形 {risk.TerrainRisk:0}";
        if (risk.ChokeBlockRisk >= 78f && !fatalRisk)
            return $"卡口有压力，主动卡住反打 {scoreTarget}，不要舍近求远：封路 {risk.ChokeBlockRisk:0}，地形 {risk.TerrainRisk:0}";
        if (risk.ChokeBlockRisk >= 88f && fatalRisk)
            return $"卡口是必死埋伏，换侧线继续打 {scoreTarget}：封路 {risk.ChokeBlockRisk:0}，被包 {risk.EncirclementRisk:0}";
        if (risk.CoordinatedSquadRisk >= 70f)
            return $"发现敌方固定小队协同，后排别散，优先控/压这组：组排 {risk.CoordinatedSquadRisk:0}";
        if (fatalRisk && risk.OverallRisk >= 90f && !engagement.CanTakeFight)
            return $"战力崩盘风险，脱出包围后回头打 {scoreTarget}：{risk.SummaryText}";
        if (risk.SkillThreatRisk >= 86f)
            return $"关键技能威胁高，横向拉开骗技能，技能交完反打：技能 {risk.SkillThreatRisk:0}，极限技 {risk.LimitBreakRisk:0}";
        if (fatalRisk && risk.EncirclementRisk >= 84f && !engagement.CanTakeFight)
            return $"被包成形，先脱出夹角，再回头打 {scoreTarget}：被包 {risk.EncirclementRisk:0}，第三方 {risk.ThirdPartyPincerRisk:0}";
        if (risk.RetreatRouteRisk >= 82f && risk.EnemyMainGroupDirectionRisk >= 64f && !engagement.CanPush)
            return $"出路有压迫，控深追，转为正面压 {scoreTarget}：出路 {risk.RetreatRouteRisk:0}，敌方方向 {risk.EnemyMainGroupDirectionRisk:0}";
        if (risk.FlankRisk >= 78f && !fatalRisk)
            return $"侧面有人，主团不退，回头清最近侧后继续压 {scoreTarget}：夹击 {risk.FlankRisk:0}";
        if (primary.HasValue && primary.Value.PriorityScore >= 70f)
            return $"{primary.Value.RecommendedAction}：{primary.Value.Name}，优先级 {primary.Value.PriorityScore:0}，预计 {FormatDuration(primary.Value.MountedEtaSeconds)}";
        if (fightPlan.IsAvailable)
            return $"{fightPlan.GoalText}：拉扯找 {fightPlan.TargetName} 的低血/被控/落单，不打无关家";
        if (risk.OverallRisk >= 76f && !string.IsNullOrWhiteSpace(mapTactics.CurrentRecommendation))
            return $"按地图建议找进攻角度：{mapTactics.CurrentRecommendation}";
        if (primary.HasValue)
            return $"{primary.Value.RecommendedAction}：{primary.Value.Name}，优先级 {primary.Value.PriorityScore:0}";
        return !string.IsNullOrWhiteSpace(mapTactics.CurrentRecommendation) ? $"围绕地图建议主动推进：{mapTactics.CurrentRecommendation}" : "主动找高分家打架抢分";
    }

    private BattlefieldCommandSituationSnapshot BuildCommandSituation(
        BattlefieldPlayerSnapshot? localPlayer,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        IReadOnlyList<BattlefieldFieldMarkerSnapshot> fieldMarkers,
        IReadOnlyList<BattlefieldTargetMarkerSnapshot> targetMarkers,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        AnnouncementDecisionContext announcementContext,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        BattlefieldDecisionQualitySnapshot quality,
        EngagementOpportunity engagement,
        StrategicFightPlan fightPlan,
        KnowledgeDecisionContext knowledgeContext,
        BattlefieldInputReliabilitySnapshot inputReliability)
    {
        var commands = new List<BattlefieldCommandSnapshot>();
        var anchor = localPlayer?.Position ?? teamSituation.Friendly.MainCluster?.Center ?? Vector3.Zero;
        AddEmergencyCommands(commands, anchor, teamSituation, risk, engagement, playerFrameEvents);
        AddMicroFightCommands(commands, anchor, teamSituation, risk, engagement, fightPlan, playerFrameEvents);
        AddFightDecisionCommands(commands, anchor, primary, teamSituation, scoreSituation, timeSituation, risk, engagement, fightPlan, playerFrameEvents);
        AddFormationCommands(commands, anchor, teamSituation, risk, engagement);
        AddTravelFollowCommands(commands, anchor, primary, teamSituation, timeSituation, risk);
        AddObjectiveCommands(commands, priorities, primary, risk, scoreSituation, timeSituation, fightPlan);
        AddAnnouncementCommands(commands, anchor, announcementContext, priorities, primary, teamSituation, scoreSituation, timeSituation, risk);
        AddChatObjectiveEventCommands(commands, anchor, chatEvents, priorities, primary, scoreSituation, timeSituation, risk);
        AddCommanderDoctrineCommands(commands, anchor, priorities, primary, teamSituation, scoreSituation, timeSituation, risk, engagement, fightPlan, playerFrameEvents, knowledgeContext);
        AddTargetCommands(commands, teamSituation, targetMarkers, risk);
        AddEngagementCommands(commands, anchor, teamSituation, risk, engagement);
        AddFieldMarkerCommands(commands, anchor, fieldMarkers, primary, teamSituation, risk, engagement);
        AddSnowBlessingCommands(commands, anchor, teamSituation, risk, engagement);
        AddMapCommands(commands, anchor, mapTactics, risk);
        AddTempoCommands(commands, anchor, primary, teamSituation, scoreSituation, timeSituation, chatEvents, risk, engagement);
        AddEnemyIntentCommands(commands, anchor, quality.EnemyIntentPredictions, risk, engagement);
        AddTeamRoleCommands(commands, anchor, quality.TeamRoleInsights, risk);

        var ordered = commands
            .GroupBy(command => command.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(command => command.Score).ThenByDescending(command => command.Urgency).First())
            .Where(command => command.Score >= 35f)
            .Select(command => ApplyDecisionQualityModifier(command, quality.CommandEffectiveness))
            .OrderByDescending(ResolveCommandPriority)
            .ThenByDescending(command => command.Urgency)
            .ThenByDescending(command => command.Score)
            .Take(10)
            .ToArray();
        var emergency = ordered
            .Where(command => command.Urgency >= 88f || command.Kind is BattlefieldCommandKind.Retreat or BattlefieldCommandKind.Disengage)
            .OrderByDescending(command => command.Score)
            .FirstOrDefault();
        var hasEmergency = !string.IsNullOrWhiteSpace(emergency.Id);
        BattlefieldCommandSnapshot? emergencyCommand = hasEmergency ? emergency : null;
        BattlefieldCommandSnapshot? primaryCommand = ordered.Length > 0 ? ordered[0] : null;
        var actionCandidates = BuildActionCandidates(ordered, priorities, mapTactics, risk, timeSituation, teamSituation, anchor, inputReliability);
        var actionSelection = ResolvePrimaryAction(actionCandidates, risk, timeSituation, teamSituation);
        var publish = ResolveCommandPublish(BuildPublishCommandOrder(ordered, actionSelection.Action), actionSelection.Action, inputReliability);
        var publishedCommandId = publish.Command.HasValue ? publish.Command.Value.Id : string.Empty;
        var publishedAction = !string.IsNullOrWhiteSpace(publishedCommandId)
            ? actionCandidates.FirstOrDefault(action => string.Equals(action.CommandId, publishedCommandId, StringComparison.Ordinal))
            : default;
        BattlefieldActionCandidateSnapshot? publishedActionSnapshot = !string.IsNullOrWhiteSpace(publishedAction.Id) ? publishedAction : null;
        var alignedAction = actionSelection.Action;
        var alignedPrimaryCommand = ResolveAlignedPrimaryCommand(ordered, publish.Command, alignedAction, primaryCommand);
        var macroText = fightPlan.IsAvailable ? $"战略锁定：{fightPlan.TargetName}；" : string.Empty;
        var summary = alignedPrimaryCommand.HasValue
            ? $"实时指挥：{alignedPrimaryCommand.Value.CommandText}（分 {alignedPrimaryCommand.Value.Score:0} / 急 {alignedPrimaryCommand.Value.Urgency:0}）；候选 {ordered.Length}"
            : "实时指挥：暂无强指令，寻找目标/态势变化";

        summary = alignedPrimaryCommand.HasValue
            ? $"{macroText}实时小决策：{alignedPrimaryCommand.Value.CommandText}（优先 {ResolveCommandPriorityText(alignedPrimaryCommand.Value)} / 分 {alignedPrimaryCommand.Value.Score:0} / 急 {alignedPrimaryCommand.Value.Urgency:0}）；当前行动 {(alignedAction.HasValue ? alignedAction.Value.Text : "无")}；输入可靠 {inputReliability.OverallReliability:0}；{publish.StatusText}；候选 {ordered.Length}"
            : $"{macroText}实时指挥：暂无强指令，寻找目标/态势变化";

        return new BattlefieldCommandSituationSnapshot
        {
            IsAvailable = ordered.Length > 0 || actionCandidates.Length > 0,
            Commands = ordered,
            ActionCandidates = actionCandidates,
            PrimaryCommand = alignedPrimaryCommand,
            EmergencyCommand = emergencyCommand,
            PrimaryAction = alignedAction,
            PublishedAction = publishedActionSnapshot,
            IsActionHoldActive = actionSelection.IsHeld,
            ActionHoldRemainingSeconds = actionSelection.HoldRemainingSeconds,
            ActionHoldReason = actionSelection.HoldReason,
            Publish = publish,
            SummaryText = summary,
        };
    }

    private static BattlefieldDecisionQualitySnapshot BuildDecisionQualitySnapshot(
        BattlefieldMapDecisionTemplateSnapshot mapTemplate,
        BattlefieldInputReliabilitySnapshot inputReliability,
        IReadOnlyList<BattlefieldCommandEffectivenessSnapshot> commandEffectiveness,
        IReadOnlyList<BattlefieldEnemyIntentPredictionSnapshot> enemyIntentPredictions,
        IReadOnlyList<BattlefieldTeamRoleInsightSnapshot> teamRoleInsights)
    {
        var usableEffectiveness = commandEffectiveness
            .Where(item => item.SampleCount >= 4)
            .OrderByDescending(item => MathF.Abs(item.Modifier))
            .Take(8)
            .ToArray();
        var intents = enemyIntentPredictions
            .OrderByDescending(item => item.Confidence * 0.56f + item.Urgency * 0.44f)
            .Take(8)
            .ToArray();
        var roles = teamRoleInsights
            .OrderByDescending(item => item.Severity)
            .Take(8)
            .ToArray();

        var topFeedback = usableEffectiveness.FirstOrDefault();
        var feedbackText = string.IsNullOrWhiteSpace(topFeedback.SummaryText)
            ? "暂无足够回放调权样本"
            : topFeedback.SummaryText;
        var summary = $"决策质量：模板 {mapTemplate.Name}；输入可靠 {inputReliability.OverallReliability:0}；回放调权 {usableEffectiveness.Length} 类；敌方意图 {intents.Length} 条；职责洞察 {roles.Length} 条";
        return new BattlefieldDecisionQualitySnapshot
        {
            IsAvailable = true,
            CommandEffectiveness = usableEffectiveness,
            EnemyIntentPredictions = intents,
            TeamRoleInsights = roles,
            MapTemplate = mapTemplate,
            InputReliability = inputReliability,
            CalibrationText = feedbackText,
            SummaryText = summary
        };
    }

    private static BattlefieldInputReliabilitySnapshot BuildInputReliabilitySnapshot(
        BattlefieldPlayerSnapshot? localPlayer,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldAnnouncementSituationSnapshot announcements,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents)
    {
        var scoreReliability = scoreSituation.HasScoreData
            ? 90f
            : scoreSituation.Alliances.Length >= 3 ? 62f : 34f;
        var timeReliability = timeSituation.HasMatchTime
            ? 88f
            : timeSituation.NextResourceSeconds.HasValue ? 64f : 38f;
        var visibleFriendly = Math.Max(teamSituation.Friendly.TotalCount, teamSituation.FriendlyPlayers.Length);
        var visibleEnemy = Math.Max(teamSituation.Enemy.TotalCount, teamSituation.EnemyAlliance1Players.Length + teamSituation.EnemyAlliance2Players.Length);
        var groupConfidence = teamSituation.EnemyMainGroupMovement.HasMainGroup
            ? Math.Clamp(teamSituation.EnemyMainGroupMovement.Confidence * 100f, 0f, 96f)
            : 38f;
        var playerReliability = Math.Clamp(
            (localPlayer.HasValue ? 18f : 0f)
            + Math.Min(30f, visibleFriendly * 2.0f)
            + Math.Min(32f, visibleEnemy * 1.5f)
            + groupConfidence * 0.20f,
            20f,
            96f);
        var objectiveReliability = objectives.Count > 0
            ? Math.Clamp(38f + objectives.Average(objective => objective.Confidence) * 48f + Math.Min(10f, objectives.Count * 1.5f), 0f, 96f)
            : 46f;
        var mapTacticsReliability = !mapTactics.IsAvailable
            ? 30f
            : Math.Clamp(
                44f
                + Math.Min(18f, mapTactics.AnnotationCount * 0.9f)
                + Math.Min(12f, (mapTactics.MandatoryChokeCount + mapTactics.HighGroundCount + mapTactics.JumpPadCount + mapTactics.OneWayPassageCount) * 1.2f)
                + Math.Min(4f, mapTactics.Routes.Length * 0.5f)
                + Math.Min(10f, mapTactics.HeatPoints.Length * 0.8f)
                + (mapTactics.BuiltInGraphPointCount > 0 ? 12f : 0f),
                0f,
                94f);
        var combatEventReliability = Math.Clamp(
            42f
            + Math.Min(16f, chatEvents.RecentEvents.Length * 2.0f)
            + Math.Min(18f, (playerFrameEvents.StatusEvents.Length + playerFrameEvents.DeathEvents.Length + playerFrameEvents.TargetEvents.Length) * 1.2f)
            + (teamSituation.KeySkillThreats.RecentUses.Length > 0 ? 8f : 0f),
            0f,
            90f);
        var announcementReliability = announcements.IsAvailable
            ? Math.Clamp(58f + Math.Min(24f, announcements.RecentAnnouncements.Length * 4f), 0f, 88f)
            : 52f;

        var components = new[]
        {
            new BattlefieldInputReliabilityComponentSnapshot("score", "比分", scoreReliability, 1.20f, true, scoreSituation.HasScoreData ? "结构化比分可用" : scoreSituation.SummaryText),
            new BattlefieldInputReliabilityComponentSnapshot("time", "时间", timeReliability, 0.95f, true, timeSituation.HasMatchTime ? timeSituation.MatchPhaseDetail : timeSituation.SummaryText),
            new BattlefieldInputReliabilityComponentSnapshot("players", "玩家/大团", playerReliability, 1.35f, true, $"本地:{localPlayer.HasValue} 友:{visibleFriendly} 敌:{visibleEnemy} 大团置信:{groupConfidence:0}"),
            new BattlefieldInputReliabilityComponentSnapshot("objectives", "地图目标", objectiveReliability, 1.10f, objectives.Count > 0, objectives.Count > 0 ? $"目标 {objectives.Count}，均置信 {objectives.Average(objective => objective.Confidence):P0}" : "暂无实时目标，不能强喊拿点"),
            new BattlefieldInputReliabilityComponentSnapshot("map", "地图战术", mapTacticsReliability, 0.95f, false, mapTactics.SummaryText),
            new BattlefieldInputReliabilityComponentSnapshot("combat", "战斗事件", combatEventReliability, 0.85f, false, $"聊天 {chatEvents.RecentEvents.Length}，帧事件 {playerFrameEvents.StatusEvents.Length}/{playerFrameEvents.DeathEvents.Length}/{playerFrameEvents.TargetEvents.Length}"),
            new BattlefieldInputReliabilityComponentSnapshot("announcement", "通告", announcementReliability, 0.55f, false, announcements.SummaryText),
        };
        var weightTotal = components.Sum(component => component.Weight);
        var overall = Math.Clamp(components.Sum(component => component.Reliability * component.Weight) / Math.Max(0.01f, weightTotal), 0f, 100f);
        var criticalLow = components
            .Where(component => component.IsCritical && component.Reliability < MinCriticalInputReliability)
            .OrderBy(component => component.Reliability)
            .ToArray();
        var canPublish = overall >= MinPublishInputReliability && criticalLow.Length == 0;
        var gateText = canPublish
            ? $"输入可靠 {overall:0}，允许喊指令"
            : criticalLow.Length > 0
                ? $"低置信只提示：{criticalLow[0].Label}可靠 {criticalLow[0].Reliability:0} 低于 {MinCriticalInputReliability:0}"
                : $"低置信只提示：输入可靠 {overall:0} 低于 {MinPublishInputReliability:0}";

        return new BattlefieldInputReliabilitySnapshot
        {
            IsAvailable = true,
            OverallReliability = overall,
            ScoreReliability = scoreReliability,
            TimeReliability = timeReliability,
            PlayerReliability = playerReliability,
            ObjectiveReliability = objectiveReliability,
            MapTacticsReliability = mapTacticsReliability,
            CombatEventReliability = combatEventReliability,
            AnnouncementReliability = announcementReliability,
            PublishReliabilityThreshold = MinPublishInputReliability,
            PublishActionConfidenceThreshold = MinPublishActionConfidence,
            CriticalInputReliabilityThreshold = MinCriticalInputReliability,
            CanPublish = canPublish,
            GateText = gateText,
            Components = components,
            SummaryText = $"{gateText}；比分 {scoreReliability:0}，时间 {timeReliability:0}，玩家/大团 {playerReliability:0}，目标 {objectiveReliability:0}，地图 {mapTacticsReliability:0}，事件 {combatEventReliability:0}"
        };
    }

    private static BattlefieldPriorityTargetSnapshot? BuildObjectivePriorityTarget(
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (!primary.HasValue)
            return null;

        var item = primary.Value;
        var etaText = item.MountedEtaSeconds > 0 ? $"，预计 {FormatDuration(item.MountedEtaSeconds)}" : string.Empty;
        var countdownText = item.RemainingSeconds.HasValue ? $"，倒计时 {FormatDuration(Math.Max(0, item.RemainingSeconds.Value))}" : string.Empty;
        var urgency = Math.Clamp(item.TimingScore * 0.42f + item.PressureScore * 0.32f + item.RewardScore * 0.26f, 0f, 100f);
        if (ShouldHoldObjectivePosition(item, timeSituation))
        {
            return new BattlefieldPriorityTargetSnapshot(
                "拿点",
                "挂边观察",
                $"挂边观察，别硬进 {item.Name}{countdownText}",
                Math.Clamp(item.PriorityScore * 0.84f + item.RiskScore * 0.16f, 0f, 100f),
                Math.Clamp(38f + item.RiskScore * 0.26f + item.CrossfirePenalty * 0.32f + item.RouteBlockPenalty * 0.24f + item.LongTravelPenalty * 0.22f, 0f, 100f),
                item.Position,
                $"这个点不值得带主团硬进：自家侧 {item.HomeSideScore:0} / 远点 {item.LongTravelPenalty:0} / 敌方门口 {item.EnemyDoorstepPenalty:0} / 两家中间 {item.CrossfirePenalty:0} / 挡团 {item.RouteBlockPenalty:0}",
                item.EvidenceText);
        }

        return new BattlefieldPriorityTargetSnapshot(
            "拿点",
            item.Name,
            $"{item.RecommendedAction} {item.Name}{etaText}{countdownText}",
            item.PriorityScore,
            urgency,
            item.Position,
            $"地图收益目标：收益 {item.RewardScore:0} / 时机 {item.TimingScore:0} / 风险 {item.RiskScore:0}",
            item.EvidenceText);
    }

    private static BattlefieldPriorityTargetSnapshot? BuildFightPriorityTarget(
        BattlefieldCommandSituationSnapshot commandSituation,
        StrategicFightPlan fightPlan,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        Vector3 anchor)
    {
        var command = commandSituation.Commands
            .Where(IsFightPriorityCommand)
            .OrderByDescending(ResolveCommandPriority)
            .ThenByDescending(item => item.Urgency)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(command.Id))
        {
            var targetName = !string.IsNullOrWhiteSpace(command.TargetName)
                ? command.TargetName
                : fightPlan.IsAvailable ? fightPlan.TargetName : ResolveScoreFightTargetName(scoreSituation);
            return new BattlefieldPriorityTargetSnapshot(
                "打架",
                string.IsNullOrWhiteSpace(targetName) ? "敌方主团" : targetName,
                command.CommandText,
                ResolveCommandPriority(command),
                command.Urgency,
                command.Position,
                $"战斗目标：{CommandKindLabel(command.Kind)} / {command.Scope} / {command.ReasonText}",
                command.EvidenceText);
        }

        if (fightPlan.IsAvailable)
        {
            var position = IsMeaningfulPosition(fightPlan.TargetPosition)
                ? fightPlan.TargetPosition
                : teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor;
            return new BattlefieldPriorityTargetSnapshot(
                "打架",
                fightPlan.TargetName,
                fightPlan.FightStyleText,
                Math.Clamp(54f + fightPlan.Urgency * 0.40f, 0f, 92f),
                fightPlan.Urgency,
                position,
                fightPlan.GoalText,
                fightPlan.EvidenceText);
        }

        if (teamSituation.EnemyMainGroupMovement.HasMainGroup)
        {
            return new BattlefieldPriorityTargetSnapshot(
                "打架",
                "敌方主团",
                "压敌方主团，先找低血/被控/落单目标集火",
                52f,
                48f,
                teamSituation.EnemyMainGroupMovement.CurrentCenter,
                "战斗目标兜底：已有敌方主团位置，但缺少更细的集火样本",
                teamSituation.EnemyMainGroupMovement.SummaryText);
        }

        return null;
    }

    private static bool IsFightPriorityCommand(BattlefieldCommandSnapshot command)
        => command.Kind is BattlefieldCommandKind.Engage
            or BattlefieldCommandKind.FocusTarget
            or BattlefieldCommandKind.PressureSide
            || command.Id.StartsWith("fight:", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:", StringComparison.Ordinal)
            || command.Id.StartsWith("engage:", StringComparison.Ordinal)
            || command.Id.StartsWith("target:focus:", StringComparison.Ordinal)
            || command.Id.StartsWith("target:control-skill:", StringComparison.Ordinal)
            || command.Id.StartsWith("tempo:early-battle-high", StringComparison.Ordinal)
            || command.Id.StartsWith("tempo:early-pick", StringComparison.Ordinal);

    private static string BuildDualPriorityTargetSummary(
        BattlefieldPriorityTargetSnapshot? objectiveTarget,
        BattlefieldPriorityTargetSnapshot? fightTarget)
    {
        var objectiveText = objectiveTarget.HasValue
            ? $"{objectiveTarget.Value.TargetName}（{objectiveTarget.Value.Priority:0}）"
            : "暂无";
        var fightText = fightTarget.HasValue
            ? $"{fightTarget.Value.TargetName}（{fightTarget.Value.Priority:0}）"
            : "暂无";
        return $"双优先目标：拿点 {objectiveText}；打架 {fightText}";
    }

    private static AnnouncementDecisionContext BuildAnnouncementDecisionContext(
        BattlefieldAnnouncementSituationSnapshot announcements,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldScoreSituationSnapshot scoreSituation)
    {
        var objective = announcements.LatestObjectiveAnnouncement;
        var hasRecentObjective = objective.HasValue && objective.Value.AgeMs <= 120000;
        var objectiveAnnouncement = objective.GetValueOrDefault();
        var objectiveCategory = hasRecentObjective
            ? InferAnnouncementObjectiveCategory(objectiveAnnouncement, scoreSituation.MapType)
            : BattlefieldMapObjectiveCategory.Unknown;
        var matchedObjectiveIds = hasRecentObjective
            ? ResolveAnnouncementObjectiveIds(objectiveAnnouncement, objectiveCategory, objectives)
            : Array.Empty<string>();

        var weather = announcements.LatestWeatherAnnouncement;
        var hasRecentWeather = weather.HasValue
            && weather.Value.AgeMs <= 180000
            && weather.Value.Weather != BattlefieldWeatherKind.Unknown
            && weather.Value.Kind != BattlefieldAnnouncementKind.WeatherEnded;

        return new AnnouncementDecisionContext(
            hasRecentObjective,
            objective ?? default,
            objectiveCategory,
            matchedObjectiveIds,
            hasRecentWeather,
            weather ?? default);
    }

    private static string[] ResolveAnnouncementObjectiveIds(
        BattlefieldAnnouncementSnapshot announcement,
        BattlefieldMapObjectiveCategory category,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives)
    {
        var actionable = objectives
            .Where(IsObjectiveActionable)
            .Where(objective => category == BattlefieldMapObjectiveCategory.Unknown || objective.Category == category)
            .ToArray();
        if (actionable.Length == 0)
            return Array.Empty<string>();

        var text = $"{announcement.Text} {announcement.SummaryText}".Trim();
        if (!string.IsNullOrWhiteSpace(announcement.LocationId))
        {
            var byLocation = actionable
                .Where(objective => string.Equals(objective.LocationId, announcement.LocationId, StringComparison.Ordinal)
                    || (!string.IsNullOrWhiteSpace(objective.Name) && objective.Name.Contains(announcement.LocationId, StringComparison.Ordinal)))
                .Select(objective => objective.Id)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (byLocation.Length > 0)
                return byLocation;
        }

        var byName = actionable
            .Where(objective => !string.IsNullOrWhiteSpace(objective.Name)
                && text.Contains(objective.Name, StringComparison.OrdinalIgnoreCase))
            .Select(objective => objective.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (byName.Length > 0)
            return byName;

        if (!string.IsNullOrWhiteSpace(announcement.RankName))
        {
            var byRank = actionable
                .Where(objective => string.Equals(objective.RankName, announcement.RankName, StringComparison.OrdinalIgnoreCase)
                    || objective.Name.Contains($"{announcement.RankName}级", StringComparison.OrdinalIgnoreCase)
                    || objective.Name.Contains($"{announcement.RankName}級", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (byRank.Length == 1)
                return new[] { byRank[0].Id };
        }

        if (actionable.Length == 1
            && announcement.Kind is BattlefieldAnnouncementKind.ObjectiveWarning
                or BattlefieldAnnouncementKind.ObjectiveAvailable
                or BattlefieldAnnouncementKind.ObjectiveOther)
        {
            return new[] { actionable[0].Id };
        }

        return Array.Empty<string>();
    }

    private static BattlefieldMapObjectiveCategory InferAnnouncementObjectiveCategory(
        BattlefieldAnnouncementSnapshot announcement,
        FrontlineMapType mapType)
    {
        var text = $"{announcement.Text} {announcement.SummaryText}".Trim();
        if (ContainsAny(text, "冰封", "冰块", "冰塊", "冰柱", "大冰", "小冰", "ice"))
            return BattlefieldMapObjectiveCategory.Ice;
        if (ContainsAny(text, "无垢的大地", "無垢的大地", "无垢", "無垢", "契约", "契約", "ovo"))
            return BattlefieldMapObjectiveCategory.Ovoo;
        if (ContainsAny(text, "战略目标点", "戰略目標點", "目标点", "目標點", "strategic target", "tactical target"))
            return BattlefieldMapObjectiveCategory.StrategicPoint;
        if (ContainsAny(text, "亚拉戈石文", "亞拉戈石文", "石文", "tomelith"))
            return mapType == FrontlineMapType.FieldsOfHonor ? BattlefieldMapObjectiveCategory.Ice : BattlefieldMapObjectiveCategory.Tomelith;
        if (ContainsAny(text, "据点", "據點", "base"))
            return BattlefieldMapObjectiveCategory.Base;

        return mapType switch
        {
            FrontlineMapType.SealRock => BattlefieldMapObjectiveCategory.Tomelith,
            FrontlineMapType.FieldsOfHonor => BattlefieldMapObjectiveCategory.Ice,
            FrontlineMapType.OnsalHakair => BattlefieldMapObjectiveCategory.Ovoo,
            FrontlineMapType.Vochester => BattlefieldMapObjectiveCategory.StrategicPoint,
            FrontlineMapType.BorderlandRuinsSecure => BattlefieldMapObjectiveCategory.Base,
            _ => BattlefieldMapObjectiveCategory.Unknown,
        };
    }

    private static AnnouncementObjectiveModifier ResolveAnnouncementObjectiveModifier(
        BattlefieldMapObjectiveSnapshot objective,
        AnnouncementDecisionContext context,
        int etaSeconds)
    {
        if (!context.MatchesObjective(objective))
            return default;

        var announcement = context.Objective;
        var remaining = announcement.RemainingSeconds ?? objective.RemainingSeconds;
        var highValue = IsHighAnnouncementObjective(context, objective);
        var evidence = $"战场通告 {announcement.SummaryText}";
        return announcement.Kind switch
        {
            BattlefieldAnnouncementKind.ObjectiveWarning => new AnnouncementObjectiveModifier(
                remaining.HasValue && remaining.Value <= etaSeconds + 20 ? 18f : 10f,
                remaining.HasValue && remaining.Value <= etaSeconds + 20 ? 18f : remaining.HasValue && remaining.Value <= etaSeconds + 60 ? 12f : 6f,
                remaining.HasValue && remaining.Value <= 30 ? 8f : 4f,
                0f,
                evidence),
            BattlefieldAnnouncementKind.ObjectiveAvailable => new AnnouncementObjectiveModifier(
                highValue ? 20f : 14f,
                18f,
                10f,
                -3f,
                evidence),
            BattlefieldAnnouncementKind.ObjectiveControlled => context.ObjectiveCategory is BattlefieldMapObjectiveCategory.Ovoo or BattlefieldMapObjectiveCategory.StrategicPoint
                ? new AnnouncementObjectiveModifier(-22f, -20f, -8f, -4f, $"{evidence}，控制后不可夺回")
                : new AnnouncementObjectiveModifier(6f, 4f, 8f, 2f, evidence),
            BattlefieldAnnouncementKind.ObjectiveReleased => new AnnouncementObjectiveModifier(-18f, -18f, -8f, -2f, $"{evidence}，目标已结束"),
            BattlefieldAnnouncementKind.ObjectiveOther when remaining.HasValue => new AnnouncementObjectiveModifier(8f, 10f, 4f, 0f, evidence),
            _ => default,
        };
    }

    private static AnnouncementRiskModifier ResolveAnnouncementRiskModifier(AnnouncementDecisionContext context)
    {
        var objectiveRisk = 0f;
        var terrainRisk = 0f;
        var limitBreakRisk = 0f;
        var parts = new List<string>(2);

        if (context.HasRecentObjective)
        {
            var remaining = context.Objective.RemainingSeconds;
            if (context.Objective.Kind is BattlefieldAnnouncementKind.ObjectiveWarning or BattlefieldAnnouncementKind.ObjectiveAvailable
                && remaining is null or <= 45)
            {
                objectiveRisk += 8f;
                parts.Add(context.Objective.SummaryText);
            }
        }

        if (context.HasRecentWeather)
        {
            if (context.Weather.Weather == BattlefieldWeatherKind.Snow)
            {
                terrainRisk += context.Weather.Kind == BattlefieldAnnouncementKind.WeatherStarted ? 14f : 8f;
                parts.Add(context.Weather.SummaryText);
            }
            else if (context.Weather.Weather == BattlefieldWeatherKind.Aurora)
            {
                limitBreakRisk += context.Weather.Kind == BattlefieldAnnouncementKind.WeatherStarted ? 12f : 7f;
                parts.Add(context.Weather.SummaryText);
            }
        }

        return new AnnouncementRiskModifier(
            objectiveRisk,
            terrainRisk,
            limitBreakRisk,
            string.Join("；", parts.Take(2)));
    }

    private static BattlefieldMapObjectiveSnapshot? ResolveAnnouncedObjective(
        AnnouncementDecisionContext context,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives)
    {
        if (!context.HasRecentObjective || context.MatchedObjectiveIds.Length == 0)
            return null;

        foreach (var objective in objectives)
        {
            if (context.MatchesObjective(objective))
                return objective;
        }

        return null;
    }

    private static BattlefieldObjectivePrioritySnapshot? ResolveAnnouncedPriority(
        AnnouncementDecisionContext context,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities)
    {
        if (!context.HasRecentObjective || context.MatchedObjectiveIds.Length == 0)
            return null;

        foreach (var priority in priorities)
        {
            if (context.MatchedObjectiveIds.Contains(priority.ObjectiveId, StringComparer.Ordinal))
                return priority;
        }

        return null;
    }

    private static bool IsHighAnnouncementObjective(AnnouncementDecisionContext context, BattlefieldMapObjectiveSnapshot objective)
        => IsHighAnnouncementRank(context.Objective.RankName)
            || IsHighAnnouncementRank(objective.RankName)
            || objective.ScoreValue is >= 100;

    private static bool IsHighAnnouncementObjective(AnnouncementDecisionContext context, BattlefieldObjectivePrioritySnapshot objective)
        => IsHighAnnouncementRank(context.Objective.RankName)
            || IsHighAnnouncementRank(objective.Name)
            || objective.ScoreValue is >= 100;

    private static bool IsHighAnnouncementRank(string rankName)
        => rankName.Contains("S", StringComparison.OrdinalIgnoreCase)
            || rankName.Contains("A", StringComparison.OrdinalIgnoreCase);

    private static string BuildAnnouncementTargetName(BattlefieldAnnouncementSnapshot announcement, FrontlineMapType mapType)
    {
        var category = InferAnnouncementObjectiveCategory(announcement, mapType);
        var rank = string.IsNullOrWhiteSpace(announcement.RankName) ? string.Empty : $"{announcement.RankName}级";
        var categoryText = CategoryText(category);
        if (!string.IsNullOrWhiteSpace(categoryText) && category != BattlefieldMapObjectiveCategory.Unknown)
            return $"{rank}{categoryText}".Trim();

        if (!string.IsNullOrWhiteSpace(announcement.SummaryText))
            return announcement.SummaryText;
        return "通告目标";
    }

    private static string BuildAnnouncementCommandId(
        string kind,
        BattlefieldAnnouncementSnapshot announcement,
        BattlefieldObjectivePrioritySnapshot? target)
    {
        var targetKey = target.HasValue
            ? target.Value.ObjectiveId
            : FirstNonEmpty(announcement.LocationId, announcement.RankName, announcement.Kind.ToString());
        return $"announcement:{kind}:{NormalizeIdPart(targetKey)}";
    }

    private static string BuildAnnouncementEvidence(
        BattlefieldAnnouncementSnapshot announcement,
        BattlefieldObjectivePrioritySnapshot? target)
    {
        var match = target.HasValue ? $"；匹配 {target.Value.Name}" : string.Empty;
        var remaining = announcement.RemainingSeconds.HasValue ? $"；剩余 {FormatDuration(Math.Max(0, announcement.RemainingSeconds.Value))}" : string.Empty;
        return $"{announcement.SummaryText}{match}{remaining}；来源 {announcement.Source}";
    }

    private static string NormalizeIdPart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = value
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or ':')
            .Take(64)
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static bool IsChatObjectiveEvent(BattlefieldChatEventKind kind)
        => kind is BattlefieldChatEventKind.ObjectiveCaptured
            or BattlefieldChatEventKind.ObjectiveLost
            or BattlefieldChatEventKind.ObjectiveContested
            or BattlefieldChatEventKind.ObjectiveOther;

    private static BattlefieldObjectivePrioritySnapshot? ResolveChatObjectivePriority(
        BattlefieldChatEventSnapshot item,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        FrontlineMapType mapType)
    {
        if (priorities.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(item.LocationId))
        {
            var locationMatches = priorities
                .Where(priority => ContainsObjectiveToken(priority, item.LocationId))
                .OrderByDescending(priority => priority.PriorityScore)
                .ToArray();
            if (locationMatches.Length > 0)
                return locationMatches[0];
        }

        if (!string.IsNullOrWhiteSpace(item.ObjectiveName))
        {
            var nameMatches = priorities
                .Where(priority => ContainsObjectiveToken(priority, item.ObjectiveName))
                .OrderByDescending(priority => priority.PriorityScore)
                .ToArray();
            if (nameMatches.Length == 1)
                return nameMatches[0];
        }

        var category = InferChatObjectiveCategory(item, mapType);
        if (category == BattlefieldMapObjectiveCategory.Unknown)
            return null;

        var categoryMatches = priorities
            .Where(priority => priority.Category == category)
            .Where(priority => priority.State is BattlefieldMapObjectiveState.Warning
                or BattlefieldMapObjectiveState.Active
                or BattlefieldMapObjectiveState.Contested
                or BattlefieldMapObjectiveState.Controlled)
            .OrderByDescending(priority => priority.PriorityScore)
            .ToArray();
        return categoryMatches.Length == 1 ? categoryMatches[0] : null;
    }

    private static bool ContainsObjectiveToken(BattlefieldObjectivePrioritySnapshot priority, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var normalized = NormalizeIdPart(token);
        return (!string.IsNullOrWhiteSpace(priority.ObjectiveId)
                && (priority.ObjectiveId.Contains(token, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(normalized) && priority.ObjectiveId.Contains(normalized, StringComparison.OrdinalIgnoreCase))))
            || (!string.IsNullOrWhiteSpace(priority.Name)
                && priority.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsChatObjectiveLikelySameCategory(
        BattlefieldChatEventSnapshot item,
        BattlefieldObjectivePrioritySnapshot objective,
        FrontlineMapType mapType)
    {
        var category = InferChatObjectiveCategory(item, mapType);
        return category != BattlefieldMapObjectiveCategory.Unknown && objective.Category == category;
    }

    private static bool IsChatObjectiveLockedAfterCapture(
        BattlefieldChatEventSnapshot item,
        BattlefieldObjectivePrioritySnapshot? target,
        FrontlineMapType mapType)
    {
        var category = target.HasValue
            ? target.Value.Category
            : InferChatObjectiveCategory(item, mapType);
        return category is BattlefieldMapObjectiveCategory.Ovoo or BattlefieldMapObjectiveCategory.StrategicPoint;
    }

    private static BattlefieldMapObjectiveCategory InferChatObjectiveCategory(
        BattlefieldChatEventSnapshot item,
        FrontlineMapType mapType)
    {
        var text = $"{item.Text} {item.SummaryText} {item.ObjectiveName} {item.LocationId}".Trim();
        if (ContainsAny(text, "冰封", "冰块", "大冰", "小冰", "ice"))
            return BattlefieldMapObjectiveCategory.Ice;
        if (ContainsAny(text, "无垢", "契约", "ovo"))
            return BattlefieldMapObjectiveCategory.Ovoo;
        if (ContainsAny(text, "战略目标", "目标点", "strategic target", "tactical target"))
            return BattlefieldMapObjectiveCategory.StrategicPoint;
        if (ContainsAny(text, "亚拉戈", "石文", "tomelith"))
            return mapType == FrontlineMapType.FieldsOfHonor ? BattlefieldMapObjectiveCategory.Ice : BattlefieldMapObjectiveCategory.Tomelith;
        if (ContainsAny(text, "据点", "base", "outpost"))
            return BattlefieldMapObjectiveCategory.Base;

        return mapType switch
        {
            FrontlineMapType.SealRock => BattlefieldMapObjectiveCategory.Tomelith,
            FrontlineMapType.FieldsOfHonor => BattlefieldMapObjectiveCategory.Ice,
            FrontlineMapType.OnsalHakair => BattlefieldMapObjectiveCategory.Ovoo,
            FrontlineMapType.Vochester => BattlefieldMapObjectiveCategory.StrategicPoint,
            FrontlineMapType.BorderlandRuinsSecure => BattlefieldMapObjectiveCategory.Base,
            _ => BattlefieldMapObjectiveCategory.Unknown,
        };
    }

    private static string BuildChatObjectiveTargetName(BattlefieldChatEventSnapshot item, FrontlineMapType mapType)
    {
        var name = FirstNonEmpty(item.ObjectiveName, CategoryText(InferChatObjectiveCategory(item, mapType)), "地图目标");
        return name;
    }

    private static string BuildChatObjectiveCommandId(
        string kind,
        BattlefieldChatEventSnapshot item,
        BattlefieldObjectivePrioritySnapshot? target)
    {
        var key = target.HasValue
            ? target.Value.ObjectiveId
            : FirstNonEmpty(item.LocationId, item.ObjectiveName, item.Kind.ToString());
        return $"chat-objective:{kind}:{NormalizeIdPart(key)}";
    }

    private static string BuildChatObjectiveEvidence(
        BattlefieldChatEventSnapshot item,
        BattlefieldObjectivePrioritySnapshot? target)
    {
        var text = FirstNonEmpty(item.SummaryText, item.Text, item.Kind.ToString());
        var match = target.HasValue ? $"；匹配 {target.Value.Name}" : "；未匹配具体地图点";
        var age = item.AgeMs >= 0 ? $"；{Math.Max(0, item.AgeMs / 1000)}秒前" : string.Empty;
        return $"{text}{match}{age}；来源 {item.Source}";
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

    private static string CommandKindLabel(BattlefieldCommandKind kind)
        => kind switch
        {
            BattlefieldCommandKind.Engage => "开团",
            BattlefieldCommandKind.FocusTarget => "集火",
            BattlefieldCommandKind.PressureSide => "压侧",
            BattlefieldCommandKind.Split => "分队",
            BattlefieldCommandKind.ProtectTarget => "保护",
            BattlefieldCommandKind.ContestObjective => "抢点",
            BattlefieldCommandKind.AttackObjective => "打目标",
            _ => kind.ToString(),
        };

    private static BattlefieldEnemyIntentPredictionSnapshot[] BuildEnemyIntentPredictions(
        BattlefieldTeamSituationSnapshot teamSituation,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldTimeSituationSnapshot timeSituation,
        AnnouncementDecisionContext announcementContext)
    {
        var predictions = new List<BattlefieldEnemyIntentPredictionSnapshot>();
        var friendlyCenter = teamSituation.Friendly.MainCluster?.Center;
        var mainMovement = teamSituation.EnemyMainGroupMovement;
        var movementConfidenceScale = mainMovement.HasMainGroup
            ? Math.Clamp(mainMovement.Confidence, 0.18f, 1f)
            : 1f;
        var movementStalePenalty = mainMovement.ObservationAgeMs > 0
            ? Math.Min(26f, mainMovement.ObservationAgeMs / 1000f * 2.6f)
            : 0f;
        float AdjustConfidence(float value, float staleScale = 1f)
            => Math.Clamp(value * movementConfidenceScale - movementStalePenalty * staleScale, 0f, 96f);
        float AdjustUrgency(float value, float staleScale = 0.45f)
            => Math.Clamp(value - movementStalePenalty * staleScale, 0f, 96f);
        var nextObjective = objectives
            .Where(IsObjectiveActionable)
            .OrderBy(objective => objective.RemainingSeconds ?? 999)
            .ThenByDescending(objective => objective.ScoreValue ?? 0)
            .FirstOrDefault();

        if (mainMovement.HasMainGroup && (mainMovement.IsRotationLikely || mainMovement.IsTeleportLikely))
        {
            predictions.Add(new BattlefieldEnemyIntentPredictionSnapshot(
                BattlefieldEnemyIntentKind.Rotate,
                mainMovement.Battalion,
                mainMovement.AllianceName,
                AdjustConfidence((mainMovement.IsTeleportLikely ? 60f : 54f) + mainMovement.PlayerCount * 1.3f, 0.55f),
                AdjustUrgency((mainMovement.IsTeleportLikely ? 62f : 56f) + mainMovement.SpeedPerSecond * 7f, 0.30f),
                mainMovement.PredictedNextCenter,
                mainMovement.IsTeleportLikely
                    ? "敌方可能通过传送或快速换线重定位，先补目标侧和侧后视野"
                    : "敌方主团疑似整队转线，先补侧翼和目标点前置视野",
                $"{mainMovement.TransitionText}；样本置信 {mainMovement.Confidence:P0}；观测年龄 {mainMovement.ObservationAgeMs / 1000f:0.0}s"));
        }

        if (mainMovement.HasMainGroup
            && mainMovement.IsMemoryEstimate
            && !string.IsNullOrWhiteSpace(nextObjective.Id)
            && mainMovement.ObservationAgeMs <= 6500)
        {
            var memoryObjectiveDistance = Distance2D(mainMovement.PredictedNextCenter, nextObjective.Position);
            if (memoryObjectiveDistance <= 150f)
            {
                var memoryIntentKind = nextObjective.State == BattlefieldMapObjectiveState.Contested
                    || nextObjective.RemainingSeconds is >= 0 and <= 25
                    ? BattlefieldEnemyIntentKind.ObjectiveRush
                    : BattlefieldEnemyIntentKind.Rotate;
                predictions.Add(new BattlefieldEnemyIntentPredictionSnapshot(
                    memoryIntentKind,
                    mainMovement.Battalion,
                    mainMovement.AllianceName,
                    AdjustConfidence(42f + MathF.Max(0f, 130f - memoryObjectiveDistance) * 0.18f, 0.85f),
                    AdjustUrgency(46f + (nextObjective.RemainingSeconds.HasValue ? MathF.Max(0f, 55f - nextObjective.RemainingSeconds.Value) * 0.35f : 0f), 0.35f),
                    nextObjective.Position,
                    $"敌方主团记忆轨迹贴近 {nextObjective.Name}，优先补视野并准备接转点",
                    $"记忆样本 {mainMovement.ObservationAgeMs / 1000f:0.0}s；预测点距 {nextObjective.Name} {memoryObjectiveDistance:0}y；状态 {MapObjectiveStateText(nextObjective.State)}"));
            }
        }

        if (mainMovement.HasMainGroup && friendlyCenter.HasValue)
        {
            var currentDistance = Distance2D(mainMovement.CurrentCenter, friendlyCenter.Value);
            var predictedDistance = Distance2D(mainMovement.PredictedNextCenter, friendlyCenter.Value);
            if (predictedDistance + 10f < currentDistance || currentDistance <= 72f)
            {
                predictions.Add(new BattlefieldEnemyIntentPredictionSnapshot(
                    BattlefieldEnemyIntentKind.Engage,
                    mainMovement.Battalion,
                    mainMovement.AllianceName,
                    AdjustConfidence(48f + mainMovement.PlayerCount * 2.4f + (currentDistance <= 72f ? 18f : 0f), 0.65f),
                    AdjustUrgency(46f + mainMovement.SpeedPerSecond * 11f + risk.SkillThreatRisk * 0.18f, 0.35f),
                    mainMovement.PredictedNextCenter,
                    "准备正面接团，横向拉开防控，前排顶住后排集火",
                    $"敌主团 {mainMovement.PlayerCount} 人，距离 {currentDistance:0}y，预测距离 {predictedDistance:0}y，速度 {mainMovement.SpeedPerSecond:0.0}y/s；样本置信 {mainMovement.Confidence:P0}"));
            }
        }

        if (teamSituation.IsEnemySplit && friendlyCenter.HasValue)
        {
            foreach (var cluster in teamSituation.EnemyClusters.Where(cluster => !cluster.IsMainCluster && cluster.Count >= 3).Take(4))
            {
                var distance = Distance2D(cluster.Center, friendlyCenter.Value);
                var kind = distance <= 120f ? BattlefieldEnemyIntentKind.Pincer : BattlefieldEnemyIntentKind.Flank;
                predictions.Add(new BattlefieldEnemyIntentPredictionSnapshot(
                    kind,
                    cluster.Battalion,
                    cluster.AllianceName,
                    Math.Clamp(44f + cluster.Count * 5f + cluster.SeparationFromMain * 0.18f, 0f, 94f),
                    Math.Clamp(42f + MathF.Max(0f, 150f - distance) * 0.22f + risk.FlankRisk * 0.22f, 0f, 94f),
                    cluster.Center,
                    kind == BattlefieldEnemyIntentKind.Pincer ? "敌方侧翼夹入，主团靠拢清最近侧" : "敌方侧翼绕线，后排别脱队，跟我压进",
                    $"{cluster.AllianceName} 侧翼簇 {cluster.Count} 人，离我方 {distance:0}y，离敌主团 {cluster.SeparationFromMain:0}y"));
            }
        }

        if (!string.IsNullOrWhiteSpace(nextObjective.Id))
        {
            var enemyPressure = nextObjective.EnemyAttackerCount + nextObjective.CasterCount;
            var enemyNear = teamSituation.EnemyClusters.Any(cluster => Distance2D(cluster.Center, nextObjective.Position) <= 95f && cluster.Count >= 3);
            if (nextObjective.State is BattlefieldMapObjectiveState.Warning or BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested
                && (enemyPressure > 0 || enemyNear || nextObjective.RemainingSeconds is >= 0 and <= 55))
            {
                var objectiveConfidence = 48f + enemyPressure * 9f + (enemyNear ? 12f : 0f);
                var objectiveUrgency = 46f + (nextObjective.RemainingSeconds.HasValue ? MathF.Max(0f, 70f - nextObjective.RemainingSeconds.Value) * 0.35f : 0f);
                predictions.Add(new BattlefieldEnemyIntentPredictionSnapshot(
                    enemyPressure >= 2 || nextObjective.State == BattlefieldMapObjectiveState.Contested
                        ? BattlefieldEnemyIntentKind.ObjectiveRush
                        : BattlefieldEnemyIntentKind.Rotate,
                    null,
                    "敌方",
                    mainMovement.HasMainGroup
                        ? AdjustConfidence(objectiveConfidence, 0.45f)
                        : Math.Clamp(objectiveConfidence, 0f, 94f),
                    mainMovement.HasMainGroup
                        ? AdjustUrgency(objectiveUrgency, 0.20f)
                        : Math.Clamp(objectiveUrgency, 0f, 94f),
                    nextObjective.Position,
                    $"敌方可能转/抢 {nextObjective.Name}，提前站位或打断摸点",
                    $"{nextObjective.Name} 状态 {MapObjectiveStateText(nextObjective.State)}，敌方压力 {enemyPressure}，倒计时 {FormatOptionalSeconds(nextObjective.RemainingSeconds)}"));
            }
        }

        var announcedObjective = ResolveAnnouncedObjective(announcementContext, objectives);
        if (announcedObjective.HasValue
            && announcementContext.HasRecentObjective
            && announcementContext.Objective.Kind is BattlefieldAnnouncementKind.ObjectiveWarning
                or BattlefieldAnnouncementKind.ObjectiveAvailable
                or BattlefieldAnnouncementKind.ObjectiveOther)
        {
            var item = announcedObjective.Value;
            var remaining = announcementContext.Objective.RemainingSeconds ?? item.RemainingSeconds;
            var soonBonus = remaining.HasValue ? MathF.Max(0f, 75f - remaining.Value) * 0.22f : 4f;
            predictions.Add(new BattlefieldEnemyIntentPredictionSnapshot(
                BattlefieldEnemyIntentKind.ObjectiveRush,
                null,
                "敌方",
                mainMovement.HasMainGroup
                    ? AdjustConfidence(54f + soonBonus + (IsHighAnnouncementObjective(announcementContext, item) ? 10f : 0f), 0.35f)
                    : Math.Clamp(54f + soonBonus + (IsHighAnnouncementObjective(announcementContext, item) ? 10f : 0f), 0f, 94f),
                mainMovement.HasMainGroup
                    ? AdjustUrgency(56f + soonBonus, 0.20f)
                    : Math.Clamp(56f + soonBonus, 0f, 94f),
                item.Position,
                $"战场通告指向 {item.Name}，敌方大概率转抢，提前压位或准备打断",
                $"{announcementContext.Objective.SummaryText}；匹配 {item.Name}；剩余 {FormatOptionalSeconds(remaining)}"));
        }

        if (teamSituation.AdvancedTactics.IsEnemyFakeRetreatAmbushLikely || risk.AmbushRisk >= 65f)
        {
            predictions.Add(new BattlefieldEnemyIntentPredictionSnapshot(
                BattlefieldEnemyIntentKind.RetreatBait,
                mainMovement.Battalion,
                mainMovement.AllianceName,
                mainMovement.HasMainGroup
                    ? AdjustConfidence(58f + risk.AmbushRisk * 0.38f, 0.50f)
                    : Math.Clamp(58f + risk.AmbushRisk * 0.38f, 0f, 96f),
                mainMovement.HasMainGroup
                    ? AdjustUrgency(54f + risk.RetreatRouteRisk * 0.25f, 0.20f)
                    : Math.Clamp(54f + risk.RetreatRouteRisk * 0.25f, 0f, 94f),
                mainMovement.HasMainGroup ? mainMovement.PredictedNextCenter : friendlyCenter ?? Vector3.Zero,
                "敌方疑似诱追，控深追，清侧翼后继续压",
                $"追击陷阱 {risk.AmbushRisk:0}，出路 {risk.RetreatRouteRisk:0}，地图建议 {mapTactics.CurrentRecommendation}"));
        }

        return predictions
            .Where(item => item.Confidence >= 42f)
            .OrderByDescending(item => item.Confidence * 0.56f + item.Urgency * 0.44f)
            .Take(8)
            .ToArray();
    }

    private static BattlefieldTeamRoleInsightSnapshot[] BuildTeamRoleInsights(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        var insights = new List<BattlefieldTeamRoleInsightSnapshot>();
        var anchor = teamSituation.Friendly.MainCluster?.Center ?? Vector3.Zero;
        var highBattleHigh = teamSituation.FriendlyPlayers
            .Where(player => !player.IsDead && (player.BattleHighLevel >= 4 || player.IsBattleFever))
            .OrderByDescending(player => player.IsBattleFever)
            .ThenByDescending(player => player.BattleHighLevel)
            .FirstOrDefault();
        if (highBattleHigh.GameObjectId != 0)
        {
            insights.Add(new BattlefieldTeamRoleInsightSnapshot(
                BattlefieldTeamRoleInsightKind.ProtectHighBattleHigh,
                "高战意保护",
                Math.Clamp(48f + highBattleHigh.BattleHighLevel * 8f + risk.BattleHighRisk * 0.18f, 0f, 96f),
                1,
                highBattleHigh.Name,
                highBattleHigh.Position,
                $"保护 {highBattleHigh.Name}，不要让他被切死",
                $"{highBattleHigh.Name} 战意 {highBattleHigh.BattleHighLevel}{(highBattleHigh.IsBattleFever ? " 狂热" : string.Empty)}，血量 {highBattleHigh.HpPercent:0}%"));
        }

        var endangered = teamSituation.EnemyFocusTargets
            .OrderByDescending(target => target.ThreatScore)
            .FirstOrDefault();
        if (endangered.TargetGameObjectId != 0)
        {
            var kind = endangered.TargetJobName.Contains("白", StringComparison.Ordinal)
                || endangered.TargetJobName.Contains("学", StringComparison.Ordinal)
                || endangered.TargetJobName.Contains("占", StringComparison.Ordinal)
                || endangered.TargetJobName.Contains("贤", StringComparison.Ordinal)
                    ? BattlefieldTeamRoleInsightKind.BacklineUnderDive
                    : BattlefieldTeamRoleInsightKind.ProtectFocusTarget;
            insights.Add(new BattlefieldTeamRoleInsightSnapshot(
                kind,
                kind == BattlefieldTeamRoleInsightKind.BacklineUnderDive ? "后排被切" : "集火目标保护",
                Math.Clamp(44f + endangered.ThreatScore * 11f + endangered.CasterCount * 7f, 0f, 96f),
                endangered.AttackerCount + endangered.CasterCount,
                endangered.TargetName,
                endangered.Position,
                $"保 {endangered.TargetName}，清他身边的人",
                $"敌攻击 {endangered.AttackerCount}，敌咏唱 {endangered.CasterCount}，血量 {endangered.HpPercent:0}%"));
        }

        if (playerFrameEvents.EnemyControlledRecent >= 2 || teamSituation.Enemy.CrowdControlledCount >= 2)
        {
            insights.Add(new BattlefieldTeamRoleInsightSnapshot(
                BattlefieldTeamRoleInsightKind.ControlWindow,
                "控场窗口",
                Math.Clamp(46f + playerFrameEvents.EnemyControlledRecent * 10f + teamSituation.Enemy.CrowdControlledCount * 7f, 0f, 94f),
                Math.Max(playerFrameEvents.EnemyControlledRecent, teamSituation.Enemy.CrowdControlledCount),
                "敌方被控",
                anchor,
                "控场已打出，集火低血/高威胁目标",
                $"敌方近期被控 {playerFrameEvents.EnemyControlledRecent}，当前被控 {teamSituation.Enemy.CrowdControlledCount}"));
        }

        if (teamSituation.Friendly.GuardingCount >= 2 && risk.OverallRisk <= 68f && teamSituation.Friendly.AliveCount >= teamSituation.Enemy.AliveCount - 1)
        {
            insights.Add(new BattlefieldTeamRoleInsightSnapshot(
                BattlefieldTeamRoleInsightKind.FrontlineOpenPath,
                "前排开路",
                Math.Clamp(42f + teamSituation.Friendly.GuardingCount * 7f + (68f - risk.OverallRisk) * 0.20f, 0f, 88f),
                teamSituation.Friendly.GuardingCount,
                "前排",
                anchor,
                "前排可顶住入口，后排跟上不要脱节",
                $"我方防御/减伤样本 {teamSituation.Friendly.GuardingCount}，总体风险 {risk.OverallRisk:0}"));
        }

        var focus = teamSituation.FriendlyFocusTargets
            .OrderByDescending(target => target.ThreatScore)
            .FirstOrDefault();
        if (focus.TargetGameObjectId != 0 && focus.HpPercent is > 0f and <= 55f)
        {
            insights.Add(new BattlefieldTeamRoleInsightSnapshot(
                BattlefieldTeamRoleInsightKind.BurstWindow,
                "爆发窗口",
                Math.Clamp(48f + focus.ThreatScore * 10f + (55f - focus.HpPercent) * 0.45f, 0f, 94f),
                focus.AttackerCount + focus.CasterCount,
                focus.TargetName,
                focus.Position,
                $"集火 {focus.TargetName}，打出人数差",
                $"我方攻击 {focus.AttackerCount}，咏唱 {focus.CasterCount}，目标血量 {focus.HpPercent:0}%"));
        }

        return insights
            .Where(item => item.Severity >= 42f)
            .OrderByDescending(item => item.Severity)
            .Take(8)
            .ToArray();
    }

    private static StrategicFightPlan BuildStrategicFightPlan(
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldObjectivePrioritySnapshot? primary,
        KnowledgeDecisionContext knowledgeContext)
    {
        var enemies = scoreSituation.Alliances
            .Where(alliance => !alliance.IsLocalAlliance)
            .OrderByDescending(alliance => alliance.Score)
            .ThenBy(alliance => alliance.RankIndex <= 0 ? 99 : alliance.RankIndex)
            .ToArray();
        if (enemies.Length == 0)
            return new StrategicFightPlan(false, null, string.Empty, string.Empty, string.Empty, string.Empty, 0f, false, false, false, false, Vector3.Zero);

        var friendly = scoreSituation.FriendlyAlliance;
        var candidates = enemies
            .Select(alliance => BuildStrategicTargetCandidate(alliance, friendly, scoreSituation, teamSituation))
            .OrderByDescending(candidate => candidate.ThreatScore)
            .ToArray();
        var targetCandidate = candidates[0];
        var enemyNearVictory = candidates
            .Where(candidate => candidate.RemainingToWin <= 220)
            .OrderBy(candidate => candidate.RemainingToWin)
            .ThenByDescending(candidate => candidate.ThreatScore)
            .FirstOrDefault();
        var hasEnemyNearVictory = IsValidScoreAlliance(enemyNearVictory.Alliance);
        if (hasEnemyNearVictory)
        {
            targetCandidate = enemyNearVictory;
        }
        else if (friendly.HasValue)
        {
            targetCandidate = friendly.Value.RankIndex == 1
                ? candidates
                    .OrderByDescending(candidate => candidate.ThreatScore + Math.Clamp(candidate.Alliance.Score - friendly.Value.Score, -80, 180) * 0.08f)
                    .ThenByDescending(candidate => candidate.Alliance.ScorePerSecond30s)
                    .First()
                : candidates
                    .OrderByDescending(candidate => candidate.ThreatScore + (candidate.Alliance.RankIndex == 1 ? 12f : 0f))
                    .First();
        }

        var fakeThird = candidates
            .Where(candidate => candidate.IsFakeThird && candidate.ThreatScore >= targetCandidate.ThreatScore - 10f)
            .OrderByDescending(candidate => candidate.ThreatScore)
            .FirstOrDefault();
        if (!hasEnemyNearVictory && IsValidScoreAlliance(fakeThird.Alliance))
            targetCandidate = fakeThird;

        if (targetCandidate.IsTrueWeakThird
            && (timeSituation.MatchTimeRemainingSeconds <= 0 || timeSituation.MatchTimeRemainingSeconds > 180))
        {
            var betterTarget = candidates
                .Where(candidate => !candidate.IsTrueWeakThird)
                .OrderByDescending(candidate => candidate.ThreatScore)
                .FirstOrDefault();
            if (IsValidScoreAlliance(betterTarget.Alliance))
                targetCandidate = betterTarget;
        }

        var target = targetCandidate.Alliance;

        var scorePressure = ResolveScorePressure(scoreSituation);
        var targetLeadToFriendly = friendly.HasValue ? target.Score - friendly.Value.Score : 0;
        var targetRemaining = target.VictoryScore > 0 ? target.VictoryScore - target.Score : 9999;
        var aggressiveEndgame = IsFinalCircleAggressiveMode(scoreSituation, timeSituation);
        var endgame = aggressiveEndgame || targetRemaining <= 220 || timeSituation.MatchTimeRemainingSeconds is > 0 and <= 240;
        var mustAttackLeader = !target.IsLocalAlliance
            && (target.RankIndex == 1 || targetRemaining <= 220 || targetLeadToFriendly >= 120 || (friendly.HasValue && friendly.Value.RankIndex == 3));
        var protectLead = friendly.HasValue && friendly.Value.RankIndex == 1 && target.Score >= friendly.Value.Score - 180;
        var objectiveKillScore = knowledgeContext.PlayerKillScoreValue;
        var primaryScoreValue = primary?.ScoreValue.GetValueOrDefault() ?? 0;
        var primaryObjectiveDominatesKills = primary.HasValue
            && primaryScoreValue > 0
            && objectiveKillScore > 0
            && primaryScoreValue >= Math.Max(50, objectiveKillScore * 6);
        var lockedObjectivePriority = primary.HasValue && IsLockedObjectiveCategory(primary.Value.Category, knowledgeContext);
        var highValueTomelithPriority = primary.HasValue
            && primary.Value.Category == BattlefieldMapObjectiveCategory.Tomelith
            && primaryScoreValue >= Math.Max(100, objectiveKillScore * 8);
        var urgency = Math.Clamp(
            scorePressure
            + Math.Clamp(targetLeadToFriendly / 10f, 0f, 18f)
            + (targetRemaining <= 220 ? 18f : 0f)
            + (timeSituation.MatchTimeRemainingSeconds is > 0 and <= 180 ? 10f : 0f)
            + (aggressiveEndgame ? 10f : 0f)
            + Math.Clamp(target.ScorePerSecond30s * 8f, 0f, 10f)
            - risk.ThirdPartyPincerRisk * (aggressiveEndgame ? 0.03f : 0.08f),
            0f,
            100f);
        var targetName = string.IsNullOrWhiteSpace(target.Name) ? "高分敌方" : target.Name;
        var goal = friendly.HasValue
            ? friendly.Value.RankIndex switch
            {
                1 => $"目标定 {targetName}：我们第一，压追分家，别让第二名白拿分",
                2 => $"目标定 {targetName}：追第一，抢目标和击杀都围绕这家",
                3 => $"目标定 {targetName}：落后要主动抢分，优先打高分家",
                _ => $"目标定 {targetName}：按比分优先级打人拿分"
            }
            : $"目标定 {targetName}：按当前最高分敌方处理";
        var fightStyle = aggressiveEndgame
            ? $"决赛圈锁 {targetName}，抢点/打断/击杀都围绕这家，能收人就直接收"
            : endgame
            ? $"末段看分打 {targetName}，目标、击杀、打断都围绕这家"
            : protectLead
                ? $"压 {targetName} 的推进线，守住我方分差，能杀就杀"
                : $"找 {targetName} 的低血、被控、落单和摸点人，先造人数差再拿点";
        if (lockedObjectivePriority && primaryObjectiveDominatesKills && primary.HasValue)
            fightStyle = $"围绕 {primary.Value.Name} 摸点/打断/掩护，不为散人追击停步";
        else if (highValueTomelithPriority && primary.HasValue)
            fightStyle = $"围绕 {primary.Value.Name} 抢占/中立化，击杀只服务断分和保点";
        else if (knowledgeContext.AuroraActive && primary.HasValue && primary.Value.Category == BattlefieldMapObjectiveCategory.StrategicPoint)
            fightStyle = $"极光期围绕 {primary.Value.Name} 抢高价值点，极限技和打断都往点上转化";
        if (targetCandidate.IsFakeThird)
            fightStyle = $"表面老三但跳分/战意在涨，先压 {targetName} 的进场线，不让他拿散人提战意";

        var evidence = friendly.HasValue
            ? $"我方第{friendly.Value.RankIndex} {friendly.Value.Score}/{friendly.Value.VictoryScore}；{targetName}第{target.RankIndex} {target.Score}/{target.VictoryScore}；差值 {targetLeadToFriendly:+0;-0;0}；目标剩余 {Math.Max(0, targetRemaining)}；比分压力 {scorePressure:0}"
            : $"{targetName}第{target.RankIndex} {target.Score}/{target.VictoryScore}；目标剩余 {Math.Max(0, targetRemaining)}；比分压力 {scorePressure:0}";

        if (lockedObjectivePriority && primary.HasValue)
            evidence = $"{evidence}；资源锁定 {primary.Value.Name}；击杀分 {objectiveKillScore}";
        else if (knowledgeContext.AuroraActive && primary.HasValue && primary.Value.Category == BattlefieldMapObjectiveCategory.StrategicPoint)
            evidence = $"{evidence}；极光窗口；资源 {primary.Value.Name}";

        evidence = $"{evidence}；{targetCandidate.EvidenceText}";

        return new StrategicFightPlan(
            true,
            target.Battalion,
            targetName,
            goal,
            fightStyle,
            evidence,
            urgency,
            mustAttackLeader,
            protectLead,
            endgame,
            aggressiveEndgame,
            ResolveScoreTargetPosition(target.Battalion, teamSituation));
    }

    private static bool IsFinalCircleAggressiveMode(
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (timeSituation.MatchTimeRemainingSeconds is > 0 and <= 150)
            return true;

        var victoryScore = scoreSituation.VictoryScore;
        var ranked = scoreSituation.RankedAlliances.Length > 0
            ? scoreSituation.RankedAlliances
            : scoreSituation.Alliances.OrderByDescending(alliance => alliance.Score).ToArray();
        if (ranked.Length == 0)
            return false;

        var leader = ranked[0];
        var leaderVictory = leader.VictoryScore > 0 ? leader.VictoryScore : victoryScore;
        var leaderRemaining = leaderVictory > 0 ? leaderVictory - leader.Score : 9999;
        if (leaderRemaining <= 180)
            return true;

        var friendly = scoreSituation.FriendlyAlliance;
        if (friendly.HasValue)
        {
            var friendlyVictory = friendly.Value.VictoryScore > 0 ? friendly.Value.VictoryScore : victoryScore;
            if (friendlyVictory > 0 && friendlyVictory - friendly.Value.Score <= 120)
                return true;
        }

        return timeSituation.MatchTimeRemainingSeconds is > 0 and <= 240
            && leaderRemaining <= 260
            && ranked.Count(alliance =>
            {
                var allianceVictory = alliance.VictoryScore > 0 ? alliance.VictoryScore : victoryScore;
                return allianceVictory > 0 && allianceVictory - alliance.Score <= 320;
            }) >= 2;
    }

    private static StrategicTargetCandidate BuildStrategicTargetCandidate(
        BattlefieldAllianceScoreSnapshot alliance,
        BattlefieldAllianceScoreSnapshot? friendly,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        var allianceSituation = ResolveTargetAllianceSituation(alliance.Battalion, teamSituation);
        var victoryScore = alliance.VictoryScore > 0 ? alliance.VictoryScore : scoreSituation.VictoryScore;
        var remainingToWin = victoryScore > 0 ? Math.Max(0, victoryScore - alliance.Score) : 9999;
        var leader = scoreSituation.RankedAlliances.FirstOrDefault(item => item.RankIndex == 1);
        var gapToLeader = IsValidScoreAlliance(leader) ? Math.Max(0, leader.Score - alliance.Score) : 0;
        var scoreProgress = victoryScore > 0
            ? Math.Clamp(alliance.Score / MathF.Max(1f, victoryScore) * 100f, 0f, 100f)
            : Math.Clamp(alliance.Score / 24f, 0f, 100f);
        var scoreFlow = Math.Clamp(Math.Max(0f, alliance.ScoreDelta30s) * 0.18f + Math.Max(0f, alliance.ScorePerSecond30s) * 12f, 0f, 24f);
        var battleHigh = allianceSituation?.BattleHighTotalLevel ?? 0;
        var fever = allianceSituation?.BattleFeverCount ?? 0;
        var alive = allianceSituation?.AliveCount ?? 0;
        var inCombat = allianceSituation?.InCombatCount ?? 0;
        var battleHighPressure = Math.Clamp(battleHigh * 2.4f + fever * 10f, 0f, 32f);
        var fieldPresence = Math.Clamp(alive * 0.55f + inCombat * 1.15f, 0f, 18f);
        var rankPressure = alliance.RankIndex switch
        {
            1 => 20f,
            2 => 10f,
            3 => 0f,
            _ => 4f,
        };
        var nearWinPressure = remainingToWin <= 220 ? 28f : remainingToWin <= 420 ? 14f : 0f;
        var friendlyPressure = friendly.HasValue
            ? Math.Clamp((alliance.Score - friendly.Value.Score) / 8f, -10f, 18f)
            : 0f;
        var threatScore = Math.Clamp(
            scoreProgress * 0.34f
            + rankPressure
            + nearWinPressure
            + scoreFlow
            + battleHighPressure
            + fieldPresence
            + friendlyPressure,
            0f,
            100f);
        var isFakeThird = alliance.RankIndex == 3
            && gapToLeader <= 300
            && threatScore >= 54f
            && (scoreFlow >= 8f || battleHighPressure >= 14f || alliance.ScoreDelta30s >= 40);
        var isTrueWeakThird = alliance.RankIndex == 3
            && gapToLeader >= 180
            && threatScore < 46f
            && scoreFlow < 6f
            && battleHighPressure < 12f;
        var evidence = $"三家威胁 {threatScore:0}；表分进度 {scoreProgress:0}；跳分 {scoreFlow:0}；战意压 {battleHighPressure:0}；在场 {fieldPresence:0}；距胜 {remainingToWin}；距第一 {gapToLeader}";
        if (isFakeThird)
            evidence += "；假老三";
        else if (isTrueWeakThird)
            evidence += "；真弱老三";

        return new StrategicTargetCandidate(
            alliance,
            threatScore,
            remainingToWin,
            isFakeThird,
            isTrueWeakThird,
            evidence);
    }

    private static bool IsValidScoreAlliance(BattlefieldAllianceScoreSnapshot alliance)
        => alliance.AllianceId != 0 || alliance.Battalion.HasValue || !string.IsNullOrWhiteSpace(alliance.Name);

    private static Vector3 ResolveScoreTargetPosition(byte? targetBattalion, BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (!targetBattalion.HasValue)
            return teamSituation.EnemyMainGroupMovement.HasMainGroup
                ? teamSituation.EnemyMainGroupMovement.CurrentCenter
                : Vector3.Zero;

        var alliance = ResolveTargetAllianceSituation(targetBattalion, teamSituation);
        if (alliance?.MainPlayerCluster.HasValue == true)
            return alliance.MainPlayerCluster.Value.Center;

        var players = alliance?.VisiblePlayers ?? Array.Empty<BattlefieldPlayerSnapshot>();
        var alive = players.Where(player => !player.IsDead).ToArray();
        if (alive.Length == 0)
            return teamSituation.EnemyMainGroupMovement.HasMainGroup
                ? teamSituation.EnemyMainGroupMovement.CurrentCenter
                : Vector3.Zero;

        return new Vector3(
            alive.Average(player => player.Position.X),
            alive.Average(player => player.Position.Y),
            alive.Average(player => player.Position.Z));
    }

    private static BattlefieldAllianceSituationSnapshot? ResolveTargetAllianceSituation(
        byte? targetBattalion,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (!targetBattalion.HasValue)
            return null;
        if (teamSituation.EnemyAlliance1?.Battalion == targetBattalion)
            return teamSituation.EnemyAlliance1;
        if (teamSituation.EnemyAlliance2?.Battalion == targetBattalion)
            return teamSituation.EnemyAlliance2;
        return teamSituation.Alliances.FirstOrDefault(alliance => alliance.Battalion == targetBattalion);
    }

    private static EngagementOpportunity ResolveEngagementOpportunity(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents)
    {
        var friendlyLocal = ResolveLocalFightCount(teamSituation.Friendly, teamSituation.Friendly.AliveCount);
        var enemyLocal = ResolveLocalFightCount(teamSituation.Enemy, ResolveEnemyMainGroupCount(teamSituation));
        var localAdvantage = friendlyLocal - enemyLocal;
        var killSwing = chatEvents.FriendlyKillsRecent
            - chatEvents.FriendlyDeathsRecent
            + playerFrameEvents.EnemyDeathsRecent
            - playerFrameEvents.FriendlyDeathsRecent;
        var controlWindow = playerFrameEvents.EnemyControlledRecent + teamSituation.Enemy.CrowdControlledCount;
        var vulnerableEnemyCount = teamSituation.Enemy.LowHpCount
            + teamSituation.Enemy.ExecutableCount
            + teamSituation.Enemy.ControlVulnerableCount
            + controlWindow;
        var friendlyTools = teamSituation.Friendly.GuardingCount
            + teamSituation.KeySkillThreats.FriendlyHighThreatCount
            + teamSituation.KeySkillThreats.FriendlyLikelyReadyCount
            + teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount;
        var enemyPressure = teamSituation.Enemy.NearCount
            + teamSituation.Enemy.InCombatCount
            + playerFrameEvents.EnemyTargetingFriendlyRecent
            + teamSituation.Enemy.CastingCount;
        var friendlyControlPressure = Math.Max(0, playerFrameEvents.FriendlyControlledRecent - 2);
        var hasFatalRisk = IsFatalFightState(risk, teamSituation);
        var enoughNumbers = enemyLocal <= 0 || friendlyLocal >= enemyLocal * 0.92f;
        var moraleWindow = teamSituation.Friendly.BattleFeverCount > 0
            || teamSituation.Friendly.BattleHighTotalLevel >= Math.Max(4, teamSituation.Enemy.BattleHighTotalLevel * 0.75f);
        var toolWindow = friendlyTools >= 2
            || teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 2
            || teamSituation.KeySkillThreats.FriendlyLikelyReadyCount >= 2;
        var enemyLoose = teamSituation.IsEnemySplit
            || teamSituation.Enemy.LowHpCount >= 2
            || teamSituation.RespawnRhythm.EnemyDeadNow >= teamSituation.RespawnRhythm.FriendlyDeadNow + 2;
        var enemySpentDefensive = playerFrameEvents.EnemyDefensiveRecent >= 2;
        var danger = risk.ThirdPartyPincerRisk * 0.35f
            + risk.EncirclementRisk * 0.30f
            + risk.LimitBreakRisk * 0.20f
            + risk.SkillThreatRisk * 0.15f
            + Math.Max(0, -localAdvantage) * 6f
            + friendlyControlPressure * 5f;
        var score = Math.Clamp(
            50f
            + localAdvantage * 5.2f
            + killSwing * 7f
            + vulnerableEnemyCount * 3.4f
            + friendlyTools * 2.8f
            + Math.Min(12f, enemyPressure * 1.4f)
            + (moraleWindow ? 8f : 0f)
            + (toolWindow ? 7f : 0f)
            + (enemyLoose ? 7f : 0f)
            + (enemySpentDefensive ? 8f : 0f)
            - danger * 0.18f
            - risk.OverallRisk * 0.06f,
            0f,
            100f);
        var enemyEngaging = risk.EnemyMainGroupDirectionRisk >= 54f || teamSituation.Enemy.NearCount >= 3 || teamSituation.Enemy.InCombatCount >= 4;
        var hasBurstWindow = vulnerableEnemyCount >= 3 || killSwing >= 2 || controlWindow >= 2 || enemySpentDefensive;
        var strongForwardWindow = (moraleWindow && toolWindow && localAdvantage >= 0)
            || (hasBurstWindow && localAdvantage >= 0)
            || killSwing >= 2;
        var canTakeFight = !hasFatalRisk
            && score >= 50f
            && (enoughNumbers || strongForwardWindow)
            && risk.NumberDisadvantageRisk <= 82f
            && risk.ThirdPartyPincerRisk < 78f
            && risk.EncirclementRisk < 80f
            && risk.RetreatRouteRisk < 82f;
        var shouldCounterEngage = !hasFatalRisk
            && enemyEngaging
            && (canTakeFight
                || (enoughNumbers && hasBurstWindow && risk.ThirdPartyPincerRisk < 82f && risk.EncirclementRisk < 84f)
                || (toolWindow && localAdvantage >= 0 && risk.RetreatRouteRisk < 84f));
        var canPush = !hasFatalRisk
            && score >= 68f
            && (localAdvantage >= 1 || (hasBurstWindow && localAdvantage >= 0) || (toolWindow && moraleWindow && localAdvantage >= 0))
            && risk.RetreatRouteRisk < 76f
            && risk.ThirdPartyPincerRisk < 72f
            && risk.EncirclementRisk < 74f;
        var evidence = $"接团评分 {score:0}；近中场我方 {friendlyLocal}/敌方 {enemyLocal}；局部差 {localAdvantage:+0;-0;0}；击杀摆动 {killSwing:+0;-0;0}；敌脆弱 {vulnerableEnemyCount}；我方工具 {friendlyTools}；敌减伤已交 {playerFrameEvents.EnemyDefensiveRecent}；总体风险 {risk.OverallRisk:0}";

        return new EngagementOpportunity(
            score,
            friendlyLocal,
            enemyLocal,
            localAdvantage,
            killSwing,
            vulnerableEnemyCount,
            friendlyTools,
            enemyEngaging,
            hasBurstWindow,
            canTakeFight,
            shouldCounterEngage,
            canPush,
            evidence);
    }

    private static int ResolveLocalFightCount(BattlefieldTeamSummarySnapshot summary, int fallback)
    {
        var local = summary.NearCount + summary.MidCount;
        if (local > 0)
            return local;

        if (summary.MainCluster.HasValue && summary.MainCluster.Value.PlayerCount > 0)
            return Math.Min(Math.Max(0, summary.AliveCount), summary.MainCluster.Value.PlayerCount);

        return Math.Min(Math.Max(0, fallback), 24);
    }

    private static int ResolveEnemyMainGroupCount(BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (teamSituation.EnemyMainGroupMovement.HasMainGroup && teamSituation.EnemyMainGroupMovement.PlayerCount > 0)
            return teamSituation.EnemyMainGroupMovement.PlayerCount;

        if (teamSituation.Enemy.MainCluster.HasValue && teamSituation.Enemy.MainCluster.Value.PlayerCount > 0)
            return teamSituation.Enemy.MainCluster.Value.PlayerCount;

        return teamSituation.Enemy.AliveCount;
    }

    private static void AddEngagementCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement)
    {
        if (IsFatalFightState(risk, teamSituation))
            return;

        var destination = teamSituation.EnemyMainGroupMovement.HasMainGroup
            ? teamSituation.EnemyMainGroupMovement.CurrentCenter
            : anchor;
        var friendlyPowerWindow = teamSituation.Friendly.BattleFeverCount > 0
            || teamSituation.Friendly.BattleHighTotalLevel >= 8
            || teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 2
            || teamSituation.KeySkillThreats.FriendlyLikelyReadyCount >= 2;
        var enemyWeakWindow = teamSituation.LimitBreakThreats.EnemyLikelyReadyCount == 0
            && teamSituation.Enemy.BattleFeverCount == 0
            && teamSituation.Enemy.BattleHighTotalLevel <= Math.Max(4, teamSituation.Friendly.BattleHighTotalLevel);
        var safeToForceFight = IsSafeForwardFightWindow(engagement, risk);
        var safePowerWindow = friendlyPowerWindow
            && safeToForceFight
            && (engagement.CanTakeFight || engagement.CanPush || engagement.HasBurstWindow);
        var safeEnemyWeakWindow = enemyWeakWindow
            && safeToForceFight
            && engagement.Score >= 58f
            && (engagement.CanTakeFight || engagement.CanPush || engagement.HasBurstWindow);
        var safeSplitPick = teamSituation.IsEnemySplit
            && safeToForceFight
            && (engagement.CanTakeFight || engagement.CanPush || engagement.HasBurstWindow)
            && (engagement.LocalAdvantage >= 0 || engagement.HasBurstWindow);
        var safeBurstCleanup = engagement.HasBurstWindow
            && (engagement.ShouldCounterEngage
                || safeToForceFight
                || teamSituation.Enemy.LowHpCount > 0
                || teamSituation.Enemy.CrowdControlledCount > 0)
            && risk.RetreatRouteRisk < 78f
            && risk.ThirdPartyPincerRisk < 76f;

        if (!engagement.ShouldCounterEngage
            && !safePowerWindow
            && !safeEnemyWeakWindow
            && !safeSplitPick
            && !safeBurstCleanup
            && !(engagement.CanPush && safeToForceFight))
            return;

        if (safePowerWindow)
        {
            AddCommand(
                commands,
                "engage:power-window",
                BattlefieldCommandKind.Engage,
                "主团",
                "我方战意/极限技窗口，主动找敌方大团开一波",
                Math.Clamp(56f + teamSituation.Friendly.BattleFeverCount * 8f + teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount * 6f + engagement.Score * 0.18f, 0f, 96f),
                Math.Clamp(58f + teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount * 8f + teamSituation.Friendly.BattleFeverCount * 6f, 0f, 94f),
                7,
                destination,
                "敌方大团",
                "战意和极限技要主动转化为团战收益",
                $"{engagement.EvidenceText}；我方战意狂热 {teamSituation.Friendly.BattleFeverCount}；我方极限技就绪 {teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount}");
        }

        if (safeEnemyWeakWindow)
        {
            AddCommand(
                commands,
                "engage:enemy-weak-window",
                BattlefieldCommandKind.PressureSide,
                "主团",
                "敌方无极限技无高战意，直接压进抢节奏",
                Math.Clamp(54f + engagement.Score * 0.25f + Math.Max(0, engagement.LocalAdvantage) * 2f, 0f, 92f),
                Math.Clamp(54f + engagement.Score * 0.18f, 0f, 88f),
                8,
                destination,
                "敌方弱势期",
                "敌方爆发资源不足",
                $"{engagement.EvidenceText}；敌方极限技就绪 {teamSituation.LimitBreakThreats.EnemyLikelyReadyCount}；敌方战意狂热 {teamSituation.Enemy.BattleFeverCount}");
        }

        if (safeSplitPick)
        {
            AddCommand(
                commands,
                "engage:enemy-split-pick",
                BattlefieldCommandKind.FocusTarget,
                "主团",
                "敌方分散，压上去逐个收，不给他们重新抱团",
                Math.Clamp(54f + engagement.Score * 0.24f + risk.ScorePressure * 0.12f, 0f, 92f),
                62f,
                7,
                destination,
                "分散敌方",
                "敌方未抱团，主动求战逐个击破",
                $"{engagement.EvidenceText}；{teamSituation.EnemySplitSummaryText}");
        }

        if (engagement.ShouldCounterEngage)
        {
            AddCommand(
                commands,
                "engage:counter-front",
                BattlefieldCommandKind.Engage,
                "主团",
                engagement.HasBurstWindow
                    ? "接团反打，集火被控/低血目标"
                    : "正面接住，前排顶住，后排跟集火",
                Math.Clamp(50f + engagement.Score * 0.48f + Math.Max(0, engagement.LocalAdvantage) * 2f, 0f, 96f),
                Math.Clamp(48f + engagement.Score * 0.34f + risk.EnemyMainGroupDirectionRisk * 0.12f, 0f, 92f),
                6,
                destination,
                "敌方主团",
                "敌方靠近但局部交换条件可接受",
                engagement.EvidenceText);
        }

        if (safeBurstCleanup)
        {
            AddCommand(
                commands,
                "engage:burst-window",
                BattlefieldCommandKind.FocusTarget,
                "主团",
                "别散，控场窗口已出，集火最近低血/被控目标",
                Math.Clamp(50f + engagement.Score * 0.42f + engagement.VulnerableEnemyCount * 2f, 0f, 94f),
                Math.Clamp(46f + engagement.Score * 0.28f + engagement.KillSwing * 5f, 0f, 90f),
                5,
                destination,
                "低血/被控目标",
                "敌方出现可击杀窗口",
                engagement.EvidenceText);
        }

        if (engagement.CanPush && safeToForceFight)
        {
            AddCommand(
                commands,
                "engage:push-window",
                BattlefieldCommandKind.PressureSide,
                "主团",
                "可以往前压一波，打完就收，不要深追",
                Math.Clamp(48f + engagement.Score * 0.38f + Math.Max(0, engagement.LocalAdvantage) * 2f, 0f, 88f),
                Math.Clamp(44f + engagement.Score * 0.24f, 0f, 84f),
                8,
                destination,
                "敌方退路",
                "我方有局部优势或击杀窗口",
                $"{engagement.EvidenceText}；出路 {risk.RetreatRouteRisk:0}");
        }
    }

    private static void AddEnemyIntentCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        IReadOnlyList<BattlefieldEnemyIntentPredictionSnapshot> predictions,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement)
    {
        foreach (var prediction in predictions.Take(3))
        {
            var key = prediction.Kind.ToString().ToLowerInvariant();
            switch (prediction.Kind)
            {
                case BattlefieldEnemyIntentKind.Pincer:
                case BattlefieldEnemyIntentKind.Flank:
                    if (engagement.CanTakeFight && risk.ThirdPartyPincerRisk < 68f && risk.EncirclementRisk < 72f)
                    {
                        AddCommand(
                            commands,
                            $"intent:{key}:{prediction.Battalion}:clear-side",
                            BattlefieldCommandKind.FocusTarget,
                            "主团",
                            "敌方侧翼来了，不散，回头清最近侧",
                            Math.Clamp(46f + prediction.Confidence * 0.32f + engagement.Score * 0.28f, 0f, 92f),
                            Math.Clamp(46f + prediction.Urgency * 0.30f + engagement.Score * 0.14f, 0f, 88f),
                            6,
                            prediction.Position,
                            prediction.AllianceName,
                            "敌方意图预测：侧翼压力但可反打",
                            $"{prediction.EvidenceText}；{engagement.EvidenceText}");
                    }
                    else
                    {
                        AddCommand(
                            commands,
                            $"intent:{key}:{prediction.Battalion}:clear-side",
                            BattlefieldCommandKind.FocusTarget,
                            "主团",
                            risk.ThirdPartyPincerRisk >= 76f || risk.EncirclementRisk >= 84f
                                ? "夹角危险，先出角，出角后清最近侧"
                                : "敌方侧翼来了，主团靠拢清最近侧",
                            Math.Clamp(44f + prediction.Confidence * 0.30f + risk.FlankRisk * 0.10f, 0f, 86f),
                            Math.Clamp(42f + prediction.Urgency * 0.26f, 0f, 82f),
                            7,
                            prediction.Position,
                            prediction.AllianceName,
                            "敌方意图预测：侧翼/绕后，默认清侧不默认后退",
                            prediction.EvidenceText);
                    }
                    break;
                case BattlefieldEnemyIntentKind.Rotate:
                case BattlefieldEnemyIntentKind.ObjectiveRush:
                    AddCommand(
                        commands,
                        $"intent:{key}:{prediction.Position.X:0}:{prediction.Position.Z:0}",
                        BattlefieldCommandKind.ContestObjective,
                        "主团",
                        prediction.Recommendation,
                        Math.Clamp(44f + prediction.Confidence * 0.42f, 0f, 90f),
                        Math.Clamp(42f + prediction.Urgency * 0.40f, 0f, 92f),
                        9,
                        prediction.Position,
                        "敌方转点",
                        "敌方意图预测：目标转线",
                        prediction.EvidenceText);
                    break;
                case BattlefieldEnemyIntentKind.Engage:
                    if (engagement.ShouldCounterEngage)
                    {
                        AddCommand(
                            commands,
                            $"intent:{key}:{prediction.Battalion}:counter",
                            BattlefieldCommandKind.Engage,
                            "主团",
                            "敌方开团，接住反打，前排顶住后排集火",
                            Math.Clamp(48f + prediction.Confidence * 0.34f + engagement.Score * 0.30f, 0f, 94f),
                            Math.Clamp(48f + prediction.Urgency * 0.32f + engagement.Score * 0.18f, 0f, 92f),
                            6,
                            prediction.Position,
                            prediction.AllianceName,
                            "敌方意图预测：正面开团，可接团",
                            $"{prediction.EvidenceText}；{engagement.EvidenceText}");
                    }
                    else
                    {
                        AddCommand(
                            commands,
                            $"intent:{key}:{prediction.Battalion}:spread",
                            BattlefieldCommandKind.Engage,
                            "主团",
                            "敌方准备开团，横向展开接住，前排先顶",
                            Math.Clamp(42f + prediction.Confidence * 0.32f + risk.SkillThreatRisk * 0.08f, 0f, 86f),
                            Math.Clamp(46f + prediction.Urgency * 0.30f, 0f, 88f),
                            7,
                            prediction.Position,
                            prediction.AllianceName,
                            "敌方意图预测：正面开团，默认接住而不是后退",
                            $"{prediction.EvidenceText}；{engagement.EvidenceText}");
                    }
                    break;
                case BattlefieldEnemyIntentKind.RetreatBait:
                    AddCommand(
                        commands,
                        $"intent:{key}",
                        IsFatalRisk(risk) ? BattlefieldCommandKind.Disengage : BattlefieldCommandKind.FocusTarget,
                        "主团",
                        IsFatalRisk(risk) ? "敌方像在钓，收住深追，清侧翼" : "敌方像在钓，控住前排，清侧翼继续压",
                        Math.Clamp(46f + prediction.Confidence * 0.34f + risk.AmbushRisk * 0.12f, 0f, 90f),
                        Math.Clamp(48f + prediction.Urgency * 0.30f, 0f, 88f),
                        7,
                        prediction.Position,
                        "诱追",
                        "敌方意图预测：诱追设伏",
                        prediction.EvidenceText);
                    break;
            }
        }
    }

    private static void AddTeamRoleCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        IReadOnlyList<BattlefieldTeamRoleInsightSnapshot> roleInsights,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        foreach (var insight in roleInsights.Take(4))
        {
            switch (insight.Kind)
            {
                case BattlefieldTeamRoleInsightKind.ProtectHighBattleHigh:
                case BattlefieldTeamRoleInsightKind.BacklineUnderDive:
                case BattlefieldTeamRoleInsightKind.ProtectFocusTarget:
                    AddCommand(
                        commands,
                        $"role:protect:{insight.Kind}:{insight.TargetName}",
                        BattlefieldCommandKind.ProtectTarget,
                        "辅助/近战",
                        insight.Recommendation,
                        Math.Clamp(44f + insight.Severity * 0.44f + risk.SkillThreatRisk * 0.10f, 0f, 94f),
                        Math.Clamp(46f + insight.Severity * 0.38f, 0f, 94f),
                        6,
                        insight.Position,
                        insight.TargetName,
                        insight.Label,
                        insight.EvidenceText);
                    break;
                case BattlefieldTeamRoleInsightKind.ControlWindow:
                case BattlefieldTeamRoleInsightKind.BurstWindow:
                    AddCommand(
                        commands,
                        $"role:focus:{insight.Kind}:{insight.TargetName}",
                        BattlefieldCommandKind.FocusTarget,
                        "主团",
                        insight.Recommendation,
                        Math.Clamp(42f + insight.Severity * 0.46f, 0f, 92f),
                        Math.Clamp(42f + insight.Severity * 0.34f, 0f, 90f),
                        5,
                        insight.Position,
                        insight.TargetName,
                        insight.Label,
                        insight.EvidenceText);
                    break;
                case BattlefieldTeamRoleInsightKind.FrontlineOpenPath:
                    AddCommand(
                        commands,
                        "role:frontline-open",
                        BattlefieldCommandKind.Hold,
                        "主团",
                        insight.Recommendation,
                        Math.Clamp(40f + insight.Severity * 0.35f, 0f, 82f),
                        42f,
                        10,
                        anchor,
                        "前排",
                        insight.Label,
                        insight.EvidenceText);
                    break;
            }
        }
    }

    private static void AddMicroFightCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        StrategicFightPlan fightPlan,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents)
    {
        if (IsFatalFightState(risk, teamSituation))
            return;

        var target = ResolveMicroFocusTarget(teamSituation, fightPlan, anchor);
        var hasTarget = !string.IsNullOrWhiteSpace(target.Name);
        var hasVulnerableTarget = hasTarget
            && (target.IsExecutable || target.IsCrowdControlled || target.HpPercent is > 0f and <= 30f);
        var targetName = hasTarget ? target.Name : fightPlan.IsAvailable ? fightPlan.TargetName : "最近低血/被控目标";
        var destination = hasTarget && IsMeaningfulPosition(target.Position)
            ? target.Position
            : IsMeaningfulPosition(fightPlan.TargetPosition)
                ? fightPlan.TargetPosition
                : teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor;
        var safeForwardWindow = IsSafeForwardFightWindow(engagement, risk);
        var safeBurstCleanup = engagement.HasBurstWindow
            && (engagement.ShouldCounterEngage
                || safeForwardWindow
                || playerFrameEvents.EnemyControlledRecent > 0
                || teamSituation.Enemy.LowHpCount > 0
                || teamSituation.Enemy.CrowdControlledCount > 0)
            && risk.RetreatRouteRisk < 80f;
        var splitPressureWindow = teamSituation.IsEnemySplit
            && safeForwardWindow
            && (engagement.CanTakeFight || engagement.CanPush || engagement.HasBurstWindow);
        var powerWindow = (teamSituation.Friendly.BattleFeverCount > 0
                || teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 1)
            && safeForwardWindow
            && engagement.Score >= 58f;
        var cleanupWindow = (playerFrameEvents.EnemyControlledRecent > 0
                && engagement.Score >= 54f
                && risk.RetreatRouteRisk < 80f)
            || (playerFrameEvents.EnemyDeathsRecent > playerFrameEvents.FriendlyDeathsRecent && safeForwardWindow);
        var canCallFight = engagement.CanTakeFight
            || engagement.ShouldCounterEngage
            || (engagement.CanPush && safeForwardWindow)
            || safeBurstCleanup
            || splitPressureWindow
            || powerWindow
            || cleanupWindow;

        var kitePlan = ResolveKiteReengagePlan(teamSituation, risk, engagement, fightPlan, playerFrameEvents, target, targetName, destination);
        if (kitePlan.ShouldKite && !engagement.HasBurstWindow)
            return;

        if (!canCallFight && !hasVulnerableTarget)
            return;

        var countdownWindow = ResolveSynchronizedCountdownWindow(teamSituation, risk, engagement, playerFrameEvents);
        var canGroupCountdown = countdownWindow.CanCountdown
            && (fightPlan.MustAttackLeader
                || engagement.ShouldCounterEngage
                || ((engagement.CanTakeFight || engagement.CanPush) && safeForwardWindow)
                || (engagement.HasBurstWindow && (safeForwardWindow || playerFrameEvents.EnemyControlledRecent > 0)));
        var focusScore = Math.Clamp(
            62f
            + target.Score * 0.32f
            + engagement.Score * 0.16f
            + (engagement.HasBurstWindow ? 10f : 0f)
            + (teamSituation.IsEnemySplit ? 6f : 0f),
            0f,
            98f);
        var focusUrgency = Math.Clamp(
            60f
            + target.Score * 0.24f
            + (target.HpPercent is > 0f and <= 35f ? 12f : 0f)
            + (target.IsCrowdControlled ? 10f : 0f)
            + (target.IsExecutable ? 12f : 0f),
            0f,
            98f);

        if (canGroupCountdown)
        {
            AddCommand(
                commands,
                $"micro:group-countdown:{(fightPlan.TargetBattalion.HasValue ? fightPlan.TargetBattalion.Value.ToString() : "main")}",
                BattlefieldCommandKind.Engage,
                "主团",
                $"敌方大团进线，{BuildCountdownCall(3)}，AOE往人最多处打",
                Math.Clamp(66f + engagement.Score * 0.24f + (fightPlan.MustAttackLeader ? 8f : 0f) + (engagement.HasBurstWindow ? 8f : 0f), 0f, 96f),
                Math.Clamp(68f + engagement.Score * 0.20f + (engagement.HasBurstWindow ? 8f : 0f), 0f, 96f),
                6,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : destination,
                fightPlan.IsAvailable ? $"{fightPlan.TargetName}大团" : "敌方大团",
                $"{ResolveMacroLockText(fightPlan)}倒数用于同步打敌方人最多的位置，不按单个人名喊",
                $"{engagement.EvidenceText}；{countdownWindow.EvidenceText}");
        }
        else if (hasVulnerableTarget)
        {
            AddCommand(
                commands,
                $"micro:focus-target:{target.TargetId}",
                BattlefieldCommandKind.FocusTarget,
                "全体",
                $"收 {targetName}，只打低血/被控，不追散人",
                focusScore,
                focusUrgency,
                5,
                destination,
                targetName,
                $"{ResolveMacroLockText(fightPlan)}单点只作为低血/被控收割，不作为大团倒数目标",
                $"{target.EvidenceText}；{countdownWindow.EvidenceText}");
        }

        var methodText = ResolveMicroFightMethod(teamSituation, risk, engagement, target);
        AddCommand(
            commands,
            $"micro:fight-method:{target.TargetId}:{methodText.GetHashCode()}",
            engagement.CanPush || splitPressureWindow ? BattlefieldCommandKind.Engage : BattlefieldCommandKind.PressureSide,
            "主团",
            hasTarget
                ? $"{methodText}；前排贴住，控制补 {targetName}，远程别换目标"
                : $"{methodText}；前排开路，远程准备转同一目标",
            Math.Clamp(58f + engagement.Score * 0.30f + (hasTarget ? target.Score * 0.16f : 0f), 0f, 94f),
            Math.Clamp(56f + engagement.Score * 0.22f + (engagement.HasBurstWindow ? 8f : 0f), 0f, 92f),
            7,
            destination,
            targetName,
            $"{ResolveMacroLockText(fightPlan)}实时打法：说明怎么打、何时压、火力怎么集中",
            $"{engagement.EvidenceText}；{target.EvidenceText}");
    }

    private static void AddFightDecisionCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        StrategicFightPlan fightPlan,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents)
    {
        if (IsFatalFightState(risk, teamSituation))
            return;

        var target = ResolveMicroFocusTarget(teamSituation, fightPlan, anchor);
        var hasTarget = !string.IsNullOrWhiteSpace(target.Name);
        var hasVulnerableTarget = hasTarget
            && (target.IsExecutable || target.IsCrowdControlled || target.HpPercent is > 0f and <= 35f);
        var strategicTarget = fightPlan.IsAvailable ? fightPlan.TargetName : ResolveScoreFightTargetName(scoreSituation);
        var targetName = hasTarget
            ? target.Name
            : !string.IsNullOrWhiteSpace(strategicTarget) ? strategicTarget : "敌方主团";
        var destination = hasTarget && IsMeaningfulPosition(target.Position)
            ? target.Position
            : IsMeaningfulPosition(fightPlan.TargetPosition)
                ? fightPlan.TargetPosition
                : teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor;
        var objectiveName = primary.HasValue && !string.IsNullOrWhiteSpace(primary.Value.Name)
            ? primary.Value.Name
            : !string.IsNullOrWhiteSpace(timeSituation.NextResourceName) ? timeSituation.NextResourceName : "下一目标";
        var highValueObjective = primary.HasValue && IsHighValueObjective(primary.Value);
        var resourceSoon = timeSituation.NextResourceSeconds.HasValue && timeSituation.NextResourceSeconds.Value <= 10;
        var objectiveDemand = primary.HasValue && (primary.Value.PriorityScore >= 58f || highValueObjective || resourceSoon);
        var openingAggressiveWindow = IsOpeningBattleHighFarmWindow(timeSituation) && !objectiveDemand;
        var enemyWeakWindow = teamSituation.LimitBreakThreats.EnemyLikelyReadyCount == 0
            && teamSituation.Enemy.BattleFeverCount == 0
            && teamSituation.Enemy.BattleHighTotalLevel <= teamSituation.Friendly.BattleHighTotalLevel + 4;
        var safeForwardWindow = IsSafeForwardFightWindow(engagement, risk);
        var safeOpeningPressure = openingAggressiveWindow
            && IsSafeForwardFightWindow(engagement, risk, allowOpeningRelaxation: true)
            && (engagement.CanTakeFight
                || engagement.ShouldCounterEngage
                || engagement.CanPush
                || engagement.HasBurstWindow
                || teamSituation.IsEnemySplit
                || teamSituation.Enemy.LowHpCount > 0
                || teamSituation.Enemy.CrowdControlledCount > 0
                || teamSituation.Friendly.BattleFeverCount > 0
                || playerFrameEvents.EnemyControlledRecent > 0
                || playerFrameEvents.EnemyDeathsRecent > playerFrameEvents.FriendlyDeathsRecent);
        var enemyWeakFightWindow = enemyWeakWindow
            && IsSafeForwardFightWindow(engagement, risk, allowOpeningRelaxation: openingAggressiveWindow)
            && engagement.Score >= (openingAggressiveWindow ? 56f : 60f)
            && (engagement.CanTakeFight || engagement.CanPush || engagement.HasBurstWindow || teamSituation.IsEnemySplit);
        var splitPressureWindow = teamSituation.IsEnemySplit
            && IsSafeForwardFightWindow(engagement, risk, allowOpeningRelaxation: openingAggressiveWindow)
            && (engagement.CanTakeFight || engagement.CanPush || engagement.HasBurstWindow || playerFrameEvents.EnemyControlledRecent > 0);
        var burstCleanupWindow = engagement.HasBurstWindow
            && (engagement.ShouldCounterEngage
                || safeForwardWindow
                || playerFrameEvents.EnemyControlledRecent > 0
                || teamSituation.Enemy.LowHpCount > 0
                || teamSituation.Enemy.CrowdControlledCount > 0);
        var hasFightWindow = engagement.CanTakeFight
            || engagement.ShouldCounterEngage
            || (engagement.CanPush && safeForwardWindow)
            || burstCleanupWindow
            || splitPressureWindow
            || (teamSituation.Friendly.BattleFeverCount > 0 && safeForwardWindow)
            || (teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 1 && safeForwardWindow && engagement.Score >= 58f)
            || (playerFrameEvents.EnemyControlledRecent > 0 && engagement.Score >= 54f && risk.RetreatRouteRisk < 80f)
            || (playerFrameEvents.EnemyDeathsRecent > playerFrameEvents.FriendlyDeathsRecent && safeForwardWindow)
            || safeOpeningPressure
            || fightPlan.MustAttackLeader
            || fightPlan.Endgame
            || enemyWeakFightWindow;

        var kitePlan = ResolveKiteReengagePlan(teamSituation, risk, engagement, fightPlan, playerFrameEvents, target, targetName, destination);
        if (kitePlan.ShouldKite)
        {
            AddCommand(
                commands,
                kitePlan.Id,
                kitePlan.Kind,
                "主团",
                kitePlan.CommandText,
                kitePlan.Score,
                kitePlan.Urgency,
                6,
                kitePlan.Position,
                kitePlan.TargetName,
                kitePlan.ReasonText,
                kitePlan.EvidenceText);
            return;
        }

        if (!hasFightWindow && !hasVulnerableTarget)
            return;

        var countdownWindow = ResolveSynchronizedCountdownWindow(teamSituation, risk, engagement, playerFrameEvents);
        var canSingleTargetCountdown = hasTarget
            && countdownWindow.CanCountdown
            && (target.IsCrowdControlled
                || target.IsExecutable
                || target.HpPercent is > 0f and <= 35f
                || engagement.HasBurstWindow
                || fightPlan.AggressiveEndgame);
        var canGroupCountdown = countdownWindow.CanCountdown
            && (engagement.ShouldCounterEngage
                || ((engagement.CanTakeFight || engagement.CanPush) && (safeForwardWindow || safeOpeningPressure))
                || (engagement.HasBurstWindow && (safeForwardWindow || safeOpeningPressure))
                || fightPlan.MustAttackLeader
                || fightPlan.AggressiveEndgame);
        var countdown = canGroupCountdown
            ? BuildCountdownCall(3)
            : canSingleTargetCountdown ? BuildCountdownCall(ResolveFocusCountdownSeconds(target, engagement, playerFrameEvents)) : string.Empty;
        var methodText = ResolveMicroFightMethod(teamSituation, risk, engagement, target);
        var groupTargetName = fightPlan.IsAvailable ? $"{fightPlan.TargetName}大团" : "敌方大团";
        var executionText = canGroupCountdown
            ? $"{countdown}，打{groupTargetName}，AOE往人最多处落"
            : hasTarget
                ? $"收 {targetName}，只打低血/被控"
                : $"压{groupTargetName}，前排开路，远程打人最多处";
        var objectiveFollowText = highValueObjective || resourceSoon
            ? $"边进 {objectiveName} 边打，不为零散敌情停步"
            : $"打出人数差再转 {objectiveName}";
        var prefix = safeOpeningPressure
            ? "前4分钟先打架炒战意"
            : fightPlan.MustAttackLeader
                ? "看分先打关键家"
                : engagement.HasBurstWindow
                    ? "控场窗口先打一波"
                    : teamSituation.IsEnemySplit
                        ? "敌方分散先抓人"
                        : "现在先打这一波";
        var score = Math.Clamp(
            58f
            + engagement.Score * 0.16f
            + target.Score * 0.06f
            + ResolveScorePressure(scoreSituation) * 0.06f
            + (safeOpeningPressure ? 4f : 0f)
            + (engagement.HasBurstWindow ? 8f : 0f)
            + (fightPlan.MustAttackLeader ? 7f : 0f)
            + (fightPlan.AggressiveEndgame ? 10f : 0f)
            + (canGroupCountdown ? 7f : 0f)
            + (enemyWeakFightWindow ? 4f : 0f)
            - (objectiveDemand ? 16f : 0f),
            0f,
            99f);
        var urgency = Math.Clamp(
            58f
            + (target.IsExecutable ? 12f : 0f)
            + (target.IsCrowdControlled ? 9f : 0f)
            + (target.HpPercent is > 0f and <= 35f ? 10f : 0f)
            + (resourceSoon ? 4f : 0f)
            + (fightPlan.Endgame ? 8f : 0f)
            + (fightPlan.AggressiveEndgame ? 8f : 0f)
            + (canGroupCountdown ? 7f : 0f),
            0f,
            98f);
        var commandId = canGroupCountdown
            ? $"fight:decision:group:{(fightPlan.TargetBattalion.HasValue ? fightPlan.TargetBattalion.Value.ToString() : "main")}"
            : hasTarget
            ? $"fight:decision:{target.TargetId}"
            : $"fight:decision:{(fightPlan.TargetBattalion.HasValue ? fightPlan.TargetBattalion.Value.ToString() : "main")}";

        AddCommand(
            commands,
            commandId,
            canGroupCountdown ? BattlefieldCommandKind.Engage : hasTarget ? BattlefieldCommandKind.FocusTarget : BattlefieldCommandKind.Engage,
            "主团",
            $"{prefix}：{executionText}；{methodText}，{objectiveFollowText}",
            score,
            urgency,
            6,
            destination,
            canGroupCountdown ? groupTargetName : targetName,
            $"{ResolveMacroLockText(fightPlan)}战斗执行优先于普通赶点，先用击杀/人数差服务拿分",
            $"{engagement.EvidenceText}；{fightPlan.EvidenceText}；{target.EvidenceText}；{countdownWindow.EvidenceText}；目标 {objectiveName}；高价值 {highValueObjective}；资源将刷 {resourceSoon}");
    }

    private static string ResolveScoreFightTargetName(BattlefieldScoreSituationSnapshot scoreSituation)
    {
        var target = scoreSituation.RankedAlliances
            .Where(alliance => !alliance.IsLocalAlliance)
            .OrderBy(alliance => alliance.RankIndex <= 0 ? 99 : alliance.RankIndex)
            .ThenByDescending(alliance => alliance.Score)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(target.Name) ? "高分敌方" : target.Name;
    }

    private static string ResolveMacroLockText(StrategicFightPlan fightPlan)
        => fightPlan.IsAvailable ? $"战略锁定 {fightPlan.TargetName}；" : string.Empty;

    private static MicroFocusTarget ResolveMicroFocusTarget(
        BattlefieldTeamSituationSnapshot teamSituation,
        StrategicFightPlan fightPlan,
        Vector3 anchor)
    {
        var focus = teamSituation.FriendlyFocusTargets
            .Where(target => target.TargetGameObjectId != 0
                && (target.AttackerCount + target.CasterCount >= 3
                    || target.HpPercent is > 0f and <= 35f))
            .Select(target => new MicroFocusTarget(
                target.TargetGameObjectId.ToString(),
                target.TargetName,
                target.Position,
                target.HpPercent,
                target.TargetJobName,
                false,
                false,
                false,
                Math.Clamp(45f + target.ThreatScore * 12f + target.AttackerCount * 7f + target.CasterCount * 5f + (target.HpPercent is > 0f and <= 40f ? 18f : 0f), 0f, 100f),
                $"已有友方集火 {target.AttackerCount} 人/咏唱 {target.CasterCount}；血量 {FormatHpPercent(target.HpPercent)}；职业 {target.TargetJobName}"))
            .OrderByDescending(target => target.Score)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(focus.Name))
            return focus;

        var enemies = teamSituation.EnemyAlliance1Players
            .Concat(teamSituation.EnemyAlliance2Players)
            .Where(player => !player.IsDead && !player.IsInvulnerable)
            .Select(player =>
            {
                var targetAllianceBonus = fightPlan.TargetBattalion.HasValue && player.Battalion == fightPlan.TargetBattalion.Value ? 10f : 0f;
                var hpScore = player.HpPercent > 0f ? Math.Clamp(65f - player.HpPercent, 0f, 40f) : 0f;
                var distanceScore = Math.Clamp(70f - Distance2D(anchor, player.Position), 0f, 24f) * 0.35f;
                var score = 42f
                    + hpScore
                    + distanceScore
                    + (player.IsExecutable ? 28f : 0f)
                    + (player.IsCrowdControlled ? 22f : 0f)
                    + (player.IsCasting ? 8f : 0f)
                    + (player.IsGuarding ? -24f : 0f)
                    + (player.BattleHighLevel >= 4 || player.IsBattleFever ? 8f : 0f)
                    + targetAllianceBonus;
                var evidence = $"可见敌方；血量 {FormatHpPercent(player.HpPercent)}；距离 {Distance2D(anchor, player.Position):0}y；{(player.IsCrowdControlled ? "被控；" : string.Empty)}{(player.IsExecutable ? "低血；" : string.Empty)}{(player.IsCasting ? "读条；" : string.Empty)}战意 {player.BattleHighLevel}{(player.IsBattleFever ? "/狂热" : string.Empty)}";
                return new MicroFocusTarget(
                    player.GameObjectId.ToString(),
                    player.Name,
                    player.Position,
                    player.HpPercent,
                    string.Empty,
                    player.IsCrowdControlled,
                    player.IsExecutable,
                    player.IsGuarding,
                    Math.Clamp(score, 0f, 100f),
                    evidence);
            })
            .OrderByDescending(target => target.Score)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(enemies.Name))
            return enemies;

        return enemies.IsExecutable
            || enemies.IsCrowdControlled
            || enemies.HpPercent is > 0f and <= 35f
            || enemies.Score >= 84f
            ? enemies
            : default;
    }

    private static int ResolveFocusCountdownSeconds(
        MicroFocusTarget target,
        EngagementOpportunity engagement,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents)
    {
        if (target.IsExecutable || target.HpPercent is > 0f and <= 28f || playerFrameEvents.EnemyControlledRecent >= 2)
            return 1;
        if (target.IsCrowdControlled || engagement.HasBurstWindow || target.HpPercent is > 0f and <= 45f)
            return 2;
        return 3;
    }

    private static CountdownWindow ResolveSynchronizedCountdownWindow(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents)
    {
        var hasDistance = TryResolveMainClusterDistance(teamSituation, out var clusterDistance);
        var recentEnemyMajorUses = teamSituation.KeySkillThreats.RecentUses.Count(use =>
            use.Relation == BattlefieldPlayerRelation.Enemy
            && use.AgeMs is >= 0 and <= CountdownRecentEnemySkillWindowMs
            && IsCountdownRelevantEnemySkill(use.Kind));
        var recentEnemyDefensive = playerFrameEvents.EnemyDefensiveRecent;
        var enemyControlled = playerFrameEvents.EnemyControlledRecent + teamSituation.Enemy.CrowdControlledCount;
        var enemyReadyThreats = teamSituation.KeySkillThreats.EnemyHighThreatCount
            + teamSituation.KeySkillThreats.EnemyControlChainCount
            + teamSituation.LimitBreakThreats.EnemyHighThreatCount;
        var enemyLikelyReady = teamSituation.KeySkillThreats.EnemyLikelyReadyCount
            + teamSituation.LimitBreakThreats.EnemyLikelyReadyCount;
        var spentSignals = recentEnemyMajorUses
            + recentEnemyDefensive
            + Math.Min(2, enemyControlled);
        var enemySpentEnough = spentSignals >= 2
            && enemyReadyThreats <= 2
            && enemyLikelyReady <= 4;
        var closeEnough = hasDistance && clusterDistance <= SynchronizedCountdownClusterDistanceYalms;
        var friendlyReady = teamSituation.KeySkillThreats.FriendlyLikelyReadyCount
            + teamSituation.KeySkillThreats.FriendlyHighThreatCount
            + teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 2
            || teamSituation.Friendly.BattleFeverCount > 0;
        var safeEnough = !IsFatalFightState(risk, teamSituation)
            && risk.ThirdPartyPincerRisk < 82f
            && risk.EncirclementRisk < 84f
            && risk.HighGroundDropRisk < 78f;
        var canCountdown = closeEnough
            && enemySpentEnough
            && friendlyReady
            && safeEnough
            && (engagement.HasBurstWindow || engagement.CanPush || enemyControlled >= 2);
        var distanceText = hasDistance ? $"{clusterDistance:0}y" : "未知";
        var evidence = $"倒数窗：团距 {distanceText}；敌已交 {spentSignals}；敌可用威胁 {enemyReadyThreats}/{enemyLikelyReady}；我方工具 {(friendlyReady ? "足" : "不足")}；风险 {risk.OverallRisk:0}";

        return new CountdownWindow(
            canCountdown,
            hasDistance,
            clusterDistance,
            spentSignals,
            enemyReadyThreats,
            enemyLikelyReady,
            evidence);
    }

    private static bool TryResolveMainClusterDistance(BattlefieldTeamSituationSnapshot teamSituation, out float distance)
    {
        distance = 9999f;
        if (!teamSituation.Friendly.MainCluster.HasValue)
            return false;

        var friendlyCenter = teamSituation.Friendly.MainCluster.Value.Center;
        Vector3 enemyCenter;
        if (teamSituation.EnemyMainGroupMovement.HasMainGroup)
            enemyCenter = teamSituation.EnemyMainGroupMovement.CurrentCenter;
        else if (teamSituation.Enemy.MainCluster.HasValue)
            enemyCenter = teamSituation.Enemy.MainCluster.Value.Center;
        else
            return false;

        distance = Distance2D(friendlyCenter, enemyCenter);
        return true;
    }

    private static bool IsCountdownRelevantEnemySkill(BattlefieldKeySkillKind kind)
        => kind is BattlefieldKeySkillKind.Engage
            or BattlefieldKeySkillKind.CrowdControl
            or BattlefieldKeySkillKind.GuardBreak
            or BattlefieldKeySkillKind.Burst
            or BattlefieldKeySkillKind.AreaPressure
            or BattlefieldKeySkillKind.Support
            or BattlefieldKeySkillKind.Defensive
            or BattlefieldKeySkillKind.Purify
            or BattlefieldKeySkillKind.Invulnerability;

    private static string BuildCountdownCall(int seconds)
        => seconds <= 1
            ? "1秒倒数集火：1，打"
            : seconds == 2
                ? "2秒倒数集火：2、1，打"
                : "3秒倒数集火：3、2、1，打";

    private static string ResolveMicroFightMethod(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        MicroFocusTarget target)
    {
        if (target.IsExecutable || target.HpPercent is > 0f and <= 30f)
            return "现在打，目标残血可收";
        if (target.IsCrowdControlled || engagement.HasBurstWindow)
            return "控场窗口已出，立刻压一波";
        if (teamSituation.IsEnemySplit)
            return "敌方分散，主团压最近一组逐个收";
        if (teamSituation.Friendly.BattleFeverCount > 0 || teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 1)
            return "我方战意/极限技窗口，正面开团";
        if (risk.ChokeBlockRisk >= 70f)
            return "卡住路口打，不让敌方展开";
        if (engagement.CanPush)
            return "正面推进，打完一波再转点";
        return "先压到技能距离，等敌方大团进线再倒数";
    }

    private static KiteReengagePlan ResolveKiteReengagePlan(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        StrategicFightPlan fightPlan,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        MicroFocusTarget target,
        string fallbackTargetName,
        Vector3 fallbackPosition)
    {
        if (IsFatalFightState(risk, teamSituation))
            return default;

        var hasRealKillTarget = !string.IsNullOrWhiteSpace(target.Name)
            && (target.IsExecutable
                || target.IsCrowdControlled
                || target.HpPercent is > 0f and <= 34f
                || target.Score >= 82f);
        if (hasRealKillTarget
            && risk.EncirclementRisk < 80f
            && risk.ThirdPartyPincerRisk < 78f
            && risk.AmbushRisk < 82f
            && risk.HighGroundDropRisk < 84f)
        {
            return default;
        }

        var noFightWindow = !engagement.CanTakeFight
            || (!engagement.HasBurstWindow
                && engagement.VulnerableEnemyCount <= 1
                && target.Score < 64f
                && playerFrameEvents.EnemyControlledRecent == 0);
        var enemyBurstWindow = risk.SkillThreatRisk >= 86f
            || risk.LimitBreakRisk >= 88f
            || risk.HighGroundDropRisk >= 78f
            || risk.CoordinatedSquadRisk >= 80f;
        var pincerForming = risk.ThirdPartyPincerRisk >= 72f
            || risk.EncirclementRisk >= 76f
            || (risk.FlankRisk >= 82f && risk.RetreatRouteRisk >= 70f);
        var fakeRetreatOrDeepChase = teamSituation.AdvancedTactics.IsEnemyFakeRetreatAmbushLikely
            || risk.AmbushRisk >= 74f
            || (risk.RetreatRouteRisk >= 80f && risk.EnemyMainGroupDirectionRisk >= 62f && !engagement.CanPush);
        var badLocalExchange = engagement.LocalAdvantage <= -4
            || (risk.NumberDisadvantageRisk >= 82f && engagement.FriendlyLocalCount < engagement.EnemyLocalCount);
        var friendlyFragile = (playerFrameEvents.FriendlyDeathsRecent >= 2 && playerFrameEvents.EnemyDeathsRecent == 0)
            || playerFrameEvents.FriendlyControlledRecent >= 4
            || teamSituation.RespawnRhythm.FriendlyDeadNow >= 6;

        var shouldKite = (noFightWindow || badLocalExchange || friendlyFragile)
            && (enemyBurstWindow || pincerForming || fakeRetreatOrDeepChase || badLocalExchange || friendlyFragile);
        if (!shouldKite)
            return default;

        if (fightPlan.Endgame
            && fightPlan.MustAttackLeader
            && risk.OverallRisk < 78f
            && !enemyBurstWindow
            && !pincerForming
            && !fakeRetreatOrDeepChase
            && !friendlyFragile)
        {
            return default;
        }

        if (fightPlan.AggressiveEndgame
            && fightPlan.MustAttackLeader
            && risk.OverallRisk < 86f
            && risk.ThirdPartyPincerRisk < 84f
            && risk.EncirclementRisk < 86f
            && !friendlyFragile)
        {
            return default;
        }

        var targetName = !string.IsNullOrWhiteSpace(fallbackTargetName)
            ? fallbackTargetName
            : fightPlan.IsAvailable ? fightPlan.TargetName : "高分家";
        var position = IsMeaningfulPosition(fallbackPosition)
            ? fallbackPosition
            : teamSituation.Friendly.MainCluster?.Center ?? Vector3.Zero;
        var maxRisk = MathF.Max(
            risk.OverallRisk,
            MathF.Max(risk.SkillThreatRisk, MathF.Max(risk.ThirdPartyPincerRisk, MathF.Max(risk.EncirclementRisk, risk.AmbushRisk))));
        var score = Math.Clamp(
            74f
            + maxRisk * 0.16f
            + Math.Max(0, -engagement.LocalAdvantage) * 3f
            + (fakeRetreatOrDeepChase ? 8f : 0f)
            + (pincerForming ? 7f : 0f)
            + (enemyBurstWindow ? 6f : 0f)
            + (friendlyFragile ? 5f : 0f),
            0f,
            99f);
        var urgency = Math.Clamp(
            76f
            + maxRisk * 0.14f
            + (pincerForming ? 8f : 0f)
            + (enemyBurstWindow ? 7f : 0f)
            + Math.Max(0, -engagement.LocalAdvantage) * 2f,
            0f,
            99f);

        var commandText = fakeRetreatOrDeepChase
            ? $"别追深，横拉5秒，等敌方回头脱节，反打大团边缘 {targetName}"
            : pincerForming
                ? $"不进夹角，往己方侧拉开，清最近侧，再回头打 {targetName}"
                : enemyBurstWindow
                    ? $"先不硬接，横向拉开骗爆发，敌方技能交完反打大团边缘"
                    : $"少人/被控不硬接，慢退到火力线，等人跟上再打 {targetName}";
        var reason = fakeRetreatOrDeepChase
            ? "敌方可能诱追或深追路线变差，先把敌方拉脱节"
            : pincerForming
                ? "夹角正在成形，先换位置制造反打角度"
                : enemyBurstWindow
                    ? "敌方爆发/空降压力高，先骗技能再打"
                    : "当前交换窗口差，先拉到队友火力线重新开";
        var evidence = $"{engagement.EvidenceText}；夹角 {risk.EncirclementRisk:0}；第三方 {risk.ThirdPartyPincerRisk:0}；爆发 {MathF.Max(risk.SkillThreatRisk, risk.LimitBreakRisk):0}；假撤 {risk.AmbushRisk:0}；我方倒地 {playerFrameEvents.FriendlyDeathsRecent}；我方被控 {playerFrameEvents.FriendlyControlledRecent}";
        var id = fakeRetreatOrDeepChase
            ? "fight:decision:kite-bait"
            : pincerForming
                ? "fight:decision:kite-pincer"
                : enemyBurstWindow ? "fight:decision:kite-burst" : "fight:decision:kite-reset";

        return new KiteReengagePlan(
            true,
            id,
            BattlefieldCommandKind.Disengage,
            commandText,
            score,
            urgency,
            position,
            targetName,
            reason,
            evidence);
    }

    private static BattlefieldCommandSnapshot ApplyDecisionQualityModifier(
        BattlefieldCommandSnapshot command,
        IReadOnlyList<BattlefieldCommandEffectivenessSnapshot> effectiveness)
    {
        var feedback = effectiveness.FirstOrDefault(item => item.Kind == command.Kind && item.SampleCount >= 4);
        if (feedback.SampleCount < 4 || MathF.Abs(feedback.Modifier) < 0.1f)
            return command;

        var score = Math.Clamp(command.Score + feedback.Modifier, 0f, 100f);
        var urgency = Math.Clamp(command.Urgency + feedback.Modifier * 0.45f, 0f, 100f);
        var evidence = $"{command.EvidenceText}；回放调权 {feedback.Modifier:+0.0;-0.0;0}（{feedback.SampleCount} 样本）";
        return new BattlefieldCommandSnapshot(
            command.Id,
            command.Kind,
            command.Scope,
            command.CommandText,
            score,
            urgency,
            command.CooldownSeconds,
            command.Position,
            command.TargetName,
            command.ReasonText,
            evidence);
    }

    private (BattlefieldActionCandidateSnapshot? Action, bool IsHeld, int HoldRemainingSeconds, string HoldReason) ResolvePrimaryAction(
        IReadOnlyList<BattlefieldActionCandidateSnapshot> candidates,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        var now = Environment.TickCount64;
        if (candidates.Count == 0)
        {
            heldAction = null;
            return (null, false, 0, string.Empty);
        }

        var best = candidates[0];
        if (heldAction.HasValue)
        {
            var held = heldAction.Value;
            var current = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, held.ActionId, StringComparison.Ordinal)
                || string.Equals(BuildActionDedupeKey(candidate), held.ActionKey, StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(current.Id))
            {
                var remainingMs = Math.Max(0, held.HoldUntilTicks - now);
                var remainingSeconds = FormatSecondsCeiling(remainingMs);
                var heldPriority = ResolveActionSortPriority(current);
                var bestPriority = ResolveActionSortPriority(best);
                var expired = remainingMs <= 0;
                var failureLikely = IsActionFailureLikely(current, risk, timeSituation, teamSituation);
                var emergencySwitch = IsEmergencyAction(best) && !string.Equals(best.Id, current.Id, StringComparison.Ordinal) && bestPriority >= heldPriority + 4f;
                var combatSwitch = !string.Equals(best.Id, current.Id, StringComparison.Ordinal)
                    && IsCombatAction(best)
                    && !IsCombatAction(current)
                    && bestPriority >= heldPriority - 2f
                    && best.Urgency >= 62f;
                var meaningfulSwitch = !string.Equals(best.Id, current.Id, StringComparison.Ordinal)
                    && bestPriority >= heldPriority + ActionSwitchPriorityMargin
                    && best.Confidence >= current.Confidence - 8f;

                if (!expired && !failureLikely && !emergencySwitch && !combatSwitch && !meaningfulSwitch)
                {
                    return (current, true, remainingSeconds, $"这条先执行完，暂不改口");
                }
            }
        }

        heldAction = new ActionHoldState(
            best.Id,
            BuildActionDedupeKey(best),
            best.ActionType,
            now,
            now + Math.Max(3, best.HoldSeconds) * 1000L,
            ResolveActionSortPriority(best),
            best.Text);
        return (best, false, best.HoldSeconds, "新行动已锁定");
    }

    private static bool IsEmergencyAction(BattlefieldActionCandidateSnapshot candidate)
        => candidate.ActionType is BattlefieldActionType.Retreat
            or BattlefieldActionType.ReturnToBase
            or BattlefieldActionType.Spread
            || candidate.Urgency >= 88f
            || ResolveActionSortPriority(candidate) >= 88f;

    private static bool IsCombatAction(BattlefieldActionCandidateSnapshot candidate)
        => candidate.ActionType is BattlefieldActionType.Engage
            or BattlefieldActionType.FocusTarget
            or BattlefieldActionType.Flank
            or BattlefieldActionType.WrapBehind
            or BattlefieldActionType.BacklinePressure
            || candidate.CommandKind is BattlefieldCommandKind.Engage
                or BattlefieldCommandKind.FocusTarget
                or BattlefieldCommandKind.PressureSide;

    private static IReadOnlyList<BattlefieldCommandSnapshot> BuildPublishCommandOrder(
        IReadOnlyList<BattlefieldCommandSnapshot> ordered,
        BattlefieldActionCandidateSnapshot? selectedAction)
    {
        if (!selectedAction.HasValue || string.IsNullOrWhiteSpace(selectedAction.Value.CommandId) || ordered.Count <= 1)
            return ordered;

        var selectedCommandId = selectedAction.Value.CommandId;
        var selected = ordered.FirstOrDefault(command => string.Equals(command.Id, selectedCommandId, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(selected.Id) || string.Equals(selected.Id, ordered[0].Id, StringComparison.Ordinal))
            return ordered;

        return ordered
            .Where(command => !string.Equals(command.Id, selected.Id, StringComparison.Ordinal))
            .Prepend(selected)
            .ToArray();
    }

    private static BattlefieldCommandSnapshot? ResolveAlignedPrimaryCommand(
        IReadOnlyList<BattlefieldCommandSnapshot> ordered,
        BattlefieldCommandSnapshot? publishedCommand,
        BattlefieldActionCandidateSnapshot? selectedAction,
        BattlefieldCommandSnapshot? fallbackPrimary)
    {
        if (selectedAction.HasValue)
        {
            var action = selectedAction.Value;
            if (!string.IsNullOrWhiteSpace(action.CommandId))
            {
                var matched = ordered.FirstOrDefault(command => string.Equals(command.Id, action.CommandId, StringComparison.Ordinal));
                if (!string.IsNullOrWhiteSpace(matched.Id))
                    return matched;

                if (publishedCommand.HasValue && string.Equals(publishedCommand.Value.Id, action.CommandId, StringComparison.Ordinal))
                    return publishedCommand.Value;
            }

            var derived = BuildAlignedCommandFromAction(action);
            if (derived.HasValue)
                return derived.Value;
        }

        if (publishedCommand.HasValue)
            return publishedCommand.Value;
        return fallbackPrimary;
    }

    private static BattlefieldCommandSnapshot? BuildAlignedCommandFromAction(BattlefieldActionCandidateSnapshot action)
    {
        var commandText = BuildCommandTextFromAction(action);
        if (string.IsNullOrWhiteSpace(commandText))
            return null;

        var targetName = !string.IsNullOrWhiteSpace(action.TargetName)
            ? action.TargetName
            : action.DestinationName;
        var commandKind = action.CommandKind != BattlefieldCommandKind.Unknown
            ? action.CommandKind
            : ResolveCommandKind(action.ActionType);
        var score = Math.Clamp(
            action.Priority * 0.50f
            + action.Confidence * 0.18f
            + action.Urgency * 0.22f
            + Math.Max(0f, 100f - action.Risk) * 0.10f,
            0f,
            100f);

        return new BattlefieldCommandSnapshot(
            !string.IsNullOrWhiteSpace(action.CommandId) ? action.CommandId : $"derived:{action.Id}",
            commandKind,
            string.IsNullOrWhiteSpace(action.Scope) ? "主团" : action.Scope,
            commandText,
            score,
            Math.Clamp(action.Urgency, 0f, 100f),
            Math.Clamp(action.HoldSeconds, 3, 30),
            action.Destination,
            targetName ?? string.Empty,
            action.ReasonText,
            action.EvidenceText);
    }

    private static string BuildCommandTextFromAction(BattlefieldActionCandidateSnapshot action)
    {
        var scope = string.IsNullOrWhiteSpace(action.Scope) ? "主团" : action.Scope;
        var target = !string.IsNullOrWhiteSpace(action.TargetName)
            ? action.TargetName
            : !string.IsNullOrWhiteSpace(action.DestinationName) ? action.DestinationName : "目标";

        return action.ActionType switch
        {
            BattlefieldActionType.Rotate => $"{scope}转向 {target}",
            BattlefieldActionType.DefendObjective => $"{scope}控住 {target}",
            BattlefieldActionType.ContestObjective => $"{scope}抢 {target}",
            BattlefieldActionType.AbandonObjective => $"{scope}别硬进 {target}",
            BattlefieldActionType.AttackIce => $"{scope}打冰 {target}",
            BattlefieldActionType.TouchObjective => $"{scope}摸 {target}",
            BattlefieldActionType.InterruptTouch => $"{scope}断摸 {target}",
            BattlefieldActionType.Engage => $"{scope}接团 {target}",
            BattlefieldActionType.Retreat => $"{scope}脱出夹角",
            BattlefieldActionType.ReturnToBase => $"{scope}回会合点",
            BattlefieldActionType.Flank => $"{scope}侧压 {target}",
            BattlefieldActionType.WrapBehind => $"{scope}绕后压 {target}",
            BattlefieldActionType.BacklinePressure => $"{scope}压后排 {target}",
            BattlefieldActionType.FocusTarget => $"{scope}集火 {target}",
            BattlefieldActionType.ProtectHighBattleHigh => $"{scope}保高战意 {target}",
            BattlefieldActionType.Regroup => $"{scope}靠拢 {target}",
            BattlefieldActionType.Spread => $"{scope}横向展开",
            BattlefieldActionType.Detour => $"{scope}换侧线压 {target}",
            BattlefieldActionType.Hold => target.Contains("自家侧边线", StringComparison.Ordinal) ? $"{scope}挂边观察" : $"{scope}卡住 {target}",
            BattlefieldActionType.Wait => $"{scope}提前压向 {target}",
            _ => action.Text,
        };
    }

    private static bool IsActionFailureLikely(
        BattlefieldActionCandidateSnapshot candidate,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (candidate.CountdownSeconds.HasValue && candidate.EtaSeconds > 0 && candidate.CountdownSeconds.Value + 3 < candidate.EtaSeconds)
            return true;

        return candidate.ActionType switch
        {
            BattlefieldActionType.Rotate
                or BattlefieldActionType.ContestObjective
                or BattlefieldActionType.TouchObjective
                or BattlefieldActionType.AttackIce
                or BattlefieldActionType.InterruptTouch
                => IsFatalFightState(risk, teamSituation),
            BattlefieldActionType.Engage
                => IsFatalFightState(risk, teamSituation),
            BattlefieldActionType.Flank
                or BattlefieldActionType.WrapBehind
                or BattlefieldActionType.BacklinePressure
                => risk.ThirdPartyPincerRisk >= 86f && risk.EncirclementRisk >= 82f,
            BattlefieldActionType.DefendObjective
                or BattlefieldActionType.Hold
                => timeSituation.NextResourceSeconds.HasValue && timeSituation.NextResourceSeconds.Value <= 25,
            BattlefieldActionType.Retreat
                or BattlefieldActionType.ReturnToBase
                => risk.OverallRisk <= 48f && teamSituation.RespawnRhythm.FriendlyDeadNow <= 1,
            BattlefieldActionType.ProtectHighBattleHigh
                => risk.BattleHighRisk <= 30f,
            _ => false,
        };
    }

    private static BattlefieldActionCandidateSnapshot[] BuildActionCandidates(
        IReadOnlyList<BattlefieldCommandSnapshot> orderedCommands,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldTeamSituationSnapshot teamSituation,
        Vector3 anchor,
        BattlefieldInputReliabilitySnapshot inputReliability)
    {
        var candidates = new List<BattlefieldActionCandidateSnapshot>();

        foreach (var command in orderedCommands)
        {
            var candidate = BuildActionCandidateFromCommand(command, priorities, mapTactics, risk, timeSituation, teamSituation, anchor);
            if (!string.IsNullOrWhiteSpace(candidate.Id))
                candidates.Add(candidate);
        }

        foreach (var objective in priorities.Take(6))
        {
            var candidate = BuildObjectiveActionCandidate(objective, mapTactics, risk, anchor);
            if (!string.IsNullOrWhiteSpace(candidate.Id))
                candidates.Add(candidate);

            var interruptCandidate = BuildObjectiveInterruptCandidate(objective, mapTactics, risk, anchor);
            if (!string.IsNullOrWhiteSpace(interruptCandidate.Id))
                candidates.Add(interruptCandidate);
        }

        AddHighBattleHighProtectionCandidate(candidates, teamSituation, mapTactics, risk, anchor);
        AddEngageOrReturnCandidate(candidates, teamSituation, mapTactics, risk, timeSituation, anchor);
        AddFlankCandidate(candidates, teamSituation, mapTactics, risk, anchor);

        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Id) && candidate.Priority >= 35f)
            .Select(candidate => ApplyInputReliabilityToActionCandidate(candidate, inputReliability))
            .GroupBy(BuildActionDedupeKey, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(ResolveActionSortPriority)
                .ThenByDescending(candidate => candidate.Confidence)
                .First())
            .OrderByDescending(ResolveActionSortPriority)
            .ThenByDescending(candidate => candidate.Confidence)
            .ThenByDescending(candidate => candidate.Urgency)
            .Take(12)
            .ToArray();
    }

    private static BattlefieldActionCandidateSnapshot BuildActionCandidateFromCommand(
        BattlefieldCommandSnapshot command,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldTeamSituationSnapshot teamSituation,
        Vector3 anchor)
    {
        var objective = FindMatchingObjective(command, priorities);
        var actionType = ResolveActionType(command, objective, teamSituation);
        var route = ResolveBestRouteForAction(actionType, mapTactics, risk);
        var destination = ResolveActionDestination(command.Position, objective, actionType, teamSituation, anchor);
        var destinationName = ResolveActionDestinationName(command.TargetName, objective, actionType);
        var countdown = ResolveActionCountdown(command, objective, timeSituation);
        var eta = ResolveActionEta(destination, anchor, objective, route, actionType);
        var priority = ResolveCommandPriority(command);
        var actionRisk = objective.HasValue ? objective.Value.RiskScore : ResolveActionRisk(actionType, risk);
        var confidence = ResolveActionConfidence(command, objective, risk, actionType);
        var targetId = objective.HasValue ? objective.Value.ObjectiveId : ResolveCommandTargetId(command);
        var targetName = !string.IsNullOrWhiteSpace(command.TargetName)
            ? command.TargetName
            : objective.HasValue ? objective.Value.Name : destinationName;

        return CreateActionCandidate(
            $"action:command:{command.Id}",
            command.Id,
            actionType,
            command.Kind,
            command.Scope,
            BuildActionText(actionType, command.Scope, targetName, eta, countdown),
            priority,
            confidence,
            actionRisk,
            command.Urgency,
            destination,
            destinationName,
            targetId,
            targetName,
            route,
            countdown,
            eta,
            ResolveActionHoldSeconds(actionType, command.CooldownSeconds),
            ResolveActionPurpose(actionType),
            command.ReasonText,
            command.EvidenceText,
            ResolveActionFailureCondition(actionType));
    }

    private static BattlefieldActionCandidateSnapshot BuildObjectiveActionCandidate(
        BattlefieldObjectivePrioritySnapshot objective,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        Vector3 anchor)
    {
        var actionType = ResolveObjectiveActionType(objective);
        var commandKind = ResolveCommandKind(actionType);
        var route = ResolveBestRouteForAction(actionType, mapTactics, risk);
        var confidence = Math.Clamp(
            34f + objective.PriorityScore * 0.42f + objective.TimingScore * 0.16f + (100f - objective.RiskScore) * 0.16f,
            0f,
            100f);
        var urgency = Math.Clamp(objective.PriorityScore * 0.48f + objective.TimingScore * 0.34f + objective.PressureScore * 0.18f, 0f, 100f);
        var actionTarget = objective.ShouldHoldInstead ? "\u81ea\u5BB6\u4FA7\u8FB9\u7EBF" : objective.Name;
        var scope = actionType is BattlefieldActionType.TouchObjective or BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind ? "分队" : "主团";
        var eta = objective.MountedEtaSeconds > 0
            ? objective.MountedEtaSeconds
            : EstimateEtaSeconds(Distance2D(anchor, objective.Position), MountedYalmsPerSecond);

        return CreateActionCandidate(
            $"action:objective:{objective.ObjectiveId}:{actionType}",
            string.Empty,
            actionType,
            commandKind,
            scope,
            BuildActionText(actionType, scope, actionTarget, eta, objective.RemainingSeconds),
            objective.PriorityScore,
            confidence,
            objective.RiskScore,
            urgency,
            objective.Position,
            actionTarget,
            objective.ObjectiveId,
            actionTarget,
            route,
            objective.RemainingSeconds,
            eta,
            ResolveActionHoldSeconds(actionType, 0),
            ResolveActionPurpose(actionType),
            objective.RecommendedAction,
            objective.EvidenceText,
            ResolveActionFailureCondition(actionType));
    }

    private static BattlefieldActionCandidateSnapshot BuildObjectiveInterruptCandidate(
        BattlefieldObjectivePrioritySnapshot objective,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        Vector3 anchor)
    {
        if (objective.State != BattlefieldMapObjectiveState.Contested || !IsCaptureObjective(objective.Category))
            return default;

        var actionType = BattlefieldActionType.InterruptTouch;
        var route = ResolveBestRouteForAction(actionType, mapTactics, risk);
        var eta = objective.MountedEtaSeconds > 0
            ? objective.MountedEtaSeconds
            : EstimateEtaSeconds(Distance2D(anchor, objective.Position), MountedYalmsPerSecond);
        var priority = Math.Clamp(objective.PriorityScore + objective.TimingScore * 0.16f + (objective.RiskScore <= 70f ? 8f : 0f), 0f, 96f);
        var urgency = Math.Clamp(62f + objective.TimingScore * 0.22f + objective.PressureScore * 0.12f, 0f, 94f);

        return CreateActionCandidate(
            $"action:interrupt-touch:{objective.ObjectiveId}",
            string.Empty,
            actionType,
            BattlefieldCommandKind.FocusTarget,
            "控制/近战",
            BuildActionText(actionType, "控制/近战", objective.Name, eta, objective.RemainingSeconds),
            priority,
            Math.Clamp(46f + priority * 0.28f + (100f - objective.RiskScore) * 0.12f, 0f, 92f),
            objective.RiskScore,
            urgency,
            objective.Position,
            objective.Name,
            objective.ObjectiveId,
            objective.Name,
            route,
            objective.RemainingSeconds,
            eta,
            ResolveActionHoldSeconds(actionType, 0),
            ResolveActionPurpose(actionType),
            "目标正在争夺，占领读条需要被打断",
            objective.EvidenceText,
            ResolveActionFailureCondition(actionType));
    }

    private static void AddHighBattleHighProtectionCandidate(
        List<BattlefieldActionCandidateSnapshot> candidates,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        Vector3 anchor)
    {
        var target = teamSituation.FriendlyPlayers
            .Where(player => !player.IsDead && (player.IsBattleFever || player.BattleHighLevel >= 4))
            .OrderByDescending(player => player.IsBattleFever)
            .ThenByDescending(player => player.BattleHighLevel)
            .ThenBy(player => player.HpPercent <= 0f ? 101f : player.HpPercent)
            .FirstOrDefault();
        if (target.GameObjectId == 0)
            return;

        var focused = teamSituation.EnemyFocusTargets.Any(item =>
            item.TargetGameObjectId == target.GameObjectId
            || string.Equals(item.TargetName, target.Name, StringComparison.Ordinal));
        if (!focused && risk.BattleHighRisk < 48f && target.HpPercent > 65f)
            return;

        var route = ResolveBestRouteForAction(BattlefieldActionType.ProtectHighBattleHigh, mapTactics, risk);
        var priority = Math.Clamp(48f + target.BattleHighLevel * 8f + (target.IsBattleFever ? 10f : 0f) + risk.BattleHighRisk * 0.18f, 0f, 92f);
        var urgency = Math.Clamp(42f + (focused ? 24f : 0f) + (target.HpPercent is > 0f and <= 45f ? 18f : 0f), 0f, 88f);
        var eta = EstimateEtaSeconds(Distance2D(anchor, target.Position), MountedYalmsPerSecond);
        var feverText = target.IsBattleFever ? "战意狂热" : $"战意{target.BattleHighLevel}";

        candidates.Add(CreateActionCandidate(
            $"action:protect-bh:{target.GameObjectId}",
            string.Empty,
            BattlefieldActionType.ProtectHighBattleHigh,
            BattlefieldCommandKind.ProtectTarget,
            "辅助/近战",
            $"保护高战意 {target.Name}（{feverText}）",
            priority,
            Math.Clamp(48f + priority * 0.35f + urgency * 0.20f, 0f, 94f),
            Math.Clamp(risk.BattleHighRisk + (focused ? 16f : 0f), 0f, 100f),
            urgency,
            target.Position,
            target.Name,
            target.GameObjectId.ToString(),
            target.Name,
            route,
            null,
            eta,
            ResolveActionHoldSeconds(BattlefieldActionType.ProtectHighBattleHigh, 0),
            ResolveActionPurpose(BattlefieldActionType.ProtectHighBattleHigh),
            focused ? "高战意友方正在被集火" : "高战意友方价值高，防止被换掉",
            $"战意 {feverText}；血量 {target.HpPercent:0}%；战意风险 {risk.BattleHighRisk:0}",
            ResolveActionFailureCondition(BattlefieldActionType.ProtectHighBattleHigh)));
    }

    private static void AddEngageOrReturnCandidate(
        List<BattlefieldActionCandidateSnapshot> candidates,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldTimeSituationSnapshot timeSituation,
        Vector3 anchor)
    {
        var enemyComparable = ResolveComparableEnemyCount(teamSituation, teamSituation.Friendly.AliveCount, teamSituation.Enemy.AliveCount);
        var aliveAdvantage = teamSituation.Friendly.AliveCount - enemyComparable;
        var openingAggressiveWindow = IsOpeningBattleHighFarmWindow(timeSituation);
        var friendlyPowerWindow = teamSituation.Friendly.BattleFeverCount > 0
            || teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 2;
        var safeStandardEngage = aliveAdvantage >= (openingAggressiveWindow ? 0 : 1)
            && risk.OverallRisk <= (openingAggressiveWindow ? 76f : 72f)
            && risk.LimitBreakRisk <= (openingAggressiveWindow ? 80f : 78f)
            && risk.ThirdPartyPincerRisk < (openingAggressiveWindow ? 76f : 72f)
            && risk.EncirclementRisk < (openingAggressiveWindow ? 78f : 74f)
            && risk.RetreatRouteRisk < (openingAggressiveWindow ? 80f : 76f);
        var safePowerEngage = friendlyPowerWindow
            && aliveAdvantage >= 0
            && risk.OverallRisk <= 78f
            && risk.LimitBreakRisk <= 82f
            && risk.ThirdPartyPincerRisk < 76f
            && risk.EncirclementRisk < 78f
            && risk.RetreatRouteRisk < 80f;
        if (!IsFatalRisk(risk)
            && (safeStandardEngage || safePowerEngage))
        {
            var destination = teamSituation.EnemyMainGroupMovement.HasMainGroup
                ? teamSituation.EnemyMainGroupMovement.CurrentCenter
                : anchor;
            var route = ResolveBestRouteForAction(BattlefieldActionType.Engage, mapTactics, risk);
            var eta = EstimateEtaSeconds(Distance2D(anchor, destination), MountedYalmsPerSecond);
            candidates.Add(CreateActionCandidate(
                "action:engage:favorable",
                string.Empty,
                BattlefieldActionType.Engage,
                BattlefieldCommandKind.Engage,
                "主团",
                safePowerEngage && aliveAdvantage < 1 ? "接团，战意/极限技窗口可以打" : openingAggressiveWindow ? "接团，开局均势也能打" : "接团，人数窗口可以打",
                Math.Clamp(50f + aliveAdvantage * 3f + Math.Max(0f, 80f - risk.OverallRisk) * 0.16f + (openingAggressiveWindow ? 4f : 0f), 0f, 90f),
                Math.Clamp(48f + aliveAdvantage * 2f + Math.Max(0f, 82f - risk.LimitBreakRisk) * 0.08f + (friendlyPowerWindow ? 4f : 0f), 0f, 88f),
                risk.OverallRisk,
                Math.Clamp(46f + aliveAdvantage * 5f, 0f, 82f),
                destination,
                "敌方主团",
                string.Empty,
                "敌方主团",
                route,
                null,
                eta,
                ResolveActionHoldSeconds(BattlefieldActionType.Engage, 0),
                ResolveActionPurpose(BattlefieldActionType.Engage),
                openingAggressiveWindow ? "开局允许在均势下主动接团，但仍要求夹击和退路风险可控" : "我方战斗窗口可接受，普通风险不阻止接团",
                $"可比人数差 {aliveAdvantage:+0;-0;0}；总体 {risk.OverallRisk:0}；敌极限技 {risk.LimitBreakRisk:0}；第三方 {risk.ThirdPartyPincerRisk:0}；出路 {risk.RetreatRouteRisk:0}",
                ResolveActionFailureCondition(BattlefieldActionType.Engage)));
        }
    }

    private static void AddFlankCandidate(
        List<BattlefieldActionCandidateSnapshot> candidates,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk,
        Vector3 anchor)
    {
        if (IsFatalRisk(risk) || risk.ThirdPartyPincerRisk >= 86f || risk.EncirclementRisk >= 86f)
            return;

        var route = ResolveBestRouteForAction(BattlefieldActionType.Flank, mapTactics, risk);
        if (!route.HasValue && !teamSituation.EnemyMainGroupMovement.HasMainGroup)
            return;

        var destination = teamSituation.EnemyMainGroupMovement.HasMainGroup
            ? teamSituation.EnemyMainGroupMovement.PredictedNextCenter
            : anchor;
        var eta = route.HasValue && route.Value.MountedEtaSeconds > 0
            ? route.Value.MountedEtaSeconds
            : EstimateEtaSeconds(Distance2D(anchor, destination), MountedYalmsPerSecond);
        var priority = Math.Clamp(50f + (teamSituation.IsEnemySplit ? 14f : 0f) + Math.Max(0f, 82f - risk.OverallRisk) * 0.16f, 0f, 88f);

        candidates.Add(CreateActionCandidate(
            "action:flank:side-route",
            string.Empty,
            BattlefieldActionType.Flank,
            BattlefieldCommandKind.PressureSide,
            "分队",
            "分队夹击/绕后，主团别脱节",
            priority,
            Math.Clamp(42f + priority * 0.36f + (route.HasValue ? 8f : 0f), 0f, 84f),
            Math.Clamp(risk.OverallRisk + risk.RetreatRouteRisk * 0.12f, 0f, 100f),
            50f,
            destination,
            "敌方侧后",
            string.Empty,
            "敌方侧后",
            route,
            null,
            eta,
            ResolveActionHoldSeconds(BattlefieldActionType.Flank, 0),
            ResolveActionPurpose(BattlefieldActionType.Flank),
            teamSituation.IsEnemySplit ? "敌方分兵，侧翼有窗口" : "地图通行约束允许主动侧压",
            $"敌方分兵 {teamSituation.IsEnemySplit}；总体 {risk.OverallRisk:0}；通行样本 {(route.HasValue ? route.Value.TotalRisk.ToString("0") : "无")}",
            ResolveActionFailureCondition(BattlefieldActionType.Flank)));

        if (route.HasValue && route.Value.TotalRisk <= 78f && risk.RetreatRouteRisk <= 82f)
        {
            candidates.Add(CreateActionCandidate(
                "action:wrap-behind:side-route",
                string.Empty,
                BattlefieldActionType.WrapBehind,
                BattlefieldCommandKind.PressureSide,
                "分队",
                "分队绕后压敌方退路，主团正面控深追",
                Math.Clamp(priority - 2f + (teamSituation.IsEnemySplit ? 6f : 0f), 0f, 82f),
                Math.Clamp(44f + priority * 0.30f + (58f - route.Value.TotalRisk) * 0.18f, 0f, 84f),
                Math.Clamp(route.Value.TotalRisk + risk.EncirclementRisk * 0.18f, 0f, 100f),
                48f,
                destination,
                "敌方退路",
                string.Empty,
                "敌方退路",
                route,
                null,
                eta,
                ResolveActionHoldSeconds(BattlefieldActionType.WrapBehind, 0),
                ResolveActionPurpose(BattlefieldActionType.WrapBehind),
                "侧翼通行样本风险可接受，可以压敌方后撤方向",
                $"通行样本 {route.Value.RouteId}；通行风险 {route.Value.TotalRisk:0}；出路风险 {risk.RetreatRouteRisk:0}",
                ResolveActionFailureCondition(BattlefieldActionType.WrapBehind)));
        }
    }

    private static BattlefieldActionCandidateSnapshot CreateActionCandidate(
        string id,
        string commandId,
        BattlefieldActionType actionType,
        BattlefieldCommandKind commandKind,
        string scope,
        string text,
        float priority,
        float confidence,
        float risk,
        float urgency,
        Vector3 destination,
        string destinationName,
        string targetId,
        string targetName,
        BattlefieldMapTacticalRouteSnapshot? route,
        int? countdownSeconds,
        int etaSeconds,
        int holdSeconds,
        string purposeText,
        string reasonText,
        string evidenceText,
        string failureConditionText)
    {
        var routeId = route.HasValue ? route.Value.RouteId ?? string.Empty : string.Empty;
        var routeText = BuildRouteText(route, actionType);
        return new BattlefieldActionCandidateSnapshot(
            id ?? string.Empty,
            commandId ?? string.Empty,
            actionType,
            commandKind,
            scope ?? string.Empty,
            text ?? string.Empty,
            Math.Clamp(priority, 0f, 100f),
            Math.Clamp(confidence, 0f, 100f),
            Math.Clamp(risk, 0f, 100f),
            Math.Clamp(urgency, 0f, 100f),
            destination,
            destinationName ?? string.Empty,
            targetId ?? string.Empty,
            targetName ?? string.Empty,
            routeId,
            routeText,
            countdownSeconds,
            Math.Clamp(etaSeconds, 0, 600),
            Math.Clamp(holdSeconds, 3, 45),
            purposeText ?? string.Empty,
            reasonText ?? string.Empty,
            evidenceText ?? string.Empty,
            failureConditionText ?? string.Empty);
    }

    private static BattlefieldObjectivePrioritySnapshot? FindMatchingObjective(
        BattlefieldCommandSnapshot command,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities)
    {
        foreach (var objective in priorities)
        {
            if (!string.IsNullOrWhiteSpace(objective.ObjectiveId)
                && command.Id.Contains(objective.ObjectiveId, StringComparison.Ordinal))
                return objective;
            if (!string.IsNullOrWhiteSpace(objective.Name)
                && (string.Equals(command.TargetName, objective.Name, StringComparison.Ordinal)
                    || command.CommandText.Contains(objective.Name, StringComparison.Ordinal)))
                return objective;
        }

        if (IsMeaningfulPosition(command.Position))
        {
            var nearest = priorities
                .Select(objective => new { Objective = objective, Distance = Distance2D(command.Position, objective.Position) })
                .Where(item => item.Distance <= 18f)
                .OrderBy(item => item.Distance)
                .FirstOrDefault();
            if (nearest is not null)
                return nearest.Objective;
        }

        return null;
    }

    private static BattlefieldActionType ResolveActionType(
        BattlefieldCommandSnapshot command,
        BattlefieldObjectivePrioritySnapshot? objective,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (objective.HasValue)
        {
            var item = objective.Value;
            if (item.Category == BattlefieldMapObjectiveCategory.Ice
                && command.Kind is BattlefieldCommandKind.AttackObjective or BattlefieldCommandKind.Rotate or BattlefieldCommandKind.ContestObjective)
                return BattlefieldActionType.AttackIce;
            if (item.State == BattlefieldMapObjectiveState.Contested && command.Kind == BattlefieldCommandKind.ContestObjective)
                return BattlefieldActionType.InterruptTouch;
            if (IsCaptureObjective(item.Category)
                && item.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Warning
                && command.Kind is BattlefieldCommandKind.Rotate or BattlefieldCommandKind.ContestObjective or BattlefieldCommandKind.Split)
                return BattlefieldActionType.TouchObjective;
        }

        return command.Kind switch
        {
            BattlefieldCommandKind.Regroup => BattlefieldActionType.Regroup,
            BattlefieldCommandKind.Engage => BattlefieldActionType.Engage,
            BattlefieldCommandKind.Retreat => command.Id.Contains("return", StringComparison.Ordinal) ? BattlefieldActionType.ReturnToBase : BattlefieldActionType.Retreat,
            BattlefieldCommandKind.Disengage => BattlefieldActionType.Retreat,
            BattlefieldCommandKind.Rotate => BattlefieldActionType.Rotate,
            BattlefieldCommandKind.AttackObjective => objective.HasValue && objective.Value.Category == BattlefieldMapObjectiveCategory.Ice ? BattlefieldActionType.AttackIce : BattlefieldActionType.ContestObjective,
            BattlefieldCommandKind.ContestObjective => BattlefieldActionType.ContestObjective,
            BattlefieldCommandKind.DefendObjective => BattlefieldActionType.DefendObjective,
            BattlefieldCommandKind.AbandonObjective => BattlefieldActionType.AbandonObjective,
            BattlefieldCommandKind.Split => objective.HasValue && IsCaptureObjective(objective.Value.Category) ? BattlefieldActionType.TouchObjective : BattlefieldActionType.Flank,
            BattlefieldCommandKind.FocusTarget => BattlefieldActionType.FocusTarget,
            BattlefieldCommandKind.ProtectTarget => BattlefieldActionType.ProtectHighBattleHigh,
            BattlefieldCommandKind.Spread => BattlefieldActionType.Spread,
            BattlefieldCommandKind.Hold => BattlefieldActionType.Hold,
            BattlefieldCommandKind.Detour => BattlefieldActionType.Detour,
            BattlefieldCommandKind.PressureSide => command.Id.Contains("behind", StringComparison.Ordinal) || command.Id.Contains("wrap", StringComparison.Ordinal)
                ? BattlefieldActionType.WrapBehind
                : command.Id.Contains("push", StringComparison.Ordinal) ? BattlefieldActionType.BacklinePressure : BattlefieldActionType.Flank,
            BattlefieldCommandKind.Wait => BattlefieldActionType.Wait,
            _ => BattlefieldActionType.Unknown,
        };
    }

    private static BattlefieldActionType ResolveObjectiveActionType(BattlefieldObjectivePrioritySnapshot objective)
    {
        if (objective.ShouldHoldInstead)
            return BattlefieldActionType.Hold;
        if (objective.Category == BattlefieldMapObjectiveCategory.Ice)
            return BattlefieldActionType.AttackIce;
        if (objective.State == BattlefieldMapObjectiveState.Controlled)
            return BattlefieldActionType.DefendObjective;
        if (objective.State == BattlefieldMapObjectiveState.Contested)
            return BattlefieldActionType.ContestObjective;
        if (IsCaptureObjective(objective.Category) && objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Warning)
            return BattlefieldActionType.TouchObjective;
        return BattlefieldActionType.Rotate;
    }

    private static BattlefieldCommandKind ResolveCommandKind(BattlefieldActionType actionType)
        => actionType switch
        {
            BattlefieldActionType.Regroup => BattlefieldCommandKind.Regroup,
            BattlefieldActionType.Engage => BattlefieldCommandKind.Engage,
            BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase => BattlefieldCommandKind.Retreat,
            BattlefieldActionType.Rotate => BattlefieldCommandKind.Rotate,
            BattlefieldActionType.AttackIce => BattlefieldCommandKind.AttackObjective,
            BattlefieldActionType.TouchObjective or BattlefieldActionType.InterruptTouch or BattlefieldActionType.ContestObjective => BattlefieldCommandKind.ContestObjective,
            BattlefieldActionType.DefendObjective => BattlefieldCommandKind.DefendObjective,
            BattlefieldActionType.AbandonObjective => BattlefieldCommandKind.AbandonObjective,
            BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => BattlefieldCommandKind.PressureSide,
            BattlefieldActionType.FocusTarget => BattlefieldCommandKind.FocusTarget,
            BattlefieldActionType.ProtectHighBattleHigh => BattlefieldCommandKind.ProtectTarget,
            BattlefieldActionType.Spread => BattlefieldCommandKind.Spread,
            BattlefieldActionType.Detour => BattlefieldCommandKind.Detour,
            BattlefieldActionType.Hold => BattlefieldCommandKind.Hold,
            BattlefieldActionType.Wait => BattlefieldCommandKind.Wait,
            _ => BattlefieldCommandKind.Unknown,
        };

    private static Vector3 ResolveActionDestination(
        Vector3 commandPosition,
        BattlefieldObjectivePrioritySnapshot? objective,
        BattlefieldActionType actionType,
        BattlefieldTeamSituationSnapshot teamSituation,
        Vector3 anchor)
    {
        if (IsMeaningfulPosition(commandPosition))
            return commandPosition;
        if (objective.HasValue && IsMeaningfulPosition(objective.Value.Position))
            return objective.Value.Position;
        if (actionType is BattlefieldActionType.FocusTarget or BattlefieldActionType.Engage or BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure
            && teamSituation.EnemyMainGroupMovement.HasMainGroup)
            return teamSituation.EnemyMainGroupMovement.CurrentCenter;
        return anchor;
    }

    private static string ResolveActionDestinationName(
        string commandTargetName,
        BattlefieldObjectivePrioritySnapshot? objective,
        BattlefieldActionType actionType)
    {
        if (!string.IsNullOrWhiteSpace(commandTargetName))
            return commandTargetName;
        if (objective.HasValue && !string.IsNullOrWhiteSpace(objective.Value.Name))
            return objective.Value.Name;
        return actionType switch
        {
            BattlefieldActionType.Regroup => "主团推进锚点",
            BattlefieldActionType.Retreat => "脱出夹角方向",
            BattlefieldActionType.ReturnToBase => "复活会合方向",
            BattlefieldActionType.Spread => "横向展开位置",
            BattlefieldActionType.Detour => "侧压路径",
            BattlefieldActionType.Engage => "敌方主团",
            BattlefieldActionType.Flank => "敌方侧翼",
            BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => "敌方侧后",
            BattlefieldActionType.Hold => "当前压制地形",
            BattlefieldActionType.Wait => "下一资源点方向",
            _ => "未指定",
        };
    }

    private static int? ResolveActionCountdown(
        BattlefieldCommandSnapshot command,
        BattlefieldObjectivePrioritySnapshot? objective,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (objective.HasValue && objective.Value.RemainingSeconds.HasValue)
            return objective.Value.RemainingSeconds;
        if (command.Id.Contains("prepare-next", StringComparison.Ordinal) && timeSituation.NextResourceSeconds.HasValue)
            return timeSituation.NextResourceSeconds;
        return null;
    }

    private static int ResolveActionEta(
        Vector3 destination,
        Vector3 anchor,
        BattlefieldObjectivePrioritySnapshot? objective,
        BattlefieldMapTacticalRouteSnapshot? route,
        BattlefieldActionType actionType)
    {
        if (objective.HasValue && objective.Value.MountedEtaSeconds > 0)
            return objective.Value.MountedEtaSeconds;
        if (route.HasValue && route.Value.MountedEtaSeconds > 0
            && actionType is BattlefieldActionType.Rotate or BattlefieldActionType.Detour or BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase or BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind)
            return route.Value.MountedEtaSeconds;
        return IsMeaningfulPosition(destination) ? EstimateEtaSeconds(Distance2D(anchor, destination), MountedYalmsPerSecond) : 0;
    }

    private static BattlefieldMapTacticalRouteSnapshot? ResolveBestRouteForAction(
        BattlefieldActionType actionType,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        if (mapTactics.Routes.Length == 0)
            return null;

        var routes = mapTactics.Routes.Where(route => route.PointCount > 0).ToArray();
        if (routes.Length == 0)
            return null;

        var preferSafe = actionType is BattlefieldActionType.Retreat
            or BattlefieldActionType.ReturnToBase
            or BattlefieldActionType.Detour;
        var preferFastAttack = actionType is BattlefieldActionType.Rotate
            or BattlefieldActionType.TouchObjective
            or BattlefieldActionType.AttackIce
            or BattlefieldActionType.ContestObjective
            or BattlefieldActionType.InterruptTouch
            or BattlefieldActionType.Engage
            or BattlefieldActionType.Flank
            or BattlefieldActionType.WrapBehind
            or BattlefieldActionType.BacklinePressure;
        var route = preferSafe
            ? routes
                .OrderBy(item => item.CrossesMandatoryChoke)
                .ThenBy(item => item.CrossesDangerZone)
                .ThenBy(item => item.TotalRisk)
                .ThenBy(item => item.MountedEtaSeconds)
                .First()
            : preferFastAttack
                ? routes
                    .OrderBy(item => item.TotalRisk >= 88f && (item.CrossesDangerZone || item.CrossesMandatoryChoke))
                    .ThenBy(item => item.MountedEtaSeconds <= 0 ? 999 : item.MountedEtaSeconds)
                    .ThenBy(item => item.TotalRisk)
                    .First()
            : routes
                .OrderBy(item => item.MountedEtaSeconds <= 0 ? 999 : item.MountedEtaSeconds)
                .ThenBy(item => item.TotalRisk)
                .First();

        if (actionType == BattlefieldActionType.Detour && risk.ChokeBlockRisk < 45f && route.CrossesMandatoryChoke)
            return null;
        return route;
    }

    private static string BuildRouteText(BattlefieldMapTacticalRouteSnapshot? route, BattlefieldActionType actionType)
    {
        if (!route.HasValue)
        {
            return actionType switch
            {
                BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase => "脱出夹角后回头接团",
                BattlefieldActionType.Detour => "换侧线压进，不放弃目标",
                BattlefieldActionType.Flank => "沿侧翼角度推进，和主团同步夹击",
                BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => "沿侧后角度推进，压敌方退路但保留回接点",
                BattlefieldActionType.Rotate or BattlefieldActionType.TouchObjective or BattlefieldActionType.AttackIce => "按当前最低风险通行角度前往",
                _ => "无需固定路径",
            };
        }

        var value = route.Value;
        var label = !string.IsNullOrWhiteSpace(value.Recommendation)
            ? value.Recommendation
            : !string.IsNullOrWhiteSpace(value.KindSummary) ? value.KindSummary : "地图通行样本";
        return $"{label}；预计 {FormatDuration(value.MountedEtaSeconds)}；风险 {value.TotalRisk:0}";
    }

    private static string BuildActionText(BattlefieldActionType actionType, string scope, string targetName, int etaSeconds, int? countdownSeconds)
    {
        var target = string.IsNullOrWhiteSpace(targetName) ? "目标" : targetName;
        var eta = etaSeconds > 0 ? $"，预计 {FormatDuration(etaSeconds)}" : string.Empty;
        var countdown = countdownSeconds.HasValue ? $"，倒计时 {FormatDuration(countdownSeconds.Value)}" : string.Empty;
        return actionType switch
        {
            BattlefieldActionType.Rotate => $"{scope}转点 {target}{eta}{countdown}",
            BattlefieldActionType.DefendObjective => $"{scope}控点 {target}{countdown}",
            BattlefieldActionType.ContestObjective => $"{scope}抢点 {target}{eta}{countdown}",
            BattlefieldActionType.AbandonObjective => $"{scope}不硬进 {target}，侧压换击杀",
            BattlefieldActionType.AttackIce => $"{scope}打冰 {target}{eta}{countdown}",
            BattlefieldActionType.TouchObjective => $"{scope}摸点 {target}{eta}{countdown}",
            BattlefieldActionType.InterruptTouch => $"{scope}打断摸点 {target}{eta}{countdown}",
            BattlefieldActionType.Engage => $"{scope}接团 {target}{eta}",
            BattlefieldActionType.Retreat => $"{scope}脱出夹角到 {target}",
            BattlefieldActionType.ReturnToBase => $"{scope}向复活会合点靠拢",
            BattlefieldActionType.Flank => $"{scope}夹击 {target}{eta}",
            BattlefieldActionType.WrapBehind => $"{scope}绕后 {target}{eta}",
            BattlefieldActionType.BacklinePressure => $"{scope}压后排 {target}{eta}",
            BattlefieldActionType.FocusTarget => $"{scope}集火 {target}",
            BattlefieldActionType.ProtectHighBattleHigh => $"{scope}保护高战意 {target}",
            BattlefieldActionType.Regroup => $"{scope}靠拢压进 {target}",
            BattlefieldActionType.Spread => $"{scope}横向展开，继续输出",
            BattlefieldActionType.Detour => $"{scope}换侧线压 {target}",
            BattlefieldActionType.Hold => $"{scope}卡住 {target}",
            BattlefieldActionType.Wait => $"{scope}提前压向 {target}{countdown}",
            _ => $"{scope}处理 {target}",
        };
    }

    private static string ResolveActionPurpose(BattlefieldActionType actionType)
        => actionType switch
        {
            BattlefieldActionType.Rotate => "抢在刷新/归属变化前占位",
            BattlefieldActionType.DefendObjective => "控住已有分数并继续压制敌方进点",
            BattlefieldActionType.ContestObjective => "争夺目标收益，阻止敌方白拿",
            BattlefieldActionType.AbandonObjective => "只在必死埋伏时换角度找击杀收益",
            BattlefieldActionType.AttackIce => "快速转化碎冰分数",
            BattlefieldActionType.TouchObjective => "用低成本触发/抢占目标控制权",
            BattlefieldActionType.InterruptTouch => "打断敌方占领进度",
            BattlefieldActionType.Engage => "利用人数/节奏窗口正面接团",
            BattlefieldActionType.Retreat => "脱出致命包围后重新接团",
            BattlefieldActionType.ReturnToBase => "向复活节奏靠拢后重新进攻",
            BattlefieldActionType.Flank => "从侧翼制造夹击压力",
            BattlefieldActionType.WrapBehind => "压敌方退路，逼迫敌方回头或脱节",
            BattlefieldActionType.BacklinePressure => "压迫敌方后排和退路",
            BattlefieldActionType.FocusTarget => "集中火力快速造成人数差",
            BattlefieldActionType.ProtectHighBattleHigh => "保住高价值战意成员",
            BattlefieldActionType.Regroup => "把队形收回到能继续推进的位置",
            BattlefieldActionType.Spread => "降低群控/极限技收益，同时保持火力",
            BattlefieldActionType.Detour => "只在必死封路时换侧线继续打",
            BattlefieldActionType.Hold => "卡住当前地形并寻找压进窗口",
            BattlefieldActionType.Wait => "提前压向下一资源或复活会合点",
            _ => "补充战术行动",
        };

    private static string ResolveActionFailureCondition(BattlefieldActionType actionType)
        => actionType switch
        {
            BattlefieldActionType.Rotate => "目标消失/剩余时间不足/敌方主团压近/出路风险>75",
            BattlefieldActionType.DefendObjective => "敌方主团离开/据点已安全/下一资源刷新<30秒",
            BattlefieldActionType.ContestObjective => "我方人数转劣/敌极限技威胁升高/目标收益下降",
            BattlefieldActionType.AbandonObjective => "目标风险下降/敌方离开/我方复活到位",
            BattlefieldActionType.AttackIce => "冰血量被抢空/敌方大团压到/我方贡献不足",
            BattlefieldActionType.TouchObjective => "摸点被打断/敌方主团抵达/剩余时间不足",
            BattlefieldActionType.InterruptTouch => "敌方停止摸点/我方人数劣势扩大/控制技能不足",
            BattlefieldActionType.Engage => "人数差转负/敌极限技或关键技能威胁升高/队形散乱",
            BattlefieldActionType.Retreat => "安全线出现/敌方不追/我方已到位",
            BattlefieldActionType.ReturnToBase => "复活节奏恢复/目标刷新/追击压力消失",
            BattlefieldActionType.Flank => "主团脱节/侧翼路径被封/敌方回头包夹",
            BattlefieldActionType.WrapBehind => "出路变差/主团无法接应/敌方回头包夹",
            BattlefieldActionType.BacklinePressure => "后排撤走/我方出路变差/敌方反打窗口出现",
            BattlefieldActionType.FocusTarget => "目标死亡/目标开防御或无敌/集火人数不足",
            BattlefieldActionType.ProtectHighBattleHigh => "目标脱离集火/血量安全/敌方切换目标",
            BattlefieldActionType.Regroup => "跟随率恢复/人数到齐/下一目标倒计时过近",
            BattlefieldActionType.Spread => "敌方爆发窗口结束/高台威胁解除/需要重新集火",
            BattlefieldActionType.Detour => "卡口压力下降/路径风险下降/侧线预计用时过长",
            BattlefieldActionType.Hold => "下一资源刷新/敌方强压/比分压力需要主动拿分",
            BattlefieldActionType.Wait => "复活到齐/下一资源刷新/敌方开团",
            _ => "关键条件变化时重新评分",
        };

    private static int ResolveActionHoldSeconds(BattlefieldActionType actionType, int commandCooldownSeconds)
    {
        var baseline = actionType switch
        {
            BattlefieldActionType.FocusTarget => 5,
            BattlefieldActionType.ProtectHighBattleHigh => 7,
            BattlefieldActionType.TouchObjective or BattlefieldActionType.InterruptTouch => 6,
            BattlefieldActionType.Engage => 10,
            BattlefieldActionType.Rotate or BattlefieldActionType.AttackIce or BattlefieldActionType.ContestObjective => 16,
            BattlefieldActionType.DefendObjective or BattlefieldActionType.Hold => 14,
            BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase or BattlefieldActionType.Detour => 14,
            BattlefieldActionType.Regroup => 12,
            BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => 14,
            BattlefieldActionType.Spread => 6,
            BattlefieldActionType.Wait => 10,
            _ => 8,
        };
        return Math.Max(baseline, commandCooldownSeconds);
    }

    private static float ResolveActionConfidence(
        BattlefieldCommandSnapshot command,
        BattlefieldObjectivePrioritySnapshot? objective,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldActionType actionType)
    {
        var score = 32f + command.Score * 0.30f + command.Urgency * 0.24f - risk.OverallRisk * 0.08f;
        if (objective.HasValue)
            score += objective.Value.PriorityScore * 0.16f + (100f - objective.Value.RiskScore) * 0.08f;
        if (actionType is BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase && IsFatalRisk(risk))
            score += 8f;
        if (actionType is BattlefieldActionType.Spread && (risk.LimitBreakRisk >= 84f || risk.SkillThreatRisk >= 88f))
            score += 4f;
        return Math.Clamp(score, 0f, 100f);
    }

    private static BattlefieldActionCandidateSnapshot ApplyInputReliabilityToActionCandidate(
        BattlefieldActionCandidateSnapshot candidate,
        BattlefieldInputReliabilitySnapshot inputReliability)
    {
        if (!inputReliability.IsAvailable)
            return candidate;

        var relevantReliability = ResolveActionInputReliability(candidate.ActionType, inputReliability);
        var scale = Math.Clamp(0.52f + relevantReliability * 0.0048f, 0.52f, 1.0f);
        var adjustedConfidence = Math.Clamp(candidate.Confidence * scale, 0f, 100f);
        var evidence = string.IsNullOrWhiteSpace(candidate.EvidenceText)
            ? $"输入可靠 {relevantReliability:0}，最终置信 {adjustedConfidence:0}"
            : $"{candidate.EvidenceText}；输入可靠 {relevantReliability:0}，最终置信 {adjustedConfidence:0}";
        return candidate with
        {
            Confidence = adjustedConfidence,
            EvidenceText = evidence
        };
    }

    private static float ResolveActionInputReliability(
        BattlefieldActionType actionType,
        BattlefieldInputReliabilitySnapshot inputReliability)
        => actionType switch
        {
            BattlefieldActionType.Rotate
                or BattlefieldActionType.DefendObjective
                or BattlefieldActionType.ContestObjective
                or BattlefieldActionType.TouchObjective
                or BattlefieldActionType.InterruptTouch
                or BattlefieldActionType.AttackIce
                    => WeightedReliability(inputReliability.OverallReliability, inputReliability.ObjectiveReliability, inputReliability.TimeReliability, inputReliability.MapTacticsReliability),
            BattlefieldActionType.Engage
                or BattlefieldActionType.FocusTarget
                or BattlefieldActionType.Flank
                or BattlefieldActionType.WrapBehind
                or BattlefieldActionType.BacklinePressure
                    => WeightedReliability(inputReliability.OverallReliability, inputReliability.PlayerReliability, inputReliability.CombatEventReliability),
            BattlefieldActionType.Retreat
                or BattlefieldActionType.ReturnToBase
                or BattlefieldActionType.Spread
                or BattlefieldActionType.Detour
                    => WeightedReliability(inputReliability.OverallReliability, inputReliability.PlayerReliability, inputReliability.MapTacticsReliability),
            _ => inputReliability.OverallReliability,
        };

    private static float WeightedReliability(params float[] values)
        => values.Length == 0 ? 0f : Math.Clamp(values.Average(), 0f, 100f);

    private static float ResolveActionRisk(BattlefieldActionType actionType, BattlefieldRiskAssessmentSnapshot risk)
        => actionType switch
        {
            BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase => MathF.Max(risk.OverallRisk, risk.EncirclementRisk),
            BattlefieldActionType.Detour => MathF.Max(risk.ChokeBlockRisk, risk.TerrainRisk),
            BattlefieldActionType.Spread => MathF.Max(risk.LimitBreakRisk, risk.SkillThreatRisk),
            BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => MathF.Max(risk.FlankRisk, risk.RetreatRouteRisk),
            BattlefieldActionType.ProtectHighBattleHigh => risk.BattleHighRisk,
            BattlefieldActionType.FocusTarget => risk.SkillThreatRisk,
            BattlefieldActionType.Engage => risk.CombatRisk,
            _ => risk.OverallRisk,
        };

    private static float ResolveActionSortPriority(BattlefieldActionCandidateSnapshot candidate)
    {
        var actionBonus = candidate.ActionType switch
        {
            BattlefieldActionType.Retreat or BattlefieldActionType.ReturnToBase => -10f,
            BattlefieldActionType.AbandonObjective => -12f,
            BattlefieldActionType.Detour => -8f,
            BattlefieldActionType.Wait => -10f,
            BattlefieldActionType.Regroup => -4f,
            BattlefieldActionType.Spread => -3f,
            BattlefieldActionType.Engage => 7f,
            BattlefieldActionType.ProtectHighBattleHigh => 5f,
            BattlefieldActionType.InterruptTouch => 14f,
            BattlefieldActionType.FocusTarget => 3f,
            BattlefieldActionType.TouchObjective or BattlefieldActionType.AttackIce or BattlefieldActionType.ContestObjective => 16f,
            BattlefieldActionType.DefendObjective => 12f,
            BattlefieldActionType.Rotate => 11f,
            BattlefieldActionType.Flank or BattlefieldActionType.WrapBehind or BattlefieldActionType.BacklinePressure => 9f,
            _ => 0f,
        };
        return Math.Clamp(
            candidate.Priority * 0.46f
            + candidate.Urgency * 0.30f
            + candidate.Confidence * 0.24f
            + actionBonus
            + ResolveCommandIdPriorityBonus(candidate.CommandId),
            0f,
            100f);
    }

    private static string BuildActionDedupeKey(BattlefieldActionCandidateSnapshot candidate)
    {
        var target = !string.IsNullOrWhiteSpace(candidate.TargetId)
            ? candidate.TargetId
            : !string.IsNullOrWhiteSpace(candidate.TargetName)
                ? candidate.TargetName
                : candidate.DestinationName ?? string.Empty;
        return $"{candidate.ActionType}:{candidate.Scope}:{target}".Trim();
    }

    private static string ResolveCommandTargetId(BattlefieldCommandSnapshot command)
    {
        var lastColon = command.Id.LastIndexOf(':');
        if (lastColon >= 0 && lastColon < command.Id.Length - 1)
            return command.Id[(lastColon + 1)..];
        return string.Empty;
    }

    private static bool IsCaptureObjective(BattlefieldMapObjectiveCategory category)
        => category is BattlefieldMapObjectiveCategory.Base
            or BattlefieldMapObjectiveCategory.Tomelith
            or BattlefieldMapObjectiveCategory.Ovoo
            or BattlefieldMapObjectiveCategory.StrategicPoint;

    private static byte? OwnershipToBattalion(NodeOwnership? ownership)
        => ownership switch
        {
            NodeOwnership.Maelstrom => 0,
            NodeOwnership.TwinAdder => 1,
            NodeOwnership.ImmortalFlames => 2,
            _ => null,
        };

    private static Vector3? ResolveAllianceCenter(BattlefieldAllianceSituationSnapshot? alliance)
    {
        if (alliance is null)
            return null;

        if (alliance.MainPlayerCluster.HasValue && IsMeaningfulPosition(alliance.MainPlayerCluster.Value.Center))
            return alliance.MainPlayerCluster.Value.Center;
        if (alliance.MainMapVisionCluster.HasValue && IsMeaningfulPosition(alliance.MainMapVisionCluster.Value.Center))
            return alliance.MainMapVisionCluster.Value.Center;

        return null;
    }

    private static void AddEnemyAnchor(List<ObjectiveEnemyAnchor> anchors, BattlefieldAllianceSituationSnapshot? alliance)
    {
        var center = ResolveAllianceCenter(alliance);
        if (!center.HasValue || !IsMeaningfulPosition(center.Value))
            return;

        var weight = Math.Max(
            Math.Max(alliance?.AliveCount ?? 0, alliance?.MainPlayerCluster?.PlayerCount ?? 0),
            alliance?.MainMapVisionCluster?.PointCount ?? 0);
        anchors.Add(new ObjectiveEnemyAnchor(center.Value, Math.Max(1, weight)));
    }

    private static float DistancePointToSegment2D(Vector3 point, Vector3 start, Vector3 end, out float along)
    {
        var abX = end.X - start.X;
        var abZ = end.Z - start.Z;
        var lengthSquared = abX * abX + abZ * abZ;
        if (lengthSquared <= 0.001f)
        {
            along = 0f;
            return Distance2D(point, start);
        }

        var apX = point.X - start.X;
        var apZ = point.Z - start.Z;
        along = Math.Clamp((apX * abX + apZ * abZ) / lengthSquared, 0f, 1f);
        var projected = new Vector3(start.X + abX * along, point.Y, start.Z + abZ * along);
        return Distance2D(point, projected);
    }

    private static bool IsMeaningfulPosition(Vector3 position)
        => MathF.Abs(position.X) + MathF.Abs(position.Y) + MathF.Abs(position.Z) > 0.1f;

    private BattlefieldCommandPublishSnapshot ResolveCommandPublish(
        IReadOnlyList<BattlefieldCommandSnapshot> ordered,
        BattlefieldActionCandidateSnapshot? selectedAction,
        BattlefieldInputReliabilitySnapshot inputReliability)
    {
        var now = Environment.TickCount64;
        PruneCommandIssueHistory(now);
        if (ordered.Count == 0)
        {
            return new BattlefieldCommandPublishSnapshot
            {
                LastIssuedAgeMs = lastIssuedCommand.HasValue ? Math.Max(0, now - lastIssuedCommand.Value.IssuedAtTicks) : -1,
                Sequence = commandIssueSequence,
            };
        }

        var command = ordered[0];
        var commandPriority = ResolveCommandPriority(command);
        var priorityText = ResolveCommandPriorityText(command);
        var familyKey = BuildCommandFamilyKey(command);
        var commandCooldownMs = command.CooldownSeconds * 1000L;
        var kindCooldownMs = ResolveKindCooldownSeconds(command) * 1000L;
        var globalCooldownMs = ResolveGlobalCooldownSeconds(command) * 1000L;
        var commandRemaining = ResolveRemainingCooldown(commandIssueStateById.TryGetValue(command.Id, out var commandState) ? commandState.IssuedAtTicks : 0, commandCooldownMs, now);
        var familyRemaining = ResolveRemainingCooldown(commandIssueStateByFamily.TryGetValue(familyKey, out var familyState) ? familyState.IssuedAtTicks : 0, kindCooldownMs, now);
        var globalRemaining = ResolveRemainingCooldown(lastIssuedCommand?.IssuedAtTicks ?? 0, globalCooldownMs, now);
        var lastAge = lastIssuedCommand.HasValue ? Math.Max(0, now - lastIssuedCommand.Value.IssuedAtTicks) : -1;
        var canInterrupt = CanInterruptCooldown(command, commandPriority, now);
        var reliabilitySuppression = ResolveReliabilitySuppression(command, selectedAction, inputReliability);

        var suppression = string.Empty;
        if (!string.IsNullOrWhiteSpace(reliabilitySuppression))
            suppression = reliabilitySuppression;
        else if (commandRemaining > 0)
            suppression = $"同一句冷却 {FormatSecondsCeiling(commandRemaining)}秒";
        else if (familyRemaining > 0)
            suppression = $"同类指令冷却 {FormatSecondsCeiling(familyRemaining)}秒";
        else if (globalRemaining > 0 && !canInterrupt)
            suppression = $"全局防刷屏 {FormatSecondsCeiling(globalRemaining)}秒";

        if (!string.IsNullOrWhiteSpace(suppression))
        {
            return new BattlefieldCommandPublishSnapshot
            {
                ShouldAnnounce = false,
                IsSuppressed = true,
                Command = command,
                SpeakText = command.CommandText,
                PriorityText = priorityText,
                StatusText = $"压制：{suppression}",
                SuppressionReason = suppression,
                GlobalCooldownRemainingSeconds = FormatSecondsCeiling(globalRemaining),
                CommandCooldownRemainingSeconds = FormatSecondsCeiling(commandRemaining),
                KindCooldownRemainingSeconds = FormatSecondsCeiling(familyRemaining),
                LastIssuedAgeMs = lastAge,
                Sequence = commandIssueSequence,
            };
        }

        commandIssueSequence++;
        var issueState = new CommandIssueState(
            command.Id,
            familyKey,
            command.Kind,
            command.Scope,
            command.CommandText,
            commandPriority,
            now,
            commandIssueSequence);
        commandIssueStateById[command.Id] = issueState;
        commandIssueStateByFamily[familyKey] = issueState;
        lastIssuedCommand = issueState;

        return new BattlefieldCommandPublishSnapshot
        {
            ShouldAnnounce = true,
            InterruptedCooldown = canInterrupt && globalRemaining > 0,
            Command = command,
            SpeakText = command.CommandText,
            PriorityText = priorityText,
            StatusText = canInterrupt && globalRemaining > 0 ? $"可喊：{priorityText}，打断冷却" : $"可喊：{priorityText}",
            GlobalCooldownRemainingSeconds = 0,
            CommandCooldownRemainingSeconds = 0,
            KindCooldownRemainingSeconds = 0,
            LastIssuedAgeMs = 0,
            Sequence = commandIssueSequence,
        };
    }

    private void PruneCommandIssueHistory(long now)
    {
        var expiryMs = CommandHistoryExpirySeconds * 1000L;
        foreach (var key in commandIssueStateById.Keys.ToArray())
        {
            if (now - commandIssueStateById[key].IssuedAtTicks > expiryMs)
                commandIssueStateById.Remove(key);
        }

        foreach (var key in commandIssueStateByFamily.Keys.ToArray())
        {
            if (now - commandIssueStateByFamily[key].IssuedAtTicks > expiryMs)
                commandIssueStateByFamily.Remove(key);
        }

        if (lastIssuedCommand.HasValue && now - lastIssuedCommand.Value.IssuedAtTicks > expiryMs)
            lastIssuedCommand = null;
    }

    private static string ResolveReliabilitySuppression(
        BattlefieldCommandSnapshot command,
        BattlefieldActionCandidateSnapshot? selectedAction,
        BattlefieldInputReliabilitySnapshot inputReliability)
    {
        if (!inputReliability.IsAvailable)
            return string.Empty;
        if (!inputReliability.CanPublish)
            return inputReliability.GateText;
        if (!selectedAction.HasValue)
            return string.Empty;

        var action = selectedAction.Value;
        if (action.Confidence < inputReliability.PublishActionConfidenceThreshold)
            return $"低置信只提示：行动置信 {action.Confidence:0} 低于 {inputReliability.PublishActionConfidenceThreshold:0}";

        if (!string.IsNullOrWhiteSpace(action.CommandId)
            && !string.Equals(action.CommandId, command.Id, StringComparison.Ordinal))
            return string.Empty;

        return string.Empty;
    }

    private bool CanInterruptCooldown(BattlefieldCommandSnapshot command, float commandPriority, long now)
    {
        if (!lastIssuedCommand.HasValue)
            return false;

        var last = lastIssuedCommand.Value;
        if (now - last.IssuedAtTicks < 900)
            return false;
        if (command.Id == last.CommandId)
            return false;

        var emergency = command.Kind is BattlefieldCommandKind.Retreat or BattlefieldCommandKind.Disengage
            || command.Urgency >= 88f
            || commandPriority >= 88f;
        return emergency && commandPriority >= last.Priority + 7f;
    }

    private static float ResolveCommandPriority(BattlefieldCommandSnapshot command)
        => Math.Clamp(
            command.Score * 0.46f
            + command.Urgency * 0.54f
            + ResolveKindPriorityBonus(command.Kind)
            + ResolveCommandIdPriorityBonus(command.Id),
            0f,
            100f);

    private static float ResolveKindPriorityBonus(BattlefieldCommandKind kind)
        => kind switch
        {
            BattlefieldCommandKind.Engage => 7f,
            BattlefieldCommandKind.FocusTarget => 3f,
            BattlefieldCommandKind.Retreat => -10f,
            BattlefieldCommandKind.Disengage => -8f,
            BattlefieldCommandKind.ProtectTarget => 5f,
            BattlefieldCommandKind.ContestObjective => 16f,
            BattlefieldCommandKind.AttackObjective => 14f,
            BattlefieldCommandKind.DefendObjective => 12f,
            BattlefieldCommandKind.PressureSide => 9f,
            BattlefieldCommandKind.Split => 6f,
            BattlefieldCommandKind.Rotate => 11f,
            BattlefieldCommandKind.AbandonObjective => -12f,
            BattlefieldCommandKind.Detour => -8f,
            BattlefieldCommandKind.Wait => -8f,
            BattlefieldCommandKind.Spread => -3f,
            BattlefieldCommandKind.Regroup => -2f,
            _ => 0f,
        };

    private static float ResolveCommandIdPriorityBonus(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return 0f;
        if (id.StartsWith("fight:decision:kite", StringComparison.Ordinal))
            return 10f;
        if (id.StartsWith("fight:decision", StringComparison.Ordinal))
            return 4f;
        if (id.StartsWith("micro:focus-countdown", StringComparison.Ordinal))
            return 4f;
        if (id.StartsWith("micro:group-countdown", StringComparison.Ordinal))
            return 12f;
        if (id.StartsWith("micro:fight-method", StringComparison.Ordinal))
            return 5f;
        if (id.StartsWith("objective:primary", StringComparison.Ordinal))
            return 16f;
        if (id.StartsWith("objective:hold", StringComparison.Ordinal))
            return 10f;
        if (id.StartsWith("objective:split", StringComparison.Ordinal))
            return 8f;
        if (id.StartsWith("doctrine:aoe-countdown", StringComparison.Ordinal))
            return 16f;
        if (id.StartsWith("doctrine:return-reset", StringComparison.Ordinal)
            || id.StartsWith("doctrine:interrupt-touch", StringComparison.Ordinal)
            || id.StartsWith("doctrine:big-ice-all-in", StringComparison.Ordinal))
            return 14f;
        if (id.StartsWith("doctrine:spread-precast", StringComparison.Ordinal)
            || id.StartsWith("doctrine:feint-bait", StringComparison.Ordinal)
            || id.StartsWith("doctrine:hit-and-run", StringComparison.Ordinal)
            || id.StartsWith("doctrine:losing-kill-all-in", StringComparison.Ordinal))
            return 10f;
        if (id.StartsWith("doctrine:cover-touch", StringComparison.Ordinal)
            || id.StartsWith("doctrine:single-target", StringComparison.Ordinal)
            || id.StartsWith("doctrine:split-scout", StringComparison.Ordinal))
            return 8f;
        if (id.StartsWith("engage:", StringComparison.Ordinal))
            return 10f;
        if (id.StartsWith("tempo:early-battle-high-farm", StringComparison.Ordinal)
            || id.StartsWith("tempo:early-pick-window", StringComparison.Ordinal))
            return 12f;
        if (id.StartsWith("tempo:third-place-score-first", StringComparison.Ordinal)
            || id.StartsWith("tempo:kill-win-now", StringComparison.Ordinal))
            return 18f;
        if (id.StartsWith("tempo:stop-farming-score", StringComparison.Ordinal)
            || id.StartsWith("tempo:leader-exit-crossfire", StringComparison.Ordinal)
            || id.StartsWith("tempo:stop-after-profit", StringComparison.Ordinal))
            return 14f;
        if (id.StartsWith("tempo:opening-probe", StringComparison.Ordinal)
            || id.StartsWith("tempo:leave-expiring", StringComparison.Ordinal))
            return 10f;
        if (id.StartsWith("announcement:objective", StringComparison.Ordinal))
            return 12f;
        if (id.StartsWith("announcement:weather", StringComparison.Ordinal))
            return 8f;
        if (id.StartsWith("chat-objective:", StringComparison.Ordinal))
            return 10f;
        if (id.StartsWith("field-marker:", StringComparison.Ordinal))
            return 6f;
        if (id.StartsWith("snow-blessing:", StringComparison.Ordinal))
            return 7f;
        if (id.StartsWith("target:marked:", StringComparison.Ordinal)
            || id.StartsWith("target:focus:", StringComparison.Ordinal)
            || id.StartsWith("target:control-skill:", StringComparison.Ordinal))
            return 8f;
        return 0f;
    }

    private static string ResolveCommandPriorityText(BattlefieldCommandSnapshot command)
    {
        var priority = ResolveCommandPriority(command);
        return priority >= 88f ? "最高" : priority >= 74f ? "高" : priority >= 58f ? "中" : "低";
    }

    private static int ResolveGlobalCooldownSeconds(BattlefieldCommandSnapshot command)
        => command.Kind is BattlefieldCommandKind.Retreat or BattlefieldCommandKind.Disengage || command.Urgency >= 86f
            ? EmergencyGlobalCommandCooldownSeconds
            : DefaultGlobalCommandCooldownSeconds;

    private static int ResolveKindCooldownSeconds(BattlefieldCommandSnapshot command)
        => command.Kind switch
        {
            BattlefieldCommandKind.FocusTarget => Math.Max(4, command.CooldownSeconds),
            BattlefieldCommandKind.Engage => Math.Max(6, command.CooldownSeconds),
            BattlefieldCommandKind.ProtectTarget => Math.Max(5, command.CooldownSeconds),
            BattlefieldCommandKind.Retreat => Math.Max(7, command.CooldownSeconds),
            BattlefieldCommandKind.Disengage => Math.Max(7, command.CooldownSeconds),
            BattlefieldCommandKind.Spread => Math.Max(6, command.CooldownSeconds),
            BattlefieldCommandKind.Regroup => Math.Max(9, command.CooldownSeconds),
            BattlefieldCommandKind.Rotate => Math.Max(10, command.CooldownSeconds),
            BattlefieldCommandKind.AttackObjective => Math.Max(10, command.CooldownSeconds),
            BattlefieldCommandKind.ContestObjective => Math.Max(8, command.CooldownSeconds),
            BattlefieldCommandKind.DefendObjective => Math.Max(10, command.CooldownSeconds),
            BattlefieldCommandKind.AbandonObjective => Math.Max(10, command.CooldownSeconds),
            BattlefieldCommandKind.Split => Math.Max(14, command.CooldownSeconds),
            BattlefieldCommandKind.Detour => Math.Max(9, command.CooldownSeconds),
            BattlefieldCommandKind.Hold => Math.Max(12, command.CooldownSeconds),
            _ => Math.Max(8, command.CooldownSeconds),
        };

    private static string BuildCommandFamilyKey(BattlefieldCommandSnapshot command)
    {
        var target = command.Kind is BattlefieldCommandKind.FocusTarget
            or BattlefieldCommandKind.ProtectTarget
            or BattlefieldCommandKind.AttackObjective
            or BattlefieldCommandKind.ContestObjective
            or BattlefieldCommandKind.DefendObjective
            or BattlefieldCommandKind.AbandonObjective
            or BattlefieldCommandKind.Rotate
                ? command.TargetName
                : string.Empty;
        return $"{command.Kind}:{command.Scope}:{target}".Trim();
    }

    private static long ResolveRemainingCooldown(long issuedAtTicks, long cooldownMs, long now)
    {
        if (issuedAtTicks <= 0 || cooldownMs <= 0)
            return 0;

        return Math.Max(0, cooldownMs - Math.Max(0, now - issuedAtTicks));
    }

    private static int FormatSecondsCeiling(long milliseconds)
        => milliseconds <= 0 ? 0 : (int)Math.Ceiling(milliseconds / 1000.0);

    private static void AddEmergencyCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents)
    {
        var fatal = IsFatalFightState(risk, teamSituation);
        if (fatal && risk.ThirdPartyPincerRisk >= 84f)
        {
            AddCommand(
                commands,
                "emergency:pincer",
                BattlefieldCommandKind.Retreat,
                "主团",
                "第三方夹了，先撤出夹角，出角后回头反打",
                Math.Clamp(50f + risk.ThirdPartyPincerRisk * 0.42f + risk.EncirclementRisk * 0.22f, 0f, 100f),
                Math.Clamp(76f + risk.ThirdPartyPincerRisk * 0.20f, 0f, 100f),
                8,
                anchor,
                "第三方夹击",
                "两队敌方从不同方向靠近",
                $"第三方 {risk.ThirdPartyPincerRisk:0}；被包 {risk.EncirclementRisk:0}；夹击 {risk.FlankRisk:0}");
        }

        if (fatal && risk.AmbushRisk >= 88f && risk.LimitBreakRisk >= 78f)
        {
            AddCommand(
                commands,
                "emergency:ambush",
                BattlefieldCommandKind.Disengage,
                "主团",
                "疑似满爆发埋伏，收住深追，清侧翼后继续打",
                Math.Clamp(48f + risk.AmbushRisk * 0.42f + risk.LimitBreakRisk * 0.18f, 0f, 100f),
                Math.Clamp(70f + risk.AmbushRisk * 0.22f, 0f, 100f),
                7,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor,
                "诱追",
                "敌方后撤且疑似大团高战意满爆发设伏",
                $"追击陷阱 {risk.AmbushRisk:0}；出路 {risk.RetreatRouteRisk:0}；敌分兵 {teamSituation.IsEnemySplit}");
        }

        if (fatal && (risk.OverallRisk >= 90f || risk.EncirclementRisk >= 86f) && !engagement.CanTakeFight)
        {
            AddCommand(
                commands,
                "emergency:retreat",
                BattlefieldCommandKind.Retreat,
                "主团",
                "战力崩盘，脱出包围，复活到位后回头打",
                Math.Clamp(MathF.Max(risk.OverallRisk, risk.EncirclementRisk) + 8f, 0f, 100f),
                96f,
                6,
                anchor,
                "出路",
                "总体/被包风险过高",
                $"总体 {risk.OverallRisk:0}；被包 {risk.EncirclementRisk:0}；出路 {risk.RetreatRouteRisk:0}");
        }

        if (risk.HighGroundDropRisk >= 82f
            || risk.SkillThreatRisk >= 92f
            || risk.LimitBreakRisk >= 92f
            || ((risk.HighGroundDropRisk >= 76f || risk.SkillThreatRisk >= 88f || risk.LimitBreakRisk >= 88f) && !engagement.ShouldCounterEngage))
        {
            AddCommand(
                commands,
                "emergency:spread",
                BattlefieldCommandKind.Spread,
                "主团",
                "横向展开防爆发，持续输出，技能交完反打",
                Math.Clamp(50f + MathF.Max(risk.HighGroundDropRisk, MathF.Max(risk.SkillThreatRisk, risk.LimitBreakRisk)) * 0.50f, 0f, 100f),
                Math.Clamp(64f + MathF.Max(risk.SkillThreatRisk, risk.LimitBreakRisk) * 0.28f, 0f, 100f),
                6,
                anchor,
                "范围伤害/极限技",
                "高台空降、关键技能或极限技窗口",
                $"高台 {risk.HighGroundDropRisk:0}；技能 {risk.SkillThreatRisk:0}；极限技 {risk.LimitBreakRisk:0}");
        }
    }

    private static void AddFormationCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement)
    {
        if ((risk.CohesionRisk >= 82f || teamSituation.AdvancedTactics.FriendlyCohesionScore is > 0f and < 34f)
            && !engagement.ShouldCounterEngage)
        {
            AddCommand(
                commands,
                "formation:regroup",
                BattlefieldCommandKind.Regroup,
                "全体",
                "落单人员回主团，主力继续压，不停步等人",
                Math.Clamp(38f + risk.CohesionRisk * 0.46f + risk.NumberDisadvantageRisk * 0.10f, 0f, 88f),
                Math.Clamp(40f + risk.CohesionRisk * 0.30f, 0f, 78f),
                9,
                anchor,
                "集合",
                "跟随率/方向一致性偏低，落单不拖慢主力推进",
                $"跟随 {teamSituation.AdvancedTactics.FriendlyFollowRate:0}%；凝聚 {teamSituation.AdvancedTactics.FriendlyCohesionScore:0}；散乱 {risk.CohesionRisk:0}");
        }

        if (teamSituation.RespawnRhythm.FriendlyDeadNow >= 7 && !engagement.HasBurstWindow && risk.NumberDisadvantageRisk >= 86f)
        {
            AddCommand(
                commands,
                "formation:wait-respawn",
                BattlefieldCommandKind.Wait,
                "主团",
                "少人太多，往下一目标慢压，复活到位直接打",
                Math.Clamp(42f + teamSituation.RespawnRhythm.FriendlyDeadNow * 6f + risk.NumberDisadvantageRisk * 0.12f, 0f, 88f),
                64f,
                10,
                anchor,
                "复活节奏",
                "我方死亡人数过多",
                $"我方死亡 {teamSituation.RespawnRhythm.FriendlyDeadNow}；近期倒地 {teamSituation.RespawnRhythm.FriendlyRecentlyDied}");
        }
    }

    private static void AddTravelFollowCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        if (IsFatalFightState(risk, teamSituation))
            return;

        var hasObjectiveTravel = primary.HasValue && primary.Value.MountedEtaSeconds >= 8;
        var resourceSoon = timeSituation.NextResourceSeconds is > 0 and <= 45;
        var followWeak = teamSituation.AdvancedTactics.FriendlyFollowRate is > 0f and < 78f
            || teamSituation.AdvancedTactics.FriendlyCohesionScore is > 0f and < 58f;
        if (!hasObjectiveTravel && !resourceSoon && !followWeak)
            return;

        var destination = primary.HasValue && IsMeaningfulPosition(primary.Value.Position)
            ? primary.Value.Position
            : anchor;
        var target = primary.HasValue
            ? primary.Value.Name
            : !string.IsNullOrWhiteSpace(timeSituation.NextResourceName) ? timeSituation.NextResourceName : "下一目标";
        var etaText = primary.HasValue && primary.Value.MountedEtaSeconds > 0
            ? $"，预计 {FormatDuration(primary.Value.MountedEtaSeconds)}"
            : string.Empty;
        var score = Math.Clamp(
            56f
            + (hasObjectiveTravel ? 10f : 0f)
            + (resourceSoon ? 10f : 0f)
            + Math.Max(0f, 78f - teamSituation.AdvancedTactics.FriendlyFollowRate) * 0.18f,
            0f,
            88f);
        var urgency = Math.Clamp(
            52f
            + (resourceSoon ? 14f : 0f)
            + Math.Max(0f, 58f - teamSituation.AdvancedTactics.FriendlyCohesionScore) * 0.20f,
            0f,
            86f);

        AddCommand(
            commands,
            $"travel:follow:{target}",
            BattlefieldCommandKind.Rotate,
            "全体",
            $"主团跟我赶路，骑上向 {target} 带位{etaText}，别掉队",
            score,
            urgency,
            8,
            destination,
            target,
            "转点要带队友走，主力不断线",
            $"跟随 {teamSituation.AdvancedTactics.FriendlyFollowRate:0}%；凝聚 {teamSituation.AdvancedTactics.FriendlyCohesionScore:0}；下一资源 {FormatOptionalSeconds(timeSituation.NextResourceSeconds)}");
    }

    private static void AddObjectiveCommands(
        List<BattlefieldCommandSnapshot> commands,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        StrategicFightPlan fightPlan)
    {
        if (!primary.HasValue)
        {
            if (timeSituation.NextResourceSeconds.HasValue && timeSituation.NextResourceSeconds.Value <= 45)
            {
                var forceWindow = timeSituation.NextResourceSeconds.Value <= 10;
                AddCommand(
                    commands,
                    "objective:prepare-next",
                    BattlefieldCommandKind.Rotate,
                    "主团",
                    forceWindow
                        ? $"资源快刷，立刻压 {timeSituation.NextResourceName}"
                        : $"提前站下一波 {timeSituation.NextResourceName}",
                    forceWindow ? 88f : 62f,
                    forceWindow ? 92f : 58f,
                    12,
                    Vector3.Zero,
                    timeSituation.NextResourceName,
                    forceWindow ? "资源刷新前10秒，强制提前压位" : "下一资源即将刷新",
                    $"下一资源 {FormatDuration(timeSituation.NextResourceSeconds.Value)}；来源 {timeSituation.NextResourceSource}");
            }

            return;
        }

        var item = primary.Value;
        if (IsFinalMinuteAllIn(timeSituation))
        {
            var highest = priorities
                .Where(IsHighValueObjective)
                .OrderByDescending(objective => objective.PriorityScore + objective.RewardScore)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(highest.ObjectiveId))
                item = highest;
        }
        var scorePressure = ResolveScorePressure(scoreSituation);
        var highValue = IsHighValueObjective(item);
        var refreshNow = item.RemainingSeconds is >= 0 and <= 10 || timeSituation.NextResourceSeconds is >= 0 and <= 10;
        var endgameAllIn = IsEndgameAllIn(timeSituation);
        var finalMinuteAllIn = IsFinalMinuteAllIn(timeSituation);
        var urgency = Math.Clamp(
            item.PriorityScore * 0.50f
            + item.TimingScore * 0.25f
            + scorePressure * 0.25f
            + (highValue ? 16f : 0f)
            + (refreshNow ? 18f : 0f)
            + (endgameAllIn ? 10f : 0f)
            + (finalMinuteAllIn ? 12f : 0f),
            0f,
            100f);
        var targetText = string.IsNullOrWhiteSpace(item.Name) ? CategoryText(item.Category) : item.Name;
        var strategicTargetText = fightPlan.IsAvailable ? fightPlan.TargetName : "高分家";
        var etaText = FormatDuration(item.MountedEtaSeconds);
        var fatalObjective = IsFatalRisk(risk) && (item.RiskScore >= 92f || risk.EncirclementRisk >= 88f) && !endgameAllIn;
        var shouldHold = ShouldHoldObjectivePosition(item, timeSituation);
        if (ShouldLetThirdPlaceTakeObjective(item, scoreSituation, timeSituation, fightPlan, out var thirdName, out var letThirdReason))
        {
            AddCommand(
                commands,
                $"objective:let-third-score:{item.ObjectiveId}",
                BattlefieldCommandKind.PressureSide,
                "主团",
                $"这个小点让老三 {thirdName} 补分，主团跟我压 {strategicTargetText}",
                Math.Clamp(58f + scorePressure * 0.18f + fightPlan.Urgency * 0.20f, 0f, 88f),
                Math.Clamp(54f + scorePressure * 0.14f, 0f, 82f),
                10,
                item.Position,
                thirdName,
                "低价值点用于平衡比分，不让第一名舒服领跑",
                $"{letThirdReason}；{item.EvidenceText}");
            return;
        }

        if (shouldHold)
        {
            AddCommand(
                commands,
                $"objective:hold:{item.ObjectiveId}",
                BattlefieldCommandKind.Hold,
                "主团",
                $"当前没安全点可拿，主团先挂自家侧观察，别硬进 {targetText}",
                Math.Clamp(44f + item.PriorityScore * 0.22f + item.CrossfirePenalty * 0.30f + item.RouteBlockPenalty * 0.22f + item.LongTravelPenalty * 0.18f, 0f, 88f),
                Math.Clamp(40f + item.RiskScore * 0.18f + item.CrossfirePenalty * 0.26f + item.LongTravelPenalty * 0.20f, 0f, 84f),
                8,
                item.Position,
                "挂边观察",
                "这个点位太远、太深或太容易被挡团/两家夹，允许先挂边等机会，不强行带主团送进死位",
                item.EvidenceText);
            return;
        }

        if (fatalObjective
            && !fightPlan.Endgame
            && !fightPlan.MustAttackLeader)
        {
            AddCommand(
                commands,
                $"objective:side-pressure:{item.ObjectiveId}",
                BattlefieldCommandKind.PressureSide,
                "主团",
                $"不从正面硬进 {targetText}，换角度打 {strategicTargetText}",
                Math.Clamp(46f + item.RiskScore * 0.22f + risk.OverallRisk * 0.12f, 0f, 88f),
                Math.Clamp(50f + item.RiskScore * 0.18f, 0f, 86f),
                10,
                item.Position,
                targetText,
                "目标正面是致命埋伏，改成侧压找击杀",
                item.EvidenceText);
        }

        if ((item.PriorityScore >= 58f || highValue || refreshNow || endgameAllIn) && !fatalObjective)
        {
            var commandText = item.State switch
            {
                BattlefieldMapObjectiveState.Warning => highValue || refreshNow
                    ? $"高价值快刷，主团立刻压 {targetText}，预计 {etaText}"
                    : $"主团提前压 {targetText}，预计 {etaText}",
                BattlefieldMapObjectiveState.Contested => highValue
                    ? $"高价值 {targetText} 必抢，主团直接压进去反抢"
                    : $"主团压 {targetText}，直接反抢",
                BattlefieldMapObjectiveState.Controlled => $"主团控 {targetText}，向外压第一名",
                _ => highValue
                    ? $"高价值 {targetText} 必抢，主团硬冲占点，预计 {etaText}"
                    : item.PressureScore >= 66f
                        ? $"主团卡 {targetText} 周边接团，预计 {etaText}"
                        : $"主团快速拿 {targetText}，拿完就转",
            };
            var kind = item.State == BattlefieldMapObjectiveState.Contested
                ? BattlefieldCommandKind.ContestObjective
                : item.State == BattlefieldMapObjectiveState.Controlled
                    ? BattlefieldCommandKind.DefendObjective
                    : IsCaptureObjective(item.Category) || item.Category == BattlefieldMapObjectiveCategory.Ice
                        ? BattlefieldCommandKind.ContestObjective
                        : BattlefieldCommandKind.Rotate;
            AddCommand(
                commands,
                $"objective:primary:{item.ObjectiveId}",
                kind,
                "主团",
                commandText,
                Math.Clamp(
                    item.PriorityScore
                    + scorePressure * 0.16f
                    + (highValue ? 18f : 0f)
                    + (refreshNow ? 16f : 0f)
                    + (finalMinuteAllIn ? 14f : 0f)
                    - item.RiskScore * (highValue || endgameAllIn ? 0.01f : 0.03f),
                    0f,
                    100f),
                urgency,
                8,
                item.Position,
                targetText,
                highValue ? "高价值点永久高于普通避险" : "目标收益/比分压力优先于普通风险",
                item.EvidenceText);
        }
        else if (item.PriorityScore >= 48f)
        {
            AddCommand(
                commands,
                $"objective:probe:{item.ObjectiveId}",
                BattlefieldCommandKind.PressureSide,
                "小队",
                $"小队看 {targetText}，主团压 {strategicTargetText} 找击杀",
                Math.Clamp(item.PriorityScore * 0.78f + item.RewardScore * 0.22f, 0f, 88f),
                Math.Clamp(48f + item.TimingScore * 0.22f, 0f, 84f),
                10,
                item.Position,
                targetText,
                "目标仍有价值，用侧压牵制换得分机会",
                item.EvidenceText);
        }

        if (priorities.Count >= 2 && !finalMinuteAllIn && !IsFatalRisk(risk) && risk.CohesionRisk <= 64f)
        {
            var second = priorities[1];
            if (!ShouldPreferAlternateObjective(second, timeSituation)
                && second.PriorityScore >= 50f
                && second.RiskScore <= 82f
                && second.MountedEtaSeconds <= 42)
            {
                AddCommand(
                    commands,
                    $"objective:split:{second.ObjectiveId}",
                    BattlefieldCommandKind.Split,
                    "分队",
                    $"分队去 {second.Name}，主团跟我压住 {targetText}",
                    Math.Clamp(second.PriorityScore * 0.88f + second.RewardScore * 0.12f - second.RiskScore * 0.04f, 0f, 86f),
                    54f,
                    14,
                    second.Position,
                    second.Name,
                    "次级目标可用分队主动牵制/摸点",
                    second.EvidenceText);
            }
        }
    }

    private static void AddAnnouncementCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        AnnouncementDecisionContext announcementContext,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        if (announcementContext.HasRecentObjective)
        {
            var announcement = announcementContext.Objective;
            var matchedPriority = ResolveAnnouncedPriority(announcementContext, priorities);
            var target = matchedPriority;
            var targetName = target.HasValue
                ? target.Value.Name
                : BuildAnnouncementTargetName(announcement, scoreSituation.MapType);
            var position = target.HasValue && IsMeaningfulPosition(target.Value.Position)
                ? target.Value.Position
                : anchor;
            var remainingText = announcement.RemainingSeconds.HasValue
                ? $"，剩 {FormatDuration(Math.Max(0, announcement.RemainingSeconds.Value))}"
                : string.Empty;
            var highValue = target.HasValue
                ? IsHighValueObjective(target.Value)
                : IsHighAnnouncementRank(announcement.RankName);
            var scorePressure = ResolveScorePressure(scoreSituation);

            switch (announcement.Kind)
            {
                case BattlefieldAnnouncementKind.ObjectiveWarning:
                case BattlefieldAnnouncementKind.ObjectiveOther when announcement.RemainingSeconds.HasValue:
                {
                    var forceWindow = announcement.RemainingSeconds is >= 0 and <= 20;
                    AddCommand(
                        commands,
                        BuildAnnouncementCommandId("objective-warning", announcement, target),
                        BattlefieldCommandKind.Rotate,
                        "主团",
                        forceWindow
                            ? $"通告目标快刷，立刻压 {targetName}{remainingText}"
                            : $"通告目标将刷，提前站 {targetName}{remainingText}",
                        Math.Clamp(60f + scorePressure * 0.14f + (highValue ? 14f : 0f) + (forceWindow ? 14f : 0f), 0f, 96f),
                        Math.Clamp(58f + (forceWindow ? 22f : 8f) + (highValue ? 8f : 0f), 0f, 96f),
                        7,
                        position,
                        targetName,
                        "战场通告给出目标刷新窗口，优先用于提前压位而不是原地等刷新",
                        BuildAnnouncementEvidence(announcement, target));
                    break;
                }

                case BattlefieldAnnouncementKind.ObjectiveAvailable:
                {
                    var kind = target.HasValue && IsCaptureObjective(target.Value.Category)
                        ? BattlefieldCommandKind.ContestObjective
                        : BattlefieldCommandKind.Rotate;
                    AddCommand(
                        commands,
                        BuildAnnouncementCommandId("objective-available", announcement, target),
                        kind,
                        "主团",
                        $"通告确认 {targetName} 可抢，先压人再摸点",
                        Math.Clamp(66f + scorePressure * 0.12f + (highValue ? 16f : 0f), 0f, 98f),
                        Math.Clamp(70f + (highValue ? 10f : 0f), 0f, 98f),
                        6,
                        position,
                        targetName,
                        "战场通告确认目标进入可处理状态，抢点/打断优先级上调",
                        BuildAnnouncementEvidence(announcement, target));
                    break;
                }

                case BattlefieldAnnouncementKind.ObjectiveControlled:
                {
                    var lockedObjective = announcementContext.ObjectiveCategory is BattlefieldMapObjectiveCategory.Ovoo
                        or BattlefieldMapObjectiveCategory.StrategicPoint;
                    AddCommand(
                        commands,
                        BuildAnnouncementCommandId("objective-controlled", announcement, target),
                        lockedObjective ? BattlefieldCommandKind.Rotate : BattlefieldCommandKind.PressureSide,
                        "主团",
                        lockedObjective
                            ? $"{targetName} 已被控制，别硬夺，转下一波"
                            : $"{targetName} 已被控制，外圈压人断收益",
                        lockedObjective ? 50f : Math.Clamp(54f + scorePressure * 0.12f, 0f, 84f),
                        lockedObjective ? 58f : 62f,
                        9,
                        position,
                        targetName,
                        lockedObjective
                            ? "该类目标控制后不可夺回，通告用于停止无效投入"
                            : "通告确认归属变化，转为外圈压制和准备下一轮",
                        BuildAnnouncementEvidence(announcement, target));
                    break;
                }

                case BattlefieldAnnouncementKind.ObjectiveReleased:
                {
                    AddCommand(
                        commands,
                        BuildAnnouncementCommandId("objective-released", announcement, target),
                        BattlefieldCommandKind.Rotate,
                        "主团",
                        $"{targetName} 已失效，别缠尾分，提前带下一波",
                        58f,
                        60f,
                        10,
                        position,
                        targetName,
                        "战场通告确认目标结束，避免为尾分拖住主团节奏",
                        BuildAnnouncementEvidence(announcement, target));
                    break;
                }
            }
        }

        if (!announcementContext.HasRecentWeather)
            return;

        var weather = announcementContext.Weather;
        var weatherRemaining = weather.RemainingSeconds.HasValue
            ? $"，剩 {FormatDuration(Math.Max(0, weather.RemainingSeconds.Value))}"
            : string.Empty;
        if (weather.Weather == BattlefieldWeatherKind.Snow)
        {
            AddCommand(
                commands,
                "announcement:weather:snow",
                BattlefieldCommandKind.Spread,
                "主团",
                weather.Kind == BattlefieldAnnouncementKind.WeatherWarning
                    ? $"小雪将至，低地别站桩{weatherRemaining}"
                    : $"小雪进行中，避雪人，贴护盾打",
                weather.Kind == BattlefieldAnnouncementKind.WeatherWarning ? 56f : 62f,
                weather.Kind == BattlefieldAnnouncementKind.WeatherWarning ? 58f : 66f,
                10,
                anchor,
                "小雪",
                "天气通告会改变地形和环境伤害风险，主团需要提前分散站位",
                weather.SummaryText);
        }
        else if (weather.Weather == BattlefieldWeatherKind.Aurora)
        {
            var friendlyToolsReady = teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount
                + teamSituation.KeySkillThreats.FriendlyLikelyReadyCount
                + teamSituation.Friendly.BattleFeverCount;
            AddCommand(
                commands,
                "announcement:weather:aurora",
                friendlyToolsReady >= 2 && risk.ThirdPartyPincerRisk < 78f ? BattlefieldCommandKind.Engage : BattlefieldCommandKind.Rotate,
                "主团",
                weather.Kind == BattlefieldAnnouncementKind.WeatherWarning
                    ? $"极光将至，提前压高价值点{weatherRemaining}"
                    : $"极光窗口，极限技增长快，抢高价值点",
                Math.Clamp(58f + friendlyToolsReady * 5f + ResolveScorePressure(scoreSituation) * 0.10f, 0f, 92f),
                Math.Clamp(58f + friendlyToolsReady * 6f, 0f, 90f),
                10,
                primary.HasValue && IsMeaningfulPosition(primary.Value.Position) ? primary.Value.Position : anchor,
                "极光",
                "极光通告提高高等级目标和极限技窗口价值，主动抢节奏",
                weather.SummaryText);
        }
    }

    private static void AddChatObjectiveEventCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldChatEventSituationSnapshot chatEvents,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        if (!chatEvents.IsAvailable || !chatEvents.LatestObjectiveEvent.HasValue)
            return;

        var item = chatEvents.LatestObjectiveEvent.Value;
        if (item.AgeMs > 35000 || !IsChatObjectiveEvent(item.Kind))
            return;

        var target = ResolveChatObjectivePriority(item, priorities, scoreSituation.MapType);
        var targetName = target.HasValue
            ? target.Value.Name
            : BuildChatObjectiveTargetName(item, scoreSituation.MapType);
        var position = target.HasValue && IsMeaningfulPosition(target.Value.Position)
            ? target.Value.Position
            : primary.HasValue && IsChatObjectiveLikelySameCategory(item, primary.Value, scoreSituation.MapType) && IsMeaningfulPosition(primary.Value.Position)
                ? primary.Value.Position
                : anchor;
        var pressure = ResolveScorePressure(scoreSituation);
        var endgameBonus = IsEndgameAllIn(timeSituation) ? 10f : 0f;
        var targetBonus = target.HasValue ? Math.Min(12f, target.Value.PriorityScore * 0.12f) : 0f;
        var lockedAfterCapture = IsChatObjectiveLockedAfterCapture(item, target, scoreSituation.MapType);

        switch (item.Kind)
        {
            case BattlefieldChatEventKind.ObjectiveContested:
                AddCommand(
                    commands,
                    BuildChatObjectiveCommandId("contested", item, target),
                    BattlefieldCommandKind.ContestObjective,
                    "主团",
                    $"{targetName}争夺中，先断摸点，再看能不能反抢",
                    Math.Clamp(56f + pressure * 0.12f + targetBonus + endgameBonus, 0f, 94f),
                    Math.Clamp(68f + endgameBonus, 0f, 96f),
                    6,
                    position,
                    targetName,
                    "聊天目标事件确认当前目标正在争夺，适合转成打断/抢点动作",
                    BuildChatObjectiveEvidence(item, target));
                break;

            case BattlefieldChatEventKind.ObjectiveLost:
                AddCommand(
                    commands,
                    BuildChatObjectiveCommandId("lost", item, target),
                    item.ActorSide == BattlefieldTacticalSide.Friendly && risk.OverallRisk >= 78f
                        ? BattlefieldCommandKind.Rotate
                        : BattlefieldCommandKind.ContestObjective,
                    "主团",
                    item.ActorSide == BattlefieldTacticalSide.Friendly
                        ? $"{targetName}刚丢，能断就回头断，打不了立刻转线"
                        : $"{targetName}刚掉归属，主团压过去补分窗口",
                    Math.Clamp(54f + pressure * 0.16f + targetBonus + (item.ActorSide == BattlefieldTacticalSide.Friendly ? 8f : 0f), 0f, 94f),
                    Math.Clamp(60f + endgameBonus + (item.ActorSide == BattlefieldTacticalSide.Friendly ? 8f : 0f), 0f, 94f),
                    7,
                    position,
                    targetName,
                    "聊天目标事件确认归属变化，比单纯地图状态更适合触发即时转线",
                    BuildChatObjectiveEvidence(item, target));
                break;

            case BattlefieldChatEventKind.ObjectiveCaptured:
                if (item.ActorSide == BattlefieldTacticalSide.Friendly)
                {
                    AddCommand(
                        commands,
                        BuildChatObjectiveCommandId("captured-friendly", item, target),
                        risk.ThirdPartyPincerRisk >= 68f || risk.RetreatRouteRisk >= 72f
                            ? BattlefieldCommandKind.Disengage
                            : BattlefieldCommandKind.DefendObjective,
                        "主团",
                        $"{targetName}已拿到，外圈压住别深追，准备下一波",
                        Math.Clamp(50f + pressure * 0.10f + MathF.Max(risk.ThirdPartyPincerRisk, risk.RetreatRouteRisk) * 0.12f, 0f, 88f),
                        Math.Clamp(54f + MathF.Max(risk.ThirdPartyPincerRisk, risk.RetreatRouteRisk) * 0.14f, 0f, 90f),
                        8,
                        position,
                        targetName,
                        "聊天目标事件确认已经拿到收益，继续深追容易把人头和位置送回去",
                        BuildChatObjectiveEvidence(item, target));
                }
                else
                {
                    AddCommand(
                        commands,
                        BuildChatObjectiveCommandId("captured-enemy", item, target),
                        lockedAfterCapture ? BattlefieldCommandKind.Rotate : BattlefieldCommandKind.ContestObjective,
                        "主团",
                        lockedAfterCapture
                            ? $"{targetName}已被对面拿，别硬送，转下一波或拦尾分"
                            : $"{targetName}被对面拿，外圈压人，能抢回再进点",
                        Math.Clamp(52f + pressure * 0.14f + targetBonus + endgameBonus, 0f, 92f),
                        Math.Clamp(58f + endgameBonus + (lockedAfterCapture ? 4f : 0f), 0f, 94f),
                        8,
                        position,
                        targetName,
                        "聊天目标事件确认敌方获得目标，避免继续按旧目标状态做决策",
                        BuildChatObjectiveEvidence(item, target));
                }

                break;

            case BattlefieldChatEventKind.ObjectiveOther when chatEvents.ObjectiveEventsRecent >= 2:
                AddCommand(
                    commands,
                    BuildChatObjectiveCommandId("changed", item, target),
                    BattlefieldCommandKind.Hold,
                    "主团",
                    "目标连续变化，先看地图收节奏，别追尾分",
                    Math.Clamp(42f + pressure * 0.10f, 0f, 78f),
                    48f,
                    12,
                    position,
                    targetName,
                    "聊天目标事件密集出现，说明地图收益窗口正在切换",
                    BuildChatObjectiveEvidence(item, target));
                break;
        }
    }

    private static void AddCommanderDoctrineCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        IReadOnlyList<BattlefieldObjectivePrioritySnapshot> priorities,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        StrategicFightPlan fightPlan,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        KnowledgeDecisionContext knowledgeContext)
    {
        var fatal = IsFatalFightState(risk, teamSituation);
        var friendlyToolsReady = teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount
            + teamSituation.KeySkillThreats.FriendlyLikelyReadyCount
            + teamSituation.KeySkillThreats.FriendlyHighThreatCount;
        var enemyVulnerable = teamSituation.Enemy.LowHpCount
            + teamSituation.Enemy.CrowdControlledCount
            + playerFrameEvents.EnemyControlledRecent;
        var hasReturnResetMacro = HasMacroIntent(knowledgeContext, "macro.intent.return_reset");
        var hasResourceRefreshMacro = HasMacroIntent(knowledgeContext, "macro.intent.resource_refresh");
        var hasCounterEngageMacro = HasMacroIntent(knowledgeContext, "macro.intent.counter_engage");
        var hasBigIceAllInMacro = HasMacroIntent(knowledgeContext, "macro.intent.big_ice_all_in");
        var hasWaitTimingMacro = HasMacroIntent(knowledgeContext, "macro.intent.wait_timing");
        var hasSpreadAntiBurstMacro = HasMacroIntent(knowledgeContext, "macro.intent.spread_anti_burst");
        var hasSplitTouchMacro = HasMacroIntent(knowledgeContext, "macro.intent.split_touch");
        var hasSingleTargetMacro = HasMacroIntent(knowledgeContext, "macro.intent.single_target");
        var hasHitAndRunMacro = HasMacroIntent(knowledgeContext, "macro.intent.hit_and_run");
        var returnAvailableInCombat = HasGlobalRule(knowledgeContext, "global.return.combat_available");
        var returnInterruptedByDamage = HasGlobalRule(knowledgeContext, "global.return.interrupt");

        if ((fatal || risk.NumberDisadvantageRisk >= 78f)
            && teamSituation.RespawnRhythm.FriendlyDeadNow >= 6
            && teamSituation.Enemy.NearCount <= 2
            && risk.SkillThreatRisk < 82f
            && !engagement.CanTakeFight)
        {
            AddCommand(
                commands,
                "doctrine:return-reset",
                BattlefieldCommandKind.Retreat,
                "能返回的人",
                "能返回就返回重整，别零散送",
                Math.Clamp(
                    58f
                    + teamSituation.RespawnRhythm.FriendlyDeadNow * 5f
                    + risk.NumberDisadvantageRisk * 0.16f
                    + (hasReturnResetMacro ? 8f : 0f)
                    + (returnAvailableInCombat ? 6f : 0f)
                    - (returnInterruptedByDamage && teamSituation.Enemy.NearCount >= 2 ? 10f : 0f),
                    0f,
                    96f),
                Math.Clamp(
                    66f
                    + risk.NumberDisadvantageRisk * 0.22f
                    + (hasReturnResetMacro ? 6f : 0f)
                    - (returnInterruptedByDamage && teamSituation.Enemy.NearCount >= 2 ? 8f : 0f),
                    0f,
                    96f),
                8,
                anchor,
                "出生点重整",
                "指挥宏语义：返回是快速重整工具，但贴脸压力高时会被打断",
                $"我方死亡 {teamSituation.RespawnRhythm.FriendlyDeadNow}；近敌 {teamSituation.Enemy.NearCount}；人数差 {risk.NumberDisadvantageRisk:0}；技能压 {risk.SkillThreatRisk:0}");
        }

        if (!fatal
            && (risk.LimitBreakRisk >= 74f || risk.SkillThreatRisk >= 78f || risk.HighGroundDropRisk >= 70f)
            && teamSituation.Enemy.NearCount >= 3
            && !engagement.ShouldCounterEngage)
        {
            AddCommand(
                commands,
                "doctrine:spread-precast",
                BattlefieldCommandKind.Spread,
                "主团",
                "横向分散防开团，技能交完再收束",
                Math.Clamp(48f + MathF.Max(risk.LimitBreakRisk, MathF.Max(risk.SkillThreatRisk, risk.HighGroundDropRisk)) * 0.34f + (hasSpreadAntiBurstMacro ? 8f : 0f), 0f, 92f),
                Math.Clamp(54f + MathF.Max(risk.LimitBreakRisk, risk.SkillThreatRisk) * 0.26f + (hasSpreadAntiBurstMacro ? 6f : 0f), 0f, 92f),
                6,
                anchor,
                "防爆发",
                "指挥宏语义：分散不是停手，是降低群控/极限技/高台空降收益",
                $"极限技 {risk.LimitBreakRisk:0}；技能 {risk.SkillThreatRisk:0}；高台 {risk.HighGroundDropRisk:0}；近敌 {teamSituation.Enemy.NearCount}");
        }

        var countdownWindow = ResolveSynchronizedCountdownWindow(teamSituation, risk, engagement, playerFrameEvents);
        if (!fatal
            && countdownWindow.CanCountdown
            && (engagement.HasBurstWindow || fightPlan.AggressiveEndgame)
            && enemyVulnerable >= (fightPlan.AggressiveEndgame ? 2 : 3)
            && friendlyToolsReady >= (fightPlan.AggressiveEndgame ? 1 : 2)
            && risk.ThirdPartyPincerRisk < 82f)
        {
            var destination = teamSituation.EnemyMainGroupMovement.HasMainGroup
                ? teamSituation.EnemyMainGroupMovement.CurrentCenter
                : anchor;
            AddCommand(
                commands,
                "doctrine:aoe-countdown",
                BattlefieldCommandKind.Engage,
                "主团",
                "三秒倒数打敌方大团，AOE往人最多处落",
                Math.Clamp(72f + enemyVulnerable * 5f + friendlyToolsReady * 4f + engagement.Score * 0.12f + (fightPlan.AggressiveEndgame ? 6f : 0f), 0f, 98f),
                Math.Clamp(76f + enemyVulnerable * 4f + friendlyToolsReady * 3f + (fightPlan.AggressiveEndgame ? 6f : 0f), 0f, 98f),
                5,
                destination,
                fightPlan.IsAvailable ? $"{fightPlan.TargetName}大团" : "敌方大团",
                "指挥宏语义：倒计时只在控制/低血/关键技能窗口重叠时使用，目标是敌方大团密集处而不是单人",
                $"敌脆弱 {enemyVulnerable}；我方工具 {friendlyToolsReady}；接团 {engagement.Score:0}；第三方 {risk.ThirdPartyPincerRisk:0}；{countdownWindow.EvidenceText}");
        }

        if (!fatal && primary.HasValue)
        {
            var item = primary.Value;
            var targetText = string.IsNullOrWhiteSpace(item.Name) ? CategoryText(item.Category) : item.Name;
            var captureObjective = IsCaptureObjective(item.Category) || item.Category == BattlefieldMapObjectiveCategory.Ice;
            if (captureObjective
                && item.State == BattlefieldMapObjectiveState.Contested
                && (item.PressureScore >= 62f || item.TimingScore >= 66f || item.RemainingSeconds is >= 0 and <= 45))
            {
                AddCommand(
                    commands,
                    $"doctrine:interrupt-touch:{item.ObjectiveId}",
                    BattlefieldCommandKind.ContestObjective,
                    "主团",
                    $"先打断 {targetText}，别让白拿",
                    Math.Clamp(62f + item.PressureScore * 0.20f + item.TimingScore * 0.20f + ResolveScorePressure(scoreSituation) * 0.14f, 0f, 96f),
                    Math.Clamp(68f + item.TimingScore * 0.18f + item.PressureScore * 0.14f, 0f, 96f),
                    6,
                    item.Position,
                    targetText,
                    "指挥宏语义：打断摸点优先于追零散目标，先阻止对方白拿分",
                    item.EvidenceText);
            }

            if (IsCaptureObjective(item.Category)
                && item.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Warning
                && item.PriorityScore >= 56f
                && item.RiskScore <= 82f
                && risk.ThirdPartyPincerRisk < 78f)
            {
                AddCommand(
                    commands,
                    $"doctrine:cover-touch:{item.ObjectiveId}",
                    BattlefieldCommandKind.PressureSide,
                    "主团",
                    $"前压掩护摸 {targetText}",
                    Math.Clamp(54f + item.PriorityScore * 0.25f + item.TeamAdvantageScore * 0.10f, 0f, 90f),
                    Math.Clamp(54f + item.PressureScore * 0.18f + item.TimingScore * 0.12f, 0f, 88f),
                    8,
                    item.Position,
                    targetText,
                    "指挥宏语义：前压是为了保护交互成功，不是无脑深进",
                    item.EvidenceText);
            }

            if (scoreSituation.MapType == FrontlineMapType.FieldsOfHonor
                && item.Category == BattlefieldMapObjectiveCategory.Ice
                && IsHighValueObjective(item)
                && item.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested or BattlefieldMapObjectiveState.Unknown
                && risk.ThirdPartyPincerRisk < 84f)
            {
                AddCommand(
                    commands,
                    $"doctrine:big-ice-all-in:{item.ObjectiveId}",
                    BattlefieldCommandKind.AttackObjective,
                    "主团",
                    $"全力大冰 {targetText}，别分火追人",
                    Math.Clamp(74f + item.RewardScore * 0.18f + item.PressureScore * 0.10f + (hasBigIceAllInMacro ? 8f : 0f), 0f, 98f),
                    Math.Clamp(72f + item.TimingScore * 0.16f + (hasBigIceAllInMacro ? 6f : 0f), 0f, 96f),
                    7,
                    item.Position,
                    targetText,
                    "指挥宏语义：高价值大冰期间优先转化分数，分火追人会丢贡献",
                    item.EvidenceText);
            }
        }

        if (!fatal
            && hasCounterEngageMacro
            && engagement.ShouldCounterEngage
            && teamSituation.Enemy.NearCount >= 3)
        {
            AddCommand(
                commands,
                "doctrine:counter-engage",
                BattlefieldCommandKind.Hold,
                "主团",
                "先接住再反压，别先把技能交空",
                Math.Clamp(64f + engagement.Score * 0.22f + enemyVulnerable * 3f, 0f, 94f),
                Math.Clamp(66f + engagement.Score * 0.18f + friendlyToolsReady * 3f, 0f, 94f),
                6,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor,
                fightPlan.IsAvailable ? fightPlan.TargetName : "敌方大团",
                "指挥宏语义：反打是先把敌方开团窗口接空，再用我方爆发回头压",
                $"{engagement.EvidenceText}；我方工具 {friendlyToolsReady}；近敌 {teamSituation.Enemy.NearCount}");
        }

        if (!fatal
            && (risk.SkillThreatRisk >= 78f || risk.LimitBreakRisk >= 78f)
            && !engagement.CanTakeFight
            && teamSituation.Enemy.NearCount >= 4
            && friendlyToolsReady < 2)
        {
            AddCommand(
                commands,
                "doctrine:feint-bait",
                BattlefieldCommandKind.Wait,
                "主团",
                "假压骗技能，别真进",
                Math.Clamp(48f + MathF.Max(risk.SkillThreatRisk, risk.LimitBreakRisk) * 0.28f + risk.AmbushRisk * 0.10f, 0f, 88f),
                Math.Clamp(54f + MathF.Max(risk.SkillThreatRisk, risk.LimitBreakRisk) * 0.22f, 0f, 90f),
                7,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor,
                "骗技能",
                "指挥宏语义：佯攻只制造压力，不把主团送进敌方爆发窗口",
                $"技能 {risk.SkillThreatRisk:0}；极限技 {risk.LimitBreakRisk:0}；我方工具 {friendlyToolsReady}；近敌 {teamSituation.Enemy.NearCount}");
        }

        if (!fatal
            && hasSingleTargetMacro
            && !engagement.HasBurstWindow
            && (engagement.CanTakeFight || engagement.CanPush)
            && enemyVulnerable >= 1
            && teamSituation.Enemy.NearCount <= 6)
        {
            AddCommand(
                commands,
                "doctrine:single-target",
                BattlefieldCommandKind.Engage,
                "主团",
                "先单点击减员，别空交 AOE",
                Math.Clamp(56f + engagement.Score * 0.18f + enemyVulnerable * 4f, 0f, 90f),
                Math.Clamp(52f + enemyVulnerable * 6f + friendlyToolsReady * 2f, 0f, 88f),
                6,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor,
                fightPlan.IsAvailable ? fightPlan.TargetName : "低血目标",
                "指挥宏语义：没有稳定 AOE 窗口时，先用单点集火把人数压出来",
                $"{engagement.EvidenceText}；敌方脆点 {enemyVulnerable}；我方工具 {friendlyToolsReady}");
        }

        if (!fatal
            && engagement.CanPush
            && !IsEndgameAllIn(timeSituation)
            && (risk.ThirdPartyPincerRisk >= 58f || risk.RetreatRouteRisk >= 64f || timeSituation.NextResourceSeconds is > 0 and <= 45))
        {
            AddCommand(
                commands,
                "doctrine:hit-and-run",
                BattlefieldCommandKind.Engage,
                "主团",
                "打一套就走，不深追",
                Math.Clamp(54f + engagement.Score * 0.26f + MathF.Max(risk.ThirdPartyPincerRisk, risk.RetreatRouteRisk) * 0.10f + (hasHitAndRunMacro ? 8f : 0f), 0f, 90f),
                Math.Clamp(52f + engagement.Score * 0.18f + risk.RetreatRouteRisk * 0.12f + (hasHitAndRunMacro ? 6f : 0f), 0f, 88f),
                8,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor,
                "短收益窗口",
                "指挥宏语义：只打一波用于收益窗口短、出路变差或资源将刷新的场面",
                $"{engagement.EvidenceText}；第三方 {risk.ThirdPartyPincerRisk:0}；出路 {risk.RetreatRouteRisk:0}；下一资源 {FormatOptionalSeconds(timeSituation.NextResourceSeconds)}");
        }

        if (!fatal
            && hasResourceRefreshMacro
            && timeSituation.NextResourceSeconds is > 0 and <= 30
            && (!primary.HasValue || !IsHighValueObjective(primary.Value) || primary.Value.State == BattlefieldMapObjectiveState.Controlled))
        {
            AddCommand(
                commands,
                "doctrine:resource-refresh",
                BattlefieldCommandKind.Rotate,
                "主团",
                timeSituation.NextResourceSeconds.Value <= 12
                    ? $"停掉空追，立刻压 {timeSituation.NextResourceName}"
                    : $"拉开拉扯，提前转 {timeSituation.NextResourceName}",
                Math.Clamp(58f + (30 - timeSituation.NextResourceSeconds.Value) * 1.1f + risk.ScorePressure * 0.12f, 0f, 92f),
                Math.Clamp(60f + (30 - timeSituation.NextResourceSeconds.Value) * 0.9f, 0f, 92f),
                10,
                primary.HasValue ? primary.Value.Position : Vector3.Zero,
                timeSituation.NextResourceName,
                "指挥宏语义：资源刷新前停掉没有收益的缠斗，把节奏提前切到下一波",
                $"下一资源 {FormatDuration(timeSituation.NextResourceSeconds.Value)}；来源 {timeSituation.NextResourceSource}");
        }

        if (!fatal
            && hasWaitTimingMacro
            && timeSituation.NextResourceSeconds is > 0 and <= 22
            && !IsEndgameAllIn(timeSituation)
            && !engagement.HasBurstWindow
            && (!primary.HasValue || !IsHighValueObjective(primary.Value) || primary.Value.PriorityScore < 70f))
        {
            AddCommand(
                commands,
                "doctrine:wait-timing",
                BattlefieldCommandKind.Wait,
                "主团",
                $"先卡位等 {timeSituation.NextResourceName}，别在这里空开",
                Math.Clamp(54f + (22 - timeSituation.NextResourceSeconds.Value) * 1.4f + MathF.Max(risk.ThirdPartyPincerRisk, risk.SkillThreatRisk) * 0.08f, 0f, 90f),
                Math.Clamp(56f + (22 - timeSituation.NextResourceSeconds.Value) * 1.1f, 0f, 88f),
                8,
                primary.HasValue ? primary.Value.Position : anchor,
                timeSituation.NextResourceName,
                "指挥宏语义：等时机不是原地挂机，是在可介入位置保留资源和进场点",
                $"下一资源 {FormatDuration(timeSituation.NextResourceSeconds.Value)}；第三方 {risk.ThirdPartyPincerRisk:0}；技能压 {risk.SkillThreatRisk:0}");
        }

        if (!fatal
            && priorities.Count >= 2
            && risk.CohesionRisk <= 58f
            && risk.ThirdPartyPincerRisk < 72f)
        {
            var second = priorities[1];
            if (second.PriorityScore >= 48f
                && second.RiskScore <= 72f
                && second.MountedEtaSeconds <= 36
                && (second.Category == BattlefieldMapObjectiveCategory.Ice || IsCaptureObjective(second.Category)))
            {
                AddCommand(
                    commands,
                    $"doctrine:split-scout:{second.ObjectiveId}",
                    BattlefieldCommandKind.Split,
                    "1-4人",
                    $"分队摸 {second.Name}，主团别散",
                    Math.Clamp(44f + second.PriorityScore * 0.36f - second.RiskScore * 0.08f + (hasSplitTouchMacro ? 8f : 0f), 0f, 84f),
                    Math.Clamp(44f + second.TimingScore * 0.18f + (hasSplitTouchMacro ? 6f : 0f), 0f, 78f),
                    14,
                    second.Position,
                    second.Name,
                    "指挥宏语义：分队处理副目标，主团不能因此拆散",
                    second.EvidenceText);
            }
        }

        var friendly = scoreSituation.FriendlyAlliance;
        if (friendly.HasValue
            && friendly.Value.RankIndex == 3
            && timeSituation.MatchTimeRemainingSeconds is > 0 and <= 45
            && scoreSituation.RankedAlliances.Length > 0
            && scoreSituation.RankedAlliances[0].Score - friendly.Value.Score >= 120
            && (enemyVulnerable >= 2 || engagement.CanTakeFight))
        {
            AddCommand(
                commands,
                "doctrine:losing-kill-all-in",
                BattlefieldCommandKind.FocusTarget,
                "主团",
                "翻盘路很窄，就地收人头",
                Math.Clamp(64f + enemyVulnerable * 6f + engagement.Score * 0.16f, 0f, 92f),
                88f,
                5,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor,
                "终局击杀",
                "指挥宏语义：败势换击杀只允许在极低翻盘率终局触发，不能替代正常拿分",
                $"我方 {friendly.Value.Score}/{friendly.Value.VictoryScore}；落后第一 {scoreSituation.RankedAlliances[0].Score - friendly.Value.Score}；剩余 {FormatDuration(timeSituation.MatchTimeRemainingSeconds)}");
        }
    }

    private static bool ShouldLetThirdPlaceTakeObjective(
        BattlefieldObjectivePrioritySnapshot objective,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        StrategicFightPlan fightPlan,
        out string thirdName,
        out string reason)
    {
        thirdName = "第三名";
        reason = string.Empty;

        if (IsEndgameAllIn(timeSituation) || IsHighValueObjective(objective))
            return false;
        if (objective.RewardScore >= 74f || objective.ScoreValue is >= 100)
            return false;
        if (objective.State == BattlefieldMapObjectiveState.Contested && objective.PressureScore >= 70f)
            return false;

        var friendly = scoreSituation.FriendlyAlliance;
        if (!friendly.HasValue || friendly.Value.RankIndex == 3)
            return false;

        var third = scoreSituation.RankedAlliances.FirstOrDefault(alliance => alliance.RankIndex == 3 && !alliance.IsLocalAlliance);
        if (!IsValidScoreAlliance(third))
            return false;
        if (fightPlan.TargetBattalion.HasValue && third.Battalion == fightPlan.TargetBattalion)
            return false;

        var leader = scoreSituation.RankedAlliances.FirstOrDefault(alliance => alliance.RankIndex == 1);
        if (!IsValidScoreAlliance(leader))
            return false;

        var thirdGapToLeader = leader.Score - third.Score;
        var friendlyLeadOverThird = friendly.Value.Score - third.Score;
        if (thirdGapToLeader < 80)
            return false;
        if (friendly.Value.RankIndex == 2 && friendlyLeadOverThird < 130)
            return false;

        var ownerBattalion = OwnershipToBattalion(objective.Ownership);
        var ownedByThird = ownerBattalion.HasValue && third.Battalion.HasValue && ownerBattalion.Value == third.Battalion.Value;
        var cheapNeutralPoint = !ownerBattalion.HasValue || objective.Ownership == NodeOwnership.Neutral;
        var canLetGo = ownedByThird
            || (cheapNeutralPoint && objective.DistanceToLocal >= 70f)
            || objective.RewardScore <= 58f;
        if (!canLetGo)
            return false;

        thirdName = string.IsNullOrWhiteSpace(third.Name) ? "第三名" : third.Name;
        reason = $"老三第3 {third.Score}/{third.VictoryScore}，落后第一 {thirdGapToLeader}；我方第{friendly.Value.RankIndex} {friendly.Value.Score}；点位收益 {objective.RewardScore:0}，价值 {(objective.ScoreValue.HasValue ? objective.ScoreValue.Value.ToString() : "未知")}";
        return true;
    }

    private static MarkedEnemyTarget? ResolveMarkedEnemyTarget(
        BattlefieldTeamSituationSnapshot teamSituation,
        IReadOnlyList<BattlefieldTargetMarkerSnapshot> targetMarkers)
    {
        if (targetMarkers.Count == 0)
            return null;

        var playersById = new Dictionary<ulong, BattlefieldPlayerSnapshot>();
        foreach (var alliance in teamSituation.Alliances)
        {
            foreach (var player in alliance.VisiblePlayers)
            {
                if (player.GameObjectId != 0 && !playersById.ContainsKey(player.GameObjectId))
                    playersById[player.GameObjectId] = player;
            }
        }

        return targetMarkers
            .Where(marker => marker.TargetGameObjectId != 0)
            .Select(marker => playersById.TryGetValue(marker.TargetGameObjectId, out var player)
                ? new MarkedEnemyTarget(marker, player, ResolveTargetMarkerPriorityBonus(marker.Index), ScoreMarkedEnemyTarget(marker, player))
                : default)
            .Where(item => item.Player.GameObjectId != 0
                && item.Player.Relation == BattlefieldPlayerRelation.Enemy
                && !item.Player.IsDead
                && !item.Player.IsInvulnerable)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Marker.Index)
            .Select(item => (MarkedEnemyTarget?)item)
            .FirstOrDefault();
    }

    private static float ResolveTargetMarkerPriorityBonus(uint markerIndex)
        => markerIndex switch
        {
            0 => 12f,
            <= 4 => 9f,
            <= 8 => 6f,
            _ => 4f,
        };

    private static float ScoreMarkedEnemyTarget(BattlefieldTargetMarkerSnapshot marker, BattlefieldPlayerSnapshot player)
    {
        if (player.GameObjectId == 0 || player.Relation != BattlefieldPlayerRelation.Enemy || player.IsDead || player.IsInvulnerable)
            return 0f;

        var hpBonus = player.HpPercent switch
        {
            > 0f and <= 25f => 22f,
            > 0f and <= 45f => 16f,
            > 0f and <= 65f => 8f,
            _ => 0f,
        };
        var stateBonus = (player.IsCrowdControlled ? 10f : 0f)
            + (player.IsExecutable ? 10f : 0f)
            + (player.IsGuarding ? -8f : 0f)
            + (player.IsBattleFever ? 8f : 0f)
            + Math.Min(8f, player.BattleHighLevel * 1.5f);
        return Math.Clamp(50f + ResolveTargetMarkerPriorityBonus(marker.Index) + hpBonus + stateBonus, 0f, 100f);
    }

    private static void AddTargetCommands(
        List<BattlefieldCommandSnapshot> commands,
        BattlefieldTeamSituationSnapshot teamSituation,
        IReadOnlyList<BattlefieldTargetMarkerSnapshot> targetMarkers,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        var marked = ResolveMarkedEnemyTarget(teamSituation, targetMarkers);
        if (marked.HasValue)
        {
            var item = marked.Value;
            var hpText = item.Player.HpPercent > 0f ? $"{item.Player.HpPercent:0}%" : "血量未知";
            var vulnerableBonus = item.Player.HpPercent is > 0f and <= 45f
                ? 12f
                : item.Player.IsCrowdControlled || item.Player.IsExecutable ? 10f : 0f;
            AddCommand(
                commands,
                $"target:marked:{item.Marker.Index}:{item.Player.GameObjectId}",
                BattlefieldCommandKind.FocusTarget,
                "主团",
                $"集火标记目标 {item.Player.Name}，{hpText}，跟大字转火",
                Math.Clamp(58f + vulnerableBonus + item.MarkerPriorityBonus - risk.OverallRisk * 0.05f, 0f, 96f),
                Math.Clamp(60f + vulnerableBonus + item.MarkerPriorityBonus, 0f, 94f),
                5,
                item.Player.Position,
                item.Player.Name,
                "游戏目标标记是明确集火信号，优先转成屏幕集火提示",
                $"标记 {item.Marker.Index + 1}；职业ID {item.Player.ClassJobId}；血量 {hpText}；战意 {item.Player.BattleHighLevel}");
        }

        var focus = teamSituation.FriendlyFocusTargets
            .OrderByDescending(target => target.ThreatScore)
            .ThenBy(target => target.HpPercent <= 0f ? 101f : target.HpPercent)
            .FirstOrDefault();
        if (focus.TargetGameObjectId != 0 && (focus.AttackerCount + focus.CasterCount >= 3 || focus.HpPercent is > 0f and <= 35f))
        {
            var hpText = focus.HpPercent > 0f ? $"{focus.HpPercent:0}%" : "血量未知";
            AddCommand(
                commands,
                $"target:focus:{focus.TargetGameObjectId}",
                BattlefieldCommandKind.FocusTarget,
                "主团",
                $"集火 {focus.TargetName}，{hpText}",
                Math.Clamp(44f + focus.ThreatScore * 12f + (focus.HpPercent is > 0f and <= 35f ? 16f : 0f), 0f, 96f),
                Math.Clamp(48f + focus.CasterCount * 8f + (focus.HpPercent is > 0f and <= 35f ? 18f : 0f), 0f, 90f),
                5,
                focus.Position,
                focus.TargetName,
                "我方已有集火样本",
                $"攻击 {focus.AttackerCount}；咏唱 {focus.CasterCount}；职业 {focus.TargetJobName}；来源 {string.Join("/", focus.SourceNames.Take(4))}");
        }

        var endangered = teamSituation.EnemyFocusTargets
            .OrderByDescending(target => target.ThreatScore)
            .ThenBy(target => target.HpPercent <= 0f ? 101f : target.HpPercent)
            .FirstOrDefault();
        if (endangered.TargetGameObjectId != 0 && (endangered.AttackerCount + endangered.CasterCount >= 4 || endangered.HpPercent is > 0f and <= 35f))
        {
            AddCommand(
                commands,
                $"target:protect:{endangered.TargetGameObjectId}",
                BattlefieldCommandKind.ProtectTarget,
                "辅助/近战",
                $"保 {endangered.TargetName}，清他身边的人",
                Math.Clamp(42f + endangered.ThreatScore * 11f + risk.SkillThreatRisk * 0.10f, 0f, 92f),
                Math.Clamp(54f + endangered.CasterCount * 8f, 0f, 92f),
                6,
                endangered.Position,
                endangered.TargetName,
                "我方成员被敌方集火",
                $"敌攻击 {endangered.AttackerCount}；敌咏唱 {endangered.CasterCount}；血量 {endangered.HpPercent:0}%");
        }

        var topEnemySkill = teamSituation.KeySkillThreats.TopEnemyThreats.FirstOrDefault();
        if (topEnemySkill.GameObjectId != 0 && topEnemySkill.ThreatScore >= 70f)
        {
            AddCommand(
                commands,
                $"target:control-skill:{topEnemySkill.GameObjectId}:{topEnemySkill.SkillName}",
                BattlefieldCommandKind.FocusTarget,
                "控场/近战",
                $"盯 {topEnemySkill.Name}，别让他打 {topEnemySkill.SkillName}",
                Math.Clamp(45f + topEnemySkill.ThreatScore * 0.48f, 0f, 94f),
                Math.Clamp(48f + topEnemySkill.ThreatScore * 0.38f, 0f, 92f),
                8,
                Vector3.Zero,
                topEnemySkill.Name,
                "敌方关键技能威胁高",
                topEnemySkill.EvidenceText);
        }
    }

    private static void AddFieldMarkerCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        IReadOnlyList<BattlefieldFieldMarkerSnapshot> fieldMarkers,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement)
    {
        if (fieldMarkers.Count == 0)
            return;

        if (primary.HasValue && IsMeaningfulPosition(primary.Value.Position))
        {
            var markerNearPrimary = FindNearestFieldMarker(fieldMarkers, primary.Value.Position, 42f);
            if (markerNearPrimary.HasValue && primary.Value.PriorityScore >= 44f)
            {
                var marker = markerNearPrimary.Value.Marker;
                var distance = markerNearPrimary.Value.Distance;
                var kind = primary.Value.State == BattlefieldMapObjectiveState.Contested
                    ? BattlefieldCommandKind.ContestObjective
                    : BattlefieldCommandKind.Rotate;
                AddCommand(
                    commands,
                    $"field-marker:objective:{marker.Index}:{primary.Value.ObjectiveId}",
                    kind,
                    "主团",
                    $"场地标记贴近 {primary.Value.Name}，按标记站位处理目标",
                    Math.Clamp(42f + primary.Value.PriorityScore * 0.32f - risk.OverallRisk * 0.05f + (distance <= 18f ? 8f : 0f), 0f, 88f),
                    Math.Clamp(44f + primary.Value.TimingScore * 0.22f + (primary.Value.State == BattlefieldMapObjectiveState.Contested ? 10f : 0f), 0f, 86f),
                    12,
                    marker.Position,
                    $"标记 {marker.Index + 1}/{primary.Value.Name}",
                    "场地标记是人工站位锚点；只在贴近当前高优先级目标时参与决策",
                    $"标记距离目标 {distance:0}y；目标优先级 {primary.Value.PriorityScore:0}；风险 {risk.OverallRisk:0}");
            }
        }

        if (teamSituation.EnemyMainGroupMovement.HasMainGroup)
        {
            var markerNearEnemy = FindNearestFieldMarker(fieldMarkers, teamSituation.EnemyMainGroupMovement.CurrentCenter, 40f);
            if (markerNearEnemy.HasValue && (engagement.CanTakeFight || engagement.ShouldCounterEngage) && risk.ThirdPartyPincerRisk < 78f)
            {
                var marker = markerNearEnemy.Value.Marker;
                AddCommand(
                    commands,
                    $"field-marker:enemy:{marker.Index}",
                    engagement.CanTakeFight ? BattlefieldCommandKind.Engage : BattlefieldCommandKind.Hold,
                    "主团",
                    $"敌方主团贴近场地标记 {marker.Index + 1}，按标记接团，不要越过太深",
                    Math.Clamp(44f + engagement.Score * 0.30f - risk.ThirdPartyPincerRisk * 0.08f, 0f, 84f),
                    Math.Clamp(46f + engagement.Score * 0.18f, 0f, 82f),
                    12,
                    marker.Position,
                    $"场地标记 {marker.Index + 1}",
                    "场地标记与敌方主团重合时，可作为人工接团/卡位置锚点",
                    $"{engagement.EvidenceText}；标记距离敌团 {markerNearEnemy.Value.Distance:0}y；第三方 {risk.ThirdPartyPincerRisk:0}");
            }
        }

        if (risk.CohesionRisk >= 58f)
        {
            var markerNearAnchor = FindNearestFieldMarker(fieldMarkers, anchor, 120f);
            if (markerNearAnchor.HasValue)
            {
                var marker = markerNearAnchor.Value.Marker;
                AddCommand(
                    commands,
                    $"field-marker:regroup:{marker.Index}",
                    BattlefieldCommandKind.Regroup,
                    "主团",
                    $"队形散，往场地标记 {marker.Index + 1} 收一下再动",
                    Math.Clamp(42f + risk.CohesionRisk * 0.30f - markerNearAnchor.Value.Distance * 0.04f, 0f, 82f),
                    Math.Clamp(48f + risk.CohesionRisk * 0.24f, 0f, 84f),
                    14,
                    marker.Position,
                    $"场地标记 {marker.Index + 1}",
                    "队伍跟随散乱时，场地标记可作为低歧义集合点",
                    $"凝聚风险 {risk.CohesionRisk:0}；标记距离指挥 {markerNearAnchor.Value.Distance:0}y");
            }
        }
    }

    private static (BattlefieldFieldMarkerSnapshot Marker, float Distance)? FindNearestFieldMarker(
        IReadOnlyList<BattlefieldFieldMarkerSnapshot> fieldMarkers,
        Vector3 position,
        float maxDistance)
    {
        if (fieldMarkers.Count == 0 || !IsMeaningfulPosition(position))
            return null;

        (BattlefieldFieldMarkerSnapshot Marker, float Distance)? best = null;
        foreach (var marker in fieldMarkers)
        {
            if (!IsMeaningfulPosition(marker.Position))
                continue;

            var distance = Distance2D(marker.Position, position);
            if (distance > maxDistance)
                continue;

            if (!best.HasValue || distance < best.Value.Distance)
                best = (marker, distance);
        }

        return best;
    }

    private static void AddSnowBlessingCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement)
    {
        var friendly = teamSituation.Friendly.SnowBlessingCount;
        var enemy = teamSituation.Enemy.SnowBlessingCount;
        if (friendly <= 0 && enemy <= 0)
            return;

        var destination = teamSituation.EnemyMainGroupMovement.HasMainGroup
            ? teamSituation.EnemyMainGroupMovement.CurrentCenter
            : anchor;
        if (friendly >= Math.Max(3, enemy + 2) && risk.ThirdPartyPincerRisk < 76f && risk.RetreatRouteRisk < 82f)
        {
            AddCommand(
                commands,
                "snow-blessing:friendly-window",
                engagement.CanTakeFight ? BattlefieldCommandKind.Engage : BattlefieldCommandKind.PressureSide,
                "主团",
                "我方雪精护盾多，贴护盾压一波，打完别追进雪人区",
                Math.Clamp(48f + friendly * 5f + engagement.Score * 0.18f - risk.ThirdPartyPincerRisk * 0.08f, 0f, 88f),
                Math.Clamp(50f + friendly * 4f, 0f, 86f),
                10,
                destination,
                "雪精护盾窗口",
                "雪精祝福是真实护盾状态，可作为短时间换血窗口",
                $"雪精祝福 我方 {friendly}/敌方 {enemy}；接团 {engagement.Score:0}；第三方 {risk.ThirdPartyPincerRisk:0}");
        }
        else if (enemy >= Math.Max(3, friendly + 2))
        {
            AddCommand(
                commands,
                "snow-blessing:enemy-shield",
                BattlefieldCommandKind.Hold,
                "主团",
                "对面雪精护盾多，先拉开骗盾，别硬灌第一波",
                Math.Clamp(46f + enemy * 5f + risk.CombatRisk * 0.08f, 0f, 86f),
                Math.Clamp(52f + enemy * 4f, 0f, 88f),
                10,
                anchor,
                "敌方雪精护盾",
                "敌方护盾样本明显更多时，硬开会降低击杀转化率",
                $"雪精祝福 我方 {friendly}/敌方 {enemy}；战斗风险 {risk.CombatRisk:0}");
        }
    }

    private static void AddMapCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        var fatal = IsFatalRisk(risk);
        if (risk.ChokeBlockRisk >= 70f)
        {
            var zone = mapTactics.TopZones
                .Where(item => item.Kind == MapAnnotationKind.Choke || item.IsMandatoryChoke)
                .OrderByDescending(item => item.TotalRisk)
                .FirstOrDefault();
            var name = string.IsNullOrWhiteSpace(zone.Label) ? "卡口" : zone.Label;
            AddCommand(
                commands,
                fatal && risk.ChokeBlockRisk >= 88f ? "map:side-choke-fatal" : "map:hold-choke-counter",
                fatal && risk.ChokeBlockRisk >= 88f ? BattlefieldCommandKind.Detour : BattlefieldCommandKind.Engage,
                "主团",
                fatal && risk.ChokeBlockRisk >= 88f
                    ? $"{name} 是必死埋伏，换侧线继续压"
                    : $"卡住 {name} 反打，前排堵口后排集火",
                Math.Clamp(48f + risk.ChokeBlockRisk * 0.32f, 0f, 90f),
                Math.Clamp(46f + risk.ChokeBlockRisk * 0.24f, 0f, 84f),
                9,
                string.IsNullOrWhiteSpace(zone.Id) ? anchor : zone.Position,
                name,
                fatal && risk.ChokeBlockRisk >= 88f ? "关键通道为致命埋伏" : "卡口有压力但可以主动反打",
                string.IsNullOrWhiteSpace(zone.EvidenceText) ? $"封路 {risk.ChokeBlockRisk:0}" : zone.EvidenceText);
        }

        if (!fatal)
        {
            var highGround = mapTactics.TopZones
                .Where(item => item.Kind == MapAnnotationKind.HighGround)
                .OrderByDescending(item => item.EnemyNearby + item.EnemyMapVisionNearby + item.FriendlyNearby)
                .ThenByDescending(item => item.TotalRisk)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(highGround.Id))
            {
                var name = string.IsNullOrWhiteSpace(highGround.Label) ? "高地" : highGround.Label;
                AddCommand(
                    commands,
                    $"map:highground-press:{highGround.Id}",
                    BattlefieldCommandKind.PressureSide,
                    "主团",
                    $"占 {name} 后往下压，别蹲高地不动",
                    Math.Clamp(52f + highGround.FriendlyNearby * 2f + highGround.EnemyNearby * 3f, 0f, 86f),
                    Math.Clamp(48f + highGround.EnemyNearby * 4f + highGround.EnemyMapVisionNearby * 2f, 0f, 82f),
                    10,
                    highGround.Position,
                    name,
                    "高地用于进攻压制，不做纯防守点",
                    string.IsNullOrWhiteSpace(highGround.EvidenceText) ? $"高地；敌附近 {highGround.EnemyNearby}" : highGround.EvidenceText);
            }

            var lowGroundEnemy = mapTactics.TopZones
                .Where(item => item.Kind == MapAnnotationKind.LowGround && item.EnemyNearby + item.EnemyMapVisionNearby > 0)
                .OrderByDescending(item => item.EnemyNearby + item.EnemyMapVisionNearby)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(lowGroundEnemy.Id))
            {
                var name = string.IsNullOrWhiteSpace(lowGroundEnemy.Label) ? "低洼区" : lowGroundEnemy.Label;
                AddCommand(
                    commands,
                    $"map:lowground-dive:{lowGroundEnemy.Id}",
                    BattlefieldCommandKind.Engage,
                    "主团",
                    $"敌方在 {name}，直接下压打",
                    Math.Clamp(54f + (lowGroundEnemy.EnemyNearby + lowGroundEnemy.EnemyMapVisionNearby) * 4f, 0f, 92f),
                    Math.Clamp(58f + lowGroundEnemy.EnemyNearby * 5f, 0f, 90f),
                    8,
                    lowGroundEnemy.Position,
                    name,
                    "敌方低洼无地形掩护，主动突进",
                    string.IsNullOrWhiteSpace(lowGroundEnemy.EvidenceText) ? $"敌附近 {lowGroundEnemy.EnemyNearby}" : lowGroundEnemy.EvidenceText);
            }

            var jumpPad = mapTactics.TopZones
                .Where(item => item.Kind == MapAnnotationKind.JumpPad)
                .OrderBy(item => Distance2D(item.Position, anchor))
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(jumpPad.Id))
            {
                var name = string.IsNullOrWhiteSpace(jumpPad.Label) ? "跳台" : jumpPad.Label;
                var isSJump = name.Contains("S", StringComparison.OrdinalIgnoreCase);
                AddCommand(
                    commands,
                    $"map:jump-pad:{jumpPad.Id}",
                    BattlefieldCommandKind.PressureSide,
                    "分队",
                    isSJump ? $"S跳点 {name} 必走，分队绕后切后排，主团正面压" : $"分队用 {name} 绕后切后排，主团正面压",
                    Math.Clamp(50f + Math.Max(0f, 80f - jumpPad.TotalRisk) * 0.18f + risk.FlankRisk * 0.08f + (isSJump ? 14f : 0f), 0f, 92f),
                    isSJump ? 74f : 58f,
                    12,
                    jumpPad.Position,
                    name,
                    "跳台/弹射用于主动绕后，不因普通风险禁用",
                    string.IsNullOrWhiteSpace(jumpPad.EvidenceText) ? $"跳台风险 {jumpPad.TotalRisk:0}" : jumpPad.EvidenceText);
            }

            var teleporter = mapTactics.TopZones
                .Where(item => item.Kind == MapAnnotationKind.Teleporter)
                .OrderBy(item => Distance2D(item.Position, anchor))
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(teleporter.Id))
            {
                var name = string.IsNullOrWhiteSpace(teleporter.Label) ? "传送装置" : teleporter.Label;
                AddCommand(
                    commands,
                    $"map:teleporter:{teleporter.Id}",
                    BattlefieldCommandKind.Rotate,
                    "主团",
                    $"用 {name} 快速转点突袭，别走远路",
                    Math.Clamp(54f + Math.Max(0f, 75f - teleporter.TotalRisk) * 0.20f, 0f, 88f),
                    62f,
                    12,
                    teleporter.Position,
                    name,
                    "传送装置满足条件就用于快速转点",
                    string.IsNullOrWhiteSpace(teleporter.EvidenceText) ? $"传送风险 {teleporter.TotalRisk:0}" : teleporter.EvidenceText);
            }

            var flankZone = mapTactics.TopZones
                .Where(item => item.Kind == MapAnnotationKind.Flank)
                .OrderBy(item => Distance2D(item.Position, anchor))
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(flankZone.Id))
            {
                var name = string.IsNullOrWhiteSpace(flankZone.Label) ? "夹击线" : flankZone.Label;
                AddCommand(
                    commands,
                    $"map:flank-line:{flankZone.Id}",
                    BattlefieldCommandKind.Split,
                    "分队",
                    $"分队走 {name} 包抄，主力正面推进拿点",
                    Math.Clamp(50f + Math.Max(0f, 78f - flankZone.TotalRisk) * 0.16f + risk.ScorePressure * 0.10f, 0f, 86f),
                    56f,
                    14,
                    flankZone.Position,
                    name,
                    "夹击路线用于主动包抄，不只用于防夹",
                    string.IsNullOrWhiteSpace(flankZone.EvidenceText) ? $"夹击线风险 {flankZone.TotalRisk:0}" : flankZone.EvidenceText);
            }
        }

        if (!string.IsNullOrWhiteSpace(mapTactics.CurrentRecommendation) && risk.OverallRisk >= 76f && fatal)
        {
            AddCommand(
                commands,
                "map:recommendation",
                BattlefieldCommandKind.Hold,
                "主团",
                $"按地图建议换进攻角度：{mapTactics.CurrentRecommendation}",
                Math.Clamp(36f + risk.MapRisk * 0.22f + risk.TerrainRisk * 0.18f, 0f, 76f),
                42f,
                12,
                anchor,
                "地图建议",
                "地图战术层给出当前建议",
                mapTactics.SummaryText);
        }
    }

    private static void AddTempoCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement)
    {
        var friendly = scoreSituation.FriendlyAlliance;
        AddThreeFactionTempoCommands(commands, anchor, primary, teamSituation, scoreSituation, timeSituation, chatEvents, risk, engagement, friendly);
        var openingAggressiveWindow = IsOpeningBattleHighFarmWindow(timeSituation);

        if (friendly.HasValue
            && friendly.Value.VictoryScore > 0
            && friendly.Value.VictoryScore - friendly.Value.Score is > 0 and <= 24
            && !IsFatalFightState(risk, teamSituation)
            && (engagement.CanTakeFight || engagement.HasBurstWindow || teamSituation.Enemy.LowHpCount > 0 || teamSituation.Enemy.CrowdControlledCount > 0))
        {
            AddCommand(
                commands,
                "tempo:kill-win-now",
                BattlefieldCommandKind.FocusTarget,
                "主团",
                "差一两个人头，别散，直接集火杀赢",
                98f,
                98f,
                5,
                teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor,
                "杀赢",
                "决赛圈杀分已经够近，撤退会让队伍散掉，优先用最近低血/被控目标结束比赛",
                $"我方 {friendly.Value.Score}/{friendly.Value.VictoryScore}；距胜 {friendly.Value.VictoryScore - friendly.Value.Score}；接团 {engagement.Score:0}；敌残血 {teamSituation.Enemy.LowHpCount}；敌被控 {teamSituation.Enemy.CrowdControlledCount}");
        }

        var earlyFightEvidence = engagement.Score >= 54f
            || teamSituation.IsEnemySplit
            || teamSituation.Enemy.LowHpCount > 0
            || teamSituation.Enemy.CrowdControlledCount > 0
            || teamSituation.Friendly.BattleFeverCount > 0
            || teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 1;
        if (openingAggressiveWindow
            && !IsFatalFightState(risk, teamSituation)
            && risk.ThirdPartyPincerRisk < 78f
            && risk.EncirclementRisk < 80f
            && risk.RetreatRouteRisk < 82f
            && earlyFightEvidence)
        {
            var destination = teamSituation.EnemyMainGroupMovement.HasMainGroup
                ? teamSituation.EnemyMainGroupMovement.CurrentCenter
                : anchor;
            var targetName = teamSituation.IsEnemySplit ? "分散敌方" : "敌方主团";
            var earlyFightScore = Math.Clamp(
                76f
                + teamSituation.Friendly.BattleFeverCount * 6f
                + teamSituation.Friendly.BattleHighTotalLevel * 0.8f
                + teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount * 5f
                + teamSituation.Enemy.LowHpCount * 4f
                + (teamSituation.IsEnemySplit ? 8f : 0f),
                0f,
                96f);
            AddCommand(
                commands,
                "tempo:early-battle-high-farm",
                BattlefieldCommandKind.Engage,
                "主团",
                "前4分钟主动找架刷战意，优先低血/落单/被控",
                earlyFightScore,
                Math.Clamp(72f + teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount * 5f + (teamSituation.IsEnemySplit ? 8f : 0f), 0f, 94f),
                7,
                destination,
                targetName,
                "前期击杀和助攻用于滚战意，后续抢点更强",
                $"已进行 {FormatDuration(timeSituation.MatchElapsedSeconds)}；我方战意总层 {teamSituation.Friendly.BattleHighTotalLevel}；战意狂热 {teamSituation.Friendly.BattleFeverCount}；我方极限技就绪 {teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount}；敌方分散 {teamSituation.IsEnemySplit}");

            if ((teamSituation.IsEnemySplit || teamSituation.Enemy.LowHpCount >= 2 || teamSituation.Enemy.CrowdControlledCount >= 2)
                && engagement.Score >= 58f
                && risk.ThirdPartyPincerRisk < 74f
                && risk.EncirclementRisk < 76f
                && risk.RetreatRouteRisk < 78f)
            {
                AddCommand(
                    commands,
                    "tempo:early-pick-window",
                    BattlefieldCommandKind.FocusTarget,
                    "主团",
                    "前期抓落单窗口，压上去收人炒战意",
                    Math.Clamp(72f + teamSituation.Enemy.LowHpCount * 5f + teamSituation.Enemy.CrowdControlledCount * 4f, 0f, 94f),
                    78f,
                    6,
                    destination,
                    targetName,
                    "敌方分散/残血/被控，适合主动滚战意",
                    $"敌残血 {teamSituation.Enemy.LowHpCount}；敌被控 {teamSituation.Enemy.CrowdControlledCount}；敌分散 {teamSituation.IsEnemySplit}");
            }
        }

        if (IsFinalMinuteAllIn(timeSituation))
        {
            var leader = scoreSituation.RankedAlliances.FirstOrDefault(alliance => !alliance.IsLocalAlliance);
            var target = string.IsNullOrWhiteSpace(leader.Name) ? "最高价值点/第一名" : $"{leader.Name}/最高价值点";
            AddCommand(
                commands,
                "tempo:final-minute-all-in",
                BattlefieldCommandKind.AttackObjective,
                "主团",
                $"最后1分钟，all in {target}，贴脸抢点拖战局",
                96f,
                96f,
                6,
                anchor,
                target,
                "最后1分钟放弃次要收益，最高价值目标优先",
                $"剩余 {FormatDuration(timeSituation.MatchTimeRemainingSeconds)}；比分压力 {ResolveScorePressure(scoreSituation):0}");
        }
        else if (IsEndgameAllIn(timeSituation))
        {
            AddCommand(
                commands,
                "tempo:last-three-minutes-force-fight",
                BattlefieldCommandKind.Engage,
                "主团",
                "最后3分钟，能抢就抢，能开团就开团，搏分",
                Math.Clamp(72f + ResolveScorePressure(scoreSituation) * 0.22f, 0f, 94f),
                84f,
                7,
                anchor,
                "终局抢分",
                "终局阶段普通风险不阻止抢分开团",
                $"剩余 {FormatDuration(timeSituation.MatchTimeRemainingSeconds)}；比分压力 {ResolveScorePressure(scoreSituation):0}");
        }

        if (friendly.HasValue
            && friendly.Value.IsLeading
            && !IsEndgameAllIn(timeSituation)
            && (risk.ThirdPartyPincerRisk >= 70f || risk.EncirclementRisk >= 74f || risk.RetreatRouteRisk >= 76f))
        {
            AddCommand(
                commands,
                "tempo:leader-exit-crossfire",
                BattlefieldCommandKind.Disengage,
                "主团",
                "我们领先，别站两家中间，先拉出夹角等反打",
                Math.Clamp(66f + MathF.Max(risk.ThirdPartyPincerRisk, risk.EncirclementRisk) * 0.28f, 0f, 94f),
                Math.Clamp(72f + risk.ThirdPartyPincerRisk * 0.18f + risk.RetreatRouteRisk * 0.10f, 0f, 94f),
                7,
                anchor,
                "出夹角",
                "领先方深夹最容易把摸点分和战意送回去，先保证撤退路线，再等两家技能交完回头吃残血",
                $"我方领先 {friendly.Value.Score}/{friendly.Value.VictoryScore}；第三方 {risk.ThirdPartyPincerRisk:0}；被包 {risk.EncirclementRisk:0}；出路 {risk.RetreatRouteRisk:0}");
        }

        if (friendly.HasValue && friendly.Value.IsLeading && risk.OverallRisk >= 72f && risk.ThirdPartyPincerRisk >= 65f)
        {
            AddCommand(
                commands,
                "tempo:leading-pressure",
                BattlefieldCommandKind.PressureSide,
                "主团",
                "我们领先，控关键点，压追分家，不给他们白拿分",
                Math.Clamp(50f + risk.OverallRisk * 0.18f, 0f, 84f),
                50f,
                14,
                anchor,
                "领先压制",
                "我方领先，继续压关键点和追分家",
                $"我方分数 {friendly.Value.Score}；风险 {risk.OverallRisk:0}；时间 {FormatDuration(Math.Max(0, timeSituation.MatchTimeRemainingSeconds))}");
        }

        if (friendly.HasValue && friendly.Value.RankIndex == 3 && !IsFatalRisk(risk) && timeSituation.MatchTimeRemainingSeconds is > 0 and <= 240)
        {
            AddCommand(
                commands,
                "tempo:must-score",
                BattlefieldCommandKind.AttackObjective,
                "主团",
                "时间不多，必须抢分，别拖边缘团",
                Math.Clamp(58f + ResolveScorePressure(scoreSituation) * 0.35f, 0f, 92f),
                68f,
                12,
                anchor,
                "抢分",
                "末段落后，需要主动拿目标",
                $"排名 {friendly.Value.RankIndex}；剩余 {FormatDuration(timeSituation.MatchTimeRemainingSeconds)}；比分压力 {ResolveScorePressure(scoreSituation):0}");
        }

        if ((chatEvents.FriendlyKillsRecent >= 2 || engagement.KillSwing >= 2)
            && (risk.ThirdPartyPincerRisk >= 64f || risk.EncirclementRisk >= 68f || risk.RetreatRouteRisk >= 72f)
            && !IsEndgameAllIn(timeSituation))
        {
            AddCommand(
                commands,
                "tempo:stop-after-profit",
                BattlefieldCommandKind.Disengage,
                "主团",
                "这波已经赚了，收住出夹角，别把人头分送回去",
                Math.Clamp(58f + MathF.Max(risk.ThirdPartyPincerRisk, risk.RetreatRouteRisk) * 0.25f + chatEvents.FriendlyKillsRecent * 5f, 0f, 92f),
                Math.Clamp(66f + risk.EncirclementRisk * 0.20f, 0f, 92f),
                7,
                anchor,
                "收住",
                "打出收益后继续深追容易被两家反吃，先用宏明确告诉散人撤出夹角",
                $"我方击杀 {chatEvents.FriendlyKillsRecent}；击杀摆动 {engagement.KillSwing:+0;-0;0}；第三方 {risk.ThirdPartyPincerRisk:0}；被包 {risk.EncirclementRisk:0}；出路 {risk.RetreatRouteRisk:0}");
        }

        if (chatEvents.FriendlyKillsRecent >= 3 && risk.OverallRisk <= 66f && teamSituation.RespawnRhythm.EnemyDeadNow >= teamSituation.RespawnRhythm.FriendlyDeadNow)
        {
            AddCommand(
                commands,
                "tempo:push-after-kill",
                BattlefieldCommandKind.PressureSide,
                "主团",
                "刚打出击杀，往前压一波但别过深",
                Math.Clamp(52f + chatEvents.FriendlyKillsRecent * 7f - risk.RetreatRouteRisk * 0.12f, 0f, 86f),
                56f,
                8,
                anchor,
                "击杀窗口",
                "我方近期击杀形成节奏窗口",
                $"我方击杀 {chatEvents.FriendlyKillsRecent}；敌死亡 {teamSituation.RespawnRhythm.EnemyDeadNow}；出路 {risk.RetreatRouteRisk:0}");
        }
    }

    private static void AddThreeFactionTempoCommands(
        List<BattlefieldCommandSnapshot> commands,
        Vector3 anchor,
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldRiskAssessmentSnapshot risk,
        EngagementOpportunity engagement,
        BattlefieldAllianceScoreSnapshot? friendly)
    {
        var fatal = IsFatalFightState(risk, teamSituation);
        if (!fatal
            && timeSituation.HasMatchTime
            && timeSituation.MatchElapsedSeconds >= 0
            && timeSituation.MatchElapsedSeconds <= ResolveOpeningProbeWindowSeconds(scoreSituation.MapType)
            && risk.ThirdPartyPincerRisk < ResolveOpeningProbePincerLimit(scoreSituation.MapType)
            && risk.RetreatRouteRisk < ResolveOpeningProbeRetreatLimit(scoreSituation.MapType)
            && !IsMandatoryTempoObjectiveImminent(primary, timeSituation, scoreSituation.MapType)
            && teamSituation.EnemyMainGroupMovement.HasMainGroup)
        {
            AddCommand(
                commands,
                "tempo:opening-probe",
                BattlefieldCommandKind.Engage,
                "主团",
                "开局接一波试强度，后排留撤退线",
                Math.Clamp(68f + engagement.Score * 0.18f + teamSituation.Friendly.BattleHighTotalLevel * 0.35f, 0f, 92f),
                Math.Clamp(66f + (teamSituation.IsEnemySplit ? 8f : 0f) - risk.ThirdPartyPincerRisk * 0.08f, 0f, 88f),
                8,
                teamSituation.EnemyMainGroupMovement.CurrentCenter,
                "开局对冲",
                "早期可控对冲用于判断己方跟队、敌方指挥习惯和战意起势；不抢必争资源时才开，撤退线必须先留好",
                $"接团 {engagement.Score:0}；第三方 {risk.ThirdPartyPincerRisk:0}；出路 {risk.RetreatRouteRisk:0}；敌团 {teamSituation.EnemyMainGroupMovement.PlayerCount}");
        }

        var highestEnemyFlow = scoreSituation.Alliances
            .Where(alliance => !alliance.IsLocalAlliance)
            .OrderByDescending(alliance => alliance.ScoreDelta30s)
            .ThenByDescending(alliance => alliance.ScorePerSecond30s)
            .FirstOrDefault();
        var enemyFlowHigh = IsValidScoreAlliance(highestEnemyFlow)
            && IsEnemyScoreFlowHigh(highestEnemyFlow, scoreSituation.MapType);
        var friendlyFlowLow = friendly.HasValue && IsFriendlyScoreFlowLow(friendly.Value, scoreSituation.MapType);
        var friendlyBattleHighReady = IsFriendlyBattleHighReadyForTempo(teamSituation, scoreSituation.MapType);
        if (!fatal && friendly.HasValue && friendlyBattleHighReady && friendlyFlowLow && enemyFlowHigh)
        {
            var target = string.IsNullOrWhiteSpace(highestEnemyFlow.Name) ? "跳分家" : highestEnemyFlow.Name;
            var destinationName = primary.HasValue && !string.IsNullOrWhiteSpace(primary.Value.Name)
                ? primary.Value.Name
                : "下一目标点";
            var destination = primary.HasValue && IsMeaningfulPosition(primary.Value.Position)
                ? primary.Value.Position
                : teamSituation.EnemyMainGroupMovement.HasMainGroup ? teamSituation.EnemyMainGroupMovement.CurrentCenter : anchor;
            AddCommand(
                commands,
                "tempo:stop-farming-score",
                BattlefieldCommandKind.Rotate,
                "主团",
                $"转去 {destinationName} 压 {target}",
                Math.Clamp(70f + teamSituation.Friendly.BattleHighTotalLevel * 0.7f + highestEnemyFlow.ScoreDelta30s * 0.22f, 0f, 94f),
                Math.Clamp(64f + highestEnemyFlow.ScoreDelta30s * 0.28f, 0f, 92f),
                10,
                destination,
                destinationName,
                "拿散人提战意要有度；我方有战意但没跳分时，应转为抢点/压点，避免第三家满屏拿点",
                $"我方跳分 {friendly.Value.ScoreDelta30s}/30s；我方战意 {teamSituation.Friendly.BattleHighTotalLevel} 狂热 {teamSituation.Friendly.BattleFeverCount}；目标 {destinationName}；对面 {target} 跳分 {highestEnemyFlow.ScoreDelta30s}/30s");
        }

        if (!fatal
            && friendly.HasValue
            && friendly.Value.RankIndex == 3
            && timeSituation.MatchTimeRemainingSeconds is > 0 and <= 240
            && primary.HasValue
            && IsTempoScoreFirstObjective(primary.Value, scoreSituation.MapType)
            && !IsBattleHighGapHopelessForScoreFirst(teamSituation, scoreSituation.MapType))
        {
            AddCommand(
                commands,
                "tempo:third-place-score-first",
                BattlefieldCommandKind.AttackObjective,
                "主团",
                $"我们老三先补分，带到 {primary.Value.Name}，别替老二先手打工",
                Math.Clamp(78f + primary.Value.RewardScore * 0.16f + ResolveScorePressure(scoreSituation) * 0.20f, 0f, 96f),
                Math.Clamp(70f + (primary.Value.RemainingSeconds is > 0 and <= 35 ? 10f : 0f), 0f, 92f),
                8,
                primary.Value.Position,
                primary.Value.Name,
                "终局老三要先拿补分窗口；除非战意差距已经没法接，否则不要替老二先手送进老一脸上",
                $"我方第3 {friendly.Value.Score}/{friendly.Value.VictoryScore}；目标 {primary.Value.Name} 价值 {primary.Value.ScoreValue?.ToString() ?? "未知"}；战意差 {teamSituation.Friendly.BattleHighTotalLevel - teamSituation.Enemy.BattleHighTotalLevel:+0;-0;0}");
        }

        if (!fatal
            && primary.HasValue
            && CanLeaveExpiringTempoObjective(primary.Value, scoreSituation.MapType, timeSituation, risk))
        {
            var ownerText = string.IsNullOrWhiteSpace(primary.Value.OwnershipText) ? "当前目标" : primary.Value.OwnershipText;
            var remainingSeconds = primary.Value.RemainingSeconds.GetValueOrDefault();
            AddCommand(
                commands,
                $"tempo:leave-expiring:{primary.Value.ObjectiveId}",
                BattlefieldCommandKind.Rotate,
                "主团",
                $"{primary.Value.Name} 快跳完，别为尾分缠太久，提前带下一波位置",
                Math.Clamp(56f + Math.Max(0f, 40f - remainingSeconds) * 0.7f + risk.ScorePressure * 0.12f, 0f, 84f),
                Math.Clamp(54f + Math.Max(0f, 40f - remainingSeconds) * 0.8f, 0f, 86f),
                12,
                primary.Value.Position,
                ownerText,
                "低/中价值目标快结算时，继续缠斗容易错过下一波资源或被两家反吃；提前带位置比尾分硬换更稳",
                $"{primary.Value.Name} 剩余 {FormatDuration(remainingSeconds)}；价值 {primary.Value.ScoreValue}; 风险 {primary.Value.RiskScore:0}");
        }
    }

    private static int ResolveOpeningProbeWindowSeconds(FrontlineMapType mapType)
        => mapType switch
        {
            FrontlineMapType.FieldsOfHonor => 180,
            FrontlineMapType.BorderlandRuinsSecure => 210,
            FrontlineMapType.OnsalHakair => 225,
            FrontlineMapType.Vochester => 225,
            FrontlineMapType.SealRock => 240,
            _ => 210,
        };

    private static float ResolveOpeningProbePincerLimit(FrontlineMapType mapType)
        => mapType switch
        {
            FrontlineMapType.FieldsOfHonor => 66f,
            FrontlineMapType.BorderlandRuinsSecure => 70f,
            FrontlineMapType.SealRock => 74f,
            _ => 72f,
        };

    private static float ResolveOpeningProbeRetreatLimit(FrontlineMapType mapType)
        => mapType switch
        {
            FrontlineMapType.FieldsOfHonor => 72f,
            FrontlineMapType.SealRock => 80f,
            _ => 78f,
        };

    private static bool IsOpeningBattleHighFarmWindow(BattlefieldTimeSituationSnapshot timeSituation)
        => timeSituation.HasMatchTime && timeSituation.MatchElapsedSeconds is >= 0 and <= 240;

    private static bool IsSafeForwardFightWindow(
        EngagementOpportunity engagement,
        BattlefieldRiskAssessmentSnapshot risk,
        bool allowOpeningRelaxation = false)
    {
        var scoreLimit = allowOpeningRelaxation ? 56f : 60f;
        var localAdvantageLimit = allowOpeningRelaxation ? -1 : 0;
        var overallLimit = allowOpeningRelaxation ? 76f : 72f;
        var limitBreakLimit = allowOpeningRelaxation ? 82f : 78f;
        var pincerLimit = allowOpeningRelaxation ? 76f : 72f;
        var encirclementLimit = allowOpeningRelaxation ? 78f : 74f;
        var retreatLimit = allowOpeningRelaxation ? 80f : 76f;

        return engagement.Score >= scoreLimit
            && engagement.LocalAdvantage >= localAdvantageLimit
            && risk.OverallRisk <= overallLimit
            && risk.LimitBreakRisk <= limitBreakLimit
            && risk.ThirdPartyPincerRisk < pincerLimit
            && risk.EncirclementRisk < encirclementLimit
            && risk.RetreatRouteRisk < retreatLimit;
    }

    private static bool IsMandatoryTempoObjectiveImminent(
        BattlefieldObjectivePrioritySnapshot? primary,
        BattlefieldTimeSituationSnapshot timeSituation,
        FrontlineMapType mapType)
    {
        if (!primary.HasValue)
            return false;

        var objective = primary.Value;
        var highValue = IsHighValueObjective(objective)
            || objective.Category == BattlefieldMapObjectiveCategory.Ice && objective.ScoreValue is >= 100
            || objective.Category == BattlefieldMapObjectiveCategory.Ovoo && objective.RewardScore >= 78f
            || objective.Category == BattlefieldMapObjectiveCategory.StrategicPoint && objective.RewardScore >= 78f;
        if (!highValue)
            return false;

        if (objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested)
            return true;
        if (objective.State == BattlefieldMapObjectiveState.Warning && objective.RemainingSeconds is >= 0 and <= 70)
            return true;
        if (timeSituation.NextResourceSeconds is >= 0 and <= 45)
            return true;

        return mapType == FrontlineMapType.FieldsOfHonor
            && objective.Category == BattlefieldMapObjectiveCategory.Ice
            && objective.RemainingSeconds is >= 0 and <= 90;
    }

    private static bool IsEnemyScoreFlowHigh(BattlefieldAllianceScoreSnapshot alliance, FrontlineMapType mapType)
    {
        var deltaThreshold = mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => 24,
            FrontlineMapType.FieldsOfHonor => 30,
            FrontlineMapType.Vochester => 30,
            _ => 35,
        };
        var perSecondThreshold = mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => 0.8f,
            FrontlineMapType.FieldsOfHonor => 1.0f,
            _ => 1.2f,
        };

        return alliance.ScoreDelta30s >= deltaThreshold
            || alliance.ScorePerSecond30s >= perSecondThreshold;
    }

    private static bool IsFriendlyScoreFlowLow(BattlefieldAllianceScoreSnapshot alliance, FrontlineMapType mapType)
    {
        var deltaLimit = mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => 8,
            FrontlineMapType.FieldsOfHonor => 12,
            _ => 10,
        };
        var perSecondLimit = mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => 0.35f,
            FrontlineMapType.FieldsOfHonor => 0.55f,
            _ => 0.5f,
        };

        return alliance.ScoreDelta30s <= deltaLimit
            && alliance.ScorePerSecond30s <= perSecondLimit;
    }

    private static bool IsFriendlyBattleHighReadyForTempo(BattlefieldTeamSituationSnapshot teamSituation, FrontlineMapType mapType)
    {
        var battleHighThreshold = mapType switch
        {
            FrontlineMapType.FieldsOfHonor => 10,
            FrontlineMapType.BorderlandRuinsSecure => 10,
            _ => 12,
        };

        return teamSituation.Friendly.BattleHighTotalLevel >= battleHighThreshold
            || teamSituation.Friendly.BattleFeverCount > 0
            || teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount >= 2;
    }

    private static bool IsTempoScoreFirstObjective(BattlefieldObjectivePrioritySnapshot objective, FrontlineMapType mapType)
    {
        if (!IsTempoScoreObjectiveCategory(objective.Category))
            return false;
        if (!IsMeaningfulPosition(objective.Position))
            return false;
        if (objective.RiskScore >= 92f && !IsHighValueObjective(objective))
            return false;

        var minimumScoreValue = mapType switch
        {
            FrontlineMapType.FieldsOfHonor => 100,
            FrontlineMapType.OnsalHakair => 100,
            FrontlineMapType.SealRock => 80,
            FrontlineMapType.Vochester => 80,
            _ => 70,
        };
        var minimumReward = mapType switch
        {
            FrontlineMapType.FieldsOfHonor => 66f,
            FrontlineMapType.OnsalHakair => 66f,
            _ => 64f,
        };

        return objective.ScoreValue.GetValueOrDefault() >= minimumScoreValue
            || objective.RewardScore >= minimumReward
            || IsHighValueObjective(objective);
    }

    private static bool IsBattleHighGapHopelessForScoreFirst(BattlefieldTeamSituationSnapshot teamSituation, FrontlineMapType mapType)
    {
        var tolerance = mapType switch
        {
            FrontlineMapType.FieldsOfHonor => 18,
            FrontlineMapType.OnsalHakair => 16,
            _ => 14,
        };
        var enemyGap = teamSituation.Enemy.BattleHighTotalLevel - teamSituation.Friendly.BattleHighTotalLevel;

        return enemyGap > tolerance
            && teamSituation.Enemy.BattleFeverCount > teamSituation.Friendly.BattleFeverCount
            && teamSituation.LimitBreakThreats.FriendlyLikelyReadyCount == 0;
    }

    private static bool CanLeaveExpiringTempoObjective(
        BattlefieldObjectivePrioritySnapshot objective,
        FrontlineMapType mapType,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldRiskAssessmentSnapshot risk)
    {
        if (objective.State != BattlefieldMapObjectiveState.Controlled)
            return false;
        if (!objective.RemainingSeconds.HasValue || objective.RemainingSeconds.Value <= 0)
            return false;
        if (!IsTempoCaptureObjectiveCategory(objective.Category))
            return false;
        if (IsHighValueObjective(objective))
            return false;
        if (IsEndgameAllIn(timeSituation))
            return false;
        if (risk.ThirdPartyPincerRisk >= 84f || risk.RetreatRouteRisk >= 88f)
            return false;

        var remaining = objective.RemainingSeconds.Value;
        var leaveWindow = mapType switch
        {
            FrontlineMapType.SealRock => 40,
            FrontlineMapType.OnsalHakair => 34,
            FrontlineMapType.Vochester => 34,
            _ => 30,
        };
        if (remaining > leaveWindow)
            return false;

        var maxTailValue = mapType switch
        {
            FrontlineMapType.SealRock => 120,
            FrontlineMapType.OnsalHakair => 100,
            FrontlineMapType.Vochester => 100,
            _ => 80,
        };
        if (objective.ScoreValue.GetValueOrDefault() > maxTailValue || objective.RewardScore >= 78f)
            return false;

        var nextResourceSoon = timeSituation.NextResourceSeconds is >= 0 and <= 70;
        return nextResourceSoon || remaining <= Math.Max(18, leaveWindow / 2) || risk.ScorePressure >= 58f;
    }

    private static bool IsTempoScoreObjectiveCategory(BattlefieldMapObjectiveCategory category)
        => category is BattlefieldMapObjectiveCategory.Base
            or BattlefieldMapObjectiveCategory.Tomelith
            or BattlefieldMapObjectiveCategory.Ice
            or BattlefieldMapObjectiveCategory.Ovoo
            or BattlefieldMapObjectiveCategory.StrategicPoint;

    private static bool IsTempoCaptureObjectiveCategory(BattlefieldMapObjectiveCategory category)
        => category is BattlefieldMapObjectiveCategory.Base
            or BattlefieldMapObjectiveCategory.Tomelith
            or BattlefieldMapObjectiveCategory.Ovoo
            or BattlefieldMapObjectiveCategory.StrategicPoint;

    private static void AddCommand(
        List<BattlefieldCommandSnapshot> commands,
        string id,
        BattlefieldCommandKind kind,
        string scope,
        string commandText,
        float score,
        float urgency,
        int cooldownSeconds,
        Vector3 position,
        string targetName,
        string reasonText,
        string evidenceText)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(commandText))
            return;

        commands.Add(new BattlefieldCommandSnapshot(
            id,
            kind,
            scope,
            commandText,
            Math.Clamp(score, 0f, 100f),
            Math.Clamp(urgency, 0f, 100f),
            Math.Clamp(cooldownSeconds, 3, 30),
            position,
            targetName,
            reasonText,
            evidenceText));
    }

    private static float ResolveScorePressure(BattlefieldScoreSituationSnapshot scoreSituation)
    {
        var friendly = scoreSituation.FriendlyAlliance;
        if (!friendly.HasValue || scoreSituation.RankedAlliances.Length == 0)
            return 40f;

        var leader = scoreSituation.RankedAlliances[0];
        var gap = leader.Score - friendly.Value.Score;
        var pressure = friendly.Value.RankIndex switch
        {
            1 => 30f,
            2 => 48f,
            3 => 64f,
            _ => 45f
        };

        pressure += Math.Clamp(gap / 15f, 0f, 28f);
        if (scoreSituation.VictoryScore > 0 && leader.VictoryScore - leader.Score <= 180)
            pressure += 10f;
        return Math.Clamp(pressure, 0f, 100f);
    }

    private static KnowledgeDecisionContext BuildKnowledgeDecisionContext(
        FrontlineKnowledgeSnapshot knowledge,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldAnnouncementSituationSnapshot announcements)
    {
        var mapKnowledge = knowledge.CurrentMap;
        if (mapKnowledge == null)
            return new KnowledgeDecisionContext(null, 0, 0, string.Empty, false, false, false, false, Array.Empty<string>(), Array.Empty<string>(), false, false, string.Empty);

        var killScore = mapKnowledge.ScoreSources
            .Where(source => source.SourceName.Contains("击倒", StringComparison.Ordinal)
                || source.Tags.Any(tag => tag.Contains("击倒", StringComparison.Ordinal)))
            .Select(source => source.OwnScoreDelta.GetValueOrDefault())
            .DefaultIfEmpty(0)
            .Max();
        if (killScore <= 0)
        {
            killScore = mapKnowledge.ScoreSources
                .Select(source => source.OwnScoreDelta.GetValueOrDefault())
                .Where(value => value is > 0 and <= 20)
                .DefaultIfEmpty(0)
                .Max();
        }

        var teleportLeadSeconds = mapKnowledge.TeleportRules
            .Select(rule => rule.ActivationLeadSeconds)
            .DefaultIfEmpty(0)
            .Max();
        var locksAfterCapture = mapKnowledge.Rules.Any(rule => rule.Detail.Contains("不可被敌方夺回", StringComparison.Ordinal))
            || mapKnowledge.ObjectiveRules.Any(rule => rule.Detail.Contains("不可被敌方夺回", StringComparison.Ordinal));
        var canInterruptObjective = mapKnowledge.Rules.Any(rule =>
                rule.Detail.Contains("打断", StringComparison.Ordinal)
                && (rule.Detail.Contains("契约", StringComparison.Ordinal)
                    || rule.Detail.Contains("抢占", StringComparison.Ordinal)
                    || rule.Detail.Contains("摸点", StringComparison.Ordinal)))
            || mapKnowledge.DecisionHints.Any(hint =>
                hint.Recommendation.Contains("打断", StringComparison.Ordinal)
                || hint.Reason.Contains("打断", StringComparison.Ordinal));
        var macroIntentIds = knowledge.CommanderMacroIntents
            .Where(intent => !string.IsNullOrWhiteSpace(intent.Id))
            .Select(intent => intent.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var globalRuleIds = knowledge.GlobalRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Id))
            .Select(rule => rule.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hasInterruptTouchMacro = macroIntentIds.Contains("macro.intent.interrupt_touch", StringComparer.Ordinal);
        var hasCoverTouchMacro = macroIntentIds.Contains("macro.intent.cover_touch", StringComparer.Ordinal);
        var auroraActive = announcements.CurrentWeather == BattlefieldWeatherKind.Aurora
            && mapKnowledge.WeatherRules.Any(rule => rule.Name.Contains("极光", StringComparison.Ordinal));
        var snowActive = announcements.CurrentWeather == BattlefieldWeatherKind.Snow
            && mapKnowledge.WeatherRules.Any(rule => rule.Name.Contains("小雪", StringComparison.Ordinal));
        var summary = $"击杀分 {killScore}；阶段最低 {timeSituation.MapRuleMinimumObjectiveRank}；传送预热 {teleportLeadSeconds}；锁点 {locksAfterCapture}；可打断 {canInterruptObjective}";

        return new KnowledgeDecisionContext(
            mapKnowledge,
            killScore,
            teleportLeadSeconds,
            timeSituation.MapRuleMinimumObjectiveRank,
            locksAfterCapture,
            canInterruptObjective,
            hasInterruptTouchMacro,
            hasCoverTouchMacro,
            macroIntentIds,
            globalRuleIds,
            auroraActive,
            snowActive,
            summary);
    }

    private static KnowledgeObjectiveAdjustment ResolveKnowledgeObjectiveAdjustment(
        BattlefieldMapObjectiveSnapshot objective,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> allObjectives,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        KnowledgeDecisionContext knowledgeContext,
        float distance,
        int etaSeconds)
    {
        var mapKnowledge = knowledgeContext.MapKnowledge;
        if (mapKnowledge == null)
            return new KnowledgeObjectiveAdjustment(0f, 0f, 0f, 0f, string.Empty, string.Empty);

        float rewardBonus = 0f;
        float timingBonus = 0f;
        float priorityBonus = 0f;
        float riskAdjustment = 0f;
        string actionOverride = string.Empty;
        var evidence = new List<string>();
        var objectiveScore = objective.ScoreValue ?? ResolveKnowledgeObjectiveScoreValue(objective, mapKnowledge);
        var objectiveBeatsKills = knowledgeContext.PlayerKillScoreValue > 0
            && objectiveScore >= Math.Max(50, knowledgeContext.PlayerKillScoreValue * 6);
        var rankTier = ResolveRankTier(objective.RankName);
        var minimumRankTier = ResolveRankTier(knowledgeContext.PhaseMinimumObjectiveRank);

        if (minimumRankTier != NodeRank.Unknown
            && rankTier != NodeRank.Unknown
            && rankTier < minimumRankTier)
        {
            rewardBonus -= 8f;
            priorityBonus -= 14f;
            evidence.Add($"阶段最低 {knowledgeContext.PhaseMinimumObjectiveRank}，当前 {objective.RankName}");
            if (objective.State == BattlefieldMapObjectiveState.Warning)
                actionOverride = "低级点牵制";
        }

        var locationRule = ResolveActiveLocationRule(mapKnowledge, objective, timeSituation);
        if (locationRule.HasValue)
        {
            rewardBonus += 6f;
            timingBonus += objective.State == BattlefieldMapObjectiveState.Warning ? 10f : 6f;
            priorityBonus += 8f;
            evidence.Add($"点位规则 {locationRule.Value.Name}");
            if (string.IsNullOrWhiteSpace(actionOverride) && objective.State == BattlefieldMapObjectiveState.Warning)
                actionOverride = "提前压高价值点";
        }

        if (IsLockedObjectiveCategory(objective.Category, knowledgeContext)
            && objective.State is BattlefieldMapObjectiveState.Warning or BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested)
        {
            rewardBonus += objectiveBeatsKills ? 10f : 6f;
            timingBonus += 8f;
            priorityBonus += objectiveBeatsKills ? 12f : 6f;
            evidence.Add(objectiveBeatsKills ? "资源收益显著高于击杀" : "资源控制后不可夺回");
            if (knowledgeContext.HasInterruptTouchMacro
                && objective.EnemyAttackerCount > 0
                && objective.FriendlyAttackerCount <= objective.EnemyAttackerCount)
            {
                actionOverride = "先断摸点";
            }
            else if (knowledgeContext.HasCoverTouchMacro
                && (objective.FriendlyAttackerCount > 0
                    || objective.State == BattlefieldMapObjectiveState.Contested
                    || objective.State == BattlefieldMapObjectiveState.Active))
            {
                actionOverride = "前压掩护摸点";
            }
        }

        if (knowledgeContext.CanInterruptObjective
            && objective.EnemyAttackerCount > 0
            && objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested
            && objective.Category is BattlefieldMapObjectiveCategory.Ovoo or BattlefieldMapObjectiveCategory.StrategicPoint)
        {
            timingBonus += 6f;
            priorityBonus += 6f;
            evidence.Add("交互可打断");
            if (string.IsNullOrWhiteSpace(actionOverride))
                actionOverride = "先断摸点";
        }

        if (objective.Category == BattlefieldMapObjectiveCategory.Tomelith && objectiveBeatsKills)
        {
            rewardBonus += 4f;
            priorityBonus += 6f;
            evidence.Add("高等级石文收益压过击杀");
            if (objective.EnemyAttackerCount > 0 && string.IsNullOrWhiteSpace(actionOverride))
                actionOverride = "先断分再反抢";
        }

        if (knowledgeContext.AuroraActive && objective.Category == BattlefieldMapObjectiveCategory.StrategicPoint)
        {
            rewardBonus += 10f;
            timingBonus += 12f;
            priorityBonus += 10f;
            evidence.Add("极光抬高高等级点和极限技窗口");
            if (string.IsNullOrWhiteSpace(actionOverride) && objective.State != BattlefieldMapObjectiveState.Controlled)
                actionOverride = "极光期抢高价值点";
        }

        if (knowledgeContext.SnowActive && objective.Category == BattlefieldMapObjectiveCategory.StrategicPoint)
        {
            riskAdjustment += 6f;
            evidence.Add("小雪期先避雪人落点");
        }

        if (knowledgeContext.TeleportLeadSeconds > 0
            && objective.Category == BattlefieldMapObjectiveCategory.Monster
            && objective.State == BattlefieldMapObjectiveState.Warning
            && objective.RemainingSeconds is > 0 and <= 35)
        {
            timingBonus += 8f;
            priorityBonus += 6f;
            evidence.Add($"传送装置提前 {knowledgeContext.TeleportLeadSeconds} 秒可用");
            if (string.IsNullOrWhiteSpace(actionOverride))
                actionOverride = "提前压传送";
        }

        if (scoreSituation.FriendlyAlliance.HasValue
            && scoreSituation.FriendlyAlliance.Value.IsLeading
            && IsLockedObjectiveCategory(objective.Category, knowledgeContext)
            && objective.State == BattlefieldMapObjectiveState.Controlled)
        {
            priorityBonus += 4f;
            evidence.Add("领先时锁定收益更值钱");
        }

        var secureAdjustment = ResolveSecureBaseObjectiveAdjustment(objective, allObjectives, scoreSituation, knowledgeContext);
        rewardBonus += secureAdjustment.RewardBonus;
        timingBonus += secureAdjustment.TimingBonus;
        priorityBonus += secureAdjustment.PriorityBonus;
        riskAdjustment += secureAdjustment.RiskAdjustment;
        if (string.IsNullOrWhiteSpace(actionOverride) && !string.IsNullOrWhiteSpace(secureAdjustment.ActionOverride))
            actionOverride = secureAdjustment.ActionOverride;
        if (!string.IsNullOrWhiteSpace(secureAdjustment.EvidenceText))
            evidence.Add(secureAdjustment.EvidenceText);

        var shatterAdjustment = ResolveShatterIceObjectiveAdjustment(objective, allObjectives, teamSituation, scoreSituation, knowledgeContext);
        rewardBonus += shatterAdjustment.RewardBonus;
        timingBonus += shatterAdjustment.TimingBonus;
        priorityBonus += shatterAdjustment.PriorityBonus;
        riskAdjustment += shatterAdjustment.RiskAdjustment;
        if (string.IsNullOrWhiteSpace(actionOverride) && !string.IsNullOrWhiteSpace(shatterAdjustment.ActionOverride))
            actionOverride = shatterAdjustment.ActionOverride;
        if (!string.IsNullOrWhiteSpace(shatterAdjustment.EvidenceText))
            evidence.Add(shatterAdjustment.EvidenceText);

        var globalAdjustment = ResolveGlobalKnowledgeObjectiveAdjustment(objective, teamSituation, scoreSituation, timeSituation, knowledgeContext, distance, etaSeconds);
        rewardBonus += globalAdjustment.RewardBonus;
        timingBonus += globalAdjustment.TimingBonus;
        priorityBonus += globalAdjustment.PriorityBonus;
        riskAdjustment += globalAdjustment.RiskAdjustment;
        if (string.IsNullOrWhiteSpace(actionOverride) && !string.IsNullOrWhiteSpace(globalAdjustment.ActionOverride))
            actionOverride = globalAdjustment.ActionOverride;
        if (!string.IsNullOrWhiteSpace(globalAdjustment.EvidenceText))
            evidence.Add(globalAdjustment.EvidenceText);

        return new KnowledgeObjectiveAdjustment(
            rewardBonus,
            timingBonus,
            priorityBonus,
            riskAdjustment,
            actionOverride,
            string.Join("；", evidence));
    }

    private static KnowledgeObjectiveAdjustment ResolveSecureBaseObjectiveAdjustment(
        BattlefieldMapObjectiveSnapshot objective,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> allObjectives,
        BattlefieldScoreSituationSnapshot scoreSituation,
        KnowledgeDecisionContext knowledgeContext)
    {
        var mapKnowledge = knowledgeContext.MapKnowledge;
        if (mapKnowledge == null
            || mapKnowledge.MapType != FrontlineMapType.BorderlandRuinsSecure
            || objective.Category != BattlefieldMapObjectiveCategory.Base
            || !scoreSituation.FriendlyAlliance.HasValue
            || !scoreSituation.FriendlyAlliance.Value.Battalion.HasValue)
        {
            return new KnowledgeObjectiveAdjustment(0f, 0f, 0f, 0f, string.Empty, string.Empty);
        }

        var friendlyBattalion = scoreSituation.FriendlyAlliance.Value.Battalion.Value;
        var friendlyBaseCount = CountControlledBases(allObjectives, friendlyBattalion);
        var currentTick = ResolveBaseScorePerTick(mapKnowledge, friendlyBaseCount);
        var nextTick = ResolveBaseScorePerTick(mapKnowledge, friendlyBaseCount + 1);
        var previousTick = ResolveBaseScorePerTick(mapKnowledge, friendlyBaseCount - 1);
        var captureGain = Math.Max(0, nextTick - currentTick);
        var lossOnDrop = Math.Max(0, currentTick - previousTick);
        var isFriendlyOwned = ObjectiveOwnedByBattalion(objective, friendlyBattalion);
        float rewardBonus = 0f;
        float timingBonus = 0f;
        float priorityBonus = 0f;
        string actionOverride = string.Empty;
        var evidence = new List<string>(2);

        if (!isFriendlyOwned
            && objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested or BattlefieldMapObjectiveState.Controlled)
        {
            if (captureGain >= 4)
            {
                rewardBonus += 12f;
                timingBonus += objective.State == BattlefieldMapObjectiveState.Contested ? 8f : 4f;
                priorityBonus += 18f;
                evidence.Add($"据点数 {friendlyBaseCount}->{friendlyBaseCount + 1}，3秒收益 {currentTick}->{nextTick}");
                actionOverride = objective.State == BattlefieldMapObjectiveState.Controlled ? "先压点反抢" : "先拿分再接团";
            }
            else if (captureGain >= 2 && HasMacroIntent(knowledgeContext, "macro.intent.resource_refresh"))
            {
                rewardBonus += 3f;
                priorityBonus += 4f;
                evidence.Add($"据点持续收益 +{captureGain}/3秒");
            }
        }

        if (isFriendlyOwned && objective.State == BattlefieldMapObjectiveState.Controlled)
        {
            if (lossOnDrop >= 4)
            {
                rewardBonus += 8f;
                timingBonus += objective.EnemyAttackerCount > 0 ? 8f : 4f;
                priorityBonus += objective.EnemyAttackerCount > 0 ? 18f : 12f;
                evidence.Add($"掉点会从 {currentTick} 掉到 {previousTick}/3秒");
                if (HasMacroIntent(knowledgeContext, "macro.intent.defend_objective") || objective.EnemyAttackerCount > 0)
                    actionOverride = "先守点反打";
            }
            else if (objective.EnemyAttackerCount > 0 && HasMacroIntent(knowledgeContext, "macro.intent.defend_objective"))
            {
                priorityBonus += 6f;
                evidence.Add("宏意图要求留人防偷");
                actionOverride = "先守点";
            }
        }

        return new KnowledgeObjectiveAdjustment(
            rewardBonus,
            timingBonus,
            priorityBonus,
            0f,
            actionOverride,
            string.Join("；", evidence));
    }

    private static KnowledgeObjectiveAdjustment ResolveShatterIceObjectiveAdjustment(
        BattlefieldMapObjectiveSnapshot objective,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> allObjectives,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        KnowledgeDecisionContext knowledgeContext)
    {
        var mapKnowledge = knowledgeContext.MapKnowledge;
        if (mapKnowledge == null
            || mapKnowledge.MapType != FrontlineMapType.FieldsOfHonor
            || objective.Category != BattlefieldMapObjectiveCategory.Ice)
        {
            return new KnowledgeObjectiveAdjustment(0f, 0f, 0f, 0f, string.Empty, string.Empty);
        }

        var bigRule = ResolveDestructibleObjectiveRule(mapKnowledge, "map.shatter.destructible.big_ice");
        var smallRule = ResolveDestructibleObjectiveRule(mapKnowledge, "map.shatter.destructible.small_ice");
        var isBigIce = IsLargeIceObjective(objective, bigRule);
        var isSmallIce = !isBigIce && IsSmallIceObjective(objective, smallRule);
        var bigIceObjectives = allObjectives
            .Where(item => item.Category == BattlefieldMapObjectiveCategory.Ice && IsLargeIceObjective(item, bigRule))
            .ToArray();
        var activeBigIceCount = bigIceObjectives.Count(item => item.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested or BattlefieldMapObjectiveState.Unknown);
        var warningBigIceCount = bigIceObjectives.Count(item => item.State == BattlefieldMapObjectiveState.Warning);
        var bigIceWindowSeconds = activeBigIceCount > 0
            ? 0
            : bigIceObjectives
                .Where(item => item.State == BattlefieldMapObjectiveState.Warning && item.RemainingSeconds.HasValue)
                .Select(item => item.RemainingSeconds!.Value)
                .DefaultIfEmpty(int.MaxValue)
                .Min();
        var activeSmallIceCount = allObjectives.Count(item =>
            item.Category == BattlefieldMapObjectiveCategory.Ice
            && IsSmallIceObjective(item, smallRule)
            && item.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested or BattlefieldMapObjectiveState.Unknown);
        var friendlyContribution = objective.Contributors
            .Where(item => item.Relation is BattlefieldPlayerRelation.LocalPlayer or BattlefieldPlayerRelation.Friendly)
            .Sum(item => item.EstimatedContributionWeight);
        var enemyContribution = objective.Contributors
            .Where(item => item.Relation == BattlefieldPlayerRelation.Enemy)
            .Sum(item => item.EstimatedContributionWeight);
        var friendlyHasEnmity = objective.FriendlyAttackerCount > 0 || friendlyContribution >= 1.5f;
        var enemyHasEnmityLead = enemyContribution > friendlyContribution + 1f
            || objective.EnemyAttackerCount > objective.FriendlyAttackerCount + 1;
        var scoreValue = objective.ScoreValue.GetValueOrDefault();
        var killScore = Math.Max(1, knowledgeContext.PlayerKillScoreValue);
        var objectiveBeatsKills = scoreValue >= Math.Max(50, killScore * 6);
        var smallEfficiencyRatio = bigRule.MaxHp > 0 && smallRule.MaxHp > 0
            ? (float)smallRule.ScoreValue / smallRule.MaxHp / ((float)bigRule.ScoreValue / bigRule.MaxHp)
            : 1f;
        float rewardBonus = 0f;
        float timingBonus = 0f;
        float priorityBonus = 0f;
        float riskAdjustment = 0f;
        string actionOverride = string.Empty;
        var evidence = new List<string>(4);

        if (objectiveBeatsKills)
        {
            rewardBonus += 4f;
            evidence.Add("冰分高于空追击杀");
        }

        if (isBigIce)
        {
            rewardBonus += 10f;
            timingBonus += objective.State == BattlefieldMapObjectiveState.Warning ? 10f : 4f;
            priorityBonus += 8f + (HasMacroIntent(knowledgeContext, "macro.intent.big_ice_all_in") ? 6f : 0f);
            evidence.Add($"大冰窗口：活跃 {activeBigIceCount}，预告 {warningBigIceCount}");

            if (friendlyHasEnmity && objective.RecentHpLossPerSecond > 0f)
            {
                rewardBonus += 4f;
                priorityBonus += 6f;
                evidence.Add("我方已有打冰贡献，别断仇恨");
            }

            if (activeSmallIceCount >= 3)
                evidence.Add("小冰在场也不改大冰主团优先");

            if (HasMacroIntent(knowledgeContext, "macro.intent.big_ice_all_in")
                && objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested)
            {
                actionOverride = "主团先打冰";
            }
        }
        else if (isSmallIce)
        {
            rewardBonus += Math.Clamp((smallEfficiencyRatio - 1f) * 6f, 0f, 10f);
            evidence.Add($"小冰换分效率 {smallEfficiencyRatio:0.0}x");

            if (friendlyHasEnmity && objective.RecentHpLossPerSecond > 0f)
            {
                rewardBonus += 4f;
                priorityBonus += 6f;
                evidence.Add("我方已吃到小冰仇恨");
            }

            var bigIceSoon = activeBigIceCount > 0 || (warningBigIceCount > 0 && bigIceWindowSeconds <= 30);
            if (bigIceSoon)
            {
                timingBonus -= 4f;
                priorityBonus -= 10f;
                evidence.Add(activeBigIceCount > 0 ? "大冰已开，小冰不要过投" : $"大冰 {bigIceWindowSeconds} 秒内到，别贪小冰");
                if (!friendlyHasEnmity)
                    actionOverride = "顺手收冰别贪";
            }
            else if (scoreSituation.FriendlyAlliance.HasValue && scoreSituation.FriendlyAlliance.Value.RankIndex == 3)
            {
                priorityBonus += 4f;
                evidence.Add("落后时小冰可以快速追分");
            }

            if (enemyHasEnmityLead && !friendlyHasEnmity && bigIceSoon)
            {
                riskAdjustment += 6f;
                priorityBonus -= 8f;
                evidence.Add("敌方先挂到冰分，大冰窗前不值得硬换");
            }
        }

        return new KnowledgeObjectiveAdjustment(
            rewardBonus,
            timingBonus,
            priorityBonus,
            riskAdjustment,
            actionOverride,
            string.Join("；", evidence));
    }

    private static KnowledgeObjectiveAdjustment ResolveGlobalKnowledgeObjectiveAdjustment(
        BattlefieldMapObjectiveSnapshot objective,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        KnowledgeDecisionContext knowledgeContext,
        float distance,
        int etaSeconds)
    {
        float rewardBonus = 0f;
        float timingBonus = 0f;
        float priorityBonus = 0f;
        float riskAdjustment = 0f;
        var evidence = new List<string>(3);

        if (HasGlobalRule(knowledgeContext, "global.mount.dismount_lockout")
            && distance >= 150f
            && objective.State != BattlefieldMapObjectiveState.Warning)
        {
            var mountPenalty = distance >= 200f ? 8f : 4f;
            riskAdjustment += mountPenalty;
            evidence.Add($"远转点 {distance:0}y，被打一手就锁马 5 秒");
        }

        if (HasGlobalRule(knowledgeContext, "global.battle_high.limit_break")
            && IsTempoScoreObjectiveCategory(objective.Category))
        {
            var battleHighGap = teamSituation.Enemy.BattleHighTotalLevel - teamSituation.Friendly.BattleHighTotalLevel;
            if (battleHighGap >= 12 || teamSituation.Enemy.BattleFeverCount > teamSituation.Friendly.BattleFeverCount)
            {
                rewardBonus += 4f;
                priorityBonus += 6f;
                evidence.Add("敌方战意/LB 压力高，更该优先抢分目标");
            }
        }

        if (HasGlobalRule(knowledgeContext, "global.limit_break.rank_speed")
            && scoreSituation.FriendlyAlliance.HasValue
            && scoreSituation.FriendlyAlliance.Value.RankIndex == 1
            && objective.State == BattlefieldMapObjectiveState.Controlled
            && IsTempoCaptureObjectiveCategory(objective.Category))
        {
            priorityBonus += 3f;
            evidence.Add("第一名极限技回转更慢，先保稳定持续分");
        }

        if (HasMacroIntent(knowledgeContext, "macro.intent.resource_refresh")
            && timeSituation.NextResourceSeconds is > 0
            && timeSituation.NextResourceSeconds.Value <= etaSeconds + 8
            && !IsHighValueMapObjective(objective))
        {
            priorityBonus -= 4f;
            timingBonus -= 3f;
            evidence.Add("下一资源比赶这个低价值点更重要");
        }

        return new KnowledgeObjectiveAdjustment(
            rewardBonus,
            timingBonus,
            priorityBonus,
            riskAdjustment,
            string.Empty,
            string.Join("；", evidence));
    }

    private static FrontlineDestructibleObjectiveRuleSnapshot ResolveDestructibleObjectiveRule(
        FrontlineMapKnowledgeSnapshot mapKnowledge,
        string ruleId)
        => mapKnowledge.DestructibleObjectiveRules.FirstOrDefault(rule => string.Equals(rule.Id, ruleId, StringComparison.Ordinal));

    private static int CountControlledBases(
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        byte battalion)
        => objectives.Count(objective =>
            objective.Category == BattlefieldMapObjectiveCategory.Base
            && objective.State == BattlefieldMapObjectiveState.Controlled
            && ObjectiveOwnedByBattalion(objective, battalion));

    private static int ResolveBaseScorePerTick(FrontlineMapKnowledgeSnapshot mapKnowledge, int capturedBaseCount)
    {
        if (capturedBaseCount <= 0)
            return 0;

        return mapKnowledge.BaseCaptureScores
            .Where(rule => rule.CapturedBaseCount <= capturedBaseCount)
            .Select(rule => rule.ScorePerTick)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static bool ObjectiveOwnedByBattalion(BattlefieldMapObjectiveSnapshot objective, byte battalion)
        => OwnershipToBattalion(objective.Ownership) == battalion;

    private static bool IsLargeIceObjective(
        BattlefieldMapObjectiveSnapshot objective,
        FrontlineDestructibleObjectiveRuleSnapshot bigRule)
        => objective.Category == BattlefieldMapObjectiveCategory.Ice
            && (objective.ScoreValue is >= 150
                || objective.MaxHp.GetValueOrDefault() >= (uint)Math.Max(1000000, bigRule.MaxHp / 2));

    private static bool IsSmallIceObjective(
        BattlefieldMapObjectiveSnapshot objective,
        FrontlineDestructibleObjectiveRuleSnapshot smallRule)
        => objective.Category == BattlefieldMapObjectiveCategory.Ice
            && !IsLargeIceObjective(objective, default)
            && (objective.ScoreValue is > 0 and < 150
                || (objective.MaxHp.GetValueOrDefault() > 0u
                    && objective.MaxHp.GetValueOrDefault() < (uint)Math.Max(1000000, smallRule.MaxHp * 2)));

    private static bool IsHighValueMapObjective(BattlefieldMapObjectiveSnapshot objective)
        => objective.ScoreValue is >= 150
            || (!string.IsNullOrWhiteSpace(objective.RankName) && objective.RankName.Contains("S", StringComparison.OrdinalIgnoreCase));

    private static bool HasMacroIntent(KnowledgeDecisionContext knowledgeContext, string intentId)
        => knowledgeContext.MacroIntentIds.Contains(intentId, StringComparer.Ordinal);

    private static bool HasGlobalRule(KnowledgeDecisionContext knowledgeContext, string ruleId)
        => knowledgeContext.GlobalRuleIds.Contains(ruleId, StringComparer.Ordinal);

    private static int ResolveKnowledgeObjectiveScoreValue(
        BattlefieldMapObjectiveSnapshot objective,
        FrontlineMapKnowledgeSnapshot? mapKnowledge)
    {
        if (mapKnowledge == null)
            return 0;

        if (!string.IsNullOrWhiteSpace(objective.RankName))
        {
            var rankRule = mapKnowledge.ObjectiveRankScores
                .FirstOrDefault(rule => objective.RankName.Contains(rule.RankName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(rankRule.RankName))
                return rankRule.TotalScore;
        }

        return mapKnowledge.ObjectiveRules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.Name)
                && (objective.Name.Contains(rule.Name, StringComparison.OrdinalIgnoreCase)
                    || rule.Name.Contains(objective.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(rule => rule.TacticalScore.GetValueOrDefault())
            .DefaultIfEmpty(0)
            .Max();
    }

    private static FrontlineMapLocationRuleSnapshot? ResolveActiveLocationRule(
        FrontlineMapKnowledgeSnapshot mapKnowledge,
        BattlefieldMapObjectiveSnapshot objective,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (string.IsNullOrWhiteSpace(objective.LocationId))
            return null;

        return mapKnowledge.LocationRules
            .Where(rule => rule.LocationIds.Any(id => string.Equals(id, objective.LocationId, StringComparison.OrdinalIgnoreCase)))
            .Where(rule => IsKnowledgeWindowActive(rule.StartElapsedSeconds, rule.EndElapsedSeconds, timeSituation.MatchElapsedSeconds, timeSituation.HasMatchTime))
            .Where(rule => string.IsNullOrWhiteSpace(rule.MinimumObjectiveRank)
                || RankMeetsMinimum(objective.RankName, rule.MinimumObjectiveRank))
            .Select(rule => (FrontlineMapLocationRuleSnapshot?)rule)
            .FirstOrDefault();
    }

    private static bool IsKnowledgeWindowActive(int? startElapsedSeconds, int? endElapsedSeconds, int elapsedSeconds, bool hasMatchTime)
    {
        if (!hasMatchTime)
            return true;

        if (startElapsedSeconds.HasValue && elapsedSeconds < startElapsedSeconds.Value)
            return false;
        if (endElapsedSeconds.HasValue && elapsedSeconds >= endElapsedSeconds.Value)
            return false;
        return true;
    }

    private static NodeRank ResolveRankTier(string rankName)
    {
        if (string.IsNullOrWhiteSpace(rankName))
            return NodeRank.Unknown;
        if (rankName.Contains("S", StringComparison.OrdinalIgnoreCase))
            return NodeRank.S;
        if (rankName.Contains("A", StringComparison.OrdinalIgnoreCase))
            return NodeRank.A;
        if (rankName.Contains("B", StringComparison.OrdinalIgnoreCase))
            return NodeRank.B;
        return NodeRank.Unknown;
    }

    private static bool RankMeetsMinimum(string rankName, string minimumRankName)
    {
        var rank = ResolveRankTier(rankName);
        var minimum = ResolveRankTier(minimumRankName);
        if (minimum == NodeRank.Unknown || rank == NodeRank.Unknown)
            return true;
        return rank >= minimum;
    }

    private static bool IsLockedObjectiveCategory(BattlefieldMapObjectiveCategory category, KnowledgeDecisionContext knowledgeContext)
        => knowledgeContext.ObjectiveLocksAfterCapture
            && category is BattlefieldMapObjectiveCategory.Ovoo or BattlefieldMapObjectiveCategory.StrategicPoint;

    private static string BuildEvidence(
        BattlefieldMapObjectiveSnapshot objective,
        float reward,
        float timing,
        float distanceScore,
        float pressure,
        float teamAdvantage,
        float terrain,
        float risk,
        float distance,
        int etaSeconds,
        ObjectivePositioningAssessment positioning,
        string announcementEvidence,
        string knowledgeEvidence)
    {
        var parts = new List<string>
        {
            $"收益 {reward:0}",
            $"时机 {timing:0}",
            $"距离 {distance:0}y/预计 {FormatDuration(etaSeconds)}",
            $"距离分 {distanceScore:0}",
            $"压力 {pressure:0}",
            $"人数/极限技 {teamAdvantage:0}",
            $"地形 {terrain:0}",
            $"风险 {risk:0}"
        };

        if (objective.ScoreValue.HasValue)
            parts.Add($"价值 {objective.ScoreValue.Value}");
        if (!string.IsNullOrWhiteSpace(objective.RankName))
            parts.Add($"等级 {objective.RankName}");
        if (objective.RemainingSeconds.HasValue)
            parts.Add($"剩余 {FormatDuration(objective.RemainingSeconds.Value)}");
        if (positioning.LongTravelPenalty > 0f || positioning.CrossfirePenalty > 0f || positioning.EnemyDoorstepPenalty > 0f || positioning.RouteBlockPenalty > 0f)
            parts.Add($"站位 自家侧{positioning.HomeSideScore:0}/远点{positioning.LongTravelPenalty:0}/两家中间{positioning.CrossfirePenalty:0}/门口{positioning.EnemyDoorstepPenalty:0}/挡团{positioning.RouteBlockPenalty:0}");
        if (positioning.ShouldHoldInstead)
            parts.Add("结论 不值得带主团硬进");
        if (!string.IsNullOrWhiteSpace(announcementEvidence))
            parts.Add(announcementEvidence);
        if (!string.IsNullOrWhiteSpace(knowledgeEvidence))
            parts.Add(knowledgeEvidence);

        return string.Join("；", parts);
    }

    private static float RankScore(string rankName, float b, float a, float s)
    {
        if (rankName.Contains("S", StringComparison.OrdinalIgnoreCase))
            return s;
        if (rankName.Contains("A", StringComparison.OrdinalIgnoreCase))
            return a;
        if (rankName.Contains("B", StringComparison.OrdinalIgnoreCase))
            return b;
        return (a + b) * 0.5f;
    }

    private static string CategoryText(BattlefieldMapObjectiveCategory category)
        => category switch
        {
            BattlefieldMapObjectiveCategory.Base => "据点",
            BattlefieldMapObjectiveCategory.Tomelith => "石文",
            BattlefieldMapObjectiveCategory.Ice => "冰",
            BattlefieldMapObjectiveCategory.Ovoo => "无垢",
            BattlefieldMapObjectiveCategory.StrategicPoint => "战略目标",
            BattlefieldMapObjectiveCategory.Monster => "机制目标",
            _ => "目标",
        };

    private static string MapObjectiveStateText(BattlefieldMapObjectiveState state)
        => state switch
        {
            BattlefieldMapObjectiveState.Warning => "预告",
            BattlefieldMapObjectiveState.Active => "可争夺",
            BattlefieldMapObjectiveState.Contested => "争夺中",
            BattlefieldMapObjectiveState.Controlled => "已占领",
            BattlefieldMapObjectiveState.Inactive => "未激活",
            _ => "未知",
        };

    private static string FormatOptionalSeconds(int? seconds)
        => seconds.HasValue ? FormatDuration(Math.Max(0, seconds.Value)) : "未知";

    private static string FormatHpPercent(float hpPercent)
        => hpPercent > 0f ? $"{hpPercent:0}%" : "未知";

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static int EstimateEtaSeconds(float distance, float yalmsPerSecond)
        => (int)MathF.Ceiling(Math.Max(0f, distance) / Math.Max(0.1f, yalmsPerSecond));

    private static string FormatDuration(int seconds)
        => $"{seconds / 60:D2}:{seconds % 60:D2}";

    private readonly record struct AnnouncementDecisionContext(
        bool HasRecentObjective,
        BattlefieldAnnouncementSnapshot Objective,
        BattlefieldMapObjectiveCategory ObjectiveCategory,
        string[] MatchedObjectiveIds,
        bool HasRecentWeather,
        BattlefieldAnnouncementSnapshot Weather)
    {
        public bool MatchesObjective(BattlefieldMapObjectiveSnapshot objective)
            => HasRecentObjective && MatchedObjectiveIds.Contains(objective.Id, StringComparer.Ordinal);
    }

    private readonly record struct AnnouncementObjectiveModifier(
        float PriorityBonus,
        float TimingBonus,
        float PressureBonus,
        float RiskAdjustment,
        string EvidenceText);

    private readonly record struct AnnouncementRiskModifier(
        float ObjectiveRiskBonus,
        float TerrainRiskBonus,
        float LimitBreakRiskBonus,
        string EvidenceText);

    private readonly record struct KnowledgeDecisionContext(
        FrontlineMapKnowledgeSnapshot? MapKnowledge,
        int PlayerKillScoreValue,
        int TeleportLeadSeconds,
        string PhaseMinimumObjectiveRank,
        bool ObjectiveLocksAfterCapture,
        bool CanInterruptObjective,
        bool HasInterruptTouchMacro,
        bool HasCoverTouchMacro,
        string[] MacroIntentIds,
        string[] GlobalRuleIds,
        bool AuroraActive,
        bool SnowActive,
        string SummaryText);

    private readonly record struct KnowledgeObjectiveAdjustment(
        float RewardBonus,
        float TimingBonus,
        float PriorityBonus,
        float RiskAdjustment,
        string ActionOverride,
        string EvidenceText);

    private readonly record struct CommandIssueState(
        string CommandId,
        string FamilyKey,
        BattlefieldCommandKind Kind,
        string Scope,
        string CommandText,
        float Priority,
        long IssuedAtTicks,
        int Sequence);

    private readonly record struct ActionHoldState(
        string ActionId,
        string ActionKey,
        BattlefieldActionType ActionType,
        long StartedAtTicks,
        long HoldUntilTicks,
        float Priority,
        string Text);

    private readonly record struct MicroFocusTarget(
        string TargetId,
        string Name,
        Vector3 Position,
        float HpPercent,
        string JobName,
        bool IsCrowdControlled,
        bool IsExecutable,
        bool IsGuarding,
        float Score,
        string EvidenceText);

    private readonly record struct MarkedEnemyTarget(
        BattlefieldTargetMarkerSnapshot Marker,
        BattlefieldPlayerSnapshot Player,
        float MarkerPriorityBonus,
        float Score);

    private readonly record struct KiteReengagePlan(
        bool ShouldKite,
        string Id,
        BattlefieldCommandKind Kind,
        string CommandText,
        float Score,
        float Urgency,
        Vector3 Position,
        string TargetName,
        string ReasonText,
        string EvidenceText);

    private readonly record struct EngagementOpportunity(
        float Score,
        int FriendlyLocalCount,
        int EnemyLocalCount,
        int LocalAdvantage,
        int KillSwing,
        int VulnerableEnemyCount,
        int FriendlyToolCount,
        bool EnemyEngaging,
        bool HasBurstWindow,
        bool CanTakeFight,
        bool ShouldCounterEngage,
        bool CanPush,
        string EvidenceText);

    private readonly record struct ObjectiveEnemyAnchor(
        Vector3 Center,
        int Weight);

    private readonly record struct ObjectivePositioningAssessment(
        float HomeSideScore,
        float EnemyDoorstepPenalty,
        float CrossfirePenalty,
        float RouteBlockPenalty,
        float LongTravelPenalty,
        bool ShouldHoldInstead);

    private readonly record struct StrategicFightPlan(
        bool IsAvailable,
        byte? TargetBattalion,
        string TargetName,
        string GoalText,
        string FightStyleText,
        string EvidenceText,
        float Urgency,
        bool MustAttackLeader,
        bool ProtectLead,
        bool Endgame,
        bool AggressiveEndgame,
        Vector3 TargetPosition);

    private readonly record struct StrategicTargetCandidate(
        BattlefieldAllianceScoreSnapshot Alliance,
        float ThreatScore,
        int RemainingToWin,
        bool IsFakeThird,
        bool IsTrueWeakThird,
        string EvidenceText);

    private readonly record struct CountdownWindow(
        bool CanCountdown,
        bool HasMainClusterDistance,
        float MainClusterDistance,
        int EnemySpentSignals,
        int EnemyReadyThreats,
        int EnemyLikelyReadyThreats,
        string EvidenceText);
}
