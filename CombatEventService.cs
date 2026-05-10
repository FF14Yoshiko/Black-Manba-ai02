using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using ActionSheet = Lumina.Excel.Sheets.Action;

namespace ai02;

public sealed class CombatEventService : IDisposable
{
    private const long EventTtlMs = 30000;
    private const long DuplicateWindowMs = 1500;
    private const long PruneIntervalMs = 2000;
    private const int MaxRecentEventCount = 384;
    private const uint InvalidEntityId = 0xE0000000;

    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly CombatActionEffectHook actionEffectHook;
    private readonly CombatStartCastHook startCastHook;
    private readonly List<CombatActionEvent> events = new();
    private readonly Dictionary<string, long> lastSeenByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> actionNameCache = new();
    private long lastPruneTicks = -1;
    private bool captureEnabled;
    private bool disposed;

    public CombatEventService(
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        IObjectTable objectTable,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;
        actionEffectHook = new CombatActionEffectHook(sigScanner, gameInteropProvider, log);
        startCastHook = new CombatStartCastHook(sigScanner, gameInteropProvider, log);
        actionEffectHook.OnRaw += HandleActionEffect;
        startCastHook.OnRaw += HandleStartCast;
    }

    public CombatActionEvent[] GetRecentEvents(long now, long maxAgeMs = EventTtlMs)
    {
        if (disposed)
            return Array.Empty<CombatActionEvent>();

        Prune(now, force: true);
        if (events.Count == 0)
            return Array.Empty<CombatActionEvent>();

        var recent = new List<CombatActionEvent>(Math.Min(events.Count, MaxRecentEventCount));
        for (var i = events.Count - 1; i >= 0 && recent.Count < MaxRecentEventCount; i--)
        {
            var item = events[i];
            if (now - item.ObservedAtTicks > maxAgeMs)
                break;

            recent.Add(item);
        }

        return recent.ToArray();
    }

    public void SetCaptureEnabled(bool enabled)
    {
        if (disposed || captureEnabled == enabled)
            return;

        captureEnabled = enabled;
        actionEffectHook.SetEnabled(enabled);
        startCastHook.SetEnabled(enabled);
        if (!enabled)
        {
            events.Clear();
            lastSeenByKey.Clear();
            lastPruneTicks = -1;
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        actionEffectHook.OnRaw -= HandleActionEffect;
        startCastHook.OnRaw -= HandleStartCast;
        actionEffectHook.Dispose();
        startCastHook.Dispose();
        events.Clear();
        lastSeenByKey.Clear();
        actionNameCache.Clear();
    }

    private unsafe void HandleActionEffect(
        uint sourceEntityId,
        IntPtr sourceChara,
        IntPtr pos,
        IntPtr effectHeader,
        IntPtr effectArray,
        IntPtr effectTrail)
    {
        if (disposed || !captureEnabled || effectHeader == IntPtr.Zero || effectArray == IntPtr.Zero || effectTrail == IntPtr.Zero)
            return;

        try
        {
            var now = Environment.TickCount64;
            var header = Marshal.PtrToStructure<CombatActionEffectHeader>(effectHeader);
            var sourceObject = ResolveObject(sourceEntityId, sourceChara, 0);
            var sourceGameObjectId = sourceObject?.GameObjectId ?? 0UL;
            var sourceName = ResolveObjectName(sourceObject, sourceEntityId);
            var sourceClassJobId = ResolveClassJobId(sourceObject);
            var sourceJobName = ResolveJobName(sourceObject);
            var sourcePosition = pos != IntPtr.Zero ? Marshal.PtrToStructure<Vector3>(pos) : sourceObject?.Position ?? Vector3.Zero;
            var targetCount = Math.Min(header.EffectCount, (byte)32);
            var rawTargets = (CombatTargetsEntry*)effectTrail;
            var rawEffects = (CombatEffectEntry*)effectArray;
            var targets = new List<CombatActionTargetSnapshot>(targetCount);

            for (var i = 0; i < targetCount; i++)
            {
                var targetObjectId = rawTargets->Entry[i];
                if (targetObjectId == 0)
                    continue;

                var targetObject = ResolveObject(0, IntPtr.Zero, targetObjectId);
                var targetEntityId = targetObject?.EntityId ?? TryConvertObjectIdToEntityId(targetObjectId);
                var targetName = ResolveObjectName(targetObject, targetEntityId);
                var effects = new List<CombatActionEffectItem>(8);
                for (var j = 0; j < 8; j++)
                {
                    var effect = rawEffects[i * 8 + j];
                    if (effect.Type == 0)
                        continue;

                    effects.Add(new CombatActionEffectItem(
                        effect.Type,
                        effect.Param0,
                        effect.Param1,
                        effect.Param2,
                        effect.Param3,
                        effect.Param4,
                        effect.Param5));
                }

                targets.Add(new CombatActionTargetSnapshot(
                    targetObjectId,
                    targetEntityId,
                    targetName,
                    effects.ToArray()));
            }

            var key = $"ae:{sourceEntityId}:{header.ActionId}:{header.GlobalSequence}:{header.ActionAnimationId}:{targetCount}";
            if (!ShouldAccept(key, now))
                return;

            var firstTarget = targets.FirstOrDefault();
            events.Add(new CombatActionEvent(
                now,
                CombatEventKind.ActionEffect,
                sourceEntityId,
                sourceGameObjectId,
                sourceName,
                sourceClassJobId,
                sourceJobName,
                header.ActionId,
                ResolveActionName(header.ActionId),
                header.GlobalSequence,
                header.AnimationLockTime,
                header.ActionAnimationId,
                header.Variation,
                header.EffectDisplayType,
                0f,
                false,
                sourcePosition,
                firstTarget.TargetObjectId,
                firstTarget.TargetEntityId,
                firstTarget.TargetName,
                targets.ToArray(),
                "战斗事件/技能生效",
                $"技能#{header.ActionId} 动画#{header.ActionAnimationId} 目标{targets.Count}个"));
            TrimRecentEvents();
            Prune(now, force: false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] 解析 ActionEffect 失败");
        }
    }

    private void HandleStartCast(uint sourceEntityId, IntPtr ptr)
    {
        if (disposed || !captureEnabled || ptr == IntPtr.Zero)
            return;

        try
        {
            var now = Environment.TickCount64;
            var cast = Marshal.PtrToStructure<CombatActorCast>(ptr);
            var sourceObject = ResolveObject(sourceEntityId, IntPtr.Zero, 0);
            var sourceGameObjectId = sourceObject?.GameObjectId ?? 0UL;
            var sourceName = ResolveObjectName(sourceObject, sourceEntityId);
            var sourceClassJobId = ResolveClassJobId(sourceObject);
            var sourceJobName = ResolveJobName(sourceObject);
            var targetObject = ResolveObject(cast.TargetId, IntPtr.Zero, 0);
            var targetObjectId = targetObject?.GameObjectId ?? 0UL;
            var targetName = ResolveObjectName(targetObject, cast.TargetId);
            var actionId = cast.RealActionId != 0 ? cast.RealActionId : cast.ActionId;
            var key = $"sc:{sourceEntityId}:{actionId}:{cast.TargetId}:{MathF.Round(cast.CastTime, 2)}";
            if (!ShouldAccept(key, now))
                return;

            events.Add(new CombatActionEvent(
                now,
                CombatEventKind.StartCast,
                sourceEntityId,
                sourceGameObjectId,
                sourceName,
                sourceClassJobId,
                sourceJobName,
                actionId,
                ResolveActionName(actionId),
                0,
                0f,
                0,
                cast.DisplayDelay,
                0,
                cast.CastTime,
                cast.CanInterrupt != 0,
                cast.Position,
                targetObjectId,
                cast.TargetId,
                targetName,
                Array.Empty<CombatActionTargetSnapshot>(),
                "战斗事件/开始读条",
                $"技能#{actionId} 读条{cast.CastTime:0.0}s 目标#{cast.TargetId}"));
            TrimRecentEvents();
            Prune(now, force: false);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] 解析 StartCast 失败");
        }
    }

    private bool ShouldAccept(string key, long now)
    {
        if (lastSeenByKey.TryGetValue(key, out var lastSeen) && now - lastSeen < DuplicateWindowMs)
            return false;

        lastSeenByKey[key] = now;
        return true;
    }

    private void Prune(long now, bool force)
    {
        if (!force && lastPruneTicks >= 0 && now - lastPruneTicks < PruneIntervalMs)
            return;

        lastPruneTicks = now;
        events.RemoveAll(item => now - item.ObservedAtTicks > EventTtlMs);
        TrimRecentEvents();
        foreach (var key in lastSeenByKey.Keys.ToArray())
        {
            if (now - lastSeenByKey[key] > EventTtlMs)
                lastSeenByKey.Remove(key);
        }
    }

    private void TrimRecentEvents()
    {
        if (events.Count <= MaxRecentEventCount)
            return;

        events.RemoveRange(0, events.Count - MaxRecentEventCount);
    }

    private IGameObject? ResolveObject(uint entityId, IntPtr address, ulong objectId)
    {
        if (entityId != 0 && entityId != InvalidEntityId)
        {
            var byEntityId = objectTable.SearchByEntityId(entityId);
            if (byEntityId != null && byEntityId.IsValid())
                return byEntityId;
        }

        foreach (var obj in objectTable)
        {
            if (obj == null || !obj.IsValid())
                continue;

            if (objectId != 0 && obj.GameObjectId == objectId)
                return obj;
            if (address != IntPtr.Zero && obj.Address == address)
                return obj;
        }

        if (objectId != 0 && objectId <= uint.MaxValue)
        {
            var byTargetEntityId = objectTable.SearchByEntityId((uint)objectId);
            if (byTargetEntityId != null && byTargetEntityId.IsValid())
                return byTargetEntityId;
        }

        return null;
    }

    private static uint TryConvertObjectIdToEntityId(ulong objectId)
        => objectId != 0 && objectId <= uint.MaxValue ? (uint)objectId : 0;

    private static uint ResolveClassJobId(IGameObject? obj)
        => obj is IPlayerCharacter player ? player.ClassJob.RowId : 0;

    private static string ResolveJobName(IGameObject? obj)
        => obj is IPlayerCharacter player ? player.ClassJob.Value.Name.ExtractText() : string.Empty;

    private static string ResolveObjectName(IGameObject? obj, uint fallbackEntityId)
    {
        if (obj != null && obj.IsValid() && !string.IsNullOrWhiteSpace(obj.Name.TextValue))
            return obj.Name.TextValue;

        return fallbackEntityId != 0 && fallbackEntityId != InvalidEntityId
            ? $"实体#{fallbackEntityId:X8}"
            : "未知目标";
    }

    private string ResolveActionName(uint actionId)
    {
        if (actionId == 0)
            return string.Empty;
        if (actionNameCache.TryGetValue(actionId, out var cached))
            return cached;

        try
        {
            var sheet = dataManager.GetExcelSheet<ActionSheet>();
            if (sheet.TryGetRow(actionId, out var action))
            {
                var name = action.Name.ExtractText();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    actionNameCache[actionId] = name;
                    return name;
                }
            }
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[CombatEvent] 读取技能名失败: {ActionId}", actionId);
        }

        var fallback = $"技能#{actionId}";
        actionNameCache[actionId] = fallback;
        return fallback;
    }
}
