using Xunit;

namespace ai02.Tests;

public sealed class BuiltInMapPointTuningTests
{
    [Fact]
    public void Apply_RenamesAndRaisesBorderlandCliffDanger()
    {
        var point = new MapAnnotationPoint
        {
            Kind = MapAnnotationKind.Choke,
            Label = "跳下去会死",
            RiskScore = 50
        };

        BuiltInMapPointTuning.Apply(FrontlineMapType.BorderlandRuinsSecure, point);

        Assert.Equal("断崖坠落区", point.Label);
        Assert.Equal(95, point.RiskScore);
    }

    [Fact]
    public void Apply_RenamesOnsalBridgeAndChokePoints()
    {
        var bridge = new MapAnnotationPoint
        {
            Kind = MapAnnotationKind.Bridge,
            Label = "01桥",
            RiskScore = 50
        };
        var choke = new MapAnnotationPoint
        {
            Kind = MapAnnotationKind.Choke,
            Label = "桥口子",
            RiskScore = 50
        };

        BuiltInMapPointTuning.Apply(FrontlineMapType.OnsalHakair, bridge);
        BuiltInMapPointTuning.Apply(FrontlineMapType.OnsalHakair, choke);

        Assert.Equal("01主桥", bridge.Label);
        Assert.Equal(76, bridge.RiskScore);
        Assert.Equal("中心桥口", choke.Label);
        Assert.Equal(84, choke.RiskScore);
    }

    [Fact]
    public void Apply_LowersVochesterHighGroundAndRaisesTrapRisk()
    {
        var highGround = new MapAnnotationPoint
        {
            Kind = MapAnnotationKind.HighGround,
            Label = "11点高台",
            RiskScore = 71
        };
        var trap = new MapAnnotationPoint
        {
            Kind = MapAnnotationKind.LowGround,
            Label = "11洞内区域",
            RiskScore = 80
        };

        BuiltInMapPointTuning.Apply(FrontlineMapType.Vochester, highGround);
        BuiltInMapPointTuning.Apply(FrontlineMapType.Vochester, trap);

        Assert.Equal("11点高台火力位", highGround.Label);
        Assert.Equal(52, highGround.RiskScore);
        Assert.Equal("11洞内低地陷阱", trap.Label);
        Assert.Equal(86, trap.RiskScore);
    }

    [Fact]
    public void Apply_RefinesFieldsAndSealLabels()
    {
        var fields = new MapAnnotationPoint
        {
            Kind = MapAnnotationKind.Choke,
            Label = "中央窄口",
            RiskScore = 50
        };
        var seal = new MapAnnotationPoint
        {
            Kind = MapAnnotationKind.Choke,
            Label = "桥口",
            RiskScore = 80
        };

        BuiltInMapPointTuning.Apply(FrontlineMapType.FieldsOfHonor, fields);
        BuiltInMapPointTuning.Apply(FrontlineMapType.SealRock, seal);

        Assert.Equal("中央窄口爆点", fields.Label);
        Assert.Equal(84, fields.RiskScore);
        Assert.Equal("主桥口", seal.Label);
        Assert.Equal(86, seal.RiskScore);
    }
}
