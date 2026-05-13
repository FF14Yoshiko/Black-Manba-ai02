using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MapSheet = Lumina.Excel.Sheets.Map;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;

namespace ai02;

public sealed class MapTacticalGraphService
{
    private const string BuiltInPointPrefix = "builtin.";
    private const string CustomPointPrefix = "custom.";
    private const string BuiltInVersion = "内置战术图谱第 0.1 版";
    private const string CustomVersion = "自定义战术图谱";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly Dictionary<string, MapTacticalGraphSnapshot> cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CustomMapTacticalGraphDocument?> builtInDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CustomMapTacticalGraphDocument?> customDocuments = new(StringComparer.Ordinal);
    private readonly string builtInDirectory;
    private readonly string rootDirectory;

    public MapTacticalGraphService(IDataManager dataManager, IPluginLog log, string pluginAssemblyDirectory)
    {
        this.dataManager = dataManager;
        this.log = log;
        builtInDirectory = ResolveBuiltInDirectory(pluginAssemblyDirectory);
        rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN",
            "pluginConfigs",
            "ai02",
            "MapTacticalGraphs");
    }

    public string RootDirectory => rootDirectory;

    private static string ResolveBuiltInDirectory(string? pluginAssemblyDirectory)
    {
        var candidates = new[]
        {
            pluginAssemblyDirectory,
            AppContext.BaseDirectory
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var path = Path.Combine(candidate, "BuiltInTacticalGraphs");
            if (Directory.Exists(path))
                return path;
        }

        var fallback = string.IsNullOrWhiteSpace(pluginAssemblyDirectory) ? AppContext.BaseDirectory : pluginAssemblyDirectory;
        return Path.Combine(fallback, "BuiltInTacticalGraphs");
    }

    public MapTacticalGraphSnapshot? Resolve(uint territoryType, uint mapId)
    {
        var builtInDocument = LoadBuiltInDocument(territoryType, mapId);
        BuiltInTacticalGraphDefinition? definition;
        MapTransform transform;
        if (builtInDocument != null)
        {
            var territoryTypeIds = ResolveBuiltInTerritoryTypeIds(territoryType, mapId, builtInDocument);
            var mapIds = ResolveBuiltInMapIds(territoryType, mapId, builtInDocument);
            var fallbackMapId = mapIds.FirstOrDefault();
            if (fallbackMapId == 0)
                fallbackMapId = builtInDocument.SourceMapId != 0 ? builtInDocument.SourceMapId : builtInDocument.MapId;
            if (fallbackMapId == 0)
                fallbackMapId = mapId;
            transform = ResolveMapTransform(territoryType, mapId, fallbackMapId);
            definition = BuildDefinitionFromDocument(builtInDocument, territoryTypeIds, mapIds, transform);
        }
        else
        {
            definition = ResolveDefinition(territoryType, mapId);
            transform = definition == null
                ? ResolveMapTransform(territoryType, mapId, mapId)
                : ResolveMapTransform(definition, territoryType, mapId);
        }

        var customDocument = LoadCustomDocument(territoryType, mapId);
        if (builtInDocument != null && customDocument != null && IsPromotedBuiltInShadow(builtInDocument, customDocument))
            customDocument = null;

        if (definition == null && customDocument == null)
            return null;

        var builtInStamp = builtInDocument?.UpdatedAtUnixMs ?? 0;
        var customStamp = customDocument?.UpdatedAtUnixMs ?? 0;
        var cacheKey = $"{definition?.Key ?? "custom"}:{territoryType}:{transform.MapId}:{transform.Scale:0.0000}:{transform.OffsetX}:{transform.OffsetY}:{builtInStamp}:{customStamp}";
        if (!cache.TryGetValue(cacheKey, out var cached))
        {
            cached = BuildSnapshot(definition, customDocument, territoryType, transform);
            cache[cacheKey] = cached;
        }

        return CloneSnapshot(cached);
    }

    public MapAnnotationPoint[] GetPoints(uint territoryType, uint mapId)
        => Resolve(territoryType, mapId)?.Points ?? Array.Empty<MapAnnotationPoint>();

    public static bool IsBuiltInPoint(MapAnnotationPoint point)
        => point.Id.StartsWith(BuiltInPointPrefix, StringComparison.Ordinal);

    public static bool IsCustomPoint(MapAnnotationPoint point)
        => point.Id.StartsWith(CustomPointPrefix, StringComparison.Ordinal);

    public SaveCustomTacticalGraphResult SaveCustomGraphFromAnnotations(
        uint territoryType,
        uint mapId,
        string mapName,
        IReadOnlyList<MapAnnotationPoint> annotations)
    {
        if (territoryType == 0 || mapId == 0)
            return new SaveCustomTacticalGraphResult(false, 0, 0, 0, "当前地图无效，不能保存战术图谱。", string.Empty);

        if (annotations.Count == 0)
            return new SaveCustomTacticalGraphResult(false, 0, 0, 0, "当前地图没有手动标注，不能保存战术图谱。", string.Empty);

        var document = BuildCustomDocument(territoryType, mapId, mapName, annotations);
        Directory.CreateDirectory(rootDirectory);
        var path = BuildCustomPath(territoryType, mapId);
        if (File.Exists(path))
            BackupCurrentFile(territoryType, mapId, "save");

        File.WriteAllText(path, JsonSerializer.Serialize(document, SerializerOptions));
        WriteVersionDocument(territoryType, mapId, document);

        var key = BuildCustomKey(territoryType, mapId);
        customDocuments[key] = document;
        cache.Clear();

        var message = $"已保存自定义战术图谱：区域 {document.Regions.Count}，路径 {document.Paths.Count}，节点 {document.Points.Count}。";
        return new SaveCustomTacticalGraphResult(true, document.Regions.Count, document.Paths.Count, document.Points.Count, message, path);
    }

    public MapTacticalGraphVersionInfo[] ListCustomGraphVersions(uint territoryType, uint mapId)
    {
        var versions = new List<MapTacticalGraphVersionInfo>();
        var current = LoadCustomDocument(territoryType, mapId);
        var currentPath = BuildCustomPath(territoryType, mapId);
        if (current != null)
            versions.Add(BuildVersionInfo("__current", "当前图谱", current, true, currentPath));

        var directory = BuildVersionDirectory(territoryType, mapId);
        if (Directory.Exists(directory))
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                var document = ReadCustomDocument(path);
                if (document == null)
                    continue;

                var versionId = string.IsNullOrWhiteSpace(document.GraphVersionId)
                    ? Path.GetFileNameWithoutExtension(path)
                    : document.GraphVersionId;
                versions.Add(BuildVersionInfo(versionId, versionId, document, false, path));
            }
        }

        return versions
            .OrderByDescending(version => version.IsCurrent)
            .ThenByDescending(version => version.UpdatedAtUnixMs)
            .ThenByDescending(version => version.VersionId, StringComparer.Ordinal)
            .ToArray();
    }

    public TacticalGraphOperationResult BackupCurrentCustomGraph(uint territoryType, uint mapId)
    {
        var path = BuildCustomPath(territoryType, mapId);
        if (!File.Exists(path))
            return new TacticalGraphOperationResult(false, "当前地图没有已存自定义图谱，无法备份。", string.Empty);

        var backupPath = BackupCurrentFile(territoryType, mapId, "manual");
        return new TacticalGraphOperationResult(true, $"已备份当前图谱：{backupPath}", backupPath);
    }

    public TacticalGraphOperationResult RollbackCustomGraphToVersion(uint territoryType, uint mapId, string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId) || versionId == "__current")
            return new TacticalGraphOperationResult(false, "请选择一个历史版本回滚。", string.Empty);

        var versionPath = FindVersionPath(territoryType, mapId, versionId);
        if (string.IsNullOrWhiteSpace(versionPath))
            return new TacticalGraphOperationResult(false, $"没有找到版本：{versionId}", string.Empty);

        var document = ReadCustomDocument(versionPath);
        if (document == null)
            return new TacticalGraphOperationResult(false, $"版本文件读取失败：{versionPath}", versionPath);

        var currentPath = BuildCustomPath(territoryType, mapId);
        if (File.Exists(currentPath))
            BackupCurrentFile(territoryType, mapId, "rollback");

        PrepareDocumentForWrite(document, territoryType, mapId, document.MapName, true);
        Directory.CreateDirectory(rootDirectory);
        File.WriteAllText(currentPath, JsonSerializer.Serialize(document, SerializerOptions));
        WriteVersionDocument(territoryType, mapId, document);
        customDocuments[BuildCustomKey(territoryType, mapId)] = document;
        cache.Clear();

        return new TacticalGraphOperationResult(true, $"已回滚到版本 {versionId}，并生成新的当前版本。", currentPath);
    }

    public MapTacticalGraphCompareResult CompareCurrentWithVersion(uint territoryType, uint mapId, string versionId)
    {
        var current = LoadCustomDocument(territoryType, mapId);
        if (current == null)
            return new MapTacticalGraphCompareResult(false, "当前地图没有已存自定义图谱，无法对比。", 0, 0, 0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(versionId) || versionId == "__current")
            return new MapTacticalGraphCompareResult(false, "请选择一个历史版本对比。", 0, 0, 0, 0, 0, 0);

        var versionPath = FindVersionPath(territoryType, mapId, versionId);
        var other = string.IsNullOrWhiteSpace(versionPath) ? null : ReadCustomDocument(versionPath);
        if (other == null)
            return new MapTacticalGraphCompareResult(false, $"没有找到可对比的版本：{versionId}", 0, 0, 0, 0, 0, 0);

        var currentPointIds = current.Points.Select(PointCompareKey).ToHashSet(StringComparer.Ordinal);
        var otherPointIds = other.Points.Select(PointCompareKey).ToHashSet(StringComparer.Ordinal);
        var added = currentPointIds.Except(otherPointIds, StringComparer.Ordinal).Count();
        var removed = otherPointIds.Except(currentPointIds, StringComparer.Ordinal).Count();
        var changed = CountChangedPoints(current.Points, other.Points);
        var pointDelta = current.Points.Count - other.Points.Count;
        var regionDelta = current.Regions.Count - other.Regions.Count;
        var pathDelta = current.Paths.Count - other.Paths.Count;
        var message = $"当前相比版本 {versionId}：点位 {FormatSigned(pointDelta)}，区域 {FormatSigned(regionDelta)}，路径 {FormatSigned(pathDelta)}；新增点 {added}，删除点 {removed}，修改点 {changed}。";
        return new MapTacticalGraphCompareResult(true, message, pointDelta, regionDelta, pathDelta, added, removed, changed);
    }

    public TacticalGraphOperationResult MigrateCustomGraphToCurrentMapVersion(uint territoryType, uint mapId)
    {
        var document = LoadCustomDocument(territoryType, mapId);
        if (document == null)
            return new TacticalGraphOperationResult(false, "当前地图没有已存自定义图谱，无法迁移。", string.Empty);

        if (document.SourceMapSizeScale <= 0f)
            return new TacticalGraphOperationResult(false, "该图谱缺少旧地图比例/偏移元数据，无法按地图版本自动迁移。", string.Empty);

        var currentTransform = ResolveMapTransform(territoryType, mapId, mapId);
        var oldTransform = new MapTransform(
            document.SourceMapId == 0 ? currentTransform.MapId : document.SourceMapId,
            MathF.Max(0.0001f, document.SourceMapSizeScale),
            document.SourceMapOffsetX,
            document.SourceMapOffsetY,
            false);

        if (MathF.Abs(oldTransform.Scale - currentTransform.Scale) < 0.0001f
            && oldTransform.OffsetX == currentTransform.OffsetX
            && oldTransform.OffsetY == currentTransform.OffsetY
            && oldTransform.MapId == currentTransform.MapId)
        {
            return new TacticalGraphOperationResult(false, "当前图谱记录的地图版本已经与游戏地图数据一致，无需迁移。", BuildCustomPath(territoryType, mapId));
        }

        var currentPath = BuildCustomPath(territoryType, mapId);
        if (File.Exists(currentPath))
            BackupCurrentFile(territoryType, mapId, "migrate");

        MigrateDocumentCoordinates(document, oldTransform, currentTransform);
        PrepareDocumentForWrite(document, territoryType, mapId, document.MapName, true);
        document.SourceMapId = currentTransform.MapId;
        document.SourceMapSizeScale = currentTransform.Scale;
        document.SourceMapOffsetX = currentTransform.OffsetX;
        document.SourceMapOffsetY = currentTransform.OffsetY;

        File.WriteAllText(currentPath, JsonSerializer.Serialize(document, SerializerOptions));
        WriteVersionDocument(territoryType, mapId, document);
        customDocuments[BuildCustomKey(territoryType, mapId)] = document;
        cache.Clear();
        return new TacticalGraphOperationResult(true, $"已按当前地图数据迁移图谱坐标：旧 {oldTransform.MapId}/{oldTransform.Scale:0.000}/{oldTransform.OffsetX}/{oldTransform.OffsetY} -> 新 {currentTransform.MapId}/{currentTransform.Scale:0.000}/{currentTransform.OffsetX}/{currentTransform.OffsetY}", currentPath);
    }

    public TacticalGraphOperationResult ApplyCoordinateCorrectionToCustomGraph(
        uint territoryType,
        uint mapId,
        MapCoordinateCorrection correction)
    {
        var document = LoadCustomDocument(territoryType, mapId);
        if (document == null)
            return new TacticalGraphOperationResult(false, "当前地图没有已存自定义图谱，无法批量修正。", string.Empty);

        var currentPath = BuildCustomPath(territoryType, mapId);
        if (File.Exists(currentPath))
            BackupCurrentFile(territoryType, mapId, "correct");

        ApplyCoordinateCorrection(document, correction);
        PrepareDocumentForWrite(document, territoryType, mapId, document.MapName, true);
        File.WriteAllText(currentPath, JsonSerializer.Serialize(document, SerializerOptions));
        WriteVersionDocument(territoryType, mapId, document);
        customDocuments[BuildCustomKey(territoryType, mapId)] = document;
        cache.Clear();

        return new TacticalGraphOperationResult(true, $"已批量修正已存图谱：缩放 {correction.Scale:0.0000}，旋转 {RadiansToDegrees(correction.RotationRadians):+0.00;-0.00;0}°，偏移 X {correction.OffsetX:+0.00;-0.00;0} / Y {correction.OffsetY:+0.00;-0.00;0} / Z {correction.OffsetZ:+0.00;-0.00;0}。", currentPath);
    }

    private CustomMapTacticalGraphDocument? LoadCustomDocument(uint territoryType, uint mapId)
    {
        var key = BuildCustomKey(territoryType, mapId);
        if (customDocuments.TryGetValue(key, out var cached))
            return cached;

        var path = BuildCustomPath(territoryType, mapId);
        if (!File.Exists(path))
        {
            customDocuments[key] = null;
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<CustomMapTacticalGraphDocument>(File.ReadAllText(path), SerializerOptions);
            if (document != null)
                PrepareDocumentForWrite(document, territoryType, mapId, document.MapName, false);
            customDocuments[key] = document;
            return document;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MapTacticalGraph] 读取自定义战术图谱失败");
            customDocuments[key] = null;
            return null;
        }
    }

    private CustomMapTacticalGraphDocument? LoadBuiltInDocument(uint territoryType, uint mapId)
    {
        var path = ResolveBuiltInPath(territoryType, mapId);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        var key = Path.GetFileName(path);
        if (builtInDocuments.TryGetValue(key, out var cached))
            return cached;

        try
        {
            var document = JsonSerializer.Deserialize<CustomMapTacticalGraphDocument>(File.ReadAllText(path), SerializerOptions);
            if (document != null)
            {
                var resolvedTerritoryType = ResolveBuiltInTerritoryTypeIds(territoryType, mapId, document).FirstOrDefault();
                var resolvedMapId = ResolveBuiltInMapIds(territoryType, mapId, document).FirstOrDefault();
                if (resolvedTerritoryType == 0)
                    resolvedTerritoryType = document.TerritoryType != 0 ? document.TerritoryType : territoryType;
                if (resolvedMapId == 0)
                    resolvedMapId = document.MapId != 0 ? document.MapId : document.SourceMapId;
                PrepareDocumentForWrite(document, resolvedTerritoryType, resolvedMapId, document.MapName, false);
            }

            builtInDocuments[key] = document;
            return document;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MapTacticalGraph] 读取内置战术图谱失败");
            builtInDocuments[key] = null;
            return null;
        }
    }

    private CustomMapTacticalGraphDocument BuildCustomDocument(
        uint territoryType,
        uint mapId,
        string mapName,
        IReadOnlyList<MapAnnotationPoint> annotations)
    {
        var routeGroups = annotations
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new RouteAnnotationGroup(group.Key, group.OrderBy(point => point.CreatedAtUnixMs).ToArray()))
            .ToArray();
        var regionGroups = routeGroups
            .Where(group => group.Points.Length >= 3 && IsRegionExportRouteId(group.RouteId))
            .ToArray();
        var regionPointIds = regionGroups
            .SelectMany(group => group.Points.Select(point => point.Id))
            .ToHashSet(StringComparer.Ordinal);
        var pathGroups = routeGroups
            .Where(group => group.Points.Length >= 2 && !IsRegionExportRouteId(group.RouteId))
            .ToArray();

        var regions = regionGroups.Select(group => BuildCustomRegion(group.RouteId, group.Points)).ToList();
        var paths = pathGroups.Select(group => BuildCustomPath(group.RouteId, group.Points)).ToList();
        var points = annotations
            .Where(point => !regionPointIds.Contains(point.Id))
            .OrderBy(point => point.CreatedAtUnixMs)
            .Select(ClonePoint)
            .ToList();

        var transform = ResolveMapTransform(territoryType, mapId, mapId);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return new CustomMapTacticalGraphDocument
        {
            SchemaVersion = 2,
            GraphVersionId = BuildVersionId(),
            TerritoryType = territoryType,
            MapId = mapId,
            MapName = mapName,
            CreatedAtUnixMs = now,
            UpdatedAtUnixMs = now,
            SourceMapId = transform.MapId,
            SourceMapSizeScale = transform.Scale,
            SourceMapOffsetX = transform.OffsetX,
            SourceMapOffsetY = transform.OffsetY,
            Points = points,
            Regions = regions,
            Paths = paths
        };
    }

    private static CustomMapTacticalRegion BuildCustomRegion(string routeId, IReadOnlyList<MapAnnotationPoint> points)
    {
        var kind = ResolveDominantRegionKind(points);
        var label = ResolveShapeLabel(routeId, points);
        var worldY = points.Select(point => point.Y).DefaultIfEmpty(0f).Average();
        var risk = points.Select(point => point.RiskScore).DefaultIfEmpty(0).Max();
        return new CustomMapTacticalRegion
        {
            Kind = kind,
            Label = label,
            WorldY = worldY,
            RiskScore = Math.Clamp(risk, 0, 100),
            Vertices = points.OrderBy(point => point.CreatedAtUnixMs).Select(CustomMapTacticalVertex.FromPoint).ToList()
        };
    }

    private static CustomMapTacticalPath BuildCustomPath(string routeId, IReadOnlyList<MapAnnotationPoint> points)
    {
        var kind = ResolvePathKind(points);
        var label = ResolveShapeLabel(routeId, points);
        var worldY = points.Select(point => point.Y).DefaultIfEmpty(0f).Average();
        var risk = points.Select(point => point.RiskScore).DefaultIfEmpty(0).Max();
        return new CustomMapTacticalPath
        {
            Kind = kind,
            Label = label,
            RouteId = routeId,
            WorldY = worldY,
            Width = DefaultPathWidth(kind),
            RiskScore = Math.Clamp(risk, 0, 100),
            IsOneWay = IsOneWayRoute(routeId, kind),
            Points = points.OrderBy(point => point.CreatedAtUnixMs).Select(CustomMapTacticalVertex.FromPoint).ToList()
        };
    }

    private static MapAnnotationKind ResolveDominantRegionKind(IReadOnlyList<MapAnnotationPoint> points)
        => points
            .Where(point => IsRegionKind(point.Kind))
            .GroupBy(point => point.Kind)
            .OrderByDescending(group => group.Count())
            .Select(group => group.Key)
            .DefaultIfEmpty(MapAnnotationKind.Danger)
            .First();

    private static string ResolveShapeLabel(string routeId, IReadOnlyList<MapAnnotationPoint> points)
    {
        var label = points.Select(point => point.Label).FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (!string.IsNullOrWhiteSpace(label))
            return label;

        var trimmed = routeId.Trim();
        foreach (var prefix in new[] { "区域:", "区域：", "区域-", "区域_", "area:", "area-", "polygon:", "polygon-", "poly:", "poly-" })
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return trimmed[prefix.Length..].Trim();
        }

        return trimmed;
    }

    private static bool IsRegionExportRouteId(string routeId)
    {
        var text = routeId.Trim();
        return text.StartsWith("区域", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("面", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("area", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("polygon", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("poly", StringComparison.OrdinalIgnoreCase);
    }

    private CustomMapTacticalGraphDocument? ReadCustomDocument(string path)
    {
        try
        {
            var document = JsonSerializer.Deserialize<CustomMapTacticalGraphDocument>(File.ReadAllText(path), SerializerOptions);
            if (document == null)
                return null;

            PrepareDocumentForWrite(document, document.TerritoryType, document.MapId, document.MapName, false);
            return document;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MapTacticalGraph] 读取图谱版本失败");
            return null;
        }
    }

    private void PrepareDocumentForWrite(
        CustomMapTacticalGraphDocument document,
        uint territoryType,
        uint mapId,
        string mapName,
        bool createNewVersion)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        document.SchemaVersion = 2;
        document.TerritoryType = territoryType;
        document.MapId = mapId;
        if (!string.IsNullOrWhiteSpace(mapName))
            document.MapName = mapName;
        if (document.CreatedAtUnixMs <= 0)
            document.CreatedAtUnixMs = document.UpdatedAtUnixMs > 0 ? document.UpdatedAtUnixMs : now;
        if (createNewVersion || document.UpdatedAtUnixMs <= 0)
            document.UpdatedAtUnixMs = now;
        if (createNewVersion || string.IsNullOrWhiteSpace(document.GraphVersionId))
            document.GraphVersionId = BuildVersionId();
        if (createNewVersion && document.SourceMapSizeScale <= 0f)
        {
            var transform = ResolveMapTransform(territoryType, mapId, mapId);
            document.SourceMapId = transform.MapId;
            document.SourceMapSizeScale = transform.Scale;
            document.SourceMapOffsetX = transform.OffsetX;
            document.SourceMapOffsetY = transform.OffsetY;
        }
    }

    private void WriteVersionDocument(uint territoryType, uint mapId, CustomMapTacticalGraphDocument document)
    {
        PrepareDocumentForWrite(document, territoryType, mapId, document.MapName, string.IsNullOrWhiteSpace(document.GraphVersionId));
        var directory = BuildVersionDirectory(territoryType, mapId);
        Directory.CreateDirectory(directory);
        var path = BuildUniquePath(directory, SanitizeFileName(document.GraphVersionId), ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(document, SerializerOptions));
    }

    private string BackupCurrentFile(uint territoryType, uint mapId, string reason)
    {
        var sourcePath = BuildCustomPath(territoryType, mapId);
        if (!File.Exists(sourcePath))
            return string.Empty;

        var directory = BuildBackupDirectory(territoryType, mapId);
        Directory.CreateDirectory(directory);
        var path = BuildUniquePath(directory, $"{BuildVersionId()}_{SanitizeFileName(reason)}", ".json");
        File.Copy(sourcePath, path, false);
        return path;
    }

    private string? FindVersionPath(uint territoryType, uint mapId, string versionId)
    {
        var directory = BuildVersionDirectory(territoryType, mapId);
        if (!Directory.Exists(directory))
            return null;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(path), versionId, StringComparison.Ordinal))
                return path;

            var document = ReadCustomDocument(path);
            if (document != null && string.Equals(document.GraphVersionId, versionId, StringComparison.Ordinal))
                return path;
        }

        return null;
    }

    private MapTacticalGraphVersionInfo BuildVersionInfo(
        string versionId,
        string displayName,
        CustomMapTacticalGraphDocument document,
        bool isCurrent,
        string path)
        => new(
            versionId,
            displayName,
            document.CreatedAtUnixMs,
            document.UpdatedAtUnixMs,
            document.Points.Count,
            document.Regions.Count,
            document.Paths.Count,
            document.SourceMapId,
            document.SourceMapSizeScale,
            document.SourceMapOffsetX,
            document.SourceMapOffsetY,
            isCurrent,
            path);

    private void MigrateDocumentCoordinates(CustomMapTacticalGraphDocument document, MapTransform oldTransform, MapTransform newTransform)
    {
        foreach (var point in document.Points)
        {
            var texture = WorldToTexture(point.Position, oldTransform);
            point.Position = TextureToWorld(texture, point.Y, newTransform);
        }

        foreach (var region in document.Regions)
        {
            for (var i = 0; i < region.Vertices.Count; i++)
            {
                var vertex = region.Vertices[i];
                var texture = WorldToTexture(new Vector3(vertex.X, vertex.Y, vertex.Z), oldTransform);
                var world = TextureToWorld(texture, vertex.Y, newTransform);
                region.Vertices[i] = new CustomMapTacticalVertex(world.X, world.Y, world.Z);
            }
        }

        foreach (var path in document.Paths)
        {
            for (var i = 0; i < path.Points.Count; i++)
            {
                var point = path.Points[i];
                var texture = WorldToTexture(new Vector3(point.X, point.Y, point.Z), oldTransform);
                var world = TextureToWorld(texture, point.Y, newTransform);
                path.Points[i] = new CustomMapTacticalVertex(world.X, world.Y, world.Z);
            }
        }
    }

    private static void ApplyCoordinateCorrection(CustomMapTacticalGraphDocument document, MapCoordinateCorrection correction)
    {
        foreach (var point in document.Points)
        {
            var corrected = ApplyCorrection(point.Position, correction);
            point.X = corrected.X;
            point.Y = corrected.Y;
            point.Z = corrected.Z;
        }

        foreach (var region in document.Regions)
        {
            region.WorldY += correction.OffsetY;
            for (var i = 0; i < region.Vertices.Count; i++)
            {
                var vertex = region.Vertices[i];
                var corrected = ApplyCorrection(new Vector3(vertex.X, vertex.Y, vertex.Z), correction);
                region.Vertices[i] = new CustomMapTacticalVertex(corrected.X, corrected.Y, corrected.Z);
            }
        }

        foreach (var path in document.Paths)
        {
            path.WorldY += correction.OffsetY;
            for (var i = 0; i < path.Points.Count; i++)
            {
                var point = path.Points[i];
                var corrected = ApplyCorrection(new Vector3(point.X, point.Y, point.Z), correction);
                path.Points[i] = new CustomMapTacticalVertex(corrected.X, corrected.Y, corrected.Z);
            }
        }
    }

    private static Vector3 ApplyCorrection(Vector3 position, MapCoordinateCorrection correction)
    {
        var sin = MathF.Sin(correction.RotationRadians);
        var cos = MathF.Cos(correction.RotationRadians);
        var x = position.X * cos - position.Z * sin;
        var z = position.X * sin + position.Z * cos;
        return new Vector3(
            x * correction.Scale + correction.OffsetX,
            position.Y + correction.OffsetY,
            z * correction.Scale + correction.OffsetZ);
    }

    private static int CountChangedPoints(IReadOnlyList<MapAnnotationPoint> current, IReadOnlyList<MapAnnotationPoint> other)
    {
        var otherById = other
            .Where(point => !string.IsNullOrWhiteSpace(point.Id))
            .GroupBy(point => point.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var changed = 0;
        foreach (var point in current)
        {
            if (string.IsNullOrWhiteSpace(point.Id) || !otherById.TryGetValue(point.Id, out var old))
                continue;
            if (point.Kind != old.Kind
                || point.Label != old.Label
                || point.RouteId != old.RouteId
                || MathF.Abs(point.X - old.X) > 0.01f
                || MathF.Abs(point.Y - old.Y) > 0.01f
                || MathF.Abs(point.Z - old.Z) > 0.01f
                || MathF.Abs(point.Radius - old.Radius) > 0.01f
                || point.RiskScore != old.RiskScore)
            {
                changed++;
            }
        }

        return changed;
    }

    private static string PointCompareKey(MapAnnotationPoint point)
        => string.IsNullOrWhiteSpace(point.Id)
            ? $"{point.Kind}:{point.Label}:{point.RouteId}:{point.CreatedAtUnixMs}:{point.X:0.00}:{point.Z:0.00}"
            : point.Id;

    private string BuildVersionDirectory(uint territoryType, uint mapId)
        => Path.Combine(rootDirectory, "Versions", BuildCustomKey(territoryType, mapId));

    private string BuildBackupDirectory(uint territoryType, uint mapId)
        => Path.Combine(rootDirectory, "Backups", BuildCustomKey(territoryType, mapId));

    private static string BuildVersionId()
        => DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);

    private static string BuildUniquePath(string directory, string stem, string extension)
    {
        var safeStem = string.IsNullOrWhiteSpace(stem) ? BuildVersionId() : stem;
        var path = Path.Combine(directory, $"{safeStem}{extension}");
        if (!File.Exists(path))
            return path;

        for (var i = 1; i < 1000; i++)
        {
            path = Path.Combine(directory, $"{safeStem}_{i:000}{extension}");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(directory, $"{safeStem}_{Guid.NewGuid():N}{extension}");
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var text = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(text) ? BuildVersionId() : text;
    }

    private static string FormatSigned(int value)
        => value > 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);

    private static float RadiansToDegrees(float radians)
        => radians * 180f / MathF.PI;

    private string BuildCustomPath(uint territoryType, uint mapId)
        => Path.Combine(rootDirectory, $"{BuildCustomKey(territoryType, mapId)}.json");

    private static string BuildCustomKey(uint territoryType, uint mapId)
        => $"map_{territoryType}_{mapId}";

    private string? ResolveBuiltInPath(uint territoryType, uint mapId)
    {
        if (territoryType != 0 && mapId != 0)
        {
            var exactPath = Path.Combine(builtInDirectory, $"{BuildCustomKey(territoryType, mapId)}.json");
            if (File.Exists(exactPath))
                return exactPath;
        }

        var fileName = ResolveBuiltInFileName(territoryType, mapId);
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        var path = Path.Combine(builtInDirectory, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string? ResolveBuiltInFileName(uint territoryType, uint mapId)
        => (territoryType, mapId) switch
        {
            (376, _) or (_, 167) => "map_376_167.json",
            (431, _) or (_, 242) => "map_431_242.json",
            (554, _) or (_, 296) => "map_554_296.json",
            (888, _) or (_, 568) => "map_888_568.json",
            (1313, _) or (_, 1119) => "map_1313_1119.json",
            _ => null
        };

    private MapTacticalGraphSnapshot BuildSnapshot(
        BuiltInTacticalGraphDefinition? definition,
        CustomMapTacticalGraphDocument? customDocument,
        uint territoryType,
        MapTransform transform)
    {
        var builtInPoints = definition?.Points
            .Select((point, index) => ToAnnotationPoint(definition, point, index, transform))
            .ToArray() ?? Array.Empty<MapAnnotationPoint>();
        var builtInRegions = definition == null
            ? Array.Empty<MapTacticalRegionSnapshot>()
            : BuildRegions(definition, builtInPoints, transform);
        var builtInPaths = definition == null
            ? Array.Empty<MapTacticalPathSnapshot>()
            : BuildPaths(definition, builtInPoints, transform);

        var customPoints = customDocument?.Points
            .Select((point, index) => ToCustomPoint(point, index))
            .ToArray();
        customPoints ??= Array.Empty<MapAnnotationPoint>();
        var customRegions = BuildCustomRegions(customDocument, customPoints);
        var customPaths = BuildCustomPaths(customDocument, customPoints);

        var points = builtInPoints.Concat(customPoints).ToArray();
        var regions = builtInRegions.Concat(customRegions).ToArray();
        var paths = builtInPaths.Concat(customPaths).ToArray();
        var analysisPoints = BuildAnalysisPoints(points, regions);

        var snapshot = new MapTacticalGraphSnapshot
        {
            IsAvailable = true,
            MapType = definition?.MapType ?? FrontlineMapType.Unknown,
            TerritoryType = territoryType,
            MapId = transform.MapId,
            TerritoryTypeIds = definition?.TerritoryTypeIds.ToArray() ?? new[] { territoryType },
            MapIds = definition?.MapIds.ToArray() ?? new[] { transform.MapId },
            MapName = ResolveGraphMapName(definition?.MapName, customDocument?.MapName),
            Version = customDocument == null ? BuiltInVersion : $"{BuiltInVersion} + {CustomVersion}",
            SourceText = BuildSourceText(definition, customDocument, transform, points.Length, regions.Length, paths.Length),
            CoverageText = BuildCoverageText(points, regions, paths),
            UsesFallbackProjection = transform.UsesFallbackProjection,
            Points = points,
            AnalysisPoints = analysisPoints,
            Regions = regions,
            Paths = paths
        };

        return snapshot;
    }

    private MapTransform ResolveMapTransform(BuiltInTacticalGraphDefinition definition, uint territoryType, uint mapId)
        => ResolveMapTransform(territoryType, mapId, definition.MapIds.FirstOrDefault());

    private MapTransform ResolveMapTransform(uint territoryType, uint mapId, uint fallbackMapId)
    {
        var effectiveMapId = mapId;

        try
        {
            var territorySheet = dataManager.GetExcelSheet<TerritoryTypeSheet>();
            if (territoryType != 0
                && territorySheet.TryGetRow(territoryType, out var territory)
                && territory.Map.RowId != 0)
            {
                effectiveMapId = territory.Map.RowId;
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MapTacticalGraph] 读取 TerritoryType 地图映射失败");
        }

        if (effectiveMapId == 0)
            effectiveMapId = fallbackMapId;

        try
        {
            var mapSheet = dataManager.GetExcelSheet<MapSheet>();
            if (effectiveMapId != 0 && mapSheet.TryGetRow(effectiveMapId, out var map))
            {
                return new MapTransform(
                    effectiveMapId,
                    MathF.Max(0.0001f, map.SizeFactor / 100f),
                    map.OffsetX,
                    map.OffsetY,
                    false);
            }
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[MapTacticalGraph] 读取 Map 坐标映射失败");
        }

        return new MapTransform(effectiveMapId, 1f, 0, 0, true);
    }

    private static BuiltInTacticalGraphDefinition? ResolveDefinition(uint territoryType, uint mapId)
    {
        foreach (var definition in Graphs)
        {
            if (territoryType != 0 && definition.TerritoryTypeIds.Contains(territoryType))
                return definition;

            if (mapId != 0 && definition.MapIds.Contains(mapId))
                return definition;
        }

        return null;
    }

    private static BuiltInTacticalGraphDefinition BuildDefinitionFromDocument(
        CustomMapTacticalGraphDocument document,
        uint[] territoryTypeIds,
        uint[] mapIds,
        MapTransform transform)
    {
        var territoryType = territoryTypeIds.FirstOrDefault();
        if (territoryType == 0)
            territoryType = document.TerritoryType;

        var mapId = mapIds.FirstOrDefault();
        if (mapId == 0)
            mapId = document.MapId != 0 ? document.MapId : transform.MapId;

        var mapName = string.IsNullOrWhiteSpace(document.MapName) ? BuildCustomKey(territoryType, mapId) : document.MapName;
        return new BuiltInTacticalGraphDefinition(
            ResolveBuiltInMapType(territoryType, mapId),
            BuildCustomKey(territoryType, mapId),
            mapName,
            territoryTypeIds,
            mapIds,
            "当前地图使用内置战术图谱。",
            document.Points.Select(point => ToBuiltInPoint(point, transform)).ToArray(),
            document.Regions.Select(region => ToBuiltInRegion(region, transform)).ToArray(),
            document.Paths.Select(path => ToBuiltInPath(path, transform)).ToArray());
    }

    private static BuiltInTacticalPoint ToBuiltInPoint(MapAnnotationPoint point, MapTransform transform)
    {
        var texture = WorldToTexture(point.Position, transform);
        return new BuiltInTacticalPoint(
            point.Kind,
            point.Label,
            point.RouteId,
            texture.X,
            texture.Y,
            point.Y,
            Math.Clamp(point.Radius, 0f, 220f),
            Math.Clamp(point.RiskScore, 0, 100));
    }

    private static BuiltInTacticalRegion ToBuiltInRegion(CustomMapTacticalRegion region, MapTransform transform)
        => new(
            region.Kind,
            region.Label,
            region.Vertices.Select(vertex => WorldToTexture(vertex.ToVector3(region.WorldY), transform)).ToArray(),
            region.WorldY,
            Math.Clamp(region.RiskScore, 0, 100));

    private static BuiltInTacticalPath ToBuiltInPath(CustomMapTacticalPath path, MapTransform transform)
        => new(
            path.Kind,
            path.Label,
            path.RouteId,
            path.Points.Select(point => WorldToTexture(point.ToVector3(path.WorldY), transform)).ToArray(),
            path.WorldY,
            Math.Clamp(path.Width, 1f, 80f),
            Math.Clamp(path.RiskScore, 0, 100));

    private static uint[] ResolveBuiltInTerritoryTypeIds(uint territoryType, uint mapId, CustomMapTacticalGraphDocument? document = null)
    {
        if (territoryType == 376 || document?.TerritoryType == 376 || mapId == 167 || document?.MapId == 167 || document?.SourceMapId == 167)
            return new[] { 376u };
        if (territoryType == 431 || document?.TerritoryType == 431 || mapId == 242 || document?.MapId == 242 || document?.SourceMapId == 242)
            return new[] { 431u };
        if (territoryType == 554 || document?.TerritoryType == 554 || mapId == 296 || document?.MapId == 296 || document?.SourceMapId == 296)
            return new[] { 554u };
        if (territoryType == 888 || document?.TerritoryType == 888 || mapId == 568 || document?.MapId == 568 || document?.SourceMapId == 568)
            return new[] { 888u };
        if (territoryType == 1313 || document?.TerritoryType == 1313 || mapId == 1119 || document?.MapId == 1119 || document?.SourceMapId == 1119)
            return new[] { 1313u };

        var fallback = document?.TerritoryType ?? territoryType;
        return fallback == 0 ? Array.Empty<uint>() : new[] { fallback };
    }

    private static uint[] ResolveBuiltInMapIds(uint territoryType, uint mapId, CustomMapTacticalGraphDocument? document = null)
    {
        if (territoryType == 376 || document?.TerritoryType == 376 || mapId == 167 || document?.MapId == 167 || document?.SourceMapId == 167)
            return new[] { 167u };
        if (territoryType == 431 || document?.TerritoryType == 431 || mapId == 242 || document?.MapId == 242 || document?.SourceMapId == 242)
            return new[] { 242u };
        if (territoryType == 554 || document?.TerritoryType == 554 || mapId == 296 || document?.MapId == 296 || document?.SourceMapId == 296)
            return new[] { 296u };
        if (territoryType == 888 || document?.TerritoryType == 888 || mapId == 568 || document?.MapId == 568 || document?.SourceMapId == 568)
            return new[] { 568u };
        if (territoryType == 1313 || document?.TerritoryType == 1313 || mapId == 1119 || document?.MapId == 1119 || document?.SourceMapId == 1119)
            return new[] { 1119u };

        var fallback = document?.MapId ?? mapId;
        if (fallback == 0)
            fallback = document?.SourceMapId ?? 0;
        return fallback == 0 ? Array.Empty<uint>() : new[] { fallback };
    }

    private static FrontlineMapType ResolveBuiltInMapType(uint territoryType, uint mapId)
    {
        if (territoryType == 376 || mapId == 167)
            return FrontlineMapType.BorderlandRuinsSecure;
        if (territoryType == 431 || mapId == 242)
            return FrontlineMapType.SealRock;
        if (territoryType == 554 || mapId == 296)
            return FrontlineMapType.FieldsOfHonor;
        if (territoryType == 888 || mapId == 568)
            return FrontlineMapType.OnsalHakair;
        if (territoryType == 1313 || mapId == 1119)
            return FrontlineMapType.Vochester;
        return FrontlineMapType.Unknown;
    }

    private static bool IsPromotedBuiltInShadow(
        CustomMapTacticalGraphDocument builtInDocument,
        CustomMapTacticalGraphDocument customDocument)
    {
        if (!string.IsNullOrWhiteSpace(builtInDocument.GraphVersionId)
            && string.Equals(builtInDocument.GraphVersionId, customDocument.GraphVersionId, StringComparison.Ordinal))
        {
            return true;
        }

        return builtInDocument.TerritoryType == customDocument.TerritoryType
            && builtInDocument.MapId == customDocument.MapId
            && builtInDocument.SourceMapId == customDocument.SourceMapId
            && MathF.Abs(builtInDocument.SourceMapSizeScale - customDocument.SourceMapSizeScale) < 0.0001f
            && builtInDocument.SourceMapOffsetX == customDocument.SourceMapOffsetX
            && builtInDocument.SourceMapOffsetY == customDocument.SourceMapOffsetY
            && builtInDocument.UpdatedAtUnixMs == customDocument.UpdatedAtUnixMs
            && builtInDocument.Points.Count == customDocument.Points.Count
            && builtInDocument.Regions.Count == customDocument.Regions.Count
            && builtInDocument.Paths.Count == customDocument.Paths.Count;
    }

    private static MapAnnotationPoint ToAnnotationPoint(
        BuiltInTacticalGraphDefinition definition,
        BuiltInTacticalPoint point,
        int index,
        MapTransform transform)
    {
        var worldX = (point.TextureX - 1024f) / transform.Scale - transform.OffsetX;
        var worldZ = (point.TextureY - 1024f) / transform.Scale - transform.OffsetY;

        var annotation = new MapAnnotationPoint
        {
            Id = $"{BuiltInPointPrefix}{definition.Key}.{index:000}",
            Kind = point.Kind,
            Label = point.Label,
            RouteId = point.RouteId,
            X = worldX,
            Y = point.WorldY,
            Z = worldZ,
            Radius = Math.Clamp(point.Radius, 0f, 220f),
            RiskScore = Math.Clamp(point.RiskScore, 0, 100),
            CreatedAtUnixMs = index + 1
        };

        BuiltInMapPointTuning.Apply(definition.MapType, annotation);
        return annotation;
    }

    private static MapAnnotationPoint ToCustomPoint(MapAnnotationPoint point, int index)
    {
        var clone = ClonePoint(point);
        clone.Id = string.IsNullOrWhiteSpace(point.Id)
            ? $"{CustomPointPrefix}{index:000}"
            : $"{CustomPointPrefix}{point.Id}";
        clone.CreatedAtUnixMs = point.CreatedAtUnixMs <= 0 ? index + 1 : point.CreatedAtUnixMs;
        return clone;
    }

    private static MapTacticalRegionSnapshot[] BuildRegions(
        BuiltInTacticalGraphDefinition definition,
        IReadOnlyList<MapAnnotationPoint> points,
        MapTransform transform)
    {
        var regions = new List<MapTacticalRegionSnapshot>();

        foreach (var region in definition.Regions ?? Array.Empty<BuiltInTacticalRegion>())
            regions.Add(ToRegion(definition, region, regions.Count, transform));

        foreach (var point in points.Where(point => IsRegionKind(point.Kind)))
            regions.Add(ToRegionFromPoint(definition, point, regions.Count));

        return regions.ToArray();
    }

    private static MapTacticalPathSnapshot[] BuildPaths(
        BuiltInTacticalGraphDefinition definition,
        IReadOnlyList<MapAnnotationPoint> points,
        MapTransform transform)
    {
        var paths = new List<MapTacticalPathSnapshot>();

        var explicitRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in definition.Paths ?? Array.Empty<BuiltInTacticalPath>())
        {
            paths.Add(ToPath(definition, path, paths.Count, transform));
            if (!string.IsNullOrWhiteSpace(path.RouteId))
                explicitRouteIds.Add(path.RouteId.Trim());
        }

        foreach (var group in points
                     .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
                     .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (explicitRouteIds.Contains(group.Key))
                continue;

            var routePoints = group.OrderBy(point => point.CreatedAtUnixMs).ToArray();
            if (routePoints.Length < 2)
                continue;

            var kind = routePoints.Any(point => point.Kind == MapAnnotationKind.Flank)
                ? MapAnnotationKind.Flank
                : ResolvePathKind(routePoints);
            var risk = routePoints.Select(point => point.RiskScore).DefaultIfEmpty(0).Max();
            paths.Add(new MapTacticalPathSnapshot
            {
                Id = $"{BuiltInPointPrefix}{definition.Key}.path.{paths.Count:000}",
                Kind = kind,
                Label = group.Key,
                RouteId = group.Key,
                Points = routePoints.Select(point => point.Position).ToArray(),
                Width = DefaultPathWidth(kind),
                RiskScore = risk,
                IsOneWay = IsOneWayRoute(group.Key, kind),
                SourceText = "由内置路径节点生成"
            });
        }

        return paths.ToArray();
    }

    private static MapTacticalRegionSnapshot[] BuildCustomRegions(
        CustomMapTacticalGraphDocument? document,
        IReadOnlyList<MapAnnotationPoint> points)
    {
        var regions = new List<MapTacticalRegionSnapshot>();
        if (document != null)
        {
            foreach (var region in document.Regions)
                regions.Add(ToCustomRegion(region, regions.Count));
        }

        foreach (var point in points.Where(point => IsRegionKind(point.Kind)))
            regions.Add(ToCustomRegionFromPoint(point, regions.Count));

        return regions.ToArray();
    }

    private static MapTacticalPathSnapshot[] BuildCustomPaths(
        CustomMapTacticalGraphDocument? document,
        IReadOnlyList<MapAnnotationPoint> points)
    {
        var paths = new List<MapTacticalPathSnapshot>();
        var explicitRouteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (document != null)
        {
            foreach (var path in document.Paths)
            {
                paths.Add(ToCustomPath(path, paths.Count));
                if (!string.IsNullOrWhiteSpace(path.RouteId))
                    explicitRouteIds.Add(path.RouteId.Trim());
            }
        }

        foreach (var group in points
                     .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
                     .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            if (explicitRouteIds.Contains(group.Key))
                continue;

            var routePoints = group.OrderBy(point => point.CreatedAtUnixMs).ToArray();
            if (routePoints.Length < 2)
                continue;

            var kind = ResolvePathKind(routePoints);
            var risk = routePoints.Select(point => point.RiskScore).DefaultIfEmpty(0).Max();
            paths.Add(new MapTacticalPathSnapshot
            {
                Id = $"{CustomPointPrefix}path.{paths.Count:000}",
                Kind = kind,
                Label = group.Key,
                RouteId = group.Key,
                Points = routePoints.Select(point => point.Position).ToArray(),
                Width = DefaultPathWidth(kind),
                RiskScore = risk,
                IsOneWay = IsOneWayRoute(group.Key, kind),
                SourceText = "由自定义路径节点生成"
            });
        }

        return paths.ToArray();
    }

    private static MapAnnotationPoint[] BuildAnalysisPoints(
        IReadOnlyList<MapAnnotationPoint> points,
        IReadOnlyList<MapTacticalRegionSnapshot> regions)
    {
        if (regions.Count == 0)
            return points.ToArray();

        var result = new List<MapAnnotationPoint>(points.Count + regions.Count);
        result.AddRange(points);
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            result.Add(new MapAnnotationPoint
            {
                Id = $"{region.Id}.analysis",
                Kind = region.Kind,
                Label = region.Label,
                RouteId = string.Empty,
                Position = region.Center,
                Radius = region.Radius,
                RiskScore = region.RiskScore,
                CreatedAtUnixMs = 10000 + i
            });
        }

        return result.ToArray();
    }

    private static MapTacticalRegionSnapshot ToRegion(
        BuiltInTacticalGraphDefinition definition,
        BuiltInTacticalRegion region,
        int index,
        MapTransform transform)
    {
        var vertices = region.Vertices.Select(vertex => TextureToWorld(vertex, region.WorldY, transform)).ToArray();
        var center = CalculateCenter(vertices);
        return new MapTacticalRegionSnapshot
        {
            Id = $"{BuiltInPointPrefix}{definition.Key}.region.{index:000}",
            Kind = region.Kind,
            Label = region.Label,
            Center = center,
            Vertices = vertices,
            Radius = CalculateRadius(center, vertices),
            RiskScore = Math.Clamp(region.RiskScore, 0, 100),
            SourceText = "内置多边形区域"
        };
    }

    private static MapTacticalRegionSnapshot ToRegionFromPoint(
        BuiltInTacticalGraphDefinition definition,
        MapAnnotationPoint point,
        int index)
    {
        var radius = Math.Clamp(point.Radius <= 0f ? 18f : point.Radius, 4f, 220f);
        var vertices = BuildRegularRegion(point.Position, radius, point.Kind == MapAnnotationKind.Choke ? 8 : 12);
        return new MapTacticalRegionSnapshot
        {
            Id = $"{BuiltInPointPrefix}{definition.Key}.region.{index:000}",
            Kind = point.Kind,
            Label = point.Label,
            Center = point.Position,
            Vertices = vertices,
            Radius = radius,
            RiskScore = Math.Clamp(point.RiskScore, 0, 100),
            SourceText = "由内置区域节点生成"
        };
    }

    private static MapTacticalRegionSnapshot ToCustomRegion(CustomMapTacticalRegion region, int index)
    {
        var vertices = region.Vertices.Select(vertex => vertex.ToVector3(region.WorldY)).ToArray();
        var center = CalculateCenter(vertices);
        return new MapTacticalRegionSnapshot
        {
            Id = $"{CustomPointPrefix}region.{index:000}",
            Kind = region.Kind,
            Label = region.Label,
            Center = center,
            Vertices = vertices,
            Radius = CalculateRadius(center, vertices),
            RiskScore = Math.Clamp(region.RiskScore, 0, 100),
            SourceText = "自定义多边形区域"
        };
    }

    private static MapTacticalRegionSnapshot ToCustomRegionFromPoint(MapAnnotationPoint point, int index)
    {
        var radius = Math.Clamp(point.Radius <= 0f ? 18f : point.Radius, 4f, 220f);
        var vertices = BuildRegularRegion(point.Position, radius, point.Kind == MapAnnotationKind.Choke ? 8 : 12);
        return new MapTacticalRegionSnapshot
        {
            Id = $"{CustomPointPrefix}region.{index:000}",
            Kind = point.Kind,
            Label = point.Label,
            Center = point.Position,
            Vertices = vertices,
            Radius = radius,
            RiskScore = Math.Clamp(point.RiskScore, 0, 100),
            SourceText = "由自定义区域节点生成"
        };
    }

    private static MapTacticalPathSnapshot ToPath(
        BuiltInTacticalGraphDefinition definition,
        BuiltInTacticalPath path,
        int index,
        MapTransform transform)
        => new()
        {
            Id = $"{BuiltInPointPrefix}{definition.Key}.path.{index:000}",
            Kind = path.Kind,
            Label = path.Label,
            RouteId = path.RouteId,
            Points = path.Points.Select(point => TextureToWorld(point, path.WorldY, transform)).ToArray(),
            Width = Math.Clamp(path.Width, 1f, 80f),
            RiskScore = Math.Clamp(path.RiskScore, 0, 100),
            IsOneWay = IsOneWayRoute(path.RouteId, path.Kind),
            SourceText = "内置折线路径"
        };

    private static MapTacticalPathSnapshot ToCustomPath(CustomMapTacticalPath path, int index)
        => new()
        {
            Id = $"{CustomPointPrefix}path.{index:000}",
            Kind = path.Kind,
            Label = path.Label,
            RouteId = path.RouteId,
            Points = path.Points.Select(point => point.ToVector3(path.WorldY)).ToArray(),
            Width = Math.Clamp(path.Width, 1f, 80f),
            RiskScore = Math.Clamp(path.RiskScore, 0, 100),
            IsOneWay = path.IsOneWay || IsOneWayRoute(path.RouteId, path.Kind),
            SourceText = "自定义折线路径"
        };

    private static Vector3 TextureToWorld(Vector2 texturePosition, float worldY, MapTransform transform)
    {
        var worldX = (texturePosition.X - 1024f) / transform.Scale - transform.OffsetX;
        var worldZ = (texturePosition.Y - 1024f) / transform.Scale - transform.OffsetY;
        return new Vector3(worldX, worldY, worldZ);
    }

    private static Vector2 WorldToTexture(Vector3 worldPosition, MapTransform transform)
        => new Vector2(worldPosition.X, worldPosition.Z) * transform.Scale
            + new Vector2(transform.OffsetX, transform.OffsetY) * transform.Scale
            + new Vector2(1024f);

    private static Vector3[] BuildRegularRegion(Vector3 center, float radius, int sides)
    {
        sides = Math.Clamp(sides, 3, 24);
        var vertices = new Vector3[sides];
        for (var i = 0; i < sides; i++)
        {
            var angle = MathF.PI * 2f * i / sides - MathF.PI / 2f;
            vertices[i] = new Vector3(
                center.X + MathF.Cos(angle) * radius,
                center.Y,
                center.Z + MathF.Sin(angle) * radius);
        }

        return vertices;
    }

    private static Vector3 CalculateCenter(IReadOnlyList<Vector3> vertices)
    {
        if (vertices.Count == 0)
            return default;

        var sum = Vector3.Zero;
        foreach (var vertex in vertices)
            sum += vertex;
        return sum / vertices.Count;
    }

    private static float CalculateRadius(Vector3 center, IReadOnlyList<Vector3> vertices)
    {
        var radius = 0f;
        foreach (var vertex in vertices)
        {
            var dx = vertex.X - center.X;
            var dz = vertex.Z - center.Z;
            radius = MathF.Max(radius, MathF.Sqrt(dx * dx + dz * dz));
        }

        return radius;
    }

    private static MapTacticalGraphSnapshot CloneSnapshot(MapTacticalGraphSnapshot source)
        => new()
        {
            IsAvailable = source.IsAvailable,
            MapType = source.MapType,
            TerritoryType = source.TerritoryType,
            MapId = source.MapId,
            TerritoryTypeIds = source.TerritoryTypeIds.ToArray(),
            MapIds = source.MapIds.ToArray(),
            MapName = source.MapName,
            Version = source.Version,
            SourceText = source.SourceText,
            CoverageText = source.CoverageText,
            UsesFallbackProjection = source.UsesFallbackProjection,
            Points = source.Points.Select(ClonePoint).ToArray(),
            AnalysisPoints = source.AnalysisPoints.Select(ClonePoint).ToArray(),
            Regions = source.Regions.Select(CloneRegion).ToArray(),
            Paths = source.Paths.Select(ClonePath).ToArray()
        };

    private static MapAnnotationPoint ClonePoint(MapAnnotationPoint point)
        => new()
        {
            Id = point.Id,
            Kind = point.Kind,
            Label = point.Label,
            RouteId = point.RouteId,
            X = point.X,
            Y = point.Y,
            Z = point.Z,
            Radius = point.Radius,
            RiskScore = point.RiskScore,
            CreatedAtUnixMs = point.CreatedAtUnixMs
        };

    private static MapTacticalRegionSnapshot CloneRegion(MapTacticalRegionSnapshot region)
        => new()
        {
            Id = region.Id,
            Kind = region.Kind,
            Label = region.Label,
            Center = region.Center,
            Vertices = region.Vertices.ToArray(),
            Radius = region.Radius,
            RiskScore = region.RiskScore,
            SourceText = region.SourceText
        };

    private static MapTacticalPathSnapshot ClonePath(MapTacticalPathSnapshot path)
        => new()
        {
            Id = path.Id,
            Kind = path.Kind,
            Label = path.Label,
            RouteId = path.RouteId,
            Points = path.Points.ToArray(),
            Width = path.Width,
            RiskScore = path.RiskScore,
            IsOneWay = path.IsOneWay,
            SourceText = path.SourceText
        };

    private static string ResolveGraphMapName(string? builtInMapName, string? customMapName)
    {
        if (!string.IsNullOrWhiteSpace(customMapName))
            return customMapName;
        return builtInMapName ?? string.Empty;
    }

    private static string BuildSourceText(
        BuiltInTacticalGraphDefinition? definition,
        CustomMapTacticalGraphDocument? customDocument,
        MapTransform transform,
        int pointCount,
        int regionCount,
        int pathCount)
    {
        var projection = transform.UsesFallbackProjection
            ? "地图坐标表不可用，使用临时投影"
            : $"地图:{transform.MapId} 比例:{transform.Scale:0.00} 偏移:{transform.OffsetX}/{transform.OffsetY}";
        var name = ResolveGraphMapName(definition?.MapName, customDocument?.MapName);
        var customText = customDocument == null ? string.Empty : $"；已加载自定义图谱，保存时间 {customDocument.UpdatedAtUnixMs}";
        var note = definition?.Note ?? "当前地图使用自定义战术图谱。";
        return $"{BuiltInVersion}：{name}，节点 {pointCount}，区域 {regionCount}，通行图形 {pathCount}，{projection}{customText}。{note}";
    }

    private static string BuildCoverageText(
        IReadOnlyCollection<MapAnnotationPoint> points,
        IReadOnlyCollection<MapTacticalRegionSnapshot> regions,
        IReadOnlyCollection<MapTacticalPathSnapshot> paths)
    {
        var routeCount = points
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .Select(point => point.RouteId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var chokeCount = points.Count(point => point.Kind == MapAnnotationKind.Choke);
        var heightCount = points.Count(point => point.Kind is MapAnnotationKind.HighGround or MapAnnotationKind.LowGround);
        var jumpPadCount = points.Count(point => point.Kind == MapAnnotationKind.JumpPad);
        var teleporterCount = points.Count(point => point.Kind == MapAnnotationKind.Teleporter);
        var flankRouteCount = points
            .Where(point => point.Kind == MapAnnotationKind.Flank && !string.IsNullOrWhiteSpace(point.RouteId))
            .Select(point => point.RouteId.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var dangerCount = points.Count(point => point.Kind == MapAnnotationKind.Danger);
        var oneWayCount = paths.Count(path => path.IsOneWay);

        return $"覆盖：图形区域 {regions.Count}，通行图形 {paths.Count}，通行分段 {routeCount}，卡口 {chokeCount}，高低差 {heightCount}，跳台 {jumpPadCount}，传送 {teleporterCount}，侧翼入口 {flankRouteCount}，危险区 {dangerCount}，单向通道 {oneWayCount}";
    }

    private static bool IsRegionKind(MapAnnotationKind kind)
        => kind is MapAnnotationKind.Choke
            or MapAnnotationKind.HighGround
            or MapAnnotationKind.LowGround
            or MapAnnotationKind.Danger
            or MapAnnotationKind.Bridge
            or MapAnnotationKind.Underpass;

    private static MapAnnotationKind ResolvePathKind(IReadOnlyList<MapAnnotationPoint> points)
    {
        if (points.Any(point => point.Kind == MapAnnotationKind.JumpPad))
            return MapAnnotationKind.JumpPad;
        if (points.Any(point => point.Kind == MapAnnotationKind.Bridge))
            return MapAnnotationKind.Bridge;
        if (points.Any(point => point.Kind == MapAnnotationKind.Teleporter))
            return MapAnnotationKind.Teleporter;
        if (points.Any(point => point.Kind == MapAnnotationKind.Flank))
            return MapAnnotationKind.Flank;
        if (points.Any(point => point.Kind == MapAnnotationKind.Rotation))
            return MapAnnotationKind.Rotation;
        return MapAnnotationKind.RoutePoint;
    }

    private static float DefaultPathWidth(MapAnnotationKind kind)
        => kind switch
        {
            MapAnnotationKind.Bridge => 16f,
            MapAnnotationKind.JumpPad => 14f,
            MapAnnotationKind.Teleporter => 12f,
            MapAnnotationKind.Flank => 11f,
            MapAnnotationKind.Choke => 10f,
            _ => 9f,
        };

    private static bool IsOneWayRoute(string routeId, MapAnnotationKind kind)
    {
        if (kind == MapAnnotationKind.JumpPad)
            return true;

        var text = routeId ?? string.Empty;
        return text.Contains("单向", StringComparison.OrdinalIgnoreCase)
            || text.Contains("不可回", StringComparison.OrdinalIgnoreCase)
            || text.Contains("跳下", StringComparison.OrdinalIgnoreCase)
            || text.Contains("oneway", StringComparison.OrdinalIgnoreCase)
            || text.Contains("one-way", StringComparison.OrdinalIgnoreCase);
    }

    private static BuiltInTacticalPoint P(
        MapAnnotationKind kind,
        string label,
        string routeId,
        float textureX,
        float textureY,
        float worldY,
        float radius,
        int riskScore)
        => new(kind, label, routeId, textureX, textureY, worldY, radius, riskScore);

    private static BuiltInTacticalRegion R(
        MapAnnotationKind kind,
        string label,
        float worldY,
        int riskScore,
        params float[] textureCoordinates)
        => new(kind, label, BuildTextureVertices(textureCoordinates), worldY, riskScore);

    private static BuiltInTacticalPath L(
        MapAnnotationKind kind,
        string label,
        string routeId,
        float worldY,
        float width,
        int riskScore,
        params float[] textureCoordinates)
        => new(kind, label, routeId, BuildTextureVertices(textureCoordinates), worldY, width, riskScore);

    private static Vector2[] BuildTextureVertices(float[] textureCoordinates)
    {
        if (textureCoordinates.Length < 4)
            return Array.Empty<Vector2>();

        var count = textureCoordinates.Length / 2;
        var vertices = new Vector2[count];
        for (var i = 0; i < count; i++)
            vertices[i] = new Vector2(textureCoordinates[i * 2], textureCoordinates[i * 2 + 1]);
        return vertices;
    }

    private static readonly BuiltInTacticalGraphDefinition[] Graphs = Array.Empty<BuiltInTacticalGraphDefinition>();

    private sealed record BuiltInTacticalGraphDefinition(
        FrontlineMapType MapType,
        string Key,
        string MapName,
        uint[] TerritoryTypeIds,
        uint[] MapIds,
        string Note,
        BuiltInTacticalPoint[] Points,
        BuiltInTacticalRegion[]? Regions = null,
        BuiltInTacticalPath[]? Paths = null);

    private readonly record struct BuiltInTacticalPoint(
        MapAnnotationKind Kind,
        string Label,
        string RouteId,
        float TextureX,
        float TextureY,
        float WorldY,
        float Radius,
        int RiskScore);

    private readonly record struct BuiltInTacticalRegion(
        MapAnnotationKind Kind,
        string Label,
        Vector2[] Vertices,
        float WorldY,
        int RiskScore);

    private readonly record struct BuiltInTacticalPath(
        MapAnnotationKind Kind,
        string Label,
        string RouteId,
        Vector2[] Points,
        float WorldY,
        float Width,
        int RiskScore);

    private readonly record struct MapTransform(
        uint MapId,
        float Scale,
        int OffsetX,
        int OffsetY,
        bool UsesFallbackProjection);
}

