namespace ai02;

internal static class LlmStrategicTextResolver
{
    internal static string ResolvePrimaryDisplayText(BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!string.IsNullOrWhiteSpace(llmDecision.Decision))
            return llmDecision.Decision.Trim();
        if (!string.IsNullOrWhiteSpace(llmDecision.RecommendedAction))
            return llmDecision.RecommendedAction.Trim();
        if (!string.IsNullOrWhiteSpace(llmDecision.ActionType))
            return llmDecision.ActionType.Trim();

        return string.Empty;
    }

    internal static string ResolveDirectiveSourceText(BattlefieldLlmStrategicDecisionSnapshot llmDecision)
    {
        if (!string.IsNullOrWhiteSpace(llmDecision.RecommendedAction))
            return llmDecision.RecommendedAction.Trim();
        if (!string.IsNullOrWhiteSpace(llmDecision.Decision))
            return llmDecision.Decision.Trim();
        if (!string.IsNullOrWhiteSpace(llmDecision.ActionType))
            return llmDecision.ActionType.Trim();

        return string.Empty;
    }
}
