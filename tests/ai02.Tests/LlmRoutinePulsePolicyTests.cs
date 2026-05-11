using Xunit;

namespace ai02.Tests;

public sealed class LlmRoutinePulsePolicyTests
{
    [Fact]
    public void Evaluate_AllowsImmediateRequest_WhenNeverRequested()
    {
        var result = LlmRoutinePulsePolicy.Evaluate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            enabled: true,
            intervalSeconds: 25,
            lastRequestTicks: -1,
            nowTicks: 100_000);

        Assert.True(result.IsDue);
        Assert.Equal(0, result.RemainingSeconds);
    }

    [Fact]
    public void Evaluate_BlocksUntilIntervalElapses()
    {
        var result = LlmRoutinePulsePolicy.Evaluate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            enabled: true,
            intervalSeconds: 25,
            lastRequestTicks: 100_000,
            nowTicks: 112_400);

        Assert.False(result.IsDue);
        Assert.Equal(13, result.RemainingSeconds);
    }

    [Fact]
    public void Evaluate_AllowsRequest_WhenIntervalElapsed()
    {
        var result = LlmRoutinePulsePolicy.Evaluate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            enabled: true,
            intervalSeconds: 25,
            lastRequestTicks: 100_000,
            nowTicks: 125_000);

        Assert.True(result.IsDue);
        Assert.Equal(0, result.RemainingSeconds);
    }

    [Fact]
    public void Evaluate_DisabledPolicyDoesNotThrottle()
    {
        var result = LlmRoutinePulsePolicy.Evaluate(
            BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse,
            enabled: false,
            intervalSeconds: 25,
            lastRequestTicks: 100_000,
            nowTicks: 101_000);

        Assert.True(result.IsDue);
        Assert.Equal(0, result.RemainingSeconds);
    }

    [Fact]
    public void Evaluate_EventDrivenNeed_IsNotBlockedByRoutinePulseInterval()
    {
        var result = LlmRoutinePulsePolicy.Evaluate(
            BattlefieldLlmDecisionNeedKind.NearbyThirdPartyFight,
            enabled: true,
            intervalSeconds: 25,
            lastRequestTicks: 100_000,
            nowTicks: 101_000);

        Assert.True(result.IsDue);
        Assert.Equal(0, result.RemainingSeconds);
    }

    [Fact]
    public void ShouldThrottle_OnlyRoutineStrategicPulse()
    {
        Assert.True(LlmRoutinePulsePolicy.ShouldThrottle(BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse));
        Assert.False(LlmRoutinePulsePolicy.ShouldThrottle(BattlefieldLlmDecisionNeedKind.ScoreEndgameConflict));
        Assert.False(LlmRoutinePulsePolicy.ShouldThrottle(BattlefieldLlmDecisionNeedKind.ObjectiveRace));
        Assert.False(LlmRoutinePulsePolicy.ShouldThrottle(BattlefieldLlmDecisionNeedKind.UnstableThreeFaction));
    }
}
