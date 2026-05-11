using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class LlmStrategicDecisionService : IDisposable
{
    private const float MountedYalmsPerSecond = 10f;
    private const int MatchDurationSeconds = 20 * 60;
    private const int MaxPromptObjectives = 6;
    private const int MaxPromptClusters = 6;
    private const int MaxPromptInsights = 5;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private static readonly string[] StrategyVerbs =
    {
        "转点", "参战", "等待", "撤退", "侧压", "夹击", "抢点", "守点",
        "放弃", "打第一", "打高分方", "断摸点", "收割低血", "绕后", "卡口"
    };

    private static readonly string[] StrategyNouns =
    {
        "主团", "目标点", "高价值点", "敌方第一", "敌方第二", "近侧敌团",
        "远点", "撤退线", "卡口", "冰", "无垢", "石文", "高战意目标"
    };

    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly HttpClient httpClient = new();
    private readonly object sync = new();
    private readonly Queue<LlmConversationTurn> conversation = new();
    private CancellationTokenSource? requestCancellation;
    private Task? requestTask;
    private BattlefieldLlmStrategicDecisionSnapshot? lastDecision;
    private LlmGateResult lastGate = LlmGateResult.None("尚未进入需要 AI 判断的局势");
    private string currentSessionId = string.Empty;
    private uint currentTerritoryType;
    private uint currentMapId;
    private int lastMatchTimeRemaining = -1;
    private long lastRequestTicks = -1;
    private string lastRequestSituationKey = string.Empty;
    private string lastErrorText = string.Empty;
    private LlmGateResult lastRequestGate = LlmGateResult.None("尚未发起 AI 请求");
    private string lastManualInstruction = string.Empty;
    private string lastSystemPrompt = string.Empty;
    private string lastUserPrompt = string.Empty;
    private string lastRawResponse = string.Empty;
    private string lastParsedJson = string.Empty;
    private long lastResponseTicks = -1;
    private bool disposed;

    public LlmStrategicDecisionService(Configuration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public BattlefieldLlmStrategicDecisionSnapshot EvaluateAndMaybeRequest(BattlefieldSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        var config = configuration.LlmDecision ?? new LlmDecisionConfiguration();
        config.Normalize();

        if (!snapshot.IsInFrontline)
        {
            ResetSession("离开纷争前线");
            return BuildRuntimeSnapshot(now, false, config);
        }

        EnsureSession(snapshot, now);
        var apiKey = ResolveApiKey(config);
        var gate = EvaluateGate(snapshot, now, config);
        lock (sync)
        {
            lastGate = gate;
        }

        if (gate.ShouldRequest && !string.IsNullOrWhiteSpace(apiKey))
            TryStartRequest(snapshot, gate, config, apiKey, now);

        return BuildRuntimeSnapshot(now, true, config);
    }

    public BattlefieldLlmStrategicDecisionSnapshot GetSnapshot(BattlefieldSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        var config = configuration.LlmDecision ?? new LlmDecisionConfiguration();
        config.Normalize();
        if (!snapshot.IsInFrontline)
        {
            ResetSession("离开纷争前线");
            return BuildRuntimeSnapshot(now, false, config);
        }

        EnsureSession(snapshot, now);
        return BuildRuntimeSnapshot(now, true, config);
    }

    public BattlefieldLlmDebugSnapshot GetDebugSnapshot(BattlefieldSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        var config = configuration.LlmDecision ?? new LlmDecisionConfiguration();
        config.Normalize();

        BattlefieldLlmStrategicDecisionSnapshot runtime;
        if (!snapshot.IsInFrontline)
        {
            ResetSession("离开纷争前线");
            runtime = BuildRuntimeSnapshot(now, false, config);
        }
        else
        {
            EnsureSession(snapshot, now);
            runtime = BuildRuntimeSnapshot(now, true, config);
        }

        LlmGateResult requestGate;
        string manualInstruction;
        string systemPrompt;
        string userPrompt;
        string rawResponse;
        string parsedJson;
        long responseTicks;
        LlmConversationTurn[] turns;
        lock (sync)
        {
            requestGate = lastRequestGate;
            manualInstruction = lastManualInstruction;
            systemPrompt = lastSystemPrompt;
            userPrompt = lastUserPrompt;
            rawResponse = lastRawResponse;
            parsedJson = lastParsedJson;
            responseTicks = lastResponseTicks;
            turns = conversation.ToArray();
        }

        var hasRequest = runtime.RequestedAtTicks >= 0
            || !string.IsNullOrWhiteSpace(systemPrompt)
            || !string.IsNullOrWhiteSpace(userPrompt)
            || !string.IsNullOrWhiteSpace(rawResponse);
        var ageSeconds = responseTicks >= 0
            ? Math.Max(0, (int)((now - responseTicks) / 1000L))
            : runtime.AgeSeconds;

        return new BattlefieldLlmDebugSnapshot
        {
            IsEnabled = runtime.IsEnabled,
            IsConfigured = runtime.IsConfigured,
            IsPending = runtime.IsPending,
            HasRequest = hasRequest,
            StatusText = runtime.StatusText,
            SessionId = runtime.SessionId,
            CurrentNeedText = runtime.NeedText,
            CurrentGateReason = runtime.GateReason,
            LastRequestNeedText = hasRequest ? NeedKindText(requestGate.NeedKind) : string.Empty,
            LastRequestGateReason = hasRequest ? requestGate.Reason : string.Empty,
            LastRequestSituationKey = hasRequest ? requestGate.SituationKey : string.Empty,
            ManualInstruction = manualInstruction,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            RawResponse = rawResponse,
            ParsedJson = parsedJson,
            DebugText = runtime.DebugText,
            DebugScoreRead = runtime.DebugScoreRead,
            DebugPositionRead = runtime.DebugPositionRead,
            DebugLatencyNote = runtime.DebugLatencyNote,
            ErrorText = runtime.ErrorText,
            RequestedAtTicks = runtime.RequestedAtTicks,
            ReceivedAtTicks = responseTicks >= 0 ? responseTicks : runtime.ReceivedAtTicks,
            AgeSeconds = ageSeconds,
            ConversationTurns = turns
                .Reverse()
                .Select(turn => new BattlefieldLlmConversationTurnSnapshot(
                    turn.Ticks,
                    turn.MatchRemainingSeconds,
                    NeedKindText(turn.NeedKind),
                    turn.OperatorNote,
                    turn.Decision,
                    turn.ShortReason,
                    turn.Confidence,
                    turn.SituationKey))
                .ToArray()
        };
    }

    public string RequestManualProbe(BattlefieldSnapshot snapshot, string operatorNote, bool requireOperatorNote)
    {
        var now = Environment.TickCount64;
        var config = configuration.LlmDecision ?? new LlmDecisionConfiguration();
        config.Normalize();
        operatorNote = operatorNote?.Trim() ?? string.Empty;

        if (!config.Enabled)
            return "AI 大决策未启用，先打开开关。";
        if (!snapshot.IsInFrontline)
            return "当前不在纷争前线内，手动测试请求未发送。";
        if (!ResolveLocalDecision(snapshot).IsAvailable)
            return "本地决策层尚未形成，稍后再试。";
        if (requireOperatorNote && string.IsNullOrWhiteSpace(operatorNote))
            return "请先输入你想对 AI 说的话。";

        EnsureSession(snapshot, now);
        var apiKey = ResolveApiKey(config);
        if (string.IsNullOrWhiteSpace(apiKey))
            return $"未配置 API Key，请先设置环境变量 {config.ApiKeyEnvironmentVariable} 或在插件配置里填写。";

        var reason = string.IsNullOrWhiteSpace(operatorNote)
            ? "手动忽略门控请求，直接测试 AI 回复延迟与 JSON 稳定性"
            : requireOperatorNote
                ? $"手动测试对话：{Truncate(operatorNote, 80)}"
                : $"手动忽略门控请求，并附带测试附言：{Truncate(operatorNote, 80)}";
        var gate = new LlmGateResult(
            true,
            BattlefieldLlmDecisionNeedKind.ManualProbe,
            reason,
            100f,
            BuildManualSituationKey(snapshot, operatorNote, now));
        lock (sync)
        {
            lastGate = gate;
        }

        return TryStartRequest(snapshot, gate, config, apiKey, now, true, operatorNote, out var failureReason)
            ? string.IsNullOrWhiteSpace(operatorNote)
                ? "已发送手动 AI 请求。"
                : requireOperatorNote ? "已发送测试对话请求。" : "已发送手动 AI 请求，并附带测试附言。"
            : failureReason;
    }

    public void Dispose()
    {
        disposed = true;
        requestCancellation?.Cancel();
        requestCancellation?.Dispose();
        httpClient.Dispose();
    }

    private void EnsureSession(BattlefieldSnapshot snapshot, long now)
    {
        var shouldReset = string.IsNullOrWhiteSpace(currentSessionId)
            || currentTerritoryType != snapshot.TerritoryType
            || currentMapId != snapshot.MapId
            || (lastMatchTimeRemaining >= 0
                && snapshot.MatchTimeRemaining > lastMatchTimeRemaining + 60
                && snapshot.MatchTimeRemaining >= MatchDurationSeconds - 90);

        if (shouldReset)
        {
            var elapsedSeconds = snapshot.TimeSituation.HasMatchTime
                ? Math.Clamp(MatchDurationSeconds - snapshot.MatchTimeRemaining, 0, MatchDurationSeconds)
                : 0;
            var startBucket = (now - elapsedSeconds * 1000L) / 60000L;
            lock (sync)
            {
                currentSessionId = $"{snapshot.TerritoryType}:{snapshot.MapId}:{startBucket}";
                currentTerritoryType = snapshot.TerritoryType;
                currentMapId = snapshot.MapId;
                conversation.Clear();
                lastDecision = null;
                lastGate = LlmGateResult.None("新战场上下文已开始，等待本地门控");
                lastErrorText = string.Empty;
                lastRequestTicks = -1;
                lastRequestSituationKey = string.Empty;
                ClearDebugArtifacts();
                requestCancellation?.Cancel();
                requestTask = null;
            }
        }

        if (snapshot.TimeSituation.HasMatchTime && snapshot.MatchTimeRemaining > 0)
            lastMatchTimeRemaining = snapshot.MatchTimeRemaining;
    }

    private void ResetSession(string reason)
    {
        lock (sync)
        {
            currentSessionId = string.Empty;
            currentTerritoryType = 0;
            currentMapId = 0;
            lastMatchTimeRemaining = -1;
            conversation.Clear();
            lastDecision = null;
            lastGate = LlmGateResult.None(reason);
            lastErrorText = string.Empty;
            lastRequestTicks = -1;
            lastRequestSituationKey = string.Empty;
            ClearDebugArtifacts();
            requestCancellation?.Cancel();
            requestTask = null;
        }
    }

    private BattlefieldLlmStrategicDecisionSnapshot BuildRuntimeSnapshot(long now, bool isInFrontline, LlmDecisionConfiguration config)
    {
        var apiKey = ResolveApiKey(config);
        LlmGateResult gate;
        BattlefieldLlmStrategicDecisionSnapshot? decision;
        string sessionId;
        string errorText;
        bool isPending;
        lock (sync)
        {
            gate = lastGate;
            decision = lastDecision;
            sessionId = currentSessionId;
            errorText = lastErrorText;
            isPending = requestTask is { IsCompleted: false };
        }

        if (!config.Enabled)
        {
            return new BattlefieldLlmStrategicDecisionSnapshot
            {
                IsEnabled = false,
                NeedKind = gate.NeedKind,
                NeedText = NeedKindText(gate.NeedKind),
                GateReason = gate.Reason,
                SessionId = sessionId,
                StatusText = "AI 大决策未启用"
            };
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new BattlefieldLlmStrategicDecisionSnapshot
            {
                IsEnabled = true,
                IsConfigured = false,
                NeedKind = gate.NeedKind,
                NeedText = NeedKindText(gate.NeedKind),
                GateReason = "未配置 API Key，本地状态机继续独立决策",
                SessionId = sessionId,
                StatusText = $"AI 大决策等待密钥：请设置环境变量 {config.ApiKeyEnvironmentVariable}，或在插件配置中填写 API Key"
            };
        }

        if (!isInFrontline)
        {
            return new BattlefieldLlmStrategicDecisionSnapshot
            {
                IsEnabled = true,
                IsConfigured = true,
                NeedKind = gate.NeedKind,
                NeedText = NeedKindText(gate.NeedKind),
                GateReason = gate.Reason,
                SessionId = sessionId,
                StatusText = "非纷争前线区域，AI 大决策待机"
            };
        }

        var ageSeconds = decision is { ReceivedAtTicks: >= 0 }
            ? Math.Max(0, (int)((now - decision.ReceivedAtTicks) / 1000L))
            : -1;
        var isFresh = ageSeconds >= 0 && ageSeconds <= config.FreshDecisionSeconds;
        var statusText = decision is { IsAvailable: true }
            ? isPending
                ? $"AI 大决策 {ageSeconds}秒前返回，正在刷新：{NeedKindText(gate.NeedKind)}"
                : isFresh
                    ? $"AI 大决策 {ageSeconds}秒前返回"
                    : $"AI 大决策已过期（{ageSeconds}秒），等待下一次门控触发"
            : isPending
                ? $"AI 大决策请求中：{NeedKindText(gate.NeedKind)}"
                : string.IsNullOrWhiteSpace(errorText)
                    ? $"本地门控：{gate.Reason}"
                    : $"AI 大决策失败：{errorText}";

        return new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsEnabled = true,
            IsConfigured = true,
            IsAvailable = decision?.IsAvailable == true,
            IsPending = isPending,
            ShouldRequest = gate.ShouldRequest,
            IsFresh = isFresh,
            NeedKind = gate.NeedKind,
            NeedText = NeedKindText(gate.NeedKind),
            GateReason = gate.Reason,
            SituationKey = gate.SituationKey,
            SessionId = sessionId,
            Decision = decision?.Decision ?? string.Empty,
            ShortReason = decision?.ShortReason ?? string.Empty,
            RecommendedAction = decision?.RecommendedAction ?? string.Empty,
            PriorityTarget = decision?.PriorityTarget ?? string.Empty,
            Confidence = decision?.Confidence ?? 0f,
            Risk = decision?.Risk ?? 0f,
            DebugText = decision?.DebugText ?? string.Empty,
            RawJson = decision?.RawJson ?? string.Empty,
            ErrorText = errorText,
            RequestedAtTicks = decision?.RequestedAtTicks ?? lastRequestTicks,
            ReceivedAtTicks = decision?.ReceivedAtTicks ?? -1,
            AgeSeconds = ageSeconds,
            StatusText = statusText
        };
    }

    private LlmGateResult EvaluateGate(BattlefieldSnapshot snapshot, long now, LlmDecisionConfiguration config)
    {
        var decision = ResolveLocalDecision(snapshot);
        var score = snapshot.ScoreSituation;
        var time = snapshot.TimeSituation;
        var input = decision.DecisionQuality.InputReliability;
        var risk = decision.RiskAssessment;
        var reliabilityThreshold = 20f;
        var playerReliabilityThreshold = 18f;

        if (!decision.IsAvailable)
            return LlmGateResult.None("本地决策层尚未形成，先不上传");
        if (!snapshot.LocalPlayer.HasValue && !snapshot.TeamSituation.Friendly.MainCluster.HasValue)
            return LlmGateResult.None("缺少本地位置或我方主团位置，先不上传");
        if (!score.HasScoreData)
            return LlmGateResult.None("缺少结构化比分，本地低风险规则兜底");
        if (!time.HasMatchTime)
            return LlmGateResult.None("缺少对局时间，本地低风险规则兜底");
        if (input.IsAvailable && (input.OverallReliability < reliabilityThreshold || input.PlayerReliability < playerReliabilityThreshold))
            return LlmGateResult.None($"输入可靠度偏低（总体 {input.OverallReliability:0} / 玩家 {input.PlayerReliability:0}），不上传");
        if (IsImmediateLocalThreat(decision))
            return LlmGateResult.None("即时战斗威胁由本地状态机处理，不等待 AI");

        var localCenter = ResolveLocalCenter(snapshot);
        var enemyCenters = ResolveEnemyAllianceCenters(snapshot).ToArray();
        var primary = decision.PrimaryObjective;
        var scorePressure = ResolveScorePressure(score, time);
        var closestEnemyDistance = ClosestEnemyDistance(localCenter, snapshot.TeamSituation.EnemyClusters);
        var enemyPair = ResolveClosestEnemyPair(enemyCenters);
        var enemyPairDistance = enemyPair.HasValue ? Distance2D(enemyPair.Value.Left.Center, enemyPair.Value.Right.Center) : float.MaxValue;
        var enemyPairMid = enemyPair.HasValue ? (enemyPair.Value.Left.Center + enemyPair.Value.Right.Center) * 0.5f : Vector3.Zero;
        var enemyPairEta = enemyPair.HasValue ? EstimateEtaSeconds(Distance2D(localCenter, enemyPairMid)) : 999;
        var scoreBalanced = IsScoreBalanced(score);
        var objectiveNearAndLowRisk = primary.HasValue
            && primary.Value.MountedEtaSeconds <= 8
            && primary.Value.RiskScore <= 30f
            && primary.Value.PriorityScore <= 46f
            && scorePressure < 10f;

        if (enemyPair.HasValue
            && enemyPairDistance <= 90f
            && enemyPairEta > 26
            && objectiveNearAndLowRisk
            && scoreBalanced)
        {
            return BuildRoutinePulseGate(
                "两家敌方交战较远，我方附近有低风险目标；允许 AI 作为常规战略采样补充判断",
                44f,
                snapshot,
                enemyCenters,
                now,
                config);
        }

        if (objectiveNearAndLowRisk
            && risk.OverallRisk <= 32f
            && !snapshot.TeamSituation.AdvancedTactics.IsThirdPartyPincerLikely
            && closestEnemyDistance > 190f)
        {
            return BuildRoutinePulseGate(
                "当前目标近、风险低、比分压力不高；允许 AI 参与常规局势采样",
                40f,
                snapshot,
                enemyCenters,
                now,
                config);
        }

        if (IsEndgameConflict(score, time, out var endgameReason))
        {
            return BuildGate(
                BattlefieldLlmDecisionNeedKind.ScoreEndgameConflict,
                $"{endgameReason}，需要按比分决定打谁或放弃哪些目标",
                88f,
                snapshot,
                enemyCenters);
        }

        if (enemyPair.HasValue
            && enemyPairDistance <= 135f
            && enemyPairEta <= 30
            && (scorePressure >= 6f || risk.ThirdPartyPincerRisk >= 26f || primary.HasValue && primary.Value.MountedEtaSeconds <= 42))
        {
            return BuildGate(
                BattlefieldLlmDecisionNeedKind.NearbyThirdPartyFight,
                $"两家敌方距离约 {enemyPairDistance:0}y，我方约 {enemyPairEta}秒可参战，需要结合比分决定夹击/等待/转点",
                Math.Clamp(72f + scorePressure * 0.20f, 0f, 96f),
                snapshot,
                enemyCenters);
        }

        if (primary.HasValue
            && primary.Value.MountedEtaSeconds >= 14
            && closestEnemyDistance <= 175f
            && primary.Value.PriorityScore >= 34f
            && (scorePressure >= 6f || IsHighValueObjective(primary.Value) || risk.ObjectiveRisk >= 30f))
        {
            return BuildGate(
                BattlefieldLlmDecisionNeedKind.FarObjectiveWithCloseEnemies,
                $"目标 {primary.Value.Name} 离我方约 {primary.Value.MountedEtaSeconds}秒，但近侧敌团约 {closestEnemyDistance:0}y，需要判断转点还是先处理近敌",
                Math.Clamp(66f + scorePressure * 0.24f + primary.Value.PriorityScore * 0.08f, 0f, 94f),
                snapshot,
                enemyCenters);
        }

        if (primary.HasValue
            && primary.Value.State is BattlefieldMapObjectiveState.Warning or BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested
            && primary.Value.MountedEtaSeconds <= 58
            && primary.Value.PriorityScore >= 44f
            && (primary.Value.RiskScore >= 30f || primary.Value.PressureScore >= 38f || risk.ObjectiveRisk >= 34f))
        {
            return BuildGate(
                BattlefieldLlmDecisionNeedKind.ObjectiveRace,
                $"高优先目标 {primary.Value.Name} 正在窗口期，预计 {primary.Value.MountedEtaSeconds}秒，局势不是纯低风险抢点",
                Math.Clamp(62f + primary.Value.PriorityScore * 0.18f + primary.Value.RiskScore * 0.12f, 0f, 92f),
                snapshot,
                enemyCenters);
        }

        if ((snapshot.TeamSituation.IsEnemySplit
                || snapshot.TeamSituation.AdvancedTactics.IsThirdPartyPincerLikely
                || snapshot.TeamSituation.AdvancedTactics.IsHighGroundDropPrepLikely
                || risk.ThirdPartyPincerRisk >= 34f)
            && (scorePressure >= 5f || primary.HasValue && primary.Value.PriorityScore >= 42f || risk.OverallRisk >= 38f))
        {
            return BuildGate(
                BattlefieldLlmDecisionNeedKind.UnstableThreeFaction,
                $"三方站位不稳定：{snapshot.TeamSituation.EnemySplitSummaryText}，需要让 AI 做宏观权衡",
                Math.Clamp(64f + scorePressure * 0.25f + risk.ThirdPartyPincerRisk * 0.15f, 0f, 94f),
                snapshot,
                enemyCenters);
        }

        if (HasScoreTargetAmbiguity(score, decision, out var ambiguityReason))
        {
            return BuildGate(
                BattlefieldLlmDecisionNeedKind.ScoreTargetAmbiguity,
                ambiguityReason,
                Math.Clamp(60f + scorePressure * 0.30f, 0f, 90f),
                snapshot,
                enemyCenters);
        }

        if (ShouldSampleStrategicSituation(snapshot, primary, scorePressure, closestEnemyDistance, enemyPairEta, risk, out var samplingReason, out var samplingUrgency))
        {
            return BuildGate(
                BattlefieldLlmDecisionNeedKind.StrategicSampling,
                samplingReason,
                samplingUrgency,
                snapshot,
                enemyCenters);
        }

        return BuildRoutinePulseGate(
            "当前未命中特典型复杂局势，但仍允许 AI 参与常规战略采样",
            Math.Clamp(42f + scorePressure * 0.25f + (primary.HasValue ? 6f : 0f) + (closestEnemyDistance <= 220f ? 6f : 0f), 0f, 72f),
            snapshot,
            enemyCenters,
            now,
            config);
    }

    private LlmGateResult BuildRoutinePulseGate(
        string reason,
        float urgency,
        BattlefieldSnapshot snapshot,
        IReadOnlyList<AllianceCenter> enemyCenters,
        long now,
        LlmDecisionConfiguration config)
    {
        long lastRequest;
        lock (sync)
            lastRequest = lastRequestTicks;

        var scheduling = LlmGateSchedulingPolicy.ApplyRoutinePulseGate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            config.RoutinePulseEnabled,
            config.RoutinePulseIntervalSeconds,
            lastRequest,
            now);
        if (!scheduling.ShouldRequest)
            return LlmGateResult.None(scheduling.WaitReason);

        var evaluation = LlmRoutinePulsePolicy.Evaluate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            config.RoutinePulseEnabled,
            config.RoutinePulseIntervalSeconds,
            lastRequest,
            now);
        if (!evaluation.IsDue)
            return LlmGateResult.None($"鍥哄畾灞€鍐呴噰鏍锋湭鍒帮紝璺濈涓嬩竴娆″父瑙勬垬鐣ラ噰鏍疯繕鍓?{evaluation.RemainingSeconds} 绉?");

        return BuildGate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            reason,
            urgency,
            snapshot,
            enemyCenters);
    }

    private LlmGateResult BuildGate(
        BattlefieldLlmDecisionNeedKind kind,
        string reason,
        float urgency,
        BattlefieldSnapshot snapshot,
        IReadOnlyList<AllianceCenter> enemyCenters)
    {
        var situationKey = BuildSituationKey(kind, snapshot, enemyCenters);
        return new LlmGateResult(true, kind, reason, Math.Clamp(urgency, 0f, 100f), situationKey);
    }

    private void TryStartRequest(BattlefieldSnapshot snapshot, LlmGateResult gate, LlmDecisionConfiguration config, string apiKey, long now)
        => _ = TryStartRequest(snapshot, gate, config, apiKey, now, false, string.Empty, out _);

    private bool TryStartRequest(
        BattlefieldSnapshot snapshot,
        LlmGateResult gate,
        LlmDecisionConfiguration config,
        string apiKey,
        long now,
        bool ignoreRateLimit,
        string manualInstruction,
        out string failureReason)
    {
        failureReason = string.Empty;
        lock (sync)
        {
            if (disposed)
            {
                failureReason = "插件已释放，请重新打开插件后再试。";
                return false;
            }
            if (requestTask is { IsCompleted: false })
            {
                failureReason = "已有 AI 请求在进行中，等这一条回来再发。";
                return false;
            }
            var minIntervalMs = ResolveMinIntervalMs(config, gate);
            if (!ignoreRateLimit && lastRequestTicks >= 0 && now - lastRequestTicks < minIntervalMs)
            {
                var remaining = Math.Max(1, (int)Math.Ceiling((minIntervalMs - (now - lastRequestTicks)) / 1000d));
                failureReason = $"最小请求间隔还没到，请再等 {remaining} 秒。";
                return false;
            }
            var sameSituationCooldownMs = ResolveSameSituationCooldownMs(config, gate);
            if (!ignoreRateLimit
                && string.Equals(lastRequestSituationKey, gate.SituationKey, StringComparison.Ordinal)
                && lastRequestTicks >= 0
                && now - lastRequestTicks < sameSituationCooldownMs)
            {
                var remaining = Math.Max(1, (int)Math.Ceiling((sameSituationCooldownMs - (now - lastRequestTicks)) / 1000d));
                failureReason = $"同局势冷却还没到，请再等 {remaining} 秒。";
                return false;
            }

            lastRequestTicks = now;
            lastRequestSituationKey = gate.SituationKey;
            lastErrorText = string.Empty;
            requestCancellation?.Dispose();
            requestCancellation = new CancellationTokenSource();
            var requestContext = BuildRequestContext(snapshot, gate, config, apiKey, now, manualInstruction);
            lastRequestGate = gate;
            lastManualInstruction = requestContext.ManualInstruction;
            lastSystemPrompt = requestContext.SystemPrompt;
            lastUserPrompt = requestContext.UserPrompt;
            lastRawResponse = string.Empty;
            lastParsedJson = string.Empty;
            lastResponseTicks = -1;
            requestTask = Task.Run(() => ExecuteRequestAsync(requestContext, requestCancellation.Token));
            return true;
        }
    }

    private LlmRequestContext BuildRequestContext(
        BattlefieldSnapshot snapshot,
        LlmGateResult gate,
        LlmDecisionConfiguration config,
        string apiKey,
        long requestedAtTicks,
        string manualInstruction)
    {
        LlmConversationTurn[] previousTurns;
        string sessionId;
        lock (sync)
        {
            previousTurns = conversation.ToArray();
            sessionId = currentSessionId;
        }

        return new LlmRequestContext(
            config.ProviderBaseUrl,
            config.Model,
            apiKey,
            config.RequestTimeoutMs,
            sessionId,
            requestedAtTicks,
            gate,
            BuildSystemPrompt(),
            BuildUserPrompt(snapshot, gate, sessionId, previousTurns, config.IncludeDebugPayload, manualInstruction),
            manualInstruction);
    }

    private async Task ExecuteRequestAsync(LlmRequestContext context, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(context.TimeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var rawResponse = await SendChatCompletionAsync(context, linked.Token).ConfigureAwait(false);
            var receivedAtTicks = Environment.TickCount64;
            lock (sync)
            {
                if (!string.Equals(context.SessionId, currentSessionId, StringComparison.Ordinal))
                    return;

                lastRawResponse = rawResponse;
                lastResponseTicks = receivedAtTicks;
            }

            var parsedJson = ExtractJsonObject(rawResponse);
            var decision = ParseDecisionResponse(parsedJson, context, receivedAtTicks);
            lock (sync)
            {
                if (!string.Equals(context.SessionId, currentSessionId, StringComparison.Ordinal))
                    return;

                lastDecision = decision;
                lastErrorText = string.Empty;
                lastParsedJson = parsedJson;
                conversation.Enqueue(new LlmConversationTurn(
                    decision.ReceivedAtTicks,
                    lastMatchTimeRemaining,
                    context.Gate.NeedKind,
                    context.ManualInstruction,
                    decision.Decision,
                    decision.ShortReason,
                    decision.Confidence,
                    context.Gate.SituationKey));
                while (conversation.Count > Math.Max(0, configuration.LlmDecision?.MaxContextTurns ?? 6))
                    conversation.Dequeue();
            }
        }
        catch (OperationCanceledException)
        {
            lock (sync)
            {
                if (!string.Equals(context.SessionId, currentSessionId, StringComparison.Ordinal))
                    return;

                lastErrorText = "请求超时或已取消，本地状态机继续执行";
            }
        }
        catch (Exception ex)
        {
            lock (sync)
            {
                if (!string.Equals(context.SessionId, currentSessionId, StringComparison.Ordinal))
                    return;

                lastErrorText = ex.Message;
            }

            log.Debug(ex, "[LLM] AI 大决策请求失败");
        }
    }

    private async Task<string> SendChatCompletionAsync(LlmRequestContext context, CancellationToken cancellationToken)
    {
        var endpoint = $"{context.BaseUrl.TrimEnd('/')}/chat/completions";
        var body = new
        {
            model = context.Model,
            messages = new[]
            {
                new { role = "system", content = context.SystemPrompt },
                new { role = "user", content = context.UserPrompt }
            },
            temperature = 0.2,
            thinking = new { type = "disabled" },
            max_tokens = 650,
            response_format = new { type = "json_object" }
        };
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.ApiKey);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(responseText, 220)}");

        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("模型返回缺少 choices");

        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var contentElement)
            ? contentElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("模型返回内容为空");

        return content;
    }

    private BattlefieldLlmStrategicDecisionSnapshot ParseDecisionResponse(string responseJson, LlmRequestContext context, long receivedAtTicks)
    {
        var parsed = LlmDecisionResponseParser.Parse(responseJson);
        var now = receivedAtTicks >= 0 ? receivedAtTicks : Environment.TickCount64;

        return new BattlefieldLlmStrategicDecisionSnapshot
        {
            IsEnabled = true,
            IsConfigured = true,
            IsAvailable = true,
            IsFresh = true,
            NeedKind = context.Gate.NeedKind,
            NeedText = NeedKindText(context.Gate.NeedKind),
            GateReason = context.Gate.Reason,
            SituationKey = context.Gate.SituationKey,
            SessionId = context.SessionId,
            Decision = Truncate(parsed.Decision, 220),
            ShortReason = Truncate(parsed.ShortReason, 260),
            RecommendedAction = Truncate(parsed.RecommendedAction, 160),
            PriorityTarget = Truncate(parsed.PriorityTarget, 80),
            Confidence = Math.Clamp(parsed.Confidence, 0f, 100f),
            Risk = Math.Clamp(parsed.Risk, 0f, 100f),
            DebugText = Truncate(parsed.DebugText, 600),
            DebugScoreRead = Truncate(parsed.DebugScoreRead, 260),
            DebugPositionRead = Truncate(parsed.DebugPositionRead, 260),
            DebugLatencyNote = Truncate(parsed.DebugLatencyNote, 220),
            RawJson = Truncate(responseJson, 1200),
            RequestedAtTicks = context.RequestedAtTicks,
            ReceivedAtTicks = now,
            AgeSeconds = 0,
            StatusText = "AI 大决策已返回"
        };
    }

    private static string BuildSystemPrompt()
        => """
你是《最终幻想14》纷争前线的 AI 战场大决策参谋。
本地插件已经负责即时战斗、集火、撤退、爆发窗口和低风险目标执行；你不要替代这些即时小决策。
你只做战略层大决策：是否转点、是否参战、是否等待、打哪一家、是否放弃远点、是否抢点/守点/断摸点。
你会看到同一场对局的上下文摘要和当前帧数据。当前帧可能有几秒延迟，请给出在延迟存在时仍稳健的最优策略。
如果请求里出现 manual_override，说明这是人工手动测试或追问；你要优先回应 operator_note，但仍然只能结合当前战场语境输出严格 JSON。
战场为三方联盟对抗，目标资源与击杀都会影响比分；领先方、快到胜利分的一方和高战意目标需要被纳入判断。
可组合动词：转点、参战、等待、撤退、侧压、夹击、抢点、守点、放弃、打第一、打高分方、断摸点、收割低血、绕后、卡口。
可组合名词：主团、目标点、高价值点、敌方第一、敌方第二、近侧敌团、远点、撤退线、卡口、冰、无垢、石文、高战意目标。
只输出严格 JSON，不要 Markdown，不要额外解释。格式：
{
  "decision": "一句完整大决策",
  "short_reason": "一句简短理由，给调试界面看",
  "confidence": 0-100,
  "risk": 0-100,
  "priority_target": "优先目标或阵营",
  "recommended_action": "由动词+名词拼出的短行动",
  "debug": { "score_read": "...", "position_read": "...", "latency_note": "..." }
}
""";

    private string BuildUserPrompt(
        BattlefieldSnapshot snapshot,
        LlmGateResult gate,
        string sessionId,
        IReadOnlyList<LlmConversationTurn> previousTurns,
        bool includeDebugPayload,
        string manualInstruction)
    {
        var payload = BuildPromptPayload(snapshot, gate, sessionId, previousTurns, includeDebugPayload, manualInstruction);
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var header = string.IsNullOrWhiteSpace(manualInstruction)
            ? "这是同一场纷争前线对局中的一次大决策请求。请基于门控原因和当前帧数据输出 JSON。"
            : "这是同一场纷争前线对局中的一次人工手动测试/对话请求。请优先回应 manual_override.operator_note，但仍只输出 JSON。";
        return $"{header}\n{json}";
    }

    private object BuildPromptPayload(
        BattlefieldSnapshot snapshot,
        LlmGateResult gate,
        string sessionId,
        IReadOnlyList<LlmConversationTurn> previousTurns,
        bool includeDebugPayload,
        string manualInstruction)
    {
        var score = snapshot.ScoreSituation;
        var decision = ResolveLocalDecision(snapshot);
        var risk = decision.RiskAssessment;
        var input = decision.DecisionQuality.InputReliability;
        var map = snapshot.Knowledge.CurrentMap;
        int? effectiveMatchRemainingSeconds = snapshot.TimeSituation.HasMatchTime
            ? snapshot.MatchTimeRemaining
            : lastMatchTimeRemaining > 0
                ? lastMatchTimeRemaining
                : null;
        var effectivePhaseDetail = snapshot.TimeSituation.HasMatchTime
            ? snapshot.TimeSituation.MatchPhaseDetail
            : snapshot.IsInFrontline
                ? "对局进行中（时间读数暂缺）"
                : snapshot.TimeSituation.MatchPhaseDetail;

        return new
        {
            session = new
            {
                id = sessionId,
                map = !string.IsNullOrWhiteSpace(score.MapName) ? score.MapName : map?.Name ?? snapshot.MapTactics.MapName,
                territory = snapshot.TerritoryType,
                map_id = snapshot.MapId,
                match_remaining_seconds = effectiveMatchRemainingSeconds,
                phase = effectivePhaseDetail,
                previous_ai_decisions = previousTurns.Select(turn => new
                {
                    match_remaining_seconds = turn.MatchRemainingSeconds,
                    need = NeedKindText(turn.NeedKind),
                    operator_note = turn.OperatorNote,
                    decision = turn.Decision,
                    reason = turn.ShortReason,
                    confidence = turn.Confidence
                }).ToArray()
            },
            manual_override = string.IsNullOrWhiteSpace(manualInstruction)
                ? null
                : new
                {
                    enabled = true,
                    operator_note = manualInstruction,
                    purpose = "人工联调、延迟测试、JSON 稳定性验证或追问"
                },
            rules = new
            {
                world = "纷争前线三方联盟混战；本地状态机处理即时战斗，AI 只判断大决策。",
                map_summary = map?.SummaryText ?? snapshot.Knowledge.SummaryText,
                map_objective = map?.PrimaryObjective ?? string.Empty,
                ranking_rule = map?.RankingRule ?? string.Empty,
                hints = (map?.DecisionHints ?? snapshot.Knowledge.DecisionHints)
                    .Take(5)
                    .Select(hint => new { hint.Trigger, hint.Recommendation, hint.Reason })
                    .ToArray()
            },
            gate = new
            {
                need = NeedKindText(gate.NeedKind),
                reason = gate.Reason,
                urgency = gate.Urgency,
                situation_key = gate.SituationKey,
                latency_notice = "当前帧和你的回复都可能延迟，避免给需要秒级反应的指令。"
            },
            allowed_strategy = new
            {
                verbs = StrategyVerbs,
                nouns = StrategyNouns
            },
            local_state_machine = new
            {
                recommended_action = decision.RecommendedAction,
                primary_command = decision.CommandSituation.PrimaryCommand?.CommandText ?? string.Empty,
                emergency_command = decision.CommandSituation.EmergencyCommand?.CommandText ?? string.Empty,
                primary_action = decision.PrimaryAction?.Text ?? string.Empty,
                objective_target = decision.ObjectivePriorityTarget?.TargetName ?? string.Empty,
                fight_target = decision.FightPriorityTarget?.TargetName ?? string.Empty,
                input_reliability = input.IsAvailable
                    ? new
                    {
                        input.OverallReliability,
                        input.ScoreReliability,
                        input.PlayerReliability,
                        input.ObjectiveReliability,
                        input.MapTacticsReliability,
                        input.CanPublish,
                        input.SummaryText
                    }
                    : null
            },
            score = new
            {
                has_score = score.HasScoreData,
                victory_score = score.VictoryScore,
                summary = score.SummaryText,
                alliances = score.RankedAlliances.Select(alliance => new
                {
                    alliance.Name,
                    relation = RelationText(alliance.Relation, alliance.IsLocalAlliance),
                    alliance.Score,
                    alliance.RankIndex,
                    alliance.RankText,
                    alliance.ScoreDelta30s,
                    alliance.ScorePerSecond30s
                }).ToArray()
            },
            risk = new
            {
                risk.RiskLevel,
                risk.OverallRisk,
                risk.CombatRisk,
                risk.ObjectiveRisk,
                risk.FlankRisk,
                risk.NumberDisadvantageRisk,
                risk.ThirdPartyPincerRisk,
                risk.EnemyMainGroupDirectionRisk,
                risk.LimitBreakRisk,
                risk.SkillThreatRisk,
                risk.ChokeBlockRisk,
                risk.SummaryText
            },
            teams = new
            {
                friendly = BuildTeamPayload(snapshot.TeamSituation.Friendly),
                enemy = BuildTeamPayload(snapshot.TeamSituation.Enemy),
                enemy_main_group = new
                {
                    snapshot.TeamSituation.EnemyMainGroupMovement.HasMainGroup,
                    snapshot.TeamSituation.EnemyMainGroupMovement.AllianceName,
                    center = PositionPayload(snapshot.TeamSituation.EnemyMainGroupMovement.CurrentCenter),
                    snapshot.TeamSituation.EnemyMainGroupMovement.PlayerCount,
                    snapshot.TeamSituation.EnemyMainGroupMovement.DirectionText,
                    snapshot.TeamSituation.EnemyMainGroupMovement.SummaryText
                },
                enemy_split = snapshot.TeamSituation.EnemySplitSummaryText,
                enemy_clusters = snapshot.TeamSituation.EnemyClusters
                    .Take(MaxPromptClusters)
                    .Select(cluster => new
                    {
                        cluster.AllianceName,
                        cluster.Count,
                        cluster.Radius,
                        cluster.DistanceToLocal,
                        cluster.SeparationFromMain,
                        cluster.IsMainCluster,
                        center = PositionPayload(cluster.Center)
                    }).ToArray()
            },
            objectives = decision.ObjectivePriorities
                .Take(MaxPromptObjectives)
                .Select(objective => new
                {
                    objective.Name,
                    category = objective.Category.ToString(),
                    state = objective.State.ToString(),
                    objective.OwnershipText,
                    objective.ScoreValue,
                    objective.RemainingSeconds,
                    objective.DistanceToLocal,
                    objective.MountedEtaSeconds,
                    objective.PriorityScore,
                    objective.RiskScore,
                    objective.PressureScore,
                    objective.TeamAdvantageScore,
                    objective.RecommendedAction,
                    position = PositionPayload(objective.Position)
                }).ToArray(),
            tactical = new
            {
                map_recommendation = snapshot.MapTactics.CurrentRecommendation,
                map_summary = snapshot.MapTactics.SummaryText,
                advanced_summary = snapshot.TeamSituation.AdvancedTactics.SummaryText,
                advanced_flags = new
                {
                    snapshot.TeamSituation.AdvancedTactics.IsEnemyFakeRetreatAmbushLikely,
                    snapshot.TeamSituation.AdvancedTactics.IsThirdPartyPincerLikely,
                    snapshot.TeamSituation.AdvancedTactics.IsHighGroundDropPrepLikely,
                    snapshot.TeamSituation.AdvancedTactics.IsChokeBlockedLikely,
                    snapshot.TeamSituation.AdvancedTactics.IsCoordinatedSquadLikely
                },
                insights = snapshot.TeamSituation.AdvancedTactics.Insights
                    .Take(MaxPromptInsights)
                    .Select(insight => new
                    {
                        insight.Label,
                        insight.Severity,
                        insight.Confidence,
                        insight.AllianceName,
                        insight.Recommendation,
                        insight.EvidenceText,
                        position = PositionPayload(insight.Position)
                    }).ToArray()
            },
            recent_events = includeDebugPayload
                ? new
                {
                    chat = snapshot.ChatEventSituation.SummaryText,
                    frames = snapshot.PlayerFrameEvents.SummaryText,
                    announcements = snapshot.AnnouncementSituation.SummaryText,
                    limit_break = snapshot.LimitBreak.SummaryText
                }
                : null
        };
    }

    private static object BuildTeamPayload(BattlefieldTeamSummarySnapshot team)
        => new
        {
            team.Name,
            team.TotalCount,
            team.AliveCount,
            team.DeadCount,
            team.InCombatCount,
            team.LowHpCount,
            team.BattleHighCount,
            team.BattleFeverCount,
            team.MaxBattleHighLevel,
            team.GuardingCount,
            team.CrowdControlledCount,
            main_cluster = team.MainCluster.HasValue
                ? new
                {
                    team.MainCluster.Value.PlayerCount,
                    team.MainCluster.Value.DistanceToLocal,
                    center = PositionPayload(team.MainCluster.Value.Center)
                }
                : null
        };

    private static object PositionPayload(Vector3 position)
        => new { x = MathF.Round(position.X, 1), y = MathF.Round(position.Y, 1), z = MathF.Round(position.Z, 1) };

    private static bool IsImmediateLocalThreat(BattlefieldDecisionSnapshot decision)
    {
        var risk = decision.RiskAssessment;
        if (decision.CommandSituation.EmergencyCommand.HasValue)
            return true;
        if (risk.OverallRisk >= 84f || risk.CombatRisk >= 88f || risk.LimitBreakRisk >= 90f || risk.SkillThreatRisk >= 90f)
            return true;
        var action = decision.CommandSituation.PrimaryAction ?? decision.PrimaryAction;
        return action.HasValue
            && action.Value.ActionType is BattlefieldActionType.Retreat or BattlefieldActionType.Spread or BattlefieldActionType.Regroup
            && action.Value.Urgency >= 78f;
    }

    private static Vector3 ResolveLocalCenter(BattlefieldSnapshot snapshot)
    {
        if (snapshot.LocalPlayer.HasValue)
            return snapshot.LocalPlayer.Value.Position;
        if (snapshot.TeamSituation.Friendly.MainCluster.HasValue)
            return snapshot.TeamSituation.Friendly.MainCluster.Value.Center;
        return Vector3.Zero;
    }

    private static IEnumerable<AllianceCenter> ResolveEnemyAllianceCenters(BattlefieldSnapshot snapshot)
    {
        foreach (var alliance in snapshot.TeamSituation.Alliances.Where(alliance => !alliance.IsLocalAlliance && alliance.Relation == BattlefieldPlayerRelation.Enemy))
        {
            var center = ResolveAllianceCenter(alliance);
            if (center.HasValue)
                yield return new AllianceCenter(alliance.Name, alliance.Battalion, center.Value);
        }

        var knownBattalions = snapshot.TeamSituation.Alliances
            .Where(alliance => !alliance.IsLocalAlliance && alliance.Relation == BattlefieldPlayerRelation.Enemy && alliance.Battalion.HasValue)
            .Select(alliance => alliance.Battalion!.Value)
            .ToHashSet();
        foreach (var cluster in snapshot.TeamSituation.EnemyClusters.Where(cluster => cluster.Battalion.HasValue && !knownBattalions.Contains(cluster.Battalion.Value)))
        {
            yield return new AllianceCenter(cluster.AllianceName, cluster.Battalion, cluster.Center);
        }
    }

    private static Vector3? ResolveAllianceCenter(BattlefieldAllianceSituationSnapshot alliance)
    {
        if (alliance.MainMapVisionCluster.HasValue)
            return alliance.MainMapVisionCluster.Value.Center;
        if (alliance.MainPlayerCluster.HasValue)
            return alliance.MainPlayerCluster.Value.Center;
        return null;
    }

    private static (AllianceCenter Left, AllianceCenter Right)? ResolveClosestEnemyPair(IReadOnlyList<AllianceCenter> centers)
    {
        if (centers.Count < 2)
            return null;

        (AllianceCenter Left, AllianceCenter Right)? best = null;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < centers.Count; i++)
        {
            for (var j = i + 1; j < centers.Count; j++)
            {
                var distance = Distance2D(centers[i].Center, centers[j].Center);
                if (distance >= bestDistance)
                    continue;

                bestDistance = distance;
                best = (centers[i], centers[j]);
            }
        }

        return best;
    }

    private static float ClosestEnemyDistance(Vector3 localCenter, IReadOnlyList<BattlefieldEnemyClusterSnapshot> clusters)
    {
        if (clusters.Count == 0)
            return float.MaxValue;
        return clusters.Min(cluster => Distance2D(localCenter, cluster.Center));
    }

    private static float ResolveScorePressure(BattlefieldScoreSituationSnapshot score, BattlefieldTimeSituationSnapshot time)
    {
        if (!score.FriendlyAlliance.HasValue || score.RankedAlliances.Length == 0)
            return 0f;

        var friendly = score.FriendlyAlliance.Value;
        var leader = score.RankedAlliances[0];
        var gapToLeader = Math.Max(0, leader.Score - friendly.Score);
        var pressure = friendly.RankIndex > 1 ? 18f + gapToLeader * 0.18f : 0f;
        if (score.VictoryScore > 0)
        {
            var leaderRemaining = score.VictoryScore - leader.Score;
            if (leaderRemaining <= 160)
                pressure += 30f;
            if (leaderRemaining <= 80)
                pressure += 18f;
        }

        if (time.HasMatchTime && time.MatchTimeRemainingSeconds <= 180)
            pressure += 34f;
        if (leader.ScorePerSecond30s >= 2.0f && !leader.IsLocalAlliance)
            pressure += 12f;

        return Math.Clamp(pressure, 0f, 100f);
    }

    private static bool IsScoreBalanced(BattlefieldScoreSituationSnapshot score)
    {
        if (score.RankedAlliances.Length < 3)
            return false;

        var max = score.RankedAlliances.Max(alliance => alliance.Score);
        var min = score.RankedAlliances.Min(alliance => alliance.Score);
        return max - min <= 120;
    }

    private static bool IsEndgameConflict(BattlefieldScoreSituationSnapshot score, BattlefieldTimeSituationSnapshot time, out string reason)
    {
        reason = string.Empty;
        if (!score.FriendlyAlliance.HasValue || score.RankedAlliances.Length == 0)
            return false;

        var friendly = score.FriendlyAlliance.Value;
        var leader = score.RankedAlliances[0];
        var leaderRemaining = score.VictoryScore > 0 ? score.VictoryScore - leader.Score : 9999;
        var isEnemyLeader = !leader.IsLocalAlliance;
        if (time.HasMatchTime && time.MatchTimeRemainingSeconds <= 150 && score.RankedAlliances.Length >= 2)
        {
            reason = $"终盘剩余 {time.MatchTimeRemainingSeconds}秒，第一 {leader.Name} {leader.Score} 分，我方 {friendly.Score} 分";
            return true;
        }

        if (isEnemyLeader && leaderRemaining <= 140)
        {
            reason = $"敌方 {leader.Name} 距离胜利还差 {leaderRemaining} 分";
            return true;
        }

        return false;
    }

    private static bool IsHighValueObjective(BattlefieldObjectivePrioritySnapshot objective)
        => objective.ScoreValue is >= 100
            || objective.Category is BattlefieldMapObjectiveCategory.Ice or BattlefieldMapObjectiveCategory.Ovoo or BattlefieldMapObjectiveCategory.StrategicPoint
            && objective.RewardScore >= 72f;

    private static bool HasScoreTargetAmbiguity(BattlefieldScoreSituationSnapshot score, BattlefieldDecisionSnapshot decision, out string reason)
    {
        reason = string.Empty;
        if (!score.FriendlyAlliance.HasValue || score.RankedAlliances.Length < 3)
            return false;

        var enemyLeaders = score.RankedAlliances
            .Where(alliance => !alliance.IsLocalAlliance)
            .Take(2)
            .ToArray();
        if (enemyLeaders.Length < 2)
            return false;

        var gap = Math.Abs(enemyLeaders[0].Score - enemyLeaders[1].Score);
        var hasFightAndObjective = decision.FightPriorityTarget.HasValue && decision.ObjectivePriorityTarget.HasValue;
        if (gap <= 180 && hasFightAndObjective && decision.RiskAssessment.OverallRisk >= 34f)
        {
            reason = $"两家敌方比分接近（差 {gap}），本地同时存在打团线和拿点线，需要判断优先打谁";
            return true;
        }

        return false;
    }

    private static bool ShouldSampleStrategicSituation(
        BattlefieldSnapshot snapshot,
        BattlefieldObjectivePrioritySnapshot? primary,
        float scorePressure,
        float closestEnemyDistance,
        int enemyPairEta,
        BattlefieldRiskAssessmentSnapshot risk,
        out string reason,
        out float urgency)
    {
        var enemyClose = closestEnemyDistance <= 190f;
        var objectiveLive = primary.HasValue
            && primary.Value.MountedEtaSeconds <= 65
            && primary.Value.PriorityScore >= 32f;
        var scoreMatters = scorePressure >= 5f;
        var tacticalNoise = snapshot.TeamSituation.IsEnemySplit
            || snapshot.TeamSituation.AdvancedTactics.IsThirdPartyPincerLikely
            || snapshot.TeamSituation.AdvancedTactics.IsHighGroundDropPrepLikely
            || snapshot.TeamSituation.AdvancedTactics.IsCoordinatedSquadLikely
            || risk.ThirdPartyPincerRisk >= 28f
            || risk.ObjectiveRisk >= 32f
            || risk.OverallRisk >= 36f;
        var fightReachableSoon = enemyPairEta <= 34;
        var signalCount = 0;
        signalCount += enemyClose ? 1 : 0;
        signalCount += objectiveLive ? 1 : 0;
        signalCount += scoreMatters ? 1 : 0;
        signalCount += tacticalNoise ? 1 : 0;
        signalCount += fightReachableSoon ? 1 : 0;

        if (signalCount < 2)
        {
            reason = string.Empty;
            urgency = 0f;
            return false;
        }

        var objectiveText = primary.HasValue
            ? $"{primary.Value.Name} {primary.Value.MountedEtaSeconds}秒"
            : "无明确目标点";
        var enemyText = closestEnemyDistance < float.MaxValue
            ? $"{closestEnemyDistance:0}y"
            : "未知";
        reason = $"当前局势具备中等复杂度（近敌 {enemyText} / 目标 {objectiveText} / 比分压力 {scorePressure:0}），进入 AI 联调采样";
        urgency = Math.Clamp(
            54f
            + scorePressure * 0.55f
            + (enemyClose ? 8f : 0f)
            + (objectiveLive ? 8f : 0f)
            + (tacticalNoise ? 10f : 0f)
            + (fightReachableSoon ? 6f : 0f),
            0f,
            92f);
        return true;
    }

    private static string BuildSituationKey(
        BattlefieldLlmDecisionNeedKind kind,
        BattlefieldSnapshot snapshot,
        IReadOnlyList<AllianceCenter> enemyCenters)
    {
        var scoreKey = snapshot.ScoreSituation.RankedAlliances.Length > 0
            ? string.Join("-", snapshot.ScoreSituation.RankedAlliances.Select(alliance => $"{alliance.AllianceId}:{alliance.Score / 50}"))
            : "noscore";
        var objective = ResolveLocalDecision(snapshot).PrimaryObjective;
        var objectiveKey = objective.HasValue
            ? $"{objective.Value.ObjectiveId}:{objective.Value.State}:{objective.Value.MountedEtaSeconds / 10}"
            : "noobj";
        var enemyKey = enemyCenters.Count > 0
            ? string.Join("-", enemyCenters
                .Take(3)
                .Select(center => $"{center.Battalion}:{Quantize(center.Center.X)}:{Quantize(center.Center.Z)}"))
            : "noenemy";
        var timeBucket = snapshot.MatchTimeRemaining / 30;
        return $"{kind}:{timeBucket}:{scoreKey}:{objectiveKey}:{enemyKey}";
    }

    private static string BuildManualSituationKey(BattlefieldSnapshot snapshot, string operatorNote, long now)
    {
        var objective = ResolveLocalDecision(snapshot).PrimaryObjective;
        var objectiveKey = objective.HasValue ? objective.Value.ObjectiveId : "noobj";
        var noteKey = string.IsNullOrWhiteSpace(operatorNote) ? "empty" : $"{operatorNote.Length}:{Math.Abs(operatorNote.GetHashCode())}";
        return $"manual:{snapshot.MatchTimeRemaining / 5}:{objectiveKey}:{noteKey}:{now / 1000}";
    }

    private static int Quantize(float value)
        => (int)MathF.Round(value / 50f);

    private static int EstimateEtaSeconds(float distance)
        => distance <= 0f ? 0 : (int)MathF.Ceiling(distance / MountedYalmsPerSecond);

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static BattlefieldDecisionSnapshot ResolveLocalDecision(BattlefieldSnapshot snapshot)
        => snapshot.LocalDecision.IsAvailable || snapshot.Decision.IsAvailable
            ? snapshot.LocalDecision.IsAvailable ? snapshot.LocalDecision : snapshot.Decision
            : new BattlefieldDecisionSnapshot();

    private string ResolveApiKey(LlmDecisionConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKeyEnvironmentVariable))
        {
            var apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(apiKey))
                return apiKey.Trim();
        }

        return string.IsNullOrWhiteSpace(config.ApiKey)
            ? string.Empty
            : config.ApiKey.Trim();
    }

    private static string NeedKindText(BattlefieldLlmDecisionNeedKind kind)
        => kind switch
        {
            BattlefieldLlmDecisionNeedKind.ManualProbe => "手动测试/对话",
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse => "常规战略采样",
            BattlefieldLlmDecisionNeedKind.StrategicSampling => "AI 联调采样",
            BattlefieldLlmDecisionNeedKind.NearbyThirdPartyFight => "近处三方参战判断",
            BattlefieldLlmDecisionNeedKind.FarObjectiveWithCloseEnemies => "远目标与近敌冲突",
            BattlefieldLlmDecisionNeedKind.ScoreEndgameConflict => "终盘比分冲突",
            BattlefieldLlmDecisionNeedKind.ObjectiveRace => "目标窗口抢点",
            BattlefieldLlmDecisionNeedKind.UnstableThreeFaction => "三方站位不稳定",
            BattlefieldLlmDecisionNeedKind.ScoreTargetAmbiguity => "比分目标不明确",
            _ => "无需 AI 大决策"
        };

    private static string RelationText(BattlefieldPlayerRelation relation, bool isLocalAlliance)
    {
        if (isLocalAlliance)
            return "我方";
        return relation switch
        {
            BattlefieldPlayerRelation.LocalPlayer => "我方",
            BattlefieldPlayerRelation.Friendly => "我方",
            BattlefieldPlayerRelation.Enemy => "敌方",
            _ => "未知"
        };
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start)
            throw new InvalidOperationException("模型未返回 JSON 对象");
        return text[start..(end + 1)];
    }

    private static string GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return string.Empty;
    }

    private static float GetFloat(JsonElement root, float fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String && float.TryParse(value.GetString(), out number))
                return number;
        }

        return fallback;
    }

    private static string GetDebugText(JsonElement root)
    {
        if (!TryGetProperty(root, "debug", out var value) && !TryGetProperty(root, "调试", out value))
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        return value.GetRawText();
    }

    private static string GetDebugField(JsonElement root, params string[] names)
    {
        if (!TryGetProperty(root, "debug", out var value) && !TryGetProperty(root, "调试", out value))
            return string.Empty;
        if (value.ValueKind != JsonValueKind.Object)
            return string.Empty;
        return GetString(value, names);
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string Truncate(string text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;
        text = text.Trim();
        return text.Length <= maxChars ? text : text[..Math.Max(0, maxChars - 1)] + "…";
    }

    private static long ResolveMinIntervalMs(LlmDecisionConfiguration config, LlmGateResult gate)
    {
        var baseMs = Math.Max(3000L, config.MinIntervalSeconds * 1000L);
        if (gate.Urgency >= 88f)
            return Math.Max(3000L, (long)(baseMs * 0.5f));
        if (gate.Urgency >= 76f)
            return Math.Max(4000L, (long)(baseMs * 0.65f));
        if (gate.Urgency >= 62f)
            return Math.Max(5000L, (long)(baseMs * 0.8f));
        return baseMs;
    }

    private static long ResolveSameSituationCooldownMs(LlmDecisionConfiguration config, LlmGateResult gate)
    {
        var baseMs = Math.Max(5000L, config.SameSituationCooldownSeconds * 1000L);
        if (gate.Urgency >= 88f)
            return Math.Max(6000L, (long)(baseMs * 0.45f));
        if (gate.Urgency >= 76f)
            return Math.Max(8000L, (long)(baseMs * 0.6f));
        if (gate.Urgency >= 62f)
            return Math.Max(10000L, (long)(baseMs * 0.75f));
        return baseMs;
    }

    private void ClearDebugArtifacts()
    {
        lastRequestGate = LlmGateResult.None("尚未发起 AI 请求");
        lastManualInstruction = string.Empty;
        lastSystemPrompt = string.Empty;
        lastUserPrompt = string.Empty;
        lastRawResponse = string.Empty;
        lastParsedJson = string.Empty;
        lastResponseTicks = -1;
    }

    private readonly record struct LlmGateResult(
        bool ShouldRequest,
        BattlefieldLlmDecisionNeedKind NeedKind,
        string Reason,
        float Urgency,
        string SituationKey)
    {
        public static LlmGateResult None(string reason)
            => new(false, BattlefieldLlmDecisionNeedKind.None, reason, 0f, string.Empty);
    }

    private readonly record struct AllianceCenter(string Name, byte? Battalion, Vector3 Center);

    private readonly record struct LlmConversationTurn(
        long Ticks,
        int MatchRemainingSeconds,
        BattlefieldLlmDecisionNeedKind NeedKind,
        string OperatorNote,
        string Decision,
        string ShortReason,
        float Confidence,
        string SituationKey);

    private sealed record LlmRequestContext(
        string BaseUrl,
        string Model,
        string ApiKey,
        int TimeoutMs,
        string SessionId,
        long RequestedAtTicks,
        LlmGateResult Gate,
        string SystemPrompt,
        string UserPrompt,
        string ManualInstruction);
}
