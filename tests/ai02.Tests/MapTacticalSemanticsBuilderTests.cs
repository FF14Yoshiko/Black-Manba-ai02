using System.Numerics;
using Xunit;

namespace ai02.Tests;

public sealed class MapTacticalSemanticsBuilderTests
{
    [Fact]
    public void Build_UsesTerrainSemanticsInsteadOfRouteIdInRecommendation()
    {
        var summary = MapTacticalSemanticsBuilder.Build(
            FrontlineMapType.OnsalHakair,
            zones:
            [
                BattlefieldTestFactory.Zone(MapAnnotationKind.LowGround, new Vector3(10f, 0f, 0f), 18f, 86f, 78f, 84f, "强制绕路") with { Label = "中场低地" },
                BattlefieldTestFactory.Zone(MapAnnotationKind.HighGround, new Vector3(22f, 6f, 8f), 16f, 38f, 34f, 40f, "可防守占高") with { Label = "左侧高台" }
            ],
            routes:
            [
                new BattlefieldMapTacticalRouteSnapshot("west-route", "动态寻路", 3, 72f, 7, 12, 22f, 28f, 35f, false, false, "可走", "sample")
            ],
            heatPoints: [],
            objectives: [],
            timeSituation: new BattlefieldTimeSituationSnapshot(),
            announcements: new BattlefieldAnnouncementSituationSnapshot(),
            highGroundCount: 1,
            lowGroundCount: 1,
            jumpPadCount: 0,
            teleporterCount: 0,
            flankEntryCount: 1,
            bridgeCount: 0,
            underpassCount: 0,
            mandatoryChokeCount: 1,
            oneWayPassageCount: 0,
            teamSituation: new BattlefieldTeamSituationSnapshot
            {
                Friendly = new BattlefieldTeamSummarySnapshot { Side = BattlefieldTacticalSide.Friendly, Name = "我方", NearCount = 8, MidCount = 3 },
                Enemy = new BattlefieldTeamSummarySnapshot { Side = BattlefieldTacticalSide.Enemy, Name = "敌方", NearCount = 6, MidCount = 2 }
            });

        Assert.Contains("中场低地", summary.CurrentRecommendation);
        Assert.DoesNotContain("west-route", summary.CurrentRecommendation);
        Assert.Contains("低地风险", summary.DangerSummaryText);
        Assert.Contains("高台 1", summary.TerrainAdvantageSummaryText);
        Assert.Contains("必卡点 1", summary.PassabilitySummaryText);
    }

    [Fact]
    public void Build_DescribesMapSpecificRewardModel()
    {
        var summary = MapTacticalSemanticsBuilder.Build(
            FrontlineMapType.FieldsOfHonor,
            zones: [],
            routes: [],
            heatPoints: [],
            objectives:
            [
                new BattlefieldMapObjectiveSnapshot(
                    "obj-1",
                    FrontlineMapType.FieldsOfHonor,
                    BattlefieldMapObjectiveCategory.Ice,
                    BattlefieldMapObjectiveState.Active,
                    new Vector3(20f, 0f, 20f),
                    null,
                    null,
                    "冰封石文",
                    "ice-01",
                    "大",
                    null,
                    string.Empty,
                    24,
                    "test",
                    null,
                    null,
                    null,
                    200,
                    0,
                    0,
                    0,
                    0,
                    false,
                    false,
                    null,
                    0f,
                    null,
                    [],
                    string.Empty,
                    string.Empty,
                    [],
                    string.Empty,
                    1f)
            ],
            highGroundCount: 0,
            lowGroundCount: 0,
            jumpPadCount: 0,
            teleporterCount: 0,
            flankEntryCount: 0,
            bridgeCount: 0,
            underpassCount: 0,
            mandatoryChokeCount: 0,
            oneWayPassageCount: 0,
            timeSituation: new BattlefieldTimeSituationSnapshot(),
            announcements: new BattlefieldAnnouncementSituationSnapshot(),
            teamSituation: new BattlefieldTeamSituationSnapshot());

        Assert.Contains("大冰/小冰", summary.RewardModelSummaryText);
        Assert.Contains("当前窗口", summary.RewardModelSummaryText);
        Assert.Contains("大 冰封石文", summary.RewardModelSummaryText);
        Assert.Contains("右侧劣势冰", summary.MapKnowledgeFocusText);
        Assert.Contains("大冰是主团窗口", summary.MapKnowledgeFocusText);
    }

    [Fact]
    public void Build_DescribesOnsalCenterPriorityAndBridgeInterception()
    {
        var summary = MapTacticalSemanticsBuilder.Build(
            FrontlineMapType.OnsalHakair,
            zones:
            [
                BattlefieldTestFactory.Zone(MapAnnotationKind.Choke, new Vector3(10f, 0f, 0f), 6f, 74f, 68f, 72f, "谨慎通过") with { Label = "天桥窄路口" },
                BattlefieldTestFactory.Zone(MapAnnotationKind.Bridge, new Vector3(14f, 0f, 4f), 10f, 58f, 54f, 60f, "可走") with { Label = "01桥" }
            ],
            routes: [],
            heatPoints: [],
            objectives:
            [
                new BattlefieldMapObjectiveSnapshot(
                    "obj-01",
                    FrontlineMapType.OnsalHakair,
                    BattlefieldMapObjectiveCategory.Ovoo,
                    BattlefieldMapObjectiveState.Warning,
                    new Vector3(20f, 0f, 20f),
                    null,
                    null,
                    "无垢的大地",
                    "01",
                    "A",
                    null,
                    string.Empty,
                    18,
                    "test",
                    null,
                    null,
                    null,
                    100,
                    0,
                    0,
                    0,
                    0,
                    false,
                    false,
                    null,
                    0f,
                    null,
                    [],
                    string.Empty,
                    string.Empty,
                    [],
                    string.Empty,
                    1f)
            ],
            timeSituation: new BattlefieldTimeSituationSnapshot(),
            announcements: new BattlefieldAnnouncementSituationSnapshot(),
            highGroundCount: 1,
            lowGroundCount: 0,
            jumpPadCount: 0,
            teleporterCount: 0,
            flankEntryCount: 0,
            bridgeCount: 1,
            underpassCount: 1,
            mandatoryChokeCount: 1,
            oneWayPassageCount: 0,
            teamSituation: new BattlefieldTeamSituationSnapshot());

        Assert.Contains("01/11/12/13", summary.MapKnowledgeFocusText);
        Assert.Contains("天桥窄路口", summary.MapKnowledgeFocusText);
        Assert.Contains("当前中心高价值点", summary.MapKnowledgeFocusText);
        Assert.Contains("A 无垢的大地 100分 18s", summary.MapKnowledgeFocusText);
    }

    [Fact]
    public void Build_DescribesVochesterCaptureAndRotationDiscipline()
    {
        var summary = MapTacticalSemanticsBuilder.Build(
            FrontlineMapType.Vochester,
            zones:
            [
                BattlefieldTestFactory.Zone(MapAnnotationKind.Choke, new Vector3(10f, 0f, 0f), 6f, 66f, 62f, 68f, "谨慎通过") with { Label = "洞口" },
                BattlefieldTestFactory.Zone(MapAnnotationKind.HighGround, new Vector3(12f, 5f, 2f), 16f, 42f, 36f, 45f, "可防守占高") with { Label = "11点高台" }
            ],
            routes: [],
            heatPoints: [],
            objectives:
            [
                new BattlefieldMapObjectiveSnapshot(
                    "obj-11",
                    FrontlineMapType.Vochester,
                    BattlefieldMapObjectiveCategory.StrategicPoint,
                    BattlefieldMapObjectiveState.Active,
                    new Vector3(24f, 0f, 20f),
                    null,
                    null,
                    "战略目标点",
                    "11",
                    "S",
                    null,
                    string.Empty,
                    26,
                    "test",
                    null,
                    null,
                    null,
                    200,
                    0,
                    0,
                    0,
                    0,
                    false,
                    false,
                    null,
                    0f,
                    null,
                    [],
                    string.Empty,
                    string.Empty,
                    [],
                    string.Empty,
                    1f)
            ],
            timeSituation: new BattlefieldTimeSituationSnapshot(),
            announcements: new BattlefieldAnnouncementSituationSnapshot(),
            highGroundCount: 2,
            lowGroundCount: 1,
            jumpPadCount: 0,
            teleporterCount: 0,
            flankEntryCount: 0,
            bridgeCount: 1,
            underpassCount: 1,
            mandatoryChokeCount: 1,
            oneWayPassageCount: 0,
            teamSituation: new BattlefieldTeamSituationSnapshot());

        Assert.Contains("8 秒", summary.MapKnowledgeFocusText);
        Assert.Contains("11点高台", summary.MapKnowledgeFocusText);
        Assert.Contains("当前关键位", summary.MapKnowledgeFocusText);
        Assert.Contains("S 战略目标点 200分 26s", summary.MapKnowledgeFocusText);
    }

    [Fact]
    public void Build_DescribesOnsalPhaseWindowFromTimeSituation()
    {
        var summary = MapTacticalSemanticsBuilder.Build(
            FrontlineMapType.OnsalHakair,
            zones:
            [
                BattlefieldTestFactory.Zone(MapAnnotationKind.Choke, new Vector3(10f, 0f, 0f), 6f, 74f, 68f, 72f, "谨慎通过") with { Label = "天桥窄路口" },
                BattlefieldTestFactory.Zone(MapAnnotationKind.Bridge, new Vector3(14f, 0f, 4f), 10f, 58f, 54f, 60f, "可走") with { Label = "01主桥" }
            ],
            routes: [],
            heatPoints: [],
            objectives: [],
            timeSituation: new BattlefieldTimeSituationSnapshot
            {
                HasMatchTime = true,
                MapRulePhaseName = "5:00-0:00",
                MapRuleMaxActiveObjectives = 2,
                MapRuleMinimumObjectiveRank = "S",
                NextResourceSeconds = 18,
                NextResourceName = "无垢的大地"
            },
            announcements: new BattlefieldAnnouncementSituationSnapshot(),
            highGroundCount: 1,
            lowGroundCount: 0,
            jumpPadCount: 0,
            teleporterCount: 0,
            flankEntryCount: 0,
            bridgeCount: 1,
            underpassCount: 0,
            mandatoryChokeCount: 1,
            oneWayPassageCount: 0,
            teamSituation: new BattlefieldTeamSituationSnapshot());

        Assert.Contains("决胜 2 点/S 级期", summary.MapKnowledgeFocusText);
        Assert.Contains("下一轮无垢的大地 18 秒", summary.MapKnowledgeFocusText);
        Assert.Contains("01主桥", summary.MapKnowledgeFocusText);
    }

    [Fact]
    public void Build_DescribesVochesterWeatherAndPhaseWindows()
    {
        var summary = MapTacticalSemanticsBuilder.Build(
            FrontlineMapType.Vochester,
            zones:
            [
                BattlefieldTestFactory.Zone(MapAnnotationKind.Choke, new Vector3(10f, 0f, 0f), 6f, 66f, 62f, 68f, "谨慎通过") with { Label = "中央坡口" },
                BattlefieldTestFactory.Zone(MapAnnotationKind.HighGround, new Vector3(12f, 5f, 2f), 16f, 42f, 36f, 45f, "可防守占高") with { Label = "11点高台火力位" }
            ],
            routes: [],
            heatPoints: [],
            objectives: [],
            timeSituation: new BattlefieldTimeSituationSnapshot
            {
                HasMatchTime = true,
                MapRulePhaseName = "10:00-5:00",
                MapRuleMaxActiveObjectives = 3,
                MapRuleMinimumObjectiveRank = "B",
                NextResourceSeconds = 26,
                NextResourceName = "战略目标点"
            },
            announcements: new BattlefieldAnnouncementSituationSnapshot
            {
                CurrentWeather = BattlefieldWeatherKind.Aurora,
                CurrentWeatherName = "极光",
                WeatherStateText = "极光进行中",
                WeatherRemainingSeconds = 84
            },
            highGroundCount: 2,
            lowGroundCount: 1,
            jumpPadCount: 0,
            teleporterCount: 0,
            flankEntryCount: 0,
            bridgeCount: 1,
            underpassCount: 1,
            mandatoryChokeCount: 1,
            oneWayPassageCount: 0,
            teamSituation: new BattlefieldTeamSituationSnapshot());

        Assert.Contains("后期 3 点压缩期", summary.MapKnowledgeFocusText);
        Assert.Contains("极光进行中 84 秒", summary.MapKnowledgeFocusText);
        Assert.Contains("高等级点概率和极限槽增长都抬升", summary.MapKnowledgeFocusText);
        Assert.Contains("下一轮战略目标点 26 秒", summary.MapKnowledgeFocusText);
    }
}
