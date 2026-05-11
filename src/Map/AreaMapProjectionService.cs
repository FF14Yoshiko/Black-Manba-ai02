using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MapSheet = Lumina.Excel.Sheets.Map;

namespace ai02;

public sealed class AreaMapProjectionService : IDisposable
{
    private const uint LocalPlayerMapIconId = 60443;
    private const uint FrontlineDeadIconId = 60909;
    private const uint MaelstromMapIconId = 60359;
    private const uint TwinAdderMapIconId = 60360;
    private const uint ImmortalFlamesMapIconId = 60361;
    private static readonly HashSet<uint> FriendlyMapIconIds = new() { 60421, 60403, 60424 };
    private const int AreaMapScaleFloatIndex = 245;
    private const int PreferredAreaMapComponentNodeIndex = 3;
    private const int LocalPlayerMarkerStartNodeIndex = 6;
    private const int MarkerIconImageNodeIndex = 4;
    private const int DefaultSampleIntervalMs = 500;
    private const int RealtimeSampleIntervalMs = 180;
    private const long SlowSampleLogCooldownMs = 15000;
    private const double SlowSampleWarningMs = 6d;

    private readonly Configuration configuration;
    private readonly IGameGui gameGui;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;

    private AreaMapProjectionSnapshot? latestSnapshot;
    private uint cachedMapTerritoryType;
    private uint cachedMapId;
    private MapSheet? cachedMap;
    private long lastSampleTicks;
    private long adaptiveSampleBackoffUntilTicks;
    private long realtimeAdaptiveSampleBackoffUntilTicks;
    private long lastDebugTicks;
    private long lastSlowSampleLogTicks;
    private bool disposed;

    public AreaMapProjectionService(
        Configuration configuration,
        IGameGui gameGui,
        IClientState clientState,
        IObjectTable objectTable,
        IDataManager dataManager,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.gameGui = gameGui;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
        this.log = log;
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        latestSnapshot = null;
        cachedMap = null;
        cachedMapTerritoryType = 0;
        cachedMapId = 0;
        lastSampleTicks = 0;
        adaptiveSampleBackoffUntilTicks = 0;
        realtimeAdaptiveSampleBackoffUntilTicks = 0;
        lastDebugTicks = 0;
        lastSlowSampleLogTicks = 0;
    }

    public bool TryGetSnapshot(out AreaMapProjectionSnapshot snapshot)
        => TryGetSnapshotCore(preferRealtime: false, out snapshot);

    public bool TryGetRealtimeSnapshot(out AreaMapProjectionSnapshot snapshot)
        => TryGetSnapshotCore(preferRealtime: true, out snapshot);

    private bool TryGetSnapshotCore(bool preferRealtime, out AreaMapProjectionSnapshot snapshot)
    {
        if (disposed)
        {
            snapshot = default;
            return false;
        }

        try
        {
            var now = Environment.TickCount64;
            var intervalMs = ResolveSampleIntervalMs(preferRealtime);
            var adaptiveBackoffUntilTicks = preferRealtime ? realtimeAdaptiveSampleBackoffUntilTicks : adaptiveSampleBackoffUntilTicks;
            if (now - lastSampleTicks < intervalMs || now < adaptiveBackoffUntilTicks)
            {
                if (latestSnapshot.HasValue)
                {
                    snapshot = latestSnapshot.Value;
                    return true;
                }

                snapshot = default;
                return false;
            }

            var startedTimestamp = Stopwatch.GetTimestamp();
            latestSnapshot = TrySample(preferRealtime, out var sampledSnapshot) ? sampledSnapshot : null;
            lastSampleTicks = now;
            var elapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
            ApplyAdaptiveSamplingBackoff(now, elapsedMs, latestSnapshot?.MapVisionPoints.Length ?? 0, preferRealtime);
            LogSlowSample(now, elapsedMs, latestSnapshot?.MapVisionPoints.Length ?? 0);
        }
        catch (Exception ex)
        {
            latestSnapshot = null;
            LogDebug($"AreaMap projection sample failed: {ex.Message}");
        }

        if (latestSnapshot.HasValue)
        {
            snapshot = latestSnapshot.Value;
            return true;
        }

        snapshot = default;
        return false;
    }

    private int ResolveSampleIntervalMs(bool preferRealtime)
    {
        var configuredIntervalMs = configuration.Performance?.EffectiveAreaMapSampleIntervalMs ?? DefaultSampleIntervalMs;
        if (!preferRealtime)
            return configuredIntervalMs;

        return Math.Clamp(Math.Min(configuredIntervalMs, RealtimeSampleIntervalMs), 100, RealtimeSampleIntervalMs);
    }

    private unsafe bool TrySample(bool preferRealtime, out AreaMapProjectionSnapshot snapshot)
    {
        snapshot = default;

        var localPlayer = objectTable.LocalPlayer;
        if (localPlayer == null)
            return false;

        var localBattalion = TryGetLocalBattalion(localPlayer);
        var addon = GetAreaMapAddon();
        if (addon == null || !addon->IsVisible || addon->RootNode == null)
            return false;

        if (addon->UldManager.NodeList == null || addon->UldManager.NodeListCount <= PreferredAreaMapComponentNodeIndex)
        {
            LogDebug("AreaMap projection skipped: AreaMap nodes are not ready");
            return false;
        }

        var map = GetCurrentMap();
        if (map == null)
        {
            LogDebug("AreaMap projection skipped: current map data is not ready");
            return false;
        }

        var viewportPos = ImGui.GetMainViewport().Pos;
        var addonPos = new Vector2(addon->X, addon->Y);
        var uiScale = MathF.Max(0.01f, addon->Scale);
        var mapZoom = MathF.Max(0.01f, ((float*)addon)[AreaMapScaleFloatIndex]);
        var source = "AreaMap component";

        if (!TryGetMapComponentNode(addon, out var mapComponentNode))
        {
            LogDebug("AreaMap projection skipped: map component not found");
            return false;
        }

        var baseNode = mapComponentNode->AtkResNode;
        var basePos = new Vector2(baseNode.X, baseNode.Y);
        var baseSize = new Vector2(MathF.Max(1f, baseNode.Width), MathF.Max(1f, baseNode.Height));
        var clipMin = viewportPos + addonPos + basePos * uiScale;
        var clipMax = viewportPos + addonPos + (basePos + baseSize) * uiScale;
        if (clipMax.X <= clipMin.X || clipMax.Y <= clipMin.Y)
        {
            LogDebug($"AreaMap projection skipped: invalid clip min={clipMin}, max={clipMax}");
            return false;
        }

        var localAnchor = clipMin + (clipMax - clipMin) * 0.5f;
        var hasReliableAnchor = TryFindLocalPlayerMapAnchor(mapComponentNode, out var localNodePos);
        if (hasReliableAnchor)
        {
            localAnchor = viewportPos + addonPos + (basePos + localNodePos) * uiScale;
            source += $"; anchor={LocalPlayerMapIconId}";
        }
        else
        {
            source += "; center fallback";
            LogDebug($"AreaMap projection could not find local player icon {LocalPlayerMapIconId}; using map center fallback");
        }

        var baseSnapshot = new AreaMapProjectionSnapshot(
            clientState.TerritoryType,
            localPlayer.Position,
            localAnchor,
            clipMin,
            clipMax,
            mapZoom * uiScale,
            0f,
            map.Value.SizeFactor / 100f,
            map.Value.OffsetX,
            map.Value.OffsetY,
            hasReliableAnchor,
            source,
            Array.Empty<BattlefieldMapVisionPointSnapshot>());

        var mapVisionPoints = hasReliableAnchor
            ? CollectMapVisionPoints(mapComponentNode, baseSnapshot, addonPos, basePos, uiScale, localBattalion, preferRealtime)
            : Array.Empty<BattlefieldMapVisionPointSnapshot>();

        snapshot = baseSnapshot with
        {
            Source = mapVisionPoints.Length > 0 ? $"{source}; vision={mapVisionPoints.Length}" : source,
            MapVisionPoints = mapVisionPoints
        };

        return true;
    }

    private unsafe bool TryGetMapComponentNode(AtkUnitBase* addon, out AtkComponentNode* mapComponentNode)
    {
        mapComponentNode = null;
        if (addon == null || addon->UldManager.NodeList == null || addon->UldManager.NodeListCount <= 0)
            return false;

        if (IsUsableComponentNode(addon->UldManager.NodeList[PreferredAreaMapComponentNodeIndex], out var preferredComponent))
        {
            mapComponentNode = preferredComponent;
            return true;
        }

        AtkComponentNode* largestComponent = null;
        var largestArea = 0f;
        var count = Math.Min((int)addon->UldManager.NodeListCount, 96);
        for (var i = 0; i < count; i++)
        {
            var node = addon->UldManager.NodeList[i];
            if (!IsUsableComponentNode(node, out var componentNode))
                continue;

            if (TryFindLocalPlayerMapAnchor(componentNode, out _))
            {
                mapComponentNode = componentNode;
                return true;
            }

            var area = componentNode->AtkResNode.Width * componentNode->AtkResNode.Height;
            if (area > largestArea)
            {
                largestArea = area;
                largestComponent = componentNode;
            }
        }

        if (largestComponent == null)
            return false;

        mapComponentNode = largestComponent;
        return true;
    }

    private static unsafe bool IsUsableComponentNode(AtkResNode* node, out AtkComponentNode* componentNode)
    {
        componentNode = null;
        if (node == null || !IsComponentNodeType(node->Type))
            return false;

        var candidate = (AtkComponentNode*)node;
        if (candidate->Component == null)
            return false;

        componentNode = candidate;
        return true;
    }

    private unsafe bool TryFindLocalPlayerMapAnchor(AtkComponentNode* mapComponentNode, out Vector2 localNodePos)
    {
        localNodePos = default;
        if (mapComponentNode == null || mapComponentNode->Component == null)
            return false;

        var manager = &mapComponentNode->Component->UldManager;
        if (manager->NodeList != null && manager->NodeListCount >= 8)
        {
            for (var i = LocalPlayerMarkerStartNodeIndex; i < manager->NodeListCount - 1; i++)
            {
                var node = manager->NodeList[i];
                if (!IsUsableComponentNode(node, out var componentNode))
                    continue;

                var childManager = &componentNode->Component->UldManager;
                if (childManager->NodeList == null || childManager->NodeListCount <= MarkerIconImageNodeIndex)
                    continue;

                var imageNode = (AtkImageNode*)childManager->NodeList[MarkerIconImageNodeIndex];
                if (GetImageIconId(imageNode) == LocalPlayerMapIconId)
                {
                    var resNode = componentNode->AtkResNode;
                    localNodePos = new Vector2(resNode.X + resNode.OriginX, resNode.Y + resNode.OriginY);
                    return true;
                }
            }
        }

        return TryFindLocalPlayerMapAnchorRecursive((AtkResNode*)mapComponentNode, Vector2.Zero, new HashSet<nint>(), 0, out localNodePos);
    }

    private unsafe bool TryFindLocalPlayerMapAnchorRecursive(
        AtkResNode* node,
        Vector2 accumulatedPos,
        HashSet<nint> visitedNodes,
        int depth,
        out Vector2 localNodePos)
    {
        localNodePos = default;
        if (node == null || depth > 10 || !visitedNodes.Add((nint)node))
            return false;

        var nodePos = accumulatedPos;
        if (depth > 0)
            nodePos += new Vector2(node->X + node->OriginX, node->Y + node->OriginY);

        if (IsComponentNodeType(node->Type))
        {
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component != null)
            {
                var manager = &componentNode->Component->UldManager;
                if (manager->NodeList != null)
                {
                    if (manager->NodeListCount > MarkerIconImageNodeIndex)
                    {
                        var possibleImageNode = manager->NodeList[MarkerIconImageNodeIndex];
                        if (possibleImageNode != null
                            && possibleImageNode->Type == NodeType.Image
                            && GetImageIconId((AtkImageNode*)possibleImageNode) == LocalPlayerMapIconId)
                        {
                            localNodePos = nodePos;
                            return true;
                        }
                    }

                    var count = Math.Min((int)manager->NodeListCount, 128);
                    for (var i = 0; i < count; i++)
                    {
                        var child = manager->NodeList[i];
                        if (child != null && TryFindLocalPlayerMapAnchorRecursive(child, nodePos, visitedNodes, depth + 1, out localNodePos))
                            return true;
                    }
                }
            }
        }

        var childNode = node->ChildNode;
        var guard = 0;
        while (childNode != null && guard++ < 256)
        {
            if (TryFindLocalPlayerMapAnchorRecursive(childNode, nodePos, visitedNodes, depth + 1, out localNodePos))
                return true;

            childNode = childNode->NextSiblingNode;
        }

        return false;
    }

    private static bool IsComponentNodeType(NodeType nodeType)
        => nodeType == NodeType.Component || (int)nodeType >= 1000;

    private unsafe BattlefieldMapVisionPointSnapshot[] CollectMapVisionPoints(
        AtkComponentNode* mapComponentNode,
        AreaMapProjectionSnapshot snapshot,
        Vector2 addonPos,
        Vector2 basePos,
        float uiScale,
        byte localBattalion,
        bool preferRealtime)
    {
        if (mapComponentNode == null || mapComponentNode->Component == null || snapshot.HasReliableLocalPlayerAnchor == false)
            return Array.Empty<BattlefieldMapVisionPointSnapshot>();

        if (preferRealtime)
        {
            var directPoints = CollectMapVisionPointsDirect(mapComponentNode, snapshot, addonPos, basePos, uiScale, localBattalion);
            if (directPoints.Length > 0)
                return directPoints;
        }

        var points = new List<BattlefieldMapVisionPointSnapshot>(32);
        var visitedNodes = new HashSet<nint>();
        var pointKeys = new HashSet<string>(StringComparer.Ordinal);
        CollectMapVisionPointsRecursive(
            (AtkResNode*)mapComponentNode,
            Vector2.Zero,
            snapshot,
            addonPos,
            basePos,
            uiScale,
            localBattalion,
            points,
            visitedNodes,
            pointKeys,
            0);

        return points.ToArray();
    }

    private unsafe BattlefieldMapVisionPointSnapshot[] CollectMapVisionPointsDirect(
        AtkComponentNode* mapComponentNode,
        AreaMapProjectionSnapshot snapshot,
        Vector2 addonPos,
        Vector2 basePos,
        float uiScale,
        byte localBattalion)
    {
        if (mapComponentNode == null || mapComponentNode->Component == null)
            return Array.Empty<BattlefieldMapVisionPointSnapshot>();

        var manager = &mapComponentNode->Component->UldManager;
        if (manager->NodeList == null)
            return Array.Empty<BattlefieldMapVisionPointSnapshot>();

        var points = new List<BattlefieldMapVisionPointSnapshot>(32);
        var pointKeys = new HashSet<string>(StringComparer.Ordinal);
        var count = Math.Min((int)manager->NodeListCount, 128);
        for (var i = 0; i < count && points.Count < 128; i++)
        {
            var node = manager->NodeList[i];
            if (node == null)
                continue;

            var nodePos = new Vector2(node->X + node->OriginX, node->Y + node->OriginY);
            if (IsComponentNodeType(node->Type))
            {
                var componentNode = (AtkComponentNode*)node;
                if (TryCreateMapVisionPoint(componentNode, nodePos, snapshot, addonPos, basePos, uiScale, localBattalion, out var componentPoint))
                {
                    var key = $"{componentPoint.IconId}:{MathF.Round(componentPoint.MapScreenPosition.X, 1)}:{MathF.Round(componentPoint.MapScreenPosition.Y, 1)}";
                    if (pointKeys.Add(key))
                        points.Add(componentPoint);

                    continue;
                }
            }
            else if (node->Type == NodeType.Image)
            {
                var imageNode = (AtkImageNode*)node;
                if (TryCreateMapVisionPoint(imageNode, nodePos, snapshot, addonPos, basePos, uiScale, localBattalion, out var imagePoint))
                {
                    var key = $"{imagePoint.IconId}:{MathF.Round(imagePoint.MapScreenPosition.X, 1)}:{MathF.Round(imagePoint.MapScreenPosition.Y, 1)}";
                    if (pointKeys.Add(key))
                        points.Add(imagePoint);
                }
            }
        }

        return points.ToArray();
    }

    private unsafe void CollectMapVisionPointsRecursive(
        AtkResNode* node,
        Vector2 accumulatedPos,
        AreaMapProjectionSnapshot snapshot,
        Vector2 addonPos,
        Vector2 basePos,
        float uiScale,
        byte localBattalion,
        List<BattlefieldMapVisionPointSnapshot> points,
        HashSet<nint> visitedNodes,
        HashSet<string> pointKeys,
        int depth)
    {
        if (node == null || depth > 12 || points.Count >= 128 || !visitedNodes.Add((nint)node))
            return;

        var nodePos = accumulatedPos;
        if (depth > 0)
            nodePos += new Vector2(node->X + node->OriginX, node->Y + node->OriginY);

        if (IsComponentNodeType(node->Type))
        {
            var componentNode = (AtkComponentNode*)node;
            if (componentNode->Component != null)
            {
                if (TryCreateMapVisionPoint(componentNode, nodePos, snapshot, addonPos, basePos, uiScale, localBattalion, out var componentPoint))
                {
                    var key = $"{componentPoint.IconId}:{MathF.Round(componentPoint.MapScreenPosition.X, 1)}:{MathF.Round(componentPoint.MapScreenPosition.Y, 1)}";
                    if (pointKeys.Add(key))
                        points.Add(componentPoint);

                    return;
                }

                var manager = &componentNode->Component->UldManager;
                if (manager->NodeList != null)
                {
                    var count = Math.Min((int)manager->NodeListCount, 128);
                    for (var i = 0; i < count && points.Count < 128; i++)
                    {
                        var child = manager->NodeList[i];
                        if (child != null)
                            CollectMapVisionPointsRecursive(child, nodePos, snapshot, addonPos, basePos, uiScale, localBattalion, points, visitedNodes, pointKeys, depth + 1);
                    }
                }
            }
        }

        var childNode = node->ChildNode;
        var guard = 0;
        while (childNode != null && guard++ < 256 && points.Count < 128)
        {
            CollectMapVisionPointsRecursive(childNode, nodePos, snapshot, addonPos, basePos, uiScale, localBattalion, points, visitedNodes, pointKeys, depth + 1);
            childNode = childNode->NextSiblingNode;
        }
    }

    private unsafe bool TryCreateMapVisionPoint(
        AtkComponentNode* componentNode,
        Vector2 nodePos,
        AreaMapProjectionSnapshot snapshot,
        Vector2 addonPos,
        Vector2 basePos,
        float uiScale,
        byte localBattalion,
        out BattlefieldMapVisionPointSnapshot point)
    {
        point = default;
        if (componentNode == null || componentNode->Component == null)
            return false;

        var manager = &componentNode->Component->UldManager;
        if (manager->NodeList == null || manager->NodeListCount <= MarkerIconImageNodeIndex)
            return false;

        var imageNode = manager->NodeList[MarkerIconImageNodeIndex];
        if (imageNode == null || imageNode->Type != NodeType.Image)
            return false;

        var iconId = GetImageIconId((AtkImageNode*)imageNode);
        return iconId.HasValue
            && TryCreateMapVisionPoint(iconId.Value, nodePos, snapshot, addonPos, basePos, uiScale, localBattalion, out point);
    }

    private unsafe bool TryCreateMapVisionPoint(
        AtkImageNode* imageNode,
        Vector2 nodePos,
        AreaMapProjectionSnapshot snapshot,
        Vector2 addonPos,
        Vector2 basePos,
        float uiScale,
        byte localBattalion,
        out BattlefieldMapVisionPointSnapshot point)
    {
        point = default;
        var iconId = GetImageIconId(imageNode);
        return iconId.HasValue
            && TryCreateMapVisionPoint(iconId.Value, nodePos, snapshot, addonPos, basePos, uiScale, localBattalion, out point);
    }

    private bool TryCreateMapVisionPoint(
        uint iconId,
        Vector2 nodePos,
        AreaMapProjectionSnapshot snapshot,
        Vector2 addonPos,
        Vector2 basePos,
        float uiScale,
        byte localBattalion,
        out BattlefieldMapVisionPointSnapshot point)
    {
        point = default;
        if (!TryResolveMapVisionRelation(iconId, localBattalion, out var relation, out var battalion, out var isDead))
            return false;

        var screenPosition = ImGui.GetMainViewport().Pos + addonPos + (basePos + nodePos) * uiScale;
        if (!snapshot.IsInside(screenPosition))
            return false;

        var estimatedWorldPosition = snapshot.LocalWorldPosition;
        if (snapshot.TryUnproject(screenPosition, out var worldPosition))
            estimatedWorldPosition = worldPosition;

        point = new BattlefieldMapVisionPointSnapshot(
            iconId,
            relation,
            battalion,
            isDead,
            screenPosition,
            estimatedWorldPosition);
        return true;
    }

    private static bool TryResolveMapVisionRelation(
        uint iconId,
        byte localBattalion,
        out BattlefieldPlayerRelation relation,
        out byte? battalion,
        out bool isDead)
    {
        battalion = null;
        isDead = iconId == FrontlineDeadIconId;

        if (iconId == LocalPlayerMapIconId)
        {
            relation = BattlefieldPlayerRelation.LocalPlayer;
            battalion = localBattalion <= 2 ? localBattalion : null;
            return true;
        }

        relation = BattlefieldPlayerRelation.Unknown;

        if (iconId == MaelstromMapIconId)
        {
            battalion = 0;
            relation = localBattalion <= 2 ? (localBattalion == 0 ? BattlefieldPlayerRelation.Friendly : BattlefieldPlayerRelation.Enemy) : BattlefieldPlayerRelation.Unknown;
            return true;
        }

        if (iconId == TwinAdderMapIconId)
        {
            battalion = 1;
            relation = localBattalion <= 2 ? (localBattalion == 1 ? BattlefieldPlayerRelation.Friendly : BattlefieldPlayerRelation.Enemy) : BattlefieldPlayerRelation.Unknown;
            return true;
        }

        if (iconId == ImmortalFlamesMapIconId)
        {
            battalion = 2;
            relation = localBattalion <= 2 ? (localBattalion == 2 ? BattlefieldPlayerRelation.Friendly : BattlefieldPlayerRelation.Enemy) : BattlefieldPlayerRelation.Unknown;
            return true;
        }

        if (FriendlyMapIconIds.Contains(iconId))
        {
            relation = BattlefieldPlayerRelation.Friendly;
            return true;
        }

        if (isDead)
            return false;

        return false;
    }

    private unsafe byte TryGetLocalBattalion(IPlayerCharacter localPlayer)
    {
        if (localPlayer.Address == IntPtr.Zero || !localPlayer.IsValid())
            return 255;

        return ((BattleChara*)localPlayer.Address)->Battalion;
    }

    private unsafe uint? GetImageIconId(AtkImageNode* imageNode)
    {
        if (imageNode == null || imageNode->AtkResNode.Type != NodeType.Image || imageNode->PartsList == null || imageNode->PartId >= imageNode->PartsList->PartCount)
            return null;

        var asset = imageNode->PartsList->Parts[imageNode->PartId].UldAsset;
        if (asset == null || asset->AtkTexture.TextureType != TextureType.Resource || asset->AtkTexture.Resource == null)
            return null;

        return asset->AtkTexture.Resource->IconId;
    }

    private unsafe AtkUnitBase* GetAreaMapAddon()
    {
        var addonPtr = gameGui.GetAddonByName("AreaMap", 0);
        if (addonPtr == IntPtr.Zero)
            addonPtr = gameGui.GetAddonByName("AreaMap", 1);

        return addonPtr == IntPtr.Zero ? null : (AtkUnitBase*)addonPtr.Address;
    }

    private unsafe MapSheet? GetCurrentMap()
    {
        if (cachedMapTerritoryType != clientState.TerritoryType)
        {
            cachedMap = null;
            cachedMapTerritoryType = clientState.TerritoryType;
            cachedMapId = 0;
        }

        uint mapId = 0;
        var agentMap = AgentMap.Instance();
        if (agentMap != null)
            mapId = agentMap->CurrentMapId;

        if (mapId == 0)
            mapId = clientState.MapId;

        if (mapId == 0)
            return cachedMap;

        if (cachedMap.HasValue && cachedMapId == mapId)
            return cachedMap;

        var sheet = dataManager.GetExcelSheet<MapSheet>();
        if (!sheet.TryGetRow(mapId, out var map))
            return cachedMap;

        cachedMap = map;
        cachedMapId = mapId;
        return map;
    }

    private void LogDebug(string message)
    {
        var now = Environment.TickCount64;
        if (now - lastDebugTicks < 5000)
            return;

        lastDebugTicks = now;
        log.Debug($"[AreaMapProjection] {message}");
    }

    private void ApplyAdaptiveSamplingBackoff(long now, double elapsedMs, int mapVisionPointCount, bool preferRealtime)
    {
        if (preferRealtime)
        {
            var realtimeBackoffMs = elapsedMs switch
            {
                >= 45d => 300L,
                >= 25d => 200L,
                >= 12d => 120L,
                _ => 0L,
            };

            if (realtimeBackoffMs > 0)
                realtimeAdaptiveSampleBackoffUntilTicks = Math.Max(realtimeAdaptiveSampleBackoffUntilTicks, now + realtimeBackoffMs);

            return;
        }

        var backoffMs = elapsedMs switch
        {
            >= 60d => 8000L,
            >= 25d => 5000L,
            >= 12d => 2500L,
            _ => 0L,
        };

        if (mapVisionPointCount >= 48)
            backoffMs = Math.Max(backoffMs, 6000L);
        else if (mapVisionPointCount >= 24)
            backoffMs = Math.Max(backoffMs, 3500L);

        if (backoffMs > 0)
            adaptiveSampleBackoffUntilTicks = Math.Max(adaptiveSampleBackoffUntilTicks, now + backoffMs);
    }

    private void LogSlowSample(long now, double elapsedMs, int mapVisionPointCount)
    {
        if (elapsedMs < SlowSampleWarningMs || now - lastSlowSampleLogTicks < SlowSampleLogCooldownMs)
            return;

        lastSlowSampleLogTicks = now;
        log.Debug(
            "[AreaMapProjection] Slow sample: {ElapsedMs:F1}ms, vision={VisionPointCount}",
            elapsedMs,
            mapVisionPointCount);
    }

}

    public readonly record struct AreaMapProjectionSnapshot(
    uint TerritoryType,
    Vector3 LocalWorldPosition,
    Vector2 LocalPlayerAnchor,
    Vector2 ClipMin,
    Vector2 ClipMax,
    float Scale,
    float RotationRadians,
    float MapSizeScale,
    int MapOffsetX,
    int MapOffsetY,
    bool HasReliableLocalPlayerAnchor,
    string Source,
    BattlefieldMapVisionPointSnapshot[] MapVisionPoints)
{
    public bool TryProject(Vector3 worldPosition, out Vector2 screenPosition)
    {
        if (!HasReliableLocalPlayerAnchor)
        {
            screenPosition = default;
            return false;
        }

        var localTexture = WorldToTexture(LocalWorldPosition, MapSizeScale, MapOffsetX, MapOffsetY);
        var targetTexture = WorldToTexture(worldPosition, MapSizeScale, MapOffsetX, MapOffsetY);
        var delta = (targetTexture - localTexture) * Scale;

        if (MathF.Abs(RotationRadians) > 0.0001f)
        {
            var sin = MathF.Sin(RotationRadians);
            var cos = MathF.Cos(RotationRadians);
            delta = new Vector2(delta.X * cos - delta.Y * sin, delta.X * sin + delta.Y * cos);
        }

        screenPosition = LocalPlayerAnchor + delta;
        return IsInside(screenPosition, ClipMin, ClipMax);
    }

    public bool TryUnproject(Vector2 screenPosition, out Vector3 worldPosition)
    {
        if (!HasReliableLocalPlayerAnchor)
        {
            worldPosition = default;
            return false;
        }

        var delta = screenPosition - LocalPlayerAnchor;
        if (MathF.Abs(RotationRadians) > 0.0001f)
        {
            var sin = MathF.Sin(-RotationRadians);
            var cos = MathF.Cos(-RotationRadians);
            delta = new Vector2(delta.X * cos - delta.Y * sin, delta.X * sin + delta.Y * cos);
        }

        var localTexture = WorldToTexture(LocalWorldPosition, MapSizeScale, MapOffsetX, MapOffsetY);
        var targetTexture = localTexture + delta / MathF.Max(0.0001f, Scale);
        var worldX = (targetTexture.X - 1024f) / MapSizeScale - MapOffsetX;
        var worldZ = (targetTexture.Y - 1024f) / MapSizeScale - MapOffsetY;
        worldPosition = new Vector3(worldX, LocalWorldPosition.Y, worldZ);
        return true;
    }

    public bool IsInside(Vector2 screenPosition)
        => IsInside(screenPosition, ClipMin, ClipMax);

    private static Vector2 WorldToTexture(Vector3 position, float mapSizeScale, int offsetX, int offsetY)
        => new Vector2(position.X, position.Z) * mapSizeScale + new Vector2(offsetX, offsetY) * mapSizeScale + new Vector2(1024f);

    private static bool IsInside(Vector2 position, Vector2 min, Vector2 max)
        => position.X >= min.X && position.Y >= min.Y && position.X <= max.X && position.Y <= max.Y;
}
