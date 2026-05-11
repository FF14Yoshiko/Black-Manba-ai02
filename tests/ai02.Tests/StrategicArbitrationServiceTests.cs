using System.Numerics;
using Xunit;

namespace ai02.Tests;

public sealed class StrategicArbitrationServiceTests
{
    [Fact]
    public void Apply_UsesAiDirectiveAsPrimaryActionEvenWhenPublishIsSuppressed()
    {
        var service = new StrategicArbitrationService();
        var dangerObjective = BattlefieldTestFactory.ObjectivePriority(
            "danger",
            "Danger Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(50f, 0f, 0f),
            50f,
            5,
            72f,
            88f,
            recommendedAction: "抢点 Danger Node");
        var safeObjective = BattlefieldTestFactory.ObjectivePriority(
            "safe",
            "Safe Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(90f, 0f, 70f),
            114f,
            12,
            24f,
            76f,
            recommendedAction: "转点 Safe Node");

        var localContestCommand = BattlefieldTestFactory.Command(
            "local:contest:danger",
            BattlefieldCommandKind.ContestObjective,
            "主团抢点 Danger Node",
            dangerObjective.Position,
            dangerObjective.Name,
            score: 90f,
            urgency: 84f);
        var localContestAction = BattlefieldTestFactory.Action(
            "local:action:contest:danger",
            localContestCommand.Id,
            BattlefieldActionType.ContestObjective,
            BattlefieldCommandKind.ContestObjective,
            "主团抢点 Danger Node",
            dangerObjective.Position,
            dangerObjective.ObjectiveId,
            dangerObjective.Name,
            priority: 90f,
            confidence: 82f,
            urgency: 84f,
            etaSeconds: dangerObjective.MountedEtaSeconds);
        var rotateCommand = BattlefieldTestFactory.Command(
            "local:rotate:safe",
            BattlefieldCommandKind.Rotate,
            "主团转点 Safe Node",
            safeObjective.Position,
            safeObjective.Name,
            score: 74f,
            urgency: 72f);
        var rotateAction = BattlefieldTestFactory.Action(
            "local:action:rotate:safe",
            rotateCommand.Id,
            BattlefieldActionType.Rotate,
            BattlefieldCommandKind.Rotate,
            "主团转点 Safe Node",
            safeObjective.Position,
            safeObjective.ObjectiveId,
            safeObjective.Name,
            priority: 74f,
            confidence: 72f,
            urgency: 72f,
            etaSeconds: safeObjective.MountedEtaSeconds);

        var localDecision = CreateDecision(
            new BattlefieldRiskAssessmentSnapshot { OverallRisk = 42f, CombatRisk = 38f },
            localContestCommand,
            localContestAction,
            canPublish: false,
            gateText: "输入可靠度不足",
            commands: [localContestCommand, rotateCommand],
            actions: [localContestAction, rotateAction],
            objectivePriorities: [dangerObjective, safeObjective],
            primaryObjective: dangerObjective,
            objectiveTarget: BattlefieldTestFactory.PriorityTarget("Local", dangerObjective.Name, localContestAction.Text, dangerObjective.Position));

        var snapshot = CreateSnapshot(localDecision);
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsAvailable = true,
            IsFresh = true,
            RecommendedAction = "转点 Safe Node",
            PriorityTarget = "Safe Node",
            Confidence = 88f,
            Risk = 22f,
            ReceivedAtTicks = 12345
        };

        var result = service.Apply(snapshot, localDecision, llmDecision);

        Assert.NotSame(localDecision, result);
        Assert.True(result.PrimaryAction.HasValue);
        Assert.Equal(BattlefieldActionType.Rotate, result.PrimaryAction.Value.ActionType);
        Assert.Equal("Safe Node", result.PrimaryAction.Value.TargetName);
        Assert.True(result.ObjectivePriorityTarget.HasValue);
        Assert.Equal("AI", result.ObjectivePriorityTarget.Value.Lane);
        Assert.Equal("Safe Node", result.ObjectivePriorityTarget.Value.TargetName);
        Assert.True(result.PublishedAction.HasValue);
        Assert.Equal(BattlefieldActionType.Rotate, result.PublishedAction.Value.ActionType);
        Assert.False(result.CommandSituation.Publish.ShouldAnnounce);
        Assert.True(result.CommandSituation.Publish.IsSuppressed);
        Assert.Equal("输入可靠度不足", result.CommandSituation.Publish.SuppressionReason);
    }

    [Fact]
    public void Apply_KeepsLocalEmergencyRetreatAgainstNonEmergencyAiDirective()
    {
        var service = new StrategicArbitrationService();
        var retreatCommand = BattlefieldTestFactory.Command(
            "local:retreat",
            BattlefieldCommandKind.Retreat,
            "主团后撤脱战",
            Vector3.Zero,
            "安全线",
            score: 96f,
            urgency: 96f,
            cooldownSeconds: 6);
        var retreatAction = BattlefieldTestFactory.Action(
            "local:action:retreat",
            retreatCommand.Id,
            BattlefieldActionType.Retreat,
            BattlefieldCommandKind.Retreat,
            "主团后撤脱战",
            Vector3.Zero,
            "safe-line",
            "安全线",
            priority: 96f,
            confidence: 90f,
            risk: 98f,
            urgency: 96f,
            holdSeconds: 14);
        var localDecision = CreateDecision(
            new BattlefieldRiskAssessmentSnapshot { OverallRisk = 97f, CombatRisk = 92f, LimitBreakRisk = 88f },
            retreatCommand,
            retreatAction,
            canPublish: true);
        var snapshot = CreateSnapshot(localDecision);
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsAvailable = true,
            IsFresh = true,
            RecommendedAction = "打第一 Enemy1",
            PriorityTarget = "Enemy1",
            Confidence = 92f,
            Risk = 36f,
            ReceivedAtTicks = 22345
        };

        var result = service.Apply(snapshot, localDecision, llmDecision);

        Assert.Same(localDecision, result);
    }

    [Fact]
    public void Apply_AllowsAiRetreatToOverrideHighRiskLocalFightCall()
    {
        var service = new StrategicArbitrationService();
        var engageCommand = BattlefieldTestFactory.Command(
            "local:engage",
            BattlefieldCommandKind.Engage,
            "主团接团 Enemy1",
            new Vector3(40f, 0f, 0f),
            "Enemy1",
            score: 84f,
            urgency: 80f);
        var engageAction = BattlefieldTestFactory.Action(
            "local:action:engage",
            engageCommand.Id,
            BattlefieldActionType.Engage,
            BattlefieldCommandKind.Engage,
            "主团接团 Enemy1",
            new Vector3(40f, 0f, 0f),
            "alliance:2",
            "Enemy1",
            priority: 84f,
            confidence: 78f,
            risk: 86f,
            urgency: 80f);
        var retreatCommand = BattlefieldTestFactory.Command(
            "local:retreat:secondary",
            BattlefieldCommandKind.Retreat,
            "主团后撤脱战",
            Vector3.Zero,
            "安全线",
            score: 70f,
            urgency: 78f);
        var retreatAction = BattlefieldTestFactory.Action(
            "local:action:retreat:secondary",
            retreatCommand.Id,
            BattlefieldActionType.Retreat,
            BattlefieldCommandKind.Retreat,
            "主团后撤脱战",
            Vector3.Zero,
            "safe-line",
            "安全线",
            priority: 70f,
            confidence: 70f,
            risk: 94f,
            urgency: 78f,
            holdSeconds: 14);
        var localDecision = CreateDecision(
            new BattlefieldRiskAssessmentSnapshot { OverallRisk = 97f, CombatRisk = 96f, LimitBreakRisk = 75f },
            engageCommand,
            engageAction,
            canPublish: true,
            commands: [engageCommand, retreatCommand],
            actions: [engageAction, retreatAction],
            fightTarget: BattlefieldTestFactory.PriorityTarget("Local", "Enemy1", engageAction.Text, engageAction.Destination));
        var snapshot = CreateSnapshot(localDecision);
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsAvailable = true,
            IsFresh = true,
            RecommendedAction = "撤退 主团",
            Confidence = 90f,
            Risk = 92f,
            ReceivedAtTicks = 32345
        };

        var result = service.Apply(snapshot, localDecision, llmDecision);

        Assert.NotSame(localDecision, result);
        Assert.True(result.PrimaryAction.HasValue);
        Assert.Equal(BattlefieldActionType.Retreat, result.PrimaryAction.Value.ActionType);
        Assert.True(result.CommandSituation.PrimaryCommand.HasValue);
        Assert.Equal(BattlefieldCommandKind.Retreat, result.CommandSituation.PrimaryCommand.Value.Kind);
        Assert.True(result.PublishedAction.HasValue);
        Assert.Equal(BattlefieldActionType.Retreat, result.PublishedAction.Value.ActionType);
    }

    [Fact]
    public void Apply_ResolvesLeaderAliasToTopEnemyAlliance()
    {
        var service = new StrategicArbitrationService();
        var localFocusCommand = BattlefieldTestFactory.Command(
            "local:focus:second",
            BattlefieldCommandKind.FocusTarget,
            "主团打第一 Enemy2",
            new Vector3(90f, 0f, 0f),
            "Enemy2",
            score: 80f,
            urgency: 78f);
        var localFocusAction = BattlefieldTestFactory.Action(
            "local:action:focus:second",
            localFocusCommand.Id,
            BattlefieldActionType.FocusTarget,
            BattlefieldCommandKind.FocusTarget,
            "主团打第一 Enemy2",
            new Vector3(90f, 0f, 0f),
            "alliance:3",
            "Enemy2",
            priority: 80f,
            confidence: 76f,
            urgency: 78f);
        var localDecision = CreateDecision(
            new BattlefieldRiskAssessmentSnapshot { OverallRisk = 38f, CombatRisk = 34f },
            localFocusCommand,
            localFocusAction,
            canPublish: true,
            fightTarget: BattlefieldTestFactory.PriorityTarget("Local", "Enemy2", localFocusAction.Text, localFocusAction.Destination));
        var snapshot = CreateSnapshot(
            localDecision,
            teamSituation: CreateAllianceTargetTeamSituation(),
            scoreSituation: BattlefieldTestFactory.ScoreSituation(FrontlineMapType.SealRock));
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsAvailable = true,
            IsFresh = true,
            RecommendedAction = "打第一",
            Confidence = 90f,
            Risk = 30f,
            ReceivedAtTicks = 41001
        };

        var result = service.Apply(snapshot, localDecision, llmDecision);

        Assert.True(result.PrimaryAction.HasValue);
        Assert.Equal(BattlefieldActionType.FocusTarget, result.PrimaryAction.Value.ActionType);
        Assert.Equal("Enemy1", result.PrimaryAction.Value.TargetName);
        Assert.Equal("alliance:2", result.PrimaryAction.Value.TargetId);
        Assert.Equal(new Vector3(40f, 0f, 0f), result.PrimaryAction.Value.Destination);
        Assert.True(result.FightPriorityTarget.HasValue);
        Assert.Equal("Enemy1", result.FightPriorityTarget.Value.TargetName);
    }

    [Fact]
    public void Apply_ResolvesSecondPlaceAliasToSecondEnemyAlliance()
    {
        var service = new StrategicArbitrationService();
        var localFocusCommand = BattlefieldTestFactory.Command(
            "local:focus:first",
            BattlefieldCommandKind.FocusTarget,
            "主团打第一 Enemy1",
            new Vector3(40f, 0f, 0f),
            "Enemy1",
            score: 86f,
            urgency: 80f);
        var localFocusAction = BattlefieldTestFactory.Action(
            "local:action:focus:first",
            localFocusCommand.Id,
            BattlefieldActionType.FocusTarget,
            BattlefieldCommandKind.FocusTarget,
            "主团打第一 Enemy1",
            new Vector3(40f, 0f, 0f),
            "alliance:2",
            "Enemy1",
            priority: 86f,
            confidence: 80f,
            urgency: 80f);
        var localDecision = CreateDecision(
            new BattlefieldRiskAssessmentSnapshot { OverallRisk = 40f, CombatRisk = 35f },
            localFocusCommand,
            localFocusAction,
            canPublish: true,
            fightTarget: BattlefieldTestFactory.PriorityTarget("Local", "Enemy1", localFocusAction.Text, localFocusAction.Destination));
        var snapshot = CreateSnapshot(
            localDecision,
            teamSituation: CreateAllianceTargetTeamSituation(),
            scoreSituation: BattlefieldTestFactory.ScoreSituation(FrontlineMapType.SealRock));
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsAvailable = true,
            IsFresh = true,
            RecommendedAction = "打第二",
            Confidence = 91f,
            Risk = 34f,
            ReceivedAtTicks = 41002
        };

        var result = service.Apply(snapshot, localDecision, llmDecision);

        Assert.True(result.PrimaryAction.HasValue);
        Assert.Equal("Enemy2", result.PrimaryAction.Value.TargetName);
        Assert.Equal("alliance:3", result.PrimaryAction.Value.TargetId);
        Assert.Equal(new Vector3(90f, 0f, 0f), result.PrimaryAction.Value.Destination);
        Assert.True(result.FightPriorityTarget.HasValue);
        Assert.Equal("Enemy2", result.FightPriorityTarget.Value.TargetName);
    }

    [Fact]
    public void Apply_ResolvesHighValueAliasToHighestPriorityValuableObjective()
    {
        var service = new StrategicArbitrationService();
        var nearLowValue = BattlefieldTestFactory.ObjectivePriority(
            "near-low",
            "Near Low",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(25f, 0f, 0f),
            25f,
            3,
            18f,
            72f,
            scoreValue: 50,
            recommendedAction: "转点 Near Low");
        var highValuePrimary = BattlefieldTestFactory.ObjectivePriority(
            "high-main",
            "High Main",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(120f, 0f, 10f),
            120f,
            12,
            28f,
            95f,
            scoreValue: 120,
            recommendedAction: "抢点 High Main");
        var highValueSecondary = BattlefieldTestFactory.ObjectivePriority(
            "high-side",
            "High Side",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(70f, 0f, 60f),
            92f,
            9,
            30f,
            88f,
            scoreValue: 100,
            recommendedAction: "抢点 High Side");

        var localRotateCommand = BattlefieldTestFactory.Command(
            "local:rotate:near-low",
            BattlefieldCommandKind.Rotate,
            "主团转点 Near Low",
            nearLowValue.Position,
            nearLowValue.Name,
            score: 78f,
            urgency: 74f);
        var localRotateAction = BattlefieldTestFactory.Action(
            "local:action:rotate:near-low",
            localRotateCommand.Id,
            BattlefieldActionType.Rotate,
            BattlefieldCommandKind.Rotate,
            "主团转点 Near Low",
            nearLowValue.Position,
            nearLowValue.ObjectiveId,
            nearLowValue.Name,
            priority: 78f,
            confidence: 74f,
            urgency: 74f,
            etaSeconds: nearLowValue.MountedEtaSeconds);
        var localDecision = CreateDecision(
            new BattlefieldRiskAssessmentSnapshot { OverallRisk = 34f, CombatRisk = 30f },
            localRotateCommand,
            localRotateAction,
            canPublish: true,
            commands: [localRotateCommand],
            actions: [localRotateAction],
            objectivePriorities: [nearLowValue, highValuePrimary, highValueSecondary],
            primaryObjective: nearLowValue,
            objectiveTarget: BattlefieldTestFactory.PriorityTarget("Local", nearLowValue.Name, localRotateAction.Text, nearLowValue.Position));
        var snapshot = CreateSnapshot(localDecision);
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsAvailable = true,
            IsFresh = true,
            RecommendedAction = "转点 高价值点",
            Confidence = 89f,
            Risk = 26f,
            ReceivedAtTicks = 41003
        };

        var result = service.Apply(snapshot, localDecision, llmDecision);

        Assert.True(result.PrimaryAction.HasValue);
        Assert.Equal(BattlefieldActionType.Rotate, result.PrimaryAction.Value.ActionType);
        Assert.Equal("High Main", result.PrimaryAction.Value.TargetName);
        Assert.Equal("high-main", result.PrimaryAction.Value.TargetId);
        Assert.Equal(highValuePrimary.Position, result.PrimaryAction.Value.Destination);
        Assert.True(result.ObjectivePriorityTarget.HasValue);
        Assert.Equal("High Main", result.ObjectivePriorityTarget.Value.TargetName);
    }

    [Theory]
    [InlineData("转点 近点", "Near Node", "near-node")]
    [InlineData("转点 远点", "Far Node", "far-node")]
    public void Apply_ResolvesNearAndFarAliasesByObjectiveDistance(string directive, string expectedTargetName, string expectedTargetId)
    {
        var service = new StrategicArbitrationService();
        var nearObjective = BattlefieldTestFactory.ObjectivePriority(
            "near-node",
            "Near Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(20f, 0f, 10f),
            22f,
            3,
            20f,
            82f);
        var midObjective = BattlefieldTestFactory.ObjectivePriority(
            "mid-node",
            "Mid Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(70f, 0f, 20f),
            73f,
            8,
            28f,
            86f);
        var farObjective = BattlefieldTestFactory.ObjectivePriority(
            "far-node",
            "Far Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(160f, 0f, 30f),
            163f,
            17,
            35f,
            80f);

        var localRotateCommand = BattlefieldTestFactory.Command(
            "local:rotate:mid",
            BattlefieldCommandKind.Rotate,
            "主团转点 Mid Node",
            midObjective.Position,
            midObjective.Name,
            score: 84f,
            urgency: 78f);
        var localRotateAction = BattlefieldTestFactory.Action(
            "local:action:rotate:mid",
            localRotateCommand.Id,
            BattlefieldActionType.Rotate,
            BattlefieldCommandKind.Rotate,
            "主团转点 Mid Node",
            midObjective.Position,
            midObjective.ObjectiveId,
            midObjective.Name,
            priority: 84f,
            confidence: 78f,
            urgency: 78f,
            etaSeconds: midObjective.MountedEtaSeconds);
        var localDecision = CreateDecision(
            new BattlefieldRiskAssessmentSnapshot { OverallRisk = 36f, CombatRisk = 32f },
            localRotateCommand,
            localRotateAction,
            canPublish: true,
            commands: [localRotateCommand],
            actions: [localRotateAction],
            objectivePriorities: [midObjective, farObjective, nearObjective],
            primaryObjective: midObjective,
            objectiveTarget: BattlefieldTestFactory.PriorityTarget("Local", midObjective.Name, localRotateAction.Text, midObjective.Position));
        var snapshot = CreateSnapshot(localDecision);
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsAvailable = true,
            IsFresh = true,
            RecommendedAction = directive,
            Confidence = 87f,
            Risk = 28f,
            ReceivedAtTicks = 41004
        };

        var result = service.Apply(snapshot, localDecision, llmDecision);

        Assert.True(result.PrimaryAction.HasValue);
        Assert.Equal(expectedTargetName, result.PrimaryAction.Value.TargetName);
        Assert.Equal(expectedTargetId, result.PrimaryAction.Value.TargetId);
        Assert.True(result.ObjectivePriorityTarget.HasValue);
        Assert.Equal(expectedTargetName, result.ObjectivePriorityTarget.Value.TargetName);
    }

    private static BattlefieldDecisionSnapshot CreateDecision(
        BattlefieldRiskAssessmentSnapshot risk,
        BattlefieldCommandSnapshot primaryCommand,
        BattlefieldActionCandidateSnapshot primaryAction,
        bool canPublish,
        string gateText = "",
        BattlefieldCommandSnapshot[]? commands = null,
        BattlefieldActionCandidateSnapshot[]? actions = null,
        BattlefieldObjectivePrioritySnapshot[]? objectivePriorities = null,
        BattlefieldObjectivePrioritySnapshot? primaryObjective = null,
        BattlefieldPriorityTargetSnapshot? objectiveTarget = null,
        BattlefieldPriorityTargetSnapshot? fightTarget = null)
    {
        var allCommands = commands ?? [primaryCommand];
        var allActions = actions ?? [primaryAction];

        return new BattlefieldDecisionSnapshot
        {
            IsAvailable = true,
            ObjectivePriorities = objectivePriorities ?? [],
            PrimaryObjective = primaryObjective,
            ObjectivePriorityTarget = objectiveTarget,
            FightPriorityTarget = fightTarget,
            RiskAssessment = risk,
            CommandSituation = new BattlefieldCommandSituationSnapshot
            {
                IsAvailable = true,
                Commands = allCommands,
                ActionCandidates = allActions,
                PrimaryCommand = primaryCommand,
                PrimaryAction = primaryAction,
                PublishedAction = primaryAction,
                Publish = new BattlefieldCommandPublishSnapshot
                {
                    ShouldAnnounce = canPublish,
                    IsSuppressed = !canPublish,
                    Command = primaryCommand,
                    SpeakText = primaryAction.Text,
                    PriorityText = "本地",
                    StatusText = "local",
                    SuppressionReason = canPublish ? string.Empty : gateText,
                    Sequence = 1
                }
            },
            ActionCandidates = allActions,
            PrimaryAction = primaryAction,
            PublishedAction = primaryAction,
            DecisionQuality = new BattlefieldDecisionQualitySnapshot
            {
                IsAvailable = true,
                InputReliability = new BattlefieldInputReliabilitySnapshot
                {
                    IsAvailable = true,
                    CanPublish = canPublish,
                    GateText = gateText
                }
            },
            RecommendedAction = primaryAction.Text,
            SummaryText = "local"
        };
    }

    private static BattlefieldSnapshot CreateSnapshot(
        BattlefieldDecisionSnapshot localDecision,
        BattlefieldTeamSituationSnapshot? teamSituation = null,
        BattlefieldScoreSituationSnapshot? scoreSituation = null)
        => new()
        {
            IsInFrontline = true,
            LocalPlayer = BattlefieldTestFactory.LocalPlayer(Vector3.Zero),
            TeamSituation = teamSituation ?? BattlefieldTestFactory.TeamSituation(
                Vector3.Zero,
                new[] { BattlefieldTestFactory.EnemyCluster(new Vector3(40f, 0f, 0f), 8, 40f) }),
            ScoreSituation = scoreSituation ?? BattlefieldTestFactory.ScoreSituation(FrontlineMapType.SealRock),
            LocalDecision = localDecision,
            Decision = localDecision
        };

    private static BattlefieldTeamSituationSnapshot CreateAllianceTargetTeamSituation()
        => new()
        {
            Friendly = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Friendly,
                Name = "Friendly",
                TotalCount = 12,
                AliveCount = 12,
                NearCount = 8,
                MidCount = 4,
                MainCluster = new BattlefieldPlayerClusterSnapshot(BattlefieldPlayerRelation.Friendly, 0, Vector3.Zero, 8, 0, 0, 0f)
            },
            Enemy = new BattlefieldTeamSummarySnapshot
            {
                Side = BattlefieldTacticalSide.Enemy,
                Name = "Enemy",
                TotalCount = 16,
                AliveCount = 16,
                NearCount = 8,
                MidCount = 8
            },
            EnemyClusters =
            [
                new BattlefieldEnemyClusterSnapshot
                {
                    ClusterId = 101,
                    Battalion = 1,
                    AllianceName = "Enemy1",
                    SourceText = "test",
                    Center = new Vector3(40f, 0f, 0f),
                    Count = 8,
                    Radius = 18f,
                    DistanceToLocal = 40f,
                    SeparationFromMain = 0f,
                    IsMainCluster = true
                },
                new BattlefieldEnemyClusterSnapshot
                {
                    ClusterId = 202,
                    Battalion = 2,
                    AllianceName = "Enemy2",
                    SourceText = "test",
                    Center = new Vector3(90f, 0f, 0f),
                    Count = 8,
                    Radius = 18f,
                    DistanceToLocal = 90f,
                    SeparationFromMain = 36f,
                    IsMainCluster = false
                }
            ],
            SummaryText = "alliances"
        };
}
