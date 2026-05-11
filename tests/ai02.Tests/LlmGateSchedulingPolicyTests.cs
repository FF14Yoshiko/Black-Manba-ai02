using Xunit;

namespace ai02.Tests;

public sealed class LlmGateSchedulingPolicyTests
{
    [Fact]
    public void ApplyRoutinePulseGate_BlocksRoutineGate_WhenIntervalNotElapsed()
    {
        var result = LlmGateSchedulingPolicy.ApplyRoutinePulseGate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            routinePulseEnabled: true,
            routinePulseIntervalSeconds: 25,
            lastRequestTicks: 100_000,
            nowTicks: 112_400);

        Assert.False(result.ShouldRequest);
        Assert.Equal(13, result.RemainingSeconds);
        Assert.Contains("13", result.WaitReason);
    }

    [Fact]
    public void ApplyRoutinePulseGate_AllowsEventGate_WithSameLastRequestTicks()
    {
        var result = LlmGateSchedulingPolicy.ApplyRoutinePulseGate(
            BattlefieldLlmDecisionNeedKind.NearbyThirdPartyFight,
            routinePulseEnabled: true,
            routinePulseIntervalSeconds: 25,
            lastRequestTicks: 100_000,
            nowTicks: 112_400);

        Assert.True(result.ShouldRequest);
        Assert.Equal(0, result.RemainingSeconds);
        Assert.Equal(string.Empty, result.WaitReason);
    }
}
