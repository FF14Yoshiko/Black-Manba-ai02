using System.Numerics;
using Xunit;

namespace ai02.Tests;

public sealed class MapRouteRiskEvaluatorTests
{
    [Fact]
    public void Evaluate_MarksDangerousLowGroundRoutesAsDetours()
    {
        var route = MapRouteRiskEvaluator.Evaluate(
            "route-a",
            "Rotation",
            new[]
            {
                Vector3.Zero,
                new Vector3(50f, 0f, 0f),
                new Vector3(100f, 0f, 0f)
            },
            new[]
            {
                BattlefieldTestFactory.Zone(MapAnnotationKind.LowGround, new Vector3(50f, 0f, 0f), 20f, 82f, 72f, 82f)
            },
            new[]
            {
                new BattlefieldMapHeatPointSnapshot(new Vector3(50f, 0f, 0f), 16f, 76f, "heat")
            },
            "target north");

        Assert.True(route.CrossesDangerZone);
        Assert.True(route.TotalRisk >= 75f);
        Assert.Equal("绕路", route.Recommendation);
    }
}
