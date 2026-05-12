using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
namespace ai02;

public partial class MainWindow
{
    private void DrawCombatHudPage()
    {
        var battlefield = plugin.WorldStateService.GetSnapshot();
        var decision = battlefield.Decision;
        var commands = decision.CommandSituation;

        DrawSectionTitle("战斗中极简指挥界面", battlefield.IsInFrontline ? "只保留战中最需要看的指令、目标和风险" : "进入纷争前线后自动切换");

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
        DrawCombatHudRow("AI 大决策", BuildCombatHudLlmDecisionText(battlefield.LlmStrategicDecision), LlmDecisionColor(battlefield.LlmStrategicDecision));
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
            DrawHint($"AI 大决策理由：{battlefield.LlmStrategicDecision.ShortReason}");
        else if (!string.IsNullOrWhiteSpace(battlefield.LlmStrategicDecision.GateReason))
            DrawHint($"AI 门控：{battlefield.LlmStrategicDecision.GateReason}");
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
        DrawSectionTitle("复盘 / 调试", battlefield.IsInFrontline ? "按战况、战术、诊断分类查看" : "不在战场时只显示已采集到的状态");

        if (!battlefield.IsInFrontline)
        {
            DrawHint("当前不在纷争前线区域，部分战场数据会在进入战场后更新。");
            ImGui.Spacing();
        }

        DrawReviewSummaryStrip(battlefield);

        if (!ImGui.BeginTabBar("##ReviewTabs"))
            return;

        if (ImGui.BeginTabItem("战况总览"))
        {
            DrawTimeSituation(battlefield.TimeSituation);
            ImGui.Spacing();
            DrawScoreSituation(battlefield.ScoreSituation);
            ImGui.Spacing();
            DrawAnnouncementSituation(battlefield.AnnouncementSituation);
            ImGui.Spacing();
            DrawChatEventSituation(battlefield.ChatEventSituation);
            ImGui.Spacing();
            DrawTeamSituation(battlefield.TeamSituation);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("战术决策"))
        {
            DrawCommandSituation(battlefield.Decision.CommandSituation);
            ImGui.Spacing();
            DrawDecisionSituation(battlefield.Decision);
            ImGui.Spacing();
            DrawLlmStrategicDecision(battlefield.LlmStrategicDecision);
            ImGui.Spacing();
            DrawMapTacticsSituation(battlefield.MapTactics);
            ImGui.Spacing();
            DrawAdvancedTacticalSituation(battlefield.TeamSituation.AdvancedTactics, plugin.Configuration.AdvancedTactics.ShowSuppressedInsights);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("采集诊断"))
        {
            DrawGroupTitle("采集概况");
            DrawHint(battlefield.StatusText);
            ImGui.Text($"区域 {battlefield.TerritoryType} / 地图 {battlefield.MapId} / 过图中 {(battlefield.IsAreaTransitioning ? "是" : "否")}");
            ImGui.Text($"玩家 {battlefield.Players.Length} / 友方 {battlefield.FriendlyPlayerCount} / 敌方 {battlefield.EnemyPlayerCount} / 死亡 {battlefield.DeadPlayerCount} / 咏唱 {battlefield.CastingPlayerCount}");
            ImGui.Text($"目标 {battlefield.Objectives.Length} / 地图目标 {battlefield.MapObjectives.Length} / 人群 {battlefield.PlayerClusters.Length} / 轨迹 {battlefield.PlayerTracks.Length}/{battlefield.EnemyMainGroupTrack.Length}");
            if (battlefield.LocalPlayer.HasValue)
                ImGui.Text($"本地坐标 {FormatPosition(battlefield.LocalPlayer.Value.Position)} / 阵营 {battlefield.LocalPlayer.Value.Battalion}");

            ImGui.Spacing();
            DrawReplayRecorderStatus(plugin.BattlefieldReplayRecorder.GetStatus());
            ImGui.Spacing();
            DrawMapObjectivePreview(battlefield.MapObjectives);
            DrawKnowledgePreview(battlefield.Knowledge);
            DrawClusterPreview(battlefield.PlayerClusters);
            DrawMapEventPreview(battlefield.MapEvents);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawReviewSummaryStrip(BattlefieldSnapshot battlefield)
    {
        if (!ImGui.BeginTable("##ReviewSummaryStrip", 4, ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg))
            return;

        ImGui.TableNextRow();
        DrawReviewSummaryCell("剩余时间", battlefield.TimeSituation.HasMatchTime ? $"{battlefield.MatchTimeRemaining / 60:D2}:{battlefield.MatchTimeRemaining % 60:D2}" : "--:--", new Vector4(0.95f, 0.82f, 0.35f, 1f), battlefield.TimeSituation.MatchPhaseName);
        DrawReviewSummaryCell("比分", BuildCombatHudScoreText(battlefield), new Vector4(0.82f, 0.84f, 0.90f, 1f), battlefield.ScoreSituation.SummaryText);
        DrawReviewSummaryCell("主指令", ResolveCombatHudPrimaryCommandText(battlefield.Decision), PriorityColor(battlefield.Decision.CommandSituation.PrimaryCommand?.Score ?? 58f), battlefield.Decision.RecommendedAction);
        DrawReviewSummaryCell("AI", BuildCombatHudLlmDecisionText(battlefield.LlmStrategicDecision), LlmDecisionColor(battlefield.LlmStrategicDecision), battlefield.LlmStrategicDecision.ShortReason);
        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static void DrawReviewSummaryCell(string label, string value, Vector4 color, string? detail)
    {
        ImGui.TableNextColumn();
        DrawMetricCard($"##ReviewMetric_{label}", label, value, color, detail);
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
                DrawHint($"压制原因：{commands.Publish.SuppressionReason}");
        }

        if (commands.PrimaryCommand.HasValue)
        {
            var primary = commands.PrimaryCommand.Value;
            ImGui.TextColored(RiskColor(primary.Urgency), primary.CommandText);
            DrawHint($"{primary.Scope} / {CommandKindText(primary.Kind)} / 优先 {CommandPriorityText(primary)} / 分数 {primary.Score:0} / 紧急 {primary.Urgency:0}");
            DrawHint(primary.ReasonText);
        }

        if (commands.PrimaryAction.HasValue)
        {
            var action = commands.PrimaryAction.Value;
            ImGui.TextColored(PriorityColor(action.Priority), $"行动：{ActionTypeText(action.ActionType)} / {action.Text}");
            DrawHint($"目的地 {action.DestinationName} / 路线 {action.RouteText} / ETA {FormatDuration(action.EtaSeconds)} / 倒计时 {FormatCountdown(action.CountdownSeconds)}");
            DrawHint($"置信 {action.Confidence:0} / 风险 {action.Risk:0} / 保持 {action.HoldSeconds} 秒 / {action.ReasonText}");
            if (commands.IsActionHoldActive)
                DrawHint($"防抖保持：{commands.ActionHoldReason}（剩余 {commands.ActionHoldRemainingSeconds} 秒）");
        }

        var hasCandidates = commands.ActionCandidates.Length > 0 || commands.Commands.Length > 0;
        if (!hasCandidates)
            return;

        DrawHint($"候选行动 {commands.ActionCandidates.Length} 条 / 候选指令 {commands.Commands.Length} 条");
        if (!ImGui.TreeNode("展开候选与明细##CommandCandidates"))
            return;

        if (commands.ActionCandidates.Length > 0)
        {
            var actionCount = Math.Min(commands.ActionCandidates.Length, 4);
            if (ImGui.BeginTable("##RealtimeActionCandidateTable", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("行动");
                ImGui.TableSetupColumn("优先", ImGuiTableColumnFlags.WidthFixed, 48f);
                ImGui.TableSetupColumn("置信", ImGuiTableColumnFlags.WidthFixed, 48f);
                ImGui.TableSetupColumn("目的地");
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
                    ImGui.Text($"{item.DestinationName} / {FormatDuration(item.EtaSeconds)}");
                    ImGui.TableNextColumn();
                    ImGui.Text(item.FailureConditionText);
                }

                ImGui.EndTable();
            }
        }

        if (commands.Commands.Length > 0)
        {
            var count = Math.Min(commands.Commands.Length, 5);
            if (ImGui.BeginTable("##RealtimeCommandTable", 5, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("指令");
                ImGui.TableSetupColumn("范围", ImGuiTableColumnFlags.WidthFixed, 64f);
                ImGui.TableSetupColumn("级", ImGuiTableColumnFlags.WidthFixed, 44f);
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
                    ImGui.TextColored(RiskColor(item.Urgency), $"{item.Urgency:0}");
                    ImGui.TableNextColumn();
                    ImGui.Text(item.ReasonText);
                }

                ImGui.EndTable();
            }
        }

        ImGui.TreePop();
    }

    private static void DrawDecisionSituation(BattlefieldDecisionSnapshot decision)
    {
        DrawGroupTitle("目标优先级与风险");
        DrawHint(decision.SummaryText);
        ImGui.TextColored(RiskColor(decision.RiskAssessment.OverallRisk), decision.RecommendedAction);
        DrawDualPriorityTargets(decision);
        ImGui.Text($"总体风险：{decision.RiskAssessment.RiskLevel} {decision.RiskAssessment.OverallRisk:0} / 人数差 {decision.RiskAssessment.NumberDisadvantageRisk:0} / 夹击 {decision.RiskAssessment.FlankRisk:0} / 地形 {decision.RiskAssessment.TerrainRisk:0}");

        if (ImGui.TreeNode("风险拆分##DecisionRiskBreakdown"))
        {
            ImGui.Text($"帧事件 {decision.RiskAssessment.FrameEventRisk:0} / 复活 {decision.RiskAssessment.RespawnRisk:0} / 敌方方向 {decision.RiskAssessment.EnemyMainGroupDirectionRisk:0}");
            ImGui.Text($"退路 {decision.RiskAssessment.RetreatRouteRisk:0} / 包围 {decision.RiskAssessment.EncirclementRisk:0} / 技能 {decision.RiskAssessment.SkillThreatRisk:0} / LB {decision.RiskAssessment.LimitBreakRisk:0}");
            ImGui.Text($"战意 {decision.RiskAssessment.BattleHighRisk:0} / 目标 {decision.RiskAssessment.ObjectiveRisk:0} / 埋伏 {decision.RiskAssessment.AmbushRisk:0} / 凝聚 {decision.RiskAssessment.CohesionRisk:0}");
            ImGui.Text($"第三方 {decision.RiskAssessment.ThirdPartyPincerRisk:0} / 高台 {decision.RiskAssessment.HighGroundDropRisk:0} / 封路 {decision.RiskAssessment.ChokeBlockRisk:0} / 组排 {decision.RiskAssessment.CoordinatedSquadRisk:0}");
            ImGui.TreePop();
        }

        DrawDecisionQualitySituation(decision.DecisionQuality);

        if (decision.ObjectivePriorities.Length == 0)
            return;

        var count = Math.Min(decision.ObjectivePriorities.Length, 3);
        if (ImGui.BeginTable("##DecisionObjectivePriorityTable", 4, ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("目标");
            ImGui.TableSetupColumn("建议", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("优先", ImGuiTableColumnFlags.WidthFixed, 58f);
            ImGui.TableSetupColumn("风险", ImGuiTableColumnFlags.WidthFixed, 58f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < count; i++)
            {
                var item = decision.ObjectivePriorities[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text($"{item.Name} {MapObjectiveCategoryText(item.Category)}");
                ImGui.TableNextColumn();
                ImGui.Text(item.RecommendedAction);
                ImGui.TableNextColumn();
                ImGui.TextColored(PriorityColor(item.PriorityScore), $"{item.PriorityScore:0}");
                ImGui.TableNextColumn();
                ImGui.TextColored(RiskColor(item.RiskScore), $"{item.RiskScore:0}");
            }

            ImGui.EndTable();
        }

        var top = decision.ObjectivePriorities[0];
        if (!string.IsNullOrWhiteSpace(top.EvidenceText))
            DrawHint(top.EvidenceText);

        if (decision.ObjectivePriorities.Length > count && ImGui.TreeNode("更多目标优先级##DecisionObjectivePriorityMore"))
        {
            for (var i = count; i < Math.Min(decision.ObjectivePriorities.Length, count + 4); i++)
            {
                var item = decision.ObjectivePriorities[i];
                DrawHint($"{item.Name} / {item.RecommendedAction} / 优先 {item.PriorityScore:0} / 风险 {item.RiskScore:0}");
            }
            ImGui.TreePop();
        }
    }

    private static void DrawLlmStrategicDecision(BattlefieldLlmStrategicDecisionSnapshot llm)
    {
        DrawGroupTitle("AI 大决策");
        ImGui.TextColored(LlmDecisionColor(llm), llm.StatusText);
        if (!string.IsNullOrWhiteSpace(llm.GateReason))
            DrawHint($"门控：{llm.NeedText} / {llm.GateReason}");

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
        ImGui.Text($"置信 {llm.Confidence:0} / 风险 {llm.Risk:0} / 会话 {llm.SessionId}");
        if (!string.IsNullOrWhiteSpace(llm.ShortReason))
            DrawHint(llm.ShortReason);

        if ((!string.IsNullOrWhiteSpace(llm.DebugText) || !string.IsNullOrWhiteSpace(llm.RawJson))
            && ImGui.TreeNode("原始输出##LlmStrategicRaw"))
        {
            if (!string.IsNullOrWhiteSpace(llm.DebugText))
                DrawHint($"调试：{llm.DebugText}");
            if (!string.IsNullOrWhiteSpace(llm.RawJson))
                DrawReadonlyTextPanel("原始 JSON", llm.RawJson, "LlmStrategicRawJson", 120f);
            ImGui.TreePop();
        }
    }

    private static void DrawLlmDebugSnapshot(BattlefieldLlmStrategicDecisionSnapshot llm, BattlefieldLlmDebugSnapshot debug)
    {
        ImGui.TextColored(LlmDecisionColor(llm), debug.StatusText);
        ImGui.Text($"当前会话：{(string.IsNullOrWhiteSpace(debug.SessionId) ? "未建立" : debug.SessionId)}");
        ImGui.Text($"请求状态：{BuildLlmDebugStatusText(debug)}");
        if (!string.IsNullOrWhiteSpace(debug.CurrentGateReason))
            DrawHint($"当前门控：{debug.CurrentNeedText} / {debug.CurrentGateReason}");

        if (!debug.HasRequest)
        {
            DrawHint("本局还没有发起 AI 请求，可以先走一次手动测试。 ");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(debug.LastRequestGateReason))
                DrawHint($"最近触发：{debug.LastRequestNeedText} / {debug.LastRequestGateReason}");
            if (!string.IsNullOrWhiteSpace(debug.LastRequestSituationKey))
                DrawHint($"场景键：{debug.LastRequestSituationKey}");
        }

        if (!string.IsNullOrWhiteSpace(debug.ErrorText))
            DrawHint($"最近错误：{debug.ErrorText}");
        if (!string.IsNullOrWhiteSpace(llm.RecommendedAction) || !string.IsNullOrWhiteSpace(llm.ShortReason))
            DrawHint($"最近决策：{llm.RecommendedAction} / {llm.ShortReason}");

        DrawReadonlyTextPanel("手动附言", debug.ManualInstruction, "LlmManualInstruction", 56f);

        if (ImGui.TreeNode("提示词与上下文##LlmDebugPrompts"))
        {
            DrawReadonlyTextPanel("系统提示词", debug.SystemPrompt, "LlmSystemPrompt", 120f);
            DrawReadonlyTextPanel("用户提示词", debug.UserPrompt, "LlmUserPrompt", 160f);
            DrawReadonlyTextPanel("最近上下文", BuildLlmConversationDebugText(debug.ConversationTurns), "LlmConversationContext", 180f);
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("模型输出##LlmDebugOutput"))
        {
            DrawReadonlyTextPanel("模型原始响应", debug.RawResponse, "LlmRawResponse", 120f);
            DrawReadonlyTextPanel("解析结果 JSON", debug.ParsedJson, "LlmParsedJson", 120f);
            DrawReadonlyTextPanel("模型调试摘要", debug.DebugText, "LlmDebugText", 96f);
            ImGui.TreePop();
        }

        if (ImGui.TreeNode("读数调试##LlmDebugSignals"))
        {
            DrawReadonlyTextPanel("比分读取", debug.DebugScoreRead, "LlmDebugScoreRead", 64f);
            DrawReadonlyTextPanel("位置读取", debug.DebugPositionRead, "LlmDebugPositionRead", 64f);
            DrawReadonlyTextPanel("延迟说明", debug.DebugLatencyNote, "LlmDebugLatencyNote", 64f);
            ImGui.TreePop();
        }
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
        if (quality.InputReliability.IsAvailable)
        {
            var input = quality.InputReliability;
            ImGui.TextColored(input.CanPublish ? PriorityColor(input.OverallReliability) : RiskColor(72f), $"输入可靠度：{input.OverallReliability:0}  {(input.CanPublish ? "可喊" : "仅提示")}");
            DrawHint(input.SummaryText);

            var preview = input.Components.Take(2)
                .Select(component => $"{component.Label}:{component.Reliability:0}  {component.EvidenceText}");
            DrawPreviewList("DecisionQualityInputComponents", preview, 2);
        }

        if (!string.IsNullOrWhiteSpace(quality.CalibrationText))
            DrawHint($"回放校准：{quality.CalibrationText}");

        DrawPreviewList(
            "DecisionQualityEffectiveness",
            quality.CommandEffectiveness.Take(3).Select(item => $"{CommandKindText(item.Kind)} 样本 {item.SampleCount} / 均值 {item.AverageScore:+0.0;-0.0;0} / 调权 {item.Modifier:+0.0;-0.0;0}"),
            2);

        DrawPreviewList(
            "DecisionQualityEnemyIntents",
            quality.EnemyIntentPredictions.Take(4).Select(intent => $"{EnemyIntentKindText(intent.Kind)} {intent.AllianceName} / 置信 {intent.Confidence:0} / 紧急 {intent.Urgency:0} / {intent.Recommendation}"),
            2);

        DrawPreviewList(
            "DecisionQualityRoles",
            quality.TeamRoleInsights.Take(4).Select(role => $"{TeamRoleInsightKindText(role.Kind)} {role.TargetName} / 强度 {role.Severity:0} / 人数 {role.PlayerCount} / {role.Recommendation}"),
            2);
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
            DrawHint("目标标记只作为态势输入使用；集火信息统一显示在指挥界面。");
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
}
