using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MapSheet = Lumina.Excel.Sheets.Map;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;
namespace ai02;

public partial class MainWindow
{
    private void EnsureOfflineMapSelection(BattlefieldSnapshot snapshot)
    {
        if (offlineMapIndex >= 0 && offlineMapIndex < OfflineFrontlineMaps.Length)
            return;

        offlineMapIndex = Array.FindIndex(OfflineFrontlineMaps, entry => entry.TerritoryType == snapshot.TerritoryType);
        if (offlineMapIndex < 0)
            offlineMapIndex = 0;
    }
    private void ApplyMapCoordinateCorrection(uint territoryType, uint mapId, MapCoordinateCorrection correction)
    {
        var messages = new List<string>();
        messages.Add($"校正参数：缩放 {correction.Scale:0.0000}，旋转 {RadiansToDegrees(correction.RotationRadians):+0.00;-0.00;0}°，偏移 X {correction.OffsetX:+0.00;-0.00;0} / Z {correction.OffsetZ:+0.00;-0.00;0}");
        if (!applyCorrectionToDraft && !applyCorrectionToGraph)
        {
            mapCalibrationStatus = "没有选择修正目标。请勾选“修正草稿”或“修正已存图谱”。";
            return;
        }

        if (applyCorrectionToDraft)
        {
            var count = plugin.MapAnnotationService.ApplyCoordinateCorrection(territoryType, mapId, correction);
            messages.Add(count > 0 ? $"手动草稿已修正 {count} 点" : "手动草稿为空");
        }

        if (applyCorrectionToGraph)
        {
            var result = plugin.MapTacticalGraphService.ApplyCoordinateCorrectionToCustomGraph(territoryType, mapId, correction);
            messages.Add(result.Message);
            mapGraphVersionStatus = result.Message;
        }

        mapCalibrationStatus = string.Join("；", messages);
    }

    private static MapCalibrationCorrectionEstimate EstimateCalibrationCorrection(IReadOnlyList<MapCalibrationSample> samples)
    {
        if (samples.Count == 0)
            return new MapCalibrationCorrectionEstimate(0, 0f, 0f, new MapCoordinateCorrection(1f, 0f, 0f, 0f, 0f));

        var averageError = samples.Select(sample => sample.Error).Average();
        var maxError = samples.Select(sample => sample.Error).Max();
        var clickedCenter = AveragePosition(samples.Select(sample => sample.ClickedPosition));
        var actualCenter = AveragePosition(samples.Select(sample => sample.ActualPosition));
        var scale = 1f;
        var rotation = 0f;
        if (samples.Count >= 2)
        {
            var dot = 0f;
            var cross = 0f;
            var denominator = 0f;
            foreach (var sample in samples)
            {
                var sx = sample.ClickedPosition.X - clickedCenter.X;
                var sz = sample.ClickedPosition.Z - clickedCenter.Z;
                var ax = sample.ActualPosition.X - actualCenter.X;
                var az = sample.ActualPosition.Z - actualCenter.Z;
                dot += sx * ax + sz * az;
                cross += sx * az - sz * ax;
                denominator += sx * sx + sz * sz;
            }

            if (denominator > 0.001f)
            {
                scale = Math.Clamp(MathF.Sqrt(dot * dot + cross * cross) / denominator, 0.25f, 4f);
                rotation = Math.Clamp(MathF.Atan2(cross, dot), -MathF.PI / 4f, MathF.PI / 4f);
            }
        }

        var rotatedClickedCenter = Rotate2D(clickedCenter, rotation);
        var offsetX = actualCenter.X - rotatedClickedCenter.X * scale;
        var offsetY = samples.Select(sample => sample.ActualPosition.Y - sample.ClickedPosition.Y).Average();
        var offsetZ = actualCenter.Z - rotatedClickedCenter.Z * scale;
        return new MapCalibrationCorrectionEstimate(
            samples.Count,
            averageError,
            maxError,
            new MapCoordinateCorrection(scale, rotation, offsetX, offsetY, offsetZ));
    }

    private static Vector3 AveragePosition(IEnumerable<Vector3> positions)
    {
        var sum = Vector3.Zero;
        var count = 0;
        foreach (var position in positions)
        {
            sum += position;
            count++;
        }

        return count == 0 ? Vector3.Zero : sum / count;
    }

    private static Vector3 Rotate2D(Vector3 position, float radians)
    {
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        return new Vector3(
            position.X * cos - position.Z * sin,
            position.Y,
            position.X * sin + position.Z * cos);
    }

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;

    private static float RadiansToDegrees(float radians)
        => radians * 180f / MathF.PI;

    private static float PathDistance(IReadOnlyList<Vector3> points)
    {
        var distance = 0f;
        for (var i = 1; i < points.Count; i++)
            distance += Distance2D(points[i - 1], points[i]);
        return distance;
    }

    private static Vector3[] ObservedPathPoints(BattlefieldMapGroupPathSnapshot path)
        => path.Points ?? Array.Empty<Vector3>();

    private OfflineMapMetadata ResolveOfflineMapMetadata(OfflineFrontlineMapEntry entry)
    {
        var mapId = entry.FallbackMapId;
        try
        {
            var territorySheet = plugin.DataManager.GetExcelSheet<TerritoryTypeSheet>();
            if (territorySheet.TryGetRow(entry.TerritoryType, out var territory) && territory.Map.RowId != 0)
                mapId = territory.Map.RowId;
        }
        catch
        {
            mapId = entry.FallbackMapId;
        }

        try
        {
            var mapSheet = plugin.DataManager.GetExcelSheet<MapSheet>();
            if (mapSheet.TryGetRow(mapId, out var map))
            {
                var mapKey = map.Id.ToString();
                return new OfflineMapMetadata(
                    entry.MapType,
                    entry.TerritoryType,
                    map.RowId,
                    entry.DisplayName,
                    BuildOfflineMapTexturePath(mapKey),
                    map.SizeFactor / 100f,
                    map.OffsetX,
                    map.OffsetY,
                    true);
            }
        }
        catch
        {
            // The grid fallback is still useful for editing annotations if game map data is unavailable.
        }

        return new OfflineMapMetadata(
            entry.MapType,
            entry.TerritoryType,
            mapId,
            entry.DisplayName,
            string.Empty,
            1f,
            0,
            0,
            false);
    }
    private void AddMapCalibrationSample(uint territoryType, uint mapId, Vector3 clickedPosition, Vector3 actualPosition)
    {
        mapCalibrationSamples.Add(new MapCalibrationSample(
            territoryType,
            mapId,
            clickedPosition,
            actualPosition,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        mapCalibrationStatus = $"已记录校准样本：点击 {FormatPosition(clickedPosition)} -> 实际 {FormatPosition(actualPosition)}，误差 {Distance2D(clickedPosition, actualPosition):0.0}y。";
    }
    private void SaveCurrentMapAnnotationsAsCustomGraph(OfflineMapMetadata map, MapAnnotationDocument document)
    {
        var result = plugin.MapTacticalGraphService.SaveCustomGraphFromAnnotations(
            map.TerritoryType,
            map.MapId,
            map.DisplayName,
            document.Points);
        if (result.Success)
            plugin.MapAnnotationService.Clear(map.TerritoryType, map.MapId);
        mapGraphSaveStatus = result.Success
            ? $"{result.Message} 手动草稿已清空，文件：{result.Path}"
            : result.Message;
    }
    private void AddMapAnnotation(Vector3 position, uint territoryType, uint mapId, string mapName)
    {
        plugin.MapAnnotationService.AddPoint(
            territoryType,
            mapId,
            mapName,
            position,
            selectedAnnotationKind,
            annotationLabel,
            annotationRouteId,
            annotationRadius,
            annotationRiskScore);
        annotationClearArmed = false;
    }

    private void AddMapAnnotation(
        Vector3 position,
        uint territoryType,
        uint mapId,
        string mapName,
        MapAnnotationKind kind,
        string label,
        string routeId,
        float radius,
        int riskScore)
    {
        plugin.MapAnnotationService.AddPoint(
            territoryType,
            mapId,
            mapName,
            position,
            kind,
            label,
            routeId,
            radius,
            riskScore);
        annotationClearArmed = false;
    }

    private void ImportCurrentMapObjectives(BattlefieldSnapshot snapshot, uint territoryType, uint mapId, string mapName)
    {
        var document = plugin.MapAnnotationService.GetDocument(territoryType, mapId, mapName);
        foreach (var objective in snapshot.MapObjectives)
        {
            if (IsNearExistingAnnotation(document, objective.Position, MapAnnotationKind.Objective, 10f))
                continue;

            var label = !string.IsNullOrWhiteSpace(objective.Name)
                ? objective.Name
                : $"{MapObjectiveCategoryText(objective.Category)} {objective.LocationId}".Trim();
            AddMapAnnotation(objective.Position, territoryType, mapId, mapName, MapAnnotationKind.Objective, label, string.Empty, 18f, 35);
        }
    }

    private void ImportCurrentMapEvents(BattlefieldSnapshot snapshot, uint territoryType, uint mapId, string mapName)
    {
        var document = plugin.MapAnnotationService.GetDocument(territoryType, mapId, mapName);
        foreach (var item in snapshot.MapEvents)
        {
            if (IsNearExistingAnnotation(document, item.Position, MapAnnotationKind.Objective, 10f))
                continue;

            var value = MapEventValueText(item);
            var label = value == "-" ? MapEventKindText(item.Kind) : $"{MapEventKindText(item.Kind)} {value}";
            AddMapAnnotation(item.Position, territoryType, mapId, mapName, MapAnnotationKind.Objective, label, string.Empty, 18f, 35);
        }
    }

    private static bool IsNearExistingAnnotation(
        MapAnnotationDocument document,
        Vector3 position,
        MapAnnotationKind kind,
        float radius)
    {
        var radiusSquared = radius * radius;
        return document.Points.Any(point =>
        {
            if (point.Kind != kind)
                return false;

            var dx = point.X - position.X;
            var dz = point.Z - position.Z;
            return dx * dx + dz * dz <= radiusSquared;
        });
    }
}