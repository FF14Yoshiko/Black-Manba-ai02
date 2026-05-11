using Xunit;

namespace ai02.Tests;

public sealed class FrontlineScoreConfirmationPolicyTests
{
    [Fact]
    public void TryApplyCandidate_AcceptsSmallIncreaseImmediately()
    {
        var current = 900;
        var pending = FrontlineScoreConfirmationPolicy.NoPendingScore;
        var pendingReads = 0;

        var accepted = FrontlineScoreConfirmationPolicy.TryApplyCandidate(
            ref current,
            ref pending,
            ref pendingReads,
            980,
            true);

        Assert.True(accepted);
        Assert.Equal(980, current);
        Assert.Equal(FrontlineScoreConfirmationPolicy.NoPendingScore, pending);
        Assert.Equal(0, pendingReads);
    }

    [Fact]
    public void TryApplyCandidate_AcceptsTrustedDecreaseImmediately()
    {
        var current = 1000;
        var pending = FrontlineScoreConfirmationPolicy.NoPendingScore;
        var pendingReads = 0;

        var accepted = FrontlineScoreConfirmationPolicy.TryApplyCandidate(
            ref current,
            ref pending,
            ref pendingReads,
            950,
            true);

        Assert.True(accepted);
        Assert.Equal(950, current);
        Assert.Equal(FrontlineScoreConfirmationPolicy.NoPendingScore, pending);
        Assert.Equal(0, pendingReads);
    }

    [Fact]
    public void TryApplyCandidate_HoldsSuspiciousDecreaseUntilSecondMatchingRead()
    {
        var current = 1000;
        var pending = FrontlineScoreConfirmationPolicy.NoPendingScore;
        var pendingReads = 0;

        var firstAccepted = FrontlineScoreConfirmationPolicy.TryApplyCandidate(
            ref current,
            ref pending,
            ref pendingReads,
            900,
            true);

        Assert.False(firstAccepted);
        Assert.Equal(1000, current);
        Assert.Equal(900, pending);
        Assert.Equal(1, pendingReads);

        var secondAccepted = FrontlineScoreConfirmationPolicy.TryApplyCandidate(
            ref current,
            ref pending,
            ref pendingReads,
            900,
            true);

        Assert.True(secondAccepted);
        Assert.Equal(900, current);
        Assert.Equal(FrontlineScoreConfirmationPolicy.NoPendingScore, pending);
        Assert.Equal(0, pendingReads);
    }

    [Fact]
    public void TryApplyCandidate_RejectsExtremeDropAndClearsPending()
    {
        var current = 1000;
        var pending = 900;
        var pendingReads = 1;

        var accepted = FrontlineScoreConfirmationPolicy.TryApplyCandidate(
            ref current,
            ref pending,
            ref pendingReads,
            840,
            true);

        Assert.False(accepted);
        Assert.Equal(1000, current);
        Assert.Equal(FrontlineScoreConfirmationPolicy.NoPendingScore, pending);
        Assert.Equal(0, pendingReads);
    }
}
