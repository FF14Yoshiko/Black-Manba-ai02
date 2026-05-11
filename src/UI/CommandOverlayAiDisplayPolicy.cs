using System;

namespace ai02;

internal readonly record struct CommandOverlayDirectiveDisplaySnapshot(
    string PrimaryCommandLine,
    string CurrentActionLine,
    bool IsAiLead);

internal static class CommandOverlayAiDisplayPolicy
{
    internal static bool IsAiLead(BattlefieldDecisionSnapshot decision)
    {
        if (HasAiCommandId(decision.CommandSituation.PrimaryCommand?.Id)
            || HasAiCommandId(decision.CommandSituation.Publish.Command?.Id)
            || HasAiActionId(decision.PrimaryAction?.Id)
            || HasAiActionId(decision.PublishedAction?.Id)
            || HasAiActionId(decision.CommandSituation.PrimaryAction?.Id)
            || HasAiActionId(decision.CommandSituation.PublishedAction?.Id))
        {
            return true;
        }

        return string.Equals(decision.CommandSituation.Publish.PriorityText, "AI 主导", StringComparison.Ordinal)
            || decision.CommandSituation.SummaryText.StartsWith("AI 主导", StringComparison.Ordinal)
            || decision.SummaryText.StartsWith("AI 接管", StringComparison.Ordinal);
    }

    internal static CommandOverlayDirectiveDisplaySnapshot ResolveDisplay(
        CommandOverlayDirectiveDisplaySnapshot current,
        long now,
        int aiHoldSeconds,
        ref CommandOverlayDirectiveDisplaySnapshot? heldAi,
        ref long heldAiTicks)
    {
        if (current.IsAiLead)
        {
            heldAi = current;
            heldAiTicks = now;
            return current;
        }

        var holdMs = Math.Max(0, aiHoldSeconds) * 1000L;
        if (holdMs > 0
            && heldAi.HasValue
            && heldAiTicks >= 0
            && now - heldAiTicks <= holdMs)
        {
            return heldAi.Value;
        }

        heldAi = null;
        heldAiTicks = -1;
        return current;
    }

    private static bool HasAiCommandId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.StartsWith("ai:command:", StringComparison.Ordinal);

    private static bool HasAiActionId(string? id)
        => !string.IsNullOrWhiteSpace(id) && id.StartsWith("ai:action:", StringComparison.Ordinal);
}
