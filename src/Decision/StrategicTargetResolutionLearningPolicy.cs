using System;
using System.Collections.Generic;
using System.Linq;

namespace ai02;

public enum StrategicTargetResolutionKind
{
    None = 0,
    FirstPlaceAlliance,
    SecondPlaceAlliance,
    HighValueObjective,
    NearObjective,
    FarObjective
}

internal static class StrategicTargetResolutionLearningPolicy
{
    private static readonly string[] FirstPlaceKeywords =
    {
        "\u7b2c\u4e00\u540d",
        "\u7b2c1",
        "\u7b2c\u4e00",
        "\u9ad8\u5206",
        "\u9886\u5934",
        "\u6253\u7b2c\u4e00",
        "\u538b\u7b2c\u4e00",
        "\u6253\u9886\u5934"
    };

    private static readonly string[] SecondPlaceKeywords =
    {
        "\u7b2c\u4e8c\u540d",
        "\u7b2c2",
        "\u7b2c\u4e8c",
        "\u6253\u7b2c\u4e8c",
        "\u538b\u7b2c\u4e8c"
    };

    private static readonly string[] HighValueKeywords =
    {
        "\u9ad8\u4ef7\u503c\u70b9",
        "\u9ad8\u5206\u70b9"
    };

    private static readonly string[] NearKeywords =
    {
        "\u8fd1\u70b9",
        "\u8fd1\u4fa7\u70b9"
    };

    private static readonly string[] FarKeywords =
    {
        "\u8fdc\u70b9"
    };

    public static StrategicTargetResolutionKind Detect(string directiveText, string priorityTargetText)
    {
        var normalizedDirective = Normalize(directiveText);
        var normalizedPriorityTarget = Normalize(priorityTargetText);
        if (ContainsAny(normalizedDirective, FirstPlaceKeywords) || ContainsAny(normalizedPriorityTarget, FirstPlaceKeywords))
            return StrategicTargetResolutionKind.FirstPlaceAlliance;
        if (ContainsAny(normalizedDirective, SecondPlaceKeywords) || ContainsAny(normalizedPriorityTarget, SecondPlaceKeywords))
            return StrategicTargetResolutionKind.SecondPlaceAlliance;
        if (ContainsAny(normalizedDirective, HighValueKeywords) || ContainsAny(normalizedPriorityTarget, HighValueKeywords))
            return StrategicTargetResolutionKind.HighValueObjective;
        if (ContainsAny(normalizedDirective, NearKeywords) || ContainsAny(normalizedPriorityTarget, NearKeywords))
            return StrategicTargetResolutionKind.NearObjective;
        if (ContainsAny(normalizedDirective, FarKeywords) || ContainsAny(normalizedPriorityTarget, FarKeywords))
            return StrategicTargetResolutionKind.FarObjective;
        return StrategicTargetResolutionKind.None;
    }

    public static bool ContainsAny(string source, IEnumerable<string> values)
        => !string.IsNullOrWhiteSpace(source) && values.Any(value => source.Contains(value, StringComparison.Ordinal));

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var chars = new List<char>(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                continue;

            chars.Add(char.ToLowerInvariant(ch));
        }

        return new string(chars.ToArray());
    }
}
