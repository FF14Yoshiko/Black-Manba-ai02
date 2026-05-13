using Xunit;

namespace ai02.Tests;

public sealed class FrontlinePresenceResolverTests
{
    [Fact]
    public void Resolve_ReturnsTrueWhenLiveScoreDetectionAlreadyEnteredFrontline()
    {
        var result = FrontlinePresenceResolver.Resolve(
            scoreReaderIsInFrontline: true,
            latestSnapshotIsInFrontline: false,
            latestSnapshotMatchesCurrentMap: false,
            hasKnownFrontlineMap: false);

        Assert.True(result);
    }

    [Fact]
    public void Resolve_IgnoresStaleLatestSnapshotAfterMapChanged()
    {
        var result = FrontlinePresenceResolver.Resolve(
            scoreReaderIsInFrontline: false,
            latestSnapshotIsInFrontline: true,
            latestSnapshotMatchesCurrentMap: false,
            hasKnownFrontlineMap: false);

        Assert.False(result);
    }

    [Fact]
    public void Resolve_KeepsCurrentFrontlineSnapshotWhenLiveSignalsMomentarilyMiss()
    {
        var result = FrontlinePresenceResolver.Resolve(
            scoreReaderIsInFrontline: false,
            latestSnapshotIsInFrontline: true,
            latestSnapshotMatchesCurrentMap: true,
            hasKnownFrontlineMap: false);

        Assert.True(result);
    }
}
