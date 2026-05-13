using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ai02;

public static class TacticalRiskAssessmentService
{
    private const float OverallRiskWeightTotal = 1.13f;

    public static BattlefieldRiskAssessmentSnapshot Build(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldChatEventSituationSnapshot chatEvents,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        float scorePressure,
        float announcementObjectiveRiskBonus = 0f,
        float announcementTerrainRiskBonus = 0f,
        float announcementLimitBreakRiskBonus = 0f,
        string announcementEvidenceText = "")
    {
        var friendlyAlive = Math.Max(0, teamSituation.Friendly.AliveCount);
        var rawEnemyAlive = Math.Max(0, teamSituation.Enemy.AliveCount);
        var enemyAlive = ResolveComparableEnemyCount(teamSituation, friendlyAlive, rawEnemyAlive);
        var combatRisk = Math.Clamp(50f + (enemyAlive - friendlyAlive) * 6f + teamSituation.Enemy.CastingCount * 2f - teamSituation.Enemy.CrowdControlledCount * 3f, 0f, 100f);
        var numberDisadvantageRisk = ResolveNumberDisadvantageRisk(friendlyAlive, enemyAlive, chatEvents);

        var mapRisk = 0f;
        if (mapTactics.IsAvailable)
        {
            var topZoneRisk = mapTactics.TopZones.Select(zone => zone.TotalRisk).DefaultIfEmpty(0f).Max();
            var routeRisk = mapTactics.Routes.Select(route => route.TotalRisk).DefaultIfEmpty(0f).Max();
            var heatRisk = mapTactics.HeatPoints.Select(point => point.Intensity).DefaultIfEmpty(0f).Max();
            var passabilityPressure = Math.Min(18f, mapTactics.MandatoryChokeCount * 2.5f + mapTactics.OneWayPassageCount * 1.5f);
            mapRisk = Math.Clamp(topZoneRisk * 0.50f + routeRisk * 0.18f + heatRisk * 0.24f + passabilityPressure * 0.08f, 0f, 100f);
        }

        var objectiveRisk = objectives
            .Where(IsObjectiveActionable)
            .Select(objective => Math.Clamp(35f + objective.EnemyAttackerCount * 8f - objective.FriendlyAttackerCount * 4f + (objective.State == BattlefieldMapObjectiveState.Contested ? 22f : 0f), 0f, 100f))
            .DefaultIfEmpty(0f)
            .Max();
        var chatObjectiveRisk = ResolveChatObjectiveRiskModifier(chatEvents);
        objectiveRisk = Math.Clamp(objectiveRisk + chatObjectiveRisk.ObjectiveRiskBonus + announcementObjectiveRiskBonus, 0f, 100f);
        combatRisk = Math.Clamp(combatRisk + chatObjectiveRisk.CombatRiskBonus, 0f, 100f);

        var limitBreakRisk = Math.Clamp(teamSituation.LimitBreakThreats.EnemyHighThreatCount * 24f + teamSituation.LimitBreakThreats.EnemyLikelyReadyCount * 12f + announcementLimitBreakRiskBonus, 0f, 100f);
        var skillThreatRisk = ResolveSkillThreatRisk(teamSituation.KeySkillThreats);
        var battleHighRisk = Math.Clamp(teamSituation.Enemy.BattleHighTotalLevel * 4f + teamSituation.Enemy.BattleFeverCount * 15f, 0f, 100f);
        var snowBlessingRisk = ResolveSnowBlessingRiskModifier(teamSituation);
        combatRisk = Math.Clamp(combatRisk + snowBlessingRisk.CombatRiskBonus, 0f, 100f);
        var respawnRisk = ResolveRespawnRisk(teamSituation.RespawnRhythm);
        var flankRisk = ResolveFlankRisk(teamSituation, mapTactics, chatEvents);
        flankRisk = Math.Clamp(flankRisk + chatObjectiveRisk.FlankRiskBonus, 0f, 100f);
        var enemyDirectionRisk = ResolveEnemyMainGroupDirectionRisk(teamSituation, mapTactics);
        var terrainRisk = Math.Clamp(ResolveTerrainRisk(mapTactics) + announcementTerrainRiskBonus + snowBlessingRisk.TerrainRiskBonus, 0f, 100f);
        var retreatRouteRisk = ResolveRetreatRouteRisk(mapTactics, combatRisk, flankRisk, enemyDirectionRisk);
        var encirclementRisk = ResolveEncirclementRisk(teamSituation, mapTactics, numberDisadvantageRisk, flankRisk, enemyDirectionRisk, retreatRouteRisk);
        var advancedTactics = teamSituation.AdvancedTactics;
        var ambushRisk = Math.Clamp(advancedTactics.AmbushRisk, 0f, 100f);
        var cohesionRisk = Math.Clamp(advancedTactics.CohesionRisk, 0f, 100f);
        var highGroundDropRisk = Math.Clamp(advancedTactics.HighGroundDropRisk, 0f, 100f);
        var thirdPartyPincerRisk = Math.Clamp(advancedTactics.ThirdPartyPincerRisk, 0f, 100f);
        var coordinatedSquadRisk = Math.Clamp(advancedTactics.CoordinatedSquadRisk, 0f, 100f);
        var chokeBlockRisk = Math.Clamp(advancedTactics.ChokeBlockRisk, 0f, 100f);
        flankRisk = Math.Clamp(MathF.Max(flankRisk, thirdPartyPincerRisk * 0.86f), 0f, 100f);
        terrainRisk = Math.Clamp(MathF.Max(terrainRisk, highGroundDropRisk * 0.72f), 0f, 100f);
        retreatRouteRisk = Math.Clamp(MathF.Max(retreatRouteRisk, chokeBlockRisk * 0.78f + ambushRisk * 0.18f), 0f, 100f);
        encirclementRisk = Math.Clamp(MathF.Max(encirclementRisk, thirdPartyPincerRisk * 0.78f + cohesionRisk * 0.16f + ambushRisk * 0.12f), 0f, 100f);

        if (chatEvents.FriendlyDeathsRecent >= 2)
            combatRisk = Math.Clamp(combatRisk + chatEvents.FriendlyDeathsRecent * 6f, 0f, 100f);
        if (chatEvents.FriendlyKillsRecent >= 2)
            combatRisk = Math.Clamp(combatRisk - chatEvents.FriendlyKillsRecent * 4f, 0f, 100f);
        var frameEventRisk = ResolveFrameEventRisk(playerFrameEvents);
        var friendlyControlPressure = Math.Max(0, playerFrameEvents.FriendlyControlledRecent - 2);
        var enemyTargetPressure = Math.Max(0, playerFrameEvents.EnemyTargetingFriendlyRecent - 3);
        combatRisk = Math.Clamp(combatRisk + playerFrameEvents.FriendlyDeathsRecent * 9f - playerFrameEvents.EnemyDeathsRecent * 6f + enemyTargetPressure * 2f, 0f, 100f);
        skillThreatRisk = Math.Clamp(skillThreatRisk + friendlyControlPressure * 6f - playerFrameEvents.EnemyControlledRecent * 5f, 0f, 100f);

        var weightedOverallRisk =
            combatRisk * 0.08f
            + numberDisadvantageRisk * 0.08f
            + flankRisk * 0.08f
            + respawnRisk * 0.08f
            + enemyDirectionRisk * 0.07f
            + terrainRisk * 0.06f
            + retreatRouteRisk * 0.06f
            + encirclementRisk * 0.11f
            + objectiveRisk * 0.03f
            + limitBreakRisk * 0.04f
            + skillThreatRisk * 0.06f
            + battleHighRisk * 0.03f
            + ambushRisk * 0.07f
            + cohesionRisk * 0.04f
            + highGroundDropRisk * 0.04f
            + thirdPartyPincerRisk * 0.08f
            + coordinatedSquadRisk * 0.03f
            + chokeBlockRisk * 0.04f
            + frameEventRisk * 0.05f;
        var overall = Math.Clamp(weightedOverallRisk / OverallRiskWeightTotal, 0f, 100f);

        var level = overall >= 78f ? "critical" : overall >= 62f ? "high" : overall >= 42f ? "medium" : "low";
        mapRisk = Math.Clamp(terrainRisk * 0.35f + retreatRouteRisk * 0.35f + flankRisk * 0.30f, 0f, 100f);

        var summaryParts = new List<string>
        {
            $"risk {level} {overall:0}",
            $"flank {flankRisk:0}",
            $"numbers {numberDisadvantageRisk:0}",
            $"respawn {respawnRisk:0}",
            $"direction {enemyDirectionRisk:0}",
            $"terrain {terrainRisk:0}",
            $"retreat {retreatRouteRisk:0}",
            $"encircle {encirclementRisk:0}",
            $"ambush {ambushRisk:0}",
            $"cohesion {cohesionRisk:0}",
            $"third-party {thirdPartyPincerRisk:0}",
            $"choke {chokeBlockRisk:0}",
            $"skills {skillThreatRisk:0}",
            $"frame {frameEventRisk:0}"
        };
        if (!string.IsNullOrWhiteSpace(announcementEvidenceText))
            summaryParts.Add($"announcement {announcementEvidenceText}");
        if (!string.IsNullOrWhiteSpace(chatObjectiveRisk.EvidenceText))
            summaryParts.Add(chatObjectiveRisk.EvidenceText);
        if (!string.IsNullOrWhiteSpace(snowBlessingRisk.EvidenceText))
            summaryParts.Add(snowBlessingRisk.EvidenceText);

        return new BattlefieldRiskAssessmentSnapshot
        {
            OverallRisk = overall,
            CombatRisk = combatRisk,
            FrameEventRisk = frameEventRisk,
            MapRisk = mapRisk,
            ObjectiveRisk = objectiveRisk,
            LimitBreakRisk = limitBreakRisk,
            SkillThreatRisk = skillThreatRisk,
            BattleHighRisk = battleHighRisk,
            RespawnRisk = respawnRisk,
            NumberDisadvantageRisk = numberDisadvantageRisk,
            FlankRisk = flankRisk,
            EnemyMainGroupDirectionRisk = enemyDirectionRisk,
            TerrainRisk = terrainRisk,
            RetreatRouteRisk = retreatRouteRisk,
            EncirclementRisk = encirclementRisk,
            AmbushRisk = ambushRisk,
            CohesionRisk = cohesionRisk,
            HighGroundDropRisk = highGroundDropRisk,
            ThirdPartyPincerRisk = thirdPartyPincerRisk,
            CoordinatedSquadRisk = coordinatedSquadRisk,
            ChokeBlockRisk = chokeBlockRisk,
            ScorePressure = scorePressure,
            RiskLevel = level,
            SummaryText = string.Join("; ", summaryParts)
        };
    }

    public static int ResolveComparableEnemyCount(
        BattlefieldTeamSituationSnapshot teamSituation,
        int friendlyAlive,
        int rawEnemyAlive)
    {
        var localEnemy = teamSituation.Enemy.NearCount + teamSituation.Enemy.MidCount;
        if (localEnemy > 0)
            return Math.Clamp(localEnemy, 0, Math.Max(rawEnemyAlive, localEnemy));

        var mainEnemy = ResolveEnemyMainGroupCount(teamSituation);
        if (mainEnemy > 0)
            return mainEnemy;

        var allianceAlive = Math.Max(
            teamSituation.EnemyAlliance1?.AliveCount ?? 0,
            teamSituation.EnemyAlliance2?.AliveCount ?? 0);
        if (allianceAlive > 0)
            return allianceAlive;

        if (friendlyAlive > 0 && rawEnemyAlive > friendlyAlive * 1.35f)
            return Math.Max(1, (int)MathF.Ceiling(rawEnemyAlive * 0.5f));

        return rawEnemyAlive;
    }

    public static bool IsFatalRisk(BattlefieldRiskAssessmentSnapshot risk)
        => (risk.ThirdPartyPincerRisk >= 84f && risk.EncirclementRisk >= 80f)
            || (risk.AmbushRisk >= 88f && risk.LimitBreakRisk >= 80f && risk.BattleHighRisk >= 68f)
            || (risk.NumberDisadvantageRisk >= 90f && risk.RespawnRisk >= 82f && risk.CombatRisk >= 82f)
            || (risk.OverallRisk >= 94f && risk.EncirclementRisk >= 86f);

    public static bool IsFatalFightState(BattlefieldRiskAssessmentSnapshot risk, BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (IsFatalRisk(risk))
            return true;

        var friendlyAlive = Math.Max(0, teamSituation.Friendly.AliveCount);
        var enemyComparable = ResolveComparableEnemyCount(teamSituation, friendlyAlive, Math.Max(0, teamSituation.Enemy.AliveCount));
        var manyDead = teamSituation.RespawnRhythm.FriendlyDeadNow >= 7
            || teamSituation.RespawnRhythm.FriendlyDeadNow >= teamSituation.RespawnRhythm.EnemyDeadNow + 5;
        var halfLowHp = friendlyAlive > 0
            && teamSituation.Friendly.LowHpCount >= Math.Max(5, friendlyAlive / 2)
            && teamSituation.RespawnRhythm.FriendlyDeadNow >= 3;
        var outnumberedHard = friendlyAlive > 0
            && enemyComparable >= friendlyAlive * 1.65f
            && risk.NumberDisadvantageRisk >= 86f;

        return (manyDead || halfLowHp || outnumberedHard)
            && risk.EncirclementRisk >= 74f
            && risk.CombatRisk >= 74f;
    }

    private static bool IsObjectiveActionable(BattlefieldMapObjectiveSnapshot objective)
        => objective.State is BattlefieldMapObjectiveState.Warning
            or BattlefieldMapObjectiveState.Active
            or BattlefieldMapObjectiveState.Contested
            or BattlefieldMapObjectiveState.Controlled
            or BattlefieldMapObjectiveState.Unknown;

    private static float ResolveNumberDisadvantageRisk(
        int friendlyAlive,
        int enemyAlive,
        BattlefieldChatEventSituationSnapshot chatEvents)
    {
        if (friendlyAlive == 0 && enemyAlive == 0)
            return 40f;

        var risk = 38f + (enemyAlive - friendlyAlive) * 8f;
        if (friendlyAlive > 0 && enemyAlive >= friendlyAlive * 1.5f)
            risk += 16f;
        if (chatEvents.FriendlyDeathsRecent > chatEvents.FriendlyKillsRecent)
            risk += (chatEvents.FriendlyDeathsRecent - chatEvents.FriendlyKillsRecent) * 6f;
        if (chatEvents.FriendlyKillsRecent > chatEvents.FriendlyDeathsRecent)
            risk -= (chatEvents.FriendlyKillsRecent - chatEvents.FriendlyDeathsRecent) * 4f;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static ChatObjectiveRiskModifier ResolveChatObjectiveRiskModifier(BattlefieldChatEventSituationSnapshot chatEvents)
    {
        if (!chatEvents.IsAvailable || chatEvents.ObjectiveEventsRecent <= 0)
            return default;

        var objectiveRisk = Math.Min(14f, chatEvents.ObjectiveEventsRecent * 3.5f);
        var combatRisk = 0f;
        var flankRisk = 0f;
        var parts = new List<string>(2);

        if (chatEvents.LatestObjectiveEvent.HasValue && chatEvents.LatestObjectiveEvent.Value.AgeMs <= 30000)
        {
            var item = chatEvents.LatestObjectiveEvent.Value;
            var targetName = BuildChatObjectiveTargetName(item);
            switch (item.Kind)
            {
                case BattlefieldChatEventKind.ObjectiveContested:
                    objectiveRisk += 12f;
                    combatRisk += 5f;
                    flankRisk += 4f;
                    parts.Add($"{targetName} contested");
                    break;
                case BattlefieldChatEventKind.ObjectiveLost:
                    objectiveRisk += item.ActorSide == BattlefieldTacticalSide.Friendly ? 14f : 8f;
                    combatRisk += item.ActorSide == BattlefieldTacticalSide.Friendly ? 7f : 2f;
                    flankRisk += 4f;
                    parts.Add(item.ActorSide == BattlefieldTacticalSide.Friendly ? $"{targetName} just lost" : $"{targetName} ownership lost");
                    break;
                case BattlefieldChatEventKind.ObjectiveCaptured:
                    objectiveRisk += item.ActorSide == BattlefieldTacticalSide.Enemy ? 10f : 5f;
                    combatRisk += item.ActorSide == BattlefieldTacticalSide.Enemy ? 5f : 0f;
                    flankRisk += item.ActorSide == BattlefieldTacticalSide.Friendly ? 6f : 3f;
                    parts.Add(item.ActorSide == BattlefieldTacticalSide.Friendly ? $"{targetName} captured" : $"enemy captured {targetName}");
                    break;
                case BattlefieldChatEventKind.ObjectiveOther:
                    objectiveRisk += 4f;
                    parts.Add($"{targetName} changed");
                    break;
            }
        }

        if (chatEvents.ObjectiveEventsRecent >= 2)
            parts.Add($"recent objective events {chatEvents.ObjectiveEventsRecent}");

        return new ChatObjectiveRiskModifier(
            objectiveRisk,
            combatRisk,
            flankRisk,
            string.Join("; ", parts.Take(2)));
    }

    private static SnowBlessingRiskModifier ResolveSnowBlessingRiskModifier(BattlefieldTeamSituationSnapshot teamSituation)
    {
        var friendly = teamSituation.Friendly.SnowBlessingCount;
        var enemy = teamSituation.Enemy.SnowBlessingCount;
        if (friendly <= 0 && enemy <= 0)
            return default;

        var enemyAdvantage = enemy - friendly;
        var terrainBonus = Math.Clamp(enemyAdvantage * 5f, -14f, 24f);
        var combatBonus = Math.Clamp(enemyAdvantage * 4f, -10f, 20f);
        return new SnowBlessingRiskModifier(
            terrainBonus,
            combatBonus,
            $"snow blessing friendly {friendly}/enemy {enemy}");
    }

    private static float ResolveFrameEventRisk(BattlefieldPlayerFrameEventSituationSnapshot events)
    {
        var risk = 0f;
        risk += events.FriendlyDeathsRecent * 28f;
        risk -= events.EnemyDeathsRecent * 18f;
        risk += Math.Max(0, events.FriendlyControlledRecent - 2) * 10f;
        risk -= events.EnemyControlledRecent * 10f;
        risk += Math.Max(0, events.EnemyTargetingFriendlyRecent - 3) * 6f;
        risk -= events.FriendlyTargetingEnemyRecent * 4f;
        risk -= events.EnemyDefensiveRecent * 4f;
        risk -= events.FriendlyDefensiveRecent * 3f;
        risk += events.EnemyRevivesRecent * 4f;
        risk -= events.FriendlyRevivesRecent * 3f;
        return Math.Clamp(risk, 0f, 100f);
    }

    private static float ResolveSkillThreatRisk(BattlefieldKeySkillThreatSituationSnapshot keySkillThreats)
    {
        var topEnemyScore = keySkillThreats.TopEnemyThreats
            .Select(threat => threat.ThreatScore)
            .DefaultIfEmpty(0f)
            .Max();
        var risk = topEnemyScore * 0.45f
            + keySkillThreats.EnemyHighThreatCount * 14f
            + keySkillThreats.EnemyLikelyReadyCount * 5f
            + keySkillThreats.EnemyControlChainCount * 12f
            + keySkillThreats.EnemyDefenseBreakWindowCount * 10f
            + keySkillThreats.EnemyExecuteWindowCount * 10f
            - keySkillThreats.FriendlyHighThreatCount * 4f;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static float ResolveFlankRisk(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldChatEventSituationSnapshot chatEvents)
    {
        var risk = teamSituation.IsEnemySplit ? 55f : 18f;
        if (teamSituation.EnemyClusters.Length > 1)
        {
            risk += Math.Min(28f, (teamSituation.EnemyClusters.Length - 1) * 10f);
            risk += Math.Min(18f, teamSituation.EnemyClusters.Max(cluster => cluster.SeparationFromMain) / 8f);
        }

        if (mapTactics.IsAvailable)
        {
            var flankZoneRisk = mapTactics.TopZones
                .Where(zone => zone.Kind == MapAnnotationKind.Flank || ContainsToken(zone.Label, "flank", "side"))
                .Select(zone => zone.TotalRisk)
                .DefaultIfEmpty(0f)
                .Max();
            var flankHeatRisk = mapTactics.HeatPoints
                .Where(point => ContainsToken(point.SourceText, "flank", "side"))
                .Select(point => point.Intensity)
                .DefaultIfEmpty(0f)
                .Max();
            risk += flankZoneRisk * 0.35f + flankHeatRisk * 0.45f;
        }

        if (chatEvents.FriendlyDeathsRecent >= 2)
            risk += Math.Min(20f, chatEvents.FriendlyDeathsRecent * 5f);

        return Math.Clamp(risk, 0f, 100f);
    }

    private static float ResolveRespawnRisk(BattlefieldRespawnRhythmSnapshot rhythm)
    {
        var risk = 35f
            + rhythm.FriendlyDeadNow * 8f
            + rhythm.FriendlyRecentlyDied * 5f
            + rhythm.EnemyRecentlyRevived * 4f
            + rhythm.EnemyLikelyReturningSoon * 3f
            - rhythm.EnemyDeadNow * 6f
            - rhythm.EnemyRecentlyDied * 4f
            - rhythm.FriendlyRecentlyRevived * 4f
            - rhythm.FriendlyLikelyReturningSoon * 3f;

        if (rhythm.FriendlyDeadNow >= 4)
            risk += 12f;
        if (rhythm.EnemyDeadNow >= 4)
            risk -= 10f;
        if (rhythm.FriendlyDeadNow >= rhythm.EnemyDeadNow + 3)
            risk += 10f;
        if (rhythm.EnemyDeadNow >= rhythm.FriendlyDeadNow + 3)
            risk -= 8f;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static float ResolveEnemyMainGroupDirectionRisk(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics)
    {
        var movement = teamSituation.EnemyMainGroupMovement;
        if (!movement.HasMainGroup)
            return teamSituation.IsEnemySplit ? 46f : 30f;

        var friendlyCenter = teamSituation.Friendly.MainCluster?.Center;
        var risk = 28f + Math.Min(26f, movement.PlayerCount * 2.2f) + Math.Min(18f, movement.SpeedPerSecond * 8f);
        if (friendlyCenter.HasValue)
        {
            var currentDistance = Distance2D(movement.CurrentCenter, friendlyCenter.Value);
            var predictedDistance = Distance2D(movement.PredictedNextCenter, friendlyCenter.Value);
            if (predictedDistance + 8f < currentDistance)
                risk += 24f;
            if (currentDistance <= 80f)
                risk += 18f;
            if (currentDistance <= 45f)
                risk += 12f;
        }

        if (teamSituation.IsEnemySplit)
            risk += 10f;
        if (mapTactics.IsAvailable && mapTactics.TopZones.Any(zone => zone.IsMandatoryChoke && zone.EnemyMapVisionNearby + zone.EnemyNearby > 0))
            risk += 10f;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static float ResolveTerrainRisk(BattlefieldMapTacticsSnapshot mapTactics)
    {
        if (!mapTactics.IsAvailable)
            return 35f;

        var badZoneRisk = mapTactics.TopZones
            .Where(zone => zone.Kind is MapAnnotationKind.Danger or MapAnnotationKind.LowGround or MapAnnotationKind.Underpass or MapAnnotationKind.Flank
                || zone.IsMandatoryChoke
                || zone.TotalRisk >= 70f
                || ContainsToken(zone.Recommendation, "avoid", "danger"))
            .Select(zone => zone.TotalRisk)
            .DefaultIfEmpty(0f)
            .Max();
        var routeRisk = mapTactics.Routes
            .Where(route => route.CrossesDangerZone || route.CrossesMandatoryChoke || route.TotalRisk >= 55f)
            .Select(route => route.TotalRisk)
            .DefaultIfEmpty(0f)
            .Max();
        var passabilityPressure = Math.Min(18f, mapTactics.MandatoryChokeCount * 4f + mapTactics.OneWayPassageCount * 2f);
        var heatRisk = mapTactics.HeatPoints
            .Where(point => point.Intensity >= 55f)
            .Select(point => point.Intensity)
            .DefaultIfEmpty(0f)
            .Max();
        var highGroundRelief = mapTactics.TopZones
            .Where(zone => zone.Kind == MapAnnotationKind.HighGround && (zone.TotalRisk <= 40f || ContainsToken(zone.Recommendation, "defend", "hold")))
            .Select(_ => 12f)
            .DefaultIfEmpty(0f)
            .Max();

        return Math.Clamp(badZoneRisk * 0.52f + routeRisk * 0.20f + heatRisk * 0.20f + passabilityPressure - highGroundRelief, 0f, 100f);
    }

    private static float ResolveRetreatRouteRisk(
        BattlefieldMapTacticsSnapshot mapTactics,
        float combatRisk,
        float flankRisk,
        float enemyDirectionRisk)
    {
        if (!mapTactics.IsAvailable)
            return Math.Clamp(36f + Math.Max(combatRisk, flankRisk) * 0.25f, 0f, 100f);

        var retreatRoutes = mapTactics.Routes
            .Where(IsRetreatRoute)
            .ToArray();
        if (retreatRoutes.Length == 0)
        {
            var fallback = 34f + Math.Max(combatRisk, enemyDirectionRisk) * 0.22f + flankRisk * 0.18f;
            if (mapTactics.Routes.Length == 0)
                fallback += 10f;
            return Math.Clamp(fallback, 0f, 100f);
        }

        var bestRetreat = retreatRoutes.OrderBy(route => route.TotalRisk).First();
        var risk = bestRetreat.TotalRisk;
        if (bestRetreat.CrossesDangerZone)
            risk += 12f;
        if (bestRetreat.CrossesMandatoryChoke && enemyDirectionRisk >= 55f)
            risk += 10f;
        if (retreatRoutes.All(route => route.TotalRisk >= 65f))
            risk += 12f;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static float ResolveEncirclementRisk(
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        float numberDisadvantageRisk,
        float flankRisk,
        float enemyDirectionRisk,
        float retreatRouteRisk)
    {
        var risk = numberDisadvantageRisk * 0.20f
            + flankRisk * 0.28f
            + enemyDirectionRisk * 0.22f
            + retreatRouteRisk * 0.30f;

        if (teamSituation.IsEnemySplit)
            risk += 14f;

        var friendlyCenter = teamSituation.Friendly.MainCluster?.Center;
        if (friendlyCenter.HasValue && mapTactics.IsAvailable)
        {
            var nearbyDanger = mapTactics.TopZones
                .Where(zone => Distance2D(zone.Position, friendlyCenter.Value) <= MathF.Max(zone.Radius, 24f) + 42f)
                .Where(zone => zone.Kind is MapAnnotationKind.Danger or MapAnnotationKind.LowGround or MapAnnotationKind.Underpass or MapAnnotationKind.Flank || zone.IsMandatoryChoke)
                .Select(zone => zone.TotalRisk)
                .DefaultIfEmpty(0f)
                .Max();
            risk += nearbyDanger * 0.22f;
        }

        if (teamSituation.RespawnRhythm.FriendlyDeadNow >= 4)
            risk += 10f;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static int ResolveEnemyMainGroupCount(BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (teamSituation.EnemyMainGroupMovement.HasMainGroup && teamSituation.EnemyMainGroupMovement.PlayerCount > 0)
            return teamSituation.EnemyMainGroupMovement.PlayerCount;

        if (teamSituation.Enemy.MainCluster.HasValue && teamSituation.Enemy.MainCluster.Value.PlayerCount > 0)
            return teamSituation.Enemy.MainCluster.Value.PlayerCount;

        return teamSituation.Enemy.AliveCount;
    }

    private static bool IsRetreatRoute(BattlefieldMapTacticalRouteSnapshot route)
    {
        var text = $"{route.RouteId} {route.KindSummary} {route.Recommendation} {route.EvidenceText}";
        return ContainsToken(text, "retreat", "back", "safe", "home", "return", "spawn");
    }

    private static string BuildChatObjectiveTargetName(BattlefieldChatEventSnapshot item)
        => FirstNonEmpty(item.ObjectiveName, item.LocationId, "objective");

    private static bool ContainsToken(string text, params string[] tokens)
        => tokens.Any(token => !string.IsNullOrWhiteSpace(token) && text.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private readonly record struct ChatObjectiveRiskModifier(
        float ObjectiveRiskBonus,
        float CombatRiskBonus,
        float FlankRiskBonus,
        string EvidenceText);

    private readonly record struct SnowBlessingRiskModifier(
        float TerrainRiskBonus,
        float CombatRiskBonus,
        string EvidenceText);
}
