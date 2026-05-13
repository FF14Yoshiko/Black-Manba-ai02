using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
namespace ai02;

public partial class MainWindow
{
    private void DrawToolsPage()
    {
        DrawSectionTitle("工具", "按常用、AI、数据调试分类整理");

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

        DrawToolsSummaryStrip(commandOverlay, llmDecision, replay, performance);

        if (ImGui.BeginTabBar("##ToolsTabs"))
        {
            if (ImGui.BeginTabItem("常用"))
            {
                if (BeginCollapsibleSection("配置管理", "导入、导出和迁移当前插件配置", true))
                {
                    if (ImGui.Button("导出配置", new Vector2(130f, 28f)))
                        HandleExportConfiguration();
                    ImGui.SameLine();
                    if (ImGui.Button("导入配置", new Vector2(130f, 28f)))
                        HandleImportConfiguration();

                    if (!string.IsNullOrWhiteSpace(configurationTransferStatus))
                        DrawHint(configurationTransferStatus);
                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("窗口入口", "悬浮球和主窗口入口", true))
                {
                    changed |= DrawToggle("启用悬浮球", floating.Enabled, value => floating.Enabled = value);
                    if (ImGui.Button("重置悬浮球位置", new Vector2(160f, 28f)))
                    {
                        floating.X = 80f;
                        floating.Y = 360f;
                        changed = true;
                    }

                    DrawHint("命令：/man 打开主窗口，/manradar 开关雷达。");
                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("指挥大字", "屏幕指挥短句、位置和停留时间", true))
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
                    changed |= DrawSliderInt("AI 主导锁定秒数", commandOverlay.AiLeadHoldSeconds, 0, 20, value => commandOverlay.AiLeadHoldSeconds = value);
                    changed |= DrawColorEdit("大字文字颜色", new Vector4(commandOverlay.TextColorR, commandOverlay.TextColorG, commandOverlay.TextColorB, 1f), value =>
                    {
                        commandOverlay.TextColorR = value.X;
                        commandOverlay.TextColorG = value.Y;
                        commandOverlay.TextColorB = value.Z;
                    });
                    changed |= DrawColorEdit("AI 主导文字颜色", new Vector4(commandOverlay.AiTextColorR, commandOverlay.AiTextColorG, commandOverlay.AiTextColorB, 1f), value =>
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
                        commandOverlay.AiLeadHoldSeconds = 5;
                        commandOverlay.AiTextColorR = 0.35f;
                        commandOverlay.AiTextColorG = 0.82f;
                        commandOverlay.AiTextColorB = 1f;
                        changed = true;
                    }

                    var commands = plugin.WorldStateService.GetSnapshot().Decision.CommandSituation;
                    if (commands.PrimaryCommand.HasValue)
                        DrawHint($"当前预览主指令：{commands.PrimaryCommand.Value.CommandText}");
                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("极限槽预测", "极限技充能显示、位置和调试开关", true))
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
                        DrawPreviewList("LimitBreakDebugLines", plugin.LimitBreakService.GetAddonDebugLines().Take(8), 4);
                    }
                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("性能模式", "降低实时扫描、地图采样和调试叠加开销", true))
                {
                    changed |= DrawToggle("低影响模式", performance.LowImpactMode, value => performance.LowImpactMode = value);
                    changed |= DrawSliderInt("战场态势刷新间隔（毫秒）", performance.WorldRefreshIntervalMs, 250, 5000, value => performance.WorldRefreshIntervalMs = value);
                    changed |= DrawSliderInt("战斗 / 风险刷新间隔（毫秒）", performance.CombatRefreshIntervalMs, 500, 5000, value => performance.CombatRefreshIntervalMs = value);
                    changed |= DrawSliderInt("比分扫描间隔（毫秒）", performance.ScoreScanIntervalMs, 250, 10000, value => performance.ScoreScanIntervalMs = value);
                    changed |= DrawSliderInt("状态 Buff 扫描间隔（毫秒）", performance.StatusScanIntervalMs, 250, 10000, value => performance.StatusScanIntervalMs = value);
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
                    EndCollapsibleSection();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("AI"))
            {
                if (BeginCollapsibleSection("AI 大决策", "模型配置、请求频率和手动测试", true))
                {
                    changed |= DrawToggle("启用 AI 大决策", llmDecision.Enabled, value => llmDecision.Enabled = value);
                    changed |= DrawInputText("API 地址", llmDecision.ProviderBaseUrl, 180, value => llmDecision.ProviderBaseUrl = value);
                    changed |= DrawInputText("模型", llmDecision.Model, 120, value => llmDecision.Model = value);
                    changed |= DrawInputText("API Key 环境变量", llmDecision.ApiKeyEnvironmentVariable, 80, value => llmDecision.ApiKeyEnvironmentVariable = value);
                    changed |= DrawInputText("API Key（可留空，优先用环境变量）", llmDecision.ApiKey, 220, value => llmDecision.ApiKey = value);
                    changed |= DrawSliderInt("请求超时（毫秒）", llmDecision.RequestTimeoutMs, 1500, 15000, value => llmDecision.RequestTimeoutMs = value);
                    changed |= DrawSliderInt("最小请求间隔（秒）", llmDecision.MinIntervalSeconds, 3, 120, value => llmDecision.MinIntervalSeconds = value);
                    changed |= DrawSliderInt("同局势冷却（秒）", llmDecision.SameSituationCooldownSeconds, 5, 180, value => llmDecision.SameSituationCooldownSeconds = value);
                    changed |= DrawToggle("启用固定局内 AI 分析", llmDecision.RoutinePulseEnabled, value => llmDecision.RoutinePulseEnabled = value);
                    changed |= DrawSliderInt("固定分析间隔（秒）", llmDecision.RoutinePulseIntervalSeconds, 10, 180, value => llmDecision.RoutinePulseIntervalSeconds = value);
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
                        DrawHint($"当前门控：{ai.NeedText} / {ai.GateReason}");
                    if (ai.IsAvailable)
                        DrawHint($"最近决策：{ai.RecommendedAction} / {ai.ShortReason}");
                    if (!string.IsNullOrWhiteSpace(llmManualStatus))
                        DrawHint($"测试通道：{llmManualStatus}");

                    if (BeginCollapsibleSection("AI 对话调试", "查看提示词、原始回复、解析 JSON 和会话上下文", false))
                    {
                        var debug = plugin.LlmStrategicDecisionService.GetDebugSnapshot(snapshot);
                        DrawLlmDebugSnapshot(ai, debug);
                        EndCollapsibleSection();
                    }

                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("实战回放记录", "逐行结构化回放、评估事件和保留策略", true))
                {
                    changed |= DrawToggle("启用前线实战回放", replay.Enabled, value => replay.Enabled = value);
                    changed |= DrawSliderInt("记录间隔（秒）", replay.RecordIntervalSeconds, 1, 30, value => replay.RecordIntervalSeconds = value);
                    changed |= DrawSliderInt("保留场次数", replay.MaxSessionFiles, 1, 300, value => replay.MaxSessionFiles = value);
                    changed |= DrawSliderInt("保留天数", replay.MaxSessionAgeDays, 1, 365, value => replay.MaxSessionAgeDays = value);
                    changed |= DrawToggle("记录玩家明细", replay.IncludePlayerDetails, value => replay.IncludePlayerDetails = value);
                    changed |= DrawToggle("显示回放调试信息", replay.ShowDebugInfo, value => replay.ShowDebugInfo = value);
                    changed |= DrawInputText("回放目录名", replay.DirectoryName, 80, value => replay.DirectoryName = value);
                    DrawReplayRecorderStatus(plugin.BattlefieldReplayRecorder.GetStatus());
                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("AI 老师学习", "查看当前地图样本、最近学到的命令类和落点偏好", true))
                {
                    var snapshot = plugin.WorldStateService.GetSnapshot();
                    var teacherStatus = plugin.AiTeacherLearningService.GetStatus(snapshot.ScoreSituation.MapType);
                    DrawAiTeacherLearningStatus(teacherStatus);
                    if (ImGui.Button("清空学习样本", new Vector2(150f, 28f)))
                    {
                        plugin.AiTeacherLearningService.ResetLearningStats();
                        aiTeacherLearningStatus = "已清空 AI 老师学习样本";
                    }

                    if (!string.IsNullOrWhiteSpace(aiTeacherLearningStatus))
                        DrawHint(aiTeacherLearningStatus);
                    EndCollapsibleSection();
                }

                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("数据调试"))
            {
                if (BeginCollapsibleSection("比分读取", "结构化比分来源和原始调试信息", true))
                {
                    changed |= DrawToggle("显示比分原始来源调试信息", scoreReader.ShowRawSourceDebug, value => scoreReader.ShowRawSourceDebug = value);
                    DrawHint(plugin.WorldStateService.GetSnapshot().ScoreDebugInfo);
                    if (scoreReader.ShowRawSourceDebug)
                    {
                        DrawPreviewList("ScoreReaderDebugLines", plugin.FrontlineScoreReader.GetRawScoreSourceDebugLines().Take(18), 6);
                    }
                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("战意状态读取", "战意状态识别和可见状态调试", false))
                {
                    changed |= DrawInputText("候选状态编号（按 1~5 层顺序）", battleHigh.CandidateStatusIds, 120, value => battleHigh.CandidateStatusIds = value);
                    changed |= DrawToggle("显示战意状态调试信息", battleHigh.ShowDebugInfo, value => battleHigh.ShowDebugInfo = value);
                    changed |= DrawToggle("调试时列出可见玩家全部状态", battleHigh.ShowAllVisibleStatusesInDebug, value => battleHigh.ShowAllVisibleStatusesInDebug = value);
                    if (battleHigh.ShowDebugInfo)
                    {
                        DrawPreviewList("BattleHighDebugLines", plugin.WorldStateService.GetBattleHighDebugLines().Take(42), 8);
                    }
                    EndCollapsibleSection();
                }

                if (BeginCollapsibleSection("状态效果战术化", "防御、控制、抗控、无敌识别与派生标签", false))
                {
                    changed |= DrawToggle("显示战术状态调试信息", tacticalStatus.ShowDebugInfo, value => tacticalStatus.ShowDebugInfo = value);
                    changed |= DrawToggle("战术状态调试列出全部可见状态", tacticalStatus.ShowAllVisibleStatusesInDebug, value => tacticalStatus.ShowAllVisibleStatusesInDebug = value);
                    changed |= DrawInputText("防御候选状态编号", tacticalStatus.GuardingStatusIds, 120, value => tacticalStatus.GuardingStatusIds = value);
                    changed |= DrawInputText("被控候选状态编号", tacticalStatus.CrowdControlledStatusIds, 120, value => tacticalStatus.CrowdControlledStatusIds = value);
                    changed |= DrawInputText("抗控候选状态编号", tacticalStatus.ControlImmuneStatusIds, 120, value => tacticalStatus.ControlImmuneStatusIds = value);
                    changed |= DrawInputText("无敌候选状态编号", tacticalStatus.InvulnerableStatusIds, 120, value => tacticalStatus.InvulnerableStatusIds = value);
                    changed |= DrawInputText("雪精祝福候选状态编号", tacticalStatus.SnowBlessingStatusIds, 120, value => tacticalStatus.SnowBlessingStatusIds = value);
                    if (tacticalStatus.ShowDebugInfo)
                    {
                        DrawPreviewList("TacticalStatusDebugLines", plugin.WorldStateService.GetTacticalStatusDebugLines().Take(58), 8);
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
                        DrawPreviewList("AnnouncementDebugLines", plugin.FrontlineAnnouncementReader.GetAddonDebugLines().Take(10), 5);
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
                    EndCollapsibleSection();
                }

                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
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

    private void DrawToolsSummaryStrip(
        CommandOverlayConfiguration commandOverlay,
        LlmDecisionConfiguration llmDecision,
        BattlefieldReplayConfiguration replay,
        PerformanceConfiguration performance)
    {
        if (!ImGui.BeginTable("##ToolsSummaryStrip", 4, ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawMetricCard(
            "##ToolsOverlay",
            "指挥大字",
            commandOverlay.Enabled ? "已启用" : "已关闭",
            commandOverlay.Enabled ? new Vector4(0.38f, 0.84f, 1f, 1f) : new Vector4(0.62f, 0.62f, 0.66f, 1f),
            commandOverlay.OnlyInFrontline ? "仅战场显示" : "全局可见");

        ImGui.TableNextColumn();
        DrawMetricCard(
            "##ToolsLlm",
            "AI 分析",
            llmDecision.Enabled ? "已启用" : "已关闭",
            llmDecision.Enabled ? new Vector4(0.50f, 0.88f, 0.66f, 1f) : new Vector4(0.62f, 0.62f, 0.66f, 1f),
            llmDecision.RoutinePulseEnabled ? $"固定脉冲 {llmDecision.RoutinePulseIntervalSeconds}s" : "仅事件触发");

        ImGui.TableNextColumn();
        DrawMetricCard(
            "##ToolsReplay",
            "回放记录",
            replay.Enabled ? "记录中" : "未记录",
            replay.Enabled ? new Vector4(0.95f, 0.82f, 0.35f, 1f) : new Vector4(0.62f, 0.62f, 0.66f, 1f),
            $"间隔 {replay.RecordIntervalSeconds}s / 保留 {replay.MaxSessionFiles} 局");

        ImGui.TableNextColumn();
        DrawMetricCard(
            "##ToolsPerf",
            "性能模式",
            performance.LowImpactMode ? "低影响" : "标准",
            performance.LowImpactMode ? new Vector4(0.95f, 0.82f, 0.35f, 1f) : new Vector4(0.66f, 0.80f, 0.98f, 1f),
            $"态势 {performance.EffectiveWorldRefreshIntervalMs}ms / 战斗 {performance.EffectiveCombatRefreshIntervalMs}ms");
        ImGui.EndTable();
        ImGui.Spacing();
    }

    private static void DrawAiTeacherLearningStatus(BattlefieldAiTeacherLearningStatusSnapshot status)
    {
        DrawGroupTitle("\u0041I \u8001\u5e08");
        var color = !status.Enabled
            ? new Vector4(0.62f, 0.62f, 0.66f, 1f)
            : status.CommandSampleCount > 0 || status.TargetResolutionSampleCount > 0
                ? new Vector4(0.48f, 0.90f, 0.62f, 1f)
                : new Vector4(0.95f, 0.82f, 0.35f, 1f);
        ImGui.TextColored(color, status.StatusText);
        DrawHint($"\u5730\u56fe\uff1a{status.MapType}  \u547d\u4ee4:{status.CommandSampleCount}  \u843d\u70b9:{status.TargetResolutionSampleCount}");
        DrawHint($"\u6587\u4ef6\uff1a{status.StatsPath}");
        if (status.CommandEffectiveness.Length > 0)
            DrawHint("\u547d\u4ee4\u7c7b\uff1a" + string.Join(" / ", status.CommandEffectiveness.Take(3).Select(item => $"{item.Kind}({item.SampleCount})")));
        if (status.TargetResolutions.Length > 0)
            DrawHint("\u843d\u70b9\u504f\u597d\uff1a" + string.Join(" / ", status.TargetResolutions.Take(3).Select(item => $"{item.Kind}->{item.TargetName}({item.SampleCount})")));
        DrawPreviewList("AiTeacherRecentLearned", status.RecentLearned.Take(6).Select(item => $"\u6700\u65b0\uff1a{item.SummaryText}"), 3);
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
}
