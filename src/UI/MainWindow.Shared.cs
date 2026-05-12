using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
namespace ai02;

public partial class MainWindow
{
    private static void DrawSectionTitle(string title, string subtitle)
    {
        var drawList = ImGui.GetWindowDrawList();
        var origin = ImGui.GetCursorScreenPos();
        drawList.AddRectFilled(origin, origin + new Vector2(4f, 42f), Color(new Vector4(0.27f, 0.59f, 0.98f, 0.95f)), 3f);
        ImGui.SetCursorScreenPos(origin + new Vector2(12f, 0f));
        ImGui.TextColored(new Vector4(0.94f, 0.95f, 0.98f, 1f), title);
        if (!string.IsNullOrWhiteSpace(subtitle))
            ImGui.TextColored(new Vector4(0.58f, 0.62f, 0.70f, 1f), subtitle);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    private static bool BeginCollapsibleSection(string title, string summary, bool defaultOpen = true)
    {
        ImGui.Spacing();
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        ImGui.PushStyleColor(ImGuiCol.Header, new Vector4(0.14f, 0.16f, 0.20f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.19f, 0.22f, 0.28f, 0.96f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, new Vector4(0.17f, 0.20f, 0.26f, 1f));
        var open = ImGui.CollapsingHeader($"{title}##section_{title}", flags);
        ImGui.PopStyleColor(3);
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
        ImGui.TextColored(new Vector4(0.88f, 0.80f, 0.50f, 1f), title);
    }

    private static void DrawHint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.60f, 0.64f, 0.70f, 1f));
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void DrawPreviewList(string treeId, IEnumerable<string> lines, int previewCount = 4)
    {
        var items = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (items.Length == 0)
            return;

        var count = Math.Min(items.Length, previewCount);
        for (var i = 0; i < count; i++)
            DrawHint(items[i]);

        if (items.Length <= count)
            return;

        if (!ImGui.TreeNode($"更多##{treeId}"))
            return;

        for (var i = count; i < items.Length; i++)
            DrawHint(items[i]);
        ImGui.TreePop();
    }

    private static void DrawMetricCard(string id, string label, string value, Vector4 accent, string? detail = null)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(0.10f, 0.12f, 0.16f, 0.88f));
        var height = string.IsNullOrWhiteSpace(detail) ? 54f : 72f;
        if (ImGui.BeginChild(id, new Vector2(0f, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            ImGui.TextColored(new Vector4(0.62f, 0.66f, 0.72f, 1f), label);
            ImGui.TextColored(accent, string.IsNullOrWhiteSpace(value) ? "-" : value);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.50f, 0.54f, 0.60f, 1f));
                ImGui.TextWrapped(detail);
                ImGui.PopStyleColor();
            }
        }

        ImGui.EndChild();
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
            ImGui.TableSetupColumn("血线 / 战意");
            ImGui.TableSetupColumn("分布 / 主团");
            ImGui.TableHeadersRow();
            DrawTeamSummaryRow(situation.Friendly);
            DrawTeamSummaryRow(situation.Enemy);
            if (situation.Unknown.TotalCount > 0)
                DrawTeamSummaryRow(situation.Unknown);
            ImGui.EndTable();
        }

        ImGui.Text($"复活节奏：{situation.RespawnRhythm.SummaryText}");
        ImGui.Text($"敌方大团：{situation.EnemyMainGroupMovement.SummaryText}");
        if (!string.IsNullOrWhiteSpace(situation.EnemySplitSummaryText))
            DrawHint($"敌方分兵：{situation.EnemySplitSummaryText}");

        if (!ImGui.TreeNode("更多团队细节##TeamSituationDetails"))
            return;

        ImGui.Text($"阵营玩家：我方 {situation.FriendlyPlayers.Length} / 敌方1 {situation.EnemyAlliance1Players.Length} / 敌方2 {situation.EnemyAlliance2Players.Length}");
        ImGui.Text($"大团来源：{situation.EnemyMainGroupMovement.SourceText}");
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
        ImGui.TreePop();
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
}
