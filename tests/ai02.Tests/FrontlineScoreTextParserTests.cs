using Xunit;

namespace ai02.Tests;

public sealed class FrontlineScoreTextParserTests
{
    [Fact]
    public void TryExtractFractionScore_ParsesScoreLimit()
    {
        Assert.True(FrontlineScoreTextParser.TryExtractFractionScore("黑涡团 950/1600", out var score, out var limit));
        Assert.Equal(950, score);
        Assert.Equal(1600, limit);
    }

    [Fact]
    public void TryExtractFractionScore_IgnoresTimerText()
    {
        Assert.False(FrontlineScoreTextParser.TryExtractFractionScore("剩余时间 12:30", out _, out _));
    }

    [Fact]
    public void TryBuildStructuredScoreResult_BuildsAllianceTriple()
    {
        var rows = new[]
        {
            new FrontlineStructuredScoreRow(0, "黑涡团 950/1600", 0, null, "todo"),
            new FrontlineStructuredScoreRow(1, "双蛇党 820/1600", 0, null, "todo"),
            new FrontlineStructuredScoreRow(2, "恒辉队 780/1600", 0, null, "todo")
        };

        Assert.True(FrontlineScoreTextParser.TryBuildStructuredScoreResult(rows, "test", out var result));
        Assert.Equal(950, result.Maelstrom);
        Assert.Equal(820, result.TwinAdder);
        Assert.Equal(780, result.ImmortalFlames);
        Assert.Equal(1600, result.ScoreLimit);
    }

    [Fact]
    public void TryExtractStructuredRowScore_UsesStructuredValueWhenTextHasNoFraction()
    {
        var row = new FrontlineStructuredScoreRow(0, "黑涡团 当前分数", 1120, 1600, "todo");

        Assert.True(FrontlineScoreTextParser.TryExtractStructuredRowScore(row, out var score, out var scoreLimit));
        Assert.Equal(1120, score);
        Assert.Equal(1600, scoreLimit);
    }
}
