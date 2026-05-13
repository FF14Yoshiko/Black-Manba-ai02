using System;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ai02.Tests;

public sealed class LlmStrategicDecisionServiceTests
{
    [Fact]
    public async Task EvaluateAndMaybeRequest_UsesRoutinePulseForLowRiskObjective()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(CreateDecisionJson("rotate"))));
        using var service = CreateService(handler, out _, includeApiKey: true, configure: config =>
        {
            config.RoutinePulseEnabled = true;
            config.RoutinePulseIntervalSeconds = 25;
        });
        var snapshot = CreateRoutinePulseSnapshot();

        var initial = service.EvaluateAndMaybeRequest(snapshot);
        var final = await WaitForSnapshotAsync(service, snapshot, current => current.IsAvailable);
        var debug = service.GetDebugSnapshot(snapshot);

        Assert.True(initial.ShouldRequest);
        Assert.Equal(BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse, initial.NeedKind);
        Assert.Equal(BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse, final.NeedKind);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("常规战略采样", debug.CurrentRequestSourceText);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_UsesRoutinePulseEvenWhenImmediateLocalThreatExists()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(CreateDecisionJson("retreat"))));
        using var service = CreateService(handler, out _, includeApiKey: true, configure: config =>
        {
            config.RoutinePulseEnabled = true;
            config.RoutinePulseIntervalSeconds = 25;
        });
        var snapshot = CreateRoutinePulseSnapshot(immediateLocalThreat: true);

        var initial = service.EvaluateAndMaybeRequest(snapshot);
        var final = await WaitForSnapshotAsync(service, snapshot, current => current.IsAvailable);
        var debug = service.GetDebugSnapshot(snapshot);

        Assert.True(initial.ShouldRequest);
        Assert.Equal(BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse, initial.NeedKind);
        Assert.Equal(BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse, final.NeedKind);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal("常规战略采样", debug.CurrentRequestSourceText);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_UsesEventGateWhenNearbyThirdPartyFightIsDetected()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(CreateDecisionJson("engage"))));
        using var service = CreateService(handler, out _, includeApiKey: true);
        var snapshot = CreateNearbyThirdPartySnapshot();

        var initial = service.EvaluateAndMaybeRequest(snapshot);
        var final = await WaitForSnapshotAsync(service, snapshot, current => current.IsAvailable);
        var debug = service.GetDebugSnapshot(snapshot);

        Assert.True(initial.ShouldRequest);
        Assert.Equal(BattlefieldLlmDecisionNeedKind.NearbyThirdPartyFight, initial.NeedKind);
        Assert.Equal(BattlefieldLlmDecisionNeedKind.NearbyThirdPartyFight, final.NeedKind);
        Assert.Equal(1, handler.SendCount);
        Assert.Contains("事件触发", debug.CurrentRequestSourceText);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_EventRequestAlsoRefreshesRoutinePulseTimestampWhenDue()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(CreateDecisionJson("engage"))));
        using var service = CreateService(handler, out _, includeApiKey: true);
        var snapshot = CreateNearbyThirdPartySnapshot();

        _ = service.EvaluateAndMaybeRequest(snapshot);
        await WaitForSnapshotAsync(service, snapshot, current => current.IsAvailable);
        var debug = service.GetDebugSnapshot(snapshot);

        Assert.Contains("事件触发", debug.CurrentRequestSourceText);
        Assert.True(debug.LastRoutinePulseRequestedAtUnixMs > 0);
        Assert.InRange(debug.RoutinePulseRemainingSeconds, 1, 25);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_DueRoutinePulseBypassesEventMinInterval()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(CreateDecisionJson("engage"))));
        using var service = CreateService(handler, out _, includeApiKey: true, configure: config =>
        {
            config.MinIntervalSeconds = 30;
            config.RoutinePulseEnabled = true;
            config.RoutinePulseIntervalSeconds = 25;
        });
        var snapshot = CreateNearbyThirdPartySnapshot();

        _ = service.EvaluateAndMaybeRequest(snapshot);
        await WaitForSnapshotAsync(service, snapshot, current => current.IsAvailable);
        Assert.Equal(1, handler.SendCount);

        var now = Environment.TickCount64;
        SetPrivateLongField(service, "lastRequestTicks", now - 1000);
        SetPrivateLongField(service, "lastRoutinePulseTicks", now - 25_000);

        _ = service.EvaluateAndMaybeRequest(snapshot);
        await WaitForSendCountAsync(handler, expected: 2);

        Assert.Equal(2, handler.SendCount);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_DeduplicatesWhileRequestIsPending()
    {
        var requestStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResponse = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            requestStarted.TrySetResult(true);
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return CreateCompletionResponse(CreateDecisionJson("engage"));
        });
        using var service = CreateService(handler, out _, includeApiKey: true);
        var snapshot = CreateNearbyThirdPartySnapshot();

        _ = service.EvaluateAndMaybeRequest(snapshot);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, handler.SendCount);

        _ = service.EvaluateAndMaybeRequest(snapshot);
        await Task.Delay(100);
        Assert.Equal(1, handler.SendCount);
        Assert.True(service.GetSnapshot(snapshot).IsPending);

        releaseResponse.TrySetResult(true);
        await WaitForSnapshotAsync(service, snapshot, current => current.IsAvailable);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_ReportsInvalidJsonResponse()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse("not-json")));
        using var service = CreateService(handler, out var log, includeApiKey: true);
        var snapshot = CreateNearbyThirdPartySnapshot();

        _ = service.EvaluateAndMaybeRequest(snapshot);
        var failed = await WaitForSnapshotAsync(service, snapshot, current => !current.IsPending && !current.IsAvailable && !string.IsNullOrWhiteSpace(current.ErrorText));
        var debug = service.GetDebugSnapshot(snapshot);

        Assert.False(failed.IsAvailable);
        Assert.Equal("not-json", debug.RawResponse);
        Assert.Empty(debug.ParsedJson);
        Assert.NotEmpty(log.DebugEntries);
    }

    [Fact]
    public async Task Apply_KeepsLocalDecisionWhenServiceReturnsUnknownActionType()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(CreateDecisionJson("teleport_everyone"))));
        using var service = CreateService(handler, out _, includeApiKey: true);
        var snapshot = CreateNearbyThirdPartySnapshot();

        _ = service.EvaluateAndMaybeRequest(snapshot);
        var llmDecision = await WaitForSnapshotAsync(service, snapshot, current => current.IsAvailable);
        var arbitration = new StrategicArbitrationService();

        var result = arbitration.Apply(snapshot, snapshot.LocalDecision, llmDecision);

        Assert.Equal("teleport_everyone", llmDecision.ActionType);
        Assert.Same(snapshot.LocalDecision, result);
    }

    [Fact]
    public void EvaluateAndMaybeRequest_WaitsForApiKeyWithoutSendingRequest()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(CreateDecisionJson("engage"))));
        using var service = CreateService(handler, out _, includeApiKey: false);
        var snapshot = CreateNearbyThirdPartySnapshot();

        var result = service.EvaluateAndMaybeRequest(snapshot);

        Assert.True(result.IsEnabled);
        Assert.False(result.IsConfigured);
        Assert.False(result.IsAvailable);
        Assert.Equal(BattlefieldLlmDecisionNeedKind.NearbyThirdPartyFight, result.NeedKind);
        Assert.Contains("API Key", result.StatusText);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_ReportsTimeoutWithoutLoggingDebugException()
    {
        using var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var service = CreateService(handler, out var log, includeApiKey: true, configure: config => config.RequestTimeoutMs = 1500);
        var snapshot = CreateNearbyThirdPartySnapshot();

        _ = service.EvaluateAndMaybeRequest(snapshot);
        var failed = await WaitForSnapshotAsync(service, snapshot, current => !current.IsPending && !current.IsAvailable && !string.IsNullOrWhiteSpace(current.ErrorText), timeoutMs: 5000);
        var debug = service.GetDebugSnapshot(snapshot);

        Assert.False(failed.IsPending);
        Assert.NotEmpty(failed.ErrorText);
        Assert.Empty(debug.RawResponse);
        Assert.Empty(log.DebugEntries);
    }

    [Fact]
    public async Task EvaluateAndMaybeRequest_ReportsEmptyModelContent()
    {
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(CreateCompletionResponse(string.Empty)));
        using var service = CreateService(handler, out var log, includeApiKey: true);
        var snapshot = CreateNearbyThirdPartySnapshot();

        _ = service.EvaluateAndMaybeRequest(snapshot);
        var failed = await WaitForSnapshotAsync(service, snapshot, current => !current.IsPending && !current.IsAvailable && !string.IsNullOrWhiteSpace(current.ErrorText));
        var debug = service.GetDebugSnapshot(snapshot);

        Assert.False(failed.IsAvailable);
        Assert.Empty(debug.RawResponse);
        Assert.NotEmpty(log.DebugEntries);
    }

    private static LlmStrategicDecisionService CreateService(
        HttpMessageHandler handler,
        out TestPluginLog log,
        bool includeApiKey,
        Action<LlmDecisionConfiguration>? configure = null)
    {
        log = new TestPluginLog();
        var config = new Configuration
        {
            LlmDecision = new LlmDecisionConfiguration
            {
                Enabled = true,
                ProviderBaseUrl = "https://unit.test",
                Model = "test-model",
                ApiKey = includeApiKey ? "test-key" : string.Empty,
                RequestTimeoutMs = 4500,
                MinIntervalSeconds = 5,
                SameSituationCooldownSeconds = 8,
                RoutinePulseEnabled = true,
                RoutinePulseIntervalSeconds = 25,
                FreshDecisionSeconds = 40,
                MaxContextTurns = 6,
                IncludeDebugPayload = true
            }
        };
        configure?.Invoke(config.LlmDecision!);
        var httpClient = new HttpClient(handler);
        return new LlmStrategicDecisionService(config, log, httpClient);
    }

    private static BattlefieldSnapshot CreateRoutinePulseSnapshot(bool immediateLocalThreat = false)
    {
        var objective = BattlefieldTestFactory.ObjectivePriority(
            "safe",
            "Safe Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(60f, 0f, 20f),
            64f,
            8,
            20f,
            40f,
            scoreValue: 100,
            remainingSeconds: 24,
            recommendedAction: "rotate safe");
        var score = CreateFriendlyLeadScoreSituation(FrontlineMapType.OnsalHakair);
        var team = BattlefieldTestFactory.TeamSituation(
            Vector3.Zero,
            [CreateEnemyCluster(1, new Vector3(250f, 0f, 0f), 8, 250f)],
            friendlyAlive: 12,
            enemyAlive: 12);
        var risk = new BattlefieldRiskAssessmentSnapshot
        {
            OverallRisk = 24f,
            CombatRisk = 20f,
            ObjectiveRisk = 18f,
            ThirdPartyPincerRisk = 0f,
            RiskLevel = "Low",
            SummaryText = "routine"
        };
        return CreateSnapshot(objective, score, team, risk, immediateLocalThreat);
    }

    private static BattlefieldSnapshot CreateNearbyThirdPartySnapshot()
    {
        var objective = BattlefieldTestFactory.ObjectivePriority(
            "far",
            "Far Node",
            BattlefieldMapObjectiveCategory.Tomelith,
            BattlefieldMapObjectiveState.Active,
            new Vector3(180f, 0f, 0f),
            180f,
            20,
            36f,
            60f,
            scoreValue: 100,
            remainingSeconds: 30,
            recommendedAction: "engage nearby");
        var score = BattlefieldTestFactory.ScoreSituation(FrontlineMapType.OnsalHakair);
        var team = BattlefieldTestFactory.TeamSituation(
            Vector3.Zero,
            [
                CreateEnemyCluster(1, new Vector3(100f, 0f, 0f), 8, 100f),
                CreateEnemyCluster(2, new Vector3(130f, 0f, 0f), 8, 130f, isMain: false)
            ],
            friendlyAlive: 12,
            enemyAlive: 16);
        var risk = new BattlefieldRiskAssessmentSnapshot
        {
            OverallRisk = 36f,
            CombatRisk = 30f,
            ObjectiveRisk = 28f,
            ThirdPartyPincerRisk = 12f,
            RiskLevel = "Medium",
            SummaryText = "event"
        };
        return CreateSnapshot(objective, score, team, risk);
    }

    private static BattlefieldSnapshot CreateSnapshot(
        BattlefieldObjectivePrioritySnapshot objective,
        BattlefieldScoreSituationSnapshot score,
        BattlefieldTeamSituationSnapshot team,
        BattlefieldRiskAssessmentSnapshot risk,
        bool immediateLocalThreat = false)
    {
        var localCommand = immediateLocalThreat
            ? BattlefieldTestFactory.Command(
                "local:retreat",
                BattlefieldCommandKind.Retreat,
                "local retreat",
                new Vector3(-25f, 0f, 0f),
                "Enemy1",
                score: 88f,
                urgency: 92f)
            : BattlefieldTestFactory.Command(
                "local:engage",
                BattlefieldCommandKind.Engage,
                "local engage Enemy1",
                new Vector3(40f, 0f, 0f),
                "Enemy1",
                score: 82f,
                urgency: 70f);
        var localAction = immediateLocalThreat
            ? BattlefieldTestFactory.Action(
                "local:action:retreat",
                localCommand.Id,
                BattlefieldActionType.Retreat,
                BattlefieldCommandKind.Retreat,
                localCommand.CommandText,
                localCommand.Position,
                "alliance:2",
                localCommand.TargetName,
                priority: 88f,
                confidence: 80f,
                risk: Math.Max(risk.OverallRisk, 36f),
                urgency: 92f)
            : BattlefieldTestFactory.Action(
                "local:action:engage",
                localCommand.Id,
                BattlefieldActionType.Engage,
                BattlefieldCommandKind.Engage,
                localCommand.CommandText,
                localCommand.Position,
                "alliance:2",
                localCommand.TargetName,
                priority: 82f,
                confidence: 76f,
                risk: risk.OverallRisk,
                urgency: 70f);
        var publish = new BattlefieldCommandPublishSnapshot
        {
            Command = localCommand,
            PriorityText = "local",
            StatusText = "local",
            Sequence = 1
        };
        var decision = new BattlefieldDecisionSnapshot
        {
            IsAvailable = true,
            ObjectivePriorities = [objective],
            PrimaryObjective = objective,
            ObjectivePriorityTarget = BattlefieldTestFactory.PriorityTarget("Local", objective.Name, objective.RecommendedAction, objective.Position),
            FightPriorityTarget = BattlefieldTestFactory.PriorityTarget("Fight", localCommand.TargetName, localAction.Text, localAction.Destination),
            RiskAssessment = risk,
            CommandSituation = new BattlefieldCommandSituationSnapshot
            {
                IsAvailable = true,
                Commands = [localCommand],
                ActionCandidates = [localAction],
                PrimaryCommand = localCommand,
                PrimaryAction = localAction,
                PublishedAction = localAction,
                Publish = publish,
                SummaryText = "local"
            },
            ActionCandidates = [localAction],
            PrimaryAction = localAction,
            PublishedAction = localAction,
            DecisionQuality = new BattlefieldDecisionQualitySnapshot
            {
                IsAvailable = true,
                InputReliability = new BattlefieldInputReliabilitySnapshot
                {
                    IsAvailable = true,
                    OverallReliability = 82f,
                    ScoreReliability = 80f,
                    TimeReliability = 80f,
                    PlayerReliability = 80f,
                    ObjectiveReliability = 80f,
                    MapTacticsReliability = 80f,
                    CombatEventReliability = 80f,
                    AnnouncementReliability = 80f,
                    CanPublish = true,
                    GateText = "ready",
                    SummaryText = "ready"
                },
                SummaryText = "ready"
            },
            RecommendedAction = localAction.Text,
            SummaryText = "local"
        };

        return new BattlefieldSnapshot
        {
            UpdatedAtTicks = Environment.TickCount64,
            TerritoryType = 1,
            MapId = 1,
            IsInFrontline = true,
            MatchTimeRemaining = 600,
            LocalPlayer = BattlefieldTestFactory.LocalPlayer(Vector3.Zero),
            TeamSituation = team,
            ScoreSituation = score,
            TimeSituation = BattlefieldTestFactory.TimeSituation(600),
            MapObjectives =
            [
                BattlefieldTestFactory.Objective(
                    objective.ObjectiveId,
                    objective.Name,
                    score.MapType,
                    objective.Category,
                    objective.State,
                    objective.Position,
                    objective.ScoreValue,
                    objective.RemainingSeconds)
            ],
            LocalDecision = decision,
            Decision = decision
        };
    }

    private static BattlefieldScoreSituationSnapshot CreateFriendlyLeadScoreSituation(FrontlineMapType mapType)
    {
        var friendly = new BattlefieldAllianceScoreSnapshot(1, 0, "Local", BattlefieldPlayerRelation.Friendly, true, 900, 1600, 1, "1", true, 30, 10, 0.33f);
        var enemy1 = new BattlefieldAllianceScoreSnapshot(2, 1, "Enemy1", BattlefieldPlayerRelation.Enemy, false, 860, 1600, 2, "2", false, 30, 0, 0f);
        var enemy2 = new BattlefieldAllianceScoreSnapshot(3, 2, "Enemy2", BattlefieldPlayerRelation.Enemy, false, 840, 1600, 3, "3", false, 30, -10, -0.33f);
        return new BattlefieldScoreSituationSnapshot
        {
            HasScoreData = true,
            MapType = mapType,
            MapName = mapType.ToString(),
            VictoryScore = 1600,
            Alliances = [friendly, enemy1, enemy2],
            RankedAlliances = [friendly, enemy1, enemy2],
            FriendlyAlliance = friendly,
            EnemyAlliance1 = enemy1,
            EnemyAlliance2 = enemy2,
            SummaryText = "score ready"
        };
    }

    private static BattlefieldEnemyClusterSnapshot CreateEnemyCluster(byte battalion, Vector3 center, int count, float distanceToLocal, bool isMain = true)
        => new()
        {
            ClusterId = battalion * 10 + (isMain ? 1 : 2),
            Battalion = battalion,
            AllianceName = $"Enemy{battalion}",
            SourceText = "test",
            Center = center,
            Count = count,
            Radius = 18f,
            DistanceToLocal = distanceToLocal,
            SeparationFromMain = 0f,
            IsMainCluster = isMain
        };

    private static HttpResponseMessage CreateCompletionResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""
                {
                  "choices": [
                    {
                      "message": {
                        "content": {{System.Text.Json.JsonSerializer.Serialize(content)}}
                      }
                    }
                  ]
                }
                """,
                Encoding.UTF8,
                "application/json")
        };

    private static string CreateDecisionJson(string actionType)
        => $$"""
        {
          "decision": "test decision",
          "short_reason": "test reason",
          "action_type": "{{actionType}}",
          "recommended_action": "test action",
          "priority_target": "test target",
          "confidence": 88,
          "risk": 22,
          "debug": {
            "score_read": "ok",
            "position_read": "ok",
            "latency_note": "ok"
          }
        }
        """;

    private static void SetPrivateLongField(object target, string fieldName, long value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static async Task WaitForSendCountAsync(StubHttpMessageHandler handler, int expected, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (handler.SendCount >= expected)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for HTTP send count >= {expected}.");
    }

    private static async Task<BattlefieldLlmStrategicDecisionSnapshot> WaitForSnapshotAsync(
        LlmStrategicDecisionService service,
        BattlefieldSnapshot snapshot,
        Func<BattlefieldLlmStrategicDecisionSnapshot, bool> predicate,
        int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var current = service.GetSnapshot(snapshot);
            if (predicate(current))
                return current;

            await Task.Delay(25);
        }

        throw new TimeoutException("Timed out waiting for LLM strategic decision service state.");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler;
        private int sendCount;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
            => this.handler = handler;

        public int SendCount => Volatile.Read(ref sendCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref sendCount);
            return handler(request, cancellationToken);
        }
    }
}
