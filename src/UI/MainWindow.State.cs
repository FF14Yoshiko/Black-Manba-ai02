using System.Collections.Generic;
using System.Numerics;
namespace ai02;

public partial class MainWindow
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
    private string aiTeacherLearningStatus = string.Empty;
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