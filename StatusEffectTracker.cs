using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class StatusEffectTracker : IDisposable
{
    private const long DefaultUpdateIntervalMs = 1000;
    private const long EventTtlMs = 90000;
    private const long SlowUpdateLogCooldownMs = 15000;
    private const double SlowUpdateWarningMs = 8d;
    private const int MaxRecentEventCount = 256;
    private const int MaxStatusPlayersPerFrame = 8;
    private const ulong InvalidGameObjectId = 0xE0000000;
    private static readonly HashSet<uint> FrontlineTerritoryTypes = new()
    {
        376,
        431,
        554,
        888,
        1273,
        1313
    };
    private static readonly ConcurrentDictionary<uint, string> JobNameCache = new();
    private static readonly ConcurrentDictionary<uint, string> StatusNameCache = new();

    private readonly Configuration configuration;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly object syncRoot = new();
    private readonly Dictionary<ulong, ObservedPlayerState> previousPlayers = new();
    private readonly List<PlayerStatusChangedEvent> recentStatusEvents = new();
    private readonly List<PlayerDeathStateChangedEvent> recentDeathEvents = new();
    private readonly List<PlayerTargetChangedEvent> recentTargetEvents = new();
    private long lastUpdateTicks;
    private long lastSlowUpdateLogTicks;
    private StatusScanSession? activeScan;
    private int updateInProgress;
    private bool disposed;

    public StatusEffectTracker(
        Configuration configuration,
        IObjectTable objectTable,
        IClientState clientState,
        IFramework framework,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.framework = framework;
        this.log = log;
        var intervalMs = configuration.Performance?.EffectiveStatusScanIntervalMs ?? DefaultUpdateIntervalMs;
        var worldIntervalMs = configuration.Performance?.EffectiveWorldRefreshIntervalMs ?? 750;
        var initialDelayMs = Math.Min(
            Math.Max(250, intervalMs - 250),
            Math.Max(1200, worldIntervalMs * 3 / 4));
        lastUpdateTicks = Environment.TickCount64 - intervalMs + initialDelayMs;

        framework.Update += OnFrameworkUpdate;
    }

    public event Action<PlayerStatusChangedEvent>? StatusChanged;

    public event Action<PlayerDeathStateChangedEvent>? DeathStateChanged;

    public event Action<PlayerTargetChangedEvent>? TargetChanged;

    public PlayerStatusChangedEvent[] GetRecentStatusChangedEvents(long now, long maxAgeMs = EventTtlMs)
    {
        lock (syncRoot)
        {
            Prune(now);
            if (recentStatusEvents.Count == 0)
                return Array.Empty<PlayerStatusChangedEvent>();

            var recent = new List<PlayerStatusChangedEvent>(Math.Min(recentStatusEvents.Count, 32));
            for (var i = recentStatusEvents.Count - 1; i >= 0; i--)
            {
                var item = recentStatusEvents[i];
                if (now - item.ObservedAtTicks > maxAgeMs)
                    break;

                recent.Add(item);
            }

            return recent.ToArray();
        }
    }

    public PlayerDeathStateChangedEvent[] GetRecentDeathStateChangedEvents(long now, long maxAgeMs = EventTtlMs)
    {
        lock (syncRoot)
        {
            Prune(now);
            if (recentDeathEvents.Count == 0)
                return Array.Empty<PlayerDeathStateChangedEvent>();

            var recent = new List<PlayerDeathStateChangedEvent>(Math.Min(recentDeathEvents.Count, 32));
            for (var i = recentDeathEvents.Count - 1; i >= 0; i--)
            {
                var item = recentDeathEvents[i];
                if (now - item.ObservedAtTicks > maxAgeMs)
                    break;

                recent.Add(item);
            }

            return recent.ToArray();
        }
    }

    public PlayerTargetChangedEvent[] GetRecentTargetChangedEvents(long now, long maxAgeMs = EventTtlMs)
    {
        lock (syncRoot)
        {
            Prune(now);
            if (recentTargetEvents.Count == 0)
                return Array.Empty<PlayerTargetChangedEvent>();

            var recent = new List<PlayerTargetChangedEvent>(Math.Min(recentTargetEvents.Count, 32));
            for (var i = recentTargetEvents.Count - 1; i >= 0; i--)
            {
                var item = recentTargetEvents[i];
                if (now - item.ObservedAtTicks > maxAgeMs)
                    break;

                recent.Add(item);
            }

            return recent.ToArray();
        }
    }

    public ObservedStatusSnapshot[] GetStatusesForPlayer(ulong gameObjectId)
    {
        lock (syncRoot)
        {
            return previousPlayers.TryGetValue(gameObjectId, out var state)
                ? state.StatusArray
                : Array.Empty<ObservedStatusSnapshot>();
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed)
            return;

        UpdateStatuses(Environment.TickCount64);
    }

    public void UpdateStatuses()
        => UpdateStatuses(Environment.TickCount64);

    private void UpdateStatuses(long now)
    {
        if (activeScan != null)
        {
            ProcessStatusScanBatch(now);
            return;
        }

        if (!TryBeginUpdate(now))
            return;

        try
        {
            if (!IsFrontlineTerritory(clientState.TerritoryType))
            {
                ClearObservedState(now);
                Interlocked.Exchange(ref updateInProgress, 0);
                return;
            }

            activeScan = BeginStatusScan(now);
            ProcessStatusScanBatch(now);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[StatusEffectTracker] UpdateStatuses failed");
            activeScan = null;
            Interlocked.Exchange(ref updateInProgress, 0);
        }
    }

    private StatusScanSession BeginStatusScan(long now)
    {
        var objectNames = new Dictionary<ulong, string>(96);
        var playerEntries = new List<TrackedPlayerTableEntry>(96);
        for (var index = 0; index < objectTable.Length; index++)
        {
            var obj = objectTable[index];
            if (obj == null || !obj.IsValid())
                continue;

            var name = obj.Name.TextValue;
            if (!string.IsNullOrWhiteSpace(name))
            {
                objectNames[obj.GameObjectId] = name;
                if (obj.EntityId != 0)
                    objectNames[obj.EntityId] = name;
            }

            if (obj is IPlayerCharacter player
                && player.IsValid()
                && player.GameObjectId != 0
                && player.GameObjectId != InvalidGameObjectId)
            {
                playerEntries.Add(new TrackedPlayerTableEntry(index, player.GameObjectId));
            }
        }

        return new StatusScanSession(now, Stopwatch.GetTimestamp(), objectNames, playerEntries.ToArray());
    }

    private void ProcessStatusScanBatch(long now)
    {
        var scan = activeScan;
        if (scan == null)
            return;

        var batchStartedTimestamp = Stopwatch.GetTimestamp();
        var statusEvents = new List<PlayerStatusChangedEvent>(16);
        var deathEvents = new List<PlayerDeathStateChangedEvent>(4);
        var targetEvents = new List<PlayerTargetChangedEvent>(8);
        var scannedThisFrame = 0;

        try
        {
            var batchStart = scan.NextIndex;
            var batchEnd = Math.Min(scan.PlayerEntries.Length, batchStart + MaxStatusPlayersPerFrame);
            if (batchStart >= batchEnd)
                return;
            scan.NextIndex = batchEnd;

            for (var i = batchStart; i < batchEnd; i++)
            {
                if (!TryGetTrackedPlayer(scan.PlayerEntries[i], out var player))
                    continue;

                ObservedPlayerState state;
                try
                {
                    state = BuildPlayerState(player!, scan.ObjectNames);
                }
                catch (Exception ex)
                {
                    log.Verbose(ex, "[StatusEffectTracker] 跳过瞬时失效玩家样本");
                    continue;
                }

                if (state.Player.GameObjectId == 0 || state.Player.GameObjectId == InvalidGameObjectId)
                    continue;

                scan.SeenPlayerIds.Add(state.Player.GameObjectId);
                scannedThisFrame++;
                scan.ScannedPlayerCount++;
                lock (syncRoot)
                {
                    if (previousPlayers.TryGetValue(state.Player.GameObjectId, out var previousState))
                    {
                        CollectStatusDiffs(previousState, state, now, statusEvents);
                        CollectDeathDiff(previousState, state, now, deathEvents);
                        CollectTargetDiff(previousState, state, PlayerTargetChangeKind.Target, now, targetEvents);
                        CollectCastTargetDiff(previousState, state, now, targetEvents);
                    }

                    previousPlayers[state.Player.GameObjectId] = state;
                }
            }

            scan.StatusEventCount += statusEvents.Count;
            scan.DeathEventCount += deathEvents.Count;
            scan.TargetEventCount += targetEvents.Count;
            lock (syncRoot)
            {
                AppendRecentEvents(statusEvents, deathEvents, targetEvents);
                Prune(now);
            }

            NotifyEvents(statusEvents, StatusChanged, "status");
            NotifyEvents(deathEvents, DeathStateChanged, "death");
            NotifyEvents(targetEvents, TargetChanged, "target");

            if (scan.NextIndex < scan.PlayerEntries.Length)
                return;

            activeScan = null;
            scan.WorkElapsedMs += Stopwatch.GetElapsedTime(batchStartedTimestamp).TotalMilliseconds;
            CompleteStatusScan(now, scan);
        }
        catch (Exception ex)
        {
            log.Error(ex, "[StatusEffectTracker] incremental status scan failed");
            activeScan = null;
            Interlocked.Exchange(ref updateInProgress, 0);
        }
        finally
        {
            if (activeScan != null)
                scan.WorkElapsedMs += Stopwatch.GetElapsedTime(batchStartedTimestamp).TotalMilliseconds;
        }
    }

    private void CompleteStatusScan(long now, StatusScanSession scan)
    {
        lock (syncRoot)
        {
            List<ulong>? stalePlayerIds = null;
            foreach (var pair in previousPlayers)
            {
                if (scan.CandidatePlayerIds.Contains(pair.Key))
                    continue;

                stalePlayerIds ??= new List<ulong>(4);
                stalePlayerIds.Add(pair.Key);
            }

            if (stalePlayerIds != null)
            {
                foreach (var key in stalePlayerIds)
                    previousPlayers.Remove(key);
            }

            Prune(now);
        }

        LogSlowUpdate(now, scan.WorkElapsedMs, scan.ScannedPlayerCount, scan.StatusEventCount, scan.DeathEventCount, scan.TargetEventCount);
        Interlocked.Exchange(ref updateInProgress, 0);
    }

    private bool TryGetTrackedPlayer(TrackedPlayerTableEntry entry, out IPlayerCharacter? player)
    {
        player = null;
        if (entry.ObjectTableIndex < 0 || entry.ObjectTableIndex >= objectTable.Length)
            return false;

        var obj = objectTable[entry.ObjectTableIndex];
        if (obj is not IPlayerCharacter candidate
            || !candidate.IsValid()
            || candidate.GameObjectId != entry.GameObjectId)
        {
            return false;
        }

        player = candidate;
        return true;
    }

    private bool TryBeginUpdate(long now)
    {
        var intervalMs = configuration.Performance?.EffectiveStatusScanIntervalMs ?? DefaultUpdateIntervalMs;
        var last = Volatile.Read(ref lastUpdateTicks);
        if (now - last < intervalMs)
            return false;

        if (Interlocked.CompareExchange(ref updateInProgress, 1, 0) != 0)
            return false;

        last = Volatile.Read(ref lastUpdateTicks);
        if (now - last < intervalMs)
        {
            Interlocked.Exchange(ref updateInProgress, 0);
            return false;
        }

        Volatile.Write(ref lastUpdateTicks, now);
        return true;
    }

    private void ClearObservedState(long now)
    {
        lock (syncRoot)
        {
            previousPlayers.Clear();
            Prune(now);
        }
    }

    private static bool IsFrontlineTerritory(uint territoryType)
        => FrontlineTerritoryTypes.Contains(territoryType);

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        activeScan = null;
        lock (syncRoot)
        {
            previousPlayers.Clear();
            recentStatusEvents.Clear();
            recentDeathEvents.Clear();
            recentTargetEvents.Clear();
        }
    }

    private Dictionary<ulong, ObservedPlayerState> CollectCurrentPlayers()
    {
        var objectNames = new Dictionary<ulong, string>(96);
        foreach (var obj in objectTable)
        {
            if (obj == null || !obj.IsValid())
                continue;

            var name = obj.Name.TextValue;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            objectNames[obj.GameObjectId] = name;
            if (obj.EntityId != 0)
                objectNames[obj.EntityId] = name;
        }

        var players = new Dictionary<ulong, ObservedPlayerState>(96);
        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player || !player.IsValid())
                continue;

            var snapshot = BuildPlayerState(player, objectNames);
            if (snapshot.Player.GameObjectId == 0 || snapshot.Player.GameObjectId == InvalidGameObjectId)
                continue;

            players[snapshot.Player.GameObjectId] = snapshot;
        }

        return players;
    }

    private ObservedPlayerState BuildPlayerState(
        IPlayerCharacter player,
        IReadOnlyDictionary<ulong, string> objectNames)
    {
        var statuses = new Dictionary<ObservedStatusKey, ObservedStatusSnapshot>();
        ObservedStatusSnapshot[] statusArray = Array.Empty<ObservedStatusSnapshot>();
        var castTargetObjectId = 0UL;
        var localGameObjectId = objectTable.LocalPlayer?.GameObjectId ?? 0;
        var isLocalPlayer = localGameObjectId != 0 && player.GameObjectId == localGameObjectId;
        if (player is IBattleChara battleChara)
        {
            if (isLocalPlayer)
                castTargetObjectId = SafeReadObjectId(() => battleChara.CastTargetObjectId);

            try
            {
                foreach (var status in battleChara.StatusList)
                {
                    if (status.StatusId == 0)
                        continue;

                    var snapshot = BuildStatusSnapshot(status, objectNames);
                    var key = new ObservedStatusKey(snapshot.StatusId, snapshot.Param, snapshot.SourceId);
                    statuses[key] = snapshot;
                }
            }
            catch (Exception ex)
            {
                log.Verbose(ex, "[StatusEffectTracker] 读取玩家状态列表失败，继续使用已采到的字段");
            }

            if (statuses.Count > 0)
                statusArray = statuses.Values.ToArray();
        }

        var jobName = ResolveJobName(player);
        var observedPlayer = new ObservedPlayerSnapshot(
            player.GameObjectId,
            player.EntityId,
            ResolveContentId(player),
            player.Name.TextValue,
            player.ClassJob.RowId,
            jobName,
            ResolveBattalion(player));

        var targetObjectId = SafeReadObjectId(() => player.TargetObjectId);
        return new ObservedPlayerState(
            observedPlayer,
            player.IsDead,
            targetObjectId,
            ResolveObjectName(targetObjectId, objectNames),
            castTargetObjectId,
            ResolveObjectName(castTargetObjectId, objectNames),
            statuses,
            statusArray);
    }

    private static ulong SafeReadObjectId(Func<ulong> getter)
    {
        try
        {
            return NormalizeObjectId(getter());
        }
        catch
        {
            return 0;
        }
    }

    private static ulong ResolveContentId(IPlayerCharacter player)
        => 0UL;

    private static byte ResolveBattalion(IPlayerCharacter player)
        => byte.MaxValue;

    private static string ResolveJobName(IPlayerCharacter player)
    {
        var classJobId = player.ClassJob.RowId;
        if (classJobId != 0 && JobNameCache.TryGetValue(classJobId, out var cached))
            return cached;

        try
        {
            var name = player.ClassJob.Value.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name) && classJobId != 0)
                JobNameCache[classJobId] = name;
            return name;
        }
        catch
        {
            return string.Empty;
        }
    }

    private ObservedStatusSnapshot BuildStatusSnapshot(
        IStatus status,
        IReadOnlyDictionary<ulong, string> objectNames)
    {
        var statusName = ResolveStatusName(status);
        var sourceName = ResolveSourceName(status.SourceId, objectNames);
        return new ObservedStatusSnapshot(
            status.StatusId,
            statusName,
            status.Param,
            Math.Max(0f, status.RemainingTime),
            status.SourceId,
            sourceName);
    }

    private static string ResolveStatusName(IStatus status)
    {
        if (StatusNameCache.TryGetValue(status.StatusId, out var cached))
            return cached;

        try
        {
            var name = status.GameData.Value.Name.ExtractText();
            var resolved = string.IsNullOrWhiteSpace(name) ? $"status#{status.StatusId}" : name;
            StatusNameCache[status.StatusId] = resolved;
            return resolved;
        }
        catch
        {
            var fallback = $"status#{status.StatusId}";
            StatusNameCache[status.StatusId] = fallback;
            return fallback;
        }
    }

    private static string ResolveSourceName(uint sourceId, IReadOnlyDictionary<ulong, string> objectNames)
    {
        if (sourceId == 0)
            return string.Empty;

        return objectNames.TryGetValue(sourceId, out var sourceName) && !string.IsNullOrWhiteSpace(sourceName)
            ? sourceName
            : $"source#{sourceId:X8}";
    }

    private static ulong NormalizeObjectId(ulong objectId)
        => objectId == InvalidGameObjectId || objectId == ulong.MaxValue ? 0UL : objectId;

    private static string ResolveObjectName(ulong objectId, IReadOnlyDictionary<ulong, string> objectNames)
    {
        if (objectId == 0)
            return string.Empty;

        if (objectNames.TryGetValue(objectId, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        return $"瀵硅薄#{objectId:X}";
    }

    private void CollectDiffEvents(
        IReadOnlyDictionary<ulong, ObservedPlayerState> previous,
        IReadOnlyDictionary<ulong, ObservedPlayerState> current,
        long now,
        List<PlayerStatusChangedEvent> statusEvents,
        List<PlayerDeathStateChangedEvent> deathEvents,
        List<PlayerTargetChangedEvent> targetEvents)
    {
        foreach (var pair in current)
        {
            if (!previous.TryGetValue(pair.Key, out var previousState))
                continue;

            var currentState = pair.Value;
            CollectStatusDiffs(previousState, currentState, now, statusEvents);
            CollectDeathDiff(previousState, currentState, now, deathEvents);
            CollectTargetDiff(previousState, currentState, PlayerTargetChangeKind.Target, now, targetEvents);
            CollectCastTargetDiff(previousState, currentState, now, targetEvents);
        }
    }

    private static void CollectStatusDiffs(
        ObservedPlayerState previousState,
        ObservedPlayerState currentState,
        long now,
        List<PlayerStatusChangedEvent> statusEvents)
    {
        foreach (var pair in currentState.Statuses)
        {
            if (previousState.Statuses.ContainsKey(pair.Key))
                continue;

            statusEvents.Add(new PlayerStatusChangedEvent(
                now,
                PlayerStatusChangeKind.Gained,
                currentState.Player,
                pair.Value,
                "status list diff"));
        }

        foreach (var pair in previousState.Statuses)
        {
            if (currentState.Statuses.ContainsKey(pair.Key))
                continue;

            statusEvents.Add(new PlayerStatusChangedEvent(
                now,
                PlayerStatusChangeKind.Lost,
                currentState.Player,
                pair.Value,
                "status list diff"));
        }
    }

    private static void CollectDeathDiff(
        ObservedPlayerState previousState,
        ObservedPlayerState currentState,
        long now,
        List<PlayerDeathStateChangedEvent> deathEvents)
    {
        if (previousState.IsDead == currentState.IsDead)
            return;

        deathEvents.Add(new PlayerDeathStateChangedEvent(
            now,
            currentState.Player,
            currentState.IsDead,
            "death state diff"));
    }

    private static void CollectTargetDiff(
        ObservedPlayerState previousState,
        ObservedPlayerState currentState,
        PlayerTargetChangeKind kind,
        long now,
        List<PlayerTargetChangedEvent> targetEvents)
    {
        if (previousState.TargetObjectId == currentState.TargetObjectId)
            return;

        targetEvents.Add(new PlayerTargetChangedEvent(
            now,
            kind,
            currentState.Player,
            previousState.TargetObjectId,
            previousState.TargetName,
            currentState.TargetObjectId,
            currentState.TargetName,
            "target diff"));
    }

    private static void CollectCastTargetDiff(
        ObservedPlayerState previousState,
        ObservedPlayerState currentState,
        long now,
        List<PlayerTargetChangedEvent> targetEvents)
    {
        if (previousState.CastTargetObjectId == currentState.CastTargetObjectId)
            return;

        targetEvents.Add(new PlayerTargetChangedEvent(
            now,
            PlayerTargetChangeKind.CastTarget,
            currentState.Player,
            previousState.CastTargetObjectId,
            previousState.CastTargetName,
            currentState.CastTargetObjectId,
            currentState.CastTargetName,
            "cast target diff"));
    }

    private void AppendRecentEvents(
        IReadOnlyCollection<PlayerStatusChangedEvent> statusEvents,
        IReadOnlyCollection<PlayerDeathStateChangedEvent> deathEvents,
        IReadOnlyCollection<PlayerTargetChangedEvent> targetEvents)
    {
        if (statusEvents.Count > 0)
        {
            recentStatusEvents.AddRange(statusEvents);
            TrimRecentEvents(recentStatusEvents);
        }

        if (deathEvents.Count > 0)
        {
            recentDeathEvents.AddRange(deathEvents);
            TrimRecentEvents(recentDeathEvents);
        }

        if (targetEvents.Count > 0)
        {
            recentTargetEvents.AddRange(targetEvents);
            TrimRecentEvents(recentTargetEvents);
        }
    }

    private void Prune(long now)
    {
        recentStatusEvents.RemoveAll(item => now - item.ObservedAtTicks > EventTtlMs);
        recentDeathEvents.RemoveAll(item => now - item.ObservedAtTicks > EventTtlMs);
        recentTargetEvents.RemoveAll(item => now - item.ObservedAtTicks > EventTtlMs);
    }

    private static void TrimRecentEvents<T>(List<T> events)
    {
        if (events.Count <= MaxRecentEventCount)
            return;

        events.RemoveRange(0, events.Count - MaxRecentEventCount);
    }

    private void NotifyHandlers<T>(Action<T>? handlers, T evt, string category)
    {
        if (handlers == null)
            return;

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(evt);
            }
            catch (Exception ex)
            {
                log.Error(ex, "[StatusEffectTracker] Subscriber failed while handling {Category} event", category);
            }
        }
    }

    private void NotifyEvents<T>(IReadOnlyList<T> events, Action<T>? handlers, string category)
    {
        if (handlers == null || events.Count == 0)
            return;

        foreach (var evt in events)
            NotifyHandlers(handlers, evt, category);
    }

    private void LogSlowUpdate(
        long now,
        double elapsedMs,
        int scannedPlayerCount,
        int statusEventCount,
        int deathEventCount,
        int targetEventCount)
    {
        if (elapsedMs < SlowUpdateWarningMs || now - lastSlowUpdateLogTicks < SlowUpdateLogCooldownMs)
            return;

        lastSlowUpdateLogTicks = now;
        log.Debug(
            "[StatusEffectTracker] Slow status scan: {ElapsedMs:F1}ms, players={PlayerCount}, events={StatusEvents}/{DeathEvents}/{TargetEvents}",
            elapsedMs,
            scannedPlayerCount,
            statusEventCount,
            deathEventCount,
            targetEventCount);
    }

    private readonly record struct ObservedStatusKey(
        uint StatusId,
        ushort Param,
        uint SourceId);

    private sealed record ObservedPlayerState(
        ObservedPlayerSnapshot Player,
        bool IsDead,
        ulong TargetObjectId,
        string TargetName,
        ulong CastTargetObjectId,
        string CastTargetName,
        Dictionary<ObservedStatusKey, ObservedStatusSnapshot> Statuses,
        ObservedStatusSnapshot[] StatusArray);

    private sealed class StatusScanSession
    {
        public StatusScanSession(
            long startedAtTicks,
            long startedTimestamp,
            IReadOnlyDictionary<ulong, string> objectNames,
            TrackedPlayerTableEntry[] playerEntries)
        {
            StartedAtTicks = startedAtTicks;
            StartedTimestamp = startedTimestamp;
            ObjectNames = objectNames;
            PlayerEntries = playerEntries;
            CandidatePlayerIds = new HashSet<ulong>(playerEntries.Length);
            foreach (var entry in playerEntries)
                CandidatePlayerIds.Add(entry.GameObjectId);
        }

        public long StartedAtTicks { get; }
        public long StartedTimestamp { get; }
        public IReadOnlyDictionary<ulong, string> ObjectNames { get; }
        public TrackedPlayerTableEntry[] PlayerEntries { get; }
        public HashSet<ulong> CandidatePlayerIds { get; }
        public HashSet<ulong> SeenPlayerIds { get; } = new();
        public int NextIndex { get; set; }
        public int ScannedPlayerCount { get; set; }
        public int StatusEventCount { get; set; }
        public int DeathEventCount { get; set; }
        public int TargetEventCount { get; set; }
        public double WorkElapsedMs { get; set; }
    }

    private readonly record struct TrackedPlayerTableEntry(int ObjectTableIndex, ulong GameObjectId);
}
