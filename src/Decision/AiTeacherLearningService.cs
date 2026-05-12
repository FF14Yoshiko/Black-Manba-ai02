using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;

namespace ai02;

public sealed class AiTeacherLearningService : IDisposable
{
    private const int MaxSeenChatEvents = 2048;
    private const int MaxPendingEvaluations = 160;
    private const int MaxRecentLearned = 16;
    private const int SaveWorkerJoinTimeoutMs = 1000;
    private static readonly int[] EvaluationWindowsSeconds = { 10, 30 };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly Func<bool> isEnabled;
    private readonly Func<string> resolveReplayDirectory;
    private readonly object sync = new();
    private readonly object persistenceSync = new();
    private readonly List<PendingTeacherEvaluation> pendingEvaluations = new();
    private readonly Dictionary<TeacherCommandKey, TeacherOutcomeAccumulator> teacherOutcomeByCommandKey = new();
    private readonly Dictionary<TeacherTargetKey, TeacherOutcomeAccumulator> teacherOutcomeByTargetKey = new();
    private readonly HashSet<int> seenSequences = new();
    private readonly Queue<int> seenSequenceOrder = new();
    private readonly HashSet<string> seenChatEventKeys = new(StringComparer.Ordinal);
    private readonly Queue<string> seenChatEventKeyOrder = new();
    private readonly Queue<BattlefieldAiTeacherRecentLearningSnapshot> recentLearned = new();
    private readonly Dictionary<FrontlineMapType, BattlefieldCommandEffectivenessSnapshot[]> cachedCommandSnapshotsByMap = new();
    private readonly Dictionary<FrontlineMapType, BattlefieldAiTeacherTargetResolutionSnapshot[]> cachedTargetSnapshotsByMap = new();
    private readonly Thread persistenceWorkerThread;
    private BattlefieldAiTeacherRecentLearningSnapshot[] cachedRecentLearned = Array.Empty<BattlefieldAiTeacherRecentLearningSnapshot>();
    private TeacherPersistenceWorkItem? pendingPersistenceWorkItem;
    private bool loaded;
    private bool persistenceStopRequested;
    private bool disposed;
    private string currentSessionKey = string.Empty;
    private int previousRemainingSeconds = -1;
    private int friendlyKillsTotal;
    private int friendlyDeathsTotal;
    private int enemyKillsTotal;
    private int enemyDeathsTotal;
    private int objectiveEventsTotal;
    private int battleHighEventsTotal;
    private long persistenceSequence;
    private long completedPersistenceSequence;

    public AiTeacherLearningService(Func<bool> isEnabled, Func<string> resolveReplayDirectory)
    {
        this.isEnabled = isEnabled;
        this.resolveReplayDirectory = resolveReplayDirectory;
        persistenceWorkerThread = new Thread(PersistenceWorkerLoop)
        {
            IsBackground = true,
            Name = "ai02 ai teacher persistence"
        };
        persistenceWorkerThread.Start();
    }

    public BattlefieldCommandEffectivenessSnapshot[] GetCommandEffectivenessSnapshots(FrontlineMapType mapType = FrontlineMapType.Unknown)
    {
        lock (sync)
        {
            EnsureLoadedNoLock();
            return GetCachedCommandSnapshotsNoLock(mapType).ToArray();
        }
    }

    public BattlefieldAiTeacherTargetResolutionSnapshot[] GetTargetResolutionSnapshots(FrontlineMapType mapType = FrontlineMapType.Unknown)
    {
        lock (sync)
        {
            EnsureLoadedNoLock();
            return GetCachedTargetSnapshotsNoLock(mapType).ToArray();
        }
    }

    public BattlefieldAiTeacherLearningStatusSnapshot GetStatus(FrontlineMapType mapType)
    {
        lock (sync)
        {
            EnsureLoadedNoLock();
            var commands = GetCachedCommandSnapshotsNoLock(mapType);
            var targets = GetCachedTargetSnapshotsNoLock(mapType);
            var recent = cachedRecentLearned
                .Where(item => mapType == FrontlineMapType.Unknown || item.MapType == mapType)
                .Take(8)
                .ToArray();
            var commandSampleCount = commands.Sum(item => item.SampleCount);
            var targetSampleCount = targets.Sum(item => item.SampleCount);
            var label = mapType == FrontlineMapType.Unknown ? "\u5168\u90e8\u5730\u56fe" : mapType.ToString();
            var statusText = !isEnabled()
                ? "\u672a\u542f\u7528 AI \u8001\u5e08\u5b66\u4e60"
                : commandSampleCount == 0 && targetSampleCount == 0
                    ? $"\u6682\u65e0 {label} \u7684 AI \u8001\u5e08\u5b66\u4e60\u6837\u672c"
                    : $"AI \u8001\u5e08 {label}\uff1a\u547d\u4ee4 {commandSampleCount} \u6761\uff0c\u843d\u70b9 {targetSampleCount} \u6761";

            return new BattlefieldAiTeacherLearningStatusSnapshot
            {
                Enabled = isEnabled(),
                MapType = mapType,
                StatsPath = ResolveStatsPath(),
                CommandSampleCount = commandSampleCount,
                TargetResolutionSampleCount = targetSampleCount,
                CommandEffectiveness = commands,
                TargetResolutions = targets,
                RecentLearned = recent,
                StatusText = statusText
            };
        }
    }

    public float GetTargetResolutionModifier(FrontlineMapType mapType, StrategicTargetResolutionKind kind, string targetId, string targetName)
    {
        if (kind == StrategicTargetResolutionKind.None)
            return 0f;

        lock (sync)
        {
            EnsureLoadedNoLock();
            var key = TeacherTargetKey.Create(mapType, kind, targetId, targetName);
            return teacherOutcomeByTargetKey.TryGetValue(key, out var accumulator)
                ? accumulator.GetModifier()
                : 0f;
        }
    }

    public void ResetLearningStats()
    {
        long deleteSequence;
        lock (sync)
        {
            teacherOutcomeByCommandKey.Clear();
            teacherOutcomeByTargetKey.Clear();
            recentLearned.Clear();
            RefreshCachesNoLock();
            ResetPendingSessionNoLock();
            loaded = true;
            deleteSequence = QueueDeletePersistenceNoLock();
        }

        WaitForPersistence(deleteSequence);
    }

    public void Record(BattlefieldSnapshot snapshot)
    {
        if (disposed)
            return;

        if (!isEnabled())
        {
            ResetPendingSession();
            return;
        }

        lock (sync)
        {
            EnsureLoadedNoLock();
            if (!snapshot.IsInFrontline)
            {
                ResetPendingSessionNoLock();
                return;
            }

            EnsureSession(snapshot);
            UpdateCumulativeChatEvents(snapshot.ChatEventSituation.RecentEvents);
            RegisterAiTeacherCommand(snapshot);
            EvaluateDueSamples(snapshot);
        }
    }

    private void EnsureLoadedNoLock()
    {
        if (loaded)
            return;

        loaded = true;
        var path = ResolveStatsPath();
        if (!File.Exists(path))
        {
            RefreshCachesNoLock();
            return;
        }

        try
        {
            var document = JsonSerializer.Deserialize<TeacherOutcomeStatsDocument>(File.ReadAllText(path), JsonOptions);
            if (document?.Entries != null)
            {
                foreach (var entry in document.Entries)
                {
                    if (!Enum.TryParse<BattlefieldCommandKind>(entry.Kind, out var legacyKind))
                        continue;

                    teacherOutcomeByCommandKey[new TeacherCommandKey(FrontlineMapType.Unknown, legacyKind)] =
                        new TeacherOutcomeAccumulator(entry.Scores ?? Array.Empty<float>());
                }
            }

            if (document?.CommandEntries != null)
            {
                foreach (var entry in document.CommandEntries)
                {
                    if (!Enum.TryParse(entry.Kind, out BattlefieldCommandKind kind))
                        continue;
                    if (!Enum.TryParse(entry.MapType, out FrontlineMapType mapType))
                        mapType = FrontlineMapType.Unknown;

                    teacherOutcomeByCommandKey[new TeacherCommandKey(mapType, kind)] =
                        new TeacherOutcomeAccumulator(entry.Scores ?? Array.Empty<float>());
                }
            }

            if (document?.TargetEntries != null)
            {
                foreach (var entry in document.TargetEntries)
                {
                    if (!Enum.TryParse(entry.MapType, out FrontlineMapType mapType))
                        mapType = FrontlineMapType.Unknown;
                    if (!Enum.TryParse(entry.Kind, out StrategicTargetResolutionKind kind))
                        continue;

                    var key = new TeacherTargetKey(
                        mapType,
                        kind,
                        string.IsNullOrWhiteSpace(entry.TargetKey) ? TeacherTargetKey.Normalize(entry.TargetName ?? string.Empty) : entry.TargetKey,
                        entry.TargetName ?? string.Empty);
                    teacherOutcomeByTargetKey[key] = new TeacherOutcomeAccumulator(entry.Scores ?? Array.Empty<float>());
                }
            }
        }
        catch
        {
            teacherOutcomeByCommandKey.Clear();
            teacherOutcomeByTargetKey.Clear();
        }

        RefreshCachesNoLock();
    }

    private void EnsureSession(BattlefieldSnapshot snapshot)
    {
        var sessionKey = $"{snapshot.TerritoryType}:{snapshot.MapId}";
        var isNewMatch = !string.Equals(currentSessionKey, sessionKey, StringComparison.Ordinal)
            || (previousRemainingSeconds >= 0 && snapshot.MatchTimeRemaining > previousRemainingSeconds + 45);
        if (isNewMatch)
        {
            ResetPendingSessionNoLock();
            currentSessionKey = sessionKey;
        }

        previousRemainingSeconds = snapshot.MatchTimeRemaining;
    }

    private void RegisterAiTeacherCommand(BattlefieldSnapshot snapshot)
    {
        if (!CommandOverlayAiDisplayPolicy.IsAiLead(snapshot.Decision))
            return;

        var command = snapshot.Decision.CommandSituation.Publish.Command
            ?? snapshot.Decision.CommandSituation.PrimaryCommand;
        if (!command.HasValue)
            return;

        var sequence = snapshot.Decision.CommandSituation.Publish.Sequence > 0
            ? snapshot.Decision.CommandSituation.Publish.Sequence
            : HashCode.Combine(command.Value.Id, snapshot.UpdatedAtTicks);
        if (!seenSequences.Add(sequence))
            return;

        seenSequenceOrder.Enqueue(sequence);
        while (seenSequenceOrder.Count > 160)
            seenSequences.Remove(seenSequenceOrder.Dequeue());

        var localCommand = snapshot.LocalDecision.CommandSituation.Publish.Command
            ?? snapshot.LocalDecision.CommandSituation.PrimaryCommand;
        var mapType = snapshot.ScoreSituation.MapType;
        var resolutionKind = StrategicTargetResolutionLearningPolicy.Detect(
            snapshot.LlmStrategicDecision.RecommendedAction,
            snapshot.LlmStrategicDecision.PriorityTarget);
        var targetOutcome = ResolveTargetOutcome(snapshot.Decision, resolutionKind);
        var baseline = new TeacherCommandBaseline(
            sequence,
            snapshot.UpdatedAtTicks,
            mapType,
            command.Value.Id,
            command.Value.Kind,
            command.Value.CommandText,
            command.Value.TargetName,
            localCommand?.Kind ?? BattlefieldCommandKind.Unknown,
            localCommand?.CommandText ?? string.Empty,
            resolutionKind,
            targetOutcome.TargetKey,
            targetOutcome.TargetName,
            CaptureMetrics(snapshot));

        foreach (var windowSeconds in EvaluationWindowsSeconds)
            pendingEvaluations.Add(new PendingTeacherEvaluation(baseline, windowSeconds, snapshot.UpdatedAtTicks + windowSeconds * 1000L));

        if (pendingEvaluations.Count > MaxPendingEvaluations)
            pendingEvaluations.RemoveRange(0, pendingEvaluations.Count - MaxPendingEvaluations);
    }

    private void EvaluateDueSamples(BattlefieldSnapshot snapshot)
    {
        if (pendingEvaluations.Count == 0)
            return;

        var currentMetrics = CaptureMetrics(snapshot);
        var changed = false;
        foreach (var pending in pendingEvaluations.Where(item => !item.Written && snapshot.UpdatedAtTicks >= item.DueAtTicks).ToArray())
        {
            var heuristicScore = CalculateHeuristicOutcomeScore(pending.Baseline.Metrics, currentMetrics);
            RecordCommandOutcomeNoLock(pending.Baseline.MapType, pending.Baseline.Kind, heuristicScore);
            AppendRecentLearnedNoLock(
                snapshot.UpdatedAtTicks,
                pending.Baseline.MapType,
                "\u547d\u4ee4",
                pending.Baseline.Kind.ToString(),
                heuristicScore,
                pending.WindowSeconds);

            if (pending.Baseline.ResolutionKind != StrategicTargetResolutionKind.None
                && !string.IsNullOrWhiteSpace(pending.Baseline.TargetResolutionKey))
            {
                RecordTargetOutcomeNoLock(
                    pending.Baseline.MapType,
                    pending.Baseline.ResolutionKind,
                    pending.Baseline.TargetResolutionKey,
                    pending.Baseline.TargetResolutionName,
                    heuristicScore);
                AppendRecentLearnedNoLock(
                    snapshot.UpdatedAtTicks,
                    pending.Baseline.MapType,
                    "\u843d\u70b9",
                    $"{pending.Baseline.ResolutionKind} -> {pending.Baseline.TargetResolutionName}",
                    heuristicScore,
                    pending.WindowSeconds);
            }

            pending.Written = true;
            changed = true;
        }

        pendingEvaluations.RemoveAll(item => item.Written || snapshot.UpdatedAtTicks - item.Baseline.IssuedAtTicks > 45_000);
        if (changed)
            QueueSaveNoLock();
    }

    private void RecordCommandOutcomeNoLock(FrontlineMapType mapType, BattlefieldCommandKind kind, float heuristicScore)
    {
        var key = new TeacherCommandKey(mapType, kind);
        if (!teacherOutcomeByCommandKey.TryGetValue(key, out var accumulator))
        {
            accumulator = new TeacherOutcomeAccumulator();
            teacherOutcomeByCommandKey[key] = accumulator;
        }

        accumulator.Add(heuristicScore);
        RefreshCachesNoLock();
    }

    private void RecordTargetOutcomeNoLock(
        FrontlineMapType mapType,
        StrategicTargetResolutionKind kind,
        string targetKey,
        string targetName,
        float heuristicScore)
    {
        var key = TeacherTargetKey.Create(mapType, kind, targetKey, targetName);
        if (!teacherOutcomeByTargetKey.TryGetValue(key, out var accumulator))
        {
            accumulator = new TeacherOutcomeAccumulator();
            teacherOutcomeByTargetKey[key] = accumulator;
        }

        accumulator.Add(heuristicScore);
        RefreshCachesNoLock();
    }

    private void AppendRecentLearnedNoLock(
        long observedAtTicks,
        FrontlineMapType mapType,
        string category,
        string label,
        float heuristicScore,
        int windowSeconds)
    {
        var direction = heuristicScore >= 0f ? "\u6b63\u53cd\u9988" : "\u8d1f\u53cd\u9988";
        recentLearned.Enqueue(new BattlefieldAiTeacherRecentLearningSnapshot(
            observedAtTicks,
            mapType,
            category,
            label,
            heuristicScore,
            windowSeconds,
            $"{category} {label} {direction} {heuristicScore:+0.0;-0.0;0} ({windowSeconds}s)"));
        while (recentLearned.Count > MaxRecentLearned)
            recentLearned.Dequeue();

        cachedRecentLearned = recentLearned.Reverse().ToArray();
    }

    private void RefreshCachesNoLock()
    {
        cachedCommandSnapshotsByMap.Clear();
        cachedTargetSnapshotsByMap.Clear();

        var mapTypes = teacherOutcomeByCommandKey.Keys.Select(item => item.MapType)
            .Concat(teacherOutcomeByTargetKey.Keys.Select(item => item.MapType))
            .Distinct()
            .ToArray();

        cachedCommandSnapshotsByMap[FrontlineMapType.Unknown] = BuildCommandSnapshotsNoLock(null);
        cachedTargetSnapshotsByMap[FrontlineMapType.Unknown] = BuildTargetSnapshotsNoLock(null);
        foreach (var mapType in mapTypes)
        {
            cachedCommandSnapshotsByMap[mapType] = BuildCommandSnapshotsNoLock(mapType);
            cachedTargetSnapshotsByMap[mapType] = BuildTargetSnapshotsNoLock(mapType);
        }

        cachedRecentLearned = recentLearned.Reverse().ToArray();
    }

    private BattlefieldCommandEffectivenessSnapshot[] BuildCommandSnapshotsNoLock(FrontlineMapType? mapType)
    {
        var groups = teacherOutcomeByCommandKey
            .Where(pair => !mapType.HasValue || pair.Key.MapType == mapType.Value)
            .GroupBy(pair => pair.Key.Kind);
        return groups
            .Select(group => TeacherOutcomeAccumulator.Combine(group.Select(item => item.Value)).ToCommandSnapshot(group.Key))
            .Where(item => item.SampleCount > 0)
            .OrderByDescending(item => item.SampleCount)
            .ThenBy(item => item.Kind)
            .ToArray();
    }

    private BattlefieldAiTeacherTargetResolutionSnapshot[] BuildTargetSnapshotsNoLock(FrontlineMapType? mapType)
    {
        var groups = teacherOutcomeByTargetKey
            .Where(pair => !mapType.HasValue || pair.Key.MapType == mapType.Value)
            .GroupBy(pair => new TargetGroupingKey(pair.Key.Kind, pair.Key.TargetKey, pair.Key.TargetName));
        return groups
            .Select(group => TeacherOutcomeAccumulator.Combine(group.Select(item => item.Value))
                .ToTargetSnapshot(group.Key.Kind, group.Key.TargetKey, group.Key.TargetName))
            .Where(item => item.SampleCount > 0)
            .OrderByDescending(item => item.SampleCount)
            .ThenBy(item => item.Kind)
            .ThenBy(item => item.TargetName)
            .ToArray();
    }

    private BattlefieldCommandEffectivenessSnapshot[] GetCachedCommandSnapshotsNoLock(FrontlineMapType mapType)
        => cachedCommandSnapshotsByMap.TryGetValue(mapType, out var snapshots)
            ? snapshots
            : Array.Empty<BattlefieldCommandEffectivenessSnapshot>();

    private BattlefieldAiTeacherTargetResolutionSnapshot[] GetCachedTargetSnapshotsNoLock(FrontlineMapType mapType)
        => cachedTargetSnapshotsByMap.TryGetValue(mapType, out var snapshots)
            ? snapshots
            : Array.Empty<BattlefieldAiTeacherTargetResolutionSnapshot>();

    public void Dispose()
    {
        if (disposed)
            return;

        WaitForPersistence(GetLatestPersistenceSequence());
        lock (persistenceSync)
        {
            if (disposed)
                return;

            disposed = true;
            persistenceStopRequested = true;
            Monitor.PulseAll(persistenceSync);
        }

        persistenceWorkerThread.Join(SaveWorkerJoinTimeoutMs);
    }

    private long QueueSaveNoLock()
        => EnqueuePersistenceWork(new TeacherPersistenceWorkItem(
            CreateNextPersistenceSequence(),
            ResolveStatsPath(),
            BuildStatsDocumentNoLock(),
            deleteFile: false));

    private long QueueDeletePersistenceNoLock()
        => EnqueuePersistenceWork(new TeacherPersistenceWorkItem(
            CreateNextPersistenceSequence(),
            ResolveStatsPath(),
            document: null,
            deleteFile: true));

    private TeacherOutcomeStatsDocument BuildStatsDocumentNoLock()
        => new()
        {
            CommandEntries = teacherOutcomeByCommandKey
                .Select(pair => new TeacherCommandOutcomeStatsEntry
                {
                    MapType = pair.Key.MapType.ToString(),
                    Kind = pair.Key.Kind.ToString(),
                    Scores = pair.Value.Samples.ToArray()
                })
                .OrderBy(entry => entry.MapType)
                .ThenBy(entry => entry.Kind)
                .ToList(),
            TargetEntries = teacherOutcomeByTargetKey
                .Select(pair => new TeacherTargetOutcomeStatsEntry
                {
                    MapType = pair.Key.MapType.ToString(),
                    Kind = pair.Key.Kind.ToString(),
                    TargetKey = pair.Key.TargetKey,
                    TargetName = pair.Key.TargetName,
                    Scores = pair.Value.Samples.ToArray()
                })
                .OrderBy(entry => entry.MapType)
                .ThenBy(entry => entry.Kind)
                .ThenBy(entry => entry.TargetName)
                .ToList()
        };

    private long CreateNextPersistenceSequence()
    {
        lock (persistenceSync)
            return ++persistenceSequence;
    }

    private long EnqueuePersistenceWork(TeacherPersistenceWorkItem workItem)
    {
        lock (persistenceSync)
        {
            if (disposed)
                return completedPersistenceSequence;

            pendingPersistenceWorkItem = workItem;
            Monitor.PulseAll(persistenceSync);
            return workItem.Sequence;
        }
    }

    private long GetLatestPersistenceSequence()
    {
        lock (persistenceSync)
            return persistenceSequence;
    }

    private void WaitForPersistence(long sequence)
    {
        lock (persistenceSync)
        {
            while (completedPersistenceSequence < sequence)
                Monitor.Wait(persistenceSync);
        }
    }

    private void PersistenceWorkerLoop()
    {
        while (true)
        {
            TeacherPersistenceWorkItem? workItem;
            lock (persistenceSync)
            {
                while (pendingPersistenceWorkItem is null && !persistenceStopRequested)
                    Monitor.Wait(persistenceSync);

                if (pendingPersistenceWorkItem is null && persistenceStopRequested)
                    return;

                workItem = pendingPersistenceWorkItem;
                pendingPersistenceWorkItem = null;
            }

            ExecutePersistenceWork(workItem!);

            lock (persistenceSync)
            {
                completedPersistenceSequence = Math.Max(completedPersistenceSequence, workItem!.Sequence);
                Monitor.PulseAll(persistenceSync);
            }
        }
    }

    private void ExecutePersistenceWork(TeacherPersistenceWorkItem workItem)
    {
        try
        {
            if (workItem.DeleteFile)
            {
                if (File.Exists(workItem.Path))
                    File.Delete(workItem.Path);
                return;
            }

            var directory = Path.GetDirectoryName(workItem.Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(workItem.Path, JsonSerializer.Serialize(workItem.Document, JsonOptions));
        }
        catch
        {
            // Persistence is best effort.
        }
    }

    private void ResetPendingSession()
    {
        lock (sync)
            ResetPendingSessionNoLock();
    }

    private void ResetPendingSessionNoLock()
    {
        pendingEvaluations.Clear();
        seenSequences.Clear();
        seenSequenceOrder.Clear();
        seenChatEventKeys.Clear();
        seenChatEventKeyOrder.Clear();
        currentSessionKey = string.Empty;
        previousRemainingSeconds = -1;
        friendlyKillsTotal = 0;
        friendlyDeathsTotal = 0;
        enemyKillsTotal = 0;
        enemyDeathsTotal = 0;
        objectiveEventsTotal = 0;
        battleHighEventsTotal = 0;
    }

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

    private string ResolveStatsPath()
        => Path.Combine(resolveReplayDirectory(), "ai_teacher_learning_stats.json");

    private TeacherReplayMetrics CaptureMetrics(BattlefieldSnapshot snapshot)
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

        return new TeacherReplayMetrics(
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

    private static TargetResolutionOutcome ResolveTargetOutcome(
        BattlefieldDecisionSnapshot decision,
        StrategicTargetResolutionKind kind)
    {
        if (kind == StrategicTargetResolutionKind.None)
            return default;

        var action = decision.CommandSituation.PublishedAction
            ?? decision.CommandSituation.PrimaryAction
            ?? decision.PrimaryAction;
        if (action.HasValue && (!string.IsNullOrWhiteSpace(action.Value.TargetId) || !string.IsNullOrWhiteSpace(action.Value.TargetName)))
        {
            var targetKey = TeacherTargetKey.Normalize(!string.IsNullOrWhiteSpace(action.Value.TargetId) ? action.Value.TargetId : action.Value.TargetName);
            return new TargetResolutionOutcome(true, targetKey, action.Value.TargetName);
        }

        if (kind is StrategicTargetResolutionKind.HighValueObjective or StrategicTargetResolutionKind.NearObjective or StrategicTargetResolutionKind.FarObjective)
        {
            if (decision.PrimaryObjective.HasValue)
            {
                var objective = decision.PrimaryObjective.Value;
                return new TargetResolutionOutcome(true, TeacherTargetKey.Normalize(objective.ObjectiveId), objective.Name);
            }

            if (decision.ObjectivePriorityTarget.HasValue)
            {
                var target = decision.ObjectivePriorityTarget.Value;
                return new TargetResolutionOutcome(true, TeacherTargetKey.Normalize(target.TargetName), target.TargetName);
            }
        }

        if (decision.FightPriorityTarget.HasValue)
        {
            var target = decision.FightPriorityTarget.Value;
            return new TargetResolutionOutcome(true, TeacherTargetKey.Normalize(target.TargetName), target.TargetName);
        }

        return default;
    }

    private static float CalculateHeuristicOutcomeScore(TeacherReplayMetrics baseline, TeacherReplayMetrics current)
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

    private readonly record struct TeacherReplayMetrics(
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
        int PublishSequence);

    private readonly record struct TeacherCommandBaseline(
        int Sequence,
        long IssuedAtTicks,
        FrontlineMapType MapType,
        string CommandId,
        BattlefieldCommandKind Kind,
        string CommandText,
        string TargetName,
        BattlefieldCommandKind LocalKind,
        string LocalCommandText,
        StrategicTargetResolutionKind ResolutionKind,
        string TargetResolutionKey,
        string TargetResolutionName,
        TeacherReplayMetrics Metrics);

    private sealed class PendingTeacherEvaluation
    {
        public PendingTeacherEvaluation(TeacherCommandBaseline baseline, int windowSeconds, long dueAtTicks)
        {
            Baseline = baseline;
            WindowSeconds = windowSeconds;
            DueAtTicks = dueAtTicks;
        }

        public TeacherCommandBaseline Baseline { get; }
        public int WindowSeconds { get; }
        public long DueAtTicks { get; }
        public bool Written { get; set; }
    }

    private sealed class TeacherPersistenceWorkItem
    {
        public TeacherPersistenceWorkItem(long sequence, string path, TeacherOutcomeStatsDocument? document, bool deleteFile)
        {
            Sequence = sequence;
            Path = path;
            Document = document;
            DeleteFile = deleteFile;
        }

        public long Sequence { get; }
        public string Path { get; }
        public TeacherOutcomeStatsDocument? Document { get; }
        public bool DeleteFile { get; }
    }

    private sealed class TeacherOutcomeAccumulator
    {
        private const int MaxSamples = 80;
        private readonly Queue<float> samples = new();

        public TeacherOutcomeAccumulator()
        {
        }

        public TeacherOutcomeAccumulator(IEnumerable<float> initialSamples)
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

        public float GetAverage()
            => samples.Count == 0 ? 0f : (float)samples.Average();

        public float GetPositiveRate()
            => samples.Count == 0 ? 0f : samples.Count(value => value >= 8f) / (float)samples.Count;

        public float GetModifier()
        {
            if (samples.Count < 4)
                return 0f;

            var average = GetAverage();
            var positiveRate = GetPositiveRate();
            return Math.Clamp(average * 0.22f + (positiveRate - 0.5f) * 10f, -8f, 8f);
        }

        public BattlefieldCommandEffectivenessSnapshot ToCommandSnapshot(BattlefieldCommandKind kind)
        {
            if (samples.Count == 0)
            {
                return new BattlefieldCommandEffectivenessSnapshot(
                    kind,
                    0,
                    0f,
                    0f,
                    0f,
                    "AI\u8001\u5e08\u6837\u672c\u4e0d\u8db3");
            }

            var average = GetAverage();
            var positiveRate = GetPositiveRate();
            var modifier = GetModifier();
            var summary = $"AI\u8001\u5e08 {kind}: \u6837\u672c {samples.Count}, \u5747\u503c {average:+0.0;-0.0;0}, \u8c03\u6574 {modifier:+0.0;-0.0;0}";
            return new BattlefieldCommandEffectivenessSnapshot(kind, samples.Count, average, positiveRate, modifier, summary);
        }

        public BattlefieldAiTeacherTargetResolutionSnapshot ToTargetSnapshot(
            StrategicTargetResolutionKind kind,
            string targetKey,
            string targetName)
        {
            if (samples.Count == 0)
            {
                return new BattlefieldAiTeacherTargetResolutionSnapshot(
                    kind,
                    targetKey,
                    targetName,
                    0,
                    0f,
                    0f,
                    0f,
                    "AI\u8001\u5e08\u843d\u70b9\u6837\u672c\u4e0d\u8db3");
            }

            var average = GetAverage();
            var positiveRate = GetPositiveRate();
            var modifier = GetModifier();
            var summary = $"AI\u8001\u5e08 {kind}: {targetName} \u6837\u672c {samples.Count}, \u8c03\u6574 {modifier:+0.0;-0.0;0}";
            return new BattlefieldAiTeacherTargetResolutionSnapshot(kind, targetKey, targetName, samples.Count, average, positiveRate, modifier, summary);
        }

        public static TeacherOutcomeAccumulator Combine(IEnumerable<TeacherOutcomeAccumulator> accumulators)
        {
            var combined = new TeacherOutcomeAccumulator();
            foreach (var accumulator in accumulators)
            {
                foreach (var sample in accumulator.samples)
                    combined.Add(sample);
            }

            return combined;
        }
    }

    private readonly record struct TeacherCommandKey(FrontlineMapType MapType, BattlefieldCommandKind Kind);

    private readonly record struct TeacherTargetKey(
        FrontlineMapType MapType,
        StrategicTargetResolutionKind Kind,
        string TargetKey,
        string TargetName)
    {
        public static TeacherTargetKey Create(
            FrontlineMapType mapType,
            StrategicTargetResolutionKind kind,
            string targetKey,
            string targetName)
            => new(
                mapType,
                kind,
                Normalize(string.IsNullOrWhiteSpace(targetKey) ? targetName : targetKey),
                targetName ?? string.Empty);

        public static string Normalize(string text)
            => StrategicTargetResolutionLearningPolicy.Normalize(text);
    }

    private readonly record struct TargetGroupingKey(
        StrategicTargetResolutionKind Kind,
        string TargetKey,
        string TargetName);

    private readonly record struct TargetResolutionOutcome(
        bool HasValue,
        string TargetKey,
        string TargetName);

    private sealed class TeacherOutcomeStatsDocument
    {
        public List<TeacherOutcomeStatsEntry> Entries { get; set; } = new();
        public List<TeacherCommandOutcomeStatsEntry> CommandEntries { get; set; } = new();
        public List<TeacherTargetOutcomeStatsEntry> TargetEntries { get; set; } = new();
    }

    private sealed class TeacherOutcomeStatsEntry
    {
        public string Kind { get; set; } = string.Empty;
        public float[] Scores { get; set; } = Array.Empty<float>();
    }

    private sealed class TeacherCommandOutcomeStatsEntry
    {
        public string MapType { get; set; } = FrontlineMapType.Unknown.ToString();
        public string Kind { get; set; } = string.Empty;
        public float[] Scores { get; set; } = Array.Empty<float>();
    }

    private sealed class TeacherTargetOutcomeStatsEntry
    {
        public string MapType { get; set; } = FrontlineMapType.Unknown.ToString();
        public string Kind { get; set; } = StrategicTargetResolutionKind.None.ToString();
        public string TargetKey { get; set; } = string.Empty;
        public string TargetName { get; set; } = string.Empty;
        public float[] Scores { get; set; } = Array.Empty<float>();
    }
}
