using Xunit;

namespace ai02.Tests;

public sealed class LlmStrategicTextResolverTests
{
    [Fact]
    public void ResolvePrimaryDisplayText_PrefersDecisionOverRecommendedAction()
    {
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            Decision = "Rotate to Safe Node and wait for the ridge fight to open.",
            RecommendedAction = "Rotate Safe Node",
            ActionType = "rotate"
        };

        var result = LlmStrategicTextResolver.ResolvePrimaryDisplayText(llmDecision);

        Assert.Equal("Rotate to Safe Node and wait for the ridge fight to open.", result);
    }

    [Fact]
    public void ResolveDirectiveSourceText_PrefersRecommendedActionForInternalLanding()
    {
        var llmDecision = new BattlefieldLlmStrategicDecisionSnapshot
        {
            Decision = "Rotate to Safe Node and wait for the ridge fight to open.",
            RecommendedAction = "Rotate Safe Node",
            ActionType = "rotate"
        };

        var result = LlmStrategicTextResolver.ResolveDirectiveSourceText(llmDecision);

        Assert.Equal("Rotate Safe Node", result);
    }
}
