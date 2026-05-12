using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class BattlefieldReplayRecorder : IDisposable
{
    private const string RecorderVersion = "1";
    private const int MaxSeenChatEvents = 2048;
    private const int MaxQueuedWorkItems = 4;
    private const int WorkerJoinTimeoutMs = 500;
    private const long PruneIntervalMs = 10 * 60 * 1000;
    private static readonly int[] EvaluationWindowsSeconds = { 10, 30 };
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly Configuration configuration;
    private readonly IPluginLog log;
    private readonly object queueLock = new();
    private readonly Queue<ReplayWorkItem> workQueue = new();
    private readonly Thread workerThread;
    private readonly object statsLock = new();
    private readonly List<PendingCommandEvaluation> pendingCommandEvaluations = new();
    private readonly Dictionary<BattlefieldCommandKind, CommandOutcomeAccumulator> commandOutcomeByKind = new();
    private readonly HashSet<string> seenChatEventKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> seenChatEventKeyOrder = new();
    private BattlefieldCommandEffectivenessSnapshot[] cachedCommandEffectivenessSnapshots = Array.Empty<BattlefieldCommandEffectivenessSnapshot>();

    private StreamWriter? writer;
    private string currentSessionId = string.Empty;
    private string currentFilePath = string.Empty;
    private uint currentTerritoryType;
    private uint currentMapId;
    private int? previousRemainingSeconds;
    private ReplayMetrics? previousMetrics;
    private long lastRecordTicks;
    private long lastWriteTicks = -1;
    private long lastPruneTicks = -1;
    private string lastError = string.Empty;
    private int framesWritten;
    private int evaluationEventsWritten;
    private int lastRecordedCommandSequence;
    private int friendlyKillsTotal;
    private int friendlyDeathsTotal;
    private int enemyKillsTotal;
    private int enemyDeathsTotal;
    private int objectiveEventsTotal;
    private int battleHighEventsTotal;
    private bool commandOutcomeStatsLoaded;
    private bool workerStopRequested;
    private volatile bool isRecording;
    private volatile bool disposed;
    private int queuedWorkItems;
    private int droppedReplayFrames;
    private int pendingEvaluationCount;

    public BattlefieldReplayRecorder(Configuration configuration, IPluginLog log)
    {
        this.configuration = configuration;
        this.log = log;
        workerThread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "ai02 replay recorder",
        };
        workerThread.Start();
    }

    public void Record(BattlefieldSnapshot snapshot)
    {
        if (disposed)
            return;

        var replayConfig = configuration.Replay;
        replayConfig.Normalize();

        var now = Environment.TickCount64;
        if (!replayConfig.Enabled || !snapshot.IsInFrontline)
        {
            if (isRecording)
                EnqueueWork(ReplayWorkItem.Close(snapshot, replayConfig.Enabled ? "leave_frontline" : "disabled"));
            return;
        }

        var intervalMs = replayConfig.RecordIntervalSeconds * 1000L;
        if (now - lastRecordTicks < intervalMs)
            return;

        lastRecordTicks = now;
        EnqueueWork(ReplayWorkItem.Frame(snapshot));
    }

    private void ProcessRecord(BattlefieldSnapshot snapshot)
    {
        try
        {
            var replayConfig = configuration.Replay;
            replayConfig.Normalize();

            var now = Environment.TickCount64;
            if (replayConfig.Enabled)
                MaybePruneOldSessions(now);

            if (!replayConfig.Enabled || !snapshot.IsInFrontline)
            {
                if (writer is not null)
                    CloseSession(snapshot, replayConfig.Enabled ? "leave_frontline" : "disabled");
                return;
            }

            EnsureSession(snapshot);
            UpdateCumulativeChatEvents(snapshot.ChatEventSituation.RecentEvents);

            var metrics = CaptureMetrics(snapshot);
            var frame = BuildFrame(snapshot, metrics, previousMetrics);
            WriteEvent("frame", snapshot, frame);
            framesWritten++;
            Volatile.Write(ref lastWriteTicks, now);
            lastError = string.Empty;

            RegisterPublishedCommand(snapshot, metrics);
            WriteDueCommandEvaluations(snapshot, metrics);

            previousMetrics = metrics;
            previousRemainingSeconds = snapshot.MatchTimeRemaining;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            log.Debug(ex, "[Replay] Failed to write battlefield replay frame");
        }
    }

    public BattlefieldReplayRecorderStatus GetStatus()
    {
        var now = Environment.TickCount64;
        var directory = ResolveReplayDirectory();
        var writeTicks = Volatile.Read(ref lastWriteTicks);
        var pruneTicks = Volatile.Read(ref lastPruneTicks);
        var lastWriteAge = writeTicks >= 0 ? Math.Max(0, now - writeTicks) : -1;
        var statusText = !configuration.Replay.Enabled
            ? "实战回放：已关闭"
            : !isRecording
                ? "实战回放：等待进入纷争前线"
                : $"实战回放：记录中 {framesWritten} 帧，评估 {evaluationEventsWritten} 条";

        if (!string.IsNullOrWhiteSpace(lastError))
            statusText += $"；最近错误：{lastError}";

        var dropped = Volatile.Read(ref droppedReplayFrames);
        if (dropped > 0)
            statusText += $"；已丢弃 {dropped} 帧以避免阻塞";

        return new BattlefieldReplayRecorderStatus
        {
            Enabled = configuration.Replay.Enabled,
            IsRecording = isRecording,
            DirectoryPath = directory,
            CurrentSessionId = currentSessionId,
            CurrentFilePath = currentFilePath,
            FramesWritten = framesWritten,
            EvaluationEventsWritten = evaluationEventsWritten,
            PendingEvaluations = Volatile.Read(ref pendingEvaluationCount),
            QueuedWorkItems = Volatile.Read(ref queuedWorkItems),
            DroppedFrames = Volatile.Read(ref droppedReplayFrames),
            LastWriteAgeMs = lastWriteAge,
            LastPruneAgeMs = pruneTicks >= 0 ? Math.Max(0, now - pruneTicks) : -1,
            LastError = lastError,
            StatusText = statusText,
        };
    }

    public BattlefieldCommandEffectivenessSnapshot[] GetCommandEffectivenessSnapshots()
    {
        lock (statsLock)
            return cachedCommandEffectivenessSnapshots.ToArray();
    }

    public void ResetDecisionQualityFeedback()
        => EnqueueWork(ReplayWorkItem.ResetStats());

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        lock (queueLock)
        {
            workQueue.Clear();
            Volatile.Write(ref queuedWorkItems, 0);
            workerStopRequested = true;
            workQueue.Enqueue(ReplayWorkItem.Shutdown());
            Monitor.PulseAll(queueLock);
        }

        if (!workerThread.Join(WorkerJoinTimeoutMs))
            log.Debug("[Replay] Recorder worker did not stop within {TimeoutMs} ms; letting background shutdown finish.", WorkerJoinTimeoutMs);
    }

    private void EnqueueWork(ReplayWorkItem item)
    {
        if (disposed)
            return;

        lock (queueLock)
        {
            if (workerStopRequested)
                return;

            if (workQueue.Count >= MaxQueuedWorkItems)
            {
                if (DropOldestFrameNoLock())
                    Interlocked.Increment(ref droppedReplayFrames);
                else if (item.Kind == ReplayWorkKind.Frame)
                {
                    Interlocked.Increment(ref droppedReplayFrames);
                    return;
                }
            }

            workQueue.Enqueue(item);
            Volatile.Write(ref queuedWorkItems, workQueue.Count);
            Monitor.Pulse(queueLock);
        }
    }

    private bool DropOldestFrameNoLock()
    {
        if (workQueue.Count == 0)
            return false;

        var retained = new Queue<ReplayWorkItem>(workQueue.Count);
        var dropped = false;
        while (workQueue.Count > 0)
        {
            var item = workQueue.Dequeue();
            if (!dropped && item.Kind == ReplayWorkKind.Frame)
            {
                dropped = true;
                continue;
            }

            retained.Enqueue(item);
        }

        while (retained.Count > 0)
            workQueue.Enqueue(retained.Dequeue());

        Volatile.Write(ref queuedWorkItems, workQueue.Count);
        return dropped;
    }

    private void WorkerLoop()
    {
        try
        {
            EnsureCommandOutcomeStatsLoaded();
            while (true)
            {
                ReplayWorkItem item;
                lock (queueLock)
                {
                    while (workQueue.Count == 0 && !workerStopRequested)
                        Monitor.Wait(queueLock);

                    if (workQueue.Count == 0 && workerStopRequested)
                        break;

                    item = workQueue.Dequeue();
                    Volatile.Write(ref queuedWorkItems, workQueue.Count);
                }

                if (item.Kind == ReplayWorkKind.Shutdown)
                    break;
                if (item.Kind == ReplayWorkKind.Close)
                {
                    CloseSession(item.Snapshot, item.Reason);
                    continue;
                }
                if (item.Kind == ReplayWorkKind.ResetStats)
                {
                    ResetDecisionQualityFeedbackCore();
                    continue;
                }

                if (item.Snapshot is not null)
                    ProcessRecord(item.Snapshot);
            }
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            log.Debug(ex, "[Replay] Recorder worker failed");
        }
        finally
        {
            CloseSession(null, "dispose");
            Volatile.Write(ref queuedWorkItems, 0);
        }
    }

    private void EnsureSession(BattlefieldSnapshot snapshot)
    {
        if (writer is null)
        {
            BeginSession(snapshot);
            return;
        }

        if (snapshot.TerritoryType != currentTerritoryType || snapshot.MapId != currentMapId || LooksLikeNewMatch(snapshot))
        {
            CloseSession(snapshot, "new_match");
            BeginSession(snapshot);
        }
    }

    private bool LooksLikeNewMatch(BattlefieldSnapshot snapshot)
    {
        if (previousRemainingSeconds.HasValue
            && snapshot.MatchTimeRemaining > previousRemainingSeconds.Value + 45
            && snapshot.MatchTimeRemaining > 60)
            return true;

        if (!previousMetrics.HasValue)
            return false;

        var currentScoreTotal = snapshot.ScoreSituation.Alliances.Sum(alliance => alliance.Score);
        return previousMetrics.Value.ScoreTotal > 300 && currentScoreTotal + 180 < previousMetrics.Value.ScoreTotal;
    }

    private void BeginSession(BattlefieldSnapshot snapshot)
    {
        configuration.Replay.Normalize();
        var directory = ResolveReplayDirectory();
        Directory.CreateDirectory(directory);
        PruneOldSessionFiles(directory, true);

        var localNow = DateTime.Now;
        var mapPart = SanitizeFilePart(snapshot.ScoreSituation.MapName);
        currentSessionId = $"{localNow:yyyyMMdd_HHmmss}_{snapshot.TerritoryType}_{snapshot.MapId}";
        currentFilePath = Path.Combine(directory, $"{currentSessionId}_{mapPart}.jsonl");
        currentTerritoryType = snapshot.TerritoryType;
        currentMapId = snapshot.MapId;
        previousRemainingSeconds = null;
        previousMetrics = null;
        pendingCommandEvaluations.Clear();
        Volatile.Write(ref pendingEvaluationCount, 0);
        seenChatEventKeys.Clear();
        seenChatEventKeyOrder.Clear();
        framesWritten = 0;
        evaluationEventsWritten = 0;
        lastRecordedCommandSequence = 0;
        friendlyKillsTotal = 0;
        friendlyDeathsTotal = 0;
        enemyKillsTotal = 0;
        enemyDeathsTotal = 0;
        objectiveEventsTotal = 0;
        battleHighEventsTotal = 0;

        var stream = new FileStream(currentFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        isRecording = true;

        WriteEvent("session_start", snapshot, new
        {
            recorderVersion = RecorderVersion,
            pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            territoryType = snapshot.TerritoryType,
            mapId = snapshot.MapId,
            mapName = snapshot.ScoreSituation.MapName,
            intervalSeconds = configuration.Replay.RecordIntervalSeconds,
            includePlayerDetails = configuration.Replay.IncludePlayerDetails,
            note = "frame 为每秒战场状态；evaluation 为指挥短句 10/30 秒后的结果代理。",
        });
    }

    private void CloseSession(BattlefieldSnapshot? snapshot, string reason)
    {
        if (writer is null)
            return;

        try
        {
            WriteEvent("session_end", snapshot, new
            {
                reason,
                framesWritten,
                evaluationEventsWritten,
                pendingEvaluations = pendingCommandEvaluations.Count,
            });
        }
        catch
        {
            // Best-effort footer only; do not block plugin unload or zone changes.
        }

        writer.Dispose();
        writer = null;
        isRecording = false;
        pendingCommandEvaluations.Clear();
        Volatile.Write(ref pendingEvaluationCount, 0);
        previousRemainingSeconds = null;
        previousMetrics = null;
    }

    private void WriteEvent<T>(string type, BattlefieldSnapshot? snapshot, T data)
    {
        if (writer is null)
            return;

        var envelope = new ReplayEnvelope<T>
        {
            Type = type,
            RecorderVersion = RecorderVersion,
            SessionId = currentSessionId,
            UnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            UpdatedAtTicks = snapshot?.UpdatedAtTicks ?? Environment.TickCount64,
            TerritoryType = snapshot?.TerritoryType ?? currentTerritoryType,
            MapId = snapshot?.MapId ?? currentMapId,
            MapName = snapshot?.ScoreSituation.MapName ?? string.Empty,
            Data = data,
        };

        writer.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private object BuildFrame(BattlefieldSnapshot snapshot, ReplayMetrics metrics, ReplayMetrics? previous)
        => new
        {
            schema = "battlefield_replay_frame_v1",
            frontline = new
            {
                snapshot.IsInFrontline,
                snapshot.IsAreaTransitioning,
                snapshot.TerritoryType,
                snapshot.MapId,
                mapName = snapshot.ScoreSituation.MapName,
                localPlayer = snapshot.LocalPlayer.HasValue ? BuildPlayerReference(snapshot.LocalPlayer.Value) : null,
            },
            time = new
            {
                remainingSeconds = snapshot.MatchTimeRemaining,
                snapshot.TimeSituation.HasMatchTime,
                snapshot.TimeSituation.MatchElapsedSeconds,
                snapshot.TimeSituation.MatchPhaseName,
                snapshot.TimeSituation.MapRulePhaseName,
                snapshot.TimeSituation.NextResourceSeconds,
                snapshot.TimeSituation.NextResourceName,
                snapshot.TimeSituation.NextResourceSource,
                snapshot.TimeSituation.SummaryText,
            },
            score = BuildScoreFrame(snapshot.ScoreSituation),
            team = BuildTeamFrame(snapshot.TeamSituation),
            objectives = BuildObjectiveFrame(snapshot),
            announcements = BuildAnnouncementFrame(snapshot.AnnouncementSituation),
            chatEvents = BuildChatEventFrame(snapshot.ChatEventSituation),
            decision = BuildDecisionFrame(snapshot.Decision),
            command = BuildCommandSituationFrame(snapshot.Decision.CommandSituation),
            calibration = BuildCalibrationFrame(),
            outcome = BuildOutcomeFrame(metrics, previous),
            players = configuration.Replay.IncludePlayerDetails
                ? snapshot.Players.Select(BuildPlayerFrame).ToArray()
                : Array.Empty<object>(),
        };

    private object BuildCalibrationFrame()
    {
        var advanced = configuration.AdvancedTactics;
        return new
        {
            advancedTactics = new
            {
                advanced.Enabled,
                advanced.MinAlertConfidencePercent,
                advanced.CohesionMinAlertSeverity,
                advanced.CohesionMinSampleCount,
                advanced.FollowDirectDistance,
                advanced.FollowNearDistance,
                advanced.FollowMountedDistance,
                advanced.DirectionDotThresholdPercent,
                advanced.RetreatMinDistanceDelta,
                advanced.RetreatMinSpeed,
                advanced.RetreatMovingAwayRatioPercent,
                advanced.RetreatMinMovingSamples,
                advanced.RetreatMinSeverity,
                advanced.FakeRetreatMinSeverity,
                advanced.FakeRetreatMinThreatSignals,
                advanced.HighGroundMinPressure,
                advanced.HighGroundMinSeverity,
                advanced.ThirdPartyMaxDistance,
                advanced.ThirdPartyMinAngle,
                advanced.ThirdPartyMinSeverity,
                advanced.SquadSearchRadius,
                advanced.SquadMinScore,
                advanced.SquadMinDirectionSimilarityPercent,
                advanced.SquadMinFormationStability,
                advanced.ChokeMinPressure,
                advanced.ChokeMinSeverity,
            },
        };
    }

    private object BuildScoreFrame(BattlefieldScoreSituationSnapshot score)
    {
        var friendly = score.FriendlyAlliance;
        var leader = score.RankedAlliances.OrderByDescending(alliance => alliance.Score).FirstOrDefault();
        var hasLeader = score.RankedAlliances.Length > 0;

        return new
        {
            score.HasScoreData,
            mapType = score.MapType.ToString(),
            score.MapName,
            score.VictoryScore,
            friendly = friendly.HasValue ? BuildAllianceScoreFrame(friendly.Value, leader, hasLeader) : null,
            alliances = score.Alliances.Select(alliance => BuildAllianceScoreFrame(alliance, leader, hasLeader)).ToArray(),
            ranking = score.RankedAlliances.Select(alliance => new
            {
                alliance.Name,
                alliance.Score,
                alliance.RankIndex,
                alliance.RankText,
                alliance.IsLocalAlliance,
                relation = alliance.Relation.ToString(),
            }).ToArray(),
            score.SummaryText,
        };
    }

    private static object BuildAllianceScoreFrame(BattlefieldAllianceScoreSnapshot alliance, BattlefieldAllianceScoreSnapshot leader, bool hasLeader)
        => new
        {
            alliance.AllianceId,
            alliance.Battalion,
            alliance.Name,
            relation = alliance.Relation.ToString(),
            alliance.IsLocalAlliance,
            alliance.Score,
            alliance.VictoryScore,
            alliance.RankIndex,
            alliance.RankText,
            alliance.IsLeading,
            alliance.ScoreDelta30s,
            scorePerSecond30s = Round(alliance.ScorePerSecond30s),
            gapToLeader = hasLeader ? Math.Max(0, leader.Score - alliance.Score) : 0,
            gapToWin = alliance.VictoryScore > 0 ? Math.Max(0, alliance.VictoryScore - alliance.Score) : 0,
        };

    private object BuildTeamFrame(BattlefieldTeamSituationSnapshot team)
        => new
        {
            summary = team.SummaryText,
            friendly = BuildTeamSummaryFrame(team.Friendly),
            enemy = BuildTeamSummaryFrame(team.Enemy),
            unknown = BuildTeamSummaryFrame(team.Unknown),
            alliances = team.Alliances.Select(BuildAllianceSituationFrame).ToArray(),
            enemySplit = new
            {
                team.IsEnemySplit,
                team.EnemySplitSummaryText,
                clusters = team.EnemyClusters
                    .OrderByDescending(cluster => cluster.IsMainCluster)
                    .ThenByDescending(cluster => cluster.Count)
                    .Take(8)
                    .Select(cluster => new
                    {
                        cluster.ClusterId,
                        cluster.Battalion,
                        cluster.AllianceName,
                        cluster.SourceText,
                        center = Position(cluster.Center),
                        cluster.Count,
                        radius = Round(cluster.Radius),
                        distanceToLocal = Round(cluster.DistanceToLocal),
                        separationFromMain = Round(cluster.SeparationFromMain),
                        cluster.IsMainCluster,
                    })
                    .ToArray(),
            },
            respawn = new
            {
                team.RespawnRhythm.FriendlyDeadNow,
                team.RespawnRhythm.EnemyDeadNow,
                team.RespawnRhythm.FriendlyRecentlyDied,
                team.RespawnRhythm.EnemyRecentlyDied,
                team.RespawnRhythm.FriendlyRecentlyRevived,
                team.RespawnRhythm.EnemyRecentlyRevived,
                team.RespawnRhythm.FriendlyLikelyReturningSoon,
                team.RespawnRhythm.EnemyLikelyReturningSoon,
                team.RespawnRhythm.SummaryText,
            },
            enemyMainGroup = new
            {
                team.EnemyMainGroupMovement.HasMainGroup,
                team.EnemyMainGroupMovement.SourceText,
                team.EnemyMainGroupMovement.Battalion,
                team.EnemyMainGroupMovement.AllianceName,
                currentCenter = Position(team.EnemyMainGroupMovement.CurrentCenter),
                previousCenter = Position(team.EnemyMainGroupMovement.PreviousCenter),
                predictedNextCenter = Position(team.EnemyMainGroupMovement.PredictedNextCenter),
                speedPerSecond = Round(team.EnemyMainGroupMovement.SpeedPerSecond),
                team.EnemyMainGroupMovement.PlayerCount,
                team.EnemyMainGroupMovement.DirectionText,
                team.EnemyMainGroupMovement.IsEnemySplit,
                team.EnemyMainGroupMovement.EnemyClusterCount,
                team.EnemyMainGroupMovement.SummaryText,
            },
            limitBreak = new
            {
                team.LimitBreakThreats.FriendlyLikelyReadyCount,
                team.LimitBreakThreats.EnemyLikelyReadyCount,
                team.LimitBreakThreats.FriendlyHighThreatCount,
                team.LimitBreakThreats.EnemyHighThreatCount,
                team.LimitBreakThreats.SourceText,
                team.LimitBreakThreats.SummaryText,
                topEnemy = team.LimitBreakThreats.TopEnemyThreats.Take(5).Select(BuildLimitBreakThreatFrame).ToArray(),
                topFriendly = team.LimitBreakThreats.TopFriendlyThreats.Take(5).Select(BuildLimitBreakThreatFrame).ToArray(),
            },
            keySkills = new
            {
                team.KeySkillThreats.FriendlyLikelyReadyCount,
                team.KeySkillThreats.EnemyLikelyReadyCount,
                team.KeySkillThreats.FriendlyHighThreatCount,
                team.KeySkillThreats.EnemyHighThreatCount,
                team.KeySkillThreats.EnemyControlChainCount,
                team.KeySkillThreats.EnemyDefenseBreakWindowCount,
                team.KeySkillThreats.EnemyExecuteWindowCount,
                team.KeySkillThreats.SourceText,
                team.KeySkillThreats.SummaryText,
                topEnemy = team.KeySkillThreats.TopEnemyThreats.Take(6).Select(BuildKeySkillThreatFrame).ToArray(),
                topFriendly = team.KeySkillThreats.TopFriendlyThreats.Take(6).Select(BuildKeySkillThreatFrame).ToArray(),
                recentUses = team.KeySkillThreats.RecentUses.Take(12).Select(BuildKeySkillUseFrame).ToArray(),
            },
            advancedTactics = new
            {
                team.AdvancedTactics.IsAvailable,
                team.AdvancedTactics.FriendlyFollowRate,
                team.AdvancedTactics.FriendlyDirectionConsistency,
                team.AdvancedTactics.FriendlyCohesionScore,
                team.AdvancedTactics.FriendlyFollowerCount,
                team.AdvancedTactics.FriendlySampleCount,
                team.AdvancedTactics.RawInsightCount,
                team.AdvancedTactics.SuppressedInsightCount,
                team.AdvancedTactics.CalibrationText,
                team.AdvancedTactics.SummaryText,
                insights = team.AdvancedTactics.Insights.Take(8).Select(BuildAdvancedInsightFrame).ToArray(),
                suppressed = team.AdvancedTactics.SuppressedInsights.Take(8).Select(BuildAdvancedInsightFrame).ToArray(),
            },
        };

    private static object BuildTeamSummaryFrame(BattlefieldTeamSummarySnapshot summary)
        => new
        {
            side = summary.Side.ToString(),
            summary.Name,
            summary.TotalCount,
            summary.AliveCount,
            summary.DeadCount,
            summary.MountedCount,
            summary.InCombatCount,
            summary.LowHpCount,
            summary.CastingCount,
            summary.BattleHighCount,
            summary.BattleFeverCount,
            summary.MaxBattleHighLevel,
            summary.BattleHighTotalLevel,
            summary.GuardingCount,
            summary.CrowdControlledCount,
            summary.ControlVulnerableCount,
            summary.InvulnerableCount,
            summary.ExecutableCount,
            summary.SnowBlessingCount,
            summary.NearCount,
            summary.MidCount,
            summary.FarCount,
            mainCluster = summary.MainCluster.HasValue ? BuildPlayerClusterFrame(summary.MainCluster.Value) : null,
        };

    private static object BuildAllianceSituationFrame(BattlefieldAllianceSituationSnapshot alliance)
        => new
        {
            alliance.Battalion,
            alliance.Name,
            relation = alliance.Relation.ToString(),
            alliance.IsLocalAlliance,
            alliance.VisiblePlayerCount,
            alliance.MapVisionPointCount,
            alliance.AliveCount,
            alliance.DeadCount,
            alliance.LowHpCount,
            alliance.CastingCount,
            alliance.BattleHighCount,
            alliance.BattleFeverCount,
            alliance.MaxBattleHighLevel,
            alliance.GuardingCount,
            alliance.CrowdControlledCount,
            alliance.ExecutableCount,
            alliance.SnowBlessingCount,
            mainPlayerCluster = alliance.MainPlayerCluster.HasValue ? BuildPlayerClusterFrame(alliance.MainPlayerCluster.Value) : null,
            mainMapVisionCluster = alliance.MainMapVisionCluster.HasValue ? BuildMapVisionClusterFrame(alliance.MainMapVisionCluster.Value) : null,
        };

    private object BuildObjectiveFrame(BattlefieldSnapshot snapshot)
        => new
        {
            mapObjectives = snapshot.MapObjectives
                .OrderByDescending(objective => ObjectiveStateSort(objective.State))
                .ThenByDescending(objective => objective.ScoreValue ?? 0)
                .ThenBy(objective => objective.RemainingSeconds ?? int.MaxValue)
                .Take(16)
                .Select(BuildMapObjectiveFrame)
                .ToArray(),
            rawObjectives = snapshot.Objectives.Take(16).Select(objective => new
            {
                objective.Id,
                kind = objective.Kind.ToString(),
                objective.Name,
                position = Position(objective.Position),
                objective.IconId,
                objective.CountdownSeconds,
                objective.HpPercent,
                objective.ScoreValue,
            }).ToArray(),
            mapTactics = new
            {
                snapshot.MapTactics.IsAvailable,
                snapshot.MapTactics.AnnotationCount,
                snapshot.MapTactics.ZoneCount,
                snapshot.MapTactics.StaticDangerCount,
                snapshot.MapTactics.DynamicDangerCount,
                snapshot.MapTactics.MandatoryChokeCount,
                snapshot.MapTactics.CurrentRecommendation,
                snapshot.MapTactics.SummaryText,
                zones = snapshot.MapTactics.TopZones.Take(8).Select(BuildMapZoneFrame).ToArray(),
                routes = snapshot.MapTactics.Routes.Take(8).Select(BuildMapRouteFrame).ToArray(),
            },
        };

    private static object BuildMapObjectiveFrame(BattlefieldMapObjectiveSnapshot objective)
        => new
        {
            objective.Id,
            mapType = objective.MapType.ToString(),
            category = objective.Category.ToString(),
            state = objective.State.ToString(),
            position = Position(objective.Position),
            objective.IconId,
            objective.GameObjectId,
            objective.Name,
            objective.LocationId,
            objective.RankName,
            ownership = objective.Ownership?.ToString(),
            objective.OwnershipText,
            objective.RemainingSeconds,
            objective.RemainingSource,
            objective.HpPercent,
            objective.CurrentHp,
            objective.MaxHp,
            objective.ScoreValue,
            objective.AttackerCount,
            objective.FriendlyAttackerCount,
            objective.EnemyAttackerCount,
            objective.CasterCount,
            objective.IsBeingFocused,
            objective.IsBeingAttacked,
            objective.RecentHpLoss,
            recentHpLossPerSecond = Round(objective.RecentHpLossPerSecond),
            objective.LastDamageAgeMs,
            objective.ContributionSummaryText,
            objective.EnmitySourceText,
            objective.AggressorNames,
            objective.SourceText,
            confidence = Round(objective.Confidence),
        };

    private static object BuildAnnouncementFrame(BattlefieldAnnouncementSituationSnapshot announcement)
        => new
        {
            announcement.IsAvailable,
            weather = announcement.CurrentWeather.ToString(),
            announcement.CurrentWeatherName,
            announcement.WeatherStateText,
            announcement.WeatherRemainingSeconds,
            announcement.SourceText,
            announcement.SummaryText,
            latest = announcement.LatestAnnouncement.HasValue ? BuildAnnouncementEventFrame(announcement.LatestAnnouncement.Value) : null,
            recent = announcement.RecentAnnouncements.Take(8).Select(BuildAnnouncementEventFrame).ToArray(),
        };

    private static object BuildAnnouncementEventFrame(BattlefieldAnnouncementSnapshot announcement)
        => new
        {
            announcement.ObservedAtTicks,
            announcement.AgeMs,
            announcement.Source,
            announcement.Text,
            kind = announcement.Kind.ToString(),
            weather = announcement.Weather.ToString(),
            announcement.WeatherName,
            announcement.LocationId,
            announcement.RankName,
            ownership = announcement.Ownership?.ToString(),
            announcement.CountdownSeconds,
            announcement.RemainingSeconds,
            announcement.SummaryText,
        };

    private object BuildChatEventFrame(BattlefieldChatEventSituationSnapshot chat)
        => new
        {
            chat.IsAvailable,
            chat.FriendlyKillsRecent,
            chat.FriendlyDeathsRecent,
            chat.EnemyKillsRecent,
            chat.EnemyDeathsRecent,
            chat.BattleHighEventsRecent,
            chat.ObjectiveEventsRecent,
            cumulative = new
            {
                friendlyKillsTotal,
                friendlyDeathsTotal,
                enemyKillsTotal,
                enemyDeathsTotal,
                objectiveEventsTotal,
                battleHighEventsTotal,
            },
            chat.SourceText,
            chat.SummaryText,
            latestKill = chat.LatestKillEvent.HasValue ? BuildChatEventItemFrame(chat.LatestKillEvent.Value) : null,
            latestBattleHigh = chat.LatestBattleHighEvent.HasValue ? BuildChatEventItemFrame(chat.LatestBattleHighEvent.Value) : null,
            latestObjective = chat.LatestObjectiveEvent.HasValue ? BuildChatEventItemFrame(chat.LatestObjectiveEvent.Value) : null,
            recent = chat.RecentEvents.Take(16).Select(BuildChatEventItemFrame).ToArray(),
        };

    private static object BuildChatEventItemFrame(BattlefieldChatEventSnapshot item)
        => new
        {
            item.ObservedAtTicks,
            item.AgeMs,
            item.Source,
            item.Text,
            kind = item.Kind.ToString(),
            item.ActorName,
            item.TargetName,
            actorSide = item.ActorSide.ToString(),
            targetSide = item.TargetSide.ToString(),
            ownership = item.Ownership?.ToString(),
            item.LocationId,
            item.ObjectiveName,
            item.BattleHighLevel,
            item.BattleHighDelta,
            item.SummaryText,
        };

    private object BuildDecisionFrame(BattlefieldDecisionSnapshot decision)
        => new
        {
            decision.IsAvailable,
            decision.RecommendedAction,
            decision.SummaryText,
            primaryObjective = decision.PrimaryObjective.HasValue ? BuildObjectivePriorityFrame(decision.PrimaryObjective.Value) : null,
            objectivePriorityTarget = decision.ObjectivePriorityTarget.HasValue ? BuildPriorityTargetFrame(decision.ObjectivePriorityTarget.Value) : null,
            fightPriorityTarget = decision.FightPriorityTarget.HasValue ? BuildPriorityTargetFrame(decision.FightPriorityTarget.Value) : null,
            objectivePriorities = decision.ObjectivePriorities.Take(8).Select(BuildObjectivePriorityFrame).ToArray(),
            primaryAction = decision.PrimaryAction.HasValue ? BuildActionCandidateFrame(decision.PrimaryAction.Value) : null,
            publishedAction = decision.PublishedAction.HasValue ? BuildActionCandidateFrame(decision.PublishedAction.Value) : null,
            actionCandidates = decision.ActionCandidates.Take(12).Select(BuildActionCandidateFrame).ToArray(),
            risk = BuildRiskFrame(decision.RiskAssessment),
            quality = BuildDecisionQualityFrame(decision.DecisionQuality),
        };

    private static object BuildPriorityTargetFrame(BattlefieldPriorityTargetSnapshot target)
        => new
        {
            target.Lane,
            target.TargetName,
            target.ActionText,
            priority = Round(target.Priority),
            urgency = Round(target.Urgency),
            position = Position(target.Position),
            target.ReasonText,
            target.EvidenceText,
        };

    private static object BuildDecisionQualityFrame(BattlefieldDecisionQualitySnapshot quality)
        => new
        {
            quality.IsAvailable,
            quality.SummaryText,
            quality.CalibrationText,
            mapTemplate = new
            {
                mapType = quality.MapTemplate.MapType.ToString(),
                quality.MapTemplate.Name,
                rewardWeight = Round(quality.MapTemplate.RewardWeight),
                timingWeight = Round(quality.MapTemplate.TimingWeight),
                distanceWeight = Round(quality.MapTemplate.DistanceWeight),
                pressureWeight = Round(quality.MapTemplate.PressureWeight),
                teamAdvantageWeight = Round(quality.MapTemplate.TeamAdvantageWeight),
                terrainWeight = Round(quality.MapTemplate.TerrainWeight),
                riskPenaltyWeight = Round(quality.MapTemplate.RiskPenaltyWeight),
                quality.MapTemplate.SummaryText,
            },
            inputReliability = BuildInputReliabilityFrame(quality.InputReliability),
            commandEffectiveness = quality.CommandEffectiveness.Select(item => new
            {
                kind = item.Kind.ToString(),
                item.SampleCount,
                averageScore = Round(item.AverageScore),
                positiveRate = Round(item.PositiveRate),
                modifier = Round(item.Modifier),
                item.SummaryText,
            }).ToArray(),
            enemyIntent = quality.EnemyIntentPredictions.Select(item => new
            {
                kind = item.Kind.ToString(),
                item.Battalion,
                item.AllianceName,
                confidence = Round(item.Confidence),
                urgency = Round(item.Urgency),
                position = Position(item.Position),
                item.Recommendation,
                item.EvidenceText,
            }).ToArray(),
            teamRoles = quality.TeamRoleInsights.Select(item => new
            {
                kind = item.Kind.ToString(),
                item.Label,
                severity = Round(item.Severity),
                item.PlayerCount,
                item.TargetName,
                position = Position(item.Position),
                item.Recommendation,
                item.EvidenceText,
            }).ToArray(),
        };

    private static object BuildInputReliabilityFrame(BattlefieldInputReliabilitySnapshot input)
        => new
        {
            input.IsAvailable,
            overall = Round(input.OverallReliability),
            score = Round(input.ScoreReliability),
            time = Round(input.TimeReliability),
            players = Round(input.PlayerReliability),
            objectives = Round(input.ObjectiveReliability),
            mapTactics = Round(input.MapTacticsReliability),
            combatEvents = Round(input.CombatEventReliability),
            announcements = Round(input.AnnouncementReliability),
            input.CanPublish,
            input.GateText,
            input.SummaryText,
            components = input.Components.Select(component => new
            {
                component.Id,
                component.Label,
                reliability = Round(component.Reliability),
                weight = Round(component.Weight),
                component.IsCritical,
                component.EvidenceText,
            }).ToArray(),
        };

    private static object BuildRiskFrame(BattlefieldRiskAssessmentSnapshot risk)
        => new
        {
            overall = Round(risk.OverallRisk),
            combat = Round(risk.CombatRisk),
            map = Round(risk.MapRisk),
            objective = Round(risk.ObjectiveRisk),
            limitBreak = Round(risk.LimitBreakRisk),
            skillThreat = Round(risk.SkillThreatRisk),
            battleHigh = Round(risk.BattleHighRisk),
            respawn = Round(risk.RespawnRisk),
            numberDisadvantage = Round(risk.NumberDisadvantageRisk),
            flank = Round(risk.FlankRisk),
            enemyMainGroupDirection = Round(risk.EnemyMainGroupDirectionRisk),
            terrain = Round(risk.TerrainRisk),
            retreatRoute = Round(risk.RetreatRouteRisk),
            encirclement = Round(risk.EncirclementRisk),
            ambush = Round(risk.AmbushRisk),
            cohesion = Round(risk.CohesionRisk),
            highGroundDrop = Round(risk.HighGroundDropRisk),
            thirdPartyPincer = Round(risk.ThirdPartyPincerRisk),
            coordinatedSquad = Round(risk.CoordinatedSquadRisk),
            chokeBlock = Round(risk.ChokeBlockRisk),
            scorePressure = Round(risk.ScorePressure),
            risk.RiskLevel,
            risk.SummaryText,
        };

    private static object BuildObjectivePriorityFrame(BattlefieldObjectivePrioritySnapshot objective)
        => new
        {
            objective.ObjectiveId,
            objective.Name,
            category = objective.Category.ToString(),
            state = objective.State.ToString(),
            position = Position(objective.Position),
            objective.ScoreValue,
            objective.RemainingSeconds,
            distanceToLocal = Round(objective.DistanceToLocal),
            objective.MountedEtaSeconds,
            reward = Round(objective.RewardScore),
            timing = Round(objective.TimingScore),
            distance = Round(objective.DistanceScore),
            pressure = Round(objective.PressureScore),
            teamAdvantage = Round(objective.TeamAdvantageScore),
            terrain = Round(objective.TerrainScore),
            risk = Round(objective.RiskScore),
            priority = Round(objective.PriorityScore),
            objective.RecommendedAction,
            objective.EvidenceText,
        };

    private static object BuildCommandSituationFrame(BattlefieldCommandSituationSnapshot situation)
        => new
        {
            situation.IsAvailable,
            situation.SummaryText,
            publish = new
            {
                situation.Publish.ShouldAnnounce,
                situation.Publish.IsSuppressed,
                situation.Publish.InterruptedCooldown,
                situation.Publish.SpeakText,
                situation.Publish.PriorityText,
                situation.Publish.StatusText,
                situation.Publish.SuppressionReason,
                situation.Publish.GlobalCooldownRemainingSeconds,
                situation.Publish.CommandCooldownRemainingSeconds,
                situation.Publish.KindCooldownRemainingSeconds,
                situation.Publish.LastIssuedAgeMs,
                situation.Publish.Sequence,
                command = situation.Publish.Command.HasValue ? BuildCommandFrame(situation.Publish.Command.Value) : null,
            },
            primary = situation.PrimaryCommand.HasValue ? BuildCommandFrame(situation.PrimaryCommand.Value) : null,
            emergency = situation.EmergencyCommand.HasValue ? BuildCommandFrame(situation.EmergencyCommand.Value) : null,
            primaryAction = situation.PrimaryAction.HasValue ? BuildActionCandidateFrame(situation.PrimaryAction.Value) : null,
            publishedAction = situation.PublishedAction.HasValue ? BuildActionCandidateFrame(situation.PublishedAction.Value) : null,
            actionHold = new
            {
                situation.IsActionHoldActive,
                situation.ActionHoldRemainingSeconds,
                situation.ActionHoldReason,
            },
            actionCandidates = situation.ActionCandidates.Take(12).Select(BuildActionCandidateFrame).ToArray(),
            commands = situation.Commands.Take(10).Select(BuildCommandFrame).ToArray(),
        };

    private static object BuildActionCandidateFrame(BattlefieldActionCandidateSnapshot action)
        => new
        {
            action.Id,
            action.CommandId,
            type = action.ActionType.ToString(),
            commandKind = action.CommandKind.ToString(),
            action.Scope,
            action.Text,
            priority = Round(action.Priority),
            confidence = Round(action.Confidence),
            risk = Round(action.Risk),
            urgency = Round(action.Urgency),
            destination = Position(action.Destination),
            action.DestinationName,
            action.TargetId,
            action.TargetName,
            action.RouteId,
            action.RouteText,
            action.CountdownSeconds,
            action.EtaSeconds,
            action.HoldSeconds,
            action.PurposeText,
            action.ReasonText,
            action.EvidenceText,
            action.FailureConditionText,
        };

    private static object BuildCommandFrame(BattlefieldCommandSnapshot command)
        => new
        {
            command.Id,
            kind = command.Kind.ToString(),
            command.Scope,
            command.CommandText,
            score = Round(command.Score),
            urgency = Round(command.Urgency),
            command.CooldownSeconds,
            position = Position(command.Position),
            command.TargetName,
            command.ReasonText,
            command.EvidenceText,
        };

    private static object BuildOutcomeFrame(ReplayMetrics metrics, ReplayMetrics? previous)
        => new
        {
            current = BuildMetricsFrame(metrics),
            sincePrevious = previous.HasValue ? BuildMetricDeltaFrame(previous.Value, metrics) : null,
            note = "这是结果代理，不是最终正确性判定；用于回放和后续权重校准。",
        };

    private static object BuildMetricsFrame(ReplayMetrics metrics)
        => new
        {
            metrics.FriendlyScore,
            metrics.LeadingScore,
            metrics.ScoreTotal,
            metrics.FriendlyRankIndex,
            metrics.FriendlyAlive,
            metrics.FriendlyDead,
            metrics.EnemyAlive,
            metrics.EnemyDead,
            metrics.FriendlyKillsTotal,
            metrics.FriendlyDeathsTotal,
            metrics.EnemyKillsTotal,
            metrics.EnemyDeathsTotal,
            metrics.ObjectiveEventsTotal,
            metrics.BattleHighEventsTotal,
            overallRisk = Round(metrics.OverallRisk),
            metrics.PrimaryObjectiveId,
            metrics.PrimaryObjectiveName,
            metrics.CommandText,
            metrics.CommandSequence,
        };

    private static object BuildMetricDeltaFrame(ReplayMetrics baseline, ReplayMetrics current)
        => new
        {
            friendlyScore = current.FriendlyScore - baseline.FriendlyScore,
            leadingScore = current.LeadingScore - baseline.LeadingScore,
            scoreTotal = current.ScoreTotal - baseline.ScoreTotal,
            friendlyRank = current.FriendlyRankIndex.HasValue && baseline.FriendlyRankIndex.HasValue
                ? current.FriendlyRankIndex.Value - baseline.FriendlyRankIndex.Value
                : (int?)null,
            friendlyAlive = current.FriendlyAlive - baseline.FriendlyAlive,
            friendlyDead = current.FriendlyDead - baseline.FriendlyDead,
            enemyAlive = current.EnemyAlive - baseline.EnemyAlive,
            enemyDead = current.EnemyDead - baseline.EnemyDead,
            friendlyKills = current.FriendlyKillsTotal - baseline.FriendlyKillsTotal,
            friendlyDeaths = current.FriendlyDeathsTotal - baseline.FriendlyDeathsTotal,
            enemyKills = current.EnemyKillsTotal - baseline.EnemyKillsTotal,
            enemyDeaths = current.EnemyDeathsTotal - baseline.EnemyDeathsTotal,
            objectiveEvents = current.ObjectiveEventsTotal - baseline.ObjectiveEventsTotal,
            battleHighEvents = current.BattleHighEventsTotal - baseline.BattleHighEventsTotal,
            overallRisk = Round(current.OverallRisk - baseline.OverallRisk),
            primaryObjectiveChanged = !string.Equals(current.PrimaryObjectiveId, baseline.PrimaryObjectiveId, StringComparison.Ordinal),
            commandChanged = current.CommandSequence != baseline.CommandSequence,
        };

    private static object BuildPlayerFrame(BattlefieldPlayerSnapshot player)
        => new
        {
            player.GameObjectId,
            player.ContentId,
            player.Name,
            relation = player.Relation.ToString(),
            position = Position(player.Position),
            rotationRadians = Round(player.RotationRadians),
            distanceToLocal = Round(player.DistanceToLocal),
            player.ClassJobId,
            player.Battalion,
            player.IsDead,
            player.IsMounted,
            player.IsInCombat,
            player.IsPartyMember,
            player.IsAllianceMember,
            player.IsFriend,
            player.IsCasting,
            player.CurrentCastTime,
            player.TotalCastTime,
            player.TargetObjectId,
            player.CastTargetObjectId,
            player.CurrentHp,
            player.MaxHp,
            hpPercent = Round(player.HpPercent),
            player.BattleHighLevel,
            player.IsBattleFever,
            player.BattleHighStatusId,
            battleHighRemainingSeconds = Round(player.BattleHighRemainingSeconds),
            player.IsGuarding,
            player.IsCrowdControlled,
            player.IsControlImmune,
            player.IsControlVulnerable,
            player.IsInvulnerable,
            player.IsExecutable,
            player.HasSnowBlessing,
            tacticalStatuses = player.TacticalStatuses.Select(status => new
            {
                status.StatusId,
                kind = status.Kind.ToString(),
                status.Label,
                status.Name,
                status.Param,
                remainingSeconds = Round(status.RemainingSeconds),
                status.SourceText,
            }).ToArray(),
        };

    private static object BuildPlayerReference(BattlefieldPlayerSnapshot player)
        => new
        {
            player.GameObjectId,
            player.Name,
            player.ClassJobId,
            player.Battalion,
            position = Position(player.Position),
        };

    private static object BuildPlayerClusterFrame(BattlefieldPlayerClusterSnapshot cluster)
        => new
        {
            relation = cluster.Relation.ToString(),
            cluster.Battalion,
            center = Position(cluster.Center),
            cluster.PlayerCount,
            cluster.DeadCount,
            cluster.CastingCount,
            distanceToLocal = Round(cluster.DistanceToLocal),
        };

    private static object BuildMapVisionClusterFrame(BattlefieldMapVisionClusterSnapshot cluster)
        => new
        {
            relation = cluster.Relation.ToString(),
            cluster.Battalion,
            center = Position(cluster.Center),
            cluster.PointCount,
            distanceToLocal = Round(cluster.DistanceToLocal),
        };

    private static object BuildLimitBreakThreatFrame(BattlefieldLimitBreakThreatSnapshot threat)
        => new
        {
            threat.GameObjectId,
            threat.Name,
            relation = threat.Relation.ToString(),
            threat.Battalion,
            threat.AllianceName,
            threat.ClassJobId,
            threat.JobName,
            threat.Role,
            threat.BattleHighLevel,
            threat.IsBattleFever,
            distanceToLocal = Round(threat.DistanceToLocal),
            estimatedPercent = Round(threat.EstimatedPercent),
            estimatedSecondsToReady = Round(threat.EstimatedSecondsToReady),
            threat.IsLikelyReady,
            threatLevel = threat.ThreatLevel.ToString(),
            threatScore = Round(threat.ThreatScore),
            threat.IsEngagedRecently,
            threat.IsCasting,
            threat.IsTargetingOpposingSide,
            threat.ThreatType,
            threat.EvidenceText,
        };

    private static object BuildKeySkillThreatFrame(BattlefieldKeySkillThreatSnapshot threat)
        => new
        {
            threat.GameObjectId,
            threat.Name,
            relation = threat.Relation.ToString(),
            threat.Battalion,
            threat.AllianceName,
            threat.ClassJobId,
            threat.JobName,
            threat.SkillName,
            kind = threat.Kind.ToString(),
            threat.CooldownSeconds,
            estimatedCooldownRemainingSeconds = Round(threat.EstimatedCooldownRemainingSeconds),
            threat.IsEstimatedReady,
            threat.WasRecentlyUsed,
            threat.LastObservedUseAgeMs,
            threat.IsCasting,
            threat.IsTargetingOpposingSide,
            threat.IsControlChainCandidate,
            threat.IsDefenseBreakWindow,
            threat.IsExecuteWindow,
            threat.TargetIsGuardingOrInvulnerable,
            threat.OpposingVulnerableCount,
            threatLevel = threat.ThreatLevel.ToString(),
            threatScore = Round(threat.ThreatScore),
            threat.SourceText,
            threat.EvidenceText,
        };

    private static object BuildKeySkillUseFrame(BattlefieldKeySkillUseSnapshot use)
        => new
        {
            use.ObservedAtTicks,
            use.AgeMs,
            use.GameObjectId,
            use.Name,
            relation = use.Relation.ToString(),
            use.Battalion,
            use.AllianceName,
            use.ClassJobId,
            use.JobName,
            use.SkillName,
            kind = use.Kind.ToString(),
            use.TargetName,
            use.SourceText,
            use.EvidenceText,
        };

    private static object BuildAdvancedInsightFrame(BattlefieldAdvancedTacticalInsightSnapshot insight)
        => new
        {
            kind = insight.Kind.ToString(),
            insight.Label,
            severity = Round(insight.Severity),
            confidence = Round(insight.Confidence),
            position = Position(insight.Position),
            insight.InvolvedCount,
            insight.Battalion,
            insight.AllianceName,
            insight.Recommendation,
            insight.EvidenceText,
        };

    private static object BuildMapZoneFrame(BattlefieldMapTacticalZoneSnapshot zone)
        => new
        {
            zone.Id,
            kind = zone.Kind.ToString(),
            zone.Label,
            zone.RouteId,
            position = Position(zone.Position),
            radius = Round(zone.Radius),
            estimatedWidth = Round(zone.EstimatedWidth),
            heightDeltaToLocal = Round(zone.HeightDeltaToLocal),
            zone.IsCliffOrHighPlatform,
            zone.IsMandatoryChoke,
            zone.FriendlyNearby,
            zone.EnemyNearby,
            zone.EnemyMapVisionNearby,
            zone.HighBattleHighEnemies,
            zone.HighLimitBreakEnemies,
            engagementWeightModifierPercent = Round(zone.EngagementWeightModifierPercent),
            staticRisk = Round(zone.StaticRisk),
            dynamicRisk = Round(zone.DynamicRisk),
            totalRisk = Round(zone.TotalRisk),
            zone.Recommendation,
            zone.EvidenceText,
        };

    private static object BuildMapRouteFrame(BattlefieldMapTacticalRouteSnapshot route)
        => new
        {
            route.RouteId,
            route.KindSummary,
            route.PointCount,
            distance = Round(route.Distance),
            route.MountedEtaSeconds,
            route.OnFootEtaSeconds,
            staticRisk = Round(route.StaticRisk),
            dynamicRisk = Round(route.DynamicRisk),
            totalRisk = Round(route.TotalRisk),
            route.CrossesDangerZone,
            route.Recommendation,
            route.EvidenceText,
        };

    private ReplayMetrics CaptureMetrics(BattlefieldSnapshot snapshot)
    {
        var friendlyScore = snapshot.ScoreSituation.FriendlyAlliance?.Score ?? 0;
        var leadingScore = snapshot.ScoreSituation.RankedAlliances.Length > 0
            ? snapshot.ScoreSituation.RankedAlliances.Max(alliance => alliance.Score)
            : snapshot.ScoreSituation.Alliances.DefaultIfEmpty().Max(alliance => alliance.Score);
        var scoreTotal = snapshot.ScoreSituation.Alliances.Sum(alliance => alliance.Score);
        var command = snapshot.Decision.CommandSituation.Publish.Command
            ?? snapshot.Decision.CommandSituation.PrimaryCommand;
        var action = snapshot.Decision.CommandSituation.PublishedAction
            ?? snapshot.Decision.CommandSituation.PrimaryAction;

        return new ReplayMetrics(
            friendlyScore,
            leadingScore,
            scoreTotal,
            snapshot.ScoreSituation.FriendlyAlliance?.RankIndex,
            snapshot.TeamSituation.Friendly.AliveCount,
            snapshot.TeamSituation.Friendly.DeadCount,
            snapshot.TeamSituation.Enemy.AliveCount,
            snapshot.TeamSituation.Enemy.DeadCount,
            friendlyKillsTotal,
            friendlyDeathsTotal,
            enemyKillsTotal,
            enemyDeathsTotal,
            objectiveEventsTotal,
            battleHighEventsTotal,
            snapshot.Decision.RiskAssessment.OverallRisk,
            snapshot.Decision.PrimaryObjective?.ObjectiveId ?? string.Empty,
            snapshot.Decision.PrimaryObjective?.Name ?? string.Empty,
            command?.CommandText ?? action?.Text ?? string.Empty,
            snapshot.Decision.CommandSituation.Publish.Sequence);
    }

    private void RegisterPublishedCommand(BattlefieldSnapshot snapshot, ReplayMetrics metrics)
    {
        var publish = snapshot.Decision.CommandSituation.Publish;
        if (!publish.ShouldAnnounce || !publish.Command.HasValue || publish.Sequence <= lastRecordedCommandSequence)
            return;

        lastRecordedCommandSequence = publish.Sequence;
        var command = publish.Command.Value;
        var baseline = new CommandEvaluationBaseline(
            publish.Sequence,
            snapshot.UpdatedAtTicks,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            command.Id,
            command.Kind,
            command.Scope,
            command.CommandText,
            command.Score,
            command.Urgency,
            command.TargetName,
            command.ReasonText,
            command.EvidenceText,
            metrics);

        foreach (var windowSeconds in EvaluationWindowsSeconds)
            pendingCommandEvaluations.Add(new PendingCommandEvaluation(baseline, windowSeconds, snapshot.UpdatedAtTicks + windowSeconds * 1000L));

        if (pendingCommandEvaluations.Count > 120)
            pendingCommandEvaluations.RemoveRange(0, pendingCommandEvaluations.Count - 120);
        Volatile.Write(ref pendingEvaluationCount, pendingCommandEvaluations.Count);
    }

    private void WriteDueCommandEvaluations(BattlefieldSnapshot snapshot, ReplayMetrics metrics)
    {
        if (pendingCommandEvaluations.Count == 0)
            return;

        foreach (var pending in pendingCommandEvaluations.Where(item => !item.Written && snapshot.UpdatedAtTicks >= item.DueAtTicks).ToArray())
        {
            var delta = BuildMetricDeltaFrame(pending.Baseline.Metrics, metrics);
            var heuristicScore = CalculateHeuristicOutcomeScore(pending.Baseline.Metrics, metrics);
            RecordCommandOutcome(pending.Baseline.Kind, heuristicScore);
            var evaluation = new
            {
                schema = "battlefield_command_evaluation_v1",
                windowSeconds = pending.WindowSeconds,
                command = new
                {
                    sequence = pending.Baseline.Sequence,
                    issuedAtTicks = pending.Baseline.IssuedAtTicks,
                    issuedUnixMs = pending.Baseline.IssuedUnixMs,
                    id = pending.Baseline.CommandId,
                    kind = pending.Baseline.Kind.ToString(),
                    scope = pending.Baseline.Scope,
                    text = pending.Baseline.CommandText,
                    score = Round(pending.Baseline.Score),
                    urgency = Round(pending.Baseline.Urgency),
                    target = pending.Baseline.TargetName,
                    reason = pending.Baseline.ReasonText,
                    evidence = pending.Baseline.EvidenceText,
                },
                baseline = BuildMetricsFrame(pending.Baseline.Metrics),
                current = BuildMetricsFrame(metrics),
                delta,
                result = new
                {
                    heuristicScore = Round(heuristicScore),
                    verdict = heuristicScore >= 8f ? "positive" : heuristicScore <= -8f ? "negative" : "mixed",
                    signals = BuildCommandEffectivenessSignals(pending.Baseline.Metrics, metrics),
                    source = "score_delta + kill_death_delta + risk_delta + dead_count_delta",
                    note = "用于调权重的后验代理，不代表指挥一定正确或错误。",
                },
            };

            WriteEvent("evaluation", snapshot, evaluation);
            evaluationEventsWritten++;
            pending.Written = true;
        }

        pendingCommandEvaluations.RemoveAll(item => item.Written || snapshot.UpdatedAtTicks - item.Baseline.IssuedAtTicks > 45000);
        Volatile.Write(ref pendingEvaluationCount, pendingCommandEvaluations.Count);
    }

    private static float CalculateHeuristicOutcomeScore(ReplayMetrics baseline, ReplayMetrics current)
    {
        var friendlyScoreDelta = current.FriendlyScore - baseline.FriendlyScore;
        var friendlyKillsDelta = current.FriendlyKillsTotal - baseline.FriendlyKillsTotal;
        var friendlyDeathsDelta = current.FriendlyDeathsTotal - baseline.FriendlyDeathsTotal;
        var enemyDeathsDelta = current.EnemyDeathsTotal - baseline.EnemyDeathsTotal;
        var friendlyDeadDelta = current.FriendlyDead - baseline.FriendlyDead;
        var riskDelta = current.OverallRisk - baseline.OverallRisk;
        var rankDelta = current.FriendlyRankIndex.HasValue && baseline.FriendlyRankIndex.HasValue
            ? current.FriendlyRankIndex.Value - baseline.FriendlyRankIndex.Value
            : 0;

        return friendlyScoreDelta * 0.75f
            + friendlyKillsDelta * 3.5f
            + enemyDeathsDelta * 2.0f
            - friendlyDeathsDelta * 4.5f
            - friendlyDeadDelta * 1.8f
            - riskDelta * 0.35f
            - rankDelta * 4.0f;
    }

    private static object BuildCommandEffectivenessSignals(ReplayMetrics baseline, ReplayMetrics current)
    {
        var friendlyScoreDelta = current.FriendlyScore - baseline.FriendlyScore;
        var friendlyDeathsDelta = current.FriendlyDeathsTotal - baseline.FriendlyDeathsTotal;
        var friendlyDeadDelta = current.FriendlyDead - baseline.FriendlyDead;
        var enemyDeathsDelta = current.EnemyDeathsTotal - baseline.EnemyDeathsTotal;
        var objectiveDelta = current.ObjectiveEventsTotal - baseline.ObjectiveEventsTotal;
        var riskDelta = current.OverallRisk - baseline.OverallRisk;
        return new
        {
            loweredDeaths = friendlyDeathsDelta <= 0 && friendlyDeadDelta <= 0,
            raisedScore = friendlyScoreDelta > 0,
            gainedObjectiveSignal = objectiveDelta > 0 || !string.Equals(current.PrimaryObjectiveId, baseline.PrimaryObjectiveId, StringComparison.Ordinal),
            createdKillPressure = enemyDeathsDelta > 0,
            loweredRisk = riskDelta < -4f,
            friendlyScoreDelta,
            friendlyDeathsDelta,
            friendlyDeadDelta,
            enemyDeathsDelta,
            objectiveDelta,
            riskDelta = Round(riskDelta),
        };
    }

    private void RecordCommandOutcome(BattlefieldCommandKind kind, float heuristicScore)
    {
        EnsureCommandOutcomeStatsLoaded();
        if (!commandOutcomeByKind.TryGetValue(kind, out var accumulator))
        {
            accumulator = new CommandOutcomeAccumulator();
            commandOutcomeByKind[kind] = accumulator;
        }

        accumulator.Add(heuristicScore);
        RefreshCommandEffectivenessCache();
        SaveCommandOutcomeStats();
    }

    private void EnsureCommandOutcomeStatsLoaded()
    {
        if (commandOutcomeStatsLoaded)
            return;

        commandOutcomeStatsLoaded = true;
        var path = ResolveCommandOutcomeStatsPath();
        if (!File.Exists(path))
        {
            RefreshCommandEffectivenessCache();
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<CommandOutcomeStatsDocument>(File.ReadAllText(path), JsonOptions);
            if (document?.Entries == null)
                return;

            foreach (var entry in document.Entries)
            {
                if (!Enum.TryParse<BattlefieldCommandKind>(entry.Kind, out var kind))
                    continue;

                commandOutcomeByKind[kind] = new CommandOutcomeAccumulator(entry.Scores ?? Array.Empty<float>());
            }

            RefreshCommandEffectivenessCache();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[Replay] Failed to load command outcome stats");
            RefreshCommandEffectivenessCache();
        }
    }

    private void RefreshCommandEffectivenessCache()
    {
        var snapshot = commandOutcomeByKind
            .Select(pair => pair.Value.ToSnapshot(pair.Key))
            .Where(item => item.SampleCount > 0)
            .OrderByDescending(item => item.SampleCount)
            .ThenBy(item => item.Kind)
            .ToArray();

        lock (statsLock)
            cachedCommandEffectivenessSnapshots = snapshot;
    }

    private void SaveCommandOutcomeStats()
    {
        try
        {
            var directory = ResolveReplayDirectory();
            Directory.CreateDirectory(directory);
            var document = new CommandOutcomeStatsDocument
            {
                Entries = commandOutcomeByKind
                    .Select(pair => new CommandOutcomeStatsEntry
                    {
                        Kind = pair.Key.ToString(),
                        Scores = pair.Value.Samples.ToArray()
                    })
                    .ToList()
            };
            File.WriteAllText(ResolveCommandOutcomeStatsPath(), JsonSerializer.Serialize(document, JsonOptions));
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[Replay] Failed to save command outcome stats");
        }
    }

    private void ResetDecisionQualityFeedbackCore()
    {
        pendingCommandEvaluations.Clear();
        commandOutcomeByKind.Clear();
        lock (statsLock)
            cachedCommandEffectivenessSnapshots = Array.Empty<BattlefieldCommandEffectivenessSnapshot>();
        lastRecordedCommandSequence = 0;
        Volatile.Write(ref pendingEvaluationCount, 0);
        commandOutcomeStatsLoaded = true;
        try
        {
            var path = ResolveCommandOutcomeStatsPath();
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[Replay] Failed to delete decision quality stats");
        }
    }

    private string ResolveCommandOutcomeStatsPath()
        => Path.Combine(ResolveReplayDirectory(), "decision_quality_stats.json");

    private void UpdateCumulativeChatEvents(IEnumerable<BattlefieldChatEventSnapshot> events)
    {
        foreach (var item in events.OrderBy(item => item.ObservedAtTicks))
        {
            var key = $"{item.ObservedAtTicks}:{item.Kind}:{item.Text}";
            if (!seenChatEventKeys.Add(key))
                continue;

            seenChatEventKeyOrder.Enqueue(key);
            while (seenChatEventKeyOrder.Count > MaxSeenChatEvents)
                seenChatEventKeys.Remove(seenChatEventKeyOrder.Dequeue());

            switch (item.Kind)
            {
                case BattlefieldChatEventKind.Kill:
                    if (item.ActorSide == BattlefieldTacticalSide.Friendly && item.TargetSide == BattlefieldTacticalSide.Enemy)
                    {
                        friendlyKillsTotal++;
                        enemyDeathsTotal++;
                    }
                    else if (item.ActorSide == BattlefieldTacticalSide.Enemy && item.TargetSide == BattlefieldTacticalSide.Friendly)
                    {
                        enemyKillsTotal++;
                        friendlyDeathsTotal++;
                    }
                    break;
                case BattlefieldChatEventKind.BattleHigh:
                    battleHighEventsTotal++;
                    break;
                case BattlefieldChatEventKind.ObjectiveCaptured:
                case BattlefieldChatEventKind.ObjectiveLost:
                case BattlefieldChatEventKind.ObjectiveContested:
                case BattlefieldChatEventKind.ObjectiveOther:
                    objectiveEventsTotal++;
                    break;
            }
        }
    }

    private void MaybePruneOldSessions(long now)
    {
        if (lastPruneTicks >= 0 && now - lastPruneTicks < PruneIntervalMs)
            return;

        var directory = ResolveReplayDirectory();
        if (!Directory.Exists(directory))
            return;

        PruneOldSessionFiles(directory, false);
    }

    private void PruneOldSessionFiles(string directory, bool force)
    {
        try
        {
            var nowTicks = Environment.TickCount64;
            if (!force && lastPruneTicks >= 0 && nowTicks - lastPruneTicks < PruneIntervalMs)
                return;

            Volatile.Write(ref lastPruneTicks, nowTicks);
            var maxAge = TimeSpan.FromDays(configuration.Replay.MaxSessionAgeDays);
            var now = DateTime.UtcNow;
            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl").Select(path => new FileInfo(path)))
            {
                if (now - file.LastWriteTimeUtc > maxAge)
                    TryDelete(file);
            }

            var remaining = Directory.EnumerateFiles(directory, "*.jsonl")
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(configuration.Replay.MaxSessionFiles)
                .ToArray();
            foreach (var file in remaining)
                TryDelete(file);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[Replay] Failed to prune old replay files");
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch
        {
            // Pruning is best effort.
        }
    }

    private string ResolveReplayDirectory()
        => BattlefieldReplayStoragePath.ResolveDirectory(configuration.Replay.DirectoryName);

    private static string SanitizeFilePart(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "frontline";

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Trim().Where(ch => !invalid.Contains(ch)).Take(32).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "frontline" : sanitized;
    }

    private static int ObjectiveStateSort(BattlefieldMapObjectiveState state)
        => state switch
        {
            BattlefieldMapObjectiveState.Contested => 6,
            BattlefieldMapObjectiveState.Active => 5,
            BattlefieldMapObjectiveState.Warning => 4,
            BattlefieldMapObjectiveState.Controlled => 3,
            BattlefieldMapObjectiveState.Inactive => 2,
            BattlefieldMapObjectiveState.Destroyed => 1,
            _ => 0,
        };

    private static object Position(Vector3 position)
        => new
        {
            x = Round(position.X),
            y = Round(position.Y),
            z = Round(position.Z),
        };

    private static float Round(float value)
        => MathF.Round(value, 2);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class ReplayEnvelope<T>
    {
        public string Type { get; init; } = string.Empty;
        public string RecorderVersion { get; init; } = BattlefieldReplayRecorder.RecorderVersion;
        public string SessionId { get; init; } = string.Empty;
        public long UnixMs { get; init; }
        public long UpdatedAtTicks { get; init; }
        public uint TerritoryType { get; init; }
        public uint MapId { get; init; }
        public string MapName { get; init; } = string.Empty;
        public T? Data { get; init; }
    }

    private readonly record struct ReplayMetrics(
        int FriendlyScore,
        int LeadingScore,
        int ScoreTotal,
        int? FriendlyRankIndex,
        int FriendlyAlive,
        int FriendlyDead,
        int EnemyAlive,
        int EnemyDead,
        int FriendlyKillsTotal,
        int FriendlyDeathsTotal,
        int EnemyKillsTotal,
        int EnemyDeathsTotal,
        int ObjectiveEventsTotal,
        int BattleHighEventsTotal,
        float OverallRisk,
        string PrimaryObjectiveId,
        string PrimaryObjectiveName,
        string CommandText,
        int CommandSequence);

    private sealed record CommandEvaluationBaseline(
        int Sequence,
        long IssuedAtTicks,
        long IssuedUnixMs,
        string CommandId,
        BattlefieldCommandKind Kind,
        string Scope,
        string CommandText,
        float Score,
        float Urgency,
        string TargetName,
        string ReasonText,
        string EvidenceText,
        ReplayMetrics Metrics);

    private sealed class PendingCommandEvaluation
    {
        public PendingCommandEvaluation(CommandEvaluationBaseline baseline, int windowSeconds, long dueAtTicks)
        {
            Baseline = baseline;
            WindowSeconds = windowSeconds;
            DueAtTicks = dueAtTicks;
        }

        public CommandEvaluationBaseline Baseline { get; }
        public int WindowSeconds { get; }
        public long DueAtTicks { get; }
        public bool Written { get; set; }
    }

    private sealed class CommandOutcomeAccumulator
    {
        private const int MaxSamples = 80;
        private readonly Queue<float> samples = new();

        public CommandOutcomeAccumulator()
        {
        }

        public CommandOutcomeAccumulator(IEnumerable<float> initialSamples)
        {
            foreach (var sample in initialSamples)
                Add(sample);
        }

        public IReadOnlyCollection<float> Samples => samples;

        public void Add(float score)
        {
            samples.Enqueue(score);
            while (samples.Count > MaxSamples)
                samples.Dequeue();
        }

        public BattlefieldCommandEffectivenessSnapshot ToSnapshot(BattlefieldCommandKind kind)
        {
            if (samples.Count == 0)
            {
                return new BattlefieldCommandEffectivenessSnapshot(
                    kind,
                    0,
                    0f,
                    0f,
                    0f,
                    "暂无回放评估样本");
            }

            var values = samples.ToArray();
            var average = values.Average();
            var positiveRate = values.Count(value => value >= 8f) / (float)values.Length;
            var modifier = values.Length < 4 ? 0f : Math.Clamp(average * 0.18f + (positiveRate - 0.5f) * 8f, -8f, 8f);
            var summary = $"{CommandKindText(kind)}：样本 {values.Length}，均值 {average:+0.0;-0.0;0}，正向 {positiveRate:P0}，调权 {modifier:+0.0;-0.0;0}";
            return new BattlefieldCommandEffectivenessSnapshot(
                kind,
                values.Length,
                average,
                positiveRate,
                modifier,
                summary);
        }

        private static string CommandKindText(BattlefieldCommandKind kind)
            => kind switch
            {
                BattlefieldCommandKind.Regroup => "集合",
                BattlefieldCommandKind.Retreat => "撤退",
                BattlefieldCommandKind.Disengage => "脱战",
                BattlefieldCommandKind.Rotate => "转点",
                BattlefieldCommandKind.AttackObjective => "进攻目标",
                BattlefieldCommandKind.ContestObjective => "争夺目标",
                BattlefieldCommandKind.DefendObjective => "防守目标",
                BattlefieldCommandKind.AbandonObjective => "放弃目标",
                BattlefieldCommandKind.Split => "分队",
                BattlefieldCommandKind.FocusTarget => "集火",
                BattlefieldCommandKind.ProtectTarget => "保护",
                BattlefieldCommandKind.Spread => "散开",
                BattlefieldCommandKind.Hold => "稳住",
                BattlefieldCommandKind.Detour => "绕路",
                BattlefieldCommandKind.PressureSide => "侧压",
                BattlefieldCommandKind.Wait => "等待",
                _ => "未知",
            };
    }

    private sealed class CommandOutcomeStatsDocument
    {
        public List<CommandOutcomeStatsEntry> Entries { get; set; } = new();
    }

    private enum ReplayWorkKind
    {
        Frame,
        Close,
        ResetStats,
        Shutdown,
    }

    private readonly record struct ReplayWorkItem(
        ReplayWorkKind Kind,
        BattlefieldSnapshot? Snapshot,
        string Reason)
    {
        public static ReplayWorkItem Frame(BattlefieldSnapshot snapshot)
            => new(ReplayWorkKind.Frame, snapshot, string.Empty);

        public static ReplayWorkItem Close(BattlefieldSnapshot? snapshot, string reason)
            => new(ReplayWorkKind.Close, snapshot, reason);

        public static ReplayWorkItem ResetStats()
            => new(ReplayWorkKind.ResetStats, null, "reset-stats");

        public static ReplayWorkItem Shutdown()
            => new(ReplayWorkKind.Shutdown, null, "shutdown");
    }

    private sealed class CommandOutcomeStatsEntry
    {
        public string Kind { get; set; } = string.Empty;
        public float[] Scores { get; set; } = Array.Empty<float>();
    }
}

public sealed class BattlefieldReplayRecorderStatus
{
    public bool Enabled { get; init; }
    public bool IsRecording { get; init; }
    public string DirectoryPath { get; init; } = string.Empty;
    public string CurrentSessionId { get; init; } = string.Empty;
    public string CurrentFilePath { get; init; } = string.Empty;
    public int FramesWritten { get; init; }
    public int EvaluationEventsWritten { get; init; }
    public int PendingEvaluations { get; init; }
    public int QueuedWorkItems { get; init; }
    public int DroppedFrames { get; init; }
    public long LastWriteAgeMs { get; init; } = -1;
    public long LastPruneAgeMs { get; init; } = -1;
    public string LastError { get; init; } = string.Empty;
    public string StatusText { get; init; } = "实战回放：等待进入纷争前线";
}
