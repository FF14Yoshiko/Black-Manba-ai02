using System;
using System.Linq;
using System.Numerics;

namespace ai02;

public sealed class MapTacticalGraphSnapshot
{
    public bool IsAvailable { get; init; }
    public FrontlineMapType MapType { get; init; } = FrontlineMapType.Unknown;
    public uint TerritoryType { get; init; }
    public uint MapId { get; init; }
    public uint[] TerritoryTypeIds { get; init; } = Array.Empty<uint>();
    public uint[] MapIds { get; init; } = Array.Empty<uint>();
    public string MapName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string SourceText { get; init; } = string.Empty;
    public string CoverageText { get; init; } = string.Empty;
    public bool UsesFallbackProjection { get; init; }
    public MapAnnotationPoint[] Points { get; init; } = Array.Empty<MapAnnotationPoint>();
    public MapAnnotationPoint[] AnalysisPoints { get; init; } = Array.Empty<MapAnnotationPoint>();
    public MapTacticalRegionSnapshot[] Regions { get; init; } = Array.Empty<MapTacticalRegionSnapshot>();
    public MapTacticalPathSnapshot[] Paths { get; init; } = Array.Empty<MapTacticalPathSnapshot>();

    public int PointCount => Points.Length;
    public int AnalysisPointCount => AnalysisPoints.Length;
    public int RegionCount => Regions.Length;
    public int PathCount => Paths.Length;
    public int RouteCount => Points
        .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
        .Select(point => point.RouteId.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
    public int ChokeCount => Points.Count(point => point.Kind == MapAnnotationKind.Choke);
    public int HeightPointCount => Points.Count(point => point.Kind is MapAnnotationKind.HighGround or MapAnnotationKind.LowGround);
    public int JumpPadCount => Points.Count(point => point.Kind == MapAnnotationKind.JumpPad);
    public int TeleporterCount => Points.Count(point => point.Kind == MapAnnotationKind.Teleporter);
    public int DangerZoneCount => Points.Count(point => point.Kind == MapAnnotationKind.Danger);
    public int FlankRouteCount => Points
        .Where(point => point.Kind == MapAnnotationKind.Flank && !string.IsNullOrWhiteSpace(point.RouteId))
        .Select(point => point.RouteId.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
}

public sealed class MapTacticalRegionSnapshot
{
    public string Id { get; init; } = string.Empty;
    public MapAnnotationKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    public Vector3 Center { get; init; }
    public Vector3[] Vertices { get; init; } = Array.Empty<Vector3>();
    public float Radius { get; init; }
    public int RiskScore { get; init; }
    public string SourceText { get; init; } = string.Empty;
}

public sealed class MapTacticalPathSnapshot
{
    public string Id { get; init; } = string.Empty;
    public MapAnnotationKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    public string RouteId { get; init; } = string.Empty;
    public Vector3[] Points { get; init; } = Array.Empty<Vector3>();
    public float Width { get; init; }
    public int RiskScore { get; init; }
    public bool IsOneWay { get; init; }
    public string SourceText { get; init; } = string.Empty;
}

public readonly record struct SaveCustomTacticalGraphResult(
    bool Success,
    int RegionCount,
    int PathCount,
    int PointCount,
    string Message,
    string Path);

public readonly record struct MapTacticalGraphVersionInfo(
    string VersionId,
    string DisplayName,
    long CreatedAtUnixMs,
    long UpdatedAtUnixMs,
    int PointCount,
    int RegionCount,
    int PathCount,
    uint SourceMapId,
    float SourceMapSizeScale,
    int SourceMapOffsetX,
    int SourceMapOffsetY,
    bool IsCurrent,
    string Path);

public readonly record struct MapTacticalGraphCompareResult(
    bool Success,
    string Message,
    int PointDelta,
    int RegionDelta,
    int PathDelta,
    int AddedPointCount,
    int RemovedPointCount,
    int ChangedPointCount);

public readonly record struct TacticalGraphOperationResult(
    bool Success,
    string Message,
    string Path);

public readonly record struct MapCoordinateCorrection(
    float Scale,
    float RotationRadians,
    float OffsetX,
    float OffsetY,
    float OffsetZ);
