using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Xunit;

namespace ai02.Tests;

public sealed class AiTeacherLearningServiceTests
{
    [Fact]
    public void Record_LearnsPositiveTeacherBiasFromAiLedCommands()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = new AiTeacherLearningService(() => true, () => directory);
            for (var i = 0; i < 4; i++)
            {
                var issuedAt = 100_000L + i * 40_000L;
                service.Record(CreateSnapshot(
                    issuedAt,
                    sequence: i + 1,
                    isAiLead: true,
                    friendlyScore: 800 + i * 10,
                    friendlyRank: 2,
                    risk: 44f));
                service.Record(CreateSnapshot(
                    issuedAt + 12_000L,
                    sequence: i + 1,
                    isAiLead: false,
                    friendlyScore: 840 + i * 10,
                    friendlyRank: 1,
                    risk: 28f));
            }

            var feedback = Assert.Single(service.GetCommandEffectivenessSnapshots());
            Assert.Equal(BattlefieldCommandKind.Rotate, feedback.Kind);
            Assert.True(feedback.SampleCount >= 4);
            Assert.True(feedback.AverageScore > 0f);
            Assert.True(feedback.Modifier > 0f);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Record_IgnoresLocalOnlySnapshots()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = new AiTeacherLearningService(() => true, () => directory);
            service.Record(CreateSnapshot(100_000L, sequence: 1, isAiLead: false, friendlyScore: 800, friendlyRank: 2, risk: 42f));
            service.Record(CreateSnapshot(112_000L, sequence: 1, isAiLead: false, friendlyScore: 830, friendlyRank: 2, risk: 40f));

            Assert.Empty(service.GetCommandEffectivenessSnapshots());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ResetLearningStats_ClearsPersistedFeedback()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = new AiTeacherLearningService(() => true, () => directory);
            for (var i = 0; i < 2; i++)
            {
                var issuedAt = 200_000L + i * 40_000L;
                service.Record(CreateSnapshot(issuedAt, sequence: i + 1, isAiLead: true, friendlyScore: 820, friendlyRank: 2, risk: 40f));
                service.Record(CreateSnapshot(issuedAt + 31_000L, sequence: i + 1, isAiLead: false, friendlyScore: 860, friendlyRank: 1, risk: 24f));
            }

            Assert.NotEmpty(service.GetCommandEffectivenessSnapshots());

            service.ResetLearningStats();

            Assert.Empty(service.GetCommandEffectivenessSnapshots());
            Assert.False(File.Exists(Path.Combine(directory, "ai_teacher_learning_stats.json")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Record_SeparatesCommandLearningByMap()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = new AiTeacherLearningService(() => true, () => directory);
            for (var i = 0; i < 2; i++)
            {
                var issuedAt = 300_000L + i * 40_000L;
                service.Record(CreateSnapshot(
                    issuedAt,
                    sequence: i + 1,
                    isAiLead: true,
                    friendlyScore: 840,
                    friendlyRank: 2,
                    risk: 40f,
                    mapType: FrontlineMapType.OnsalHakair));
                service.Record(CreateSnapshot(
                    issuedAt + 31_000L,
                    sequence: i + 1,
                    isAiLead: false,
                    friendlyScore: 900,
                    friendlyRank: 1,
                    risk: 24f,
                    mapType: FrontlineMapType.OnsalHakair));
            }

            Assert.NotEmpty(service.GetCommandEffectivenessSnapshots(FrontlineMapType.OnsalHakair));
            Assert.Empty(service.GetCommandEffectivenessSnapshots(FrontlineMapType.FieldsOfHonor));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Record_TracksTargetResolutionLearningPerMap()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = new AiTeacherLearningService(() => true, () => directory);
            var highValueObjective = BattlefieldTestFactory.ObjectivePriority(
                "high-main",
                "High Main",
                BattlefieldMapObjectiveCategory.Tomelith,
                BattlefieldMapObjectiveState.Active,
                new Vector3(120f, 0f, 10f),
                120f,
                12,
                24f,
                95f,
                scoreValue: 120,
                recommendedAction: "rotate high");

            for (var i = 0; i < 2; i++)
            {
                var issuedAt = 400_000L + i * 40_000L;
                service.Record(CreateSnapshot(
                    issuedAt,
                    sequence: i + 1,
                    isAiLead: true,
                    friendlyScore: 860,
                    friendlyRank: 2,
                    risk: 38f,
                    mapType: FrontlineMapType.OnsalHakair,
                    aiObjective: highValueObjective,
                    llmRecommendedAction: "\u8f6c\u70b9 \u9ad8\u4ef7\u503c\u70b9"));
                service.Record(CreateSnapshot(
                    issuedAt + 31_000L,
                    sequence: i + 1,
                    isAiLead: false,
                    friendlyScore: 920,
                    friendlyRank: 1,
                    risk: 22f,
                    mapType: FrontlineMapType.OnsalHakair,
                    aiObjective: highValueObjective,
                    llmRecommendedAction: "\u8f6c\u70b9 \u9ad8\u4ef7\u503c\u70b9"));
            }

            var onsalStatus = service.GetStatus(FrontlineMapType.OnsalHakair);
            var shatterStatus = service.GetStatus(FrontlineMapType.FieldsOfHonor);
            var targetLearning = Assert.Single(onsalStatus.TargetResolutions.Where(item =>
                item.Kind == StrategicTargetResolutionKind.HighValueObjective
                && item.TargetName == "High Main"));
            Assert.True(targetLearning.SampleCount >= 2);
            Assert.Contains(onsalStatus.RecentLearned, item => item.Category == "\u843d\u70b9");
            Assert.Empty(shatterStatus.TargetResolutions);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Dispose_FlushesPendingLearningStatsToDisk()
    {
        var directory = CreateTempDirectory();
        try
        {
            using (var service = new AiTeacherLearningService(() => true, () => directory))
            {
                for (var i = 0; i < 2; i++)
                {
                    var issuedAt = 500_000L + i * 40_000L;
                    service.Record(CreateSnapshot(
                        issuedAt,
                        sequence: i + 1,
                        isAiLead: true,
                        friendlyScore: 860,
                        friendlyRank: 2,
                        risk: 36f,
                        mapType: FrontlineMapType.OnsalHakair));
                    service.Record(CreateSnapshot(
                        issuedAt + 31_000L,
                        sequence: i + 1,
                        isAiLead: false,
                        friendlyScore: 920,
                        friendlyRank: 1,
                        risk: 20f,
                        mapType: FrontlineMapType.OnsalHakair));
                }
            }

            var statsPath = Path.Combine(directory, "ai_teacher_learning_stats.json");
            Assert.True(File.Exists(statsPath));

            using var reloaded = new AiTeacherLearningService(() => true, () => directory);
            var feedback = Assert.Single(reloaded.GetCommandEffectivenessSnapshots(FrontlineMapType.OnsalHakair));
            Assert.True(feedback.SampleCount >= 2);
            Assert.True(feedback.AverageScore > 0f);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static BattlefieldSnapshot CreateSnapshot(
        long updatedAtTicks,
        int sequence,
        bool isAiLead,
        int friendlyScore,
        int friendlyRank,
        float risk,
        FrontlineMapType mapType = FrontlineMapType.OnsalHakair,
        BattlefieldObjectivePrioritySnapshot? aiObjective = null,
        string llmRecommendedAction = "")
    {
        var localPosition = Vector3.Zero;
        var defaultObjective = BattlefieldTestFactory.ObjectivePriority(
            "safe",
            "Safe Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(60f, 0f, 30f),
            66f,
            8,
            26f,
            82f,
            recommendedAction: "rotate safe");
        var teacherObjective = aiObjective ?? defaultObjective;

        var localCommand = BattlefieldTestFactory.Command(
            "local:command:engage",
            BattlefieldCommandKind.Engage,
            "local engage Enemy1",
            new Vector3(40f, 0f, 0f),
            "Enemy1",
            score: 78f,
            urgency: 76f);
        var localAction = BattlefieldTestFactory.Action(
            "local:action:engage",
            localCommand.Id,
            BattlefieldActionType.Engage,
            BattlefieldCommandKind.Engage,
            localCommand.CommandText,
            localCommand.Position,
            "alliance:2",
            localCommand.TargetName,
            priority: 78f,
            confidence: 72f,
            risk: 48f,
            urgency: 76f);

        var finalCommand = isAiLead
            ? BattlefieldTestFactory.Command(
                $"ai:command:{sequence}:Rotate:{teacherObjective.ObjectiveId}",
                BattlefieldCommandKind.Rotate,
                $"ai rotate {teacherObjective.Name}",
                teacherObjective.Position,
                teacherObjective.Name,
                score: 90f,
                urgency: 84f)
            : localCommand;
        var finalAction = isAiLead
            ? BattlefieldTestFactory.Action(
                $"ai:action:{sequence}:Rotate:{teacherObjective.ObjectiveId}",
                finalCommand.Id,
                BattlefieldActionType.Rotate,
                BattlefieldCommandKind.Rotate,
                finalCommand.CommandText,
                teacherObjective.Position,
                teacherObjective.ObjectiveId,
                teacherObjective.Name,
                priority: 90f,
                confidence: 86f,
                risk: risk,
                urgency: 84f,
                etaSeconds: teacherObjective.MountedEtaSeconds)
            : localAction;

        var localDecision = new BattlefieldDecisionSnapshot
        {
            IsAvailable = true,
            PrimaryObjective = defaultObjective,
            RiskAssessment = new BattlefieldRiskAssessmentSnapshot { OverallRisk = risk, CombatRisk = risk - 4f },
            CommandSituation = new BattlefieldCommandSituationSnapshot
            {
                IsAvailable = true,
                Commands = [localCommand],
                ActionCandidates = [localAction],
                PrimaryCommand = localCommand,
                PrimaryAction = localAction,
                PublishedAction = localAction,
                Publish = new BattlefieldCommandPublishSnapshot
                {
                    Command = localCommand,
                    Sequence = sequence,
                    PriorityText = "local"
                },
                SummaryText = "local"
            },
            ActionCandidates = [localAction],
            PrimaryAction = localAction,
            PublishedAction = localAction,
            RecommendedAction = localAction.Text,
            SummaryText = "local"
        };

        var finalDecision = new BattlefieldDecisionSnapshot
        {
            IsAvailable = true,
            PrimaryObjective = isAiLead ? teacherObjective : defaultObjective,
            ObjectivePriorityTarget = isAiLead
                ? BattlefieldTestFactory.PriorityTarget("AI", teacherObjective.Name, finalAction.Text, teacherObjective.Position)
                : default,
            RiskAssessment = new BattlefieldRiskAssessmentSnapshot { OverallRisk = risk, CombatRisk = risk - 4f },
            CommandSituation = new BattlefieldCommandSituationSnapshot
            {
                IsAvailable = true,
                Commands = [finalCommand],
                ActionCandidates = [finalAction],
                PrimaryCommand = finalCommand,
                PrimaryAction = finalAction,
                PublishedAction = finalAction,
                Publish = new BattlefieldCommandPublishSnapshot
                {
                    Command = finalCommand,
                    Sequence = sequence,
                    PriorityText = isAiLead ? "AI lead" : "local"
                },
                SummaryText = isAiLead ? "AI lead" : "local"
            },
            ActionCandidates = [finalAction],
            PrimaryAction = finalAction,
            PublishedAction = finalAction,
            RecommendedAction = finalAction.Text,
            SummaryText = isAiLead ? "AI lead" : "local"
        };

        return new BattlefieldSnapshot
        {
            UpdatedAtTicks = updatedAtTicks,
            TerritoryType = 1,
            MapId = 1,
            IsInFrontline = true,
            MatchTimeRemaining = Math.Max(0, 900 - (int)((updatedAtTicks - 100_000L) / 1000L)),
            LocalPlayer = BattlefieldTestFactory.LocalPlayer(localPosition),
            TeamSituation = BattlefieldTestFactory.TeamSituation(localPosition, Array.Empty<BattlefieldEnemyClusterSnapshot>(), friendlyAlive: 12, enemyAlive: 10),
            ScoreSituation = CreateScoreSituation(friendlyScore, friendlyRank, mapType),
            TimeSituation = BattlefieldTestFactory.TimeSituation(Math.Max(0, 900 - (int)((updatedAtTicks - 100_000L) / 1000L))),
            ChatEventSituation = new BattlefieldChatEventSituationSnapshot(),
            LlmStrategicDecision = new BattlefieldLlmStrategicDecisionSnapshot
            {
                IsAvailable = isAiLead,
                IsFresh = isAiLead,
                RecommendedAction = string.IsNullOrWhiteSpace(llmRecommendedAction) ? finalAction.Text : llmRecommendedAction,
                PriorityTarget = teacherObjective.Name,
                Confidence = isAiLead ? 88f : 0f
            },
            LocalDecision = localDecision,
            Decision = finalDecision
        };
    }

    private static BattlefieldScoreSituationSnapshot CreateScoreSituation(int friendlyScore, int friendlyRank, FrontlineMapType mapType)
    {
        var enemyLeadScore = Math.Max(friendlyScore + (friendlyRank == 1 ? -20 : 60), 0);
        var thirdScore = Math.Max(friendlyScore - 80, 0);
        var friendly = new BattlefieldAllianceScoreSnapshot(1, 0, "Local", BattlefieldPlayerRelation.Friendly, true, friendlyScore, 1600, friendlyRank, friendlyRank.ToString(), friendlyRank == 1, 30, 0, 0f);
        var enemy1 = new BattlefieldAllianceScoreSnapshot(2, 1, "Enemy1", BattlefieldPlayerRelation.Enemy, false, enemyLeadScore, 1600, enemyLeadScore >= friendlyScore ? 1 : 2, enemyLeadScore >= friendlyScore ? "1" : "2", enemyLeadScore >= friendlyScore, 30, 0, 0f);
        var enemy2 = new BattlefieldAllianceScoreSnapshot(3, 2, "Enemy2", BattlefieldPlayerRelation.Enemy, false, thirdScore, 1600, 3, "3", false, 30, 0, 0f);
        var ranked = new[] { friendly, enemy1, enemy2 };
        Array.Sort(ranked, static (left, right) => right.Score.CompareTo(left.Score));
        return new BattlefieldScoreSituationSnapshot
        {
            HasScoreData = true,
            MapType = mapType,
            MapName = mapType.ToString(),
            VictoryScore = 1600,
            Alliances = new[] { friendly, enemy1, enemy2 },
            RankedAlliances = ranked,
            FriendlyAlliance = friendly,
            EnemyAlliance1 = enemy1,
            EnemyAlliance2 = enemy2,
            SummaryText = "score"
        };
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ai02-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
