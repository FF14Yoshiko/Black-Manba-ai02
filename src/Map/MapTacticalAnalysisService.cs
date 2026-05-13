using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ai02;

public sealed class MapTacticalAnalysisService
{
    private const float MountedYalmsPerSecond = 10.0f;
    private const float OnFootYalmsPerSecond = 6.0f;
    private const float DefaultZoneRadius = 18f;
    private const float EnemyMainGroupNearRadius = 42f;
    private const float ObjectiveDangerRadius = 46f;
    private const float PathRecordMinDistance = 14f;
    private const long PathRecordMinIntervalMs = 2500;
    private const int MaxPathPointsPerSide = 96;

    private readonly MapAnnotationService mapAnnotationService;
    private readonly MapTacticalGraphService mapTacticalGraphService;
    private readonly Dictionary<string, List<GroupPathPoint>> observedPaths = new(StringComparer.Ordinal);

    public MapTacticalAnalysisService(
        MapAnnotationService mapAnnotationService,
        MapTacticalGraphService mapTacticalGraphService)
    {
        this.mapAnnotationService = mapAnnotationService;
        this.mapTacticalGraphService = mapTacticalGraphService;
    }

    public BattlefieldMapTacticsSnapshot Analyze(
        uint territoryType,
        uint mapId,
        string mapName,
        BattlefieldPlayerSnapshot? localPlayer,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldPlayerTrackSnapshot> playerTracks,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> mapObjectives,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldAnnouncementSituationSnapshot announcements,
        BattlefieldChatEventSituationSnapshot chatEvents,
        long now)
    {
        if (territoryType == 0 || mapId == 0)
            return new BattlefieldMapTacticsSnapshot { SummaryText = "地图战术层等待有效地图" };

        var document = mapAnnotationService.GetDocument(territoryType, mapId, mapName);
        var builtInGraph = mapTacticalGraphService.Resolve(territoryType, mapId);
        var builtInPoints = builtInGraph?.AnalysisPoints ?? Array.Empty<MapAnnotationPoint>();
        var graphPaths = builtInGraph?.Paths ?? Array.Empty<MapTacticalPathSnapshot>();
        var manualPoints = document.Points.ToArray();
        var points = builtInPoints.Concat(manualPoints).ToArray();
        var friendlyCenter = ResolveFriendlyMainCenter(localPlayer, teamSituation);
        var enemyCenter = ResolveEnemyMainCenter(teamSituation, mapVisionClusters);

        RecordObservedPath(territoryType, mapId, BattlefieldTacticalSide.Friendly, friendlyCenter, now);
        RecordObservedPath(territoryType, mapId, BattlefieldTacticalSide.Enemy, enemyCenter, now);

        var highLimitBreakEnemyIds = teamSituation.LimitBreakThreats.EnemyThreats
            .Where(threat => threat.ThreatLevel is BattlefieldLimitBreakThreatLevel.High or BattlefieldLimitBreakThreatLevel.Critical || threat.IsLikelyReady)
            .Select(threat => threat.GameObjectId)
            .ToHashSet();

        var heatPoints = BuildHeatPoints(players, playerTracks, mapVisionClusters, mapObjectives, teamSituation, chatEvents, highLimitBreakEnemyIds)
            .OrderByDescending(point => point.Intensity)
            .Take(18)
            .ToArray();

        var zones = points
            .Where(IsTacticalZoneKind)
            .Select(point => BuildZone(point, localPlayer, players, mapVisionClusters, mapObjectives, teamSituation, heatPoints, highLimitBreakEnemyIds, friendlyCenter, enemyCenter))
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label)
            .ToArray();

        var annotationRouteHints = BuildAnnotationRouteHints(points, zones, heatPoints);
        var dynamicRoutes = BuildDynamicRoutes(
            friendlyCenter ?? localPlayer?.Position,
            mapObjectives,
            graphPaths,
            zones,
            heatPoints);
        var routes = dynamicRoutes.Length > 0
            ? dynamicRoutes
                .OrderBy(route => route.TotalRisk)
                .ThenBy(route => route.MountedEtaSeconds)
                .Concat(annotationRouteHints
                    .OrderByDescending(route => route.TotalRisk)
                    .ThenBy(route => route.RouteId, StringComparer.OrdinalIgnoreCase))
                .ToArray()
            : annotationRouteHints
                .OrderByDescending(route => route.TotalRisk)
                .ThenBy(route => route.RouteId, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        var staticDangerCount = zones.Count(zone => zone.StaticRisk >= 55f);
        var dynamicDangerCount = zones.Count(zone => zone.DynamicRisk >= 55f)
            + heatPoints.Count(point => point.Intensity >= 55f);
        var mandatoryChokeCount = zones.Count(zone => zone.IsMandatoryChoke);
        var highGroundCount = zones.Count(zone => zone.Kind == MapAnnotationKind.HighGround);
        var lowGroundCount = zones.Count(zone => zone.Kind == MapAnnotationKind.LowGround);
        var jumpPadCount = zones.Count(zone => zone.Kind == MapAnnotationKind.JumpPad);
        var teleporterCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Teleporter);
        var flankEntryCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Flank);
        var bridgeCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Bridge);
        var underpassCount = zones.Count(zone => zone.Kind == MapAnnotationKind.Underpass);
        var oneWayPassageCount = graphPaths.Count(path => path.IsOneWay);
        var semantics = MapTacticalSemanticsBuilder.Build(
            builtInGraph?.MapType ?? FrontlineMapType.Unknown,
            zones,
            routes,
            heatPoints,
            mapObjectives,
            timeSituation,
            announcements,
            highGroundCount,
            lowGroundCount,
            jumpPadCount,
            teleporterCount,
            flankEntryCount,
            bridgeCount,
            underpassCount,
            mandatoryChokeCount,
            oneWayPassageCount,
            teamSituation);

        return new BattlefieldMapTacticsSnapshot
        {
            IsAvailable = true,
            TerritoryType = territoryType,
            MapId = mapId,
            MapName = ResolveMapName(document.MapName, mapName, builtInGraph?.MapName),
            AnnotationCount = points.Length,
            BuiltInGraphPointCount = builtInPoints.Length,
            ManualAnnotationCount = manualPoints.Length,
            TacticalGraphSourceText = builtInGraph?.SourceText ?? "当前地图没有内置战术图谱，仅使用手动标注和实时热区",
            TacticalGraphCoverageText = builtInGraph?.CoverageText ?? "内置覆盖：无",
            ZoneCount = zones.Length,
            StaticDangerCount = staticDangerCount,
            DynamicDangerCount = dynamicDangerCount,
            MandatoryChokeCount = mandatoryChokeCount,
            HighGroundCount = highGroundCount,
            LowGroundCount = lowGroundCount,
            JumpPadCount = jumpPadCount,
            TeleporterCount = teleporterCount,
            FlankEntryCount = flankEntryCount,
            BridgeCount = bridgeCount,
            UnderpassCount = underpassCount,
            OneWayPassageCount = oneWayPassageCount,
            TopZones = zones.Take(10).ToArray(),
            Routes = routes.Take(12).ToArray(),
            HeatPoints = heatPoints,
            FriendlyObservedPath = BuildObservedPathSnapshot(territoryType, mapId, BattlefieldTacticalSide.Friendly),
            EnemyObservedPath = BuildObservedPathSnapshot(territoryType, mapId, BattlefieldTacticalSide.Enemy),
            DangerSummaryText = semantics.DangerSummaryText,
            TerrainAdvantageSummaryText = semantics.TerrainAdvantageSummaryText,
            PassabilitySummaryText = semantics.PassabilitySummaryText,
            RewardModelSummaryText = semantics.RewardModelSummaryText,
            MapKnowledgeFocusText = semantics.MapKnowledgeFocusText,
            CurrentRecommendation = semantics.CurrentRecommendation,
            SummaryText = BuildSummary(
                builtInPoints.Length,
                manualPoints.Length,
                points.Length,
                zones.Length,
                heatPoints.Length,
                staticDangerCount,
                dynamicDangerCount,
                mandatoryChokeCount,
                highGroundCount,
                oneWayPassageCount,
                semantics.CurrentRecommendation)
        };
    }

    private static string ResolveMapName(string documentMapName, string currentMapName, string? builtInMapName)
    {
        if (!string.IsNullOrWhiteSpace(documentMapName))
            return documentMapName;
        if (!string.IsNullOrWhiteSpace(currentMapName))
            return currentMapName;
        return builtInMapName ?? string.Empty;
    }

    private static BattlefieldMapTacticalZoneSnapshot BuildZone(
        MapAnnotationPoint point,
        BattlefieldPlayerSnapshot? localPlayer,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> mapObjectives,
        BattlefieldTeamSituationSnapshot teamSituation,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints,
        IReadOnlySet<ulong> highLimitBreakEnemyIds,
        Vector3? friendlyCenter,
        Vector3? enemyCenter)
    {
        var radius = EffectiveRadius(point);
        var nearbyRadius = MathF.Max(radius, 32f);
        var friendlyNearby = players.Count(player => IsFriendly(player.Relation) && !player.IsDead && Distance2D(player.Position, point.Position) <= nearbyRadius);
        var enemyNearby = players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy && !player.IsDead && Distance2D(player.Position, point.Position) <= nearbyRadius);
        var enemyMapVisionNearby = mapVisionClusters
            .Where(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy)
            .Where(cluster => Distance2D(cluster.Center, point.Position) <= nearbyRadius + 25f)
            .Sum(cluster => Math.Max(1, cluster.PointCount));
        var highBattleHighEnemies = players.Count(player =>
            player.Relation == BattlefieldPlayerRelation.Enemy
            && !player.IsDead
            && player.BattleHighLevel >= 4
            && Distance2D(player.Position, point.Position) <= nearbyRadius + 12f);
        var highLimitBreakEnemies = players.Count(player =>
            player.Relation == BattlefieldPlayerRelation.Enemy
            && !player.IsDead
            && highLimitBreakEnemyIds.Contains(player.GameObjectId)
            && Distance2D(player.Position, point.Position) <= nearbyRadius + 18f);

        var heightDelta = localPlayer.HasValue ? point.Y - localPlayer.Value.Position.Y : 0f;
        var hasUsefulHeight = MathF.Abs(point.Y) > 0.01f && localPlayer.HasValue;
        var isCliffOrHighPlatform = hasUsefulHeight && MathF.Abs(heightDelta) > 5f;
        var estimatedWidth = point.Kind == MapAnnotationKind.Choke ? MathF.Max(0f, point.Radius) : radius * 2f;
        var isMandatoryChoke = point.Kind == MapAnnotationKind.Choke && estimatedWidth > 0f && estimatedWidth < 8f;
        var staticRisk = ResolveStaticRisk(point, isMandatoryChoke, isCliffOrHighPlatform);
        var dynamicRisk = ResolveDynamicRisk(point, radius, enemyNearby, enemyMapVisionNearby, highBattleHighEnemies, highLimitBreakEnemies, mapObjectives, heatPoints, teamSituation, friendlyCenter, enemyCenter);
        var terrainModifier = ResolveEngagementModifier(point.Kind, isCliffOrHighPlatform, heightDelta);
        var totalRisk = Math.Clamp(staticRisk * 0.45f + dynamicRisk * 0.55f - Math.Max(0f, terrainModifier) * 0.25f + Math.Max(0f, -terrainModifier) * 0.35f, 0f, 100f);
        var recommendation = ResolveZoneRecommendation(point, friendlyNearby, enemyNearby + enemyMapVisionNearby, teamSituation, enemyCenter, isMandatoryChoke, totalRisk, terrainModifier);
        var evidence = BuildZoneEvidence(point, radius, estimatedWidth, heightDelta, isCliffOrHighPlatform, isMandatoryChoke, friendlyNearby, enemyNearby, enemyMapVisionNearby, highBattleHighEnemies, highLimitBreakEnemies, staticRisk, dynamicRisk, terrainModifier);

        return new BattlefieldMapTacticalZoneSnapshot(
            point.Id,
            point.Kind,
            string.IsNullOrWhiteSpace(point.Label) ? KindText(point.Kind) : point.Label,
            point.RouteId,
            point.Position,
            radius,
            estimatedWidth,
            heightDelta,
            isCliffOrHighPlatform,
            isMandatoryChoke,
            friendlyNearby,
            enemyNearby,
            enemyMapVisionNearby,
            highBattleHighEnemies,
            highLimitBreakEnemies,
            terrainModifier,
            staticRisk,
            dynamicRisk,
            totalRisk,
            recommendation,
            evidence);
    }

    private static BattlefieldMapTacticalRouteSnapshot[] BuildAnnotationRouteHints(
        IReadOnlyList<MapAnnotationPoint> points,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints)
    {
        return points
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildRoute(group.Key, group.OrderBy(point => point.CreatedAtUnixMs).ToArray(), zones, heatPoints))
            .ToArray();
    }

    private static BattlefieldMapTacticalRouteSnapshot[] BuildDynamicRoutes(
        Vector3? start,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        IReadOnlyList<MapTacticalPathSnapshot> graphPaths,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints)
    {
        if (!start.HasValue || graphPaths.Count == 0)
            return Array.Empty<BattlefieldMapTacticalRouteSnapshot>();

        var graph = BuildNavigationGraph(graphPaths, zones, heatPoints);
        if (graph.Nodes.Count == 0)
            return Array.Empty<BattlefieldMapTacticalRouteSnapshot>();

        var actionableObjectives = objectives
            .Where(IsDynamicRouteTarget)
            .OrderBy(objective => Distance2D(start.Value, objective.Position))
            .Take(8)
            .ToArray();
        if (actionableObjectives.Length == 0)
            return Array.Empty<BattlefieldMapTacticalRouteSnapshot>();

        var routes = new List<BattlefieldMapTacticalRouteSnapshot>(actionableObjectives.Length);
        foreach (var objective in actionableObjectives)
        {
            var positions = ResolveNavigationRoute(graph, start.Value, objective.Position);
            if (positions.Length < 2)
                continue;

            var name = string.IsNullOrWhiteSpace(objective.Name) ? objective.Category.ToString() : objective.Name;
            routes.Add(BuildRouteFromPositions(
                $"动态-{name}",
                "动态寻路",
                positions,
                zones,
                heatPoints,
                $"目标 {name}；按当前通行图、危险区和敌方热区临时计算"));
        }

        return routes.ToArray();
    }

    private static BattlefieldMapTacticalRouteSnapshot BuildRoute(
        string routeId,
        IReadOnlyList<MapAnnotationPoint> points,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints)
        => BuildRouteFromPositions(
            routeId,
            string.Join("/", points.Select(point => KindText(point.Kind)).Distinct()),
            points.Select(point => point.Position).ToArray(),
            zones,
            heatPoints,
            string.Empty);

    private static BattlefieldMapTacticalRouteSnapshot BuildRouteFromPositions(
        string routeId,
        string kindSummary,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints,
        string prefixEvidence)
        => MapRouteRiskEvaluator.Evaluate(
            routeId,
            kindSummary,
            positions,
            zones,
            heatPoints,
            prefixEvidence);

    private static NavigationGraph BuildNavigationGraph(
        IReadOnlyList<MapTacticalPathSnapshot> graphPaths,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints)
    {
        var nodes = new List<NavigationNode>();
        var edges = new Dictionary<int, List<NavigationEdge>>();
        var nodeIds = new Dictionary<string, int>(StringComparer.Ordinal);

        int GetNodeId(Vector3 position)
        {
            var key = $"{MathF.Round(position.X, 1):0.0}:{MathF.Round(position.Z, 1):0.0}";
            if (nodeIds.TryGetValue(key, out var id))
                return id;

            id = nodes.Count;
            nodeIds[key] = id;
            nodes.Add(new NavigationNode(id, position));
            edges[id] = new List<NavigationEdge>();
            return id;
        }

        foreach (var path in graphPaths)
        {
            if (path.Points.Length < 2)
                continue;

            for (var i = 1; i < path.Points.Length; i++)
            {
                var from = GetNodeId(path.Points[i - 1]);
                var to = GetNodeId(path.Points[i]);
                var distance = Distance2D(path.Points[i - 1], path.Points[i]);
                if (distance <= 0.1f)
                    continue;

                var cost = distance * ResolvePathCostMultiplier(path, path.Points[i - 1], path.Points[i], zones, heatPoints);
                edges[from].Add(new NavigationEdge(to, cost));
                if (!path.IsOneWay)
                    edges[to].Add(new NavigationEdge(from, cost));
            }
        }

        return new NavigationGraph(nodes, edges);
    }

    private static Vector3[] ResolveNavigationRoute(NavigationGraph graph, Vector3 start, Vector3 destination)
    {
        var nodes = new List<NavigationNode>(graph.Nodes);
        var edges = graph.Edges.ToDictionary(pair => pair.Key, pair => pair.Value.ToList());
        var startId = nodes.Count;
        nodes.Add(new NavigationNode(startId, start));
        edges[startId] = new List<NavigationEdge>();
        var destinationId = nodes.Count;
        nodes.Add(new NavigationNode(destinationId, destination));
        edges[destinationId] = new List<NavigationEdge>();

        ConnectVirtualNode(startId, start, nodes, edges, true);
        ConnectVirtualNode(destinationId, destination, nodes, edges, false);

        var previous = RunDijkstra(nodes.Count, edges, startId, destinationId);
        if (!previous.TryGetValue(destinationId, out _) && destinationId != startId)
        {
            var directDistance = Distance2D(start, destination);
            if (directDistance <= 0.1f)
                return Array.Empty<Vector3>();

            return new[] { start, destination };
        }

        var routeIds = new List<int>();
        var current = destinationId;
        routeIds.Add(current);
        while (current != startId && previous.TryGetValue(current, out var prev))
        {
            current = prev;
            routeIds.Add(current);
        }

        if (routeIds[^1] != startId)
            return new[] { start, destination };

        routeIds.Reverse();
        return routeIds.Select(id => nodes[id].Position).ToArray();
    }

    private static void ConnectVirtualNode(
        int virtualId,
        Vector3 position,
        IReadOnlyList<NavigationNode> nodes,
        IDictionary<int, List<NavigationEdge>> edges,
        bool outbound)
    {
        var nearest = nodes
            .Where(node => node.Id != virtualId)
            .Select(node => new { node.Id, Distance = Distance2D(position, node.Position) })
            .OrderBy(item => item.Distance)
            .Take(4)
            .ToArray();

        foreach (var item in nearest)
        {
            var cost = Math.Max(1f, item.Distance * 1.12f);
            if (outbound)
            {
                edges[virtualId].Add(new NavigationEdge(item.Id, cost));
                if (edges.TryGetValue(item.Id, out var reverse))
                    reverse.Add(new NavigationEdge(virtualId, cost));
            }
            else if (edges.TryGetValue(item.Id, out var fromNode))
            {
                fromNode.Add(new NavigationEdge(virtualId, cost));
                edges[virtualId].Add(new NavigationEdge(item.Id, cost));
            }
        }
    }

    private static Dictionary<int, int> RunDijkstra(
        int nodeCount,
        IReadOnlyDictionary<int, List<NavigationEdge>> edges,
        int startId,
        int destinationId)
    {
        var distances = new float[nodeCount];
        Array.Fill(distances, float.PositiveInfinity);
        distances[startId] = 0f;
        var previous = new Dictionary<int, int>();
        var visited = new bool[nodeCount];
        var queue = new PriorityQueue<int, float>();
        queue.Enqueue(startId, 0f);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (visited[current])
                continue;

            visited[current] = true;
            if (current == destinationId)
                break;

            if (!edges.TryGetValue(current, out var currentEdges))
                continue;

            foreach (var edge in currentEdges)
            {
                var nextDistance = distances[current] + edge.Cost;
                if (nextDistance >= distances[edge.To])
                    continue;

                distances[edge.To] = nextDistance;
                previous[edge.To] = current;
                queue.Enqueue(edge.To, nextDistance);
            }
        }

        return previous;
    }

    private static float ResolvePathCostMultiplier(
        MapTacticalPathSnapshot path,
        Vector3 from,
        Vector3 to,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints)
    {
        var mid = (from + to) * 0.5f;
        var pathPenalty = path.Kind switch
        {
            MapAnnotationKind.JumpPad => path.IsOneWay ? 0.85f : 1.10f,
            MapAnnotationKind.Bridge => 1.18f,
            MapAnnotationKind.Flank => 1.12f,
            MapAnnotationKind.Underpass => 1.28f,
            _ => 1.0f,
        };
        pathPenalty += Math.Clamp(path.RiskScore, 0, 100) / 100f * 0.55f;

        var zoneRisk = zones
            .Where(zone => Distance2D(zone.Position, mid) <= MathF.Max(zone.Radius, 18f) + 14f)
            .Select(zone => zone.TotalRisk)
            .DefaultIfEmpty(0f)
            .Max();
        var heatRisk = heatPoints
            .Where(heat => Distance2D(heat.Position, mid) <= heat.Radius + 16f)
            .Select(heat => heat.Intensity)
            .DefaultIfEmpty(0f)
            .Max();

        return Math.Clamp(pathPenalty + zoneRisk / 100f * 1.05f + heatRisk / 100f * 1.25f, 0.55f, 4.0f);
    }

    private static bool IsDynamicRouteTarget(BattlefieldMapObjectiveSnapshot objective)
        => objective.State is BattlefieldMapObjectiveState.Warning
            or BattlefieldMapObjectiveState.Active
            or BattlefieldMapObjectiveState.Contested
            or BattlefieldMapObjectiveState.Controlled
            or BattlefieldMapObjectiveState.Unknown;

    private static BattlefieldMapHeatPointSnapshot[] BuildHeatPoints(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldPlayerTrackSnapshot> playerTracks,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> mapObjectives,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldChatEventSituationSnapshot chatEvents,
        IReadOnlySet<ulong> highLimitBreakEnemyIds)
    {
        var heat = new List<BattlefieldMapHeatPointSnapshot>(32);

        foreach (var cluster in mapVisionClusters.Where(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy))
        {
            heat.Add(new BattlefieldMapHeatPointSnapshot(
                cluster.Center,
                Math.Clamp(34f + cluster.PointCount * 2.5f, 40f, 95f),
                Math.Clamp(28f + cluster.PointCount * 6f, 0f, 100f),
                $"敌方密度：地图视野 {cluster.PointCount}"));
        }

        foreach (var player in players.Where(player => player.Relation == BattlefieldPlayerRelation.Enemy && !player.IsDead))
        {
            if (player.BattleHighLevel >= 4)
            {
                heat.Add(new BattlefieldMapHeatPointSnapshot(
                    player.Position,
                    38f,
                    Math.Clamp(38f + player.BattleHighLevel * 10f, 0f, 100f),
                    player.IsBattleFever ? $"高战意：{player.Name} 狂热" : $"高战意：{player.Name} {player.BattleHighLevel}层"));
            }

            if (highLimitBreakEnemyIds.Contains(player.GameObjectId))
            {
                heat.Add(new BattlefieldMapHeatPointSnapshot(
                    player.Position,
                    46f,
                    78f,
                    $"高极限技：{player.Name}"));
            }
        }

        foreach (var objective in mapObjectives.Where(objective => objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested or BattlefieldMapObjectiveState.Warning))
        {
            var intensity = objective.State == BattlefieldMapObjectiveState.Contested ? 82f : 58f;
            if (objective.EnemyAttackerCount > 0)
                intensity += Math.Min(22f, objective.EnemyAttackerCount * 5f);
            heat.Add(new BattlefieldMapHeatPointSnapshot(
                objective.Position,
                ObjectiveDangerRadius,
                Math.Clamp(intensity, 0f, 100f),
                $"目标争夺区：{objective.Name} {objective.OwnershipText}".Trim()));
        }

        foreach (var track in playerTracks.Where(track => IsFriendly(track.Relation) && track.DeathAgeMs is >= 0 and <= 45000))
        {
            heat.Add(new BattlefieldMapHeatPointSnapshot(
                track.LastPosition,
                36f,
                62f,
                $"死亡热区：{track.Name}"));
        }

        if (teamSituation.IsEnemySplit && teamSituation.Friendly.MainCluster.HasValue)
        {
            heat.Add(new BattlefieldMapHeatPointSnapshot(
                teamSituation.Friendly.MainCluster.Value.Center,
                70f,
                64f,
                "被夹风险：敌方分兵"));
        }

        if (chatEvents.FriendlyDeathsRecent >= 2 && teamSituation.Friendly.MainCluster.HasValue)
        {
            heat.Add(new BattlefieldMapHeatPointSnapshot(
                teamSituation.Friendly.MainCluster.Value.Center,
                54f,
                Math.Clamp(48f + chatEvents.FriendlyDeathsRecent * 8f, 0f, 100f),
                $"近期死亡：我方 {chatEvents.FriendlyDeathsRecent}"));
        }

        return heat.ToArray();
    }

    private static float ResolveStaticRisk(MapAnnotationPoint point, bool isMandatoryChoke, bool isCliffOrHighPlatform)
    {
        var baseRisk = point.Kind switch
        {
            MapAnnotationKind.Danger => 58f,
            MapAnnotationKind.Flank => 42f,
            MapAnnotationKind.Choke => isMandatoryChoke ? 48f : 28f,
            MapAnnotationKind.LowGround => 34f,
            MapAnnotationKind.Underpass => 32f,
            MapAnnotationKind.Bridge => 22f,
            MapAnnotationKind.JumpPad => 18f,
            MapAnnotationKind.Spawn => LooksLikeEnemySpawn(point) ? 62f : 12f,
            _ => 10f
        };

        if (LooksLikeNoRetreat(point))
            baseRisk += 20f;
        if (isCliffOrHighPlatform && point.Kind is MapAnnotationKind.LowGround or MapAnnotationKind.Choke or MapAnnotationKind.Underpass)
            baseRisk += 10f;

        return Math.Clamp(baseRisk + Math.Clamp(point.RiskScore, 0, 100) * 0.35f, 0f, 100f);
    }

    private static float ResolveDynamicRisk(
        MapAnnotationPoint point,
        float radius,
        int enemyNearby,
        int enemyMapVisionNearby,
        int highBattleHighEnemies,
        int highLimitBreakEnemies,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> mapObjectives,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints,
        BattlefieldTeamSituationSnapshot teamSituation,
        Vector3? friendlyCenter,
        Vector3? enemyCenter)
    {
        var risk = enemyNearby * 7f
            + enemyMapVisionNearby * 3.5f
            + highBattleHighEnemies * 12f
            + highLimitBreakEnemies * 18f;

        if (enemyCenter.HasValue && Distance2D(enemyCenter.Value, point.Position) <= EnemyMainGroupNearRadius + radius)
            risk += 25f;

        if (friendlyCenter.HasValue && enemyCenter.HasValue && teamSituation.IsEnemySplit && Distance2D(friendlyCenter.Value, point.Position) <= radius + 40f)
            risk += 18f;

        if (mapObjectives.Any(objective =>
                objective.State is BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested or BattlefieldMapObjectiveState.Warning
                && Distance2D(objective.Position, point.Position) <= ObjectiveDangerRadius + radius))
        {
            risk += 22f;
        }

        var nearbyHeat = heatPoints
            .Where(heat => Distance2D(heat.Position, point.Position) <= heat.Radius + radius)
            .Select(heat => heat.Intensity * 0.55f)
            .DefaultIfEmpty(0f)
            .Max();
        risk += nearbyHeat;

        return Math.Clamp(risk, 0f, 100f);
    }

    private static float ResolveEngagementModifier(MapAnnotationKind kind, bool isCliffOrHighPlatform, float heightDelta)
    {
        var modifier = kind switch
        {
            MapAnnotationKind.HighGround => 20f,
            MapAnnotationKind.LowGround => -30f,
            MapAnnotationKind.Bridge => 5f,
            MapAnnotationKind.Underpass => -10f,
            _ => 0f
        };

        if (isCliffOrHighPlatform)
            modifier += heightDelta > 5f ? 12f : -12f;

        return modifier;
    }

    private static string ResolveZoneRecommendation(
        MapAnnotationPoint point,
        int friendlyNearby,
        int enemyPressure,
        BattlefieldTeamSituationSnapshot teamSituation,
        Vector3? enemyCenter,
        bool isMandatoryChoke,
        float totalRisk,
        float terrainModifier)
    {
        var enemyNearPoint = enemyCenter.HasValue && Distance2D(enemyCenter.Value, point.Position) <= EnemyMainGroupNearRadius + EffectiveRadius(point);
        var friendlyAdvantage = friendlyNearby >= Math.Max(2, enemyPressure + 2)
            || teamSituation.Friendly.AliveCount >= teamSituation.Enemy.AliveCount + 3;
        var enemyAdvantage = enemyPressure >= Math.Max(2, friendlyNearby + 2)
            || teamSituation.Enemy.AliveCount >= teamSituation.Friendly.AliveCount + 3;

        if ((point.Kind == MapAnnotationKind.Choke || isMandatoryChoke) && enemyNearPoint && friendlyAdvantage)
            return "推荐卡口接团";
        if (enemyAdvantage && totalRisk >= 48f)
            return "强制绕路";
        if (point.Kind == MapAnnotationKind.HighGround && terrainModifier > 0f && !enemyAdvantage)
            return "可防守占高";
        if (point.Kind == MapAnnotationKind.LowGround && enemyPressure > 0)
            return "低地避免接战";
        if (totalRisk >= 75f)
            return "高危绕开";
        if (totalRisk >= 55f)
            return "谨慎通过";
        if (isMandatoryChoke)
            return "必卡点";
        return "可用";
    }

    private void RecordObservedPath(uint territoryType, uint mapId, BattlefieldTacticalSide side, Vector3? center, long now)
    {
        if (!center.HasValue)
            return;

        var key = BuildPathKey(territoryType, mapId, side);
        if (!observedPaths.TryGetValue(key, out var points))
        {
            points = new List<GroupPathPoint>(MaxPathPointsPerSide);
            observedPaths[key] = points;
        }

        if (points.Count > 0)
        {
            var last = points[^1];
            if (now - last.Ticks < PathRecordMinIntervalMs && Distance2D(last.Position, center.Value) < PathRecordMinDistance)
                return;
        }

        points.Add(new GroupPathPoint(now, center.Value));
        if (points.Count > MaxPathPointsPerSide)
            points.RemoveRange(0, points.Count - MaxPathPointsPerSide);
    }

    private BattlefieldMapGroupPathSnapshot BuildObservedPathSnapshot(uint territoryType, uint mapId, BattlefieldTacticalSide side)
    {
        var key = BuildPathKey(territoryType, mapId, side);
        if (!observedPaths.TryGetValue(key, out var points) || points.Count == 0)
        {
            var sideText = side == BattlefieldTacticalSide.Friendly ? "我方" : "敌方";
            return new BattlefieldMapGroupPathSnapshot(side, "实时主团轨迹", 0, 0f, 0, 0, default, Array.Empty<Vector3>(), $"{sideText}常走路径正在累积");
        }

        var distance = 0f;
        for (var i = 1; i < points.Count; i++)
            distance += Distance2D(points[i - 1].Position, points[i].Position);

        var latest = points[^1].Position;
        var label = side == BattlefieldTacticalSide.Friendly ? "我方" : "敌方";
        return new BattlefieldMapGroupPathSnapshot(
            side,
            "实时主团轨迹",
            points.Count,
            distance,
            EstimateEtaSeconds(distance, MountedYalmsPerSecond),
            EstimateEtaSeconds(distance, OnFootYalmsPerSecond),
            latest,
            points.Select(point => point.Position).ToArray(),
            $"{label}轨迹 {points.Count} 点，累计 {distance:0}y，骑乘预计 {FormatDuration(EstimateEtaSeconds(distance, MountedYalmsPerSecond))}");
    }

    private static Vector3? ResolveFriendlyMainCenter(BattlefieldPlayerSnapshot? localPlayer, BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (teamSituation.Friendly.MainCluster.HasValue)
            return teamSituation.Friendly.MainCluster.Value.Center;
        return localPlayer?.Position;
    }

    private static Vector3? ResolveEnemyMainCenter(
        BattlefieldTeamSituationSnapshot teamSituation,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters)
    {
        if (teamSituation.EnemyMainGroupMovement.HasMainGroup)
            return teamSituation.EnemyMainGroupMovement.CurrentCenter;

        var visionCluster = mapVisionClusters
            .Where(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy)
            .OrderByDescending(cluster => cluster.PointCount)
            .FirstOrDefault();
        if (visionCluster.PointCount > 0)
            return visionCluster.Center;

        var playerCluster = teamSituation.Enemy.MainCluster;
        return playerCluster?.Center;
    }

    private static bool IsTacticalZoneKind(MapAnnotationPoint point)
        => point.Kind is MapAnnotationKind.Spawn
            or MapAnnotationKind.Choke
            or MapAnnotationKind.JumpPad
            or MapAnnotationKind.Teleporter
            or MapAnnotationKind.HighGround
            or MapAnnotationKind.LowGround
            or MapAnnotationKind.Danger
            or MapAnnotationKind.Flank
            or MapAnnotationKind.Bridge
            or MapAnnotationKind.Underpass;

    private static float EffectiveRadius(MapAnnotationPoint point)
        => Math.Clamp(point.Radius <= 0f ? DefaultZoneRadius : point.Radius, 4f, 220f);

    private static bool IsFriendly(BattlefieldPlayerRelation relation)
        => relation is BattlefieldPlayerRelation.LocalPlayer or BattlefieldPlayerRelation.Friendly;

    private static bool LooksLikeEnemySpawn(MapAnnotationPoint point)
    {
        var text = $"{point.Label} {point.RouteId}";
        return point.Kind == MapAnnotationKind.Spawn
            && (text.Contains("敌", StringComparison.OrdinalIgnoreCase)
                || text.Contains("对面", StringComparison.OrdinalIgnoreCase)
                || text.Contains("enemy", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeNoRetreat(MapAnnotationPoint point)
    {
        var text = $"{point.Label} {point.RouteId}";
        return text.Contains("无退", StringComparison.OrdinalIgnoreCase)
            || text.Contains("跳崖", StringComparison.OrdinalIgnoreCase)
            || text.Contains("死路", StringComparison.OrdinalIgnoreCase)
            || text.Contains("夹", StringComparison.OrdinalIgnoreCase)
            || text.Contains("no retreat", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildZoneEvidence(
        MapAnnotationPoint point,
        float radius,
        float estimatedWidth,
        float heightDelta,
        bool isCliffOrHighPlatform,
        bool isMandatoryChoke,
        int friendlyNearby,
        int enemyNearby,
        int enemyMapVisionNearby,
        int highBattleHighEnemies,
        int highLimitBreakEnemies,
        float staticRisk,
        float dynamicRisk,
        float terrainModifier)
    {
        var parts = new List<string>
        {
            $"{KindText(point.Kind)} 半径 {radius:0}y",
            $"静态 {staticRisk:0}",
            $"动态 {dynamicRisk:0}",
            $"我/敌 {friendlyNearby}/{enemyNearby + enemyMapVisionNearby}"
        };

        if (point.Kind == MapAnnotationKind.Choke)
            parts.Add($"宽度 {estimatedWidth:0.0}y");
        if (isMandatoryChoke)
            parts.Add("窄通道<8y，必卡点");
        if (point.Kind == MapAnnotationKind.HighGround)
            parts.Add("高地防守/视野 +20%");
        if (point.Kind == MapAnnotationKind.LowGround)
            parts.Add("低地接战 -30%");
        if (isCliffOrHighPlatform)
            parts.Add($"高度差 {heightDelta:0.0}y，断崖/高台");
        if (highBattleHighEnemies > 0)
            parts.Add($"高战意敌 {highBattleHighEnemies}");
        if (highLimitBreakEnemies > 0)
            parts.Add($"高极限技敌 {highLimitBreakEnemies}");
        if (MathF.Abs(terrainModifier) > 0.1f)
            parts.Add($"地形权重 {terrainModifier:+0;-0}%");

        return string.Join("；", parts);
    }

    private static string BuildSummary(
        int builtInPointCount,
        int manualAnnotationCount,
        int totalPointCount,
        int zoneCount,
        int heatPointCount,
        int staticDangerCount,
        int dynamicDangerCount,
        int mandatoryChokeCount,
        int highGroundCount,
        int oneWayPassageCount,
        string recommendation)
    {
        if (totalPointCount == 0)
        {
            if (heatPointCount > 0)
                return $"地图战术层：当前无静态图谱，已按可见敌方/目标/死亡点生成实时危险热区 {heatPointCount} 个；{recommendation}";

            return "地图战术层已接入，但当前地图还没有内置图谱或手动标注";
        }

        return $"地图战术层：内置 {builtInPointCount}，手动 {manualAnnotationCount}，合计 {totalPointCount}，战术区 {zoneCount}，实时热区 {heatPointCount}，静态危险 {staticDangerCount}，动态危险 {dynamicDangerCount}，必卡点 {mandatoryChokeCount}，高台 {highGroundCount}，单向通道 {oneWayPassageCount}；{recommendation}";
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static int EstimateEtaSeconds(float distance, float yalmsPerSecond)
        => (int)MathF.Ceiling(Math.Max(0f, distance) / Math.Max(0.1f, yalmsPerSecond));

    private static string FormatDuration(int seconds)
        => $"{seconds / 60:D2}:{seconds % 60:D2}";

    private static string BuildPathKey(uint territoryType, uint mapId, BattlefieldTacticalSide side)
        => $"{territoryType}:{mapId}:{side}";

    private static string KindText(MapAnnotationKind kind)
        => kind switch
        {
            MapAnnotationKind.Spawn => "出生",
            MapAnnotationKind.Objective => "目标",
            MapAnnotationKind.Choke => "卡口",
            MapAnnotationKind.JumpPad => "跳台",
            MapAnnotationKind.Teleporter => "传送",
            MapAnnotationKind.HighGround => "高地",
            MapAnnotationKind.LowGround => "低地",
            MapAnnotationKind.Danger => "危险",
            MapAnnotationKind.Rotation => "转点",
            MapAnnotationKind.Flank => "夹击",
            MapAnnotationKind.RoutePoint => "路径",
            MapAnnotationKind.Bridge => "桥面",
            MapAnnotationKind.Underpass => "桥洞",
            _ => "备注",
        };

    private readonly record struct GroupPathPoint(long Ticks, Vector3 Position);

    private readonly record struct NavigationNode(int Id, Vector3 Position);

    private readonly record struct NavigationEdge(int To, float Cost);

    private sealed class NavigationGraph
    {
        public NavigationGraph(
            IReadOnlyList<NavigationNode> nodes,
            IReadOnlyDictionary<int, List<NavigationEdge>> edges)
        {
            Nodes = nodes;
            Edges = edges;
        }

        public IReadOnlyList<NavigationNode> Nodes { get; }
        public IReadOnlyDictionary<int, List<NavigationEdge>> Edges { get; }
    }
}
