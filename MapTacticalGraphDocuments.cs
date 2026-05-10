using System;
using System.Collections.Generic;
using System.Numerics;

namespace ai02;

internal readonly record struct RouteAnnotationGroup(string RouteId, MapAnnotationPoint[] Points);

internal sealed class CustomMapTacticalGraphDocument
{
    public int SchemaVersion { get; set; } = 2;
    public string GraphVersionId { get; set; } = string.Empty;
    public uint TerritoryType { get; set; }
    public uint MapId { get; set; }
    public string MapName { get; set; } = string.Empty;
    public long CreatedAtUnixMs { get; set; }
    public long UpdatedAtUnixMs { get; set; }
    public uint SourceMapId { get; set; }
    public float SourceMapSizeScale { get; set; }
    public int SourceMapOffsetX { get; set; }
    public int SourceMapOffsetY { get; set; }
    public List<MapAnnotationPoint> Points { get; set; } = new();
    public List<CustomMapTacticalRegion> Regions { get; set; } = new();
    public List<CustomMapTacticalPath> Paths { get; set; } = new();
}

internal sealed class CustomMapTacticalRegion
{
    public MapAnnotationKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
    public List<CustomMapTacticalVertex> Vertices { get; set; } = new();
    public float WorldY { get; set; }
    public int RiskScore { get; set; }
}

internal sealed class CustomMapTacticalPath
{
    public MapAnnotationKind Kind { get; set; }
    public string Label { get; set; } = string.Empty;
    public string RouteId { get; set; } = string.Empty;
    public List<CustomMapTacticalVertex> Points { get; set; } = new();
    public float WorldY { get; set; }
    public float Width { get; set; }
    public int RiskScore { get; set; }
    public bool IsOneWay { get; set; }
}

internal readonly record struct CustomMapTacticalVertex(float X, float Y, float Z)
{
    public static CustomMapTacticalVertex FromPoint(MapAnnotationPoint point)
        => new(point.X, point.Y, point.Z);

    public Vector3 ToVector3(float fallbackY)
        => new(X, MathF.Abs(Y) > 0.001f ? Y : fallbackY, Z);
}
