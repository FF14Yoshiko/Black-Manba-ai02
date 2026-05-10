using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MarkerSheet = Lumina.Excel.Sheets.Marker;
using FieldMarkerSheet = Lumina.Excel.Sheets.FieldMarker;

namespace ai02;

public sealed class FrontlineRadar : IDisposable
{
    private const uint FrontlineDeadIconId = 60909;
    private const uint MaelstromMapIconId = 60359;
    private const uint TwinAdderMapIconId = 60360;
    private const uint ImmortalFlamesMapIconId = 60361;
    private const float PvpLoadingRadius = 125f;
    private const int ControlPointDecreaseIntervalSeconds = 3;
    private const double ControlPointDecreaseFactor = 0.1;
    private const double ZeroScoreDisplayDurationSeconds = 1.0;

    private static readonly uint[] JobIconBaseIds = { 62000, 62100, 62225, 62800 };
    private static readonly HashSet<uint> NeutralControlPointIconIds = new() { 60585, 60589, 60593 };

    private static readonly Dictionary<PlayerIconType, (uint IconId, float FixedScale)> PlayerIcons = new()
    {
        [PlayerIconType.Friend] = (60424, 1.8f),
        [PlayerIconType.Party] = (60421, 1f),
        [PlayerIconType.Alliance] = (60403, 1f),
        [PlayerIconType.Other] = (FrontlineDeadIconId, 1.2f),
        [PlayerIconType.PvPDead] = (FrontlineDeadIconId, 1.2f),
        [PlayerIconType.PvPMaelstrom] = (MaelstromMapIconId, 1f),
        [PlayerIconType.PvPTwinAdder] = (TwinAdderMapIconId, 1f),
        [PlayerIconType.PvPImmortalFlames] = (ImmortalFlamesMapIconId, 1f)
    };

    private readonly Plugin plugin;
    private readonly IGameGui gameGui;
    private readonly ITextureProvider textureProvider;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly AreaMapProjectionService areaMapProjectionService;
    private readonly Dictionary<Vector3, ControlPointDisplayState> controlPointStates = new();
    private long lastMapRadarDebugTicks;
    private uint lastControlPointTerritoryType;
    private bool disposed;

    public FrontlineRadar(
        Plugin plugin,
        IGameGui gameGui,
        ITextureProvider textureProvider,
        IDataManager dataManager,
        IPluginLog log,
        AreaMapProjectionService areaMapProjectionService)
    {
        this.plugin = plugin;
        this.gameGui = gameGui;
        this.textureProvider = textureProvider;
        this.dataManager = dataManager;
        this.log = log;
        this.areaMapProjectionService = areaMapProjectionService;
    }

    public void Dispose()
    {
        disposed = true;
        lastMapRadarDebugTicks = 0;
        controlPointStates.Clear();
        lastControlPointTerritoryType = 0;
    }

    public void DrawWorld()
    {
        if (disposed)
            return;

        var config = plugin.Configuration.Radar;
        var worldState = plugin.WorldStateService.GetSnapshot();
        if (!CanDraw(config, worldState))
            return;

        if (config.ScreenRadar)
            DrawScreenRadar(config, worldState);

        if (config.FieldMarkers)
            DrawFieldMarkers(worldState);
    }

    public void DrawPostUi()
    {
        if (disposed)
            return;

        var config = plugin.Configuration.Radar;
        var worldState = plugin.WorldStateService.GetSnapshot();
        if (!config.Enabled || !config.MapRadar || !CanDraw(config, worldState))
            return;

        if (!areaMapProjectionService.TryGetSnapshot(out var mapSnapshot))
        {
            LogMapRadarDebug("AreaMap is visible, but projection snapshot is unavailable");
            return;
        }

        DrawMapRadar(config, worldState, mapSnapshot);
    }

    private bool CanDraw(RadarConfiguration config, BattlefieldSnapshot worldState)
        => worldState.LocalPlayer.HasValue
            && (worldState.IsInFrontline || config.OutsideFrontline)
            && !worldState.IsAreaTransitioning;

    private void DrawScreenRadar(RadarConfiguration config, BattlefieldSnapshot worldState)
    {
        if (!worldState.LocalPlayer.HasValue)
            return;

        var localPlayer = worldState.LocalPlayer.Value;
        var drawList = ImGui.GetForegroundDrawList();

        foreach (var player in worldState.Players)
        {
            if (player.GameObjectId == localPlayer.GameObjectId || player.Relation == BattlefieldPlayerRelation.LocalPlayer)
                continue;

            if (config.HideFriendlyCharacters && worldState.IsInFrontline && IsFriendlyToLocal(player))
                continue;

            if (!gameGui.WorldToScreen(player.Position, out var screen))
                continue;

            screen = new Vector2((int)screen.X, (int)screen.Y);
            var color = GetPlayerColor(player, worldState.IsInFrontline);
            var opacity = player.IsDead ? 0.45f : 1f;

            DrawOutlinedCircle(drawList, screen, config.ScreenDotRadius, color, opacity);

            if (config.OnlyDisplayDot)
                continue;

            var textTop = screen - new Vector2(0, 34f);
            var iconX = textTop.X;
            var iconSize = ImGui.GetTextLineHeightWithSpacing() * config.JobIconScale;

            if (config.ShowJobIcons)
            {
                if (TryDrawIcon(drawList, GetJobIconId(player, config), new Vector2(iconX - iconSize - 4f, textTop.Y), iconSize))
                    iconX += iconSize * 0.5f;
            }

            if (config.TargetMarkers && TryFindTargetMarker(worldState.TargetMarkers, player.GameObjectId, out var targetMarkerIndex))
            {
                var markerIcon = GetTargetMarkerIcon(targetMarkerIndex);
                if (markerIcon != 0)
                    TryDrawIcon(drawList, markerIcon, new Vector2(iconX - iconSize - 4f, textTop.Y), iconSize);
            }

            if (config.ShowNames)
                DrawOutlinedText(drawList, textTop + new Vector2(0, iconSize + 2f), player.Name, Color(color));

            if (config.ShowCastBars && player.IsCasting && player.TotalCastTime > 0)
                DrawCastBar(drawList, screen + new Vector2(-42f, 14f), player.CurrentCastTime, player.TotalCastTime);
        }
    }

    private void DrawFieldMarkers(BattlefieldSnapshot worldState)
    {
        if (worldState.FieldMarkers.Length == 0)
            return;

        var drawList = ImGui.GetForegroundDrawList();
        var size = ImGui.GetTextLineHeightWithSpacing() * 2.2f;

        foreach (var marker in worldState.FieldMarkers)
        {
            if (!gameGui.WorldToScreen(marker.Position, out var screen))
                continue;

            var icon = GetFieldMarkerIcon(marker.Index);
            if (icon != 0)
                TryDrawIcon(drawList, icon, screen - new Vector2(size / 2f), size);
        }
    }

    private void DrawMapRadar(RadarConfiguration config, BattlefieldSnapshot worldState, AreaMapProjectionSnapshot mapSnapshot)
    {
        if (!worldState.LocalPlayer.HasValue)
        {
            DrawAreaMapFallback(mapSnapshot);
            LogMapRadarDebug("Local player snapshot is not ready");
            return;
        }

        DrawAreaMapRadar(config, worldState, worldState.LocalPlayer.Value, mapSnapshot);
    }

    private static void DrawAreaMapFallback(AreaMapProjectionSnapshot mapSnapshot)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        drawList.PushClipRect(mapSnapshot.ClipMin, mapSnapshot.ClipMax, true);
        DrawLocalMapAnchor(drawList, mapSnapshot);
        drawList.PopClipRect();
    }

    private void DrawAreaMapRadar(
        RadarConfiguration config,
        BattlefieldSnapshot worldState,
        BattlefieldPlayerSnapshot localPlayer,
        AreaMapProjectionSnapshot mapSnapshot)
    {
        var drawList = ImGui.GetBackgroundDrawList();
        drawList.PushClipRect(mapSnapshot.ClipMin, mapSnapshot.ClipMax, true);

        if (!mapSnapshot.HasReliableLocalPlayerAnchor)
        {
            DrawLocalMapAnchor(drawList, mapSnapshot);
            drawList.PopClipRect();
            LogMapRadarDebug($"AreaMap radar skipped: local player anchor is unavailable, source={mapSnapshot.Source}");
            return;
        }

        var drawnPlayers = DrawMapPlayers(drawList, config, worldState, localPlayer, mapSnapshot, out var hiddenFriendly, out var clippedPlayers);
        DrawMapObjectiveMarkers(drawList, worldState, mapSnapshot, config);

        if (config.ShowMapLoadingRange && worldState.IsInFrontline)
            DrawLoadingRange(drawList, localPlayer, mapSnapshot);

        if (drawnPlayers == 0)
            DrawLocalMapAnchor(drawList, mapSnapshot);

        drawList.PopClipRect();

        LogMapRadarDebug($"AreaMap radar drawn: source={mapSnapshot.Source}, players={worldState.Players.Length}, drawn={drawnPlayers}, hiddenFriendly={hiddenFriendly}, clipped={clippedPlayers}, scale={mapSnapshot.Scale:0.###}");
    }

    private int DrawMapPlayers(
        ImDrawListPtr drawList,
        RadarConfiguration config,
        BattlefieldSnapshot worldState,
        BattlefieldPlayerSnapshot localPlayer,
        AreaMapProjectionSnapshot mapSnapshot,
        out int hiddenFriendly,
        out int clippedPlayers)
    {
        if (mapSnapshot.MapVisionPoints.Length > 0)
            return DrawNativeMapPlayers(drawList, config, worldState, mapSnapshot, out hiddenFriendly, out clippedPlayers);

        hiddenFriendly = 0;
        clippedPlayers = Math.Max(0, worldState.Players.Length - 1);
        return 0;
    }

    private int DrawNativeMapPlayers(
        ImDrawListPtr drawList,
        RadarConfiguration config,
        BattlefieldSnapshot worldState,
        AreaMapProjectionSnapshot mapSnapshot,
        out int hiddenFriendly,
        out int clippedPlayers)
    {
        var drawnPlayers = 0;
        hiddenFriendly = 0;
        clippedPlayers = 0;

        foreach (var point in mapSnapshot.MapVisionPoints)
        {
            if (point.Relation is BattlefieldPlayerRelation.LocalPlayer or BattlefieldPlayerRelation.Unknown)
                continue;

            if (config.HideFriendlyCharacters && worldState.IsInFrontline && point.Relation == BattlefieldPlayerRelation.Friendly)
            {
                hiddenFriendly++;
                continue;
            }

            if (!mapSnapshot.IsInside(point.MapScreenPosition))
            {
                clippedPlayers++;
                continue;
            }

            var matchedPlayer = TryMatchVisionPointToPlayer(worldState.Players, point);
            DrawNativeMapPlayer(drawList, config, worldState.IsInFrontline, point, matchedPlayer);
            drawnPlayers++;
        }

        return drawnPlayers;
    }

    private void DrawMapPlayer(
        ImDrawListPtr drawList,
        RadarConfiguration config,
        bool isFrontline,
        BattlefieldPlayerSnapshot player,
        Vector2 pos)
    {
        var color = GetPlayerColor(player, isFrontline);
        var iconType = GetPlayerIconType(player, isFrontline);
        var dotIcon = PlayerIcons[iconType];
        var dotSize = GetMapDotIconSize(config, dotIcon.FixedScale);
        if (config.OnlyDisplayDot)
        {
            DrawOutlinedCircle(drawList, pos, MathF.Max(config.MapDotRadius, 4f), color, player.IsDead ? 0.45f : 1f);
            return;
        }

        if (!TryDrawIcon(drawList, dotIcon.IconId, pos - new Vector2(dotSize / 2f), dotSize))
            DrawOutlinedCircle(drawList, pos, MathF.Max(config.MapDotRadius, 4f), color, player.IsDead ? 0.45f : 1f);

        if (config.ShowJobIcons)
        {
            var jobIconSize = Math.Clamp(ImGui.GetTextLineHeightWithSpacing() * config.JobIconScale, 12f, 24f);
            var jobIconPos = isFrontline
                ? pos + new Vector2(dotSize * 0.20f, -dotSize * 0.65f)
                : pos - new Vector2(jobIconSize / 2f, dotSize * 0.70f);
            TryDrawIcon(drawList, GetJobIconId(player, config), jobIconPos, jobIconSize);
        }

        if (config.ShowNames)
            DrawOutlinedText(drawList, pos + new Vector2(0, dotSize * 0.55f), player.Name, Color(color), true);
    }

    private void DrawNativeMapPlayer(
        ImDrawListPtr drawList,
        RadarConfiguration config,
        bool isFrontline,
        BattlefieldMapVisionPointSnapshot point,
        BattlefieldPlayerSnapshot? matchedPlayer)
    {
        var pos = point.MapScreenPosition;
        var color = matchedPlayer.HasValue
            ? GetPlayerColor(matchedPlayer.Value, isFrontline)
            : GetMapVisionColor(point, isFrontline);
        var dotSize = GetNativeMapIconSize(config, point);
        if (config.OnlyDisplayDot)
        {
            DrawOutlinedCircle(drawList, pos, MathF.Max(config.MapDotRadius, 4f), color, point.IsDead ? 0.45f : 1f);
            return;
        }

        if (!TryDrawIcon(drawList, point.IconId, pos - new Vector2(dotSize / 2f), dotSize))
            DrawOutlinedCircle(drawList, pos, MathF.Max(config.MapDotRadius, 4f), color, point.IsDead ? 0.45f : 1f);

        if (config.ShowJobIcons && matchedPlayer.HasValue)
        {
            var jobIconSize = Math.Clamp(ImGui.GetTextLineHeightWithSpacing() * config.JobIconScale, 12f, 24f);
            var jobIconPos = isFrontline
                ? pos + new Vector2(dotSize * 0.20f, -dotSize * 0.65f)
                : pos - new Vector2(jobIconSize / 2f, dotSize * 0.70f);
            TryDrawIcon(drawList, GetJobIconId(matchedPlayer.Value, config), jobIconPos, jobIconSize);
        }

        if (config.ShowNames && matchedPlayer.HasValue)
            DrawOutlinedText(drawList, pos + new Vector2(0, dotSize * 0.55f), matchedPlayer.Value.Name, Color(color), true);
    }

    private void DrawMapObjectiveMarkers(
        ImDrawListPtr drawList,
        BattlefieldSnapshot worldState,
        AreaMapProjectionSnapshot mapSnapshot,
        RadarConfiguration config)
    {
        if (worldState.MapEvents.Length > 0)
        {
            DrawNativeMapMarkers(drawList, worldState, mapSnapshot, config);
            return;
        }

        if (worldState.MapObjectives.Length > 0)
        {
            foreach (var objective in worldState.MapObjectives)
            {
                if (!mapSnapshot.TryProject(objective.Position, out var pos))
                    continue;

                var text = GetObjectiveMarkerDisplayText(objective, config);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var color = objective.IsBeingFocused
                    ? new Vector4(1f, 0.42f, 0.18f, 1f)
                    : objective.IsBeingAttacked
                        ? new Vector4(1f, 0.78f, 0.26f, 1f)
                        : new Vector4(1f, 1f, 1f, 1f);
                DrawOutlinedText(drawList, pos, text, Color(color), true);
            }

            return;
        }

        foreach (var marker in worldState.MapEvents)
        {
            if (!mapSnapshot.TryProject(marker.Position, out var pos))
                continue;

            var text = GetMarkerDisplayText(marker, config);
            if (!string.IsNullOrWhiteSpace(text))
                DrawOutlinedText(drawList, pos, text, Color(new Vector4(1f, 1f, 1f, 1f)), true);
        }
    }

    private bool DrawNativeMapMarkers(
        ImDrawListPtr drawList,
        BattlefieldSnapshot worldState,
        AreaMapProjectionSnapshot mapSnapshot,
        RadarConfiguration config)
    {
        if (worldState.MapEvents.Length == 0)
            return false;

        UpdateControlPointStates(worldState);

        var drawn = false;
        foreach (var marker in worldState.MapEvents)
        {
            if (!mapSnapshot.TryProject(marker.Position, out var pos))
                continue;

            var text = GetNativeMarkerDisplayText(marker, config);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            DrawOutlinedText(drawList, pos, text, GetNativeMarkerTextColor(marker.Kind), true);
            drawn = true;
        }

        return drawn;
    }

    private void UpdateControlPointStates(BattlefieldSnapshot worldState)
    {
        if (lastControlPointTerritoryType != worldState.TerritoryType)
        {
            controlPointStates.Clear();
            lastControlPointTerritoryType = worldState.TerritoryType;
        }

        var now = DateTime.UtcNow;
        var visiblePositions = new HashSet<Vector3>();
        foreach (var marker in worldState.MapEvents)
        {
            if (marker.Kind != BattlefieldMapEventKind.ControlPoint || !marker.ScoreValue.HasValue)
                continue;

            visiblePositions.Add(marker.Position);
            switch (worldState.TerritoryType)
            {
                case 431:
                    UpdateSealRockControlPointState(marker);
                    break;
                case 888:
                case 1313:
                    UpdateTimedControlPointState(marker, now);
                    break;
            }
        }

        if (worldState.TerritoryType is 888 or 1313)
            UpdateTimedControlPointDecay(now, visiblePositions);
        else
            RemoveStaleControlPointStates(visiblePositions);
    }

    private void UpdateSealRockControlPointState(BattlefieldMapEventSnapshot marker)
    {
        if (!marker.ScoreValue.HasValue || !marker.HpPercent.HasValue)
            return;

        var percent = marker.HpPercent.Value;
        var ones = percent % 10;
        var adjustedPercent = ones is 2 or 7 ? percent + 0.5d : percent;
        if (controlPointStates.TryGetValue(marker.Position, out var existing) && existing.IconId == marker.IconId)
        {
            existing.CurrentPercent = adjustedPercent;
            existing.CurrentScore = null;
            existing.ControlStartTimeUtc = null;
            existing.ReachedZeroTimeUtc = null;
            return;
        }

        controlPointStates[marker.Position] = new ControlPointDisplayState(marker.IconId, marker.ScoreValue.Value)
        {
            CurrentPercent = adjustedPercent
        };
    }

    private void UpdateTimedControlPointState(BattlefieldMapEventSnapshot marker, DateTime now)
    {
        if (!marker.ScoreValue.HasValue)
            return;

        var isNeutral = NeutralControlPointIconIds.Contains(marker.IconId);
        if (controlPointStates.TryGetValue(marker.Position, out var existing))
        {
            if (existing.IconId == marker.IconId)
            {
                if (isNeutral)
                {
                    existing.ControlStartTimeUtc = null;
                    existing.CurrentScore = marker.ScoreValue.Value;
                    existing.CurrentPercent = null;
                    existing.ReachedZeroTimeUtc = null;
                }
                else if (!existing.ControlStartTimeUtc.HasValue)
                {
                    existing.ControlStartTimeUtc = now;
                    existing.CurrentScore = marker.ScoreValue.Value;
                    existing.CurrentPercent = null;
                    existing.ReachedZeroTimeUtc = null;
                }

                return;
            }

            controlPointStates.Remove(marker.Position);
        }

        controlPointStates[marker.Position] = new ControlPointDisplayState(marker.IconId, marker.ScoreValue.Value)
        {
            ControlStartTimeUtc = isNeutral ? null : now,
            CurrentScore = marker.ScoreValue.Value
        };
    }

    private void UpdateTimedControlPointDecay(DateTime now, HashSet<Vector3> visiblePositions)
    {
        var toRemove = new List<Vector3>();
        foreach (var entry in controlPointStates)
        {
            if (!visiblePositions.Contains(entry.Key))
            {
                toRemove.Add(entry.Key);
                continue;
            }

            var state = entry.Value;
            if (!state.ControlStartTimeUtc.HasValue)
                continue;

            var elapsedIntervals = (int)((now - state.ControlStartTimeUtc.Value).TotalSeconds / ControlPointDecreaseIntervalSeconds);
            var scorePerInterval = (int)(state.InitialScore * ControlPointDecreaseFactor);
            state.CurrentScore = Math.Max(0, state.InitialScore - elapsedIntervals * scorePerInterval);
            if (state.CurrentScore > 0)
            {
                state.ReachedZeroTimeUtc = null;
                continue;
            }

            if (!state.ReachedZeroTimeUtc.HasValue)
            {
                state.ReachedZeroTimeUtc = now;
                continue;
            }

            if ((now - state.ReachedZeroTimeUtc.Value).TotalSeconds >= ZeroScoreDisplayDurationSeconds)
                toRemove.Add(entry.Key);
        }

        foreach (var position in toRemove)
            controlPointStates.Remove(position);
    }

    private void RemoveStaleControlPointStates(HashSet<Vector3> visiblePositions)
    {
        var toRemove = new List<Vector3>();
        foreach (var position in controlPointStates.Keys)
        {
            if (!visiblePositions.Contains(position))
                toRemove.Add(position);
        }

        foreach (var position in toRemove)
            controlPointStates.Remove(position);
    }

    private static string GetMarkerDisplayText(BattlefieldMapEventSnapshot marker, RadarConfiguration config)
    {
        if (config.ShowCountdownOnMap && marker.Kind == BattlefieldMapEventKind.Countdown && marker.CountdownSeconds.HasValue)
            return $"{marker.CountdownSeconds.Value / 60:D2}:{marker.CountdownSeconds.Value % 60:D2}";

        if (config.ShowHpPercentOnMap && marker.Kind == BattlefieldMapEventKind.Health && marker.HpPercent.HasValue)
            return $"{marker.HpPercent.Value}%";

        if (config.ShowControlPointScoreOnMap && marker.Kind == BattlefieldMapEventKind.ControlPoint && marker.ScoreValue.HasValue)
            return marker.ScoreValue.Value.ToString();

        return string.Empty;
    }

    private string GetNativeMarkerDisplayText(BattlefieldMapEventSnapshot marker, RadarConfiguration config)
    {
        if (config.ShowCountdownOnMap && marker.Kind == BattlefieldMapEventKind.Countdown && marker.CountdownSeconds.HasValue)
            return $"{marker.CountdownSeconds.Value / 60:D2}:{marker.CountdownSeconds.Value % 60:D2}";

        if (config.ShowHpPercentOnMap && marker.Kind == BattlefieldMapEventKind.Health && marker.HpPercent.HasValue)
            return $"{marker.HpPercent.Value}%";

        if (config.ShowControlPointScoreOnMap
            && marker.Kind == BattlefieldMapEventKind.ControlPoint
            && controlPointStates.TryGetValue(marker.Position, out var controlState))
        {
            var displayScore = controlState.DisplayScore;
            if (displayScore > 0d || (controlState.ControlStartTimeUtc.HasValue && Math.Abs(displayScore) < double.Epsilon))
                return ((int)Math.Round(displayScore)).ToString();
        }

        return string.Empty;
    }

    private static uint GetNativeMarkerTextColor(BattlefieldMapEventKind kind)
        => kind switch
        {
            BattlefieldMapEventKind.Countdown => Color(new Vector4(0.86f, 0.72f, 0.18f, 1f)),
            BattlefieldMapEventKind.Health => Color(new Vector4(0.86f, 0.24f, 0.34f, 1f)),
            BattlefieldMapEventKind.ControlPoint => Color(new Vector4(0.20f, 0.72f, 1f, 1f)),
            _ => Color(new Vector4(1f, 1f, 1f, 1f)),
        };

    private static string GetObjectiveMarkerDisplayText(BattlefieldMapObjectiveSnapshot objective, RadarConfiguration config)
    {
        var parts = new List<string>(4);
        if (config.ShowControlPointScoreOnMap)
        {
            if (!string.IsNullOrWhiteSpace(objective.RankName))
                parts.Add(objective.RankName);
            else if (objective.ScoreValue.HasValue)
                parts.Add(objective.ScoreValue.Value.ToString());
        }

        if (config.ShowCountdownOnMap && objective.RemainingSeconds.HasValue)
            parts.Add($"{objective.RemainingSeconds.Value / 60:D2}:{objective.RemainingSeconds.Value % 60:D2}");

        if (config.ShowHpPercentOnMap && objective.HpPercent.HasValue)
            parts.Add($"{objective.HpPercent.Value}%");

        if (objective.Category == BattlefieldMapObjectiveCategory.Ice && objective.RecentHpLossPerSecond > 0f)
            parts.Add($"{objective.RecentHpLossPerSecond:0}/s");

        if (objective.IsBeingFocused)
            parts.Add($"集火{Math.Max(objective.AttackerCount, objective.CasterCount)}");
        else if (objective.IsBeingAttacked && objective.Category == BattlefieldMapObjectiveCategory.Ice)
            parts.Add("打冰");

        return string.Join(" ", parts);
    }

    private static BattlefieldPlayerSnapshot? TryMatchVisionPointToPlayer(
        BattlefieldPlayerSnapshot[] players,
        BattlefieldMapVisionPointSnapshot point)
    {
        BattlefieldPlayerSnapshot? best = null;
        var bestDistance = 999f;
        foreach (var player in players)
        {
            if (player.Relation is BattlefieldPlayerRelation.LocalPlayer or BattlefieldPlayerRelation.Unknown)
                continue;
            if (player.IsDead != point.IsDead && !point.IsDead)
                continue;
            if (point.Battalion.HasValue && player.Battalion != point.Battalion)
                continue;
            if (point.Relation == BattlefieldPlayerRelation.Friendly && !IsFriendlyToLocal(player))
                continue;
            if (point.Relation == BattlefieldPlayerRelation.Enemy && IsFriendlyToLocal(player))
                continue;

            var distance = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), new Vector2(point.EstimatedWorldPosition.X, point.EstimatedWorldPosition.Z));
            if (distance >= bestDistance)
                continue;

            best = player;
            bestDistance = distance;
        }

        if (best.HasValue && bestDistance <= (point.IsDead ? 28f : 18f))
            return best;

        return null;
    }

    private static void DrawLocalMapAnchor(ImDrawListPtr drawList, AreaMapProjectionSnapshot mapSnapshot)
    {
        var radius = 3.5f;
        var color = Color(new Vector4(0.10f, 0.95f, 1f, 0.90f));
        var outline = Color(new Vector4(0f, 0f, 0f, 0.90f));
        drawList.AddCircleFilled(mapSnapshot.LocalPlayerAnchor, radius, color, 16);
        drawList.AddCircle(mapSnapshot.LocalPlayerAnchor, radius + 1f, outline, 16, 1.5f);
    }

    private void DrawLoadingRange(ImDrawListPtr drawList, BattlefieldPlayerSnapshot localPlayer, AreaMapProjectionSnapshot mapSnapshot)
    {
        var edge = localPlayer.Position + new Vector3(PvpLoadingRadius, 0f, 0f);
        if (!mapSnapshot.TryProject(edge, out var edgeScreen))
            return;

        var radius = Vector2.Distance(mapSnapshot.LocalPlayerAnchor, edgeScreen);
        drawList.AddCircle(mapSnapshot.LocalPlayerAnchor, radius, Color(new Vector4(0.55f, 0.55f, 0.55f, 0.55f)), 96, 2f);
    }

    private void LogMapRadarDebug(string message)
    {
        var now = Environment.TickCount64;
        if (now - lastMapRadarDebugTicks < 5000)
            return;

        lastMapRadarDebugTicks = now;
        log.Debug($"[FrontlineRadar] {message}");
    }

    private static bool TryFindTargetMarker(BattlefieldTargetMarkerSnapshot[] targetMarkers, ulong gameObjectId, out uint index)
    {
        foreach (var marker in targetMarkers)
        {
            if (marker.TargetGameObjectId == gameObjectId)
            {
                index = marker.Index;
                return true;
            }
        }

        index = 0;
        return false;
    }

    private static bool IsFriendlyToLocal(BattlefieldPlayerSnapshot player)
        => player.Relation is BattlefieldPlayerRelation.LocalPlayer or BattlefieldPlayerRelation.Friendly
            || player.IsPartyMember
            || player.IsAllianceMember
            || player.IsFriend;

    private static PlayerIconType GetPlayerIconType(BattlefieldPlayerSnapshot player, bool isFrontline)
    {
        if (isFrontline)
        {
            if (player.IsDead)
                return PlayerIconType.PvPDead;

            return player.Battalion switch
            {
                0 => PlayerIconType.PvPMaelstrom,
                1 => PlayerIconType.PvPTwinAdder,
                2 => PlayerIconType.PvPImmortalFlames,
                _ => PlayerIconType.PvPDead,
            };
        }

        if (player.IsFriend)
            return PlayerIconType.Friend;
        if (player.IsPartyMember)
            return PlayerIconType.Party;
        if (player.IsAllianceMember)
            return PlayerIconType.Alliance;

        return PlayerIconType.Other;
    }

    private static PlayerIconType GetMapVisionIconType(BattlefieldMapVisionPointSnapshot point, bool isFrontline)
    {
        if (isFrontline)
        {
            if (point.IsDead)
                return PlayerIconType.PvPDead;

            return point.Battalion switch
            {
                0 => PlayerIconType.PvPMaelstrom,
                1 => PlayerIconType.PvPTwinAdder,
                2 => PlayerIconType.PvPImmortalFlames,
                _ => PlayerIconType.PvPDead,
            };
        }

        return point.Relation switch
        {
            BattlefieldPlayerRelation.Friendly => PlayerIconType.Friend,
            _ => PlayerIconType.Other,
        };
    }

    private static Vector4 GetPlayerColor(BattlefieldPlayerSnapshot player, bool isFrontline)
    {
        if (!isFrontline)
        {
            if (player.IsFriend)
                return new Vector4(1f, 0.55f, 0.10f, 1f);
            if (player.IsPartyMember)
                return new Vector4(0.15f, 0.85f, 1f, 1f);
            if (player.IsAllianceMember)
                return new Vector4(0.30f, 0.90f, 0.30f, 1f);

            return new Vector4(1f, 1f, 1f, 1f);
        }

        return player.Battalion switch
        {
            0 => new Vector4(1f, 0.22f, 0.22f, 1f),
            1 => new Vector4(0.95f, 0.75f, 0.15f, 1f),
            2 => new Vector4(0.20f, 0.62f, 1f, 1f),
            _ => new Vector4(1f, 1f, 1f, 1f),
        };
    }

    private static Vector4 GetMapVisionColor(BattlefieldMapVisionPointSnapshot point, bool isFrontline)
    {
        if (!isFrontline)
        {
            return point.Relation switch
            {
                BattlefieldPlayerRelation.Friendly => new Vector4(1f, 0.55f, 0.10f, 1f),
                _ => new Vector4(1f, 1f, 1f, 1f),
            };
        }

        return point.Battalion switch
        {
            0 => new Vector4(1f, 0.22f, 0.22f, 1f),
            1 => new Vector4(0.95f, 0.75f, 0.15f, 1f),
            2 => new Vector4(0.20f, 0.62f, 1f, 1f),
            _ => new Vector4(1f, 1f, 1f, 1f),
        };
    }

    private static float GetMapDotIconSize(RadarConfiguration config, float fixedScale)
        => Math.Clamp(config.MapDotRadius * 8f * fixedScale, 12f, 42f);

    private static float GetNativeMapIconSize(RadarConfiguration config, BattlefieldMapVisionPointSnapshot point)
    {
        var fixedScale = point.IconId switch
        {
            60424 => 1.8f,
            60909 => 1.2f,
            _ => 1f,
        };
        return GetMapDotIconSize(config, fixedScale);
    }

    private uint GetJobIconId(BattlefieldPlayerSnapshot player, RadarConfiguration config)
    {
        var style = Math.Clamp(config.JobIconStyle, 1, 4);
        return JobIconBaseIds[style - 1] + player.ClassJobId;
    }

    private uint GetTargetMarkerIcon(uint index)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<MarkerSheet>();
            return sheet.TryGetRow(index + 1, out var row) ? (uint)row.Icon : 0;
        }
        catch
        {
            return 0;
        }
    }

    private uint GetFieldMarkerIcon(uint index)
    {
        try
        {
            var sheet = dataManager.GetExcelSheet<FieldMarkerSheet>();
            return sheet.TryGetRow(index + 1, out var row) ? (uint)row.UiIcon : 0;
        }
        catch
        {
            return 0;
        }
    }

    private bool TryDrawIcon(ImDrawListPtr drawList, uint iconId, Vector2 topLeft, float size)
    {
        if (iconId == 0)
            return false;

        var lookup = new GameIconLookup(iconId, false, true, (ClientLanguage?)null);
        if (!textureProvider.TryGetFromGameIcon(lookup, out var texture))
            return false;

        var wrap = texture.GetWrapOrEmpty();
        drawList.AddImage(wrap.Handle, topLeft, topLeft + new Vector2(size), Vector2.Zero, Vector2.One, Color(new Vector4(1f, 1f, 1f, 1f)));
        return true;
    }

    private static void DrawOutlinedCircle(ImDrawListPtr drawList, Vector2 center, float radius, Vector4 color, float opacity)
    {
        color.W *= opacity;
        drawList.AddCircleFilled(center, radius, Color(color), 24);
        drawList.AddCircle(center, radius, Color(new Vector4(0f, 0f, 0f, Math.Clamp(opacity, 0.25f, 1f))), 24, 1.5f);
    }

    private static void DrawCastBar(ImDrawListPtr drawList, Vector2 topLeft, float current, float total)
    {
        var progress = total <= 0 ? 0 : Math.Clamp(current / total, 0f, 1f);
        var size = new Vector2(84f, 7f);
        drawList.AddRectFilled(topLeft, topLeft + size, Color(new Vector4(0f, 0f, 0f, 0.75f)), 2f);
        drawList.AddRectFilled(topLeft, topLeft + new Vector2(size.X * progress, size.Y), Color(new Vector4(0.55f, 0.78f, 1f, 0.95f)), 2f);
        drawList.AddRect(topLeft, topLeft + size, Color(new Vector4(1f, 1f, 1f, 0.85f)), 2f);
    }

    private static void DrawOutlinedText(ImDrawListPtr drawList, Vector2 pos, string text, uint color, bool center = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (center)
            pos -= new Vector2(ImGui.CalcTextSize(text).X / 2f, 0f);

        var black = Color(new Vector4(0f, 0f, 0f, 1f));
        drawList.AddText(pos + new Vector2(-1f, -1f), black, text);
        drawList.AddText(pos + new Vector2(-1f, 1f), black, text);
        drawList.AddText(pos + new Vector2(1f, -1f), black, text);
        drawList.AddText(pos + new Vector2(1f, 1f), black, text);
        drawList.AddText(pos, color, text);
    }

    private static uint Color(Vector4 color)
        => ImGui.ColorConvertFloat4ToU32(color);

    private enum PlayerIconType
    {
        Friend,
        Party,
        Alliance,
        Other,
        PvPDead,
        PvPMaelstrom,
        PvPTwinAdder,
        PvPImmortalFlames
    }

    private sealed class ControlPointDisplayState
    {
        public ControlPointDisplayState(uint iconId, int initialScore)
        {
            IconId = iconId;
            InitialScore = initialScore;
            CurrentScore = initialScore;
        }

        public uint IconId { get; set; }
        public int InitialScore { get; }
        public int? CurrentScore { get; set; }
        public double? CurrentPercent { get; set; }
        public DateTime? ControlStartTimeUtc { get; set; }
        public DateTime? ReachedZeroTimeUtc { get; set; }

        public double DisplayScore
            => CurrentPercent.HasValue
                ? InitialScore * CurrentPercent.Value / 100d
                : CurrentScore.GetValueOrDefault(InitialScore);
    }
}
