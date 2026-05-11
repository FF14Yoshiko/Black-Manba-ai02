using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ai02;

public static class MapRouteRiskEvaluator
{
    private const float MountedYalmsPerSecond = 10.0f;
    private const float OnFootYalmsPerSecond = 6.0f;

    public static BattlefieldMapTacticalRouteSnapshot Evaluate(
        string routeId,
        string kindSummary,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints,
        string prefixEvidence = "")
    {
        var distance = 0f;
        var staticRisk = 0f;
        var dynamicRisk = 0f;
        var crossesDanger = false;
        var crossesMandatoryChoke = false;

        for (var i = 0; i < positions.Count; i++)
        {
            if (i > 0)
                distance += Distance2D(positions[i - 1], positions[i]);

            var sample = positions[i];
            var nearbyZones = zones.Where(zone => Distance2D(zone.Position, sample) <= MathF.Max(zone.Radius, 18f) + 12f).ToArray();
            if (nearbyZones.Length > 0)
            {
                staticRisk = MathF.Max(staticRisk, nearbyZones.Max(zone => zone.StaticRisk));
                dynamicRisk = MathF.Max(dynamicRisk, nearbyZones.Max(zone => zone.DynamicRisk));
                crossesDanger |= nearbyZones.Any(zone => zone.Kind is MapAnnotationKind.Danger or MapAnnotationKind.LowGround or MapAnnotationKind.Underpass || zone.TotalRisk >= 68f);
                crossesMandatoryChoke |= nearbyZones.Any(zone => zone.IsMandatoryChoke);
            }

            var nearbyHeat = heatPoints
                .Where(heat => Distance2D(heat.Position, sample) <= heat.Radius + 18f)
                .Select(heat => heat.Intensity)
                .DefaultIfEmpty(0f)
                .Max();
            dynamicRisk = MathF.Max(dynamicRisk, nearbyHeat);
        }

        var totalRisk = Math.Clamp(staticRisk * 0.45f + dynamicRisk * 0.55f, 0f, 100f);
        var recommendation = totalRisk >= 75f
            ? "绕路"
            : crossesDanger || totalRisk >= 55f
                ? "谨慎通过"
                : crossesMandatoryChoke
                    ? "可控卡点"
                    : "可走";

        var evidence = $"{(string.IsNullOrWhiteSpace(prefixEvidence) ? string.Empty : $"{prefixEvidence}；")}距离 {distance:0}y；静态风险 {staticRisk:0}；动态风险 {dynamicRisk:0}"
            + (crossesDanger ? "；经过危险/低地/桥洞" : string.Empty)
            + (crossesMandatoryChoke ? "；包含必卡点" : string.Empty);

        return new BattlefieldMapTacticalRouteSnapshot(
            routeId,
            kindSummary,
            positions.Count,
            distance,
            EstimateEtaSeconds(distance, MountedYalmsPerSecond),
            EstimateEtaSeconds(distance, OnFootYalmsPerSecond),
            staticRisk,
            dynamicRisk,
            totalRisk,
            crossesDanger,
            crossesMandatoryChoke,
            recommendation,
            evidence);
    }

    private static int EstimateEtaSeconds(float distance, float speed)
        => distance <= 0f ? 0 : (int)MathF.Ceiling(distance / Math.Max(0.01f, speed));

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
