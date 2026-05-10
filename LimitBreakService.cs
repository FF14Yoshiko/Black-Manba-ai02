using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState;
using Dalamud.Game.Gui;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace ai02;

public sealed unsafe class LimitBreakService : IDisposable
{
    private const float ChargeTickSeconds = 3f;
    private const float PercentIncreaseEpsilon = 0.1f;
    private const int DefaultSampleIntervalMs = 500;

    private static readonly HashSet<uint> KnownPvPTerritoryIds = new()
    {
        250, 376, 431, 554, 888, 1273, 1313
    };

    private static readonly string[] LimitBreakAddonNames =
    {
        "_LimitBreak",
        "LimitBreak",
        "LimitBreakGauge",
        "LimitBreakGaugeMain",
        "LimitBreakBar",
        "HudLimitBreakGauge",
        "_LimitBreakGauge",
        "JobHud",
        "_JobHud",
        "ParamGauge",
        "_ParamGauge"
    };

    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IGameGui gameGui;
    private readonly IPluginLog log;

    private BattlefieldLimitBreakSnapshot latestSnapshot = new();
    private long lastSampleTicks;
    private float lastPercent = -1f;
    private float percentPerTick = -1f;
    private float estimatedTimeRemaining;
    private bool disposed;

    public LimitBreakService(
        Configuration configuration,
        IClientState clientState,
        IGameGui gameGui,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.gameGui = gameGui;
        this.log = log;
    }

    public BattlefieldLimitBreakSnapshot GetSnapshot(bool isInFrontline = false)
    {
        if (disposed)
            return new BattlefieldLimitBreakSnapshot { SummaryText = "极限槽读取已停止" };

        var now = Environment.TickCount64;
        var intervalMs = configuration.Performance?.EffectiveLimitBreakSampleIntervalMs ?? DefaultSampleIntervalMs;
        if (now - lastSampleTicks < intervalMs)
            return latestSnapshot;

        lastSampleTicks = now;
        latestSnapshot = Sample(isInFrontline);
        return latestSnapshot;
    }

    public void DrawOverlay(bool isInFrontline = false)
    {
        if (disposed)
            return;

        var config = configuration.LimitBreak;
        if (!config.ShowLimitBreakUI)
            return;

        config.Normalize();
        var snapshot = GetSnapshot(isInFrontline);
        if (!snapshot.IsAvailable)
            return;

        var position = ResolveOverlayPosition(config);
        var text = BuildOverlayText(snapshot);
        var textColor = new Vector4(config.TextColorR, config.TextColorG, config.TextColorB, 1f);
        var strokeColor = new Vector4(config.StrokeColorR, config.StrokeColorG, config.StrokeColorB, 1f);
        DrawTextAtPosition(text, position, textColor, strokeColor, config.WindowScale, config.ShowStroke);
    }

    public string[] GetAddonDebugLines()
    {
        if (disposed)
            return Array.Empty<string>();

        var lines = new List<string>(LimitBreakAddonNames.Length + 1);
        foreach (var addonName in LimitBreakAddonNames)
        {
            try
            {
                var addonPtr = gameGui.GetAddonByName(addonName, 1);
                if (addonPtr == IntPtr.Zero)
                    continue;

                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon == null || addon->RootNode == null)
                    continue;

                lines.Add($"{addonName}: 位置({addon->X}, {addon->Y}) 尺寸 {addon->RootNode->Width}x{addon->RootNode->Height}");
            }
            catch (Exception ex)
            {
                log.Verbose(ex, "[LimitBreakService] Failed to inspect addon {Name}", addonName);
            }
        }

        if (lines.Count == 0)
            lines.Add("未找到极限槽相关 Addon。");

        return lines.ToArray();
    }

    public void Dispose()
    {
        disposed = true;
        latestSnapshot = new BattlefieldLimitBreakSnapshot { SummaryText = "极限槽读取已停止" };
    }

    private BattlefieldLimitBreakSnapshot Sample(bool isInFrontline)
    {
        var isInPvPRegion = IsInPvPRegion(isInFrontline);
        if (!TryReadLimitBreakUnits(out var barUnits, out var currentUnits))
        {
            return new BattlefieldLimitBreakSnapshot
            {
                IsInPvPRegion = isInPvPRegion,
                LastPercent = lastPercent,
                PercentPerTick = percentPerTick,
                EstimatedSecondsRemaining = Math.Max(0f, estimatedTimeRemaining),
                SourceText = "LimitBreakController",
                SummaryText = "无法获取极限槽数据"
            };
        }

        var percent = currentUnits * 100f / barUnits;
        var level = barUnits > 0 ? currentUnits / barUnits : 0;
        UpdatePrediction(percent);
        var remainingTicks = percentPerTick > 0f
            ? Math.Max(0f, (100f - percent) / percentPerTick)
            : 0f;

        return new BattlefieldLimitBreakSnapshot
        {
            IsAvailable = true,
            IsInPvPRegion = isInPvPRegion,
            BarUnits = barUnits,
            CurrentUnits = currentUnits,
            Percent = percent,
            Level = level,
            LastPercent = lastPercent,
            PercentPerTick = percentPerTick,
            EstimatedSecondsRemaining = Math.Max(0f, estimatedTimeRemaining),
            EstimatedTicksRemaining = remainingTicks,
            SourceText = "LimitBreakController.Instance",
            SummaryText = BuildSummaryText(percent, currentUnits, barUnits, isInPvPRegion, estimatedTimeRemaining, remainingTicks, percentPerTick)
        };
    }

    private static bool TryReadLimitBreakUnits(out ushort barUnits, out ushort currentUnits)
    {
        barUnits = 0;
        currentUnits = 0;

        var controller = LimitBreakController.Instance();
        if (controller == null)
            return false;

        barUnits = controller->BarUnits;
        currentUnits = controller->CurrentUnits;
        return barUnits > 0;
    }

    private void UpdatePrediction(float percent)
    {
        if (percent >= 100f)
        {
            estimatedTimeRemaining = 0f;
            lastPercent = percent;
            return;
        }

        if (lastPercent >= 0f && percent < lastPercent - PercentIncreaseEpsilon)
        {
            percentPerTick = -1f;
            estimatedTimeRemaining = 0f;
            lastPercent = percent;
            return;
        }

        if (lastPercent > 0f && percent > lastPercent + PercentIncreaseEpsilon)
        {
            var increase = percent - lastPercent;
            if (percentPerTick < 0f)
                percentPerTick = increase;

            if (percentPerTick > 0f)
                estimatedTimeRemaining = Math.Max(0f, (100f - percent) / percentPerTick * ChargeTickSeconds);
        }

        lastPercent = percent;
    }

    private bool IsInPvPRegion(bool isInFrontline)
        => isInFrontline || KnownPvPTerritoryIds.Contains(clientState.TerritoryType);

    private Vector2 ResolveOverlayPosition(LimitBreakConfiguration config)
    {
        var io = ImGui.GetIO();
        var position = new Vector2(io.DisplaySize.X / 2f - 50f, io.DisplaySize.Y / 2f);
        try
        {
            var addonPtr = gameGui.GetAddonByName("_LimitBreak", 1);
            if (addonPtr != IntPtr.Zero)
            {
                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon != null && addon->RootNode != null)
                    position = new Vector2(addon->X + config.OffsetX, addon->Y + config.OffsetY);
            }
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "[LimitBreakService] Failed to resolve _LimitBreak addon position.");
        }

        return position;
    }

    private static string BuildOverlayText(BattlefieldLimitBreakSnapshot snapshot)
    {
        if (!snapshot.IsInPvPRegion)
            return $"{snapshot.Percent:F1}%";

        var text = $"{snapshot.Percent:F1}%   {snapshot.EstimatedSecondsRemaining:F0}秒";
        if (snapshot.PercentPerTick > 0f)
            text = $"{text}   剩余{snapshot.EstimatedTicksRemaining:F0}跳";

        return text;
    }

    private static string BuildSummaryText(
        float percent,
        ushort currentUnits,
        ushort barUnits,
        bool isInPvPRegion,
        float estimatedSeconds,
        float estimatedTicks,
        float percentPerTick)
    {
        var baseText = $"极限槽 {percent:F1}% ({currentUnits}/{barUnits})";
        if (!isInPvPRegion)
            return baseText;

        if (percent >= 100f)
            return $"{baseText}，已满";

        if (percentPerTick <= 0f)
            return $"{baseText}，等待下一次充能跳动以估算剩余时间";

        return $"{baseText}，预估 {estimatedSeconds:F0} 秒 / {estimatedTicks:F0} 跳后充满";
    }

    private static void DrawTextAtPosition(
        string text,
        Vector2 position,
        Vector4 textColor,
        Vector4 strokeColor,
        float fontScale,
        bool showStroke)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        fontScale = Math.Clamp(fontScale, 0.5f, 3f);
        ImGui.SetNextWindowPos(position, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(15f * fontScale, 8f * fontScale));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        var flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.AlwaysAutoResize
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoBackground;

        if (ImGui.Begin("##ai02LimitBreakPercentage", flags))
        {
            ImGui.SetWindowFontScale(fontScale);
            var drawList = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var textColorU32 = Color(textColor);
            var strokeColorU32 = Color(strokeColor);
            var strokeOffset = 1f * fontScale;

            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 0f));
            ImGui.TextUnformatted(text);
            ImGui.PopStyleColor();

            if (showStroke)
            {
                drawList.AddText(pos + new Vector2(-strokeOffset, 0f), strokeColorU32, text);
                drawList.AddText(pos + new Vector2(strokeOffset, 0f), strokeColorU32, text);
                drawList.AddText(pos + new Vector2(0f, -strokeOffset), strokeColorU32, text);
                drawList.AddText(pos + new Vector2(0f, strokeOffset), strokeColorU32, text);
                drawList.AddText(pos + new Vector2(-strokeOffset, -strokeOffset), strokeColorU32, text);
                drawList.AddText(pos + new Vector2(strokeOffset, -strokeOffset), strokeColorU32, text);
                drawList.AddText(pos + new Vector2(-strokeOffset, strokeOffset), strokeColorU32, text);
                drawList.AddText(pos + new Vector2(strokeOffset, strokeOffset), strokeColorU32, text);
            }

            drawList.AddText(pos, textColorU32, text);
            ImGui.SetWindowFontScale(1f);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    private static uint Color(Vector4 color)
        => ImGui.ColorConvertFloat4ToU32(color);
}
