using Xunit;

namespace ai02.Tests;

public sealed class FrontlineChatEventTextParserTests
{
    [Fact]
    public void TryParse_ParsesFriendlyKillFromEnglishCombatLog()
    {
        Assert.True(
            FrontlineChatEventTextParser.TryParse(
                "You defeated Enemy Sage.",
                string.Empty,
                BattlefieldTacticalSide.Unknown,
                BattlefieldTacticalSide.Enemy,
                out var parsed));

        Assert.Equal(BattlefieldChatEventKind.Kill, parsed.Kind);
        Assert.Equal("你", parsed.ActorName);
        Assert.Equal("Enemy Sage", parsed.TargetName);
        Assert.Equal(BattlefieldTacticalSide.Friendly, parsed.ActorSide);
        Assert.Equal(BattlefieldTacticalSide.Enemy, parsed.TargetSide);
    }

    [Fact]
    public void TryParse_ParsesBattleHighLevelAndDelta()
    {
        Assert.True(
            FrontlineChatEventTextParser.TryParse(
                "You attain Battle High IV +20.",
                string.Empty,
                BattlefieldTacticalSide.Unknown,
                BattlefieldTacticalSide.Unknown,
                out var parsed));

        Assert.Equal(BattlefieldChatEventKind.BattleHigh, parsed.Kind);
        Assert.Equal("你", parsed.ActorName);
        Assert.Equal(4, parsed.BattleHighLevel);
        Assert.Equal(20, parsed.BattleHighDelta);
    }

    [Fact]
    public void TryParse_ParsesObjectiveCaptureLocationAndOwnership()
    {
        Assert.True(
            FrontlineChatEventTextParser.TryParse(
                "Maelstrom secured the control point at 3.",
                string.Empty,
                BattlefieldTacticalSide.Unknown,
                BattlefieldTacticalSide.Unknown,
                out var parsed));

        Assert.Equal(BattlefieldChatEventKind.ObjectiveCaptured, parsed.Kind);
        Assert.Equal(NodeOwnership.Maelstrom, parsed.Ownership);
        Assert.Equal("03", parsed.LocationId);
        Assert.Equal("目标点", parsed.ObjectiveName);
    }
}
