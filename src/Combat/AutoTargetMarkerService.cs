using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.Command;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class AutoTargetMarkerService : IDisposable
{
    private const ulong InvalidGameObjectId = 0xE0000000;
    private const long ResolveRetryCooldownMs = 700;

    private readonly Configuration configuration;
    private long nextResolveTicks;
    private long lastResolvedSnapshotTicks;
    private FocusTargetDisplay lastDisplay;
    private bool disposed;

    public AutoTargetMarkerService(
        Configuration configuration,
        ICommandManager commandManager,
        ITargetManager targetManager,
        IObjectTable objectTable,
        IPluginLog log)
    {
        this.configuration = configuration;
    }

    public void Update(BattlefieldSnapshot snapshot)
    {
        if (disposed)
            return;

        var config = configuration.Radar;
        if (!config.AutoMarkFocusTarget || !snapshot.IsInFrontline || snapshot.IsAreaTransitioning)
        {
            Reset();
            return;
        }

        var now = Environment.TickCount64;
        if (!TryResolveFocusDisplayThrottled(snapshot, now, out var display))
            return;

        DrawFocusTargetOverlay(display);
    }

    public void Dispose()
    {
        disposed = true;
        Reset();
    }

    private void Reset()
    {
        nextResolveTicks = 0;
        lastResolvedSnapshotTicks = 0;
        lastDisplay = default;
    }

    private bool TryResolveFocusDisplayThrottled(
        BattlefieldSnapshot snapshot,
        long now,
        out FocusTargetDisplay display)
    {
        if (snapshot.UpdatedAtTicks > 0 && snapshot.UpdatedAtTicks == lastResolvedSnapshotTicks)
        {
            display = lastDisplay;
            return display.IsValid;
        }

        if (snapshot.UpdatedAtTicks <= 0 && now < nextResolveTicks)
        {
            display = lastDisplay;
            return display.IsValid;
        }

        lastResolvedSnapshotTicks = snapshot.UpdatedAtTicks;
        if (TryResolveFocusDisplay(snapshot, out display))
        {
            lastDisplay = display;
            return true;
        }

        lastDisplay = default;
        nextResolveTicks = now + ResolveRetryCooldownMs;
        return false;
    }

    private static bool TryResolveFocusDisplay(BattlefieldSnapshot snapshot, out FocusTargetDisplay display)
    {
        display = default;

        var playersById = BuildPlayersById(snapshot.Players);
        foreach (var action in EnumeratePriorityActions(snapshot.Decision))
        {
            if (!IsMarkerRelevant(action))
                continue;

            if (TryResolveActionTargetDisplay(action, playersById, snapshot.TeamSituation.FriendlyFocusTargets, out display))
                return true;
        }

        foreach (var command in EnumeratePriorityCommands(snapshot.Decision.CommandSituation))
        {
            if (!IsMarkerRelevant(command))
                continue;

            if (TryResolveCommandTargetDisplay(command, playersById, snapshot.TeamSituation.FriendlyFocusTargets, out display))
                return true;
        }

        if (snapshot.Decision.FightPriorityTarget.HasValue
            && TryResolvePriorityTargetDisplay(snapshot.Decision.FightPriorityTarget.Value, playersById, snapshot.TeamSituation.FriendlyFocusTargets, out display))
        {
            return true;
        }

        var focusTarget = snapshot.TeamSituation.FriendlyFocusTargets
            .OrderByDescending(target => target.ThreatScore)
            .ThenByDescending(target => target.AttackerCount + target.CasterCount)
            .FirstOrDefault();
        if (focusTarget.TargetGameObjectId != 0)
        {
            display = FromFocusTarget(focusTarget, "我方正在集火");
            return display.IsValid;
        }

        return false;
    }

    private static Dictionary<ulong, BattlefieldPlayerSnapshot> BuildPlayersById(
        IReadOnlyList<BattlefieldPlayerSnapshot> players)
    {
        var result = new Dictionary<ulong, BattlefieldPlayerSnapshot>(players.Count);
        foreach (var player in players)
        {
            if (!IsValidObjectId(player.GameObjectId) || result.ContainsKey(player.GameObjectId))
                continue;

            result[player.GameObjectId] = player;
        }

        return result;
    }

    private static bool TryResolveActionTargetDisplay(
        BattlefieldActionCandidateSnapshot action,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playersById,
        IReadOnlyList<BattlefieldFocusTargetSnapshot> focusTargets,
        out FocusTargetDisplay display)
    {
        if (TryResolveTargetIdText(action.TargetId, playersById, out var targetId)
            || TryResolveTargetIdText(action.CommandId, playersById, out targetId)
            || TryResolveTargetName(action.TargetName, playersById, out targetId))
        {
            return TryBuildPlayerDisplay(targetId, playersById, focusTargets, CompactReason(action.Text, action.ReasonText, "决策集火"), out display);
        }

        display = default;
        return false;
    }

    private static bool TryResolveCommandTargetDisplay(
        BattlefieldCommandSnapshot command,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playersById,
        IReadOnlyList<BattlefieldFocusTargetSnapshot> focusTargets,
        out FocusTargetDisplay display)
    {
        if (TryResolveTargetIdText(command.Id, playersById, out var targetId)
            || TryResolveTargetName(command.TargetName, playersById, out targetId))
        {
            return TryBuildPlayerDisplay(targetId, playersById, focusTargets, CompactReason(command.CommandText, command.ReasonText, "指挥集火"), out display);
        }

        display = default;
        return false;
    }

    private static bool TryResolvePriorityTargetDisplay(
        BattlefieldPriorityTargetSnapshot priorityTarget,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playersById,
        IReadOnlyList<BattlefieldFocusTargetSnapshot> focusTargets,
        out FocusTargetDisplay display)
    {
        if (TryResolveTargetName(priorityTarget.TargetName, playersById, out var targetId))
            return TryBuildPlayerDisplay(targetId, playersById, focusTargets, CompactReason(priorityTarget.ActionText, priorityTarget.ReasonText, "优先集火"), out display);

        if (!IsMeaningfulPosition(priorityTarget.Position))
        {
            display = default;
            return false;
        }

        var nearest = playersById.Values
            .Where(IsEnemyKillTarget)
            .Select(player => new
            {
                Player = player,
                Distance = Vector2.Distance(
                    new Vector2(player.Position.X, player.Position.Z),
                    new Vector2(priorityTarget.Position.X, priorityTarget.Position.Z))
            })
            .Where(item => item.Distance <= 6f)
            .OrderBy(item => item.Distance)
            .FirstOrDefault();

        if (nearest != null)
            return TryBuildPlayerDisplay(nearest.Player.GameObjectId, playersById, focusTargets, CompactReason(priorityTarget.ActionText, priorityTarget.ReasonText, "优先集火"), out display);

        display = default;
        return false;
    }

    private static bool TryBuildPlayerDisplay(
        ulong targetId,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playersById,
        IReadOnlyList<BattlefieldFocusTargetSnapshot> focusTargets,
        string reason,
        out FocusTargetDisplay display)
    {
        if (!playersById.TryGetValue(targetId, out var player) || !IsEnemyKillTarget(player))
        {
            display = default;
            return false;
        }

        var focus = focusTargets.FirstOrDefault(item => item.TargetGameObjectId == targetId);
        display = new FocusTargetDisplay(
            player.GameObjectId,
            player.Name,
            focus.TargetGameObjectId != 0 ? focus.TargetJobName : string.Empty,
            player.HpPercent,
            player.DistanceToLocal,
            focus.AttackerCount,
            focus.CasterCount,
            focus.ThreatScore,
            reason);
        return true;
    }

    private static FocusTargetDisplay FromFocusTarget(BattlefieldFocusTargetSnapshot focusTarget, string reason)
        => new(
            focusTarget.TargetGameObjectId,
            focusTarget.TargetName,
            focusTarget.TargetJobName,
            focusTarget.HpPercent,
            0f,
            focusTarget.AttackerCount,
            focusTarget.CasterCount,
            focusTarget.ThreatScore,
            reason);

    private static IEnumerable<BattlefieldActionCandidateSnapshot> EnumeratePriorityActions(BattlefieldDecisionSnapshot decision)
    {
        if (decision.PublishedAction.HasValue)
            yield return decision.PublishedAction.Value;
        if (decision.PrimaryAction.HasValue)
            yield return decision.PrimaryAction.Value;
        if (decision.CommandSituation.PublishedAction.HasValue)
            yield return decision.CommandSituation.PublishedAction.Value;
        if (decision.CommandSituation.PrimaryAction.HasValue)
            yield return decision.CommandSituation.PrimaryAction.Value;

        foreach (var action in decision.ActionCandidates
                     .Concat(decision.CommandSituation.ActionCandidates)
                     .OrderByDescending(action => action.ActionType == BattlefieldActionType.FocusTarget ? 1 : 0)
                     .ThenByDescending(action => action.Priority)
                     .ThenByDescending(action => action.Urgency)
                     .ThenByDescending(action => action.Confidence))
        {
            yield return action;
        }
    }

    private static IEnumerable<BattlefieldCommandSnapshot> EnumeratePriorityCommands(BattlefieldCommandSituationSnapshot commands)
    {
        if (commands.PrimaryCommand.HasValue)
            yield return commands.PrimaryCommand.Value;

        foreach (var command in commands.Commands
                     .OrderByDescending(command => command.Kind == BattlefieldCommandKind.FocusTarget ? 1 : 0)
                     .ThenByDescending(command => command.Score)
                     .ThenByDescending(command => command.Urgency))
        {
            yield return command;
        }
    }

    private static bool IsMarkerRelevant(BattlefieldActionCandidateSnapshot action)
        => action.ActionType is BattlefieldActionType.FocusTarget or BattlefieldActionType.Engage or BattlefieldActionType.BacklinePressure
            || action.CommandKind is BattlefieldCommandKind.FocusTarget or BattlefieldCommandKind.Engage or BattlefieldCommandKind.PressureSide
            || action.CommandId.StartsWith("fight:decision", StringComparison.Ordinal)
            || action.CommandId.StartsWith("micro:focus-countdown", StringComparison.Ordinal)
            || action.CommandId.StartsWith("micro:fight-method", StringComparison.Ordinal);

    private static bool IsMarkerRelevant(BattlefieldCommandSnapshot command)
        => command.Kind is BattlefieldCommandKind.FocusTarget or BattlefieldCommandKind.Engage or BattlefieldCommandKind.PressureSide
            || command.Id.StartsWith("fight:decision", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:focus-countdown", StringComparison.Ordinal)
            || command.Id.StartsWith("micro:fight-method", StringComparison.Ordinal);

    private static bool TryResolveTargetIdText(
        string text,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playersById,
        out ulong targetId)
    {
        targetId = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var token in text.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!ulong.TryParse(token, out var parsed))
                continue;

            if (playersById.TryGetValue(parsed, out var player) && IsEnemyKillTarget(player))
            {
                targetId = parsed;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveTargetName(
        string name,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playersById,
        out ulong targetId)
    {
        targetId = 0;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var match = playersById.Values
            .Where(IsEnemyKillTarget)
            .Where(player => string.Equals(player.Name, name, StringComparison.Ordinal))
            .OrderBy(player => player.HpPercent <= 0f ? 100f : player.HpPercent)
            .FirstOrDefault();

        targetId = match.GameObjectId;
        return targetId != 0;
    }

    private static void DrawFocusTargetOverlay(FocusTargetDisplay display)
    {
        var viewport = ImGui.GetMainViewport();
        var width = MathF.Min(380f, MathF.Max(280f, viewport.Size.X - 32f));
        var pos = viewport.Pos + new Vector2((viewport.Size.X - width) * 0.5f, 126f);
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.82f);

        var flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.AlwaysAutoResize;

        if (!ImGui.Begin("##ai02FocusTargetOverlay", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.26f, 0.22f, 1f), "集火目标");
        ImGui.SameLine();
        ImGui.TextUnformatted(BuildNameLine(display));
        ImGui.PushStyleColor(ImGuiCol.PlotHistogram, HpColor(display.HpPercent));
        ImGui.ProgressBar(Math.Clamp(display.HpPercent / 100f, 0f, 1f), new Vector2(width - 24f, 8f), string.Empty);
        ImGui.PopStyleColor();
        ImGui.TextColored(new Vector4(1f, 0.92f, 0.70f, 1f), BuildDetailLine(display));
        ImGui.End();
    }

    private static string BuildNameLine(FocusTargetDisplay display)
    {
        var job = string.IsNullOrWhiteSpace(display.JobName) ? string.Empty : $" [{display.JobName}]";
        return $"{display.Name}{job}";
    }

    private static string BuildDetailLine(FocusTargetDisplay display)
    {
        var parts = new List<string>
        {
            $"HP {display.HpPercent:0}%",
        };

        if (display.DistanceToLocal > 0f)
            parts.Add($"{display.DistanceToLocal:0}y");
        if (display.AttackerCount > 0 || display.CasterCount > 0)
            parts.Add($"锁定 {display.AttackerCount}+{display.CasterCount}");
        if (display.ThreatScore > 0f)
            parts.Add($"威胁 {display.ThreatScore:0}");
        if (!string.IsNullOrWhiteSpace(display.Reason))
            parts.Add(display.Reason);

        return string.Join("  ", parts);
    }

    private static Vector4 HpColor(float hpPercent)
    {
        if (hpPercent <= 30f)
            return new Vector4(1f, 0.18f, 0.14f, 1f);
        if (hpPercent <= 55f)
            return new Vector4(1f, 0.62f, 0.18f, 1f);
        return new Vector4(0.95f, 0.28f, 0.22f, 1f);
    }

    private static string CompactReason(string primary, string secondary, string fallback)
    {
        var text = !string.IsNullOrWhiteSpace(primary)
            ? primary.Trim()
            : !string.IsNullOrWhiteSpace(secondary)
                ? secondary.Trim()
                : fallback;
        return text.Length <= 18 ? text : text[..18];
    }

    private static bool IsEnemyKillTarget(BattlefieldPlayerSnapshot player)
        => IsValidObjectId(player.GameObjectId)
            && player.Relation == BattlefieldPlayerRelation.Enemy
            && !player.IsDead
            && !player.IsInvulnerable;

    private static bool IsValidObjectId(ulong objectId)
        => objectId != 0 && objectId != InvalidGameObjectId && objectId != ulong.MaxValue;

    private static bool IsMeaningfulPosition(Vector3 position)
        => MathF.Abs(position.X) > 0.01f || MathF.Abs(position.Y) > 0.01f || MathF.Abs(position.Z) > 0.01f;

    private readonly record struct FocusTargetDisplay(
        ulong GameObjectId,
        string Name,
        string JobName,
        float HpPercent,
        float DistanceToLocal,
        int AttackerCount,
        int CasterCount,
        float ThreatScore,
        string Reason)
    {
        public bool IsValid => GameObjectId != 0 && !string.IsNullOrWhiteSpace(Name);
    }
}
