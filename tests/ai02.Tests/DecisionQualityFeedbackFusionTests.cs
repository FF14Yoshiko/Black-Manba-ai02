using System.Linq;
using Xunit;

namespace ai02.Tests;

public sealed class DecisionQualityFeedbackFusionTests
{
    [Fact]
    public void Merge_PrefersTeacherBiasWhenKindsOverlap()
    {
        var replay = new[]
        {
            new BattlefieldCommandEffectivenessSnapshot(BattlefieldCommandKind.Rotate, 6, -4f, 0.33f, -2f, "replay"),
            new BattlefieldCommandEffectivenessSnapshot(BattlefieldCommandKind.Hold, 5, 2f, 0.60f, 1f, "hold")
        };
        var teacher = new[]
        {
            new BattlefieldCommandEffectivenessSnapshot(BattlefieldCommandKind.Rotate, 4, 18f, 1f, 6f, "teacher")
        };

        var merged = DecisionQualityFeedbackFusion.Merge(replay, teacher);
        var rotate = merged.Single(item => item.Kind == BattlefieldCommandKind.Rotate);
        var hold = merged.Single(item => item.Kind == BattlefieldCommandKind.Hold);

        Assert.Equal(10, rotate.SampleCount);
        Assert.True(rotate.Modifier > 0f);
        Assert.Contains("AI老师+回放", rotate.SummaryText);
        Assert.Equal(1f, hold.Modifier);
    }
}
