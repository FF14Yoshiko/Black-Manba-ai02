# 回放学习说明

这套学习链现在分成两条：

1. `BattlefieldReplayRecorder`
   - 学“本地最终发布过的指令”在 10 秒 / 30 秒后验上表现如何
   - 产出 `decision_quality_stats.json`
2. `AiTeacherLearningService`
   - 学“AI 主导后的最终指令”在 10 秒 / 30 秒后验上表现如何
   - 产出 `ai_teacher_learning_stats.json`

两条结果会在 `WorldStateService` 里合并，再作为 `CommandEffectiveness` 喂回 `TacticalDecisionEngineService`，影响本地后续的命令排序。

## 入口开关

UI 里的开关还是这一项：

- `性能模式 -> 启用回放调权反馈`

它打开后，会同时启用：

- 普通回放调权
- AI 老师学习

## 数据文件

默认目录：

```text
%AppData%\\XIVLauncherCN\\pluginConfigs\\ai02\\ReplayLogs
```

主要文件：

```text
ReplayLogs\\*.jsonl
ReplayLogs\\decision_quality_stats.json
ReplayLogs\\ai_teacher_learning_stats.json
```

含义分别是：

- `*.jsonl`
  - 每局的帧、事件、评估记录
- `decision_quality_stats.json`
  - 本地发布指令的后验统计
- `ai_teacher_learning_stats.json`
  - AI 主导指令的后验统计

## 普通回放调权怎么学

入口在：

- [BattlefieldReplayRecorder.cs](D:/MyFF14/ai02/src/Decision/BattlefieldReplayRecorder.cs)

流程：

1. `WorldStateService` 每隔一段时间把当前 `BattlefieldSnapshot` 交给 `BattlefieldReplayRecorder.Record(...)`
2. 录制器把帧写进 `jsonl`
3. 如果本帧有真正“发布出去”的本地指令：
   - `Publish.ShouldAnnounce == true`
   - `Publish.Command` 有值
   - `Publish.Sequence` 比上次新
4. 它会记一份 baseline，并挂两个评估窗：
   - 10 秒
   - 30 秒
5. 到窗时重新取当前指标，计算 heuristic score
6. 把分数按 `BattlefieldCommandKind` 聚合
7. 生成 `BattlefieldCommandEffectivenessSnapshot`
8. 后续 TacticalEngine 在给命令排序时，会给同类命令一个小幅加减权

后验分当前主要看这些变化：

- 我方分数变化
- 我方击杀 / 阵亡变化
- 敌方阵亡变化
- 当前死亡人数变化
- 总风险变化
- 名次变化

它不是“绝对真理”，只是一个长期调权代理。

## AI 老师学习怎么学

入口在：

- [AiTeacherLearningService.cs](D:/MyFF14/ai02/src/Decision/AiTeacherLearningService.cs)

流程和普通回放很像，但样本来源不同：

1. `WorldStateService.RecordReplayArtifacts()` 同时喂：
   - `BattlefieldReplayRecorder`
   - `AiTeacherLearningService`
2. `AiTeacherLearningService` 只抓 `AI 主导` 的最终指令
   - 也就是已经经过 `StrategicArbitrationService` 接管后的 `snapshot.Decision`
3. 它一样挂 10 秒 / 30 秒两个评估窗
4. 到窗后按 `BattlefieldCommandKind` 记老师样本
5. 产出 `BattlefieldCommandEffectivenessSnapshot`
6. 再和普通回放反馈合并

当前“AI 主导”的判定，沿用：

- [CommandOverlayAiDisplayPolicy.cs](D:/MyFF14/ai02/src/UI/CommandOverlayAiDisplayPolicy.cs)

也就是看：

- `ai:command:*`
- `ai:action:*`
- `PriorityText == "AI 主导"`
- 或决策摘要里带 `AI 主导 / AI 接管`

## 这次做的优化

这一轮把 AI 老师学习补了两个关键优化：

1. `AiTeacherLearningService` 不再只看“当前帧 recent events”
   - 现在会像 `BattlefieldReplayRecorder` 一样累积击杀、据点、战意事件
   - 这样 10 秒 / 30 秒窗更稳定，不容易被单帧抖动误导
2. 新增了 `ResetLearningStats()`
   - 方便后面做 UI 按钮或命令入口

另外，普通回放调权也加了：

- `ResetDecisionQualityFeedback()`

这样两条链都具备“清空样本重学”的能力。

## 合并规则

入口在：

- [DecisionQualityFeedbackFusion.cs](D:/MyFF14/ai02/src/Decision/DecisionQualityFeedbackFusion.cs)

规则很简单：

- 只有普通回放有数据：用普通回放
- 只有 AI 老师有数据：用 AI 老师
- 两边都有：按样本量加权合并
- AI 老师会稍微更重一点

原因是这个项目现在的目标不是“AI 只挂在 HUD 上”，而是让本地开始往 AI 战术偏好上靠。

## 它会影响什么

当前只影响：

- 命令类别权重

也就是像这些层面：

- 转点
- 接团
- 退
- 守点
- 集火
- 侧压
- 等待

它还没有直接学：

- `打第一 / 打第二 / 高价值点 / 近点 / 远点`
- 目标解析词表
- 具体落点 resolution

那一层更适合单独再做一层 teacher target resolution 学习。

## 怎么看样本是不是在长

最直接的方法：

1. 开启 `启用回放调权反馈`
2. 打几局
3. 看 `ReplayLogs` 下两个 stats 文件是否增长
4. 看复盘帧里有没有持续写出 `evaluation`

如果要人工清掉重学，目前最稳的做法是删除：

```text
decision_quality_stats.json
ai_teacher_learning_stats.json
```

## 当前边界

现在这套学习仍然是“后验 bias”，不是在线强化学习：

- 它会影响排序
- 不会直接热改 TacticalEngine 规则
- 不会自动生成新词表
- 不会自动改 target resolution
- 不会自己改 prompt

所以它很适合现在这个项目阶段：

- 足够稳
- 好回归
- 好解释
- 不会突然把本地主流程带飞

## 下一步最值得做的优化

如果继续往前推，优先级我建议是：

1. 给 AI 老师学习加一个只读状态面板
   - 看样本量、最近学习到哪些命令类
2. 加“清空学习样本”按钮
3. 把老师学习推进到 target resolution 层
   - 学 `打第一 / 打第二 / 高价值点 / 近点 / 远点`
4. 给不同地图分开记老师样本
   - 避免 Onsal 的偏好污染 Shatter
