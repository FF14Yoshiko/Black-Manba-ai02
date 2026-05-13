using System;
using System.Collections.Generic;
using System.Linq;

namespace ai02;

public readonly record struct MapTacticalSemanticSummary(
    string DangerSummaryText,
    string TerrainAdvantageSummaryText,
    string PassabilitySummaryText,
    string RewardModelSummaryText,
    string MapKnowledgeFocusText,
    string CurrentRecommendation);

public static class MapTacticalSemanticsBuilder
{
    public static MapTacticalSemanticSummary Build(
        FrontlineMapType mapType,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapTacticalRouteSnapshot> routes,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldAnnouncementSituationSnapshot announcements,
        int highGroundCount,
        int lowGroundCount,
        int jumpPadCount,
        int teleporterCount,
        int flankEntryCount,
        int bridgeCount,
        int underpassCount,
        int mandatoryChokeCount,
        int oneWayPassageCount,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        var dangerSummary = BuildDangerSummary(zones, heatPoints);
        var terrainSummary = BuildTerrainSummary(
            zones,
            highGroundCount,
            lowGroundCount,
            jumpPadCount,
            teleporterCount,
            flankEntryCount,
            bridgeCount,
            underpassCount);
        var passabilitySummary = BuildPassabilitySummary(
            zones,
            routes,
            mandatoryChokeCount,
            oneWayPassageCount,
            jumpPadCount,
            teleporterCount);
        var rewardSummary = BuildRewardModelSummary(mapType, objectives);
        var mapKnowledgeFocus = BuildMapKnowledgeFocusText(mapType, zones, objectives, timeSituation, announcements);
        var recommendation = BuildCurrentRecommendation(
            zones,
            routes,
            heatPoints,
            highGroundCount,
            mandatoryChokeCount,
            oneWayPassageCount,
            teamSituation);

        return new MapTacticalSemanticSummary(
            dangerSummary,
            terrainSummary,
            passabilitySummary,
            rewardSummary,
            mapKnowledgeFocus,
            recommendation);
    }

    private static string BuildDangerSummary(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints)
    {
        var primaryDanger = zones
            .Where(IsPrimaryDangerZone)
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();
        var exposedChoke = zones
            .Where(zone => zone.IsMandatoryChoke && zone.TotalRisk >= 60f)
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();
        var hottestPoint = heatPoints
            .OrderByDescending(point => point.Intensity)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(primaryDanger.Id) && string.IsNullOrWhiteSpace(hottestPoint.SourceText))
            return "危险判断：当前没有明显地形爆点";

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(primaryDanger.Id))
            parts.Add($"{DescribeDangerZone(primaryDanger)} {primaryDanger.Label} 风险 {primaryDanger.TotalRisk:0}");
        if (!string.IsNullOrWhiteSpace(exposedChoke.Id)
            && !string.Equals(exposedChoke.Id, primaryDanger.Id, StringComparison.Ordinal))
        {
            parts.Add($"窄口压力 {exposedChoke.Label}");
        }

        if (!string.IsNullOrWhiteSpace(hottestPoint.SourceText) && hottestPoint.Intensity >= 55f)
            parts.Add($"实时热区 {hottestPoint.SourceText} 热度 {hottestPoint.Intensity:0}");

        return parts.Count == 0
            ? "危险判断：当前没有明显地形爆点"
            : $"危险判断：{string.Join("；", parts)}";
    }

    private static string BuildTerrainSummary(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        int highGroundCount,
        int lowGroundCount,
        int jumpPadCount,
        int teleporterCount,
        int flankEntryCount,
        int bridgeCount,
        int underpassCount)
    {
        var safeHighGround = zones
            .Where(zone => zone.Kind == MapAnnotationKind.HighGround && zone.TotalRisk <= 55f)
            .OrderBy(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();
        var riskyLowGround = zones
            .Where(zone => zone.Kind == MapAnnotationKind.LowGround && zone.TotalRisk >= 60f)
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();

        var parts = new List<string>(4);
        if (highGroundCount > 0 || lowGroundCount > 0)
            parts.Add($"高台 {highGroundCount}，低地 {lowGroundCount}");
        if (jumpPadCount > 0 || teleporterCount > 0)
            parts.Add($"跳台 {jumpPadCount}，传送 {teleporterCount}");
        if (flankEntryCount > 0 || bridgeCount > 0 || underpassCount > 0)
            parts.Add($"侧翼入口 {flankEntryCount}，桥面 {bridgeCount}，桥洞 {underpassCount}");
        if (!string.IsNullOrWhiteSpace(safeHighGround.Id))
            parts.Add($"可争高台 {safeHighGround.Label}");
        if (!string.IsNullOrWhiteSpace(riskyLowGround.Id))
            parts.Add($"慎入低地 {riskyLowGround.Label}");

        return parts.Count == 0
            ? "地形判断：当前没有显著地形优势点"
            : $"地形判断：{string.Join("；", parts)}";
    }

    private static string BuildPassabilitySummary(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapTacticalRouteSnapshot> routes,
        int mandatoryChokeCount,
        int oneWayPassageCount,
        int jumpPadCount,
        int teleporterCount)
    {
        var safeTransitCount = routes.Count(route => route.TotalRisk <= 45f && !route.CrossesDangerZone && !route.CrossesMandatoryChoke);
        var constrainedTransitCount = routes.Count(route => route.TotalRisk >= 65f || route.CrossesDangerZone || route.CrossesMandatoryChoke);
        var exposedChoke = zones
            .Where(zone => zone.IsMandatoryChoke && zone.TotalRisk >= 60f)
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();

        if (mandatoryChokeCount == 0
            && oneWayPassageCount == 0
            && safeTransitCount == 0
            && constrainedTransitCount == 0
            && jumpPadCount == 0
            && teleporterCount == 0)
        {
            return "通行约束：当前没有明显必卡口或单向通道";
        }

        var parts = new List<string>(5);
        if (mandatoryChokeCount > 0)
            parts.Add($"必卡点 {mandatoryChokeCount}");
        if (oneWayPassageCount > 0)
            parts.Add($"单向通道 {oneWayPassageCount}");
        if (jumpPadCount > 0 || teleporterCount > 0)
            parts.Add($"位移换边点 {jumpPadCount + teleporterCount}");
        if (safeTransitCount > 0)
            parts.Add($"低风险切线 {safeTransitCount}");
        if (constrainedTransitCount > 0)
            parts.Add($"受压通路 {constrainedTransitCount}");
        if (!string.IsNullOrWhiteSpace(exposedChoke.Id))
            parts.Add($"当前最挤 {exposedChoke.Label}");

        return $"通行约束：{string.Join("；", parts)}";
    }

    private static string BuildRewardModelSummary(
        FrontlineMapType mapType,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives)
    {
        var baseText = mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => "收益模型：据点持续跳分是主线，4/7 据点会出现收益跃迁，怪物刷新前要提前站位",
            FrontlineMapType.SealRock => "收益模型：高等级石文持续收益高于零散击杀，先中立再占领通常比深追更值钱",
            FrontlineMapType.FieldsOfHonor => "收益模型：大冰/小冰是主线分数，打冰贡献和转火时机比空追更值钱",
            FrontlineMapType.OnsalHakair => "收益模型：无垢的大地不可夺回，6 秒契约与 30 秒跳分决定节奏，提前站位和打断优先",
            FrontlineMapType.Vochester => "收益模型：战略目标固定 8 秒占领、控制后不可夺回，护点与封入口优先于无收益缠斗",
            _ => "收益模型：优先比较当前目标价值、比分压力和可持续得分，不把地图理解成固定路线题",
        };

        var activeObjective = objectives
            .Where(IsObjectiveWindowActive)
            .OrderByDescending(objective => objective.ScoreValue ?? 0)
            .ThenBy(objective => objective.RemainingSeconds ?? int.MaxValue)
            .ThenBy(objective => objective.Name, StringComparer.Ordinal)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(activeObjective.Id))
            return baseText;

        return $"{baseText}；当前窗口 {DescribeObjective(activeObjective)}";
    }

    private static string BuildMapKnowledgeFocusText(
        FrontlineMapType mapType,
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldAnnouncementSituationSnapshot announcements)
        => mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => BuildBorderlandFocusText(zones, objectives, timeSituation),
            FrontlineMapType.SealRock => BuildSealRockFocusText(zones, objectives, timeSituation),
            FrontlineMapType.FieldsOfHonor => BuildFieldsOfHonorFocusText(zones, objectives, timeSituation),
            FrontlineMapType.OnsalHakair => BuildOnsalFocusText(zones, objectives, timeSituation),
            FrontlineMapType.Vochester => BuildVochesterFocusText(zones, objectives, timeSituation, announcements),
            _ => "地图特化：当前使用通用地形判断，优先盯危险区、卡口、高台和收益窗口",
        };

    private static string BuildCurrentRecommendation(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapTacticalRouteSnapshot> routes,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints,
        int highGroundCount,
        int mandatoryChokeCount,
        int oneWayPassageCount,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        var forcedDetour = zones
            .Where(zone => zone.Recommendation.Contains("强制绕路", StringComparison.Ordinal)
                || zone.TotalRisk >= 78f && zone.Kind is MapAnnotationKind.Danger or MapAnnotationKind.LowGround or MapAnnotationKind.Underpass or MapAnnotationKind.Flank)
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forcedDetour.Id))
            return $"别把主团压进 {forcedDetour.Label}，这里地形和敌压都差，换侧线或高台再接团";

        var chokeFight = zones
            .Where(zone => zone.Recommendation.Contains("卡口接团", StringComparison.Ordinal))
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(chokeFight.Id))
            return $"可在 {chokeFight.Label} 卡口接团，我方人数和站位暂时更占优";

        var safeHighGround = zones
            .Where(zone => zone.Kind == MapAnnotationKind.HighGround && zone.TotalRisk <= 50f)
            .OrderBy(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(safeHighGround.Id)
            && highGroundCount > 0
            && teamSituation.Friendly.NearCount + teamSituation.Friendly.MidCount >= Math.Max(1, teamSituation.Enemy.NearCount))
        {
            return $"先抢 {safeHighGround.Label} 这类高台视野位，再决定压进角度";
        }

        var hottestPoint = heatPoints
            .OrderByDescending(point => point.Intensity)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(hottestPoint.SourceText) && hottestPoint.Intensity >= 65f)
            return $"实时热区在 {hottestPoint.SourceText}，先拉开爆点再回头";

        var transitFullyConstrained = routes.Count > 0
            && routes.All(route => route.TotalRisk >= 65f || route.CrossesDangerZone || route.CrossesMandatoryChoke);
        var hasSafeTransit = routes.Any(route => route.TotalRisk <= 48f && !route.CrossesDangerZone && !route.CrossesMandatoryChoke);
        if (transitFullyConstrained || mandatoryChokeCount >= 2 || oneWayPassageCount >= 2)
        {
            return teamSituation.IsEnemySplit
                ? "敌方分兵且通行受限，先保回接点再找侧翼入口"
                : "当前通行受限，先保回撤口和高台，再决定从哪边压";
        }

        if (teamSituation.IsEnemySplit)
            return hasSafeTransit ? "敌方分兵，优先从侧翼入口和低风险通道找夹角" : "敌方分兵，优先盯侧翼入口和高台夹角";

        return hasSafeTransit
            ? "地形压力平稳，围绕收益窗口灵活换角度推进"
            : "地图地形压力平稳，保持主团机动，别在低地和窄口硬拖";
    }

    private static string BuildBorderlandFocusText(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        var danger = FindZoneLabel(zones, "南崖", "东崖", "西崖", "跳下去会死");
        var transit = FindZoneLabel(zones, "传送装置", "传送入口", "传送出口", "高桥", "很高的桥");
        var monster = objectives
            .Where(objective => objective.Category == BattlefieldMapObjectiveCategory.Monster || ContainsAny(objective.Name, "截击", "无人机"))
            .OrderByDescending(objective => objective.ScoreValue ?? 0)
            .FirstOrDefault();

        var parts = new List<string>
        {
            "地图特化：怪物预告和传送开启前 20-30 秒就该转高台、卡传送口，不要等刷新后再临时赶路",
            "据点 4/7 是收益跳变点，追残血前先算会不会把持续分送回去"
        };
        var nextWindow = BuildTimedWindowText(timeSituation, "据点", "怪物", "截击", "无人机");
        if (!string.IsNullOrWhiteSpace(nextWindow))
            parts.Add($"{nextWindow}，提早占传送口和高桥");
        if (!string.IsNullOrWhiteSpace(transit.Id))
            parts.Add($"传送/高桥位重点看 {transit.Label}");
        if (!string.IsNullOrWhiteSpace(danger.Id))
            parts.Add($"断崖爆点在 {danger.Label}");
        if (!string.IsNullOrWhiteSpace(monster.Id))
            parts.Add($"当前怪物窗口 {DescribeObjective(monster)}");

        return string.Join("；", parts);
    }

    private static string BuildSealRockFocusText(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        var choke = FindZoneLabel(zones, "主桥口", "桥口", "天桥", "洞内窄口", "洞", "高低差卡口", "地形优势口");
        var highGround = FindZoneLabel(zones, "洞门口高台", "海边高台", "上家桥上", "c4高台", "c2高台");
        var active = SelectMostValuableObjective(objectives);

        var parts = new List<string>
        {
            "地图特化：桥口、天桥和洞门口高台是主卡位，抢点前先看哪一侧先占高",
            "石文被碰成中立后约 3 秒不能再改状态，停点后约 15 秒就该看下一轮"
        };
        var phaseText = BuildSealRockPhaseText(timeSituation);
        if (!string.IsNullOrWhiteSpace(phaseText))
            parts.Add(phaseText);
        var nextWindow = BuildTimedWindowText(timeSituation, "石文", "亚拉戈", "tomelith");
        if (!string.IsNullOrWhiteSpace(nextWindow))
            parts.Add($"{nextWindow}，先看哪侧高台能先站住");
        if (!string.IsNullOrWhiteSpace(choke.Id))
            parts.Add($"当前主卡口 {choke.Label}");
        if (!string.IsNullOrWhiteSpace(highGround.Id))
            parts.Add($"当前可争高台 {highGround.Label}");
        if (!string.IsNullOrWhiteSpace(active.Id))
            parts.Add($"当前收益点 {DescribeObjective(active)}");

        return string.Join("；", parts);
    }

    private static string BuildFieldsOfHonorFocusText(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        var choke = FindZoneLabel(zones, "中央窄口", "冰口", "窄桥");
        var bridge = FindZoneLabel(zones, "桥", "桥洞");
        var active = SelectMostValuableObjective(objectives);
        var hasBigIce = objectives.Any(objective =>
            objective.Category == BattlefieldMapObjectiveCategory.Ice
            && ContainsAny($"{objective.RankName} {objective.Name}", "大", "Large")
            && IsObjectiveWindowActive(objective));
        var hasSmallIce = objectives.Any(objective =>
            objective.Category == BattlefieldMapObjectiveCategory.Ice
            && ContainsAny($"{objective.RankName} {objective.Name}", "小", "Small")
            && IsObjectiveWindowActive(objective));

        var parts = new List<string>
        {
            "地图特化：中央窄口、冰口、窄桥和桥洞都是典型爆点，别在低地和桥头硬拖",
            "桥上能打冰坑，右侧劣势冰进得去但很难顺利撤出来"
        };
        var nextWindow = BuildTimedWindowText(timeSituation, "冰", "大冰", "小冰", "ice");
        if (!string.IsNullOrWhiteSpace(nextWindow))
            parts.Add($"{nextWindow}，主团要提早看桥位和回接线");
        if (hasBigIce)
            parts.Add("大冰是主团窗口，打完 5 秒就要盯下一块预告");
        if (hasSmallIce)
            parts.Add("小冰单位伤害换分约为大冰 2.5 倍，别放任敌方白吃");
        if (!string.IsNullOrWhiteSpace(choke.Id))
            parts.Add($"当前爆点 {choke.Label}");
        if (!string.IsNullOrWhiteSpace(bridge.Id))
            parts.Add($"桥位 {bridge.Label} 可做远程压制");
        if (!string.IsNullOrWhiteSpace(active.Id))
            parts.Add($"当前收益点 {DescribeObjective(active)}");

        return string.Join("；", parts);
    }

    private static string BuildOnsalFocusText(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        var choke = FindZoneLabel(zones, "中心桥口", "桥口子", "天桥窄路口", "桥下窄口", "侧线窄口", "窄口");
        var bridge = FindZoneLabel(zones, "01主桥", "01桥", "03桥", "07侧桥", "07桥", "09桥", "桥下洞口", "洞");
        var centerObjective = objectives
            .Where(objective => IsObjectiveWindowActive(objective) && IsCenterOnsalLocation(objective.LocationId))
            .OrderByDescending(objective => objective.ScoreValue ?? 0)
            .ThenBy(objective => objective.LocationId, StringComparer.Ordinal)
            .FirstOrDefault();
        var active = SelectMostValuableObjective(objectives);

        var parts = new List<string>
        {
            "地图特化：01/11/12/13 是高等级中心带，01 最低 A 级；摸点前先抢桥口和高台视野",
            "天桥窄路口、桥下窄口和 01/07 桥口是读 6 秒契约前最容易被拦截的位置"
        };
        var phaseText = BuildOnsalPhaseText(timeSituation);
        if (!string.IsNullOrWhiteSpace(phaseText))
            parts.Add(phaseText);
        var nextWindow = BuildTimedWindowText(timeSituation, "无垢", "契约", "ovoo");
        if (!string.IsNullOrWhiteSpace(nextWindow))
            parts.Add($"{nextWindow}，桥口、高台和打断线要先到");
        if (!string.IsNullOrWhiteSpace(choke.Id))
            parts.Add($"当前主拦截口 {choke.Label}");
        if (!string.IsNullOrWhiteSpace(bridge.Id))
            parts.Add($"当前桥位 {bridge.Label}");
        if (!string.IsNullOrWhiteSpace(centerObjective.Id))
            parts.Add($"当前中心高价值点 {DescribeObjective(centerObjective)}");
        else if (!string.IsNullOrWhiteSpace(active.Id))
            parts.Add($"当前收益点 {DescribeObjective(active)}");

        return string.Join("；", parts);
    }

    private static string BuildVochesterFocusText(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldAnnouncementSituationSnapshot announcements)
    {
        var choke = FindZoneLabel(zones, "中央坡口", "上坡下坡口", "11洞口", "洞口", "窄口");
        var highGround = FindZoneLabel(zones, "10高台", "11点高台", "12高台", "11洞点高台");
        var active = SelectMostValuableObjective(objectives);
        var centerOrLatePoint = objectives
            .Where(objective => IsObjectiveWindowActive(objective) && IsVochesterPriorityLocation(objective.LocationId))
            .OrderByDescending(objective => objective.ScoreValue ?? 0)
            .ThenBy(objective => objective.LocationId, StringComparer.Ordinal)
            .FirstOrDefault();

        var parts = new List<string>
        {
            "地图特化：10/11/12 高台、11 洞口和中央低地出口决定抢区路线，先看入口覆盖再让人读 8 秒",
            "点一旦占住就不可夺回，别围着已控点白耗，优先准备下一轮过渡目标"
        };
        var phaseText = BuildVochesterPhaseText(timeSituation);
        if (!string.IsNullOrWhiteSpace(phaseText))
            parts.Add(phaseText);
        var weatherText = BuildVochesterWeatherText(announcements);
        if (!string.IsNullOrWhiteSpace(weatherText))
            parts.Add(weatherText);
        var nextWindow = BuildTimedWindowText(timeSituation, "战略目标", "目标点", "control", "point");
        if (!string.IsNullOrWhiteSpace(nextWindow))
            parts.Add($"{nextWindow}，先封入口再让人读 8 秒");
        if (!string.IsNullOrWhiteSpace(choke.Id))
            parts.Add($"当前窄口 {choke.Label}");
        if (!string.IsNullOrWhiteSpace(highGround.Id))
            parts.Add($"当前高台 {highGround.Label}");
        if (!string.IsNullOrWhiteSpace(centerOrLatePoint.Id))
            parts.Add($"当前关键位 {DescribeObjective(centerOrLatePoint)}");
        else if (!string.IsNullOrWhiteSpace(active.Id))
            parts.Add($"当前收益点 {DescribeObjective(active)}");

        return string.Join("；", parts);
    }

    private static bool IsPrimaryDangerZone(BattlefieldMapTacticalZoneSnapshot zone)
        => zone.Kind is MapAnnotationKind.Danger or MapAnnotationKind.LowGround or MapAnnotationKind.Underpass or MapAnnotationKind.Flank
            || zone.IsMandatoryChoke
            || zone.TotalRisk >= 68f;

    private static BattlefieldMapTacticalZoneSnapshot FindZoneLabel(
        IReadOnlyList<BattlefieldMapTacticalZoneSnapshot> zones,
        params string[] fragments)
        => zones
            .Where(zone => ContainsAny(zone.Label, fragments))
            .OrderByDescending(zone => zone.TotalRisk)
            .ThenBy(zone => zone.Label, StringComparer.Ordinal)
            .FirstOrDefault();

    private static BattlefieldMapObjectiveSnapshot SelectMostValuableObjective(IReadOnlyList<BattlefieldMapObjectiveSnapshot> objectives)
        => objectives
            .Where(IsObjectiveWindowActive)
            .OrderByDescending(objective => objective.ScoreValue ?? 0)
            .ThenBy(objective => objective.RemainingSeconds ?? int.MaxValue)
            .ThenBy(objective => objective.Name, StringComparer.Ordinal)
            .FirstOrDefault();

    private static string DescribeDangerZone(BattlefieldMapTacticalZoneSnapshot zone)
        => zone.Kind switch
        {
            MapAnnotationKind.LowGround => "低地风险",
            MapAnnotationKind.Choke => "卡口风险",
            MapAnnotationKind.Flank => "侧翼入口风险",
            MapAnnotationKind.Underpass => "桥洞风险",
            MapAnnotationKind.Danger => "主要危险区",
            _ => zone.IsMandatoryChoke ? "必卡口风险" : "主要危险区",
        };

    private static bool IsObjectiveWindowActive(BattlefieldMapObjectiveSnapshot objective)
        => objective.State is BattlefieldMapObjectiveState.Warning or BattlefieldMapObjectiveState.Active or BattlefieldMapObjectiveState.Contested
            || objective.RemainingSeconds is > 0;

    private static bool IsCenterOnsalLocation(string locationId)
        => locationId is "01" or "11" or "12" or "13";

    private static bool IsVochesterPriorityLocation(string locationId)
        => locationId is "01" or "02" or "03" or "10" or "11" or "12";

    private static string BuildSealRockPhaseText(BattlefieldTimeSituationSnapshot timeSituation)
    {
        return (timeSituation.MapRuleMaxActiveObjectives, timeSituation.MapRuleMinimumObjectiveRank) switch
        {
            (4, "B") => "当前阶段：前期 4 石文铺开期，先手数量和高台首占都重要",
            (3, "A") => "当前阶段：后期 3 石文/A 级保底期，高质量石文比追击更值钱",
            _ => string.Empty
        };
    }

    private static string BuildOnsalPhaseText(BattlefieldTimeSituationSnapshot timeSituation)
    {
        return (timeSituation.MapRuleMaxActiveObjectives, timeSituation.MapRuleMinimumObjectiveRank) switch
        {
            (6, "B") => "当前阶段：前期 6 点铺开期，外围 B 点可交给分队，主团继续卡中心高价值带",
            (4, "B") => "当前阶段：中期 4 点期，01/11/12/13 的中心带开始更决定主团去向",
            (3, "A") => "当前阶段：后期 3 点/A 级期，桥口压缩和中心高台价值明显上升",
            (2, "S") => "当前阶段：决胜 2 点/S 级期，主团必须更集中，丢一个点就可能直接断节奏",
            _ => string.IsNullOrWhiteSpace(timeSituation.MapRulePhaseName) ? string.Empty : $"当前阶段：{timeSituation.MapRulePhaseName}"
        };
    }

    private static string BuildVochesterPhaseText(BattlefieldTimeSituationSnapshot timeSituation)
    {
        return (timeSituation.MapRuleMaxActiveObjectives, timeSituation.MapRuleMinimumObjectiveRank) switch
        {
            (6, "B") => "当前阶段：前期 6 点铺开期，10/11/12 与 01/02/03 是最稳的高等级区",
            (5, "B") => "当前阶段：中期 5 点期，中央与 10/11/12 的覆盖决定转点速度",
            (3, "B") => "当前阶段：后期 3 点压缩期，中央坡口和高台入口更容易变成决胜线",
            (2, "A") => "当前阶段：决胜 2 点高价值期，分兵成本很高，先封入口再读 8 秒",
            _ => string.IsNullOrWhiteSpace(timeSituation.MapRulePhaseName) ? string.Empty : $"当前阶段：{timeSituation.MapRulePhaseName}"
        };
    }

    private static string BuildVochesterWeatherText(BattlefieldAnnouncementSituationSnapshot announcements)
    {
        if (announcements.CurrentWeather == BattlefieldWeatherKind.Snow)
        {
            return $"{announcements.WeatherStateText}{FormatRemainingSeconds(announcements.WeatherRemainingSeconds)}，低地、洞口和坡口同时吃地形与雪人风险，留意雪精护盾";
        }

        if (announcements.CurrentWeather == BattlefieldWeatherKind.Aurora)
        {
            return $"{announcements.WeatherStateText}{FormatRemainingSeconds(announcements.WeatherRemainingSeconds)}，高等级点概率和极限槽增长都抬升，10/11/12 与 01/02/03 要提前抢位";
        }

        return string.Empty;
    }

    private static string BuildTimedWindowText(BattlefieldTimeSituationSnapshot timeSituation, params string[] fragments)
    {
        if (timeSituation.NextResourceSeconds is not > 0
            || string.IsNullOrWhiteSpace(timeSituation.NextResourceName)
            || fragments.Length > 0 && !ContainsAny($"{timeSituation.NextResourceName} {timeSituation.NextResourceSource}", fragments))
        {
            return string.Empty;
        }

        return $"下一轮{timeSituation.NextResourceName.Trim()} {timeSituation.NextResourceSeconds.Value} 秒";
    }

    private static string FormatRemainingSeconds(int? remainingSeconds)
        => remainingSeconds is > 0 ? $" {remainingSeconds.Value} 秒" : string.Empty;

    private static string DescribeObjective(BattlefieldMapObjectiveSnapshot objective)
    {
        var label = !string.IsNullOrWhiteSpace(objective.Name)
            ? objective.Name
            : CategoryText(objective.Category);
        if (!string.IsNullOrWhiteSpace(objective.RankName))
            label = $"{objective.RankName} {label}";

        var valueText = objective.ScoreValue is > 0 ? $" {objective.ScoreValue.Value}分" : string.Empty;
        var timerText = objective.RemainingSeconds is > 0 ? $" {objective.RemainingSeconds.Value}s" : string.Empty;
        return $"{label}{valueText}{timerText}".Trim();
    }

    private static string CategoryText(BattlefieldMapObjectiveCategory category)
        => category switch
        {
            BattlefieldMapObjectiveCategory.Base => "据点",
            BattlefieldMapObjectiveCategory.Tomelith => "石文",
            BattlefieldMapObjectiveCategory.Ice => "冰",
            BattlefieldMapObjectiveCategory.Ovoo => "无垢的大地",
            BattlefieldMapObjectiveCategory.StrategicPoint => "战略目标点",
            BattlefieldMapObjectiveCategory.Monster => "怪物",
            _ => "目标",
        };

    private static bool ContainsAny(string text, params string[] fragments)
    {
        if (string.IsNullOrWhiteSpace(text) || fragments.Length == 0)
            return false;

        foreach (var fragment in fragments)
        {
            if (!string.IsNullOrWhiteSpace(fragment)
                && text.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
