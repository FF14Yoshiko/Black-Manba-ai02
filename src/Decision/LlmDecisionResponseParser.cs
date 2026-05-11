using System;
using System.Text.Json;

namespace ai02;

public readonly record struct LlmDecisionResponseParseResult(
    string Decision,
    string ShortReason,
    string RecommendedAction,
    string PriorityTarget,
    float Confidence,
    float Risk,
    string DebugText,
    string DebugScoreRead,
    string DebugPositionRead,
    string DebugLatencyNote);

public static class LlmDecisionResponseParser
{
    public static LlmDecisionResponseParseResult Parse(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        return Parse(document.RootElement);
    }

    public static LlmDecisionResponseParseResult Parse(JsonElement root)
    {
        var decisionText = GetString(root, "decision", "决策", "decision_body");
        var shortReason = GetString(root, "short_reason", "shortReason", "reason", "简短理由");
        var recommendedAction = GetString(root, "recommended_action", "recommendedAction", "action", "建议行动");
        var priorityTarget = GetString(root, "priority_target", "priorityTarget", "target", "优先目标");
        var confidence = GetFloat(root, 72f, "confidence", "置信度");
        var risk = GetFloat(root, 50f, "risk", "风险");
        var debugText = GetDebugText(root);
        var debugScoreRead = GetDebugField(root, "score_read", "scoreRead", "比分读取", "score");
        var debugPositionRead = GetDebugField(root, "position_read", "positionRead", "位置读取", "position");
        var debugLatencyNote = GetDebugField(root, "latency_note", "latencyNote", "延迟说明", "latency");

        if (string.IsNullOrWhiteSpace(decisionText))
            decisionText = string.IsNullOrWhiteSpace(recommendedAction) ? "保持本地决策，等待下一帧" : recommendedAction;
        if (string.IsNullOrWhiteSpace(recommendedAction))
            recommendedAction = decisionText;

        return new LlmDecisionResponseParseResult(
            decisionText,
            shortReason,
            recommendedAction,
            priorityTarget,
            Math.Clamp(confidence, 0f, 100f),
            Math.Clamp(risk, 0f, 100f),
            debugText,
            debugScoreRead,
            debugPositionRead,
            debugLatencyNote);
    }

    private static string GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                return value.ToString();
        }

        return string.Empty;
    }

    private static float GetFloat(JsonElement root, float fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(root, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && float.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return fallback;
    }

    private static string GetDebugText(JsonElement root)
    {
        if (!TryGetProperty(root, "debug", out var value) && !TryGetProperty(root, "调试", out value))
            return string.Empty;
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? string.Empty;
        return value.GetRawText();
    }

    private static string GetDebugField(JsonElement root, params string[] names)
    {
        if (!TryGetProperty(root, "debug", out var value) && !TryGetProperty(root, "调试", out value))
            return string.Empty;
        if (value.ValueKind != JsonValueKind.Object)
            return string.Empty;
        return GetString(value, names);
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
