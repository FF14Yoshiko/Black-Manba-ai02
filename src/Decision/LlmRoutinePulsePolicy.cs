using System;

namespace ai02;

internal static class LlmRoutinePulsePolicy
{
    public static RoutinePulseEvaluation Evaluate(
        BattlefieldLlmDecisionNeedKind needKind,
        bool enabled,
        int intervalSeconds,
        long lastRequestTicks,
        long nowTicks)
    {
        if (!ShouldThrottle(needKind))
            return RoutinePulseEvaluation.Bypassed;

        if (!enabled)
            return RoutinePulseEvaluation.Disabled;

        var intervalMs = Math.Max(10000L, intervalSeconds * 1000L);
        if (lastRequestTicks < 0)
            return new RoutinePulseEvaluation(true, 0, false);

        var elapsedMs = Math.Max(0L, nowTicks - lastRequestTicks);
        if (elapsedMs >= intervalMs)
            return new RoutinePulseEvaluation(true, 0, false);

        var remainingSeconds = Math.Max(1, (int)Math.Ceiling((intervalMs - elapsedMs) / 1000d));
        return new RoutinePulseEvaluation(false, remainingSeconds, false);
    }

    public static bool ShouldThrottle(BattlefieldLlmDecisionNeedKind needKind)
        => needKind == BattlefieldLlmDecisionNeedKind.RoutineStrategicPulse;

    internal readonly record struct RoutinePulseEvaluation(bool IsDue, int RemainingSeconds, bool IsDisabled)
    {
        public static RoutinePulseEvaluation Disabled => new(false, 0, true);
        public static RoutinePulseEvaluation Bypassed => new(true, 0, false);
    }
}
