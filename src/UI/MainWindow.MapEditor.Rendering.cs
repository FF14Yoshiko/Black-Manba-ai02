using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
namespace ai02;

public partial class MainWindow
{
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

        DrawSectionTitle("地图标注", "按编辑、图谱管理、校准分类展示");
        DrawMapEditorOverview(snapshot, offlineMap, document, builtInGraph, selectedCurrentMap);

        if (!ImGui.BeginTabBar("##MapEditorTabs"))
            return;

        if (ImGui.BeginTabItem("编辑"))
        {
            if (BeginCollapsibleSection("标注工具", "选择类型、命名、写路径编号，并把草稿保存成自定义图谱", true))
            {
                annotationClickMode = DrawInlineToggle("点击画布标注", annotationClickMode);
                DrawAnnotationKindSelector();
                _ = DrawInputText("标注名称", annotationLabel, 80, value => annotationLabel = value);
                _ = DrawInputText("路径编号（桥面 / 跳台 / 传送 / 绕后可填）", annotationRouteId, 80, value => annotationRouteId = value);
                _ = DrawSliderFloat("区域半径", annotationRadius, 0f, 220f, value => annotationRadius = value);
                _ = DrawSliderInt("风险分", annotationRiskScore, 0, 100, value => annotationRiskScore = value);

                if (ImGui.Button("标注本地位置", new Vector2(130f, 28f)) && snapshot.LocalPlayer.HasValue && selectedCurrentMap)
                    AddMapAnnotation(snapshot.LocalPlayer.Value.Position, territoryType, mapId, mapName);

                ImGui.SameLine();
                if (ImGui.Button("导入实时目标", new Vector2(120f, 28f)) && selectedCurrentMap)
                    ImportCurrentMapObjectives(snapshot, territoryType, mapId, mapName);

                ImGui.SameLine();
                if (ImGui.Button("导入地图事件", new Vector2(120f, 28f)) && selectedCurrentMap)
                    ImportCurrentMapEvents(snapshot, territoryType, mapId, mapName);

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

                if (!string.IsNullOrWhiteSpace(mapGraphSaveStatus))
                    DrawHint(mapGraphSaveStatus);
                EndCollapsibleSection();
            }

            if (BeginCollapsibleSection("离线地图画布", "缩放、拖动、落点、右键删点", true))
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
                    DrawHint("尚未读取到区域地图。打开游戏内区域地图后，插件会把标注叠在地图上。");
                }
                EndCollapsibleSection();
            }

            if (BeginCollapsibleSection($"本图手动标注（{document.Points.Count}）", "查看和删除当前地图的手动草稿点", false))
            {
                DrawMapAnnotationTable(document, territoryType, mapId);
                EndCollapsibleSection();
            }

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("图谱管理"))
        {
            if (BeginCollapsibleSection("地图与图谱", "选择地图、查看图谱覆盖情况", true))
            {
                DrawOfflineMapSelector(snapshot, offlineMap);
                DrawHint($"当前标注地图：{(!string.IsNullOrWhiteSpace(document.MapName) ? document.MapName : mapName)} / 区域 {territoryType} / 地图 {mapId}");
                DrawHint(builtInGraph != null
                    ? $"内置战术图谱：节点 {builtInGraph.PointCount} / 区域 {builtInGraph.RegionCount} / 路径 {builtInGraph.PathCount} / {builtInGraph.CoverageText}"
                    : "内置战术图谱：当前地图未配置，暂时只使用手动标注。");
                EndCollapsibleSection();
            }

            if (BeginCollapsibleSection("图谱版本管理", "保存、备份、回滚、对比和地图版本迁移", true))
            {
                DrawMapGraphVersionManager(territoryType, mapId);
                EndCollapsibleSection();
            }

            if (BeginCollapsibleSection("显示过滤", "按图谱来源、路径、节点和标注类型过滤画布", true))
            {
                DrawMapGraphDisplayFilters();
                EndCollapsibleSection();
            }

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("校准"))
        {
            if (BeginCollapsibleSection("校准辅助", "实战轨迹、点击误差、路径长度误差和坐标批量修正", true))
            {
                DrawMapCalibrationTools(snapshot, offlineMap, document, combinedPoints, selectedCurrentMap);
                EndCollapsibleSection();
            }

            if (BeginCollapsibleSection($"路径耗时（内置 + 手动 {combinedPoints.Length} 点）", "按路径编号计算长度、风险和骑乘 / 步行预计用时", false))
            {
                DrawRouteSummaryTable(plugin.MapAnnotationService.BuildRouteSummaries(combinedPoints, selectedCurrentMap ? snapshot.LocalPlayer?.Position : null));
                EndCollapsibleSection();
            }

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawMapEditorOverview(
        BattlefieldSnapshot snapshot,
        OfflineMapMetadata offlineMap,
        MapAnnotationDocument document,
        MapTacticalGraphSnapshot? builtInGraph,
        bool selectedCurrentMap)
    {
        if (ImGui.BeginTable("##MapEditorOverview", 4, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawMetricCard(
                "##MapEditorMap",
                "离线地图",
                offlineMap.DisplayName,
                new Vector4(0.66f, 0.80f, 0.98f, 1f),
                $"区域 {offlineMap.TerritoryType} / 地图 {offlineMap.MapId}");

            ImGui.TableNextColumn();
            DrawMetricCard(
                "##MapEditorCurrent",
                "当前战场",
                selectedCurrentMap ? "已对齐" : "未对齐",
                selectedCurrentMap ? new Vector4(0.50f, 0.88f, 0.66f, 1f) : new Vector4(0.95f, 0.82f, 0.35f, 1f),
                $"区域 {snapshot.TerritoryType} / 地图 {snapshot.MapId}");

            ImGui.TableNextColumn();
            DrawMetricCard(
                "##MapEditorGraph",
                "内置图谱",
                builtInGraph != null ? $"{builtInGraph.PointCount} 点" : "未配置",
                builtInGraph != null ? new Vector4(0.82f, 0.74f, 1f, 1f) : new Vector4(0.62f, 0.62f, 0.66f, 1f),
                builtInGraph != null ? $"区域 {builtInGraph.RegionCount} / 路径 {builtInGraph.PathCount}" : "仅使用手动标注");

            ImGui.TableNextColumn();
            DrawMetricCard(
                "##MapEditorDraft",
                "手动草稿",
                $"{document.Points.Count} 点",
                document.Points.Count > 0 ? new Vector4(0.38f, 0.84f, 1f, 1f) : new Vector4(0.62f, 0.62f, 0.66f, 1f),
                "当前离线地图下的草稿点");
            ImGui.EndTable();
        }

        ImGui.Spacing();
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
}
