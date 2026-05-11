using Xunit;

namespace ai02.Tests;

public sealed class LlmDecisionResponseParserTests
{
    [Fact]
    public void Parse_HandlesAliasFieldsAndDebugPayload()
    {
        const string json = """
        {
          "decision": "转点抢高价值点",
          "shortReason": "敌方主团还没到",
          "recommendedAction": "转点 高价值点",
          "priorityTarget": "高价值点",
          "confidence": "88",
          "risk": 22,
          "debug": {
            "scoreRead": "敌一领先",
            "position_read": "我方在中路",
            "latency_note": "2s stale"
          }
        }
        """;

        var parsed = LlmDecisionResponseParser.Parse(json);

        Assert.Equal("转点抢高价值点", parsed.Decision);
        Assert.Equal("敌方主团还没到", parsed.ShortReason);
        Assert.Equal("转点 高价值点", parsed.RecommendedAction);
        Assert.Equal("高价值点", parsed.PriorityTarget);
        Assert.Equal(88f, parsed.Confidence);
        Assert.Equal(22f, parsed.Risk);
        Assert.Equal("敌一领先", parsed.DebugScoreRead);
        Assert.Equal("我方在中路", parsed.DebugPositionRead);
        Assert.Equal("2s stale", parsed.DebugLatencyNote);
    }

    [Fact]
    public void Parse_FallsBackAndClampsValues()
    {
        const string json = """
        {
          "recommended_action": "打第一 敌方第一",
          "confidence": 140,
          "risk": -10
        }
        """;

        var parsed = LlmDecisionResponseParser.Parse(json);

        Assert.Equal("打第一 敌方第一", parsed.Decision);
        Assert.Equal("打第一 敌方第一", parsed.RecommendedAction);
        Assert.Equal(100f, parsed.Confidence);
        Assert.Equal(0f, parsed.Risk);
    }
}
