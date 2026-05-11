using Xunit;

namespace ai02.Tests;

public sealed class FrontlineAnnouncementTextParserTests
{
    [Fact]
    public void TryParse_ParsesWeatherWarningCountdown()
    {
        Assert.True(
            FrontlineAnnouncementTextParser.TryParse(
                "PvPFrontlineInfo",
                "Battlefield weather: Aurora about to begin in 00:15.",
                123L,
                out var parsed));

        Assert.Equal(BattlefieldAnnouncementKind.WeatherWarning, parsed.Kind);
        Assert.Equal(BattlefieldWeatherKind.Aurora, parsed.Weather);
        Assert.Equal(15, parsed.CountdownSeconds);
        Assert.Equal("极光预告（15秒）", parsed.SummaryText);
    }

    [Fact]
    public void TryParse_ParsesObjectiveAvailabilityRankAndLocation()
    {
        Assert.True(
            FrontlineAnnouncementTextParser.TryParse(
                "PvPFrontlineInfo",
                "Strategic target rank S is now available at 3.",
                456L,
                out var parsed));

        Assert.Equal(BattlefieldAnnouncementKind.ObjectiveAvailable, parsed.Kind);
        Assert.Equal("03", parsed.LocationId);
        Assert.Equal("S", parsed.RankName);
        Assert.Equal("S级战略目标点可控制", parsed.SummaryText);
    }

    [Fact]
    public void TryParse_ParsesObjectiveControlOwnership()
    {
        Assert.True(
            FrontlineAnnouncementTextParser.TryParse(
                "PvPFrontlineInfo",
                "Maelstrom controlled the strategic target.",
                789L,
                out var parsed));

        Assert.Equal(BattlefieldAnnouncementKind.ObjectiveControlled, parsed.Kind);
        Assert.Equal(NodeOwnership.Maelstrom, parsed.Ownership);
        Assert.Equal("黑涡团控制战略目标点", parsed.SummaryText);
    }
}
