namespace ai02;

internal static class FrontlineScoreConfirmationPolicy
{
    internal const int NoPendingScore = -1;

    private const int MaxReadableScore = 3000;
    private const int MaxUnconfirmedScoreIncrease = 120;
    private const int MaxTrustedScoreDecrease = 64;
    private const int MaxHardRejectedScoreDecrease = 120;
    private const int ScoreChangeConfirmationReads = 2;

    internal static bool TryApplyCandidate(
        ref int currentScore,
        ref int pendingScore,
        ref int pendingReads,
        int incomingScore,
        bool countConfirmationRead)
    {
        if (!IsValidFrontlineScore(incomingScore))
            return false;

        if (currentScore == incomingScore)
        {
            ClearPendingScore(ref pendingScore, ref pendingReads);
            return true;
        }

        if (currentScore <= 0)
        {
            currentScore = incomingScore;
            ClearPendingScore(ref pendingScore, ref pendingReads);
            return true;
        }

        var increase = incomingScore - currentScore;
        var decrease = currentScore - incomingScore;

        if (increase is > 0 and <= MaxUnconfirmedScoreIncrease)
        {
            currentScore = incomingScore;
            ClearPendingScore(ref pendingScore, ref pendingReads);
            return true;
        }

        if (decrease is > 0 and <= MaxTrustedScoreDecrease)
        {
            currentScore = incomingScore;
            ClearPendingScore(ref pendingScore, ref pendingReads);
            return true;
        }

        if (decrease > MaxHardRejectedScoreDecrease)
        {
            ClearPendingScore(ref pendingScore, ref pendingReads);
            return false;
        }

        if (pendingScore == incomingScore)
        {
            if (countConfirmationRead)
                pendingReads++;
        }
        else
        {
            pendingScore = incomingScore;
            pendingReads = countConfirmationRead ? 1 : 0;
        }

        if (pendingReads < ScoreChangeConfirmationReads)
            return false;

        currentScore = incomingScore;
        ClearPendingScore(ref pendingScore, ref pendingReads);
        return true;
    }

    internal static void ClearPendingScore(ref int pendingScore, ref int pendingReads)
    {
        pendingScore = NoPendingScore;
        pendingReads = 0;
    }

    private static bool IsValidFrontlineScore(int value)
        => value is >= 0 and <= MaxReadableScore;
}
