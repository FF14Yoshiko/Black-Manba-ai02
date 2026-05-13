using System;
using System.Linq;
using System.Numerics;

namespace ai02.Tests;

internal static class BattlefieldTestFactory
{
    public static BattlefieldPlayerSnapshot LocalPlayer(Vector3 position)
        => new(
            1,
            1,
            "Local",
            position,
            0f,
            0f,
            19,
            0,
            false,
            false,
            false,
            true,
            true,
            false,
            false,
            0f,
            0f,
            0,
            0,
            10000,
            10000,
            100f,
            0,
            false,
            0,
            0f,
            Array.Empty<BattlefieldTacticalStatusSnapshot>(),
            false,
            false,
            false,
            true,
            false,
            false,
            false,
            BattlefieldPlayerRelation.LocalPlayer);

    public static BattlefieldMapObjectiveSnapshot Objective(
        string id,
        string name,
        FrontlineMapType mapType,
        BattlefieldMapObjectiveCategory category,
        BattlefieldMapObjectiveState state,
        Vector3 position,
        int? scoreValue = 100,
        int? remainingSeconds = 20,
        int attackerCount = 0,
        int friendlyAttackerCount = 0,
        int enemyAttackerCount = 0,
        float confidence = 1f)
        => new(
            id,
            mapType,
            category,
            state,
            position,
            null,
            null,
            name,
            string.Empty,
            "S",
            null,
            string.Empty,
            remainingSeconds,
            string.Empty,
            null,
            null,
            null,
            scoreValue,
            attackerCount,
            friendlyAttackerCount,
            enemyAttackerCount,
            0,
            false,
            attackerCount > 0,
            null,
            0f,
            null,
            Array.Empty<BattlefieldObjectiveContributionSnapshot>(),
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            string.Empty,
            confidence);

    public static BattlefieldScoreSituationSnapshot ScoreSituation(FrontlineMapType mapType)
    {
        var friendly = new BattlefieldAllianceScoreSnapshot(1, 0, "Local", BattlefieldPlayerRelation.Friendly, true, 800, 1600, 2, "2", false, 30, 0, 0f);
        var leader = new BattlefieldAllianceScoreSnapshot(2, 1, "Enemy1", BattlefieldPlayerRelation.Enemy, false, 900, 1600, 1, "1", true, 30, 20, 0.67f);
        var third = new BattlefieldAllianceScoreSnapshot(3, 2, "Enemy2", BattlefieldPlayerRelation.Enemy, false, 760, 1600, 3, "3", false, 30, -10, -0.33f);
        return new BattlefieldScoreSituationSnapshot
        {
            HasScoreData = true,
            MapType = mapType,
            MapName = mapType.ToString(),
            VictoryScore = 1600,
            Alliances = new[] { friendly, leader, third },
            RankedAlliances = new[] { leader, friendly, third },
            FriendlyAlliance = friendly,
            EnemyAlliance1 = leader,
            EnemyAlliance2 = third,
            SummaryText = "score ready"
        };
    }

    public static BattlefieldTimeSituationSnapshot TimeSituation(int remainingSeconds)
        => new()
        {
            HasMatchTime = true,
            MatchTimeRemainingSeconds = remainingSeconds,
            MatchElapsedSeconds = Math.Max(0, 1200 - remainingSeconds),
            MatchPhaseName = "mid",
            MatchPhaseDetail = "mid"
        };

    public static BattlefieldTeamSituationSnapshot TeamSituation(
        Vector3 localPosition,
        BattlefieldEnemyClusterSnapshot[] enemyClusters,
        int friendlyAlive = 12,
        int enemyAlive = 12)
        => new()
        {
            Friendly = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Friendly,
                Name = "Friendly",
                TotalCount = friendlyAlive,
                AliveCount = friendlyAlive,
                NearCount = Math.Min(friendlyAlive, 8),
                MidCount = Math.Max(0, friendlyAlive - 8),
                MainCluster = new BattlefieldPlayerClusterSnapshot(BattlefieldPlayerRelation.Friendly, 0, localPosition, Math.Min(friendlyAlive, 8), 0, 0, 0f)
            },
            Enemy = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Enemy,
                Name = "Enemy",
                TotalCount = enemyAlive,
                AliveCount = enemyAlive,
                NearCount = Math.Min(enemyAlive, 8),
                MidCount = Math.Max(0, enemyAlive - 8)
            },
            EnemyClusters = enemyClusters,
            EnemyAlliance1Players = Array.Empty<BattlefieldPlayerSnapshot>(),
            EnemyAlliance2Players = Array.Empty<BattlefieldPlayerSnapshot>(),
            FriendlyPlayers = Array.Empty<BattlefieldPlayerSnapshot>(),
            SummaryText = "team ready"
        };

    public static BattlefieldEnemyClusterSnapshot EnemyCluster(Vector3 center, int count, float distanceToLocal, float radius = 18f, bool isMain = true)
        => new()
        {
            ClusterId = count * 10 + (isMain ? 1 : 2),
            Battalion = 1,
            AllianceName = "Enemy1",
            SourceText = "test",
            Center = center,
            Count = count,
            Radius = radius,
            DistanceToLocal = distanceToLocal,
            SeparationFromMain = 0f,
            IsMainCluster = isMain
        };

    public static BattlefieldMapTacticsSnapshot MapTactics(
        params BattlefieldMapTacticalZoneSnapshot[] zones)
        => new()
        {
            IsAvailable = zones.Length > 0,
            TerritoryType = 1,
            MapId = 1,
            MapName = "test",
            AnnotationCount = zones.Length,
            BuiltInGraphPointCount = 0,
            ManualAnnotationCount = zones.Length,
            ZoneCount = zones.Length,
            HighGroundCount = zones.Count(zone => zone.Kind == MapAnnotationKind.HighGround),
            LowGroundCount = zones.Count(zone => zone.Kind == MapAnnotationKind.LowGround),
            JumpPadCount = zones.Count(zone => zone.Kind == MapAnnotationKind.JumpPad),
            TeleporterCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Teleporter),
            FlankEntryCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Flank),
            BridgeCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Bridge),
            UnderpassCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Underpass),
            TopZones = zones,
            Routes = Array.Empty<BattlefieldMapTacticalRouteSnapshot>(),
            HeatPoints = Array.Empty<BattlefieldMapHeatPointSnapshot>(),
            DangerSummaryText = "test",
            TerrainAdvantageSummaryText = "test",
            PassabilitySummaryText = "test",
            RewardModelSummaryText = "test",
            MapKnowledgeFocusText = "test",
            CurrentRecommendation = "test"
        };

    public static BattlefieldMapTacticalZoneSnapshot Zone(
        MapAnnotationKind kind,
        Vector3 position,
        float radius,
        float totalRisk,
        float staticRisk,
        float dynamicRisk,
        string recommendation = "test")
        => new(
            Guid.NewGuid().ToString("N"),
            kind,
            kind.ToString(),
            string.Empty,
            position,
            radius,
            radius * 2f,
            0f,
            kind == MapAnnotationKind.HighGround,
            kind == MapAnnotationKind.Choke && radius < 8f,
            0,
            0,
            0,
            0,
            0,
            0f,
            staticRisk,
            dynamicRisk,
            totalRisk,
            recommendation,
            "test");

    public static BattlefieldObjectivePrioritySnapshot ObjectivePriority(
        string id,
        string name,
        BattlefieldMapObjectiveCategory category,
        BattlefieldMapObjectiveState state,
        Vector3 position,
        float distanceToLocal,
        int etaSeconds,
        float riskScore,
        float priorityScore,
        int? scoreValue = 100,
        int? remainingSeconds = 20,
        string recommendedAction = "test")
        => new(
            id,
            name,
            category,
            state,
            position,
            null,
            string.Empty,
            scoreValue,
            remainingSeconds,
            distanceToLocal,
            etaSeconds,
            70f,
            70f,
            70f,
            70f,
            70f,
            70f,
            riskScore,
            50f,
            0f,
            0f,
            0f,
            0f,
            false,
            priorityScore,
            recommendedAction,
            "test");

    public static BattlefieldCommandSnapshot Command(
        string id,
        BattlefieldCommandKind kind,
        string commandText,
        Vector3 position,
        string targetName,
        float score = 82f,
        float urgency = 80f,
        int cooldownSeconds = 8,
        string scope = "主团")
        => new(
            id,
            kind,
            scope,
            commandText,
            score,
            urgency,
            cooldownSeconds,
            position,
            targetName,
            "test",
            "test");

    public static BattlefieldActionCandidateSnapshot Action(
        string id,
        string commandId,
        BattlefieldActionType actionType,
        BattlefieldCommandKind commandKind,
        string text,
        Vector3 destination,
        string targetId,
        string targetName,
        float priority = 82f,
        float confidence = 76f,
        float risk = 40f,
        float urgency = 80f,
        int etaSeconds = 8,
        int holdSeconds = 10,
        string destinationName = "",
        string scope = "主团")
        => new(
            id,
            commandId,
            actionType,
            commandKind,
            scope,
            text,
            priority,
            confidence,
            risk,
            urgency,
            destination,
            string.IsNullOrWhiteSpace(destinationName) ? targetName : destinationName,
            targetId,
            targetName,
            string.Empty,
            string.Empty,
            null,
            etaSeconds,
            holdSeconds,
            "test",
            "test",
            "test",
            "test");

    public static BattlefieldPriorityTargetSnapshot PriorityTarget(
        string lane,
        string targetName,
        string actionText,
        Vector3 position,
        float priority = 82f,
        float urgency = 80f)
        => new(
            lane,
            targetName,
            actionText,
            priority,
            urgency,
            position,
            "test",
            "test");
}
