using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ai02.Tests;

public sealed class BuiltInTacticalGraphsTests
{
    private static readonly Regex FileNamePattern = new(@"^map_(\d+)_(\d+)\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void BuiltInTacticalGraphs_ArePresentAndDeserializable()
    {
        var files = EnumerateBuiltInGraphFiles();
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var path in files)
        {
            try
            {
                var document = LoadDocument(path);
                if (document == null)
                    failures.Add($"{Path.GetFileName(path)}: deserialized to null");
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name} {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void BuiltInTacticalGraphs_PassStructuralValidation()
    {
        var files = EnumerateBuiltInGraphFiles();
        Assert.NotEmpty(files);

        var failures = new List<string>();
        foreach (var path in files)
        {
            var fileName = Path.GetFileName(path);
            var document = LoadDocument(path);
            ValidateFileName(fileName, document, failures);
            ValidateDocumentHeader(fileName, document, failures);
            ValidatePoints(fileName, document.Points, failures);
            ValidateRegions(fileName, document.Regions, failures);
            ValidatePaths(fileName, document.Paths, failures);
            ValidateRouteConsistency(fileName, document, failures);
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static string[] EnumerateBuiltInGraphFiles()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "BuiltInTacticalGraphs");
        Assert.True(Directory.Exists(directory), $"BuiltInTacticalGraphs directory not found: {directory}");
        return Directory.EnumerateFiles(directory, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CustomMapTacticalGraphDocument LoadDocument(string path)
    {
        var json = File.ReadAllText(path);
        var document = JsonSerializer.Deserialize<CustomMapTacticalGraphDocument>(json);
        Assert.NotNull(document);
        return document!;
    }

    private static void ValidateFileName(string fileName, CustomMapTacticalGraphDocument document, List<string> failures)
    {
        var match = FileNamePattern.Match(fileName);
        if (!match.Success)
        {
            failures.Add($"{fileName}: filename does not match map_<territory>_<map>.json");
            return;
        }

        var territoryType = uint.Parse(match.Groups[1].Value);
        var mapId = uint.Parse(match.Groups[2].Value);
        if (document.TerritoryType != territoryType)
            failures.Add($"{fileName}: filename territory {territoryType} != document territory {document.TerritoryType}");
        if (document.MapId != mapId)
            failures.Add($"{fileName}: filename map {mapId} != document map {document.MapId}");
    }

    private static void ValidateDocumentHeader(string fileName, CustomMapTacticalGraphDocument document, List<string> failures)
    {
        if (document.SchemaVersion != 2)
            failures.Add($"{fileName}: unexpected schema version {document.SchemaVersion}");
        if (document.TerritoryType == 0)
            failures.Add($"{fileName}: TerritoryType must be > 0");
        if (document.MapId == 0)
            failures.Add($"{fileName}: MapId must be > 0");
        if (string.IsNullOrWhiteSpace(document.MapName))
            failures.Add($"{fileName}: MapName is empty");
        if (document.SourceMapId == 0)
            failures.Add($"{fileName}: SourceMapId must be > 0");
        if (document.SourceMapSizeScale <= 0f || !float.IsFinite(document.SourceMapSizeScale))
            failures.Add($"{fileName}: SourceMapSizeScale must be finite and > 0");
        if (document.CreatedAtUnixMs <= 0)
            failures.Add($"{fileName}: CreatedAtUnixMs must be > 0");
        if (document.UpdatedAtUnixMs <= 0)
            failures.Add($"{fileName}: UpdatedAtUnixMs must be > 0");
        if (document.Points.Count == 0 && document.Paths.Count == 0 && document.Regions.Count == 0)
            failures.Add($"{fileName}: graph is empty");
    }

    private static void ValidatePoints(string fileName, IReadOnlyList<MapAnnotationPoint> points, List<string> failures)
    {
        var duplicateIds = points
            .GroupBy(point => point.Id, StringComparer.Ordinal)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        foreach (var id in duplicateIds)
            failures.Add($"{fileName}: duplicate point id {id}");

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var prefix = $"{fileName}: point[{i}]";
            if (string.IsNullOrWhiteSpace(point.Id))
                failures.Add($"{prefix} id is empty");
            if (!Enum.IsDefined(point.Kind))
                failures.Add($"{prefix} has invalid kind {(int)point.Kind}");
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z))
                failures.Add($"{prefix} has non-finite coordinates");
            if (!float.IsFinite(point.Radius) || point.Radius <= 0f)
                failures.Add($"{prefix} radius must be finite and > 0");
            if (point.RiskScore < 0 || point.RiskScore > 100)
                failures.Add($"{prefix} risk must be within 0..100");
            if (point.CreatedAtUnixMs <= 0)
                failures.Add($"{prefix} CreatedAtUnixMs must be > 0");
        }
    }

    private static void ValidateRegions(string fileName, IReadOnlyList<CustomMapTacticalRegion> regions, List<string> failures)
    {
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var prefix = $"{fileName}: region[{i}]";
            if (!Enum.IsDefined(region.Kind))
                failures.Add($"{prefix} has invalid kind {(int)region.Kind}");
            if (region.Vertices.Count < 3)
                failures.Add($"{prefix} must have at least 3 vertices");
            if (region.RiskScore < 0 || region.RiskScore > 100)
                failures.Add($"{prefix} risk must be within 0..100");
            foreach (var vertex in region.Vertices)
            {
                if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z))
                {
                    failures.Add($"{prefix} has non-finite vertex");
                    break;
                }
            }
        }
    }

    private static void ValidatePaths(string fileName, IReadOnlyList<CustomMapTacticalPath> paths, List<string> failures)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var prefix = $"{fileName}: path[{i}]";
            if (!Enum.IsDefined(path.Kind))
                failures.Add($"{prefix} has invalid kind {(int)path.Kind}");
            if (path.Points.Count < 2)
                failures.Add($"{prefix} must have at least 2 points");
            if (!float.IsFinite(path.Width) || path.Width < 0f)
                failures.Add($"{prefix} width must be finite and >= 0");
            if (path.RiskScore < 0 || path.RiskScore > 100)
                failures.Add($"{prefix} risk must be within 0..100");
            foreach (var vertex in path.Points)
            {
                if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z))
                {
                    failures.Add($"{prefix} has non-finite point");
                    break;
                }
            }
        }
    }

    private static void ValidateRouteConsistency(string fileName, CustomMapTacticalGraphDocument document, List<string> failures)
    {
        var pointRoutes = document.Points
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var pathRoutes = document.Paths
            .Where(path => !string.IsNullOrWhiteSpace(path.RouteId))
            .GroupBy(path => path.RouteId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var routeId in pointRoutes.Keys.Except(pathRoutes.Keys, StringComparer.OrdinalIgnoreCase))
            failures.Add($"{fileName}: point route '{routeId}' has no matching path");

        foreach (var routeId in pathRoutes.Keys.Except(pointRoutes.Keys, StringComparer.OrdinalIgnoreCase))
            failures.Add($"{fileName}: path route '{routeId}' has no matching route points");

        foreach (var pair in pointRoutes)
        {
            if (pair.Value < 2)
                failures.Add($"{fileName}: route '{pair.Key}' has only {pair.Value} point(s)");
        }
    }
}
