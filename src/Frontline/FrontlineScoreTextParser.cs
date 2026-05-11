using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ai02;

public enum FrontlineAllianceKey
{
    Unknown,
    Maelstrom,
    TwinAdder,
    ImmortalFlames
}

public readonly record struct FrontlineStructuredScoreRow(
    int Index,
    string Text,
    int Value,
    int? NeededCount,
    string Source);

public readonly record struct FrontlineStructuredScoreResult(
    int Maelstrom,
    int TwinAdder,
    int ImmortalFlames,
    int? ScoreLimit,
    string Source);

public static class FrontlineScoreTextParser
{
    private const int MaxReadableScore = 3000;
    private const int MinReadableScoreLimit = 100;

    private static readonly Regex FractionScoreRegex =
        new(@"(?<!\d)(\d{1,3}(?:[,，]\d{3})+|\d{1,5})\s*[/／]\s*(\d{1,3}(?:[,，]\d{3})+|\d{1,5})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberRegex =
        new(@"(?<![\d:])(\d{1,3}(?:,\d{3})+|\d{1,5})(?![\d:])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeRegex =
        new(@"(?<!\d)(\d{1,2}):([0-5]\d)(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryBuildStructuredScoreResult(
        IReadOnlyList<FrontlineStructuredScoreRow> rows,
        string source,
        out FrontlineStructuredScoreResult result)
    {
        var scores = new Dictionary<FrontlineAllianceKey, int>();
        var limits = new List<int>();
        var sources = new List<string>(3);

        foreach (var row in rows)
        {
            if (!TryIdentifyAlliance(row.Text, out var alliance) || alliance == FrontlineAllianceKey.Unknown)
                continue;

            if (!TryExtractStructuredRowScore(row, out var score, out var scoreLimit))
                continue;

            scores[alliance] = score;
            if (scoreLimit.HasValue)
                limits.Add(scoreLimit.Value);
            sources.Add($"{alliance}@{row.Index}:{row.Source}");
        }

        if (scores.TryGetValue(FrontlineAllianceKey.Maelstrom, out var maelstrom)
            && scores.TryGetValue(FrontlineAllianceKey.TwinAdder, out var twinAdder)
            && scores.TryGetValue(FrontlineAllianceKey.ImmortalFlames, out var immortalFlames)
            && IsLikelyScoreTriple(maelstrom, twinAdder, immortalFlames, allowAllZero: true))
        {
            var scoreLimit = ResolveScoreLimit(limits);
            result = new FrontlineStructuredScoreResult(
                maelstrom,
                twinAdder,
                immortalFlames,
                scoreLimit > 0 ? scoreLimit : null,
                $"{source}/structured[{string.Join(",", sources.Take(3))}]");
            return true;
        }

        result = default;
        return false;
    }

    public static bool TryExtractStructuredRowScore(FrontlineStructuredScoreRow row, out int score, out int? scoreLimit)
    {
        if (TryExtractFractionScore(row.Text, out var fractionScore, out var fractionLimit))
        {
            score = fractionScore;
            scoreLimit = fractionLimit;
            return true;
        }

        if (IsValidFrontlineScore(row.Value))
        {
            score = row.Value;
            scoreLimit = row.NeededCount;
            return true;
        }

        if (TryExtractStructuralScore(row.Text, out var structuralScore))
        {
            score = structuralScore;
            scoreLimit = row.NeededCount;
            return true;
        }

        score = 0;
        scoreLimit = null;
        return false;
    }

    public static bool TryExtractFractionScore(string text, out int score, out int scoreLimit)
    {
        score = 0;
        scoreLimit = 0;

        if (string.IsNullOrWhiteSpace(text) || TimeRegex.IsMatch(text))
            return false;

        var fractionMatch = FractionScoreRegex.Match(text);
        if (!fractionMatch.Success
            || !TryParseNumber(fractionMatch.Groups[1].Value, out var fractionScore)
            || !TryParseNumber(fractionMatch.Groups[2].Value, out var targetScore)
            || !IsValidScoreLimit(targetScore)
            || !IsValidFrontlineScore(fractionScore)
            || fractionScore > targetScore)
        {
            return false;
        }

        score = fractionScore;
        scoreLimit = targetScore;
        return true;
    }

    public static bool TryExtractStructuralScore(string text, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(text) || TimeRegex.IsMatch(text))
            return false;

        if (TryExtractFractionScore(text, out var fractionScore, out _))
        {
            score = fractionScore;
            return true;
        }

        var values = NumberRegex.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Select(value => TryParseNumber(value, out var number) ? number : -1)
            .Where(IsValidFrontlineScore)
            .ToArray();

        if (values.Length == 0)
            return false;

        score = values[^1];
        return true;
    }

    public static bool TryIdentifyAlliance(string text, out FrontlineAllianceKey alliance)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            alliance = FrontlineAllianceKey.Unknown;
            return false;
        }

        var normalized = NormalizeText(text);
        if (normalized.Contains("maelstrom", StringComparison.Ordinal)
            || normalized.Contains("黑涡", StringComparison.Ordinal)
            || normalized.Contains("黑涡团", StringComparison.Ordinal))
        {
            alliance = FrontlineAllianceKey.Maelstrom;
            return true;
        }

        if (normalized.Contains("twinadder", StringComparison.Ordinal)
            || normalized.Contains("adders", StringComparison.Ordinal)
            || normalized.Contains("双蛇", StringComparison.Ordinal))
        {
            alliance = FrontlineAllianceKey.TwinAdder;
            return true;
        }

        if (normalized.Contains("immortalflames", StringComparison.Ordinal)
            || normalized.Contains("flames", StringComparison.Ordinal)
            || normalized.Contains("恒辉", StringComparison.Ordinal)
            || normalized.Contains("不灭", StringComparison.Ordinal))
        {
            alliance = FrontlineAllianceKey.ImmortalFlames;
            return true;
        }

        alliance = FrontlineAllianceKey.Unknown;
        return false;
    }

    public static bool TryParseNumber(string text, out int number)
        => int.TryParse(
            text.Replace(",", string.Empty).Replace("，", string.Empty),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number);

    public static bool IsLikelyScoreTriple(int maelstrom, int twinAdder, int immortalFlames, bool allowAllZero)
    {
        if (!IsValidFrontlineScore(maelstrom) || !IsValidFrontlineScore(twinAdder) || !IsValidFrontlineScore(immortalFlames))
            return false;

        if (allowAllZero && maelstrom == 0 && twinAdder == 0 && immortalFlames == 0)
            return true;

        var max = Math.Max(maelstrom, Math.Max(twinAdder, immortalFlames));
        if (max < 5)
            return false;

        return !(maelstrom == 1 && twinAdder == 2 && immortalFlames == 3);
    }

    private static int ResolveScoreLimit(IEnumerable<int> scoreLimits)
    {
        var grouped = scoreLimits
            .Where(IsValidScoreLimit)
            .GroupBy(limit => limit)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .FirstOrDefault();

        return grouped?.Key ?? 0;
    }

    private static bool IsValidFrontlineScore(int value)
        => value is >= 0 and <= MaxReadableScore;

    private static bool IsValidScoreLimit(int value)
        => value is >= MinReadableScoreLimit and <= MaxReadableScore;

    private static string NormalizeText(string text)
        => Regex.Replace(text, @"\s+", string.Empty).ToLowerInvariant();
}
