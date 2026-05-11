using System.Numerics;
using Xunit;

namespace ai02.Tests;

public sealed class TacticalDecisionEngineRegressionTests
{
    [Fact]
    public void Analyze_PrioritizesInterruptTouchOnContestedObjective()
    {
        var engine = new TacticalDecisionEngineService();
        var local = BattlefieldTestFactory.LocalPlayer(Vector3.Zero);
        var decision = engine.Analyze(
            local,
            new[]
            {
                BattlefieldTestFactory.Objective(
                    "ovoo-a",
                    "North Ovoo",
                    FrontlineMapType.OnsalHakair,
                    BattlefieldMapObjectiveCategory.Ovoo,
                    BattlefieldMapObjectiveState.Contested,
                    new Vector3(30f, 0f, 0f),
                    scoreValue: 100,
                    remainingSeconds: 18,
                    attackerCount: 2,
                    friendlyAttackerCount: 1,
                    enemyAttackerCount: 2)
            },
            [],
            [],
            BattlefieldTestFactory.TeamSituation(
                Vector3.Zero,
                new[] { BattlefieldTestFactory.EnemyCluster(new Vector3(30f, 0f, 0f), 6, 30f) },
                friendlyAlive: 8,
                enemyAlive: 6),
            BattlefieldTestFactory.ScoreSituation(FrontlineMapType.OnsalHakair),
            BattlefieldTestFactory.TimeSituation(600),
            new BattlefieldAnnouncementSituationSnapshot(),
            new BattlefieldMapTacticsSnapshot(),
            new BattlefieldChatEventSituationSnapshot(),
            new BattlefieldPlayerFrameEventSituationSnapshot(),
            new FrontlineKnowledgeSnapshot(),
            []);

        Assert.True(decision.PrimaryAction.HasValue);
        Assert.Equal(BattlefieldActionType.InterruptTouch, decision.PrimaryAction.Value.ActionType);
    }

    [Fact]
    public void Analyze_PrefersSaferObjectiveLaneWhenScoresAreClose()
    {
        var engine = new TacticalDecisionEngineService();
        var local = BattlefieldTestFactory.LocalPlayer(Vector3.Zero);
        var decision = engine.Analyze(
            local,
            new[]
            {
                BattlefieldTestFactory.Objective(
                    "danger",
                    "Danger Node",
                    FrontlineMapType.SealRock,
                    BattlefieldMapObjectiveCategory.Tomelith,
                    BattlefieldMapObjectiveState.Active,
                    new Vector3(110f, 0f, 0f),
                    scoreValue: 120,
                    remainingSeconds: 35,
                    attackerCount: 1,
                    enemyAttackerCount: 1),
                BattlefieldTestFactory.Objective(
                    "safe",
                    "Safe Node",
                    FrontlineMapType.SealRock,
                    BattlefieldMapObjectiveCategory.Tomelith,
                    BattlefieldMapObjectiveState.Active,
                    new Vector3(120f, 0f, 70f),
                    scoreValue: 110,
                    remainingSeconds: 35)
            },
            [],
            [],
            BattlefieldTestFactory.TeamSituation(
                Vector3.Zero,
                new[] { BattlefieldTestFactory.EnemyCluster(new Vector3(60f, 0f, 0f), 8, 60f) },
                friendlyAlive: 12,
                enemyAlive: 8),
            BattlefieldTestFactory.ScoreSituation(FrontlineMapType.SealRock),
            BattlefieldTestFactory.TimeSituation(540),
            new BattlefieldAnnouncementSituationSnapshot(),
            BattlefieldTestFactory.MapTactics(
                BattlefieldTestFactory.Zone(MapAnnotationKind.LowGround, new Vector3(55f, 0f, 0f), 24f, 92f, 80f, 92f, "avoid"),
                BattlefieldTestFactory.Zone(MapAnnotationKind.HighGround, new Vector3(120f, 0f, 70f), 24f, 24f, 16f, 24f, "defend")),
            new BattlefieldChatEventSituationSnapshot(),
            new BattlefieldPlayerFrameEventSituationSnapshot(),
            new FrontlineKnowledgeSnapshot(),
            []);

        Assert.True(decision.PrimaryObjective.HasValue);
        Assert.Equal("Safe Node", decision.PrimaryObjective.Value.Name);
    }

    [Fact]
    public void Analyze_PublishesRetreatWhenFightStateIsFatal()
    {
        var engine = new TacticalDecisionEngineService();
        var local = BattlefieldTestFactory.LocalPlayer(Vector3.Zero);
        var teamSituation = new BattlefieldTeamSituationSnapshot
        {
            Friendly = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Friendly,
                Name = "Friendly",
                TotalCount = 12,
                AliveCount = 4,
                DeadCount = 8,
                LowHpCount = 3,
                NearCount = 4,
                MainCluster = new BattlefieldPlayerClusterSnapshot(BattlefieldPlayerRelation.Friendly, 0, Vector3.Zero, 4, 8, 0, 0f)
            },
            Enemy = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Enemy,
                Name = "Enemy",
                TotalCount = 16,
                AliveCount = 14,
                DeadCount = 2,
                NearCount = 12,
                CastingCount = 2,
                BattleHighTotalLevel = 6
            },
            EnemyClusters =
            [
                BattlefieldTestFactory.EnemyCluster(new Vector3(18f, 0f, 8f), 9, 20f, isMain: true),
                new BattlefieldEnemyClusterSnapshot
                {
                    ClusterId = 92,
                    Battalion = 2,
                    AllianceName = "Enemy2",
                    SourceText = "test",
                    Center = new Vector3(-15f, 0f, -12f),
                    Count = 7,
                    Radius = 18f,
                    DistanceToLocal = 19f,
                    SeparationFromMain = 36f,
                    IsMainCluster = false
                }
            ],
            RespawnRhythm = new BattlefieldRespawnRhythmSnapshot
            {
                FriendlyDeadNow = 8,
                EnemyDeadNow = 1,
                FriendlyDeathWaveSize = 8,
                EnemyDeathWaveSize = 1
            },
            EnemyMainGroupMovement = new BattlefieldGroupMovementSnapshot
            {
                HasMainGroup = true,
                CurrentCenter = new Vector3(18f, 0f, 8f),
                PredictedNextCenter = new Vector3(8f, 0f, 4f),
                PlayerCount = 9,
                Confidence = 90f
            },
            AdvancedTactics = new BattlefieldAdvancedTacticalSituationSnapshot
            {
                ThirdPartyPincerRisk = 92f,
                CohesionRisk = 80f,
                ChokeBlockRisk = 88f
            },
            SummaryText = "fatal"
        };

        var decision = engine.Analyze(
            local,
            [],
            [],
            [],
            teamSituation,
            BattlefieldTestFactory.ScoreSituation(FrontlineMapType.SealRock),
            BattlefieldTestFactory.TimeSituation(300),
            new BattlefieldAnnouncementSituationSnapshot(),
            BattlefieldTestFactory.MapTactics(
                BattlefieldTestFactory.Zone(MapAnnotationKind.LowGround, new Vector3(10f, 0f, 0f), 24f, 96f, 92f, 90f, "retreat")),
            new BattlefieldChatEventSituationSnapshot
            {
                FriendlyDeathsRecent = 3,
                EnemyKillsRecent = 3,
                SummaryText = "collapse"
            },
            new BattlefieldPlayerFrameEventSituationSnapshot
            {
                FriendlyDeathsRecent = 4,
                FriendlyControlledRecent = 5,
                EnemyTargetingFriendlyRecent = 8,
                SummaryText = "fatal window"
            },
            new FrontlineKnowledgeSnapshot(),
            []);

        Assert.True(decision.PrimaryAction.HasValue);
        Assert.Equal(BattlefieldActionType.Retreat, decision.PrimaryAction.Value.ActionType);
        Assert.True(decision.RiskAssessment.EncirclementRisk >= 70f);
    }
}
