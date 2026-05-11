using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using MapSheet = Lumina.Excel.Sheets.Map;
using TerritoryTypeSheet = Lumina.Excel.Sheets.TerritoryType;
using System.Windows.Forms;

namespace ai02;

public class MainWindow : Window, IDisposable
{
    private static readonly OfflineFrontlineMapEntry[] OfflineFrontlineMaps =
    {
        new(FrontlineMapType.BorderlandRuinsSecure, 376, 167, "周边遗迹群（阵地战）"),
        new(FrontlineMapType.SealRock, 431, 242, "尘封秘岩（争夺战）"),
        new(FrontlineMapType.FieldsOfHonor, 554, 296, "荣誉野（碎冰战）"),
        new(FrontlineMapType.OnsalHakair, 888, 568, "昂萨哈凯尔（竞争战）"),
        new(FrontlineMapType.Vochester, 1313, 1119, "沃刻其特（演习战）"),
    };

    private readonly Plugin plugin;
    private MainPage currentPage = MainPage.CombatHud;
    private string configurationTransferStatus = string.Empty;
    private MapAnnotationKind selectedAnnotationKind = MapAnnotationKind.Choke;
    private string annotationLabel = string.Empty;
    private string annotationRouteId = string.Empty;
    private float annotationRadius = 18f;
    private int annotationRiskScore = 50;
    private bool annotationClickMode = true;
    private bool annotationClearArmed;
    private bool showBuiltInTacticalGraph = true;
    private bool showManualMapAnnotations = true;
    private bool showGraphRegions = true;
    private bool showGraphPaths = true;
    private bool showGraphNodes = true;
    private bool showDynamicMapHeat;
    private bool showObservedTacticalTracks;
    private bool mapCalibrationClickMode;
    private bool applyCorrectionToDraft = true;
    private bool applyCorrectionToGraph = true;
    private int mapAnnotationKindVisibleMask = BuildAllAnnotationKindMask();
    private int offlineMapIndex = -1;
    private float offlineMapCanvasZoom = 1f;
    private Vector2 offlineMapTextureCenter = new(1024f, 1024f);
    private string mapGraphSaveStatus = string.Empty;
    private string mapGraphVersionStatus = string.Empty;
    private string mapCalibrationStatus = string.Empty;
    private float mapCorrectionOffsetX;
    private float mapCorrectionOffsetY;
    private float mapCorrectionOffsetZ;
    private float mapCorrectionScale = 1f;
    private float mapCorrectionRotationDegrees;
    private readonly List<MapCalibrationSample> mapCalibrationSamples = new();
    private string llmManualPrompt = string.Empty;
    private string llmManualStatus = string.Empty;

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

        ImGui.TableSetupColumn("导航", ImGuiTableColumnFlags.WidthFixed, 174f);
        ImGui.TableSetupColumn("内容", ImGuiTableColumnFlags.WidthStretch);
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
        DrawStatusPill(snapshot.IsInFrontline ? "前线进行中" : "待机", snapshot.IsInFrontline ? new Vector4(0.18f, 0.75f, 0.32f, 1f) : new Vector4(0.45f, 0.45f, 0.48f, 1f));

        ImGui.TextColored(new Vector4(0.68f, 0.68f, 0.72f, 1f), Plugin.Tagline);
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
        ImGui.TextColored(new Vector4(0.74f, 0.74f, 0.78f, 1f), "模块");
        ImGui.Spacing();

        DrawNavButton(MainPage.CombatHud, "战斗界面", "战中极简指挥与目标");
        DrawNavButton(MainPage.Review, "复盘/调试", "赛后详情与诊断");
        DrawNavButton(MainPage.Radar, "雷达设置", "地图、屏幕与标记显示");
        DrawNavButton(MainPage.MapEditor, "地图标注", "点位、路径与危险区");
        DrawNavButton(MainPage.Tools, "\u5DE5\u5177", "\u914D\u7F6E\u3001\u60AC\u6D6E\u7403\u4E0E\u8C03\u8BD5");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var radar = plugin.Configuration.Radar;
        DrawMiniState("雷达", radar.Enabled);
        DrawMiniState("地图", radar.MapRadar);
        DrawMiniState("屏幕", radar.ScreenRadar);
        DrawMiniState("极限槽", plugin.Configuration.LimitBreak.ShowLimitBreakUI);
        DrawMiniState("指挥大字", plugin.Configuration.CommandOverlay.Enabled);
        DrawMiniState("悬浮球", plugin.Configuration.FloatingButton.Enabled);
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
        ImGui.TextColored(color, enabled ? "●" : "○");
        ImGui.SameLine();
        ImGui.Text(label);
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

    private void DrawCombatHudPage()
    {
        var battlefield = plugin.WorldStateService.GetSnapshot();
        var decision = battlefield.Decision;
        var commands = decision.CommandSituation;

        DrawSectionTitle("战斗中极简指挥界面", battlefield.IsInFrontline ? "只保留战中最需要看的指令、目标和风险" : "进入纷争前线后自动切换");
        DrawHint("战斗中优先看这里；赛后拆解、数据源和调试细节已经移到“复盘/调试”页。");

        if (!ImGui.BeginTable("##CombatHudTable", 2, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableSetupColumn("标签", ImGuiTableColumnFlags.WidthFixed, 108f);
        ImGui.TableSetupColumn("内容", ImGuiTableColumnFlags.WidthStretch);

        DrawCombatHudRow("状态", battlefield.IsInFrontline ? "前线进行中" : "待机", battlefield.IsInFrontline ? new Vector4(0.48f, 0.90f, 0.62f, 1f) : new Vector4(0.72f, 0.72f, 0.76f, 1f));
        DrawCombatHudRow("剩余时间", battlefield.TimeSituation.HasMatchTime ? $"{battlefield.MatchTimeRemaining / 60:D2}:{battlefield.MatchTimeRemaining % 60:D2}" : "--:--", new Vector4(0.95f, 0.82f, 0.35f, 1f));
        DrawCombatHudRow("比分", BuildCombatHudScoreText(battlefield));
        DrawCombatHudRow("下一资源", BuildCombatHudNextResourceText(battlefield.TimeSituation));
        DrawCombatHudRow("主指令", ResolveCombatHudPrimaryCommandText(decision), PriorityColor(commands.PrimaryCommand?.Score ?? 58f));
        DrawCombatHudRow("当前行动", ResolveCombatHudCurrentActionText(decision), RiskColor(decision.RiskAssessment.OverallRisk));
        DrawCombatHudRow("AI大决策", BuildCombatHudLlmDecisionText(battlefield.LlmStrategicDecision), LlmDecisionColor(battlefield.LlmStrategicDecision));
        DrawCombatHudRow("目标优先级", BuildCombatHudTargetText(decision));
        DrawCombatHudRow("风险", BuildCombatHudRiskText(decision));
        DrawCombatHudRow("发布状态", string.IsNullOrWhiteSpace(commands.Publish.StatusText) ? "暂无新发布指令" : commands.Publish.StatusText, commands.Publish.ShouldAnnounce ? new Vector4(0.48f, 0.90f, 0.62f, 1f) : new Vector4(0.72f, 0.72f, 0.76f, 1f));
        ImGui.EndTable();

        if (commands.PrimaryCommand.HasValue && !string.IsNullOrWhiteSpace(commands.PrimaryCommand.Value.ReasonText))
            DrawHint($"主指令依据：{commands.PrimaryCommand.Value.ReasonText}");
        else if (decision.PrimaryAction.HasValue && !string.IsNullOrWhiteSpace(decision.PrimaryAction.Value.ReasonText))
            DrawHint($"行动依据：{decision.PrimaryAction.Value.ReasonText}");

        if (commands.Publish.IsSuppressed && !string.IsNullOrWhiteSpace(commands.Publish.SuppressionReason))
            DrawHint($"当前未发布新指令：{commands.Publish.SuppressionReason}");

        if (battlefield.LlmStrategicDecision.IsAvailable && !string.IsNullOrWhiteSpace(battlefield.LlmStrategicDecision.ShortReason))
            DrawHint($"AI大决策理由：{battlefield.LlmStrategicDecision.ShortReason}");
        else if (!string.IsNullOrWhiteSpace(battlefield.LlmStrategicDecision.GateReason))
            DrawHint($"AI门控：{battlefield.LlmStrategicDecision.GateReason}");

        if (decision.ObjectivePriorityTarget.HasValue)
            DrawHint($"拿点线：{decision.ObjectivePriorityTarget.Value.ActionText}；{decision.ObjectivePriorityTarget.Value.ReasonText}");
        if (decision.FightPriorityTarget.HasValue)
            DrawHint($"打团线：{decision.FightPriorityTarget.Value.ActionText}；{decision.FightPriorityTarget.Value.ReasonText}");
    }

    private static void DrawCombatHudRow(string label, string value, Vector4? valueColor = null)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextColored(new Vector4(0.80f, 0.80f, 0.84f, 1f), label);
        ImGui.TableNextColumn();
        if (valueColor.HasValue)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, valueColor.Value);
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
            ImGui.PopStyleColor();
        }
        else
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(value) ? "-" : value);
    }

    private static string BuildCombatHudScoreText(BattlefieldSnapshot battlefield)
    {
        if (battlefield.ScoreSituation.RankedAlliances.Length > 0)
        {
            return string.Join("  /  ", battlefield.ScoreSituation.RankedAlliances.Select(alliance =>
                $"{alliance.Name} {alliance.Score}"));
        }

        if (battlefield.Alliances.Length > 0)
            return string.Join("  /  ", battlefield.Alliances.Select(alliance => $"{alliance.Name} {alliance.Score}"));

        return "比分尚未形成";
    }

    private static string BuildCombatHudNextResourceText(BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (timeSituation.NextResourceSeconds.HasValue && !string.IsNullOrWhiteSpace(timeSituation.NextResourceName))
            return $"{timeSituation.NextResourceName}，{FormatDuration(Math.Max(0, timeSituation.NextResourceSeconds.Value))}";
        if (!string.IsNullOrWhiteSpace(timeSituation.MatchPhaseDetail))
            return timeSituation.MatchPhaseDetail;

        return "暂无明确资源计时";
    }

    private static string ResolveCombatHudPrimaryCommandText(BattlefieldDecisionSnapshot decision)
    {
        var commands = decision.CommandSituation;
        if (commands.PrimaryCommand.HasValue)
        {
            if (commands.Publish.ShouldAnnounce
                && commands.Publish.Command.HasValue
                && string.Equals(commands.Publish.Command.Value.Id, commands.PrimaryCommand.Value.Id, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(commands.Publish.SpeakText))
            {
                return commands.Publish.SpeakText;
            }

            return commands.PrimaryCommand.Value.CommandText;
        }
        if (commands.Publish.ShouldAnnounce && !string.IsNullOrWhiteSpace(commands.Publish.SpeakText))
            return commands.Publish.SpeakText;
        if (decision.PrimaryAction.HasValue)
            return decision.PrimaryAction.Value.Text;
        if (commands.PrimaryAction.HasValue)
            return commands.PrimaryAction.Value.Text;
        if (!string.IsNullOrWhiteSpace(decision.RecommendedAction))
            return decision.RecommendedAction;

        return "暂无主指令，主团跟我压进";
    }

    private static string ResolveCombatHudCurrentActionText(BattlefieldDecisionSnapshot decision)
    {
        if (decision.PrimaryAction.HasValue && !string.IsNullOrWhiteSpace(decision.PrimaryAction.Value.Text))
            return decision.PrimaryAction.Value.Text;
        if (decision.PublishedAction.HasValue && !string.IsNullOrWhiteSpace(decision.PublishedAction.Value.Text))
            return decision.PublishedAction.Value.Text;
        if (decision.CommandSituation.PrimaryAction.HasValue && !string.IsNullOrWhiteSpace(decision.CommandSituation.PrimaryAction.Value.Text))
            return decision.CommandSituation.PrimaryAction.Value.Text;
        if (!string.IsNullOrWhiteSpace(decision.RecommendedAction))
            return decision.RecommendedAction;

        return "战场态势不足，继续寻找目标";
    }

    private static string BuildCombatHudTargetText(BattlefieldDecisionSnapshot decision)
    {
        var parts = new List<string>(2);
        if (decision.ObjectivePriorityTarget.HasValue && !string.IsNullOrWhiteSpace(decision.ObjectivePriorityTarget.Value.TargetName))
            parts.Add($"拿点 {decision.ObjectivePriorityTarget.Value.TargetName}");
        if (decision.FightPriorityTarget.HasValue && !string.IsNullOrWhiteSpace(decision.FightPriorityTarget.Value.TargetName))
            parts.Add($"打团 {decision.FightPriorityTarget.Value.TargetName}");

        return parts.Count > 0 ? string.Join("  /  ", parts) : "暂无明确双目标";
    }

    private static string BuildCombatHudRiskText(BattlefieldDecisionSnapshot decision)
    {
        var risk = decision.RiskAssessment;
        return $"{risk.RiskLevel} {risk.OverallRisk:0}  /  人数差 {risk.NumberDisadvantageRisk:0}  /  夹击 {risk.FlankRisk:0}  /  敌方方向 {risk.EnemyMainGroupDirectionRisk:0}";
    }

    private static string BuildCombatHudLlmDecisionText(BattlefieldLlmStrategicDecisionSnapshot llm)
    {
        if (!llm.IsEnabled)
            return "未启用";
        if (!llm.IsConfigured)
            return "未配置 API Key";
        if (llm.IsPending && !llm.IsAvailable)
            return $"请求中：{llm.NeedText}";
        if (llm.IsAvailable)
        {
            var action = !string.IsNullOrWhiteSpace(llm.RecommendedAction) ? llm.RecommendedAction : llm.Decision;
            var age = llm.AgeSeconds >= 0 ? $" {llm.AgeSeconds}秒前" : string.Empty;
            return $"{action}{age}";
        }

        return llm.StatusText;
    }

    private static Vector4 LlmDecisionColor(BattlefieldLlmStrategicDecisionSnapshot llm)
    {
        if (!llm.IsEnabled || !llm.IsConfigured)
            return new Vector4(0.72f, 0.72f, 0.76f, 1f);
        if (llm.IsPending)
            return new Vector4(0.95f, 0.82f, 0.35f, 1f);
        if (llm.IsAvailable && llm.IsFresh)
            return PriorityColor(llm.Confidence);
        if (llm.IsAvailable)
            return new Vector4(0.72f, 0.72f, 0.76f, 1f);
        return new Vector4(0.62f, 0.62f, 0.66f, 1f);
    }

    private void DrawReviewPage()
    {
        var battlefield = plugin.WorldStateService.GetSnapshot();
        DrawSectionTitle("赛后/调试详细页", battlefield.IsInFrontline ? "完整战场拆解、诊断和复盘明细" : "进入纷争前线后自动读取详细状态");

        if (!battlefield.IsInFrontline)
        {
            DrawHint("当前不在纷争前线区域。区域地图雷达和比分读取会在进入前线后启动。");
            ImGui.Spacing();
        }

        var timeText = battlefield.TimeSituation.HasMatchTime
            ? $"{battlefield.MatchTimeRemaining / 60:D2}:{battlefield.MatchTimeRemaining % 60:D2}"
            : "--:--";
        ImGui.TextColored(new Vector4(0.95f, 0.82f, 0.35f, 1f), "剩余时间");
        ImGui.SameLine();
        ImGui.Text(timeText);
        ImGui.Spacing();

        if (BeginCollapsibleSection("核心态势", "时间、比分、指挥与目标优先级", true))
        {
            DrawTimeSituation(battlefield.TimeSituation);
            ImGui.Spacing();
            DrawScoreSituation(battlefield.ScoreSituation);
            ImGui.Spacing();
            DrawCommandSituation(battlefield.Decision.CommandSituation);
            ImGui.Spacing();
            DrawDecisionSituation(battlefield.Decision);
            ImGui.Spacing();
            DrawLlmStrategicDecision(battlefield.LlmStrategicDecision);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("战斗事件", "战场通告、击杀通报、极限槽与关键技能威胁", true))
        {
            DrawAnnouncementSituation(battlefield.AnnouncementSituation);
            ImGui.Spacing();
            DrawChatEventSituation(battlefield.ChatEventSituation);
            ImGui.Spacing();
            DrawLimitBreakSituation(battlefield.LimitBreak, battlefield.TeamSituation.LimitBreakThreats);
            ImGui.Spacing();
            DrawKeySkillThreatSituation(battlefield.TeamSituation.KeySkillThreats);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("地图与战术", "战术图谱、动态路径与高级战术洞察", true))
        {
            DrawMapTacticsSituation(battlefield.MapTactics);
            ImGui.Spacing();
            DrawAdvancedTacticalSituation(battlefield.TeamSituation.AdvancedTactics, plugin.Configuration.AdvancedTactics.ShowSuppressedInsights);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("采集诊断", "数据来源、玩家采集、地图目标和回放状态", false))
        {
            DrawGroupTitle("战场状态采集");
            DrawHint(battlefield.StatusText);
            ImGui.Text($"区域: {battlefield.TerritoryType}  地图: {battlefield.MapId}  过图中: {(battlefield.IsAreaTransitioning ? "是" : "否")}");
            ImGui.Text($"玩家: {battlefield.Players.Length}  友方: {battlefield.FriendlyPlayerCount}  敌方: {battlefield.EnemyPlayerCount}  死亡: {battlefield.DeadPlayerCount}  咏唱: {battlefield.CastingPlayerCount}");
            ImGui.Text($"目标: {battlefield.Objectives.Length}  实时地图目标: {battlefield.MapObjectives.Length}  人群: {battlefield.PlayerClusters.Length}  轨迹: {battlefield.PlayerTracks.Length}/{battlefield.EnemyMainGroupTrack.Length}  地图视野: {battlefield.MapVisionPoints.Length}/{battlefield.MapVisionClusters.Length}  地图事件: {battlefield.MapEvents.Length}  场地标记: {battlefield.FieldMarkers.Length}  目标标记: {battlefield.TargetMarkers.Length}");
            if (battlefield.LocalPlayer.HasValue)
                ImGui.Text($"本地玩家坐标: {FormatPosition(battlefield.LocalPlayer.Value.Position)}  阵营: {battlefield.LocalPlayer.Value.Battalion}");

            ImGui.Spacing();
            DrawReplayRecorderStatus(plugin.BattlefieldReplayRecorder.GetStatus());
            DrawTeamSituation(battlefield.TeamSituation);
            DrawMapObjectivePreview(battlefield.MapObjectives);
            DrawKnowledgePreview(battlefield.Knowledge);
            DrawClusterPreview(battlefield.PlayerClusters);
            DrawMapEventPreview(battlefield.MapEvents);

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.62f, 0.62f, 0.66f, 1f), battlefield.ScoreDebugInfo);
            EndCollapsibleSection();
        }
    }

    private static void DrawTimeSituation(BattlefieldTimeSituationSnapshot time)
    {
        DrawGroupTitle("时间与阶段");
        DrawHint(time.SummaryText);

        var nextResourceText = time.NextResourceSeconds.HasValue
            ? $"{time.NextResourceName} {time.NextResourceSeconds.Value / 60:D2}:{time.NextResourceSeconds.Value % 60:D2}（{time.NextResourceSource}）"
            : time.NextResourceSource;
        var mapPhaseText = string.IsNullOrWhiteSpace(time.MapRulePhaseName)
            ? "地图规则阶段：暂无"
            : $"地图规则阶段：{time.MapRulePhaseName}";
        if (time.MapRuleMaxActiveObjectives.HasValue)
            mapPhaseText += $"，最多{time.MapRuleMaxActiveObjectives.Value}个资源";
        if (!string.IsNullOrWhiteSpace(time.MapRuleMinimumObjectiveRank))
            mapPhaseText += $"，至少{time.MapRuleMinimumObjectiveRank}级";

        ImGui.Text($"阶段：{time.MatchPhaseName}  已进行：{time.MatchElapsedSeconds / 60:D2}:{time.MatchElapsedSeconds % 60:D2}");
        ImGui.Text(mapPhaseText);
        ImGui.Text($"下一资源：{nextResourceText}");
    }

    private static void DrawScoreSituation(BattlefieldScoreSituationSnapshot score)
    {
        DrawGroupTitle("比分与排名");
        DrawHint(score.SummaryText);

        if (score.RankedAlliances.Length > 0)
        {
            var rankingText = string.Join("  ", score.RankedAlliances.Select(alliance => $"{alliance.RankText}:{alliance.Name} {alliance.Score}"));
            ImGui.Text($"三方位次：{rankingText}");
        }

        ImGui.Text(score.VictoryScore > 0 ? $"获胜阈值：{score.VictoryScore}" : "获胜阈值：未知地图");
        ImGui.Spacing();

        var rows = score.Alliances.Length > 0 ? score.Alliances : score.RankedAlliances;
        if (!ImGui.BeginTable("##ScoreTable", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("阵营");
        ImGui.TableSetupColumn("分数");
        ImGui.TableSetupColumn("位次");
        ImGui.TableSetupColumn("近30秒趋势");
        ImGui.TableHeadersRow();

        foreach (var alliance in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            var name = alliance.IsLocalAlliance ? $"{alliance.Name}（我方）" : alliance.Name;
            ImGui.TextColored(GetAllianceColor(alliance.AllianceId), name);
            ImGui.TableNextColumn();
            ImGui.Text(FormatScoreWithVictory(alliance.Score, alliance.VictoryScore));
            ImGui.TableNextColumn();
            ImGui.Text(score.HasScoreData && !string.IsNullOrWhiteSpace(alliance.RankText) ? alliance.RankText : "未形成");
            ImGui.TableNextColumn();
            ImGui.Text(FormatScoreTrend(alliance));
        }

        ImGui.EndTable();
    }

    private static void DrawAnnouncementSituation(BattlefieldAnnouncementSituationSnapshot announcements)
    {
        DrawGroupTitle("战场通告");
        DrawHint(announcements.SummaryText);
        ImGui.TextWrapped($"天气：{announcements.WeatherStateText}{FormatOptionalCountdown(announcements.WeatherRemainingSeconds)}  来源：{announcements.SourceText}");

        if (announcements.RecentAnnouncements.Length == 0)
            return;

        var count = Math.Min(announcements.RecentAnnouncements.Length, 5);
        for (var i = 0; i < count; i++)
        {
            var item = announcements.RecentAnnouncements[i];
            ImGui.TextWrapped($"{AnnouncementKindText(item.Kind)}  {item.SummaryText}  {FormatOptionalCountdown(item.RemainingSeconds)}  [{item.Source}]");
            if (i < 2 && !string.IsNullOrWhiteSpace(item.Text))
                DrawHint(item.Text);
        }
    }

    private static void DrawChatEventSituation(BattlefieldChatEventSituationSnapshot chatEvents)
    {
        DrawGroupTitle("击杀/据点通报");
        DrawHint(chatEvents.SummaryText);
        ImGui.TextWrapped($"近30秒：我方击杀 {chatEvents.FriendlyKillsRecent}  我方阵亡 {chatEvents.FriendlyDeathsRecent}  敌方击杀 {chatEvents.EnemyKillsRecent}  据点/目标 {chatEvents.ObjectiveEventsRecent}  来源：{chatEvents.SourceText}");

        if (chatEvents.RecentEvents.Length == 0)
            return;

        var count = Math.Min(chatEvents.RecentEvents.Length, 6);
        for (var i = 0; i < count; i++)
        {
            var item = chatEvents.RecentEvents[i];
            ImGui.TextWrapped($"{ChatEventKindText(item.Kind)}  {item.SummaryText}  [{item.Source}]");
            if (i < 2 && !string.IsNullOrWhiteSpace(item.Text))
                DrawHint(item.Text);
        }
    }

    private static void DrawLimitBreakSituation(
        BattlefieldLimitBreakSnapshot limitBreak,
        BattlefieldLimitBreakThreatSituationSnapshot threats)
    {
        DrawGroupTitle("极限槽预测");
        DrawHint(limitBreak.SummaryText);
        if (limitBreak.IsAvailable)
        {
            var progress = Math.Clamp(limitBreak.Percent / 100f, 0f, 1f);
            ImGui.Text($"极限槽百分比：{limitBreak.Percent:0.0}%");
            ImGui.ProgressBar(progress, new Vector2(-1f, 0f), string.Empty);
            var predictionText = limitBreak.IsInPvPRegion && limitBreak.PercentPerTick > 0f
                ? $"预估剩余：{limitBreak.EstimatedSecondsRemaining:F0} 秒 / {limitBreak.EstimatedTicksRemaining:F0} 跳"
                : "预估剩余：下一次充能跳动后更新";
            ImGui.Text($"单位：{limitBreak.CurrentUnits}/{limitBreak.BarUnits}  可用等级：{limitBreak.Level}  {predictionText}");
        }

        DrawHint(threats.SummaryText);
        DrawLimitBreakThreatRows("敌方极限技威胁", threats.TopEnemyThreats);
        DrawLimitBreakThreatRows("我方极限技窗口", threats.TopFriendlyThreats);
    }

    private static void DrawKeySkillThreatSituation(BattlefieldKeySkillThreatSituationSnapshot threats)
    {
        DrawGroupTitle("关键技能威胁");
        DrawHint(threats.SummaryText);
        ImGui.Text($"敌方：疑似可用 {threats.EnemyLikelyReadyCount}  高危 {threats.EnemyHighThreatCount}  控制链 {threats.EnemyControlChainCount}  破防 {threats.EnemyDefenseBreakWindowCount}  斩杀 {threats.EnemyExecuteWindowCount}");
        ImGui.Text($"我方：疑似可用 {threats.FriendlyLikelyReadyCount}  高威胁 {threats.FriendlyHighThreatCount}  来源：{threats.SourceText}");

        DrawKeySkillThreatRows("敌方关键技能", threats.TopEnemyThreats);
        DrawKeySkillThreatRows("我方可打窗口", threats.TopFriendlyThreats);

        if (threats.RecentUses.Length == 0)
            return;

        ImGui.TextColored(new Vector4(0.72f, 0.78f, 0.86f, 1f), "近期使用/状态记录");
        var count = Math.Min(threats.RecentUses.Length, 5);
        for (var i = 0; i < count; i++)
        {
            var item = threats.RecentUses[i];
            var ageSeconds = Math.Max(0, (int)(item.AgeMs / 1000));
            var target = string.IsNullOrWhiteSpace(item.TargetName) ? string.Empty : $" -> {item.TargetName}";
            ImGui.Text($"{ageSeconds}秒前 {RelationText(item.Relation)} {item.Name}({item.JobName}) {item.SkillName}{target} [{item.SourceText}]");
        }
    }

    private static void DrawMapTacticsSituation(BattlefieldMapTacticsSnapshot tactics)
    {
        DrawGroupTitle("地图战术层");
        DrawHint(tactics.SummaryText);
        if (!string.IsNullOrWhiteSpace(tactics.TacticalGraphCoverageText))
            DrawHint(tactics.TacticalGraphCoverageText);
        if (!string.IsNullOrWhiteSpace(tactics.TacticalGraphSourceText))
            DrawHint(tactics.TacticalGraphSourceText);
        if (!string.IsNullOrWhiteSpace(tactics.CurrentRecommendation))
            ImGui.TextColored(new Vector4(0.95f, 0.82f, 0.35f, 1f), tactics.CurrentRecommendation);

        if (!tactics.IsAvailable)
            return;

        ImGui.Text($"底图采样：内置 {tactics.BuiltInGraphPointCount}  手动 {tactics.ManualAnnotationCount}  合计 {tactics.AnnotationCount}  实时热区 {tactics.HeatPoints.Length}");
        ImGui.Text($"高低差/危险：静态 {tactics.StaticDangerCount}  动态/热区 {tactics.DynamicDangerCount}  必卡点 {tactics.MandatoryChokeCount}");
        if (!string.IsNullOrWhiteSpace(tactics.FriendlyObservedPath.SummaryText))
            DrawHint(tactics.FriendlyObservedPath.SummaryText);
        if (!string.IsNullOrWhiteSpace(tactics.EnemyObservedPath.SummaryText))
            DrawHint(tactics.EnemyObservedPath.SummaryText);

        var heatCount = Math.Min(tactics.HeatPoints.Length, 5);
        if (heatCount > 0)
        {
            ImGui.TextColored(new Vector4(1f, 0.58f, 0.22f, 1f), "实时危险热区");
            for (var i = 0; i < heatCount; i++)
            {
                var heat = tactics.HeatPoints[i];
                ImGui.TextColored(RiskColor(heat.Intensity), $"{heat.SourceText}  热度:{heat.Intensity:0}  半径:{heat.Radius:0}y");
            }
        }

        var zoneCount = Math.Min(tactics.TopZones.Length, 5);
        for (var i = 0; i < zoneCount; i++)
        {
            var zone = tactics.TopZones[i];
            ImGui.TextColored(AnnotationKindColor(zone.Kind), $"{zone.Recommendation} {zone.Label} 风险:{zone.TotalRisk:0} 静:{zone.StaticRisk:0} 动:{zone.DynamicRisk:0}");
            if (i < 3)
                DrawHint(zone.EvidenceText);
        }

        var routeCount = Math.Min(tactics.Routes.Length, 4);
        for (var i = 0; i < routeCount; i++)
        {
            var route = tactics.Routes[i];
            var routeLabel = route.KindSummary == "动态寻路" ? "动态路径" : "图谱路径";
            ImGui.Text($"{routeLabel} {route.RouteId}: {route.Recommendation} 风险:{route.TotalRisk:0} 距离:{route.Distance:0}y 骑:{FormatDuration(route.MountedEtaSeconds)}");
            if (i < 2)
                DrawHint(route.EvidenceText);
        }
    }

    private static void DrawAdvancedTacticalSituation(BattlefieldAdvancedTacticalSituationSnapshot tactics, bool showSuppressedInsights)
    {
        DrawAdvancedTacticalSituation(tactics);
        if (!string.IsNullOrWhiteSpace(tactics.CalibrationText))
            DrawHint(tactics.CalibrationText);
        DrawHint($"候选:{tactics.RawInsightCount}  已压制:{tactics.SuppressedInsightCount}");
        if (!showSuppressedInsights || tactics.SuppressedInsights.Length == 0)
            return;

        DrawHint("被压制候选（用于校准，不进入报警/决策风险）：");
        foreach (var insight in tactics.SuppressedInsights.Take(4))
            DrawHint($"{insight.Label} 风险:{insight.Severity:0} 置信:{insight.Confidence:P0} 人数:{insight.InvolvedCount}；{insight.EvidenceText}");
    }

    private static void DrawAdvancedTacticalSituation(BattlefieldAdvancedTacticalSituationSnapshot tactics)
    {
        DrawGroupTitle("高级战术洞察");
        DrawHint(tactics.SummaryText);
        ImGui.Text($"跟随率:{tactics.FriendlyFollowRate:0}%  凝聚:{tactics.FriendlyCohesionScore:0}  方向一致:{tactics.FriendlyDirectionConsistency:0}%  样本:{tactics.FriendlyFollowerCount}/{tactics.FriendlySampleCount}");
        ImGui.Text($"追击陷阱:{tactics.AmbushRisk:0}  第三方:{tactics.ThirdPartyPincerRisk:0}  高台:{tactics.HighGroundDropRisk:0}  封路:{tactics.ChokeBlockRisk:0}  组排:{tactics.CoordinatedSquadRisk:0}");

        if (tactics.Insights.Length == 0)
        {
            DrawHint("暂无高级战术异常样本。");
            return;
        }

        var count = Math.Min(tactics.Insights.Length, 5);
        for (var i = 0; i < count; i++)
        {
            var insight = tactics.Insights[i];
            ImGui.TextColored(RiskColor(insight.Severity), $"{insight.Label} 风险:{insight.Severity:0} 置信:{insight.Confidence:P0} 人数:{insight.InvolvedCount} 建议:{insight.Recommendation}");
            if (i < 3)
                DrawHint(insight.EvidenceText);
        }
    }

    private static void DrawCommandSituation(BattlefieldCommandSituationSnapshot commands)
    {
        DrawGroupTitle("实时指挥");
        DrawHint(commands.SummaryText);
        if (!string.IsNullOrWhiteSpace(commands.Publish.StatusText))
        {
            var publishColor = commands.Publish.ShouldAnnounce
                ? new Vector4(0.48f, 0.90f, 0.62f, 1f)
                : commands.Publish.IsSuppressed
                    ? new Vector4(0.95f, 0.82f, 0.35f, 1f)
                    : new Vector4(0.72f, 0.72f, 0.76f, 1f);
            ImGui.TextColored(publishColor, $"发布：{commands.Publish.StatusText}");
            if (commands.Publish.IsSuppressed)
                DrawHint($"压制原因：{commands.Publish.SuppressionReason}；全局 {commands.Publish.GlobalCooldownRemainingSeconds}秒 / 同句 {commands.Publish.CommandCooldownRemainingSeconds}秒 / 同类 {commands.Publish.KindCooldownRemainingSeconds}秒");
        }

        if (commands.PrimaryCommand.HasValue)
        {
            var primary = commands.PrimaryCommand.Value;
            ImGui.TextColored(RiskColor(primary.Urgency), primary.CommandText);
            DrawHint($"{primary.Scope} / {CommandKindText(primary.Kind)} / 优先 {CommandPriorityText(primary)} / 分数 {primary.Score:0} / 紧急 {primary.Urgency:0} / 冷却 {primary.CooldownSeconds}秒；{primary.ReasonText}");
        }

        if (commands.PrimaryAction.HasValue)
        {
            var action = commands.PrimaryAction.Value;
            ImGui.TextColored(PriorityColor(action.Priority), $"行动：{ActionTypeText(action.ActionType)} / {action.Text}");
            DrawHint($"优先 {action.Priority:0} / 置信 {action.Confidence:0} / 风险 {action.Risk:0} / 紧急 {action.Urgency:0} / 保持 {action.HoldSeconds}秒；目的地 {action.DestinationName} {FormatPosition(action.Destination)}；目标 {action.TargetName}");
            DrawHint($"路径：{action.RouteText}；预计用时 {FormatDuration(action.EtaSeconds)}；倒计时 {FormatCountdown(action.CountdownSeconds)}；目的：{action.PurposeText}");
            DrawHint($"理由：{action.ReasonText}；失败条件：{action.FailureConditionText}");
            if (commands.IsActionHoldActive)
                DrawHint($"防抖保持：{commands.ActionHoldReason}（剩余 {commands.ActionHoldRemainingSeconds}秒）");
        }

        if (commands.ActionCandidates.Length > 0)
        {
            var actionCount = Math.Min(commands.ActionCandidates.Length, 5);
            if (ImGui.BeginTable("##RealtimeActionCandidateTable", 6, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("行动");
                ImGui.TableSetupColumn("优先", ImGuiTableColumnFlags.WidthFixed, 48f);
                ImGui.TableSetupColumn("置信", ImGuiTableColumnFlags.WidthFixed, 48f);
                ImGui.TableSetupColumn("目的地");
                ImGui.TableSetupColumn("路径");
                ImGui.TableSetupColumn("失败条件");
                ImGui.TableHeadersRow();

                for (var i = 0; i < actionCount; i++)
                {
                    var item = commands.ActionCandidates[i];
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text($"{ActionTypeText(item.ActionType)} {item.Text}");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(PriorityColor(item.Priority), $"{item.Priority:0}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{item.Confidence:0}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{item.DestinationName} 预计:{FormatDuration(item.EtaSeconds)} 倒:{FormatCountdown(item.CountdownSeconds)}");
                    ImGui.TableNextColumn();
                    ImGui.Text(item.RouteText);
                    ImGui.TableNextColumn();
                    ImGui.Text(item.FailureConditionText);
                }

                ImGui.EndTable();
            }
        }

        if (commands.Commands.Length == 0)
            return;

        var count = Math.Min(commands.Commands.Length, 6);
        if (!ImGui.BeginTable("##RealtimeCommandTable", 6, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("指令");
        ImGui.TableSetupColumn("范围", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn("级", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("分", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("急", ImGuiTableColumnFlags.WidthFixed, 44f);
        ImGui.TableSetupColumn("原因");
        ImGui.TableHeadersRow();

        for (var i = 0; i < count; i++)
        {
            var item = commands.Commands[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(item.CommandText);
            ImGui.TableNextColumn();
            ImGui.Text(item.Scope);
            ImGui.TableNextColumn();
            ImGui.Text(CommandPriorityText(item));
            ImGui.TableNextColumn();
            ImGui.Text($"{item.Score:0}");
            ImGui.TableNextColumn();
            ImGui.TextColored(RiskColor(item.Urgency), $"{item.Urgency:0}");
            ImGui.TableNextColumn();
            ImGui.Text(item.ReasonText);
            if (i < 3 && !string.IsNullOrWhiteSpace(item.EvidenceText))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TableSetColumnIndex(0);
                DrawHint(item.EvidenceText);
            }
        }

        ImGui.EndTable();
    }

    private static void DrawDecisionSituation(BattlefieldDecisionSnapshot decision)
    {
        DrawGroupTitle("目标优先级与风险");
        DrawHint(decision.SummaryText);
        ImGui.TextColored(RiskColor(decision.RiskAssessment.OverallRisk), decision.RecommendedAction);
        DrawDualPriorityTargets(decision);
        ImGui.Text($"总体风险：{decision.RiskAssessment.RiskLevel} {decision.RiskAssessment.OverallRisk:0}  帧事件:{decision.RiskAssessment.FrameEventRisk:0}  夹击:{decision.RiskAssessment.FlankRisk:0} 人数差:{decision.RiskAssessment.NumberDisadvantageRisk:0} 复活:{decision.RiskAssessment.RespawnRisk:0} 敌方方向:{decision.RiskAssessment.EnemyMainGroupDirectionRisk:0}");
        ImGui.Text($"地形:{decision.RiskAssessment.TerrainRisk:0} 出路:{decision.RiskAssessment.RetreatRouteRisk:0} 被包:{decision.RiskAssessment.EncirclementRisk:0}  技能:{decision.RiskAssessment.SkillThreatRisk:0} 极限技:{decision.RiskAssessment.LimitBreakRisk:0} 战意:{decision.RiskAssessment.BattleHighRisk:0} 目标:{decision.RiskAssessment.ObjectiveRisk:0}");
        ImGui.Text($"追击陷阱:{decision.RiskAssessment.AmbushRisk:0} 跟随散乱:{decision.RiskAssessment.CohesionRisk:0} 第三方:{decision.RiskAssessment.ThirdPartyPincerRisk:0} 高台:{decision.RiskAssessment.HighGroundDropRisk:0} 封路:{decision.RiskAssessment.ChokeBlockRisk:0} 组排:{decision.RiskAssessment.CoordinatedSquadRisk:0}");
        DrawDecisionQualitySituation(decision.DecisionQuality);

        if (decision.ObjectivePriorities.Length == 0)
            return;

        var count = Math.Min(decision.ObjectivePriorities.Length, 6);
        if (!ImGui.BeginTable("##DecisionObjectivePriorityTable", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("目标");
        ImGui.TableSetupColumn("建议", ImGuiTableColumnFlags.WidthFixed, 82f);
        ImGui.TableSetupColumn("优先", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("风险", ImGuiTableColumnFlags.WidthFixed, 58f);
        ImGui.TableSetupColumn("拆分");
        ImGui.TableHeadersRow();

        for (var i = 0; i < count; i++)
        {
            var item = decision.ObjectivePriorities[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{item.Name} {MapObjectiveCategoryText(item.Category)} {MapObjectiveStateText(item.State)}");
            ImGui.TableNextColumn();
            ImGui.Text(item.RecommendedAction);
            ImGui.TableNextColumn();
            ImGui.TextColored(PriorityColor(item.PriorityScore), $"{item.PriorityScore:0}");
            ImGui.TableNextColumn();
            ImGui.TextColored(RiskColor(item.RiskScore), $"{item.RiskScore:0}");
            ImGui.TableNextColumn();
            ImGui.Text($"收益:{item.RewardScore:0} 时机:{item.TimingScore:0} 距离:{item.DistanceToLocal:0}y 预计:{FormatDuration(item.MountedEtaSeconds)}");
        }

        ImGui.EndTable();

        var top = decision.ObjectivePriorities[0];
        DrawHint(top.EvidenceText);
    }

    private static void DrawLlmStrategicDecision(BattlefieldLlmStrategicDecisionSnapshot llm)
    {
        DrawGroupTitle("AI大决策");
        ImGui.TextColored(LlmDecisionColor(llm), llm.StatusText);
        if (!string.IsNullOrWhiteSpace(llm.GateReason))
            DrawHint($"门控：{llm.NeedText}；{llm.GateReason}");

        if (!llm.IsAvailable)
        {
            if (!string.IsNullOrWhiteSpace(llm.ErrorText))
                DrawHint($"错误：{llm.ErrorText}");
            return;
        }

        ImGui.TextWrapped($"决策：{llm.Decision}");
        if (!string.IsNullOrWhiteSpace(llm.RecommendedAction))
            ImGui.Text($"行动短句：{llm.RecommendedAction}");
        if (!string.IsNullOrWhiteSpace(llm.PriorityTarget))
            ImGui.Text($"优先目标：{llm.PriorityTarget}");
        ImGui.Text($"置信：{llm.Confidence:0}  风险：{llm.Risk:0}  会话：{llm.SessionId}");
        if (!string.IsNullOrWhiteSpace(llm.ShortReason))
            DrawHint($"理由：{llm.ShortReason}");
        if (!string.IsNullOrWhiteSpace(llm.DebugText))
            DrawHint($"调试：{llm.DebugText}");
        if (!string.IsNullOrWhiteSpace(llm.RawJson))
            DrawHint($"原始JSON：{llm.RawJson}");
    }

    private static void DrawLlmDebugSnapshot(BattlefieldLlmStrategicDecisionSnapshot llm, BattlefieldLlmDebugSnapshot debug)
    {
        ImGui.TextColored(LlmDecisionColor(llm), debug.StatusText);
        ImGui.Text($"当前会话：{(string.IsNullOrWhiteSpace(debug.SessionId) ? "未建立" : debug.SessionId)}");
        ImGui.Text($"请求状态：{BuildLlmDebugStatusText(debug)}");
        if (!string.IsNullOrWhiteSpace(debug.CurrentGateReason))
            DrawHint($"当前门控：{debug.CurrentNeedText}；{debug.CurrentGateReason}");

        if (!debug.HasRequest)
        {
            DrawHint("本局还没有发出 AI 请求。可以先点“忽略门控立即请求”或“发送测试对话”。");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(debug.LastRequestGateReason))
                DrawHint($"最近请求触发：{debug.LastRequestNeedText}；{debug.LastRequestGateReason}");
            if (!string.IsNullOrWhiteSpace(debug.LastRequestSituationKey))
                DrawHint($"场景键：{debug.LastRequestSituationKey}");
        }

        if (!string.IsNullOrWhiteSpace(debug.ErrorText))
            DrawHint($"最近错误：{debug.ErrorText}");
        if (!string.IsNullOrWhiteSpace(llm.RecommendedAction) || !string.IsNullOrWhiteSpace(llm.ShortReason))
            DrawHint($"最近决策：{llm.RecommendedAction}；{llm.ShortReason}");

        DrawReadonlyTextPanel("手动附言", debug.ManualInstruction, "LlmManualInstruction", 72f);
        DrawReadonlyTextPanel("系统提示词", debug.SystemPrompt, "LlmSystemPrompt", 150f);
        DrawReadonlyTextPanel("用户提示词", debug.UserPrompt, "LlmUserPrompt", 220f);
        DrawReadonlyTextPanel("模型原始响应", debug.RawResponse, "LlmRawResponse", 150f);
        DrawReadonlyTextPanel("解析结果 JSON", debug.ParsedJson, "LlmParsedJson", 150f);
        DrawReadonlyTextPanel("模型调试摘要", debug.DebugText, "LlmDebugText", 120f);
        DrawReadonlyTextPanel("比分读取", debug.DebugScoreRead, "LlmDebugScoreRead", 72f);
        DrawReadonlyTextPanel("位置读取", debug.DebugPositionRead, "LlmDebugPositionRead", 72f);
        DrawReadonlyTextPanel("延迟说明", debug.DebugLatencyNote, "LlmDebugLatencyNote", 72f);
        DrawReadonlyTextPanel("最近上下文", BuildLlmConversationDebugText(debug.ConversationTurns), "LlmConversationContext", 220f);
    }

    private static void DrawDualPriorityTargets(BattlefieldDecisionSnapshot decision)
    {
        if (!decision.ObjectivePriorityTarget.HasValue && !decision.FightPriorityTarget.HasValue)
            return;

        if (!ImGui.BeginTable("##DualPriorityTargetTable", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 56f);
        ImGui.TableSetupColumn("最优先目标", ImGuiTableColumnFlags.WidthFixed, 150f);
        ImGui.TableSetupColumn("当前动作");
        ImGui.TableSetupColumn("优先", ImGuiTableColumnFlags.WidthFixed, 54f);
        ImGui.TableSetupColumn("急", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableHeadersRow();

        if (decision.ObjectivePriorityTarget.HasValue)
            DrawPriorityTargetRow(decision.ObjectivePriorityTarget.Value);
        if (decision.FightPriorityTarget.HasValue)
            DrawPriorityTargetRow(decision.FightPriorityTarget.Value);

        ImGui.EndTable();

        if (decision.ObjectivePriorityTarget.HasValue)
            DrawHint($"拿点理由：{decision.ObjectivePriorityTarget.Value.ReasonText}");
        if (decision.FightPriorityTarget.HasValue)
            DrawHint($"打架理由：{decision.FightPriorityTarget.Value.ReasonText}");
    }

    private static void DrawPriorityTargetRow(BattlefieldPriorityTargetSnapshot target)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(target.Lane);
        ImGui.TableNextColumn();
        ImGui.Text(target.TargetName);
        ImGui.TableNextColumn();
        ImGui.Text(target.ActionText);
        ImGui.TableNextColumn();
        ImGui.TextColored(PriorityColor(target.Priority), $"{target.Priority:0}");
        ImGui.TableNextColumn();
        ImGui.TextColored(RiskColor(target.Urgency), $"{target.Urgency:0}");
    }

    private static void DrawDecisionQualitySituation(BattlefieldDecisionQualitySnapshot quality)
    {
        if (!quality.IsAvailable)
            return;

        DrawGroupTitle("决策质量");
        DrawHint(quality.SummaryText);
        DrawHint(quality.MapTemplate.SummaryText);
        if (quality.InputReliability.IsAvailable)
        {
            var input = quality.InputReliability;
            ImGui.TextColored(input.CanPublish ? PriorityColor(input.OverallReliability) : RiskColor(72f), $"输入可靠度：{input.OverallReliability:0}  {(input.CanPublish ? "可喊" : "只提示")}");
            DrawHint(input.SummaryText);
            foreach (var component in input.Components.Take(4))
                ImGui.TextColored(component.Reliability >= input.CriticalInputReliabilityThreshold ? PriorityColor(component.Reliability) : RiskColor(70f), $"{component.Label}:{component.Reliability:0}  {component.EvidenceText}");
        }
        if (!string.IsNullOrWhiteSpace(quality.CalibrationText))
            DrawHint($"回放校准：{quality.CalibrationText}");

        foreach (var item in quality.CommandEffectiveness.Take(3))
            ImGui.TextColored(item.Modifier >= 0f ? PriorityColor(60f) : RiskColor(65f), $"{CommandKindText(item.Kind)} 有效性：样本 {item.SampleCount} 均值 {item.AverageScore:+0.0;-0.0;0} 正向 {item.PositiveRate:P0} 调权 {item.Modifier:+0.0;-0.0;0}");

        foreach (var intent in quality.EnemyIntentPredictions.Take(3))
        {
            ImGui.TextColored(RiskColor(intent.Urgency), $"敌方意图：{EnemyIntentKindText(intent.Kind)} {intent.AllianceName} 置信:{intent.Confidence:0} 紧急:{intent.Urgency:0} 建议:{intent.Recommendation}");
            DrawHint(intent.EvidenceText);
        }

        foreach (var role in quality.TeamRoleInsights.Take(3))
        {
            ImGui.TextColored(PriorityColor(role.Severity), $"职责：{TeamRoleInsightKindText(role.Kind)} {role.TargetName} 强度:{role.Severity:0} 人数:{role.PlayerCount} 建议:{role.Recommendation}");
            DrawHint(role.EvidenceText);
        }
    }

    private static void DrawLimitBreakThreatRows(string title, BattlefieldLimitBreakThreatSnapshot[] threats)
    {
        if (threats.Length == 0)
        {
            DrawHint($"{title}：暂无可见样本");
            return;
        }

        ImGui.TextColored(new Vector4(0.85f, 0.76f, 0.48f, 1f), title);
        var count = Math.Min(threats.Length, 4);
        for (var i = 0; i < count; i++)
        {
            var item = threats[i];
            var ready = item.IsLikelyReady ? "可用？" : $"{item.EstimatedSecondsToReady:0}秒";
            var battleHigh = item.BattleHighLevel > 0 ? (item.IsBattleFever ? " 战意狂热" : $" 战意{item.BattleHighLevel}") : string.Empty;
            ImGui.Text($"{LimitBreakThreatLevelText(item.ThreatLevel)} {item.Name}({item.JobName}) {item.EstimatedPercent:0.0}%/{ready} 分:{item.ThreatScore:0} {item.ThreatType}{battleHigh}");
            if (i < 2)
                DrawHint(item.EvidenceText);
        }
    }

    private static void DrawKeySkillThreatRows(string title, BattlefieldKeySkillThreatSnapshot[] threats)
    {
        if (threats.Length == 0)
        {
            DrawHint($"{title}：暂无可见样本");
            return;
        }

        ImGui.TextColored(new Vector4(0.85f, 0.76f, 0.48f, 1f), title);
        var count = Math.Min(threats.Length, 4);
        for (var i = 0; i < count; i++)
        {
            var item = threats[i];
            var cooldown = item.IsEstimatedReady ? "可用？" : $"{item.EstimatedCooldownRemainingSeconds:0}秒";
            var window = item.IsControlChainCandidate ? " 控制链"
                : item.IsDefenseBreakWindow ? " 破防"
                : item.IsExecuteWindow ? " 斩杀"
                : string.Empty;
            ImGui.Text($"{LimitBreakThreatLevelText(item.ThreatLevel)} {item.Name}({item.JobName}) {item.SkillName} {KeySkillKindText(item.Kind)} 冷却:{cooldown} 分:{item.ThreatScore:0}{window}");
            if (i < 2)
                DrawHint(item.EvidenceText);
        }
    }

    private void DrawRadarPage()
    {
        var config = plugin.Configuration.Radar;
        DrawSectionTitle("雷达设置", "按使用场景分组，修改后立即保存");

        var changed = false;
        if (BeginCollapsibleSection("总开关", "雷达启用和非前线区域显示策略", true))
        {
            changed |= DrawToggle("启用雷达", config.Enabled, value => config.Enabled = value);
            changed |= DrawToggle("允许在非前线区域显示", config.OutsideFrontline, value => config.OutsideFrontline = value);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("覆盖层", "屏幕雷达、区域地图雷达和地图标记", true))
        {
            changed |= DrawToggle("屏幕玩家雷达", config.ScreenRadar, value => config.ScreenRadar = value);
            changed |= DrawToggle("区域地图雷达", config.MapRadar, value => config.MapRadar = value);
            changed |= DrawToggle("显示场地标记", config.FieldMarkers, value => config.FieldMarkers = value);
            changed |= DrawToggle("显示目标标记", config.TargetMarkers, value => config.TargetMarkers = value);
            DrawHint("不再执行游戏内目标标记；集火信息只保留在指挥界面，不再弹单点窗口。");
            changed |= DrawToggle("玩家对战中隐藏友方玩家", config.HideFriendlyCharacters, value => config.HideFriendlyCharacters = value);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("地图信息", "名称、职业、咏唱、倒计时和血量显示", true))
        {
            changed |= DrawToggle("显示玩家名称", config.ShowNames, value => config.ShowNames = value);
            changed |= DrawToggle("显示职业图标", config.ShowJobIcons, value => config.ShowJobIcons = value);
            changed |= DrawToggle("仅显示圆点", config.OnlyDisplayDot, value => config.OnlyDisplayDot = value);
            changed |= DrawToggle("显示咏唱条", config.ShowCastBars, value => config.ShowCastBars = value);
            changed |= DrawToggle("区域地图显示倒计时文本", config.ShowCountdownOnMap, value => config.ShowCountdownOnMap = value);
            changed |= DrawToggle("区域地图显示血量百分比", config.ShowHpPercentOnMap, value => config.ShowHpPercentOnMap = value);
            changed |= DrawToggle("显示据点分值", config.ShowControlPointScoreOnMap, value => config.ShowControlPointScoreOnMap = value);
            changed |= DrawToggle("区域地图显示玩家对战加载范围", config.ShowMapLoadingRange, value => config.ShowMapLoadingRange = value);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("尺寸", "点位半径、职业图标缩放和图标样式", false))
        {
            changed |= DrawSliderFloat("屏幕圆点半径", config.ScreenDotRadius, 2f, 24f, value => config.ScreenDotRadius = value);
            changed |= DrawSliderFloat("地图圆点半径", config.MapDotRadius, 1f, 12f, value => config.MapDotRadius = value);
            changed |= DrawSliderFloat("职业图标缩放", config.JobIconScale, 0.6f, 2.4f, value => config.JobIconScale = value);
            changed |= DrawSliderInt("职业图标样式", config.JobIconStyle, 1, 4, value => config.JobIconStyle = value);
            EndCollapsibleSection();
        }

        if (changed)
            plugin.Configuration.Save();
    }

    private void DrawMapEditorPage()
    {
        var snapshot = plugin.WorldStateService.GetSnapshot();
        EnsureOfflineMapSelection(snapshot);
        var offlineEntry = OfflineFrontlineMaps[Math.Clamp(offlineMapIndex, 0, OfflineFrontlineMaps.Length - 1)];
        var offlineMap = ResolveOfflineMapMetadata(offlineEntry);
        var territoryType = offlineMap.TerritoryType;
        var mapId = offlineMap.MapId;
        var mapName = offlineMap.DisplayName;
        var document = plugin.MapAnnotationService.GetDocument(territoryType, mapId, mapName);
        var builtInGraph = plugin.MapTacticalGraphService.Resolve(territoryType, mapId);
        var combinedPoints = BuildCombinedAnnotationPoints(builtInGraph?.Points, document.Points);
        var selectedCurrentMap = snapshot.TerritoryType == territoryType && snapshot.MapId == mapId;

        DrawSectionTitle("地图标注", "离线选择前线地图，直接在插件画布上落点");
        if (BeginCollapsibleSection("地图与图谱", "选择地图、查看内置图谱覆盖和保存目录", true))
        {
            DrawOfflineMapSelector(snapshot, offlineMap);
            DrawHint($"标注地图：{(!string.IsNullOrWhiteSpace(document.MapName) ? document.MapName : mapName)}  区域编号:{territoryType}  地图编号:{mapId}");
            DrawHint($"当前区域：区域编号:{snapshot.TerritoryType}  地图编号:{snapshot.MapId}  {(selectedCurrentMap ? "与离线选择一致" : "与离线选择不同")}");
            DrawHint(builtInGraph != null
                ? $"内置战术图谱：节点 {builtInGraph.PointCount}，区域 {builtInGraph.RegionCount}，路径图形 {builtInGraph.PathCount}；{builtInGraph.CoverageText}"
                : "内置战术图谱：当前地图未配置，暂只使用手动标注");
            DrawHint($"草稿目录：{plugin.MapAnnotationService.RootDirectory}");
            DrawHint($"图谱目录：{plugin.MapTacticalGraphService.RootDirectory}");
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("图谱版本管理", "保存、备份、回滚、对比和地图版本迁移", true))
        {
            DrawMapGraphVersionManager(territoryType, mapId);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("标注工具", "选择类型、命名、写路径编号，并把草稿保存成自定义图谱", true))
        {
            annotationClickMode = DrawInlineToggle("点击画布标注", annotationClickMode);
            ImGui.SameLine();
            DrawHint(annotationClickMode ? "开启后，左键点击离线地图或游戏区域地图会添加标注。" : "关闭后，只显示已有标注。");

            DrawAnnotationKindSelector();
            _ = DrawInputText("标注名称", annotationLabel, 80, value => annotationLabel = value);
            _ = DrawInputText("路径编号（桥面/跳台/传送/绕后可填）", annotationRouteId, 80, value => annotationRouteId = value);
            _ = DrawSliderFloat("区域半径", annotationRadius, 0f, 220f, value => annotationRadius = value);
            _ = DrawSliderInt("风险分", annotationRiskScore, 0, 100, value => annotationRiskScore = value);
            DrawHint("桥面、跳台、传送、绕后路径请用相同路径编号连续落点；跳台/单向落差路径编号可写“单向-跳台北缘”。区域请用“区域-名称”围 3 点以上。");

            if (ImGui.Button("标注本地位置", new Vector2(130f, 28f)))
            {
                if (snapshot.LocalPlayer.HasValue && selectedCurrentMap)
                    AddMapAnnotation(snapshot.LocalPlayer.Value.Position, territoryType, mapId, mapName);
            }

            ImGui.SameLine();
            if (ImGui.Button("导入实时目标", new Vector2(120f, 28f)))
            {
                if (selectedCurrentMap)
                    ImportCurrentMapObjectives(snapshot, territoryType, mapId, mapName);
            }

            ImGui.SameLine();
            if (ImGui.Button("导入地图事件", new Vector2(120f, 28f)))
            {
                if (selectedCurrentMap)
                    ImportCurrentMapEvents(snapshot, territoryType, mapId, mapName);
            }

            ImGui.SameLine();
            if (ImGui.Button("撤销最后一个", new Vector2(120f, 28f)))
            {
                plugin.MapAnnotationService.UndoLatest(territoryType, mapId);
                annotationClearArmed = false;
            }

            ImGui.SameLine();
            if (!annotationClearArmed)
            {
                if (ImGui.Button("清空本图", new Vector2(100f, 28f)))
                    annotationClearArmed = true;
            }
            else
            {
                if (ImGui.Button("确认清空", new Vector2(100f, 28f)))
                {
                    plugin.MapAnnotationService.Clear(territoryType, mapId);
                    annotationClearArmed = false;
                }
                ImGui.SameLine();
                if (ImGui.Button("取消", new Vector2(70f, 28f)))
                    annotationClearArmed = false;
            }

            if (ImGui.Button("保存为本图战术图谱", new Vector2(160f, 28f)))
                SaveCurrentMapAnnotationsAsCustomGraph(offlineMap, document);

            ImGui.SameLine();
            DrawHint(string.IsNullOrWhiteSpace(mapGraphSaveStatus)
                ? "保存后会直接写入本图自定义战术图谱；区域用“区域-名称”围 3 点以上，其他同路径编号点会写成通行路径。"
                : mapGraphSaveStatus);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("校准辅助", "实战轨迹、点击误差、路径长度误差和坐标批量修正", true))
        {
            DrawMapCalibrationTools(snapshot, offlineMap, document, combinedPoints, selectedCurrentMap);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("显示过滤", "按图谱来源、区域、路径、节点和标注类型过滤画布", true))
        {
            DrawMapGraphDisplayFilters();
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("离线地图画布", "缩放、拖动、落点、右键删点；按过滤结果显示图层", true))
        {
            DrawOfflineMapCanvas(offlineMap, document, builtInGraph, snapshot, selectedCurrentMap);

            if (plugin.AreaMapProjectionService.TryGetSnapshot(out var mapSnapshot))
            {
                DrawHint(mapSnapshot.HasReliableLocalPlayerAnchor
                    ? $"区域地图锚点已锁定：{mapSnapshot.Source}"
                    : "区域地图已读取，但本地玩家锚点不稳定，点击标注暂不可用。");
            }
            else
            {
                DrawHint("尚未读取到区域地图。请打开游戏内区域地图，插件会把标注直接叠在那张地图上。");
            }
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection($"本图手动标注（{document.Points.Count}）", "查看和删除当前地图的手动草稿点", false))
        {
            DrawMapAnnotationTable(document, territoryType, mapId);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection($"路径耗时（内置+手动 {combinedPoints.Length} 点）", "按路径编号计算长度、风险和骑乘/步行预计用时", false))
        {
            DrawRouteSummaryTable(plugin.MapAnnotationService.BuildRouteSummaries(combinedPoints, selectedCurrentMap ? snapshot.LocalPlayer?.Position : null));
            EndCollapsibleSection();
        }
    }

    private void EnsureOfflineMapSelection(BattlefieldSnapshot snapshot)
    {
        if (offlineMapIndex >= 0 && offlineMapIndex < OfflineFrontlineMaps.Length)
            return;

        offlineMapIndex = Array.FindIndex(OfflineFrontlineMaps, entry => entry.TerritoryType == snapshot.TerritoryType);
        if (offlineMapIndex < 0)
            offlineMapIndex = 0;
    }

    private void DrawOfflineMapSelector(BattlefieldSnapshot snapshot, OfflineMapMetadata current)
    {
        ImGui.SetNextItemWidth(320f);
        if (ImGui.BeginCombo("离线地图", current.DisplayName))
        {
            for (var i = 0; i < OfflineFrontlineMaps.Length; i++)
            {
                var entry = OfflineFrontlineMaps[i];
                var selected = i == offlineMapIndex;
                if (ImGui.Selectable($"{entry.DisplayName}##offlineMap{i}", selected))
                {
                    offlineMapIndex = i;
                    annotationClearArmed = false;
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("切到当前地图", new Vector2(110f, 24f)))
        {
            var currentIndex = Array.FindIndex(OfflineFrontlineMaps, entry => entry.TerritoryType == snapshot.TerritoryType);
            if (currentIndex >= 0)
            {
                offlineMapIndex = currentIndex;
                annotationClearArmed = false;
            }
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130f);
        ImGui.SliderFloat("画布缩放", ref offlineMapCanvasZoom, 0.5f, 8f, "%.2fx");
        ImGui.SameLine();
        if (ImGui.Button("重置视图", new Vector2(90f, 24f)))
        {
            offlineMapCanvasZoom = 1f;
            offlineMapTextureCenter = new Vector2(1024f, 1024f);
        }
    }

    private void DrawMapGraphVersionManager(uint territoryType, uint mapId)
    {
        if (ImGui.Button("备份当前图谱", new Vector2(120f, 26f)))
        {
            var result = plugin.MapTacticalGraphService.BackupCurrentCustomGraph(territoryType, mapId);
            mapGraphVersionStatus = result.Message;
        }

        ImGui.SameLine();
        if (ImGui.Button("按当前地图数据迁移", new Vector2(160f, 26f)))
        {
            var result = plugin.MapTacticalGraphService.MigrateCustomGraphToCurrentMapVersion(territoryType, mapId);
            mapGraphVersionStatus = result.Message;
        }

        ImGui.SameLine();
        DrawHint("迁移会按旧地图比例/偏移保留贴图位置，适合游戏地图数据变更后修正整张图。");

        if (!string.IsNullOrWhiteSpace(mapGraphVersionStatus))
            DrawHint(mapGraphVersionStatus);

        var versions = plugin.MapTacticalGraphService.ListCustomGraphVersions(territoryType, mapId);
        if (versions.Length == 0)
        {
            DrawHint("当前地图还没有已存自定义图谱。保存一次后会自动生成版本快照。");
            return;
        }

        if (!ImGui.BeginTable("##MapGraphVersionTable", 7, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("版本", ImGuiTableColumnFlags.WidthFixed, 142f);
        ImGui.TableSetupColumn("时间", ImGuiTableColumnFlags.WidthFixed, 132f);
        ImGui.TableSetupColumn("点", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn("区", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn("路", ImGuiTableColumnFlags.WidthFixed, 42f);
        ImGui.TableSetupColumn("地图数据");
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 142f);
        ImGui.TableHeadersRow();

        foreach (var version in versions.Take(12))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(version.IsCurrent ? "当前图谱" : version.DisplayName);
            ImGui.TableNextColumn();
            ImGui.Text(FormatUnixMs(version.UpdatedAtUnixMs));
            ImGui.TableNextColumn();
            ImGui.Text(version.PointCount.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(version.RegionCount.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(version.PathCount.ToString());
            ImGui.TableNextColumn();
            ImGui.Text($"地图 {version.SourceMapId}  比例 {version.SourceMapSizeScale:0.000}  偏移 {version.SourceMapOffsetX}/{version.SourceMapOffsetY}");
            ImGui.TableNextColumn();

            if (version.IsCurrent)
            {
                ImGui.Text("当前");
                continue;
            }

            if (ImGui.Button($"对比##cmp{version.VersionId}", new Vector2(58f, 22f)))
            {
                var result = plugin.MapTacticalGraphService.CompareCurrentWithVersion(territoryType, mapId, version.VersionId);
                mapGraphVersionStatus = result.Message;
            }

            ImGui.SameLine();
            if (ImGui.Button($"回滚##rb{version.VersionId}", new Vector2(58f, 22f)))
            {
                var result = plugin.MapTacticalGraphService.RollbackCustomGraphToVersion(territoryType, mapId, version.VersionId);
                mapGraphVersionStatus = result.Message;
            }
        }

        ImGui.EndTable();
    }

    private void DrawMapCalibrationTools(
        BattlefieldSnapshot snapshot,
        OfflineMapMetadata map,
        MapAnnotationDocument document,
        IReadOnlyList<MapAnnotationPoint> combinedPoints,
        bool selectedCurrentMap)
    {
        showObservedTacticalTracks = DrawInlineToggle("叠加实战轨迹", showObservedTacticalTracks);
        ImGui.SameLine();
        mapCalibrationClickMode = DrawInlineToggle("点击校准采样", mapCalibrationClickMode);
        ImGui.SameLine();
        applyCorrectionToDraft = DrawInlineToggle("修正草稿", applyCorrectionToDraft);
        ImGui.SameLine();
        applyCorrectionToGraph = DrawInlineToggle("修正已存图谱", applyCorrectionToGraph);

        DrawHint(mapCalibrationClickMode
            ? "校准采样开启时，在当前地图实地站到一个可确认位置，再左键点击离线底图对应位置；插件会记录“点击坐标 -> 实际坐标”的误差。"
            : "关闭校准采样后，左键继续按标注工具落点。");

        var samples = mapCalibrationSamples
            .Where(sample => sample.TerritoryType == map.TerritoryType && sample.MapId == map.MapId)
            .ToArray();
        var estimate = EstimateCalibrationCorrection(samples);
        if (estimate.SampleCount > 0)
        {
            ImGui.Text($"点击误差：锚点 {estimate.SampleCount}  平均 {estimate.AverageError:0.0}y  最大 {estimate.MaxError:0.0}y  推荐缩放 {estimate.Correction.Scale:0.0000}  旋转 {RadiansToDegrees(estimate.Correction.RotationRadians):+0.00;-0.00;0}°  偏移 X {estimate.Correction.OffsetX:+0.00;-0.00;0} / Z {estimate.Correction.OffsetZ:+0.00;-0.00;0}");
            if (ImGui.Button("填入推荐修正", new Vector2(130f, 26f)))
            {
                mapCorrectionScale = estimate.Correction.Scale;
                mapCorrectionRotationDegrees = RadiansToDegrees(estimate.Correction.RotationRadians);
                mapCorrectionOffsetX = estimate.Correction.OffsetX;
                mapCorrectionOffsetY = estimate.Correction.OffsetY;
                mapCorrectionOffsetZ = estimate.Correction.OffsetZ;
            }

            ImGui.SameLine();
            if (ImGui.Button("清空本图样本", new Vector2(120f, 26f)))
                mapCalibrationSamples.RemoveAll(sample => sample.TerritoryType == map.TerritoryType && sample.MapId == map.MapId);
        }
        else
        {
            DrawHint(selectedCurrentMap && snapshot.LocalPlayer.HasValue
                ? "暂无点击误差样本。开启“点击校准采样”后在画布上点击当前位置对应的地图点。"
                : "校准采样需要离线地图与当前实战地图一致，并且能读取本地玩家位置。");
        }

        DrawPathLengthCalibration(combinedPoints, snapshot.MapTactics, selectedCurrentMap);
        DrawCalibrationSampleTable(samples);

        ImGui.SetNextItemWidth(140f);
        ImGui.InputFloat("修正缩放", ref mapCorrectionScale, 0.001f, 0.01f, "%.5f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110f);
        ImGui.InputFloat("旋转角度", ref mapCorrectionRotationDegrees, 0.05f, 0.5f, "%.2f°");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputFloat("X 偏移", ref mapCorrectionOffsetX, 0.5f, 5f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120f);
        ImGui.InputFloat("Z 偏移", ref mapCorrectionOffsetZ, 0.5f, 5f, "%.2f");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100f);
        ImGui.InputFloat("Y 偏移", ref mapCorrectionOffsetY, 0.5f, 5f, "%.2f");

        mapCorrectionScale = Math.Clamp(mapCorrectionScale, 0.25f, 4f);
        mapCorrectionRotationDegrees = Math.Clamp(mapCorrectionRotationDegrees, -45f, 45f);
        var correction = new MapCoordinateCorrection(mapCorrectionScale, DegreesToRadians(mapCorrectionRotationDegrees), mapCorrectionOffsetX, mapCorrectionOffsetY, mapCorrectionOffsetZ);
        if (ImGui.Button("执行批量修正", new Vector2(130f, 26f)))
            ApplyMapCoordinateCorrection(map.TerritoryType, map.MapId, correction);

        ImGui.SameLine();
        if (ImGui.Button("重置修正值", new Vector2(110f, 26f)))
        {
            mapCorrectionScale = 1f;
            mapCorrectionRotationDegrees = 0f;
            mapCorrectionOffsetX = 0f;
            mapCorrectionOffsetY = 0f;
            mapCorrectionOffsetZ = 0f;
        }

        ImGui.SameLine();
        DrawHint(document.Points.Count == 0
            ? "手动草稿为空；如要修正已存图谱，请勾选“修正已存图谱”。"
            : $"手动草稿 {document.Points.Count} 点。");

        if (!string.IsNullOrWhiteSpace(mapCalibrationStatus))
            DrawHint(mapCalibrationStatus);
    }

    private void DrawPathLengthCalibration(
        IReadOnlyList<MapAnnotationPoint> combinedPoints,
        BattlefieldMapTacticsSnapshot tactics,
        bool selectedCurrentMap)
    {
        if (!selectedCurrentMap)
        {
            DrawHint("路径长度误差需要离线选择与当前实战地图一致。");
            return;
        }

        var routeGroups = combinedPoints
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => (RouteId: group.Key, Points: group.OrderBy(point => point.CreatedAtUnixMs).Select(point => point.Position).ToArray()))
            .Where(route => route.Points.Length >= 2)
            .ToArray();

        var observed = new[] { tactics.FriendlyObservedPath, tactics.EnemyObservedPath }
            .Select(path => (Path: path, Points: ObservedPathPoints(path)))
            .Where(path => path.Points.Length >= 2)
            .ToArray();
        if (routeGroups.Length == 0 || observed.Length == 0)
        {
            DrawHint("路径长度误差：需要至少一条图谱路径，以及实战主团轨迹累计到 2 个以上点。");
            return;
        }

        if (!ImGui.BeginTable("##PathLengthCalibrationTable", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("轨迹", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableSetupColumn("匹配路径");
        ImGui.TableSetupColumn("实战长度", ImGuiTableColumnFlags.WidthFixed, 82f);
        ImGui.TableSetupColumn("图谱长度", ImGuiTableColumnFlags.WidthFixed, 82f);
        ImGui.TableSetupColumn("误差", ImGuiTableColumnFlags.WidthFixed, 88f);
        ImGui.TableHeadersRow();

        foreach (var path in observed)
        {
            var points = path.Points;
            var observedDistance = PathDistance(points);
            var first = points[0];
            var last = points[^1];
            var best = routeGroups
                .Select(route =>
                {
                    var routeDistance = PathDistance(route.Points);
                    var endpointError = MathF.Min(
                        Distance2D(route.Points[0], first) + Distance2D(route.Points[^1], last),
                        Distance2D(route.Points[0], last) + Distance2D(route.Points[^1], first));
                    return (route.RouteId, RouteDistance: routeDistance, EndpointError: endpointError);
                })
                .OrderBy(item => item.EndpointError)
                .FirstOrDefault();

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(path.Path.Side == BattlefieldTacticalSide.Friendly ? "我方" : "敌方");
            ImGui.TableNextColumn();
            ImGui.Text(string.IsNullOrWhiteSpace(best.RouteId) ? "未匹配" : $"{best.RouteId}  端点误差:{best.EndpointError:0}y");
            ImGui.TableNextColumn();
            ImGui.Text($"{observedDistance:0}y");
            ImGui.TableNextColumn();
            ImGui.Text($"{best.RouteDistance:0}y");
            ImGui.TableNextColumn();
            ImGui.TextColored(RiskColor(MathF.Abs(best.RouteDistance - observedDistance)), $"{best.RouteDistance - observedDistance:+0;-0;0}y");
        }

        ImGui.EndTable();
    }

    private static void DrawCalibrationSampleTable(IReadOnlyList<MapCalibrationSample> samples)
    {
        if (samples.Count == 0)
            return;

        if (!ImGui.BeginTable("##MapCalibrationSampleTable", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("时间", ImGuiTableColumnFlags.WidthFixed, 94f);
        ImGui.TableSetupColumn("点击坐标");
        ImGui.TableSetupColumn("实际坐标");
        ImGui.TableSetupColumn("误差", ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableHeadersRow();

        foreach (var sample in samples.OrderByDescending(sample => sample.CreatedAtUnixMs).Take(6))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(FormatUnixMs(sample.CreatedAtUnixMs));
            ImGui.TableNextColumn();
            ImGui.Text(FormatPosition(sample.ClickedPosition));
            ImGui.TableNextColumn();
            ImGui.Text(FormatPosition(sample.ActualPosition));
            ImGui.TableNextColumn();
            ImGui.Text($"{sample.Error:0.0}y");
        }

        ImGui.EndTable();
    }

    private void ApplyMapCoordinateCorrection(uint territoryType, uint mapId, MapCoordinateCorrection correction)
    {
        var messages = new List<string>();
        messages.Add($"校正参数：缩放 {correction.Scale:0.0000}，旋转 {RadiansToDegrees(correction.RotationRadians):+0.00;-0.00;0}°，偏移 X {correction.OffsetX:+0.00;-0.00;0} / Z {correction.OffsetZ:+0.00;-0.00;0}");
        if (!applyCorrectionToDraft && !applyCorrectionToGraph)
        {
            mapCalibrationStatus = "没有选择修正目标。请勾选“修正草稿”或“修正已存图谱”。";
            return;
        }

        if (applyCorrectionToDraft)
        {
            var count = plugin.MapAnnotationService.ApplyCoordinateCorrection(territoryType, mapId, correction);
            messages.Add(count > 0 ? $"手动草稿已修正 {count} 点" : "手动草稿为空");
        }

        if (applyCorrectionToGraph)
        {
            var result = plugin.MapTacticalGraphService.ApplyCoordinateCorrectionToCustomGraph(territoryType, mapId, correction);
            messages.Add(result.Message);
            mapGraphVersionStatus = result.Message;
        }

        mapCalibrationStatus = string.Join("；", messages);
    }

    private static MapCalibrationCorrectionEstimate EstimateCalibrationCorrection(IReadOnlyList<MapCalibrationSample> samples)
    {
        if (samples.Count == 0)
            return new MapCalibrationCorrectionEstimate(0, 0f, 0f, new MapCoordinateCorrection(1f, 0f, 0f, 0f, 0f));

        var averageError = samples.Select(sample => sample.Error).Average();
        var maxError = samples.Select(sample => sample.Error).Max();
        var clickedCenter = AveragePosition(samples.Select(sample => sample.ClickedPosition));
        var actualCenter = AveragePosition(samples.Select(sample => sample.ActualPosition));
        var scale = 1f;
        var rotation = 0f;
        if (samples.Count >= 2)
        {
            var dot = 0f;
            var cross = 0f;
            var denominator = 0f;
            foreach (var sample in samples)
            {
                var sx = sample.ClickedPosition.X - clickedCenter.X;
                var sz = sample.ClickedPosition.Z - clickedCenter.Z;
                var ax = sample.ActualPosition.X - actualCenter.X;
                var az = sample.ActualPosition.Z - actualCenter.Z;
                dot += sx * ax + sz * az;
                cross += sx * az - sz * ax;
                denominator += sx * sx + sz * sz;
            }

            if (denominator > 0.001f)
            {
                scale = Math.Clamp(MathF.Sqrt(dot * dot + cross * cross) / denominator, 0.25f, 4f);
                rotation = Math.Clamp(MathF.Atan2(cross, dot), -MathF.PI / 4f, MathF.PI / 4f);
            }
        }

        var rotatedClickedCenter = Rotate2D(clickedCenter, rotation);
        var offsetX = actualCenter.X - rotatedClickedCenter.X * scale;
        var offsetY = samples.Select(sample => sample.ActualPosition.Y - sample.ClickedPosition.Y).Average();
        var offsetZ = actualCenter.Z - rotatedClickedCenter.Z * scale;
        return new MapCalibrationCorrectionEstimate(
            samples.Count,
            averageError,
            maxError,
            new MapCoordinateCorrection(scale, rotation, offsetX, offsetY, offsetZ));
    }

    private static Vector3 AveragePosition(IEnumerable<Vector3> positions)
    {
        var sum = Vector3.Zero;
        var count = 0;
        foreach (var position in positions)
        {
            sum += position;
            count++;
        }

        return count == 0 ? Vector3.Zero : sum / count;
    }

    private static Vector3 Rotate2D(Vector3 position, float radians)
    {
        var sin = MathF.Sin(radians);
        var cos = MathF.Cos(radians);
        return new Vector3(
            position.X * cos - position.Z * sin,
            position.Y,
            position.X * sin + position.Z * cos);
    }

    private static float DegreesToRadians(float degrees)
        => degrees * MathF.PI / 180f;

    private static float RadiansToDegrees(float radians)
        => radians * 180f / MathF.PI;

    private static float PathDistance(IReadOnlyList<Vector3> points)
    {
        var distance = 0f;
        for (var i = 1; i < points.Count; i++)
            distance += Distance2D(points[i - 1], points[i]);
        return distance;
    }

    private static Vector3[] ObservedPathPoints(BattlefieldMapGroupPathSnapshot path)
        => path.Points ?? Array.Empty<Vector3>();

    private OfflineMapMetadata ResolveOfflineMapMetadata(OfflineFrontlineMapEntry entry)
    {
        var mapId = entry.FallbackMapId;
        try
        {
            var territorySheet = plugin.DataManager.GetExcelSheet<TerritoryTypeSheet>();
            if (territorySheet.TryGetRow(entry.TerritoryType, out var territory) && territory.Map.RowId != 0)
                mapId = territory.Map.RowId;
        }
        catch
        {
            mapId = entry.FallbackMapId;
        }

        try
        {
            var mapSheet = plugin.DataManager.GetExcelSheet<MapSheet>();
            if (mapSheet.TryGetRow(mapId, out var map))
            {
                var mapKey = map.Id.ToString();
                return new OfflineMapMetadata(
                    entry.MapType,
                    entry.TerritoryType,
                    map.RowId,
                    entry.DisplayName,
                    BuildOfflineMapTexturePath(mapKey),
                    map.SizeFactor / 100f,
                    map.OffsetX,
                    map.OffsetY,
                    true);
            }
        }
        catch
        {
            // The grid fallback is still useful for editing annotations if game map data is unavailable.
        }

        return new OfflineMapMetadata(
            entry.MapType,
            entry.TerritoryType,
            mapId,
            entry.DisplayName,
            string.Empty,
            1f,
            0,
            0,
            false);
    }

    private void DrawOfflineMapCanvas(
        OfflineMapMetadata map,
        MapAnnotationDocument document,
        MapTacticalGraphSnapshot? builtInGraph,
        BattlefieldSnapshot snapshot,
        bool selectedCurrentMap)
    {
        DrawHint(map.HasGameMapData
            ? $"底图：{map.TexturePath}  滚轮缩放，中键拖动；Shift+左键拖动也可平移；左键落点，右键删点。"
            : "未读取到游戏底图，暂用坐标网格。");

        var availableWidth = MathF.Max(360f, ImGui.GetContentRegionAvail().X);
        var childHeight = Math.Clamp(availableWidth * 0.72f, 430f, 650f);
        if (!ImGui.BeginChild("##OfflineMapCanvasHost", new Vector2(0f, childHeight), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.EndChild();
            return;
        }

        offlineMapCanvasZoom = Math.Clamp(offlineMapCanvasZoom, 0.5f, 8f);
        offlineMapTextureCenter = ClampOfflineMapTextureCenter(offlineMapTextureCenter);

        var viewportMin = ImGui.GetCursorScreenPos();
        var viewportSize = ImGui.GetContentRegionAvail();
        viewportSize = new Vector2(MathF.Max(320f, viewportSize.X), MathF.Max(320f, viewportSize.Y));
        var viewportMax = viewportMin + viewportSize;
        var viewportCenter = viewportMin + viewportSize * 0.5f;
        var pixelsPerTexture = CalculateOfflinePixelsPerTexture(viewportSize, offlineMapCanvasZoom);
        var canvasSize = new Vector2(2048f * pixelsPerTexture);
        var canvasMin = viewportCenter - offlineMapTextureCenter * pixelsPerTexture;
        var canvasMax = canvasMin + canvasSize;
        var drawList = ImGui.GetWindowDrawList();

        drawList.AddRectFilled(viewportMin, viewportMax, Color(new Vector4(0.045f, 0.052f, 0.065f, 1f)), 4f);
        drawList.PushClipRect(viewportMin, viewportMax, true);
        if (!TryDrawOfflineMapTexture(drawList, map.TexturePath, canvasMin, canvasMax))
            DrawOfflineMapGrid(drawList, canvasMin, canvasSize);

        if (builtInGraph != null && showBuiltInTacticalGraph)
        {
            DrawOfflineMapTacticalGraph(drawList, builtInGraph, map, canvasMin, canvasSize, showGraphRegions, showGraphPaths, mapAnnotationKindVisibleMask);
            if (showGraphNodes)
            {
                var builtInDocument = BuildDisplayAnnotationDocument(map.TerritoryType, map.MapId, map.DisplayName, builtInGraph.Points);
                DrawOfflineMapAnnotationPoints(drawList, builtInDocument, map, canvasMin, canvasSize, mapAnnotationKindVisibleMask);
            }
        }

        if (showManualMapAnnotations)
        {
            DrawOfflineMapAnnotationRoutes(drawList, document, map, canvasMin, canvasSize, mapAnnotationKindVisibleMask);
            DrawOfflineMapAnnotationPoints(drawList, document, map, canvasMin, canvasSize, mapAnnotationKindVisibleMask);
        }

        if (showObservedTacticalTracks && selectedCurrentMap)
            DrawOfflineObservedTacticalTracks(drawList, snapshot.MapTactics, map, canvasMin, canvasSize);
        drawList.PopClipRect();
        drawList.AddRect(viewportMin, viewportMax, Color(new Vector4(1f, 1f, 1f, 0.28f)), 4f, ImDrawFlags.None, 1.5f);

        ImGui.InvisibleButton($"##OfflineMapCanvas{map.TerritoryType}_{map.MapId}", viewportSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
        if (ImGui.IsItemHovered())
        {
            var io = ImGui.GetIO();
            var mouse = io.MousePos;
            if (MathF.Abs(io.MouseWheel) > 0.001f && TryScreenToOfflineTexture(canvasMin, canvasSize, mouse, out var zoomAnchorTexture))
            {
                var nextZoom = Math.Clamp(offlineMapCanvasZoom * MathF.Pow(1.18f, io.MouseWheel), 0.5f, 8f);
                if (MathF.Abs(nextZoom - offlineMapCanvasZoom) > 0.001f)
                {
                    var nextPixelsPerTexture = CalculateOfflinePixelsPerTexture(viewportSize, nextZoom);
                    offlineMapCanvasZoom = nextZoom;
                    offlineMapTextureCenter = ClampOfflineMapTextureCenter(zoomAnchorTexture - (mouse - viewportCenter) / MathF.Max(0.0001f, nextPixelsPerTexture));
                    pixelsPerTexture = nextPixelsPerTexture;
                    canvasSize = new Vector2(2048f * pixelsPerTexture);
                    canvasMin = viewportCenter - offlineMapTextureCenter * pixelsPerTexture;
                }
            }

            if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
            {
                PanOfflineMap(ImGuiMouseButton.Middle, pixelsPerTexture);
                canvasMin = viewportCenter - offlineMapTextureCenter * pixelsPerTexture;
            }
            else if (io.KeyShift && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                PanOfflineMap(ImGuiMouseButton.Left, pixelsPerTexture);
                canvasMin = viewportCenter - offlineMapTextureCenter * pixelsPerTexture;
            }

            if (TryCanvasToOfflineWorld(map, canvasMin, canvasSize, mouse, out var worldPosition, out var texturePosition))
            {
                drawList.PushClipRect(viewportMin, viewportMax, true);
                drawList.AddCircle(mouse, 8f, Color(new Vector4(1f, 1f, 1f, 0.92f)), 24, 2f);
                drawList.AddText(mouse + new Vector2(10f, 8f), Color(new Vector4(1f, 1f, 1f, 0.95f)), $"{AnnotationKindText(selectedAnnotationKind)}  X:{worldPosition.X:0.0} Z:{worldPosition.Z:0.0}  {offlineMapCanvasZoom:0.00}x");
                drawList.PopClipRect();

                if (mapCalibrationClickMode && selectedCurrentMap && snapshot.LocalPlayer.HasValue && !io.KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    AddMapCalibrationSample(map.TerritoryType, map.MapId, worldPosition, snapshot.LocalPlayer.Value.Position);
                else if (annotationClickMode && !io.KeyShift && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                    AddMapAnnotation(worldPosition, map.TerritoryType, map.MapId, map.DisplayName);

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right) && TryFindOfflineAnnotationAt(document, map, canvasMin, canvasSize, mouse, out var pointId))
                    plugin.MapAnnotationService.DeletePoint(map.TerritoryType, map.MapId, pointId);
            }
        }

        ImGui.EndChild();
    }

    private void AddMapCalibrationSample(uint territoryType, uint mapId, Vector3 clickedPosition, Vector3 actualPosition)
    {
        mapCalibrationSamples.Add(new MapCalibrationSample(
            territoryType,
            mapId,
            clickedPosition,
            actualPosition,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        mapCalibrationStatus = $"已记录校准样本：点击 {FormatPosition(clickedPosition)} -> 实际 {FormatPosition(actualPosition)}，误差 {Distance2D(clickedPosition, actualPosition):0.0}y。";
    }

    private static void DrawOfflineObservedTacticalTracks(
        ImDrawListPtr drawList,
        BattlefieldMapTacticsSnapshot tactics,
        OfflineMapMetadata map,
        Vector2 canvasMin,
        Vector2 canvasSize)
    {
        DrawOfflineObservedPath(drawList, tactics.FriendlyObservedPath, map, canvasMin, canvasSize, new Vector4(0.38f, 0.90f, 0.58f, 0.82f), "我方轨迹");
        DrawOfflineObservedPath(drawList, tactics.EnemyObservedPath, map, canvasMin, canvasSize, new Vector4(1f, 0.32f, 0.24f, 0.82f), "敌方轨迹");
    }

    private static void DrawOfflineObservedPath(
        ImDrawListPtr drawList,
        BattlefieldMapGroupPathSnapshot path,
        OfflineMapMetadata map,
        Vector2 canvasMin,
        Vector2 canvasSize,
        Vector4 color,
        string label)
    {
        var points = ObservedPathPoints(path);
        if (points.Length < 2)
            return;

        var lineColor = Color(color);
        Vector2? lastScreen = null;
        for (var i = 0; i < points.Length; i++)
        {
            if (!TryOfflineWorldToCanvas(map, points[i], canvasMin, canvasSize, out var screen))
            {
                lastScreen = null;
                continue;
            }

            if (lastScreen.HasValue)
                drawList.AddLine(lastScreen.Value, screen, lineColor, 2.8f);

            if (i == points.Length - 1)
            {
                drawList.AddCircleFilled(screen, 4.5f, lineColor, 18);
                drawList.AddText(screen + new Vector2(8f, -9f), Color(new Vector4(1f, 1f, 1f, 0.82f)), label);
            }

            lastScreen = screen;
        }
    }

    private void PanOfflineMap(ImGuiMouseButton button, float pixelsPerTexture)
    {
        var delta = ImGui.GetMouseDragDelta(button);
        if (delta.X * delta.X + delta.Y * delta.Y <= 0.01f)
            return;

        offlineMapTextureCenter = ClampOfflineMapTextureCenter(offlineMapTextureCenter - delta / MathF.Max(0.0001f, pixelsPerTexture));
        ImGui.ResetMouseDragDelta(button);
    }

    private static float CalculateOfflinePixelsPerTexture(Vector2 viewportSize, float zoom)
        => MathF.Max(0.0001f, MathF.Min(viewportSize.X, viewportSize.Y) / 2048f * Math.Clamp(zoom, 0.5f, 8f));

    private static Vector2 ClampOfflineMapTextureCenter(Vector2 center)
        => new(Math.Clamp(center.X, 0f, 2048f), Math.Clamp(center.Y, 0f, 2048f));

    private bool TryDrawOfflineMapTexture(ImDrawListPtr drawList, string texturePath, Vector2 canvasMin, Vector2 canvasMax)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
            return false;

        try
        {
            var texture = plugin.TextureProvider.GetFromGame(texturePath);
            var wrap = texture.GetWrapOrEmpty();
            if (wrap.Handle == IntPtr.Zero)
                return false;

            drawList.AddImage(wrap.Handle, canvasMin, canvasMax, Vector2.Zero, Vector2.One, Color(new Vector4(1f, 1f, 1f, 1f)));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void DrawOfflineMapGrid(ImDrawListPtr drawList, Vector2 canvasMin, Vector2 canvasSize)
    {
        var canvasMax = canvasMin + canvasSize;
        var gridColor = Color(new Vector4(1f, 1f, 1f, 0.12f));
        for (var i = 0; i <= 8; i++)
        {
            var t = i / 8f;
            var x = MathF.Round(canvasMin.X + canvasSize.X * t);
            var y = MathF.Round(canvasMin.Y + canvasSize.Y * t);
            drawList.AddLine(new Vector2(x, canvasMin.Y), new Vector2(x, canvasMax.Y), gridColor, 1f);
            drawList.AddLine(new Vector2(canvasMin.X, y), new Vector2(canvasMax.X, y), gridColor, 1f);
        }

        drawList.AddRect(canvasMin, canvasMax, Color(new Vector4(1f, 1f, 1f, 0.28f)), 4f, ImDrawFlags.None, 1.5f);
    }

    private static void DrawOfflineMapTacticalGraph(
        ImDrawListPtr drawList,
        MapTacticalGraphSnapshot graph,
        OfflineMapMetadata map,
        Vector2 canvasMin,
        Vector2 canvasSize,
        bool showRegions,
        bool showPaths,
        int visibleKindMask)
    {
        if (showRegions)
        {
            foreach (var region in graph.Regions)
            {
                if (!IsAnnotationKindVisible(region.Kind, visibleKindMask))
                    continue;

                var vertices = new List<Vector2>(region.Vertices.Length);
                foreach (var vertex in region.Vertices)
                {
                    if (TryOfflineWorldToCanvas(map, vertex, canvasMin, canvasSize, out var screenPosition))
                        vertices.Add(screenPosition);
                }

                if (vertices.Count < 3)
                    continue;

                DrawTacticalRegionPolygon(drawList, region.Kind, region.Label, region.RiskScore, vertices, 0.10f, 0.46f);
            }
        }

        if (!showPaths)
            return;

        foreach (var path in graph.Paths)
        {
            if (!IsAnnotationKindVisible(path.Kind, visibleKindMask))
                continue;

            var thickness = Math.Clamp(path.Width * map.MapSizeScale / 2048f * canvasSize.X, 2.2f, 16f);
            for (var i = 1; i < path.Points.Length; i++)
            {
                if (!TryOfflineWorldToCanvas(map, path.Points[i - 1], canvasMin, canvasSize, out var from)
                    || !TryOfflineWorldToCanvas(map, path.Points[i], canvasMin, canvasSize, out var to))
                {
                    continue;
                }

                var color = TacticalShapeColor(path.Kind, path.RiskScore, 0.34f);
                drawList.AddLine(from, to, color, thickness);
                if (path.IsOneWay)
                    DrawDirectionArrow(drawList, from, to, color, thickness);
            }
        }
    }

    private static void DrawOfflineMapAnnotationRoutes(
        ImDrawListPtr drawList,
        MapAnnotationDocument document,
        OfflineMapMetadata map,
        Vector2 canvasMin,
        Vector2 canvasSize,
        int visibleKindMask)
    {
        var routeGroups = document.Points
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in routeGroups)
        {
            var points = group.OrderBy(point => point.CreatedAtUnixMs).ToArray();
            for (var i = 1; i < points.Length; i++)
            {
                if (!IsAnnotationKindVisible(points[i - 1].Kind, visibleKindMask) && !IsAnnotationKindVisible(points[i].Kind, visibleKindMask))
                    continue;

                if (!TryOfflineWorldToCanvas(map, points[i - 1].Position, canvasMin, canvasSize, out var from)
                    || !TryOfflineWorldToCanvas(map, points[i].Position, canvasMin, canvasSize, out var to))
                {
                    continue;
                }

                drawList.AddLine(from, to, AnnotationRouteColor(points[i - 1], points[i]), 2.4f);
            }
        }
    }

    private static void DrawOfflineMapAnnotationPoints(
        ImDrawListPtr drawList,
        MapAnnotationDocument document,
        OfflineMapMetadata map,
        Vector2 canvasMin,
        Vector2 canvasSize,
        int visibleKindMask)
    {
        foreach (var point in document.Points)
        {
            if (!IsAnnotationKindVisible(point.Kind, visibleKindMask))
                continue;

            if (!TryOfflineWorldToCanvas(map, point.Position, canvasMin, canvasSize, out var screenPosition))
                continue;

            var builtIn = MapTacticalGraphService.IsBuiltInPoint(point) || MapTacticalGraphService.IsCustomPoint(point);
            var color = AnnotationKindColor(point.Kind);
            color.W = builtIn ? 0.68f : color.W;
            var colorU32 = Color(color);
            var areaFillAlpha = builtIn ? 0.08f : 0.14f;
            var areaOutlineAlpha = builtIn ? 0.48f : 0.72f;
            var labelAlpha = builtIn ? 0.72f : 0.95f;
            var radius = IsAreaAnnotationKind(point.Kind)
                ? Math.Clamp(point.Radius * map.MapSizeScale / 2048f * canvasSize.X, 8f, 72f)
                : 8f;

            if (IsAreaAnnotationKind(point.Kind))
            {
                drawList.AddCircleFilled(screenPosition, radius, Color(new Vector4(color.X, color.Y, color.Z, areaFillAlpha)), 32);
                drawList.AddCircle(screenPosition, radius, Color(new Vector4(color.X, color.Y, color.Z, areaOutlineAlpha)), 32, 2f);
            }

            drawList.AddCircleFilled(screenPosition, builtIn ? 4.5f : 5.5f, colorU32, 20);
            drawList.AddCircle(screenPosition, builtIn ? 6.5f : 7.5f, Color(new Vector4(0f, 0f, 0f, builtIn ? 0.58f : 0.82f)), 20, 2f);

            var label = string.IsNullOrWhiteSpace(point.Label) ? AnnotationKindText(point.Kind) : point.Label;
            if (!string.IsNullOrWhiteSpace(point.RouteId))
                label = $"{label}/{point.RouteId}";
            if (ShouldDrawAnnotationPointLabel(point, builtIn))
                drawList.AddText(screenPosition + new Vector2(9f, -8f), Color(new Vector4(1f, 1f, 1f, labelAlpha)), label);
        }
    }

    private static bool TryFindOfflineAnnotationAt(
        MapAnnotationDocument document,
        OfflineMapMetadata map,
        Vector2 canvasMin,
        Vector2 canvasSize,
        Vector2 mouse,
        out string pointId)
    {
        pointId = string.Empty;
        var bestDistanceSquared = 14f * 14f;
        foreach (var point in document.Points)
        {
            if (!TryOfflineWorldToCanvas(map, point.Position, canvasMin, canvasSize, out var screenPosition))
                continue;

            var delta = screenPosition - mouse;
            var distanceSquared = delta.X * delta.X + delta.Y * delta.Y;
            if (distanceSquared > bestDistanceSquared)
                continue;

            bestDistanceSquared = distanceSquared;
            pointId = point.Id;
        }

        return !string.IsNullOrWhiteSpace(pointId);
    }

    private static bool TryCanvasToOfflineWorld(
        OfflineMapMetadata map,
        Vector2 canvasMin,
        Vector2 canvasSize,
        Vector2 screenPosition,
        out Vector3 worldPosition,
        out Vector2 texturePosition)
    {
        if (!TryScreenToOfflineTexture(canvasMin, canvasSize, screenPosition, out texturePosition))
        {
            worldPosition = default;
            return false;
        }

        var scale = MathF.Max(0.0001f, map.MapSizeScale);
        var worldX = (texturePosition.X - 1024f) / scale - map.MapOffsetX;
        var worldZ = (texturePosition.Y - 1024f) / scale - map.MapOffsetY;
        worldPosition = new Vector3(worldX, 0f, worldZ);
        return true;
    }

    private static bool TryScreenToOfflineTexture(
        Vector2 canvasMin,
        Vector2 canvasSize,
        Vector2 screenPosition,
        out Vector2 texturePosition)
    {
        texturePosition = (screenPosition - canvasMin) / MathF.Max(1f, canvasSize.X) * 2048f;
        return texturePosition.X >= 0f
            && texturePosition.Y >= 0f
            && texturePosition.X <= 2048f
            && texturePosition.Y <= 2048f;
    }

    private static bool TryOfflineWorldToCanvas(
        OfflineMapMetadata map,
        Vector3 worldPosition,
        Vector2 canvasMin,
        Vector2 canvasSize,
        out Vector2 screenPosition)
    {
        var texturePosition = OfflineWorldToTexture(map, worldPosition);
        screenPosition = canvasMin + texturePosition / 2048f * canvasSize;
        return texturePosition.X >= 0f
            && texturePosition.Y >= 0f
            && texturePosition.X <= 2048f
            && texturePosition.Y <= 2048f;
    }

    private static Vector2 OfflineWorldToTexture(OfflineMapMetadata map, Vector3 worldPosition)
        => new Vector2(worldPosition.X, worldPosition.Z) * map.MapSizeScale
            + new Vector2(map.MapOffsetX, map.MapOffsetY) * map.MapSizeScale
            + new Vector2(1024f);

    private void SaveCurrentMapAnnotationsAsCustomGraph(OfflineMapMetadata map, MapAnnotationDocument document)
    {
        var result = plugin.MapTacticalGraphService.SaveCustomGraphFromAnnotations(
            map.TerritoryType,
            map.MapId,
            map.DisplayName,
            document.Points);
        if (result.Success)
            plugin.MapAnnotationService.Clear(map.TerritoryType, map.MapId);
        mapGraphSaveStatus = result.Success
            ? $"{result.Message} 手动草稿已清空，文件：{result.Path}"
            : result.Message;
    }

    private static string BuildOfflineMapTexturePath(string mapKey)
        => string.IsNullOrWhiteSpace(mapKey)
            ? string.Empty
            : $"ui/map/{mapKey}/{mapKey.Replace("/", string.Empty)}_m.tex";

    private bool DrawInlineToggle(string label, bool value)
    {
        var current = value;
        ImGui.Checkbox(label, ref current);
        return current;
    }

    private void DrawAnnotationKindSelector()
    {
        var kinds = Enum.GetValues<MapAnnotationKind>();
        var columns = 4;
        if (!ImGui.BeginTable("##AnnotationKindSelector", columns, ImGuiTableFlags.SizingStretchSame))
            return;

        for (var i = 0; i < kinds.Length; i++)
        {
            var kind = kinds[i];
            ImGui.TableNextColumn();
            var selected = selectedAnnotationKind == kind;
            if (selected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, AnnotationKindColor(kind));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, AnnotationKindColor(kind));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, AnnotationKindColor(kind));
            }

            if (ImGui.Button($"{AnnotationKindText(kind)}##kind{kind}", new Vector2(-1f, 26f)))
                selectedAnnotationKind = kind;

            if (selected)
                ImGui.PopStyleColor(3);
        }

        ImGui.EndTable();
    }

    private void DrawMapGraphDisplayFilters()
    {
        showBuiltInTacticalGraph = DrawInlineToggle("显示已存图谱", showBuiltInTacticalGraph);
        ImGui.SameLine();
        showManualMapAnnotations = DrawInlineToggle("显示手动草稿", showManualMapAnnotations);
        ImGui.SameLine();
        showGraphRegions = DrawInlineToggle("区域", showGraphRegions);
        ImGui.SameLine();
        showGraphPaths = DrawInlineToggle("路径", showGraphPaths);
        ImGui.SameLine();
        showGraphNodes = DrawInlineToggle("节点", showGraphNodes);
        ImGui.SameLine();
        showDynamicMapHeat = DrawInlineToggle("实时危险", showDynamicMapHeat);
        ImGui.SameLine();
        showObservedTacticalTracks = DrawInlineToggle("实战轨迹", showObservedTacticalTracks);

        if (ImGui.BeginTable("##MapKindFilterTable", 6, ImGuiTableFlags.SizingStretchSame))
        {
            foreach (var kind in Enum.GetValues<MapAnnotationKind>())
            {
                ImGui.TableNextColumn();
                var visible = IsAnnotationKindVisible(kind, mapAnnotationKindVisibleMask);
                if (ImGui.Checkbox($"{AnnotationKindText(kind)}##filter{kind}", ref visible))
                    mapAnnotationKindVisibleMask = SetAnnotationKindVisible(mapAnnotationKindVisibleMask, kind, visible);
            }

            ImGui.EndTable();
        }

        if (ImGui.Button("全选显示", new Vector2(90f, 24f)))
            mapAnnotationKindVisibleMask = BuildAllAnnotationKindMask();
        ImGui.SameLine();
        if (ImGui.Button("只看区域", new Vector2(90f, 24f)))
            mapAnnotationKindVisibleMask = BuildAreaAnnotationKindMask();
        ImGui.SameLine();
        if (ImGui.Button("只看路径", new Vector2(90f, 24f)))
            mapAnnotationKindVisibleMask = BuildRouteAnnotationKindMask();
    }

    private void DrawMapAnnotationTable(MapAnnotationDocument document, uint territoryType, uint mapId)
    {
        if (document.Points.Count == 0)
        {
            DrawHint("当前地图还没有标注。在上方离线地图左键点击即可添加；右键点击已有点可快速删除。");
            return;
        }

        if (!ImGui.BeginTable("##MapAnnotationTable", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("类型", ImGuiTableColumnFlags.WidthFixed, 72f);
        ImGui.TableSetupColumn("名称");
        ImGui.TableSetupColumn("路径", ImGuiTableColumnFlags.WidthFixed, 80f);
        ImGui.TableSetupColumn("坐标", ImGuiTableColumnFlags.WidthFixed, 140f);
        ImGui.TableSetupColumn("操作", ImGuiTableColumnFlags.WidthFixed, 64f);
        ImGui.TableHeadersRow();

        foreach (var point in document.Points.OrderBy(point => point.Kind).ThenBy(point => point.RouteId).ThenBy(point => point.CreatedAtUnixMs).ToArray())
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(AnnotationKindColor(point.Kind), AnnotationKindText(point.Kind));
            ImGui.TableNextColumn();
            ImGui.Text(point.Label);
            ImGui.TableNextColumn();
            ImGui.Text(string.IsNullOrWhiteSpace(point.RouteId) ? "-" : point.RouteId);
            ImGui.TableNextColumn();
            ImGui.Text($"{point.X:0.0},{point.Y:0.0},{point.Z:0.0}");
            ImGui.TableNextColumn();
            if (ImGui.Button($"删除##{point.Id}", new Vector2(54f, 22f)))
                plugin.MapAnnotationService.DeletePoint(territoryType, mapId, point.Id);
        }

        ImGui.EndTable();
    }

    private static void DrawRouteSummaryTable(MapAnnotationRouteSummary[] routes)
    {
        if (routes.Length == 0)
        {
            DrawHint("给多个标注填写同一个路径编号后，这里会自动计算路径长度和预计用时。");
            return;
        }

        if (!ImGui.BeginTable("##RouteSummaryTable", 6, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("路径");
        ImGui.TableSetupColumn("点数", ImGuiTableColumnFlags.WidthFixed, 48f);
        ImGui.TableSetupColumn("类型");
        ImGui.TableSetupColumn("距离", ImGuiTableColumnFlags.WidthFixed, 70f);
        ImGui.TableSetupColumn("预计用时", ImGuiTableColumnFlags.WidthFixed, 110f);
        ImGui.TableSetupColumn("风险", ImGuiTableColumnFlags.WidthFixed, 52f);
        ImGui.TableHeadersRow();

        foreach (var route in routes)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(route.RouteId);
            ImGui.TableNextColumn();
            ImGui.Text(route.PointCount.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(route.KindSummary);
            ImGui.TableNextColumn();
            ImGui.Text($"{route.Distance:0}y");
            ImGui.TableNextColumn();
            var localText = route.LocalToStartEtaSeconds.HasValue ? $" 起点+{FormatDuration(route.LocalToStartEtaSeconds.Value)}" : string.Empty;
            ImGui.Text($"骑:{FormatDuration(route.MountedEtaSeconds)} 步:{FormatDuration(route.OnFootEtaSeconds)}{localText}");
            ImGui.TableNextColumn();
            ImGui.Text(route.MaxRiskScore.ToString());
        }

        ImGui.EndTable();
    }

    private void DrawMapAnnotationOverlay()
    {
        var performance = plugin.Configuration.Performance;
        if (performance.LowImpactMode && currentPage != MainPage.MapEditor)
            return;

        if (currentPage != MainPage.MapEditor
            && !showDynamicMapHeat
            && !showObservedTacticalTracks
            && !showBuiltInTacticalGraph
            && !showManualMapAnnotations)
            return;

        var world = plugin.WorldStateService.GetSnapshot();
        if (!plugin.AreaMapProjectionService.TryGetSnapshot(out var mapSnapshot))
            return;

        var mapName = world.Knowledge.CurrentMap?.Name ?? world.ScoreSituation.MapName;
        var document = plugin.MapAnnotationService.GetDocument(world.TerritoryType, world.MapId, mapName);
        var builtInGraph = plugin.MapTacticalGraphService.Resolve(world.TerritoryType, world.MapId);
        var overlayPoints = BuildCombinedAnnotationPoints(builtInGraph?.Points, document.Points);
        var heatPoints = world.MapTactics.IsAvailable ? world.MapTactics.HeatPoints : Array.Empty<BattlefieldMapHeatPointSnapshot>();
        if (overlayPoints.Length == 0 && currentPage != MainPage.MapEditor && (!showDynamicMapHeat || heatPoints.Length == 0))
            return;

        var overlayDocument = BuildDisplayAnnotationDocument(world.TerritoryType, world.MapId, mapName, overlayPoints);

        var drawList = ImGui.GetForegroundDrawList();
        drawList.PushClipRect(mapSnapshot.ClipMin, mapSnapshot.ClipMax, true);
        if (showDynamicMapHeat && heatPoints.Length > 0)
            DrawMapHeatPoints(drawList, heatPoints, mapSnapshot);
        if (showObservedTacticalTracks)
            DrawMapObservedTacticalTracks(drawList, world.MapTactics, mapSnapshot);
        if (builtInGraph != null && showBuiltInTacticalGraph)
            DrawMapTacticalGraph(drawList, builtInGraph, mapSnapshot, showGraphRegions, showGraphPaths, mapAnnotationKindVisibleMask);
        if (showManualMapAnnotations)
            DrawMapAnnotationRoutes(drawList, document, mapSnapshot, mapAnnotationKindVisibleMask);
        if (showGraphNodes || showManualMapAnnotations)
            DrawMapAnnotationPoints(drawList, overlayDocument, mapSnapshot, mapAnnotationKindVisibleMask, showGraphNodes, showManualMapAnnotations);

        if (currentPage == MainPage.MapEditor && annotationClickMode && mapSnapshot.HasReliableLocalPlayerAnchor)
        {
            var io = ImGui.GetIO();
            var mouse = io.MousePos;
            if (mapSnapshot.IsInside(mouse))
            {
                drawList.AddCircle(mouse, 8f, Color(new Vector4(1f, 1f, 1f, 0.95f)), 24, 2f);
                drawList.AddText(mouse + new Vector2(10f, 8f), Color(new Vector4(1f, 1f, 1f, 0.95f)), AnnotationKindText(selectedAnnotationKind));

                if (!io.WantCaptureMouse && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && mapSnapshot.TryUnproject(mouse, out var worldPosition))
                    AddMapAnnotation(worldPosition, world.TerritoryType, world.MapId, world.Knowledge.CurrentMap?.Name ?? world.ScoreSituation.MapName);
            }
        }

        drawList.PopClipRect();
    }

    private static void DrawMapHeatPoints(
        ImDrawListPtr drawList,
        IReadOnlyList<BattlefieldMapHeatPointSnapshot> heatPoints,
        AreaMapProjectionSnapshot mapSnapshot)
    {
        foreach (var heat in heatPoints.OrderBy(point => point.Intensity))
        {
            if (!mapSnapshot.TryProject(heat.Position, out var screenPosition))
                continue;

            var intensity = Math.Clamp(heat.Intensity, 0f, 100f);
            var color = HeatPointColor(intensity);
            var radius = Math.Clamp(heat.Radius * MathF.Max(0.15f, mapSnapshot.Scale / 10f), 12f, 96f);
            var fillAlpha = 0.08f + intensity / 100f * 0.18f;
            var lineAlpha = 0.30f + intensity / 100f * 0.42f;
            var innerRadius = Math.Clamp(4f + intensity / 100f * 5f, 4f, 9f);

            drawList.AddCircleFilled(screenPosition, radius, Color(new Vector4(color.X, color.Y, color.Z, fillAlpha)), 48);
            drawList.AddCircle(screenPosition, radius, Color(new Vector4(color.X, color.Y, color.Z, lineAlpha)), 48, 2f);
            drawList.AddCircleFilled(screenPosition, innerRadius, Color(new Vector4(color.X, color.Y, color.Z, 0.86f)), 24);
            drawList.AddCircle(screenPosition, innerRadius + 2f, Color(new Vector4(0f, 0f, 0f, 0.72f)), 24, 1.5f);

            if (intensity < 72f)
                continue;

            var label = TrimMapHeatLabel(heat.SourceText);
            if (!string.IsNullOrWhiteSpace(label))
                drawList.AddText(screenPosition + new Vector2(10f, -11f), Color(new Vector4(1f, 1f, 1f, 0.86f)), label);
        }
    }

    private static string TrimMapHeatLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "实时危险";

        var trimmed = text.Trim();
        return trimmed.Length <= 18 ? trimmed : trimmed[..18];
    }

    private static void DrawMapObservedTacticalTracks(
        ImDrawListPtr drawList,
        BattlefieldMapTacticsSnapshot tactics,
        AreaMapProjectionSnapshot mapSnapshot)
    {
        DrawMapObservedPath(drawList, tactics.FriendlyObservedPath, mapSnapshot, new Vector4(0.38f, 0.90f, 0.58f, 0.80f), "我方轨迹");
        DrawMapObservedPath(drawList, tactics.EnemyObservedPath, mapSnapshot, new Vector4(1f, 0.32f, 0.24f, 0.80f), "敌方轨迹");
    }

    private static void DrawMapObservedPath(
        ImDrawListPtr drawList,
        BattlefieldMapGroupPathSnapshot path,
        AreaMapProjectionSnapshot mapSnapshot,
        Vector4 color,
        string label)
    {
        var points = ObservedPathPoints(path);
        if (points.Length < 2)
            return;

        var lineColor = Color(color);
        Vector2? lastScreen = null;
        for (var i = 0; i < points.Length; i++)
        {
            if (!mapSnapshot.TryProject(points[i], out var screen))
            {
                lastScreen = null;
                continue;
            }

            if (lastScreen.HasValue)
                drawList.AddLine(lastScreen.Value, screen, lineColor, 2.4f);

            if (i == points.Length - 1)
            {
                drawList.AddCircleFilled(screen, 4.2f, lineColor, 18);
                drawList.AddText(screen + new Vector2(8f, -9f), Color(new Vector4(1f, 1f, 1f, 0.78f)), label);
            }

            lastScreen = screen;
        }
    }

    private static MapAnnotationPoint[] BuildCombinedAnnotationPoints(
        IReadOnlyList<MapAnnotationPoint>? builtInPoints,
        IReadOnlyList<MapAnnotationPoint> manualPoints)
    {
        if (builtInPoints == null || builtInPoints.Count == 0)
            return manualPoints.ToArray();
        if (manualPoints.Count == 0)
            return builtInPoints.ToArray();

        var combined = new MapAnnotationPoint[builtInPoints.Count + manualPoints.Count];
        for (var i = 0; i < builtInPoints.Count; i++)
            combined[i] = builtInPoints[i];
        for (var i = 0; i < manualPoints.Count; i++)
            combined[builtInPoints.Count + i] = manualPoints[i];
        return combined;
    }

    private static MapAnnotationDocument BuildDisplayAnnotationDocument(
        uint territoryType,
        uint mapId,
        string mapName,
        IReadOnlyList<MapAnnotationPoint> points)
        => new()
        {
            TerritoryType = territoryType,
            MapId = mapId,
            MapName = mapName,
            Points = points.ToList()
        };

    private static void DrawMapAnnotationRoutes(
        ImDrawListPtr drawList,
        MapAnnotationDocument document,
        AreaMapProjectionSnapshot mapSnapshot,
        int visibleKindMask)
    {
        var routeGroups = document.Points
            .Where(point => !string.IsNullOrWhiteSpace(point.RouteId))
            .GroupBy(point => point.RouteId.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in routeGroups)
        {
            var points = group
                .OrderBy(point => point.CreatedAtUnixMs)
                .ToArray();

            for (var i = 1; i < points.Length; i++)
            {
                if (!IsAnnotationKindVisible(points[i - 1].Kind, visibleKindMask) && !IsAnnotationKindVisible(points[i].Kind, visibleKindMask))
                    continue;

                if (!mapSnapshot.TryProject(points[i - 1].Position, out var from) || !mapSnapshot.TryProject(points[i].Position, out var to))
                    continue;

                drawList.AddLine(from, to, AnnotationRouteColor(points[i - 1], points[i]), 2.4f);
            }
        }
    }

    private static void DrawMapTacticalGraph(
        ImDrawListPtr drawList,
        MapTacticalGraphSnapshot graph,
        AreaMapProjectionSnapshot mapSnapshot,
        bool showRegions,
        bool showPaths,
        int visibleKindMask)
    {
        if (showRegions)
        {
            foreach (var region in graph.Regions)
            {
                if (!IsAnnotationKindVisible(region.Kind, visibleKindMask))
                    continue;

                var vertices = new List<Vector2>(region.Vertices.Length);
                foreach (var vertex in region.Vertices)
                {
                    if (mapSnapshot.TryProject(vertex, out var screenPosition))
                        vertices.Add(screenPosition);
                }

                if (vertices.Count < 3)
                    continue;

                DrawTacticalRegionPolygon(drawList, region.Kind, region.Label, region.RiskScore, vertices, 0.10f, 0.46f);
            }
        }

        if (!showPaths)
            return;

        foreach (var path in graph.Paths)
        {
            if (!IsAnnotationKindVisible(path.Kind, visibleKindMask))
                continue;

            var thickness = Math.Clamp(path.Width * MathF.Max(0.12f, mapSnapshot.Scale / 10f), 2.2f, 14f);
            for (var i = 1; i < path.Points.Length; i++)
            {
                if (!mapSnapshot.TryProject(path.Points[i - 1], out var from)
                    || !mapSnapshot.TryProject(path.Points[i], out var to))
                {
                    continue;
                }

                var color = TacticalShapeColor(path.Kind, path.RiskScore, 0.34f);
                drawList.AddLine(from, to, color, thickness);
                if (path.IsOneWay)
                    DrawDirectionArrow(drawList, from, to, color, thickness);
            }
        }
    }

    private static void DrawMapAnnotationPoints(
        ImDrawListPtr drawList,
        MapAnnotationDocument document,
        AreaMapProjectionSnapshot mapSnapshot,
        int visibleKindMask,
        bool showGraphNodes,
        bool showManualAnnotations)
    {
        foreach (var point in document.Points)
        {
            var builtIn = MapTacticalGraphService.IsBuiltInPoint(point) || MapTacticalGraphService.IsCustomPoint(point);
            if (builtIn && !showGraphNodes || !builtIn && !showManualAnnotations)
                continue;
            if (!IsAnnotationKindVisible(point.Kind, visibleKindMask))
                continue;

            if (!mapSnapshot.TryProject(point.Position, out var screenPosition))
                continue;

            var color = AnnotationKindColor(point.Kind);
            color.W = builtIn ? 0.68f : color.W;
            var colorU32 = Color(color);
            var areaFillAlpha = builtIn ? 0.08f : 0.14f;
            var areaOutlineAlpha = builtIn ? 0.48f : 0.72f;
            var labelAlpha = builtIn ? 0.72f : 0.95f;
            var radius = IsAreaAnnotationKind(point.Kind)
                ? Math.Clamp(point.Radius * MathF.Max(0.15f, mapSnapshot.Scale / 10f), 10f, 70f)
                : 8f;

            if (IsAreaAnnotationKind(point.Kind))
            {
                drawList.AddCircleFilled(screenPosition, radius, Color(new Vector4(color.X, color.Y, color.Z, areaFillAlpha)), 32);
                drawList.AddCircle(screenPosition, radius, Color(new Vector4(color.X, color.Y, color.Z, areaOutlineAlpha)), 32, 2f);
            }

            drawList.AddCircleFilled(screenPosition, builtIn ? 4.5f : 5.5f, colorU32, 20);
            drawList.AddCircle(screenPosition, builtIn ? 6.5f : 7.5f, Color(new Vector4(0f, 0f, 0f, builtIn ? 0.58f : 0.82f)), 20, 2f);

            var label = string.IsNullOrWhiteSpace(point.Label) ? AnnotationKindText(point.Kind) : point.Label;
            if (!string.IsNullOrWhiteSpace(point.RouteId))
                label = $"{label}/{point.RouteId}";
            if (ShouldDrawAnnotationPointLabel(point, builtIn))
                drawList.AddText(screenPosition + new Vector2(9f, -8f), Color(new Vector4(1f, 1f, 1f, labelAlpha)), label);
        }
    }

    private void AddMapAnnotation(Vector3 position, uint territoryType, uint mapId, string mapName)
    {
        plugin.MapAnnotationService.AddPoint(
            territoryType,
            mapId,
            mapName,
            position,
            selectedAnnotationKind,
            annotationLabel,
            annotationRouteId,
            annotationRadius,
            annotationRiskScore);
        annotationClearArmed = false;
    }

    private void AddMapAnnotation(
        Vector3 position,
        uint territoryType,
        uint mapId,
        string mapName,
        MapAnnotationKind kind,
        string label,
        string routeId,
        float radius,
        int riskScore)
    {
        plugin.MapAnnotationService.AddPoint(
            territoryType,
            mapId,
            mapName,
            position,
            kind,
            label,
            routeId,
            radius,
            riskScore);
        annotationClearArmed = false;
    }

    private void ImportCurrentMapObjectives(BattlefieldSnapshot snapshot, uint territoryType, uint mapId, string mapName)
    {
        var document = plugin.MapAnnotationService.GetDocument(territoryType, mapId, mapName);
        foreach (var objective in snapshot.MapObjectives)
        {
            if (IsNearExistingAnnotation(document, objective.Position, MapAnnotationKind.Objective, 10f))
                continue;

            var label = !string.IsNullOrWhiteSpace(objective.Name)
                ? objective.Name
                : $"{MapObjectiveCategoryText(objective.Category)} {objective.LocationId}".Trim();
            AddMapAnnotation(objective.Position, territoryType, mapId, mapName, MapAnnotationKind.Objective, label, string.Empty, 18f, 35);
        }
    }

    private void ImportCurrentMapEvents(BattlefieldSnapshot snapshot, uint territoryType, uint mapId, string mapName)
    {
        var document = plugin.MapAnnotationService.GetDocument(territoryType, mapId, mapName);
        foreach (var item in snapshot.MapEvents)
        {
            if (IsNearExistingAnnotation(document, item.Position, MapAnnotationKind.Objective, 10f))
                continue;

            var value = MapEventValueText(item);
            var label = value == "-" ? MapEventKindText(item.Kind) : $"{MapEventKindText(item.Kind)} {value}";
            AddMapAnnotation(item.Position, territoryType, mapId, mapName, MapAnnotationKind.Objective, label, string.Empty, 18f, 35);
        }
    }

    private static bool IsNearExistingAnnotation(
        MapAnnotationDocument document,
        Vector3 position,
        MapAnnotationKind kind,
        float radius)
    {
        var radiusSquared = radius * radius;
        return document.Points.Any(point =>
        {
            if (point.Kind != kind)
                return false;

            var dx = point.X - position.X;
            var dz = point.Z - position.Z;
            return dx * dx + dz * dz <= radiusSquared;
        });
    }

    private void DrawToolsPage()
    {
        DrawSectionTitle("工具", "配置管理、窗口入口与调试辅助");

        var floating = plugin.Configuration.FloatingButton;
        var commandOverlay = plugin.Configuration.CommandOverlay;
        var limitBreak = plugin.Configuration.LimitBreak;
        var scoreReader = plugin.Configuration.ScoreReader;
        var battleHigh = plugin.Configuration.BattleHigh;
        var tacticalStatus = plugin.Configuration.TacticalStatus;
        var announcement = plugin.Configuration.Announcement;
        var advanced = plugin.Configuration.AdvancedTactics;
        var replay = plugin.Configuration.Replay;
        var performance = plugin.Configuration.Performance;
        var llmDecision = plugin.Configuration.LlmDecision;
        var changed = false;

        if (BeginCollapsibleSection("配置管理", "导入/导出插件配置，方便备份、迁移和发布前验收", true))
        {
            if (ImGui.Button("导出配置", new Vector2(130f, 28f)))
                HandleExportConfiguration();
            ImGui.SameLine();
            if (ImGui.Button("导入配置", new Vector2(130f, 28f)))
                HandleImportConfiguration();

            DrawHint("这里导入导出的是插件配置参数本体。地图标注、战术图谱和回放日志仍保存在插件数据目录中。");
            if (!string.IsNullOrWhiteSpace(configurationTransferStatus))
                DrawHint(configurationTransferStatus);
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("性能模式", "降低实时扫描、地图采样、回放和调试叠加开销", true))
        {
            changed |= DrawToggle("低影响模式", performance.LowImpactMode, value => performance.LowImpactMode = value);
            changed |= DrawSliderInt("战场态势刷新间隔（毫秒）", performance.WorldRefreshIntervalMs, 250, 5000, value => performance.WorldRefreshIntervalMs = value);
            changed |= DrawSliderInt("战斗/危险刷新间隔（毫秒）", performance.CombatRefreshIntervalMs, 500, 5000, value => performance.CombatRefreshIntervalMs = value);
            changed |= DrawSliderInt("比分扫描间隔（毫秒）", performance.ScoreScanIntervalMs, 250, 10000, value => performance.ScoreScanIntervalMs = value);
            changed |= DrawSliderInt("状态/Buff扫描间隔（毫秒）", performance.StatusScanIntervalMs, 250, 10000, value => performance.StatusScanIntervalMs = value);
            changed |= DrawSliderInt("区域地图采样间隔（毫秒）", performance.AreaMapSampleIntervalMs, 100, 10000, value => performance.AreaMapSampleIntervalMs = value);
            changed |= DrawSliderInt("极限槽采样间隔（毫秒）", performance.LimitBreakSampleIntervalMs, 100, 3000, value => performance.LimitBreakSampleIntervalMs = value);
            changed |= DrawSliderInt("战略决策刷新间隔（毫秒）", performance.DecisionRefreshIntervalMs, 1000, 10000, value => performance.DecisionRefreshIntervalMs = value);
            changed |= DrawToggle("启用回放调权反馈", performance.EnableDecisionQualityFeedback, value => performance.EnableDecisionQualityFeedback = value);
            if (ImGui.Button("应用低影响推荐设置", new Vector2(180f, 28f)))
            {
                plugin.Configuration.ApplyLowImpactDefaults();
                showDynamicMapHeat = false;
                showObservedTacticalTracks = false;
                changed = true;
            }

            DrawHint($"当前生效：态势 {performance.EffectiveWorldRefreshIntervalMs}ms / 战斗 {performance.EffectiveCombatRefreshIntervalMs}ms / 比分 {performance.EffectiveScoreScanIntervalMs}ms / Buff {performance.EffectiveStatusScanIntervalMs}ms / 地图 {performance.EffectiveAreaMapSampleIntervalMs}ms / 极限槽 {performance.EffectiveLimitBreakSampleIntervalMs}ms / 战略 {performance.EffectiveDecisionRefreshIntervalMs}ms");
            DrawHint("低影响模式会拆成慢战略和快战斗两层；快层优先复用轻量缓存，减少战中决策迟钝，同时尽量避开掉帧。");
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("AI大决策", "DeepSeek 战略参谋、本地门控、请求频率和调试输出", true))
        {
            changed |= DrawToggle("启用 AI 大决策", llmDecision.Enabled, value => llmDecision.Enabled = value);
            changed |= DrawInputText("API 地址", llmDecision.ProviderBaseUrl, 180, value => llmDecision.ProviderBaseUrl = value);
            changed |= DrawInputText("模型", llmDecision.Model, 120, value => llmDecision.Model = value);
            changed |= DrawInputText("API Key 环境变量", llmDecision.ApiKeyEnvironmentVariable, 80, value => llmDecision.ApiKeyEnvironmentVariable = value);
            changed |= DrawInputText("API Key（可留空，优先用环境变量）", llmDecision.ApiKey, 220, value => llmDecision.ApiKey = value);
            changed |= DrawSliderInt("请求超时（毫秒）", llmDecision.RequestTimeoutMs, 1500, 15000, value => llmDecision.RequestTimeoutMs = value);
            changed |= DrawSliderInt("最小请求间隔（秒）", llmDecision.MinIntervalSeconds, 3, 120, value => llmDecision.MinIntervalSeconds = value);
            changed |= DrawSliderInt("同局势冷却（秒）", llmDecision.SameSituationCooldownSeconds, 5, 180, value => llmDecision.SameSituationCooldownSeconds = value);
            changed |= DrawToggle("\u542f\u7528\u56fa\u5b9a\u5c40\u5185 AI \u5206\u6790", llmDecision.RoutinePulseEnabled, value => llmDecision.RoutinePulseEnabled = value);
            changed |= DrawSliderInt("\u56fa\u5b9a\u5206\u6790\u95f4\u9694\uff08\u79d2\uff09", llmDecision.RoutinePulseIntervalSeconds, 10, 180, value => llmDecision.RoutinePulseIntervalSeconds = value);
            changed |= DrawSliderInt("AI 决策新鲜期（秒）", llmDecision.FreshDecisionSeconds, 10, 180, value => llmDecision.FreshDecisionSeconds = value);
            changed |= DrawSliderInt("上下文保留条数", llmDecision.MaxContextTurns, 0, 12, value => llmDecision.MaxContextTurns = value);
            changed |= DrawToggle("请求中包含调试摘要", llmDecision.IncludeDebugPayload, value => llmDecision.IncludeDebugPayload = value);
            _ = DrawInputText("测试附言 / 对 AI 说（可选）", llmManualPrompt, 320, value => llmManualPrompt = value);

            var snapshot = plugin.WorldStateService.GetSnapshot();
            if (ImGui.Button("忽略门控立即请求", new Vector2(180f, 28f)))
                llmManualStatus = plugin.LlmStrategicDecisionService.RequestManualProbe(snapshot, llmManualPrompt, false);
            ImGui.SameLine();
            if (ImGui.Button("发送测试对话", new Vector2(140f, 28f)))
                llmManualStatus = plugin.LlmStrategicDecisionService.RequestManualProbe(snapshot, llmManualPrompt, true);
            ImGui.SameLine();
            if (ImGui.Button("清空附言", new Vector2(110f, 28f)))
            {
                llmManualPrompt = string.Empty;
                llmManualStatus = "已清空测试附言。";
            }

            var ai = snapshot.LlmStrategicDecision;
            DrawHint(ai.StatusText);
            if (!string.IsNullOrWhiteSpace(ai.GateReason))
                DrawHint($"当前门控：{ai.NeedText}；{ai.GateReason}");
            if (ai.IsAvailable)
                DrawHint($"最近决策：{ai.RecommendedAction}；{ai.ShortReason}");
            if (!string.IsNullOrWhiteSpace(llmManualStatus))
                DrawHint($"测试通道：{llmManualStatus}");
            DrawHint("当前默认门控已放宽，方便联调 AI 延迟与 JSON 稳定性；本地状态机会继续吃掉即时战斗和紧急撤退，AI 仍只看大决策。");

            if (BeginCollapsibleSection("AI 对话调试", "局内直接查看提示词、原始回复、解析 JSON 和会话上下文", true))
            {
                var debug = plugin.LlmStrategicDecisionService.GetDebugSnapshot(snapshot);
                DrawLlmDebugSnapshot(ai, debug);
                EndCollapsibleSection();
            }

            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("窗口入口", "悬浮球和聊天命令入口", true))
        {
            changed |= DrawToggle("启用悬浮球", floating.Enabled, value => floating.Enabled = value);
            if (ImGui.Button("重置悬浮球位置", new Vector2(160f, 28f)))
            {
                floating.X = 80f;
                floating.Y = 360f;
                changed = true;
            }

            DrawHint("悬浮球可拖动；单击悬浮球会打开或关闭这个主窗口。");
            DrawHint("命令：/man 打开主窗口，/manradar 开关雷达。");
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("指挥大字", "屏幕指挥短句、位置、字号和停留时间", true))
        {
            changed |= DrawToggle("显示屏幕指挥大字", commandOverlay.Enabled, value => commandOverlay.Enabled = value);
            changed |= DrawToggle("仅在纷争前线显示", commandOverlay.OnlyInFrontline, value => commandOverlay.OnlyInFrontline = value);
            changed |= DrawToggle("没有新发布时显示当前主指令", commandOverlay.ShowPrimaryWhenIdle, value => commandOverlay.ShowPrimaryWhenIdle = value);
            changed |= DrawToggle("显示指令原因", commandOverlay.ShowReason, value => commandOverlay.ShowReason = value);
            changed |= DrawToggle("文字描边", commandOverlay.ShowStroke, value => commandOverlay.ShowStroke = value);
            changed |= DrawToggle("显示半透明背景", commandOverlay.ShowBackground, value => commandOverlay.ShowBackground = value);
            changed |= DrawSliderFloat("大字 X 位置", commandOverlay.X, 0f, 3840f, value => commandOverlay.X = value);
            changed |= DrawSliderFloat("大字 Y 位置", commandOverlay.Y, 0f, 2160f, value => commandOverlay.Y = value);
            changed |= DrawSliderFloat("大字区域宽度", commandOverlay.Width, 260f, 1800f, value => commandOverlay.Width = value);
            changed |= DrawSliderFloat("大字区域高度", commandOverlay.Height, 80f, 520f, value => commandOverlay.Height = value);
            changed |= DrawSliderFloat("大字字号", commandOverlay.FontScale, 0.8f, 5f, value => commandOverlay.FontScale = value);
            changed |= DrawSliderInt("发布后停留秒数", commandOverlay.PublishedHoldSeconds, 1, 20, value => commandOverlay.PublishedHoldSeconds = value);
            changed |= DrawSliderInt("AI \u4e3b\u5bfc\u9501\u5b9a\u79d2\u6570", commandOverlay.AiLeadHoldSeconds, 0, 20, value => commandOverlay.AiLeadHoldSeconds = value);
            changed |= DrawColorEdit("大字文字颜色", new Vector4(commandOverlay.TextColorR, commandOverlay.TextColorG, commandOverlay.TextColorB, 1f), value =>
            {
                commandOverlay.TextColorR = value.X;
                commandOverlay.TextColorG = value.Y;
                commandOverlay.TextColorB = value.Z;
            });
            changed |= DrawColorEdit("AI \u4e3b\u5bfc\u6587\u5b57\u989c\u8272", new Vector4(commandOverlay.AiTextColorR, commandOverlay.AiTextColorG, commandOverlay.AiTextColorB, 1f), value =>
            {
                commandOverlay.AiTextColorR = value.X;
                commandOverlay.AiTextColorG = value.Y;
                commandOverlay.AiTextColorB = value.Z;
            });
            changed |= DrawColorEdit("大字描边颜色", new Vector4(commandOverlay.StrokeColorR, commandOverlay.StrokeColorG, commandOverlay.StrokeColorB, 1f), value =>
            {
                commandOverlay.StrokeColorR = value.X;
                commandOverlay.StrokeColorG = value.Y;
                commandOverlay.StrokeColorB = value.Z;
            });
            changed |= DrawSliderFloat("背景透明度", commandOverlay.BackgroundAlpha, 0f, 0.85f, value => commandOverlay.BackgroundAlpha = value);
            if (ImGui.Button("恢复推荐大字位置", new Vector2(170f, 28f)))
            {
                commandOverlay.X = 430f;
                commandOverlay.Y = 150f;
                commandOverlay.Width = 980f;
                commandOverlay.Height = 150f;
                commandOverlay.FontScale = 2.0f;
                commandOverlay.PublishedHoldSeconds = 5;
                commandOverlay.AiLeadHoldSeconds = 6;
                commandOverlay.AiTextColorR = 0.35f;
                commandOverlay.AiTextColorG = 0.82f;
                commandOverlay.AiTextColorB = 1f;
                changed = true;
            }

            var commands = plugin.WorldStateService.GetSnapshot().Decision.CommandSituation;
            DrawHint(commands.PrimaryCommand.HasValue
                ? $"当前预览：第一行行动；第二行指令；第三行敌军动向。当前指令：{commands.PrimaryCommand.Value.CommandText}"
                : "当前预览：第一行行动；第二行指令；第三行敌军动向。");
            EndCollapsibleSection();
        }


        if (BeginCollapsibleSection("极限槽预测", "极限技充能显示、位置和调试来源", true))
        {
            changed |= DrawToggle("显示极限技充能百分比", limitBreak.ShowLimitBreakUI, value => limitBreak.ShowLimitBreakUI = value);
            changed |= DrawToggle("显示极限槽调试信息", limitBreak.ShowDebugInfo, value => limitBreak.ShowDebugInfo = value);
            changed |= DrawSliderFloat("极限槽 X 偏移", limitBreak.OffsetX, -100f, 500f, value => limitBreak.OffsetX = value);
            changed |= DrawSliderFloat("极限槽 Y 偏移", limitBreak.OffsetY, -100f, 100f, value => limitBreak.OffsetY = value);
            changed |= DrawSliderFloat("极限槽文字缩放", limitBreak.WindowScale, 0.5f, 3f, value => limitBreak.WindowScale = value);
            changed |= DrawToggle("极限槽文字描边", limitBreak.ShowStroke, value => limitBreak.ShowStroke = value);
            changed |= DrawColorEdit("极限槽文字颜色", new Vector4(limitBreak.TextColorR, limitBreak.TextColorG, limitBreak.TextColorB, 1f), value =>
            {
                limitBreak.TextColorR = value.X;
                limitBreak.TextColorG = value.Y;
                limitBreak.TextColorB = value.Z;
            });
            changed |= DrawColorEdit("极限槽描边颜色", new Vector4(limitBreak.StrokeColorR, limitBreak.StrokeColorG, limitBreak.StrokeColorB, 1f), value =>
            {
                limitBreak.StrokeColorR = value.X;
                limitBreak.StrokeColorG = value.Y;
                limitBreak.StrokeColorB = value.Z;
            });

            var limitBreakSnapshot = plugin.LimitBreakService.GetSnapshot(plugin.WorldStateService.GetSnapshot().IsInFrontline);
            DrawHint(limitBreakSnapshot.SummaryText);
            if (limitBreak.ShowDebugInfo)
            {
                DrawHint($"来源：{limitBreakSnapshot.SourceText}  单位：{limitBreakSnapshot.CurrentUnits}/{limitBreakSnapshot.BarUnits}  上次百分比：{limitBreakSnapshot.LastPercent:0.0}%  每跳：{limitBreakSnapshot.PercentPerTick:0.0}%");
                foreach (var line in plugin.LimitBreakService.GetAddonDebugLines().Take(8))
                    DrawHint(line);
            }
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("比分读取", "结构化比分来源和原始调试信息", true))
        {
            changed |= DrawToggle("显示比分原始来源调试信息", scoreReader.ShowRawSourceDebug, value => scoreReader.ShowRawSourceDebug = value);
            DrawHint("旧的界面节点读分链路已移除。当前只保留结构化比分来源和原始来源调试。");
            DrawHint(plugin.WorldStateService.GetSnapshot().ScoreDebugInfo);
            if (scoreReader.ShowRawSourceDebug)
            {
                foreach (var line in plugin.FrontlineScoreReader.GetRawScoreSourceDebugLines().Take(18))
                    DrawHint(line);
            }
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("战意状态读取", "战意状态识别和可见状态调试", false))
        {
            changed |= DrawInputText("候选状态编号（按 1~5 层顺序）", battleHigh.CandidateStatusIds, 120, value => battleHigh.CandidateStatusIds = value);
            changed |= DrawToggle("显示战意状态调试信息", battleHigh.ShowDebugInfo, value => battleHigh.ShowDebugInfo = value);
            changed |= DrawToggle("调试时列出可见玩家全部状态", battleHigh.ShowAllVisibleStatusesInDebug, value => battleHigh.ShowAllVisibleStatusesInDebug = value);
            DrawHint("候选编号只是兜底；实际读取会优先扫玩家状态列表，并用状态名/描述识别战意高涨、战意狂热、战意、斗志。");
            if (battleHigh.ShowDebugInfo)
            {
                foreach (var line in plugin.WorldStateService.GetBattleHighDebugLines().Take(42))
                    DrawHint(line);
            }
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("状态效果战术化", "防御、控制、抗控、无敌识别与派生标签", false))
        {
            changed |= DrawToggle("显示战术状态调试信息", tacticalStatus.ShowDebugInfo, value => tacticalStatus.ShowDebugInfo = value);
            changed |= DrawToggle("战术状态调试列出全部可见状态", tacticalStatus.ShowAllVisibleStatusesInDebug, value => tacticalStatus.ShowAllVisibleStatusesInDebug = value);
            changed |= DrawInputText("防御中候选状态编号", tacticalStatus.GuardingStatusIds, 120, value => tacticalStatus.GuardingStatusIds = value);
            changed |= DrawInputText("被控候选状态编号", tacticalStatus.CrowdControlledStatusIds, 120, value => tacticalStatus.CrowdControlledStatusIds = value);
            changed |= DrawInputText("抗控候选状态编号", tacticalStatus.ControlImmuneStatusIds, 120, value => tacticalStatus.ControlImmuneStatusIds = value);
            changed |= DrawInputText("无敌候选状态编号", tacticalStatus.InvulnerableStatusIds, 120, value => tacticalStatus.InvulnerableStatusIds = value);
            changed |= DrawInputText("雪精祝福候选状态编号", tacticalStatus.SnowBlessingStatusIds, 120, value => tacticalStatus.SnowBlessingStatusIds = value);
            DrawHint("未填编号也会按状态名/描述识别；可控和血量低线会作为派生战术标签生成。");
            if (tacticalStatus.ShowDebugInfo)
            {
                foreach (var line in plugin.WorldStateService.GetTacticalStatusDebugLines().Take(58))
                    DrawHint(line);
            }
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("战场通告读取", "天气、资源刷新和战场通告调试", false))
        {
            changed |= DrawToggle("显示战场通告调试信息", announcement.ShowDebugInfo, value => announcement.ShowDebugInfo = value);
            var announcementSnapshot = plugin.WorldStateService.GetSnapshot().AnnouncementSituation;
            DrawHint(announcementSnapshot.SummaryText);
            if (announcement.ShowDebugInfo)
            {
                foreach (var line in plugin.FrontlineAnnouncementReader.GetAddonDebugLines().Take(10))
                    DrawHint(line);
            }
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("高级战术洞察校准", "凝聚、假撤、高台、第三方、组排和封路阈值", false))
        {
            changed |= DrawToggle("启用高级战术洞察", advanced.Enabled, value => advanced.Enabled = value);
            changed |= DrawToggle("显示被压制候选", advanced.ShowSuppressedInsights, value => advanced.ShowSuppressedInsights = value);
            changed |= DrawSliderInt("最低置信度%", advanced.MinAlertConfidencePercent, 0, 100, value => advanced.MinAlertConfidencePercent = value);
            changed |= DrawSliderFloat("凝聚报警风险", advanced.CohesionMinAlertSeverity, 0f, 100f, value => advanced.CohesionMinAlertSeverity = value);
            changed |= DrawSliderInt("凝聚最小样本", advanced.CohesionMinSampleCount, 1, 24, value => advanced.CohesionMinSampleCount = value);
            changed |= DrawSliderFloat("跟随近距离", advanced.FollowNearDistance, 12f, 100f, value => advanced.FollowNearDistance = value);
            changed |= DrawSliderFloat("后撤距离增量", advanced.RetreatMinDistanceDelta, 0f, 20f, value => advanced.RetreatMinDistanceDelta = value);
            changed |= DrawSliderFloat("后撤最低速度", advanced.RetreatMinSpeed, 0f, 5f, value => advanced.RetreatMinSpeed = value);
            changed |= DrawSliderInt("后撤远离比例%", advanced.RetreatMovingAwayRatioPercent, 0, 100, value => advanced.RetreatMovingAwayRatioPercent = value);
            changed |= DrawSliderFloat("假撤报警风险", advanced.FakeRetreatMinSeverity, 0f, 100f, value => advanced.FakeRetreatMinSeverity = value);
            changed |= DrawSliderInt("假撤威胁信号数", advanced.FakeRetreatMinThreatSignals, 0, 12, value => advanced.FakeRetreatMinThreatSignals = value);
            changed |= DrawSliderInt("高台最低压力", advanced.HighGroundMinPressure, 1, 24, value => advanced.HighGroundMinPressure = value);
            changed |= DrawSliderFloat("高台报警风险", advanced.HighGroundMinSeverity, 0f, 100f, value => advanced.HighGroundMinSeverity = value);
            changed |= DrawSliderFloat("第三方最远距离", advanced.ThirdPartyMaxDistance, 60f, 320f, value => advanced.ThirdPartyMaxDistance = value);
            changed |= DrawSliderFloat("第三方最小夹角", advanced.ThirdPartyMinAngle, 45f, 180f, value => advanced.ThirdPartyMinAngle = value);
            changed |= DrawSliderFloat("第三方报警风险", advanced.ThirdPartyMinSeverity, 0f, 100f, value => advanced.ThirdPartyMinSeverity = value);
            changed |= DrawSliderFloat("组排搜索半径", advanced.SquadSearchRadius, 12f, 60f, value => advanced.SquadSearchRadius = value);
            changed |= DrawSliderFloat("组排最低分", advanced.SquadMinScore, 0f, 100f, value => advanced.SquadMinScore = value);
            changed |= DrawSliderInt("组排方向相似%", advanced.SquadMinDirectionSimilarityPercent, 0, 100, value => advanced.SquadMinDirectionSimilarityPercent = value);
            changed |= DrawSliderInt("封路最低压力", advanced.ChokeMinPressure, 1, 24, value => advanced.ChokeMinPressure = value);
            changed |= DrawSliderFloat("封路报警风险", advanced.ChokeMinSeverity, 0f, 100f, value => advanced.ChokeMinSeverity = value);
            DrawHint("调法：先开“显示被压制候选”和回放记录，实战看误报类型；误报多就抬高对应风险/压力/置信，漏报多就降低。被压制候选会写进回放但不进入决策。");
            EndCollapsibleSection();
        }

        if (BeginCollapsibleSection("实战回放记录", "逐行结构化回放、评估事件和保留策略", false))
        {
            changed |= DrawToggle("启用前线实战回放", replay.Enabled, value => replay.Enabled = value);
            changed |= DrawSliderInt("记录间隔（秒）", replay.RecordIntervalSeconds, 1, 30, value => replay.RecordIntervalSeconds = value);
            changed |= DrawSliderInt("保留场次数", replay.MaxSessionFiles, 1, 300, value => replay.MaxSessionFiles = value);
            changed |= DrawSliderInt("保留天数", replay.MaxSessionAgeDays, 1, 365, value => replay.MaxSessionAgeDays = value);
            changed |= DrawToggle("记录玩家明细", replay.IncludePlayerDetails, value => replay.IncludePlayerDetails = value);
            changed |= DrawToggle("显示回放调试信息", replay.ShowDebugInfo, value => replay.ShowDebugInfo = value);
            changed |= DrawInputText("回放目录名", replay.DirectoryName, 80, value => replay.DirectoryName = value);
            DrawReplayRecorderStatus(plugin.BattlefieldReplayRecorder.GetStatus());
            DrawHint("日志为逐行结构化记录：战场帧是每秒快照，评估事件是指挥短句 10/30 秒后的结果代理。");
            EndCollapsibleSection();
        }

        if (changed)
        {
            commandOverlay.Normalize();
            limitBreak.Normalize();
            scoreReader.Normalize();
            battleHigh.Normalize();
            tacticalStatus.Normalize();
            advanced.Normalize();
            replay.Normalize();
            performance.Normalize();
            llmDecision.Normalize();
            plugin.Configuration.Save();
        }
    }

    private void HandleExportConfiguration()
    {
        try
        {
            using var dialog = new SaveFileDialog
            {
                Title = "导出前线战术指挥配置",
                Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                DefaultExt = "json",
                AddExtension = true,
                OverwritePrompt = true,
                InitialDirectory = ResolveConfigurationTransferDirectory(),
                FileName = $"frontline-commander-config-{DateTime.Now:yyyyMMdd-HHmmss}.json"
            };

            if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                configurationTransferStatus = "配置导出已取消。";
                return;
            }

            plugin.Configuration.ExportToFile(dialog.FileName, out configurationTransferStatus);
        }
        catch (Exception ex)
        {
            configurationTransferStatus = $"配置导出失败：{ex.Message}";
        }
    }

    private void HandleImportConfiguration()
    {
        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "导入前线战术指挥配置",
                Filter = "JSON 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true,
                InitialDirectory = ResolveConfigurationTransferDirectory()
            };

            if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.FileName))
            {
                configurationTransferStatus = "配置导入已取消。";
                return;
            }

            plugin.Configuration.TryImportFromFile(dialog.FileName, out configurationTransferStatus);
        }
        catch (Exception ex)
        {
            configurationTransferStatus = $"配置导入失败：{ex.Message}";
        }
    }

    private static string ResolveConfigurationTransferDirectory()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(documents) && Directory.Exists(documents))
            return documents;

        return AppContext.BaseDirectory;
    }

    private static void DrawReplayRecorderStatus(BattlefieldReplayRecorderStatus status)
    {
        DrawGroupTitle("实战校准/回放");
        var color = status.IsRecording
            ? new Vector4(0.48f, 0.90f, 0.62f, 1f)
            : status.Enabled
                ? new Vector4(0.95f, 0.82f, 0.35f, 1f)
                : new Vector4(0.62f, 0.62f, 0.66f, 1f);
        ImGui.TextColored(color, status.StatusText);
        DrawHint($"帧:{status.FramesWritten}  评估:{status.EvaluationEventsWritten}  待评估:{status.PendingEvaluations}  队列:{status.QueuedWorkItems}  丢帧:{status.DroppedFrames}  上次写入:{FormatReplayAge(status.LastWriteAgeMs)}  上次清理:{FormatReplayAge(status.LastPruneAgeMs)}");
        DrawHint($"目录：{status.DirectoryPath}");
        if (status.IsRecording && !string.IsNullOrWhiteSpace(status.CurrentFilePath))
            DrawHint($"当前文件：{status.CurrentFilePath}");
        if (!string.IsNullOrWhiteSpace(status.LastError))
            DrawHint($"错误：{status.LastError}");
    }

    private static string FormatReplayAge(long ageMs)
    {
        if (ageMs < 0)
            return "-";
        if (ageMs < 1000)
            return $"{ageMs}毫秒";
        return $"{(int)MathF.Ceiling(ageMs / 1000f)}秒";
    }

    private static void DrawSectionTitle(string title, string subtitle)
    {
        ImGui.TextColored(new Vector4(0.92f, 0.92f, 0.96f, 1f), title);
        ImGui.TextColored(new Vector4(0.62f, 0.62f, 0.68f, 1f), subtitle);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static bool BeginCollapsibleSection(string title, string summary, bool defaultOpen = true)
    {
        ImGui.Spacing();
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        var open = ImGui.CollapsingHeader($"{title}##section_{title}", flags);
        if (!open)
            return false;

        ImGui.Indent(8f);
        if (!string.IsNullOrWhiteSpace(summary))
            DrawHint(summary);
        return true;
    }

    private static void EndCollapsibleSection()
    {
        ImGui.Unindent(8f);
        ImGui.Spacing();
    }

    private static void DrawGroupTitle(string title)
    {
        ImGui.TextColored(new Vector4(0.85f, 0.76f, 0.48f, 1f), title);
    }

    private static void DrawHint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.62f, 0.62f, 0.66f, 1f));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static bool DrawToggle(string label, bool currentValue, Action<bool> setValue)
    {
        var value = currentValue;
        if (!ImGui.Checkbox(label, ref value))
            return false;

        setValue(value);
        return true;
    }

    private static bool DrawInputText(string label, string currentValue, uint maxLength, Action<string> setValue)
    {
        var value = currentValue ?? string.Empty;
        var encoded = Encoding.UTF8.GetBytes(value);
        var buffer = new byte[Math.Max((int)maxLength + 1, encoded.Length + 8)];
        Array.Copy(encoded, buffer, Math.Min(encoded.Length, buffer.Length - 1));

        ImGui.SetNextItemWidth(320f);
        if (!ImGui.InputText(label, buffer))
            return false;

        var zeroIndex = Array.IndexOf(buffer, (byte)0);
        if (zeroIndex < 0)
            zeroIndex = buffer.Length;

        setValue(Encoding.UTF8.GetString(buffer, 0, zeroIndex).Trim());
        return true;
    }

    private static void DrawReadonlyTextPanel(string label, string text, string id, float height)
    {
        var value = text ?? string.Empty;
        ImGui.AlignTextToFramePadding();
        ImGui.Text(label);
        ImGui.SameLine();
        if (ImGui.SmallButton($"复制##{id}"))
            ImGui.SetClipboardText(value);
        ImGui.SameLine();
        ImGui.TextDisabled($"{value.Length} 字");

        if (ImGui.BeginChild($"##{id}", new Vector2(0f, height), true, ImGuiWindowFlags.HorizontalScrollbar))
        {
            if (string.IsNullOrWhiteSpace(value))
                ImGui.TextDisabled("暂无内容");
            else
                ImGui.TextUnformatted(value);
        }

        ImGui.EndChild();
    }

    private static string BuildLlmDebugStatusText(BattlefieldLlmDebugSnapshot debug)
    {
        if (!debug.HasRequest)
            return "本局尚未发起 AI 请求";
        if (debug.IsPending)
            return "请求已发出，等待模型回复";
        if (debug.ReceivedAtTicks >= 0)
            return debug.AgeSeconds >= 0 ? $"最近一次回复距今 {debug.AgeSeconds} 秒" : "最近一次请求已收到回复";
        return "最近一次请求未返回有效回复";
    }

    private static string BuildLlmConversationDebugText(IReadOnlyList<BattlefieldLlmConversationTurnSnapshot> turns)
    {
        if (turns.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < turns.Count; i++)
        {
            var turn = turns[i];
            if (builder.Length > 0)
                builder.AppendLine().AppendLine();

            builder.Append("[").Append(i + 1).Append("] ");
            if (turn.MatchRemainingSeconds >= 0)
                builder.Append("剩余 ").Append(turn.MatchRemainingSeconds).Append(" 秒 | ");
            builder.Append(turn.NeedText);
            builder.Append(" | 置信 ").Append(turn.Confidence.ToString("0"));
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(turn.OperatorNote))
                builder.Append("附言：").AppendLine(turn.OperatorNote);
            builder.Append("决策：").AppendLine(turn.Decision);
            if (!string.IsNullOrWhiteSpace(turn.ShortReason))
                builder.Append("理由：").AppendLine(turn.ShortReason);
            if (!string.IsNullOrWhiteSpace(turn.SituationKey))
                builder.Append("场景键：").Append(turn.SituationKey);
        }

        return builder.ToString();
    }

    private static bool DrawSliderFloat(string label, float currentValue, float min, float max, Action<float> setValue)
    {
        var value = currentValue;
        ImGui.SetNextItemWidth(220f);
        if (!ImGui.SliderFloat(label, ref value, min, max))
            return false;

        setValue(value);
        return true;
    }

    private static bool DrawSliderInt(string label, int currentValue, int min, int max, Action<int> setValue)
    {
        var value = currentValue;
        ImGui.SetNextItemWidth(220f);
        if (!ImGui.SliderInt(label, ref value, min, max))
            return false;

        setValue(value);
        return true;
    }

    private static bool DrawColorEdit(string label, Vector4 currentValue, Action<Vector4> setValue)
    {
        var value = currentValue;
        ImGui.SetNextItemWidth(220f);
        if (!ImGui.ColorEdit4(label, ref value))
            return false;

        setValue(value);
        return true;
    }

    private static void DrawTeamSituation(BattlefieldTeamSituationSnapshot situation)
    {
        ImGui.Spacing();
        DrawGroupTitle("团队态势");
        DrawHint(situation.SummaryText);

        if (ImGui.BeginTable("##TeamSituationTable", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("队伍");
            ImGui.TableSetupColumn("人数");
            ImGui.TableSetupColumn("血线/战意");
            ImGui.TableSetupColumn("分布/主团");
            ImGui.TableHeadersRow();
            DrawTeamSummaryRow(situation.Friendly);
            DrawTeamSummaryRow(situation.Enemy);
            if (situation.Unknown.TotalCount > 0)
                DrawTeamSummaryRow(situation.Unknown);
            ImGui.EndTable();
        }

        ImGui.Text($"阵营玩家列表：我方 {situation.FriendlyPlayers.Length} / 敌方1 {situation.EnemyAlliance1Players.Length} / 敌方2 {situation.EnemyAlliance2Players.Length}");
        ImGui.Text($"复活节奏：{situation.RespawnRhythm.SummaryText}");
        ImGui.Text($"敌方大团：{situation.EnemyMainGroupMovement.SummaryText}");
        ImGui.Text($"大团来源：{situation.EnemyMainGroupMovement.SourceText}");
        ImGui.Text($"敌方分兵：{situation.EnemySplitSummaryText}");
        DrawBattleHighPreview(situation);
        DrawTacticalStatusPreview(situation);
        DrawAllianceSituationPreview(situation.Alliances);
        DrawEnemyClusterPreview(situation.EnemyClusters);
        DrawCompositionLine("我方定位", situation.Friendly.RoleComposition, 6);
        DrawCompositionLine("敌方定位", situation.Enemy.RoleComposition, 6);
        DrawCompositionLine("我方职业", situation.Friendly.JobComposition, 8);
        DrawCompositionLine("敌方职业", situation.Enemy.JobComposition, 8);
        DrawFocusPreview("敌方集火我方", situation.EnemyFocusTargets);
        DrawFocusPreview("我方集火敌方", situation.FriendlyFocusTargets);
    }

    private static void DrawTeamSummaryRow(BattlefieldTeamSummarySnapshot team)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(team.Name);
        ImGui.TableNextColumn();
        ImGui.Text($"总:{team.TotalCount} 活:{team.AliveCount} 死:{team.DeadCount}");
        ImGui.TableNextColumn();
        ImGui.Text($"残血:{team.LowHpCount} 咏唱:{team.CastingCount} 战意:{team.BattleHighCount}/狂热{team.BattleFeverCount} 最高:{team.MaxBattleHighLevel} 防:{team.GuardingCount} 控:{team.CrowdControlledCount} 斩:{team.ExecutableCount}");
        ImGui.TableNextColumn();
        var clusterText = team.MainCluster.HasValue
            ? $"近:{team.NearCount} 中:{team.MidCount} 远:{team.FarCount} 主团:{team.MainCluster.Value.PlayerCount}人 {FormatPosition(team.MainCluster.Value.Center)}"
            : $"近:{team.NearCount} 中:{team.MidCount} 远:{team.FarCount} 主团:暂无";
        ImGui.Text(clusterText);
    }

    private static void DrawAllianceSituationPreview(BattlefieldAllianceSituationSnapshot[] alliances)
    {
        if (alliances.Length == 0)
        {
            DrawHint("分阵营态势：当前没有可用阵营样本。");
            return;
        }

        var count = Math.Min(alliances.Length, 3);
        for (var i = 0; i < count; i++)
        {
            var alliance = alliances[i];
            var relationText = alliance.IsLocalAlliance ? "我方" : PlayerRelationText(alliance.Relation);
            var mapClusterText = alliance.MainMapVisionCluster.HasValue
                ? $"地图主簇:{alliance.MainMapVisionCluster.Value.PointCount}点 {FormatPosition(alliance.MainMapVisionCluster.Value.Center)}"
                : "地图主簇:暂无";
            ImGui.Text($"{alliance.Name}({relationText}) 可见:{alliance.VisiblePlayerCount} 地图:{alliance.MapVisionPointCount} 活:{alliance.AliveCount} 死:{alliance.DeadCount} 残血:{alliance.LowHpCount} 咏唱:{alliance.CastingCount} 战意:{alliance.BattleHighCount}/狂热{alliance.BattleFeverCount} 防:{alliance.GuardingCount} 控:{alliance.CrowdControlledCount} 斩:{alliance.ExecutableCount} 雪:{alliance.SnowBlessingCount} {mapClusterText}");
        }
    }

    private static void DrawBattleHighPreview(BattlefieldTeamSituationSnapshot situation)
    {
        var players = situation.FriendlyPlayers
            .Concat(situation.EnemyAlliance1Players)
            .Concat(situation.EnemyAlliance2Players)
            .Where(player => player.BattleHighLevel > 0)
            .OrderByDescending(player => player.BattleHighLevel)
            .ThenBy(player => player.DistanceToLocal)
            .Take(8)
            .ToArray();

        if (players.Length == 0)
        {
            DrawHint("战意：当前可见玩家没有战意样本。");
            return;
        }

        var text = string.Join("  ", players.Select(player =>
        {
            var side = player.Relation == BattlefieldPlayerRelation.LocalPlayer ? "我" : PlayerRelationText(player.Relation);
            var fever = player.IsBattleFever ? "战意狂热" : $"战意{player.BattleHighLevel}";
            var remaining = player.BattleHighRemainingSeconds > 0f ? $" {player.BattleHighRemainingSeconds:0}秒" : string.Empty;
            return $"{side}:{player.Name} {fever}{remaining}";
        }));
        ImGui.Text($"战意目标：{text}");
    }

    private static void DrawTacticalStatusPreview(BattlefieldTeamSituationSnapshot situation)
    {
        var players = situation.FriendlyPlayers
            .Concat(situation.EnemyAlliance1Players)
            .Concat(situation.EnemyAlliance2Players)
            .Where(player => player.TacticalStatuses.Length > 0 && player.TacticalStatuses.Any(IsImportantTacticalStatus))
            .OrderByDescending(player => TacticalStatusPreviewPriority(player))
            .ThenBy(player => player.DistanceToLocal)
            .Take(8)
            .ToArray();

        if (players.Length == 0)
        {
            DrawHint("战术状态：当前没有防御/被控/无敌/雪精祝福样本。低血收割只作为评分，不伪装成状态。");
            return;
        }

        var text = string.Join("  ", players.Select(player =>
        {
            var side = player.Relation == BattlefieldPlayerRelation.LocalPlayer ? "我" : PlayerRelationText(player.Relation);
            var labels = string.Join("/", player.TacticalStatuses
                .Where(IsImportantTacticalStatus)
                .Select(status => status.Label)
                .Distinct()
                .Take(3));
            return $"{side}:{player.Name} {labels}";
        }));
        ImGui.Text($"战术状态：{text}");
    }

    private static bool IsImportantTacticalStatus(BattlefieldTacticalStatusSnapshot status)
        => status.Kind is BattlefieldTacticalStatusKind.Guarding
            or BattlefieldTacticalStatusKind.CrowdControlled
            or BattlefieldTacticalStatusKind.Invulnerable
            or BattlefieldTacticalStatusKind.SnowBlessing;

    private static int TacticalStatusPreviewPriority(BattlefieldPlayerSnapshot player)
    {
        if (player.IsInvulnerable)
            return 60;
        if (player.HasSnowBlessing)
            return 55;
        if (player.IsExecutable)
            return 50;
        if (player.IsCrowdControlled)
            return 45;
        if (player.IsGuarding)
            return 35;
        return 0;
    }

    private static void DrawEnemyClusterPreview(BattlefieldEnemyClusterSnapshot[] clusters)
    {
        if (clusters.Length == 0)
        {
            DrawHint("敌方聚类：当前没有可用敌方聚类样本。");
            return;
        }

        var count = Math.Min(clusters.Length, 4);
        for (var i = 0; i < count; i++)
        {
            var cluster = clusters[i];
            var mainText = cluster.IsMainCluster ? "主团" : "分簇";
            ImGui.Text($"敌方{mainText}#{cluster.ClusterId} {cluster.AllianceName} 样本:{cluster.Count} 半径:{cluster.Radius:0} 距主团:{cluster.SeparationFromMain:0} 来源:{cluster.SourceText} 中心:{FormatPosition(cluster.Center)}");
        }
    }

    private static void DrawKnowledgePreview(FrontlineKnowledgeSnapshot knowledge)
    {
        ImGui.Spacing();
        DrawGroupTitle("前线知识库");
        DrawHint(knowledge.SummaryText);

        if (knowledge.BattlefieldProfile.MaxPlayers > 0)
            ImGui.Text($"战场规模：{knowledge.BattlefieldProfile.Description}");
        if (knowledge.BattleHighTiers.Length > 0)
            ImGui.Text($"斗志昂扬：{FormatBattleHighTiers(knowledge.BattleHighTiers)}");
        if (knowledge.LimitBreakRankRules.Length > 0)
            ImGui.Text($"极限技排名速度：{FormatLimitBreakRankRules(knowledge.LimitBreakRankRules)}");
        if (knowledge.JobAdjustments.Length > 0)
            ImGui.Text($"职业补正：{FormatJobAdjustmentSummary(knowledge.JobAdjustments)}");
        if (knowledge.DefenseInteractionSkills.Length > 0)
            ImGui.Text($"防御交互：{FormatDefenseInteractions(knowledge.DefenseInteractionSkills)}");
        if (knowledge.KeySkillRules.Length > 0)
            ImGui.Text($"关键技能库：{FormatKeySkillRules(knowledge.KeySkillRules)}");
        if (knowledge.CommanderMacroIntents.Length > 0)
            ImGui.Text($"指挥宏意图：{FormatCommanderMacroIntents(knowledge.CommanderMacroIntents)}");

        var mapKnowledge = knowledge.CurrentMap ?? knowledge.KnownMaps.FirstOrDefault();
        if (mapKnowledge != null && mapKnowledge.VictoryScore > 0)
            DrawMapKnowledgePreview(mapKnowledge, knowledge.CurrentMap == null);

        var ruleCount = Math.Min(knowledge.GlobalRules.Length, 3);
        for (var i = 0; i < ruleCount; i++)
        {
            var rule = knowledge.GlobalRules[i];
            ImGui.Text($"{rule.Title}：{rule.Detail}");
        }

        var hintCount = Math.Min(knowledge.DecisionHints.Length, 2);
        for (var i = 0; i < hintCount; i++)
        {
            var hint = knowledge.DecisionHints[i];
            ImGui.TextColored(new Vector4(0.85f, 0.76f, 0.48f, 1f), $"提示：{hint.Trigger}");
            ImGui.Text($"{hint.Recommendation} 原因：{hint.Reason}");
        }
    }

    private static void DrawMapKnowledgePreview(FrontlineMapKnowledgeSnapshot map, bool isKnownMapPreview)
    {
        var prefix = isKnownMapPreview ? "已录入地图" : "当前地图";
        ImGui.TextColored(new Vector4(0.50f, 0.78f, 1f, 1f), $"{prefix}：{map.Name}（{map.RuleSetName}）");
        ImGui.Text($"胜利条件：{map.VictoryScore} 分；{map.PrimaryObjective}");

        if (map.BaseCaptureScores.Length > 0)
            ImGui.Text($"据点收益：{FormatBaseCaptureScores(map.BaseCaptureScores)}");
        if (map.ObjectiveRankScores.Length > 0)
            ImGui.Text($"目标等级：{FormatObjectiveRankScores(map.ObjectiveRankScores)}");
        if (map.DestructibleObjectiveRules.Length > 0)
            ImGui.Text($"可破坏目标：{FormatDestructibleObjectiveRules(map.DestructibleObjectiveRules)}");
        if (map.ScoreSources.Length > 0)
            ImGui.Text($"主要得分：{FormatScoreSources(map.ScoreSources)}");
        if (map.TimedSpawns.Length > 0)
            ImGui.Text($"刷新节奏：{FormatTimedSpawns(map.TimedSpawns)}");
        if (map.PhaseRules.Length > 0)
            ImGui.Text($"阶段规则：{FormatPhaseRules(map.PhaseRules)}");
        if (map.LocationRules.Length > 0)
            ImGui.Text($"点位规则：{FormatLocationRules(map.LocationRules)}");
        if (map.WeatherRules.Length > 0)
            ImGui.Text($"天气规则：{FormatWeatherRules(map.WeatherRules)}");
        if (map.TeleportRules.Length > 0)
            ImGui.Text($"传送机制：{FormatTeleportRules(map.TeleportRules)}");

        var hintCount = Math.Min(map.DecisionHints.Length, 2);
        for (var i = 0; i < hintCount; i++)
        {
            var hint = map.DecisionHints[i];
            ImGui.TextColored(new Vector4(0.85f, 0.76f, 0.48f, 1f), $"地图提示：{hint.Trigger}");
            ImGui.Text($"{hint.Recommendation} 原因：{hint.Reason}");
        }
    }

    private static string FormatBattleHighTiers(FrontlineBattleHighTierSnapshot[] tiers)
        => string.Join("  ", tiers.Select(tier => $"{tier.MinBattleHigh}-{tier.MaxBattleHigh}:{tier.Level}+{tier.DamageAndHealingBonusPercent}%"));

    private static string FormatLimitBreakRankRules(FrontlineLimitBreakRankRuleSnapshot[] rules)
        => string.Join("  ", rules.Select(rule => $"{rule.RankName}:{FormatSignedPercent(rule.ChargeSpeedModifierPercent)}"));

    private static string FormatJobAdjustmentSummary(FrontlineJobAdjustmentSnapshot[] jobs)
    {
        var hardestTargets = jobs
            .OrderBy(job => job.IncomingDamageModifierPercent)
            .ThenByDescending(job => job.MaxHp)
            .Take(3)
            .Select(job => $"{job.JobName}{job.IncomingDamageModifierPercent}%");
        var fastestLimitBreaks = jobs
            .OrderBy(job => job.FrontlineLimitBreakChargeSeconds)
            .Take(3)
            .Select(job => $"{job.JobName}{job.FrontlineLimitBreakChargeSeconds}秒");
        return $"已录入 {jobs.Length} 职业；最硬:{string.Join("、", hardestTargets)}；最快极限技:{string.Join("、", fastestLimitBreaks)}";
    }

    private static string FormatDefenseInteractions(FrontlineDefenseInteractionSkillSnapshot[] skills)
    {
        var groupBreaks = FormatDefenseInteractionGroup(skills, "解除防御", "群体");
        var singleBreaks = FormatDefenseInteractionGroup(skills, "解除防御", "单体");
        var bypasses = FormatDefenseInteractionGroup(skills, "无视防御减伤", "伤害");
        return $"群体破防:{groupBreaks}；单体破防:{singleBreaks}；无视防御:{bypasses}";
    }

    private static string FormatDefenseInteractionGroup(FrontlineDefenseInteractionSkillSnapshot[] skills, string interactionType, string targetScope)
    {
        var items = skills
            .Where(skill => skill.InteractionType == interactionType && skill.TargetScope == targetScope)
            .Select(skill => $"{skill.JobName}/{skill.SkillName}")
            .ToArray();

        return items.Length == 0 ? "暂无" : string.Join("、", items);
    }

    private static string FormatKeySkillRules(FrontlineKeySkillRuleSnapshot[] rules)
    {
        var source = rules.Select(rule => rule.SourceName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Wiki";
        var topKinds = rules
            .GroupBy(rule => rule.Kind)
            .OrderByDescending(group => group.Count())
            .Take(4)
            .Select(group => $"{KeySkillKindText(group.Key)}×{group.Count()}");

        return $"{rules.Length}项，来源：{source}，{string.Join("  ", topKinds)}";
    }

    private static string FormatCommanderMacroIntents(FrontlineCommanderMacroIntentSnapshot[] intents)
    {
        var topTags = intents
            .SelectMany(intent => intent.Tags)
            .Where(tag => !string.Equals(tag, "宏意图", StringComparison.Ordinal))
            .GroupBy(tag => tag)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Take(6)
            .Select(group => $"{group.Key}×{group.Count()}");

        return $"{intents.Length}类，覆盖 {string.Join("  ", topTags)}";
    }

    private static string FormatSignedPercent(int value)
        => value > 0 ? $"+{value}%" : $"{value}%";

    private static string FormatBaseCaptureScores(FrontlineBaseCaptureScoreSnapshot[] scores)
        => string.Join("  ", scores.Select(score => $"{score.CapturedBaseCount}点={score.ScorePerTick}/{score.TickSeconds}秒"));

    private static string FormatObjectiveRankScores(FrontlineObjectiveRankScoreSnapshot[] scores)
        => string.Join("  ", scores.Select(score => $"{score.RankName}:{score.TotalScore}分,{score.ScorePerTick}/{score.TickSeconds}秒×{score.TickCount}"));

    private static string FormatDestructibleObjectiveRules(FrontlineDestructibleObjectiveRuleSnapshot[] rules)
        => string.Join("  ", rules.Take(4).Select(rule =>
        {
            var countText = rule.TotalCount > 0 ? $"共{rule.TotalCount}个" : string.Empty;
            var activeText = rule.MaxActiveCount > 0 ? $"，同时{rule.MaxActiveCount}个" : string.Empty;
            var hpText = rule.MaxHp > 0 ? $"，血量{rule.MaxHp / 10000}万" : string.Empty;
            var scoreText = rule.ScoreValue > 0 ? $"，{rule.ScoreValue}分" : string.Empty;
            var warningText = rule.FirstWarningElapsedSeconds.HasValue ? $"，预告{FormatElapsedTime(rule.FirstWarningElapsedSeconds.Value)}" : string.Empty;
            var activationText = rule.FirstActivationElapsedSeconds.HasValue ? $"，启动{FormatElapsedTime(rule.FirstActivationElapsedSeconds.Value)}" : string.Empty;
            var respawnText = rule.RespawnSeconds.HasValue ? $"，复原{rule.RespawnSeconds.Value / 60}分钟" : string.Empty;
            return $"{rule.Name}:{countText}{activeText}{hpText}{scoreText}{warningText}{activationText}{respawnText}";
        }));

    private static string FormatScoreSources(FrontlineScoreSourceSnapshot[] sources)
        => string.Join("  ", sources.Take(6).Select(source => $"{source.SourceName}:{FormatScoreDelta(source.OwnScoreDelta, source.EnemyScoreDelta)}"));

    private static string FormatScoreDelta(int? ownScoreDelta, int? enemyScoreDelta)
    {
        if (!ownScoreDelta.HasValue && !enemyScoreDelta.HasValue)
            return "按规则";

        var ownText = ownScoreDelta.HasValue ? $"+{ownScoreDelta.Value}" : "0";
        if (!enemyScoreDelta.HasValue)
            return ownText;

        return $"{ownText}/{enemyScoreDelta.Value}";
    }

    private static string FormatTimedSpawns(FrontlineTimedSpawnRuleSnapshot[] spawns)
        => string.Join("  ", spawns.Select(spawn =>
        {
            var firstText = spawn.FirstSpawnOffsetSeconds.HasValue ? $"首刷{spawn.FirstSpawnOffsetSeconds.Value}秒" : "循环刷新";
            var warningText = spawn.WarningSeconds > 0 ? $",预告{spawn.WarningSeconds}秒" : string.Empty;
            var durationText = spawn.DurationSeconds > 0 ? $",持续{spawn.DurationSeconds}秒" : ",持续按机制";
            return $"{spawn.Name}:{firstText}{warningText}{durationText}";
        }));

    private static string FormatPhaseRules(FrontlineMapPhaseRuleSnapshot[] rules)
        => string.Join("  ", rules.Select(rule =>
        {
            var endText = rule.EndElapsedSeconds.HasValue ? $"{FormatElapsedTime(rule.EndElapsedSeconds.Value)}" : "结束";
            var countText = rule.MaxActiveObjectives.HasValue ? $"最多{rule.MaxActiveObjectives.Value}个" : "数量按机制";
            var rankText = string.IsNullOrWhiteSpace(rule.MinimumObjectiveRank) ? string.Empty : $"，至少{rule.MinimumObjectiveRank}级";
            return $"{rule.Name}:{FormatElapsedTime(rule.StartElapsedSeconds)}-{endText}，{countText}{rankText}";
        }));

    private static string FormatLocationRules(FrontlineMapLocationRuleSnapshot[] rules)
        => string.Join("  ", rules.Take(4).Select(rule =>
        {
            var locationText = rule.LocationIds.Length > 0 ? string.Join("/", rule.LocationIds) : "全图";
            var rankText = string.IsNullOrWhiteSpace(rule.MinimumObjectiveRank) ? string.Empty : $"，{rule.MinimumObjectiveRank}+";
            var timeText = FormatOptionalTimeWindow(rule.StartElapsedSeconds, rule.EndElapsedSeconds);
            return $"{rule.Name}:{locationText}{rankText}{timeText}";
        }));

    private static string FormatWeatherRules(FrontlineWeatherRuleSnapshot[] rules)
        => string.Join("  ", rules.Select(rule => $"{rule.Name}:持续{FormatDuration(rule.DurationSeconds)}，{rule.Detail}"));

    private static string FormatTeleportRules(FrontlineTeleportRuleSnapshot[] rules)
        => string.Join("  ", rules.Select(rule => $"{rule.Name}:提前{rule.ActivationLeadSeconds}秒启动，传送后{rule.InvulnerabilitySecondsAfterTeleport}秒无敌"));

    private static string FormatElapsedTime(int seconds)
        => $"{seconds / 60:D2}:{seconds % 60:D2}";

    private static string FormatDuration(int seconds)
    {
        if (seconds % 60 == 0)
            return $"{seconds / 60}分钟";

        return $"{seconds / 60}分{seconds % 60}秒";
    }

    private static string FormatUnixMs(long unixMs)
        => unixMs <= 0
            ? "-"
            : DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime.ToString("MM-dd HH:mm:ss");

    private static string FormatOptionalTimeWindow(int? startSeconds, int? endSeconds)
    {
        if (!startSeconds.HasValue && !endSeconds.HasValue)
            return string.Empty;

        var startText = startSeconds.HasValue ? FormatElapsedTime(startSeconds.Value) : "开始";
        var endText = endSeconds.HasValue ? FormatElapsedTime(endSeconds.Value) : "结束";
        return $"({startText}-{endText})";
    }

    private static void DrawCompositionLine(string label, BattlefieldCompositionSnapshot[] items, int maxItems)
    {
        ImGui.Text($"{label}：{FormatComposition(items, maxItems)}");
    }

    private static string FormatComposition(BattlefieldCompositionSnapshot[] items, int maxItems)
    {
        if (items.Length == 0)
            return "暂无";

        return string.Join("  ", items.Take(maxItems).Select(item => $"{item.Name}×{item.Count}"));
    }

    private static void DrawFocusPreview(string title, BattlefieldFocusTargetSnapshot[] focusTargets)
    {
        if (focusTargets.Length == 0)
        {
            DrawHint($"{title}：暂无明显集火");
            return;
        }

        ImGui.TextColored(new Vector4(0.85f, 0.76f, 0.48f, 1f), title);
        var count = Math.Min(focusTargets.Length, 3);
        for (var i = 0; i < count; i++)
        {
            var target = focusTargets[i];
            var pressureCount = Math.Max(target.AttackerCount, target.CasterCount);
            var hpText = target.MaxHp > 0 ? $"{target.HpPercent:0}%" : "血量未知";
            var sources = target.SourceNames.Length > 0
                ? string.Join("、", target.SourceNames.Take(4))
                : "来源未知";
            ImGui.Text($"{target.TargetName}({target.TargetJobName})  血量:{hpText}  锁定:{pressureCount}人  咏唱:{target.CasterCount}人  来源:{sources}");
        }
    }

    private static void DrawMapEventPreview(BattlefieldMapEventSnapshot[] events)
    {
        if (events.Length == 0)
        {
            DrawHint("地图事件预览：当前没有采集到倒计时、血量或据点分值事件。");
            return;
        }

        var count = Math.Min(events.Length, 4);
        for (var i = 0; i < count; i++)
        {
            var item = events[i];
            ImGui.Text($"{MapEventKindText(item.Kind)} 图标:{item.IconId} 坐标:{FormatPosition(item.Position)} 信息:{MapEventValueText(item)}");
        }
    }

    private static void DrawMapObjectivePreview(BattlefieldMapObjectiveSnapshot[] objectives)
    {
        ImGui.Spacing();
        DrawGroupTitle("实时地图目标");
        if (objectives.Length == 0)
        {
            DrawHint("当前没有识别到地图目标。需要区域地图事件、目标对象或战场目标进入客户端可见范围。");
            return;
        }

        var count = Math.Min(objectives.Length, 8);
        if (!ImGui.BeginTable("##MapObjectiveTable", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("目标");
        ImGui.TableSetupColumn("状态");
        ImGui.TableSetupColumn("数值");
        ImGui.TableSetupColumn("攻击/来源");
        ImGui.TableHeadersRow();

        for (var i = 0; i < count; i++)
        {
            var objective = objectives[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text($"{objective.Name}  {MapObjectiveCategoryText(objective.Category)}");
            ImGui.TableNextColumn();
            ImGui.Text($"{MapObjectiveStateText(objective.State)}  归属:{objective.OwnershipText}");
            ImGui.TableNextColumn();
            ImGui.Text(MapObjectiveValueText(objective));
            ImGui.TableNextColumn();
            ImGui.Text(MapObjectivePressureText(objective));
        }

        ImGui.EndTable();

        var ice = objectives.FirstOrDefault(objective => objective.Category == BattlefieldMapObjectiveCategory.Ice);
        if (!string.IsNullOrWhiteSpace(ice.EnmitySourceText))
            DrawHint($"冰仇恨/贡献来源：{ice.EnmitySourceText}");
    }

    private static void DrawClusterPreview(BattlefieldPlayerClusterSnapshot[] clusters)
    {
        if (clusters.Length == 0)
        {
            DrawHint("人群预览：当前没有形成可识别的人群。");
            return;
        }

        var count = Math.Min(clusters.Length, 4);
        for (var i = 0; i < count; i++)
        {
            var cluster = clusters[i];
            ImGui.Text($"{PlayerRelationText(cluster.Relation)}人群 {BattalionText(cluster.Battalion)} 人数:{cluster.PlayerCount} 死亡:{cluster.DeadCount} 咏唱:{cluster.CastingCount} 距离:{cluster.DistanceToLocal:0} 坐标:{FormatPosition(cluster.Center)}");
        }
    }

    private static string BattalionText(byte? battalion)
        => battalion switch
        {
            0 => "黑涡团",
            1 => "双蛇党",
            2 => "恒辉队",
            _ => "未知阵营",
        };

    private static string PlayerRelationText(BattlefieldPlayerRelation relation)
        => relation switch
        {
            BattlefieldPlayerRelation.LocalPlayer => "自己",
            BattlefieldPlayerRelation.Friendly => "友方",
            BattlefieldPlayerRelation.Enemy => "敌方",
            _ => "未知",
        };

    private static string LimitBreakThreatLevelText(BattlefieldLimitBreakThreatLevel level)
        => level switch
        {
            BattlefieldLimitBreakThreatLevel.Critical => "极高",
            BattlefieldLimitBreakThreatLevel.High => "高",
            BattlefieldLimitBreakThreatLevel.Medium => "中",
            _ => "低",
        };

    private static string KeySkillKindText(BattlefieldKeySkillKind kind)
        => kind switch
        {
            BattlefieldKeySkillKind.Engage => "开团",
            BattlefieldKeySkillKind.CrowdControl => "控制",
            BattlefieldKeySkillKind.GuardBreak => "破防",
            BattlefieldKeySkillKind.DefensePierce => "穿防",
            BattlefieldKeySkillKind.Burst => "爆发",
            BattlefieldKeySkillKind.Execute => "斩杀",
            BattlefieldKeySkillKind.Defensive => "防御",
            BattlefieldKeySkillKind.Purify => "净化",
            BattlefieldKeySkillKind.Invulnerability => "无敌",
            BattlefieldKeySkillKind.AreaPressure => "范围压制",
            BattlefieldKeySkillKind.Support => "支援",
            _ => "技能",
        };

    private static string RelationText(BattlefieldPlayerRelation relation)
        => relation switch
        {
            BattlefieldPlayerRelation.LocalPlayer => "自己",
            BattlefieldPlayerRelation.Friendly => "我方",
            BattlefieldPlayerRelation.Enemy => "敌方",
            _ => "未知",
        };

    private static string CommandKindText(BattlefieldCommandKind kind)
        => kind switch
        {
            BattlefieldCommandKind.Regroup => "集合",
            BattlefieldCommandKind.Retreat => "脱出",
            BattlefieldCommandKind.Disengage => "收追",
            BattlefieldCommandKind.Rotate => "转点",
            BattlefieldCommandKind.AttackObjective => "打目标",
            BattlefieldCommandKind.ContestObjective => "反抢",
            BattlefieldCommandKind.DefendObjective => "守点",
            BattlefieldCommandKind.AbandonObjective => "侧压",
            BattlefieldCommandKind.Split => "分队",
            BattlefieldCommandKind.FocusTarget => "集火",
            BattlefieldCommandKind.ProtectTarget => "保护",
            BattlefieldCommandKind.Spread => "散开",
            BattlefieldCommandKind.Hold => "稳住",
            BattlefieldCommandKind.Detour => "侧线",
            BattlefieldCommandKind.PressureSide => "牵制",
            BattlefieldCommandKind.Wait => "压位",
            _ => "指令",
        };

    private static string EnemyIntentKindText(BattlefieldEnemyIntentKind kind)
        => kind switch
        {
            BattlefieldEnemyIntentKind.Pincer => "夹击",
            BattlefieldEnemyIntentKind.Rotate => "转点",
            BattlefieldEnemyIntentKind.Engage => "开团",
            BattlefieldEnemyIntentKind.RetreatBait => "诱追",
            BattlefieldEnemyIntentKind.ObjectiveRush => "抢目标",
            BattlefieldEnemyIntentKind.Hold => "固守",
            BattlefieldEnemyIntentKind.Flank => "绕侧",
            _ => "未知",
        };

    private static string TeamRoleInsightKindText(BattlefieldTeamRoleInsightKind kind)
        => kind switch
        {
            BattlefieldTeamRoleInsightKind.ProtectHighBattleHigh => "高战意保护",
            BattlefieldTeamRoleInsightKind.FrontlineOpenPath => "前排开路",
            BattlefieldTeamRoleInsightKind.BacklineUnderDive => "后排被切",
            BattlefieldTeamRoleInsightKind.ControlWindow => "控场窗口",
            BattlefieldTeamRoleInsightKind.ProtectFocusTarget => "保护被集火",
            BattlefieldTeamRoleInsightKind.BurstWindow => "爆发窗口",
            _ => "职责",
        };

    private static string CommandPriorityText(BattlefieldCommandSnapshot command)
    {
        var value = Math.Clamp(command.Score * 0.46f + command.Urgency * 0.54f + CommandKindPriorityBonus(command.Kind), 0f, 100f);
        return value >= 88f ? "最高" : value >= 74f ? "高" : value >= 58f ? "中" : "低";
    }

    private static string ActionTypeText(BattlefieldActionType type)
        => type switch
        {
            BattlefieldActionType.Rotate => "转点",
            BattlefieldActionType.DefendObjective => "守点",
            BattlefieldActionType.ContestObjective => "抢点",
            BattlefieldActionType.AbandonObjective => "侧压",
            BattlefieldActionType.AttackIce => "打冰",
            BattlefieldActionType.TouchObjective => "摸点",
            BattlefieldActionType.InterruptTouch => "打断摸点",
            BattlefieldActionType.Engage => "接团",
            BattlefieldActionType.Retreat => "脱出",
            BattlefieldActionType.ReturnToBase => "回出生点",
            BattlefieldActionType.Flank => "夹击",
            BattlefieldActionType.WrapBehind => "绕后",
            BattlefieldActionType.BacklinePressure => "压后排",
            BattlefieldActionType.FocusTarget => "集火",
            BattlefieldActionType.ProtectHighBattleHigh => "保护高战意",
            BattlefieldActionType.Regroup => "集合",
            BattlefieldActionType.Spread => "散开",
            BattlefieldActionType.Detour => "侧线",
            BattlefieldActionType.Hold => "守住",
            BattlefieldActionType.Wait => "压位",
            _ => "行动",
        };

    private static string FormatCountdown(int? seconds)
        => seconds.HasValue ? FormatDuration(Math.Max(0, seconds.Value)) : "-";

    private static float CommandKindPriorityBonus(BattlefieldCommandKind kind)
        => kind switch
        {
            BattlefieldCommandKind.Retreat => 10f,
            BattlefieldCommandKind.Disengage => 8f,
            BattlefieldCommandKind.Spread => 7f,
            BattlefieldCommandKind.ProtectTarget => 5f,
            BattlefieldCommandKind.FocusTarget => 4f,
            BattlefieldCommandKind.AbandonObjective => 4f,
            BattlefieldCommandKind.ContestObjective => 3f,
            BattlefieldCommandKind.Regroup => 2f,
            _ => 0f,
        };

    private static string FormatScoreWithVictory(int score, int victoryScore)
        => victoryScore > 0 ? $"{score}/{victoryScore}" : score.ToString();

    private static string FormatScoreTrend(BattlefieldAllianceScoreSnapshot alliance)
        => $"{FormatSignedNumber(alliance.ScoreDelta30s)}，{alliance.ScorePerSecond30s:0.0}/秒";

    private static string FormatSignedNumber(int value)
        => value > 0 ? $"+{value}" : value.ToString();

    private static string FormatOptionalCountdown(int? seconds)
        => seconds.HasValue ? $"（{seconds.Value / 60:D2}:{seconds.Value % 60:D2}）" : string.Empty;

    private static string AnnouncementKindText(BattlefieldAnnouncementKind kind)
        => kind switch
        {
            BattlefieldAnnouncementKind.WeatherWarning => "天气预告",
            BattlefieldAnnouncementKind.WeatherStarted => "天气开始",
            BattlefieldAnnouncementKind.WeatherEnded => "天气结束",
            BattlefieldAnnouncementKind.ObjectiveWarning => "目标预告",
            BattlefieldAnnouncementKind.ObjectiveAvailable => "目标可控",
            BattlefieldAnnouncementKind.ObjectiveControlled => "目标控制",
            BattlefieldAnnouncementKind.ObjectiveReleased => "目标失效",
            BattlefieldAnnouncementKind.ObjectiveOther => "目标通告",
            _ => "通告",
        };

    private static string ChatEventKindText(BattlefieldChatEventKind kind)
        => kind switch
        {
            BattlefieldChatEventKind.Kill => "击杀",
            BattlefieldChatEventKind.BattleHigh => "战意",
            BattlefieldChatEventKind.ObjectiveCaptured => "占领",
            BattlefieldChatEventKind.ObjectiveLost => "失去",
            BattlefieldChatEventKind.ObjectiveContested => "争夺",
            BattlefieldChatEventKind.ObjectiveOther => "目标",
            _ => "事件",
        };

    private static string MapEventKindText(BattlefieldMapEventKind kind)
        => kind switch
        {
            BattlefieldMapEventKind.Countdown => "倒计时",
            BattlefieldMapEventKind.Health => "血量",
            BattlefieldMapEventKind.ControlPoint => "据点",
            _ => "未知",
        };

    private static string MapEventValueText(BattlefieldMapEventSnapshot item)
    {
        if (item.CountdownSeconds.HasValue)
            return $"{item.CountdownSeconds.Value / 60:D2}:{item.CountdownSeconds.Value % 60:D2}";
        if (item.HpPercent.HasValue)
            return $"{item.HpPercent.Value}%";
        if (item.ScoreValue.HasValue)
            return $"{item.ScoreValue.Value} 分";
        if (!string.IsNullOrWhiteSpace(item.Tooltip))
            return item.Tooltip;

        return "-";
    }

    private static string MapObjectiveCategoryText(BattlefieldMapObjectiveCategory category)
        => category switch
        {
            BattlefieldMapObjectiveCategory.Base => "据点",
            BattlefieldMapObjectiveCategory.Tomelith => "石文",
            BattlefieldMapObjectiveCategory.Ice => "冰",
            BattlefieldMapObjectiveCategory.Ovoo => "无垢",
            BattlefieldMapObjectiveCategory.StrategicPoint => "沃刻目标",
            BattlefieldMapObjectiveCategory.Monster => "机制",
            _ => "未知",
        };

    private static string MapObjectiveStateText(BattlefieldMapObjectiveState state)
        => state switch
        {
            BattlefieldMapObjectiveState.Inactive => "未激活",
            BattlefieldMapObjectiveState.Warning => "预告",
            BattlefieldMapObjectiveState.Active => "可处理",
            BattlefieldMapObjectiveState.Controlled => "已归属",
            BattlefieldMapObjectiveState.Contested => "争夺中",
            BattlefieldMapObjectiveState.Destroyed => "已破坏",
            _ => "未知",
        };

    private static string MapObjectiveValueText(BattlefieldMapObjectiveSnapshot objective)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(objective.RankName))
            parts.Add($"等级:{objective.RankName}");
        if (objective.ScoreValue.HasValue)
            parts.Add($"价值:{objective.ScoreValue.Value}");
        if (objective.RemainingSeconds.HasValue)
            parts.Add($"剩余:{objective.RemainingSeconds.Value / 60:D2}:{objective.RemainingSeconds.Value % 60:D2}({objective.RemainingSource})");
        else if (!string.IsNullOrWhiteSpace(objective.RemainingSource) && objective.RemainingSource != "未知")
            parts.Add($"剩余:{objective.RemainingSource}");
        if (objective.HpPercent.HasValue)
        {
            var hpText = objective.CurrentHp.HasValue && objective.MaxHp.HasValue
                ? $"血量:{objective.CurrentHp.Value}/{objective.MaxHp.Value}({objective.HpPercent.Value}%)"
                : $"血量:{objective.HpPercent.Value}%";
            parts.Add(hpText);
        }
        if (objective.RecentHpLoss.HasValue)
            parts.Add($"近伤:{objective.RecentHpLoss.Value}({objective.RecentHpLossPerSecond:0}/s)");

        return parts.Count == 0 ? "暂无数值" : string.Join("  ", parts);
    }

    private static string MapObjectivePressureText(BattlefieldMapObjectiveSnapshot objective)
    {
        var attackText = objective.IsBeingAttacked ? "被攻击:是" : "被攻击:否";
        var focusText = objective.IsBeingFocused
            ? $"集火:是 锁定:{objective.AttackerCount} 读条:{objective.CasterCount}"
            : $"集火:否 锁定:{objective.AttackerCount} 读条:{objective.CasterCount}";
        var sideText = $"我方:{objective.FriendlyAttackerCount} 敌方:{objective.EnemyAttackerCount}";
        var source = objective.Contributors.Length > 0
            ? $"贡献:{string.Join("、", objective.Contributors.Take(3).Select(FormatObjectiveContributor))}"
            : $"置信:{objective.Confidence:0.00}";
        return $"{attackText}  {focusText}  {sideText}  {source}";
    }

    private static string FormatObjectiveContributor(BattlefieldObjectiveContributionSnapshot contributor)
    {
        var side = contributor.Relation switch
        {
            BattlefieldPlayerRelation.LocalPlayer => "我",
            BattlefieldPlayerRelation.Friendly => "友",
            BattlefieldPlayerRelation.Enemy => "敌",
            _ => "?"
        };
        return $"{contributor.PlayerName}({side}/{contributor.JobName}/{contributor.EstimatedContributionWeight:0.0})";
    }

    private static string FormatPosition(Vector3 position)
        => $"{position.X:0.0},{position.Y:0.0},{position.Z:0.0}";

    private static Vector4 GetAllianceColor(uint allianceId)
    {
        return allianceId switch
        {
            1 => new Vector4(1f, 0.30f, 0.30f, 1f),
            2 => new Vector4(0.95f, 0.75f, 0.15f, 1f),
            _ => new Vector4(0.30f, 0.62f, 1f, 1f),
        };
    }

    private static Vector4 PriorityColor(float value)
        => value >= 75f
            ? new Vector4(0.36f, 0.96f, 0.52f, 1f)
            : value >= 55f
                ? new Vector4(0.95f, 0.82f, 0.35f, 1f)
                : new Vector4(0.72f, 0.72f, 0.76f, 1f);

    private static Vector4 RiskColor(float value)
        => value >= 75f
            ? new Vector4(1f, 0.24f, 0.24f, 1f)
            : value >= 55f
                ? new Vector4(1f, 0.58f, 0.22f, 1f)
                : value >= 35f
                    ? new Vector4(0.95f, 0.82f, 0.35f, 1f)
                    : new Vector4(0.48f, 0.90f, 0.62f, 1f);

    private static Vector4 HeatPointColor(float value)
        => value >= 75f
            ? new Vector4(1f, 0.20f, 0.16f, 1f)
            : value >= 55f
                ? new Vector4(1f, 0.50f, 0.18f, 1f)
                : new Vector4(0.95f, 0.78f, 0.26f, 1f);

    private static string AnnotationKindText(MapAnnotationKind kind)
        => kind switch
        {
            MapAnnotationKind.Spawn => "出生",
            MapAnnotationKind.Objective => "目标",
            MapAnnotationKind.Choke => "卡口",
            MapAnnotationKind.JumpPad => "跳台",
            MapAnnotationKind.Teleporter => "传送",
            MapAnnotationKind.HighGround => "高地",
            MapAnnotationKind.LowGround => "低地",
            MapAnnotationKind.Danger => "危险",
            MapAnnotationKind.Rotation => "转点",
            MapAnnotationKind.Flank => "夹击",
            MapAnnotationKind.RoutePoint => "路径",
            MapAnnotationKind.Bridge => "桥面",
            MapAnnotationKind.Underpass => "桥洞",
            _ => "备注",
        };

    private static Vector4 AnnotationKindColor(MapAnnotationKind kind)
        => kind switch
        {
            MapAnnotationKind.Spawn => new Vector4(0.35f, 0.72f, 1f, 1f),
            MapAnnotationKind.Objective => new Vector4(1f, 0.86f, 0.22f, 1f),
            MapAnnotationKind.Choke => new Vector4(1f, 0.45f, 0.20f, 1f),
            MapAnnotationKind.JumpPad => new Vector4(0.54f, 0.95f, 1f, 1f),
            MapAnnotationKind.Teleporter => new Vector4(0.62f, 0.48f, 1f, 1f),
            MapAnnotationKind.HighGround => new Vector4(0.42f, 0.92f, 0.48f, 1f),
            MapAnnotationKind.LowGround => new Vector4(0.62f, 0.62f, 0.66f, 1f),
            MapAnnotationKind.Danger => new Vector4(1f, 0.22f, 0.24f, 1f),
            MapAnnotationKind.Rotation => new Vector4(0.38f, 0.92f, 0.58f, 1f),
            MapAnnotationKind.Flank => new Vector4(1f, 0.56f, 0.18f, 1f),
            MapAnnotationKind.RoutePoint => new Vector4(0.35f, 0.82f, 1f, 1f),
            MapAnnotationKind.Bridge => new Vector4(0.76f, 0.70f, 0.58f, 1f),
            MapAnnotationKind.Underpass => new Vector4(0.20f, 0.82f, 0.78f, 1f),
            _ => new Vector4(0.92f, 0.92f, 0.96f, 1f),
        };

    private static uint AnnotationRouteColor(MapAnnotationPoint from, MapAnnotationPoint to)
    {
        var alpha = MapTacticalGraphService.IsBuiltInPoint(from) && MapTacticalGraphService.IsBuiltInPoint(to)
            ? 0.45f
            : 0.82f;

        if (from.Kind == MapAnnotationKind.Flank || to.Kind == MapAnnotationKind.Flank)
            return Color(new Vector4(1f, 0.48f, 0.18f, alpha));

        if (from.Kind == MapAnnotationKind.Rotation || to.Kind == MapAnnotationKind.Rotation)
            return Color(new Vector4(0.42f, 0.95f, 0.52f, alpha));

        return Color(new Vector4(0.35f, 0.82f, 1f, alpha));
    }

    private static uint TacticalShapeColor(MapAnnotationKind kind, int riskScore, float alpha)
    {
        var color = AnnotationKindColor(kind);
        var riskBoost = Math.Clamp(riskScore / 100f, 0f, 1f);
        if (kind == MapAnnotationKind.Danger || riskScore >= 65)
        {
            color.X = MathF.Min(1f, color.X + riskBoost * 0.18f);
            color.Y = MathF.Max(0f, color.Y - riskBoost * 0.10f);
            color.Z = MathF.Max(0f, color.Z - riskBoost * 0.10f);
        }

        color.W = alpha;
        return Color(color);
    }

    private static void DrawDirectionArrow(ImDrawListPtr drawList, Vector2 from, Vector2 to, uint color, float thickness)
    {
        var delta = to - from;
        var length = MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
        if (length < 8f)
            return;

        var dir = delta / length;
        var normal = new Vector2(-dir.Y, dir.X);
        var center = from + delta * 0.58f;
        var size = Math.Clamp(thickness * 1.8f, 7f, 18f);
        var tip = center + dir * size;
        var left = center - dir * size * 0.65f + normal * size * 0.55f;
        var right = center - dir * size * 0.65f - normal * size * 0.55f;
        drawList.AddTriangleFilled(tip, left, right, color);
        drawList.AddTriangle(tip, left, right, Color(new Vector4(0f, 0f, 0f, 0.58f)), 1.2f);
    }

    private static void DrawTacticalRegionPolygon(
        ImDrawListPtr drawList,
        MapAnnotationKind kind,
        string label,
        int riskScore,
        IReadOnlyList<Vector2> vertices,
        float fillAlpha,
        float borderAlpha)
    {
        if (vertices.Count < 3)
            return;

        var center = Vector2.Zero;
        foreach (var vertex in vertices)
            center += vertex;
        center /= vertices.Count;

        var fill = TacticalShapeColor(kind, riskScore, fillAlpha);
        var border = TacticalShapeColor(kind, riskScore, borderAlpha);
        for (var i = 0; i < vertices.Count; i++)
        {
            var next = (i + 1) % vertices.Count;
            drawList.AddTriangleFilled(center, vertices[i], vertices[next], fill);
            drawList.AddLine(vertices[i], vertices[next], border, 1.8f);
        }

        if (string.IsNullOrWhiteSpace(label))
            return;

        var radius = 0f;
        foreach (var vertex in vertices)
        {
            var delta = vertex - center;
            radius = MathF.Max(radius, MathF.Sqrt(delta.X * delta.X + delta.Y * delta.Y));
        }

        if (radius < 24f)
            return;

        drawList.AddText(center + new Vector2(6f, -7f), Color(new Vector4(1f, 1f, 1f, 0.66f)), label);
    }

    private static bool IsAreaAnnotationKind(MapAnnotationKind kind)
        => kind is MapAnnotationKind.Danger
            or MapAnnotationKind.Choke
            or MapAnnotationKind.HighGround
            or MapAnnotationKind.LowGround
            or MapAnnotationKind.Bridge
            or MapAnnotationKind.Underpass;

    private static bool IsRouteAnnotationKind(MapAnnotationKind kind)
        => kind is MapAnnotationKind.RoutePoint
            or MapAnnotationKind.Rotation
            or MapAnnotationKind.Flank
            or MapAnnotationKind.Bridge
            or MapAnnotationKind.JumpPad
            or MapAnnotationKind.Teleporter;

    private static int BuildAllAnnotationKindMask()
    {
        var mask = 0;
        foreach (var kind in Enum.GetValues<MapAnnotationKind>())
            mask |= AnnotationKindBit(kind);
        return mask;
    }

    private static int BuildAreaAnnotationKindMask()
    {
        var mask = 0;
        foreach (var kind in Enum.GetValues<MapAnnotationKind>().Where(IsAreaAnnotationKind))
            mask |= AnnotationKindBit(kind);
        return mask;
    }

    private static int BuildRouteAnnotationKindMask()
    {
        var mask = 0;
        foreach (var kind in Enum.GetValues<MapAnnotationKind>().Where(IsRouteAnnotationKind))
            mask |= AnnotationKindBit(kind);
        return mask;
    }

    private static bool IsAnnotationKindVisible(MapAnnotationKind kind, int mask)
        => (mask & AnnotationKindBit(kind)) != 0;

    private static int SetAnnotationKindVisible(int mask, MapAnnotationKind kind, bool visible)
        => visible ? mask | AnnotationKindBit(kind) : mask & ~AnnotationKindBit(kind);

    private static int AnnotationKindBit(MapAnnotationKind kind)
        => 1 << Math.Clamp((int)kind, 0, 30);

    private static bool ShouldDrawAnnotationPointLabel(MapAnnotationPoint point, bool builtIn)
    {
        if (!builtIn)
            return true;

        return point.Kind is MapAnnotationKind.Spawn
            or MapAnnotationKind.JumpPad
            or MapAnnotationKind.Teleporter
            or MapAnnotationKind.Objective;
    }

    private static uint Color(Vector4 color)
        => ImGui.ColorConvertFloat4ToU32(color);

    private static float Distance2D(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private readonly record struct OfflineFrontlineMapEntry(
        FrontlineMapType MapType,
        uint TerritoryType,
        uint FallbackMapId,
        string DisplayName);

    private readonly record struct OfflineMapMetadata(
        FrontlineMapType MapType,
        uint TerritoryType,
        uint MapId,
        string DisplayName,
        string TexturePath,
        float MapSizeScale,
        int MapOffsetX,
        int MapOffsetY,
        bool HasGameMapData);

    private readonly record struct MapCalibrationSample(
        uint TerritoryType,
        uint MapId,
        Vector3 ClickedPosition,
        Vector3 ActualPosition,
        long CreatedAtUnixMs)
    {
        public Vector3 Delta => ActualPosition - ClickedPosition;
        public float Error => Distance2D(ClickedPosition, ActualPosition);
    }

    private readonly record struct MapCalibrationCorrectionEstimate(
        int SampleCount,
        float AverageError,
        float MaxError,
        MapCoordinateCorrection Correction);

    private enum MainPage
    {
        CombatHud,
        Review,
        Radar,
        MapEditor,
        Tools
    }
}
