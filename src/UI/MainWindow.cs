using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace ai02;

public partial class MainWindow : Window, IDisposable
{
    public MainWindow(Plugin plugin) : base($"{Plugin.DisplayName}##MainWindow")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(700, 520),
            MaximumSize = new Vector2(1600, 1200),
        };
    }
    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();
        if (!ImGui.BeginTable("##MainLayout", 2, ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("\u5bfc\u822a", ImGuiTableColumnFlags.WidthFixed, 174f);
        ImGui.TableSetupColumn("\u5185\u5bb9", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawNavigation();
        ImGui.TableNextColumn();
        DrawCurrentPage();
        ImGui.EndTable();
        DrawMapAnnotationOverlay();
    }
    public void Dispose()
    {
    }
    private void DrawHeader()
    {
        var snapshot = plugin.WorldStateService.GetSnapshot();
        ImGui.TextColored(new Vector4(1f, 0.80f, 0.22f, 1f), Plugin.DisplayName);
        ImGui.SameLine();
        DrawStatusPill(snapshot.IsInFrontline ? "\u524d\u7ebf\u8fdb\u884c\u4e2d" : "\u5f85\u673a", snapshot.IsInFrontline ? new Vector4(0.18f, 0.75f, 0.32f, 1f) : new Vector4(0.45f, 0.45f, 0.48f, 1f));
        ImGui.TextColored(new Vector4(0.68f, 0.68f, 0.72f, 1f), Plugin.Tagline);
        ImGui.Spacing();
        ImGui.Separator();
    }
    private static void DrawStatusPill(string text, Vector4 color)
    {
        var drawList = ImGui.GetWindowDrawList();
        var min = ImGui.GetCursorScreenPos() + new Vector2(8f, 0f);
        var size = ImGui.CalcTextSize(text) + new Vector2(18f, 6f);
        drawList.AddRectFilled(min, min + size, Color(new Vector4(color.X, color.Y, color.Z, 0.22f)), 6f);
        drawList.AddRect(min, min + size, Color(new Vector4(color.X, color.Y, color.Z, 0.75f)), 6f);
        ImGui.SetCursorScreenPos(min + new Vector2(9f, 3f));
        ImGui.TextColored(color, text);
        ImGui.SetCursorScreenPos(new Vector2(min.X + size.X + 8f, min.Y));
    }
    private void DrawNavigation()
    {
        ImGui.TextColored(new Vector4(0.74f, 0.74f, 0.78f, 1f), "\u6a21\u5757");
        ImGui.Spacing();
        DrawNavButton(MainPage.CombatHud, "\u6218\u6597\u754c\u9762", "\u6218\u4e2d\u6781\u7b80\u6307\u6325\u4e0e\u76ee\u6807");
        DrawNavButton(MainPage.Review, "\u590d\u76d8/\u8c03\u8bd5", "\u8d5b\u540e\u8be6\u60c5\u4e0e\u8bca\u65ad");
        DrawNavButton(MainPage.Radar, "\u96f7\u8fbe\u8bbe\u7f6e", "\u5730\u56fe\u3001\u5c4f\u5e55\u4e0e\u6807\u8bb0\u663e\u793a");
        DrawNavButton(MainPage.MapEditor, "\u5730\u56fe\u6807\u6ce8", "\u70b9\u4f4d\u3001\u8def\u5f84\u4e0e\u5371\u9669\u533a");
        DrawNavButton(MainPage.Tools, "\u5de5\u5177", "\u914d\u7f6e\u3001\u60ac\u6d6e\u7403\u4e0e\u8c03\u8bd5");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.58f, 0.62f, 0.70f, 1f), "\u5feb\u6377\u72b6\u6001");
        var radar = plugin.Configuration.Radar;
        if (ImGui.BeginTable("##NavStates", 2, ImGuiTableFlags.SizingStretchSame))
        {
            DrawNavigationState("\u96f7\u8fbe", radar.Enabled);
            DrawNavigationState("\u5730\u56fe", radar.MapRadar);
            DrawNavigationState("\u5c4f\u5e55", radar.ScreenRadar);
            DrawNavigationState("\u6781\u9650\u69fd", plugin.Configuration.LimitBreak.ShowLimitBreakUI);
            DrawNavigationState("\u6307\u6325\u5927\u5b57", plugin.Configuration.CommandOverlay.Enabled);
            DrawNavigationState("\u60ac\u6d6e\u7403", plugin.Configuration.FloatingButton.Enabled);
            ImGui.EndTable();
        }
    }
    private void DrawNavButton(MainPage page, string title, string subtitle)
    {
        var selected = currentPage == page;
        var width = MathF.Max(150f, ImGui.GetContentRegionAvail().X);
        var height = 48f;
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.22f, 0.38f, 0.58f, 0.95f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.26f, 0.44f, 0.66f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.18f, 0.32f, 0.50f, 1f));
        }
        if (ImGui.Button($"{title}\n{subtitle}##{page}", new Vector2(width, height)))
            currentPage = page;
        if (selected)
            ImGui.PopStyleColor(3);
        ImGui.Spacing();
    }
    private static void DrawMiniState(string label, bool enabled)
    {
        var color = enabled ? new Vector4(0.25f, 0.85f, 0.42f, 1f) : new Vector4(0.55f, 0.55f, 0.58f, 1f);
        ImGui.TextColored(color, enabled ? "\u25cf" : "\u25cb");
        ImGui.SameLine();
        ImGui.Text(label);
    }

    private static void DrawNavigationState(string label, bool enabled)
    {
        ImGui.TableNextColumn();
        DrawMiniState(label, enabled);
    }
    private void DrawCurrentPage()
    {
        switch (currentPage)
        {
            case MainPage.CombatHud:
                DrawCombatHudPage();
                break;
            case MainPage.Review:
                DrawReviewPage();
                break;
            case MainPage.Radar:
                DrawRadarPage();
                break;
            case MainPage.MapEditor:
                DrawMapEditorPage();
                break;
            case MainPage.Tools:
                DrawToolsPage();
                break;
        }
    }
}
