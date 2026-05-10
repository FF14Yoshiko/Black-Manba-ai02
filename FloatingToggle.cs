using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace ai02;

public sealed class FloatingToggle
{
    private readonly Plugin plugin;
    private bool dragging;
    private bool movedWhileHeld;

    public FloatingToggle(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Draw()
    {
        var config = plugin.Configuration.FloatingButton;
        if (!config.Enabled)
            return;

        var viewport = ImGui.GetMainViewport();
        var size = new Vector2(52f, 52f);
        var pos = ClampToViewport(new Vector2(config.X, config.Y), size, viewport.Pos, viewport.Size);
        if (MathF.Abs(pos.X - config.X) > 0.5f || MathF.Abs(pos.Y - config.Y) > 0.5f)
        {
            config.X = pos.X;
            config.Y = pos.Y;
        }

        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        var flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("##ai02FloatingToggle", flags))
        {
            ImGui.End();
            return;
        }

        var drawList = ImGui.GetWindowDrawList();
        var windowPos = ImGui.GetWindowPos();
        var center = windowPos + size / 2f;
        var radius = 22f;
        var isOpen = plugin.MainWindow.IsOpen;

        ImGui.SetCursorScreenPos(windowPos);
        ImGui.InvisibleButton("##ai02FloatingToggleButton", size);

        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();
        if (ImGui.IsItemActivated())
        {
            dragging = true;
            movedWhileHeld = false;
        }

        if (active && dragging && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 3f))
        {
            var delta = ImGui.GetIO().MouseDelta;
            if (delta.LengthSquared() > 0.01f)
            {
                var next = ClampToViewport(new Vector2(config.X, config.Y) + delta, size, viewport.Pos, viewport.Size);
                config.X = next.X;
                config.Y = next.Y;
                movedWhileHeld = true;
            }
        }

        if (dragging && ImGui.IsItemDeactivated())
        {
            if (!movedWhileHeld && hovered)
            {
                plugin.MainWindow.IsOpen = !plugin.MainWindow.IsOpen;
                if (plugin.MainWindow.IsOpen)
                    ImGui.SetWindowFocus(plugin.MainWindow.WindowName);
            }

            plugin.Configuration.Save();
            dragging = false;
        }

        var baseColor = isOpen
            ? new Vector4(1f, 0.76f, 0.20f, 0.96f)
            : new Vector4(0.18f, 0.58f, 0.95f, 0.92f);
        if (hovered || active)
            baseColor = new Vector4(MathF.Min(baseColor.X + 0.12f, 1f), MathF.Min(baseColor.Y + 0.12f, 1f), MathF.Min(baseColor.Z + 0.12f, 1f), 1f);

        drawList.AddCircleFilled(center + new Vector2(0f, 3f), radius, Color(new Vector4(0f, 0f, 0f, 0.28f)), 48);
        drawList.AddCircleFilled(center, radius, Color(baseColor), 48);
        drawList.AddCircle(center, radius, Color(new Vector4(1f, 1f, 1f, 0.65f)), 48, 2f);
        DrawHelicopterGlyph(drawList, center, isOpen);

        if (hovered)
            ImGui.SetTooltip(isOpen ? "关闭插件窗口" : "打开插件窗口");

        ImGui.End();
    }

    private static void DrawHelicopterGlyph(ImDrawListPtr drawList, Vector2 center, bool isOpen)
    {
        var white = Color(new Vector4(1f, 1f, 1f, 0.96f));
        var dark = Color(new Vector4(0f, 0f, 0f, 0.35f));
        var bodyMin = center + new Vector2(-12f, -2f);
        var bodyMax = center + new Vector2(9f, 8f);
        drawList.AddRectFilled(bodyMin + new Vector2(1f), bodyMax + new Vector2(1f), dark, 4f);
        drawList.AddRectFilled(bodyMin, bodyMax, white, 4f);
        drawList.AddLine(center + new Vector2(-17f, -9f), center + new Vector2(17f, -9f), white, 3f);
        drawList.AddLine(center + new Vector2(0f, -15f), center + new Vector2(0f, -4f), white, 2f);
        drawList.AddLine(center + new Vector2(9f, 3f), center + new Vector2(17f, -2f), white, 2f);
        drawList.AddLine(center + new Vector2(17f, -6f), center + new Vector2(17f, 2f), white, 2f);

        if (isOpen)
            drawList.AddCircle(center + new Vector2(-3f, 3f), 3f, Color(new Vector4(0.15f, 0.42f, 0.9f, 1f)), 16, 2f);
    }

    private static Vector2 ClampToViewport(Vector2 pos, Vector2 size, Vector2 viewportPos, Vector2 viewportSize)
    {
        var min = viewportPos + new Vector2(8f);
        var max = viewportPos + viewportSize - size - new Vector2(8f);
        if (max.X < min.X)
            max.X = min.X;
        if (max.Y < min.Y)
            max.Y = min.Y;

        return new Vector2(Math.Clamp(pos.X, min.X, max.X), Math.Clamp(pos.Y, min.Y, max.Y));
    }

    private static uint Color(Vector4 color)
        => ImGui.ColorConvertFloat4ToU32(color);
}
