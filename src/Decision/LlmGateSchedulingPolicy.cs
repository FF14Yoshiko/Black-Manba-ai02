namespace ai02;

internal static class LlmGateSchedulingPolicy
{
    public static GateSchedulingDecision ApplyRoutinePulseGate(
        BattlefieldLlmDecisionNeedKind needKind,
        bool routinePulseEnabled,
        int routinePulseIntervalSeconds,
        long lastRequestTicks,
        long nowTicks)
    {
        var evaluation = LlmRoutinePulsePolicy.Evaluate(
            needKind,
            routinePulseEnabled,
            routinePulseIntervalSeconds,
            lastRequestTicks,
            nowTicks);
        if (evaluation.IsDisabled)
        {
            return GateSchedulingDecision.Waiting(
                "固定局内 AI 分析已关闭，仅事件触发",
                0);
        }

        if (!evaluation.IsDue)
        {
            return GateSchedulingDecision.Waiting(
                $"固定局内分析未到，距离下一次常规战略采样还剩 {evaluation.RemainingSeconds} 秒",
                evaluation.RemainingSeconds);
        }

        return GateSchedulingDecision.Allowed;
    }

    internal readonly record struct GateSchedulingDecision(bool ShouldRequest, string WaitReason, int RemainingSeconds)
    {
        public static GateSchedulingDecision Allowed => new(true, string.Empty, 0);

        public static GateSchedulingDecision Waiting(string waitReason, int remainingSeconds)
            => new(false, waitReason, remainingSeconds);
    }
}
