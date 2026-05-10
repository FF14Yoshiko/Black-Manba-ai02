# 前线战术指挥

把战场读懂，再把下一步说清楚。

`ai02` 是一个面向《最终幻想 XIV》纷争前线的 Dalamud 插件，目标是把战场态势感知、局势判断、指挥提示和地图战术图谱整合到一个可实战使用的界面里。

## 主要能力

- 前线雷达与地图信息显示
- 战场态势分析与本地即时决策
- 指挥 HUD 与战斗中短句提示
- 内置地图战术图谱与路线/区域分析
- 可选 LLM 大决策接入
- 战场回放与决策调试信息

## 项目结构

- `Plugin.cs`: 插件入口
- `WorldStateService.cs`: 战场数据采集、快照整合与主流程
- `TacticalDecisionEngineService.cs`: 本地战术决策
- `LlmStrategicDecisionService.cs`: LLM 大决策请求与门控
- `MapTacticalGraphService.cs`: 地图战术图谱加载与管理
- `BuiltInTacticalGraphs/`: 内置战术图谱 JSON
- `images/`: 插件资源
- `.github/workflows/build.yml`: GitHub Actions 构建流程

## 本地构建

本项目基于 .NET 10，依赖 Dalamud 开发库。

1. 准备 Dalamud 开发依赖  
   默认路径为：

   ```text
   %AppData%\XIVLauncherCN\addon\Hooks\dev\
   ```

2. 构建插件

   ```powershell
   dotnet build ai02.csproj -c Release
   ```

3. 构建产物  
   CI 和打包流程会生成：

   ```text
   bin/Release/ai02/latest.zip
   ```

## LLM 配置说明

- 默认支持 `deepseek-v4-flash`
- API Key 不应提交到仓库
- 运行时密钥建议放在插件配置或环境变量中

## 发布说明

仓库已包含 GitHub Actions 构建流程。推送到 GitHub 后，`push` 和 `pull_request` 会自动触发构建，并校验打包结果。

## 注意事项

- 不要把本地插件配置、日志、回放数据和私钥提交到仓库
- 当前仓库默认忽略 `bin/`、`obj/`、临时文件和本地工具目录

