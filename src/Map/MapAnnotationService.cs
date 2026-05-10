using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ai02;

public enum MapAnnotationKind
{
    Spawn,
    Objective,
    Choke,
    JumpPad,
    Teleporter,
    HighGround,
    LowGround,
    Danger,
    Rotation,
    Flank,
    RoutePoint,
    Note,
    Bridge,
    Underpass
}

public sealed class MapAnnotationPoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MapAnnotationKind Kind { get; set; } = MapAnnotationKind.Note;
    public string Label { get; set; } = string.Empty;
    public string RouteId { get; set; } = string.Empty;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Radius { get; set; } = 18f;
    public int RiskScore { get; set; }
    public long CreatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonIgnore]
    public Vector3 Position
    {
        get => new(X, Y, Z);
        set
        {
            X = value.X;
            Y = value.Y;
            Z = value.Z;
        }
    }
}

public sealed class MapAnnotationDocument
{
    public uint TerritoryType { get; set; }
    public uint MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public long UpdatedAtUnixMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public List<MapAnnotationPoint> Points { get; set; } = new();
}

public sealed class MapAnnotationRouteSummary
{
    public string RouteId { get; init; } = string.Empty;
    public int PointCount { get; init; }
    public float Distance { get; init; }
    public int MountedEtaSeconds { get; init; }
    public int OnFootEtaSeconds { get; init; }
    public int MaxRiskScore { get; init; }
    public float? DistanceFromLocalToStart { get; init; }
    public int? LocalToStartEtaSeconds { get; init; }
    public string KindSummary { get; init; } = string.Empty;
}

public sealed class MapAnnotationService
{
    private const float MountedYalmsPerSecond = 10.0f;
    private const float OnFootYalmsPerSecond = 6.0f;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string rootDirectory;
    private readonly Dictionary<string, MapAnnotationDocument> documents = new(StringComparer.Ordinal);

    public MapAnnotationService()
    {
        rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN",
            "pluginConfigs",
            "ai02",
            "MapAnnotations");
    }

    public string RootDirectory => rootDirectory;

    public MapAnnotationDocument GetDocument(uint territoryType, uint mapId, string mapName)
    {
        var key = BuildKey(territoryType, mapId);
        if (documents.TryGetValue(key, out var cached))
        {
            if (!string.IsNullOrWhiteSpace(mapName) && cached.MapName != mapName)
                cached.MapName = mapName;
            return cached;
        }

        var document = LoadDocument(territoryType, mapId);
        document.TerritoryType = territoryType;
        document.MapId = mapId;
        if (!string.IsNullOrWhiteSpace(mapName))
            document.MapName = mapName;

        documents[key] = document;
        return document;
    }

    public MapAnnotationPoint AddPoint(
        uint territoryType,
        uint mapId,
        string mapName,
        Vector3 position,
        MapAnnotationKind kind,
        string label,
        string routeId,
        float radius,
        int riskScore)
    {
        var document = GetDocument(territoryType, mapId, mapName);
        var point = new MapAnnotationPoint
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = kind,
            Label = string.IsNullOrWhiteSpace(label) ? DefaultLabel(kind) : label.Trim(),
            RouteId = routeId.Trim(),
            Position = position,
            Radius = Math.Clamp(radius, 0f, 220f),
            RiskScore = Math.Clamp(riskScore, 0, 100),
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        document.Points.Add(point);
        Save(document);
        return point;
    }

    public bool DeletePoint(uint territoryType, uint mapId, string pointId)
    {
        var document = GetDocument(territoryType, mapId, string.Empty);
        var removed = document.Points.RemoveAll(point => point.Id == pointId) > 0;
        if (removed)
            Save(document);
        return removed;
    }

    public bool UndoLatest(uint territoryType, uint mapId)
    {
        var document = GetDocument(territoryType, mapId, string.Empty);
        var latest = document.Points
            .OrderByDescending(point => point.CreatedAtUnixMs)
            .FirstOrDefault();
        if (latest == null)
            return false;

        document.Points.Remove(latest);
        Save(document);
        return true;
    }

    public void Clear(uint territoryType, uint mapId)
    {
        var document = GetDocument(territoryType, mapId, string.Empty);
        document.Points.Clear();
        Save(document);
    }

    public int ApplyCoordinateCorrection(
        uint territoryType,
        uint mapId,
        MapCoordinateCorrection correction)
    {
        var document = GetDocument(territoryType, mapId, string.Empty);
        if (document.Points.Count == 0)
            return 0;

        foreach (var point in document.Points)
        {
            var corrected = ApplyCorrection(point.Position, correction);
            point.X = corrected.X;
            point.Y = corrected.Y;
            point.Z = corrected.Z;
        }

        Save(document);
        return document.Points.Count;
    }

    public MapAnnotationRouteSummary[] BuildRouteSummaries(MapAnnotationDocument document, Vector3? localPosition)
        => BuildRouteSummaries(document.Points, localPosition);

    public MapAnnotationRouteSummary[] BuildRouteSummaries(IReadOnlyList<MapAnnotationPoint> points, Vector3? localPosition)
    {
        return points
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => BuildRouteSummary(group.Key, group.OrderBy(point => point.CreatedAtUnixMs).ToArray(), localPosition))
            .OrderBy(summary => summary.RouteId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private MapAnnotationDocument LoadDocument(uint territoryType, uint mapId)
    {
        var path = BuildPath(territoryType, mapId);
        if (!File.Exists(path))
            return new MapAnnotationDocument();

        try
        {
            var document = JsonSerializer.Deserialize<MapAnnotationDocument>(File.ReadAllText(path), SerializerOptions);
            return document ?? new MapAnnotationDocument();
        }
        catch
        {
            return new MapAnnotationDocument();
        }
    }

    private void Save(MapAnnotationDocument document)
    {
        Directory.CreateDirectory(rootDirectory);
        document.UpdatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(BuildPath(document.TerritoryType, document.MapId), JsonSerializer.Serialize(document, SerializerOptions));
    }

    private string BuildPath(uint territoryType, uint mapId)
        => Path.Combine(rootDirectory, $"{BuildKey(territoryType, mapId)}.json");

    private static string BuildKey(uint territoryType, uint mapId)
        => $"map_{territoryType}_{mapId}";

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

    private static MapAnnotationRouteSummary BuildRouteSummary(
        string routeId,
        IReadOnlyList<MapAnnotationPoint> points,
        Vector3? localPosition)
    {
        var distance = 0f;
        for (var i = 1; i < points.Count; i++)
            distance += Distance2D(points[i - 1].Position, points[i].Position);

        float? localDistance = null;
        int? localEta = null;
        if (localPosition.HasValue && points.Count > 0)
        {
            localDistance = Distance2D(localPosition.Value, points[0].Position);
            localEta = EstimateEtaSeconds(localDistance.Value, MountedYalmsPerSecond);
        }

        var kinds = points
            .Select(point => point.Kind)
            .Distinct()
            .Select(KindShortText)
            .ToArray();

        return new MapAnnotationRouteSummary
        {
            RouteId = routeId,
            PointCount = points.Count,
            Distance = distance,
            MountedEtaSeconds = EstimateEtaSeconds(distance, MountedYalmsPerSecond),
            OnFootEtaSeconds = EstimateEtaSeconds(distance, OnFootYalmsPerSecond),
            MaxRiskScore = points.Select(point => point.RiskScore).DefaultIfEmpty(0).Max(),
            DistanceFromLocalToStart = localDistance,
            LocalToStartEtaSeconds = localEta,
            KindSummary = string.Join("/", kinds)
        };
    }

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private static int EstimateEtaSeconds(float distance, float yalmsPerSecond)
        => (int)MathF.Ceiling(Math.Max(0f, distance) / Math.Max(0.1f, yalmsPerSecond));

    private static string KindShortText(MapAnnotationKind kind)
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
            _ => "备注"
        };

    private static string DefaultLabel(MapAnnotationKind kind)
        => kind switch
        {
            MapAnnotationKind.Spawn => "出生点",
            MapAnnotationKind.Objective => "目标点",
            MapAnnotationKind.Choke => "卡口",
            MapAnnotationKind.JumpPad => "跳台",
            MapAnnotationKind.Teleporter => "传送",
            MapAnnotationKind.HighGround => "高地",
            MapAnnotationKind.LowGround => "低地",
            MapAnnotationKind.Danger => "危险区",
            MapAnnotationKind.Rotation => "转点",
            MapAnnotationKind.Flank => "夹击",
            MapAnnotationKind.RoutePoint => "路径点",
            MapAnnotationKind.Bridge => "桥面",
            MapAnnotationKind.Underpass => "桥洞",
            _ => "标注"
        };
}
