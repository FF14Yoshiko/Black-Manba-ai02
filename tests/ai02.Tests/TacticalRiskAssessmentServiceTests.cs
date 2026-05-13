using System.Numerics;
using Xunit;

namespace ai02.Tests;

public sealed class TacticalRiskAssessmentServiceTests
{
    [Fact]
    public void Build_RaisesRetreatAndEncirclementRiskWhenRetreatRoutesAreCompromised()
    {
        var enemyClusters = new[]
        {
            BattlefieldTestFactory.EnemyCluster(new Vector3(18f, 0f, 8f), 9, 20f, isMain: true),
            new BattlefieldEnemyClusterSnapshot
            {
                ClusterId = 92,
                Battalion = 2,
                AllianceName = "Enemy2",
                SourceText = "test",
                Center = new Vector3(-16f, 0f, -10f),
                Count = 6,
                Radius = 18f,
                DistanceToLocal = 19f,
                SeparationFromMain = 44f,
                IsMainCluster = false
            }
        };

        var teamSituation = new BattlefieldTeamSituationSnapshot
        {
            Friendly = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Friendly,
                Name = "Friendly",
                TotalCount = 12,
                AliveCount = 6,
                DeadCount = 6,
                LowHpCount = 4,
                NearCount = 6,
                MainCluster = new BattlefieldPlayerClusterSnapshot(BattlefieldPlayerRelation.Friendly, 0, Vector3.Zero, 6, 10, 0, 0f)
            },
            Enemy = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Enemy,
                Name = "Enemy",
                TotalCount = 16,
                AliveCount = 14,
                DeadCount = 2,
                NearCount = 10,
                MidCount = 4,
                CastingCount = 2,
                BattleHighTotalLevel = 6,
                CrowdControlledCount = 0,
                SnowBlessingCount = 1
            },
            EnemyClusters = enemyClusters,
            IsEnemySplit = true,
            RespawnRhythm = new BattlefieldRespawnRhythmSnapshot
            {
                FriendlyDeadNow = 5,
                EnemyDeadNow = 1,
                FriendlyRecentlyDied = 4,
                EnemyRecentlyRevived = 2
            },
            EnemyMainGroupMovement = new BattlefieldGroupMovementSnapshot
            {
                HasMainGroup = true,
                CurrentCenter = new Vector3(18f, 0f, 8f),
                PredictedNextCenter = new Vector3(6f, 0f, 3f),
                PlayerCount = 9,
                SpeedPerSecond = 2.4f,
                Confidence = 88f
            },
            AdvancedTactics = new BattlefieldAdvancedTacticalSituationSnapshot
            {
                ThirdPartyPincerRisk = 90f,
                ChokeBlockRisk = 86f,
                CohesionRisk = 72f
            },
            SummaryText = "collapse"
        };

        var mapTactics = new BattlefieldMapTacticsSnapshot
        {
            IsAvailable = true,
            TerritoryType = 1,
            MapId = 1,
            MapName = "test",
            TopZones =
            [
                BattlefieldTestFactory.Zone(MapAnnotationKind.LowGround, new Vector3(10f, 0f, 0f), 24f, 88f, 82f, 84f, "avoid"),
                BattlefieldTestFactory.Zone(MapAnnotationKind.Choke, new Vector3(16f, 0f, 2f), 6f, 80f, 76f, 74f, "hold")
            ],
            Routes =
            [
                new BattlefieldMapTacticalRouteSnapshot("retreat-west", "retreat", 3, 80f, 8, 14, 72f, 82f, 78f, true, true, "retreat", "safe back"),
                new BattlefieldMapTacticalRouteSnapshot("home-south", "home", 3, 92f, 10, 16, 66f, 74f, 71f, false, true, "safe", "home")
            ],
            HeatPoints =
            [
                new BattlefieldMapHeatPointSnapshot(new Vector3(8f, 0f, 5f), 18f, 70f, "flank")
            ],
            CurrentRecommendation = "retreat"
        };

        var risk = TacticalRiskAssessmentService.Build(
            teamSituation,
            mapTactics,
            [],
            new BattlefieldChatEventSituationSnapshot
            {
                FriendlyDeathsRecent = 3,
                ObjectiveEventsRecent = 1
            },
            new BattlefieldPlayerFrameEventSituationSnapshot
            {
                FriendlyDeathsRecent = 3,
                FriendlyControlledRecent = 4,
                EnemyTargetingFriendlyRecent = 7
            },
            scorePressure: 36f);

        Assert.True(risk.RetreatRouteRisk >= 85f);
        Assert.True(risk.EncirclementRisk >= 80f);
        Assert.True(risk.OverallRisk >= 70f);
    }

    [Fact]
    public void IsFatalFightState_ReturnsTrueForSevereCollapse()
    {
        var teamSituation = new BattlefieldTeamSituationSnapshot
        {
            Friendly = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Friendly,
                Name = "Friendly",
                AliveCount = 6,
                LowHpCount = 4
            },
            Enemy = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Enemy,
                Name = "Enemy",
                AliveCount = 14,
                NearCount = 10,
                MidCount = 4
            },
            RespawnRhythm = new BattlefieldRespawnRhythmSnapshot
            {
                FriendlyDeadNow = 8,
                EnemyDeadNow = 1
            }
        };

        var risk = new BattlefieldRiskAssessmentSnapshot
        {
            OverallRisk = 82f,
            CombatRisk = 80f,
            EncirclementRisk = 78f,
            NumberDisadvantageRisk = 88f
        };

        Assert.True(TacticalRiskAssessmentService.IsFatalFightState(risk, teamSituation));
    }
}
