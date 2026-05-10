using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ai02;

[Serializable]
public class Configuration : IPluginConfiguration
{
    private const int CurrentVersion = 24;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public int Version { get; set; } = 0;

    public RadarConfiguration Radar { get; set; } = new();
    public FloatingButtonConfiguration FloatingButton { get; set; } = new();
    public SightDistanceConfiguration SightDistance { get; set; } = new();
    public LimitBreakConfiguration LimitBreak { get; set; } = new();
    public CommandOverlayConfiguration CommandOverlay { get; set; } = new();
    public BattlefieldAnnouncementConfiguration Announcement { get; set; } = new();
    public ScoreReaderConfiguration ScoreReader { get; set; } = new();
    public BattleHighConfiguration BattleHigh { get; set; } = new();
    public TacticalStatusConfiguration TacticalStatus { get; set; } = new();
    public BattlefieldReplayConfiguration Replay { get; set; } = new();
    public AdvancedTacticsCalibrationConfiguration AdvancedTactics { get; set; } = new();
    public PerformanceConfiguration Performance { get; set; } = new();
    public LlmDecisionConfiguration LlmDecision { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        var migrated = NormalizeAndMigrate();
        if (migrated)
            Save();
    }

    public void Save()
    {
        pluginInterface!.SavePluginConfig(this);
    }

    public void ApplyImportedConfiguration(Configuration imported)
    {
        Radar = imported.Radar ?? new RadarConfiguration();
        FloatingButton = imported.FloatingButton ?? new FloatingButtonConfiguration();
        SightDistance = imported.SightDistance ?? new SightDistanceConfiguration();
        LimitBreak = imported.LimitBreak ?? new LimitBreakConfiguration();
        CommandOverlay = imported.CommandOverlay ?? new CommandOverlayConfiguration();
        Announcement = imported.Announcement ?? new BattlefieldAnnouncementConfiguration();
        ScoreReader = imported.ScoreReader ?? new ScoreReaderConfiguration();
        BattleHigh = imported.BattleHigh ?? new BattleHighConfiguration();
        TacticalStatus = imported.TacticalStatus ?? new TacticalStatusConfiguration();
        Replay = imported.Replay ?? new BattlefieldReplayConfiguration();
        AdvancedTactics = imported.AdvancedTactics ?? new AdvancedTacticsCalibrationConfiguration();
        Performance = imported.Performance ?? new PerformanceConfiguration();
        LlmDecision = imported.LlmDecision ?? new LlmDecisionConfiguration();
        Version = imported.Version;

        NormalizeAndMigrate();
        Save();
    }

    public bool ExportToFile(string path, out string message)
    {
        try
        {
            File.WriteAllText(path, ExportToJson());
            message = $"配置已导出：{path}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"配置导出失败：{ex.Message}";
            return false;
        }
    }

    public bool TryImportFromFile(string path, out string message)
    {
        try
        {
            if (!File.Exists(path))
            {
                message = $"未找到配置文件：{path}";
                return false;
            }

            var json = File.ReadAllText(path);
            if (!TryDeserializeImportedConfiguration(json, out var imported, out message))
                return false;

            ApplyImportedConfiguration(imported);
            message = $"配置已导入：{Path.GetFileName(path)}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"配置导入失败：{ex.Message}";
            return false;
        }
    }

    public string ExportToJson()
    {
        var document = new ConfigurationExportDocument
        {
            SchemaVersion = 1,
            PluginInternalName = "ai02",
            PluginName = "前线战术指挥",
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            ConfigurationVersion = Version,
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Configuration = this
        };

        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public void ApplyLowImpactDefaults()
    {
        Radar ??= new RadarConfiguration();
        LimitBreak ??= new LimitBreakConfiguration();
        Announcement ??= new BattlefieldAnnouncementConfiguration();
        ScoreReader ??= new ScoreReaderConfiguration();
        BattleHigh ??= new BattleHighConfiguration();
        TacticalStatus ??= new TacticalStatusConfiguration();
        Replay ??= new BattlefieldReplayConfiguration();
        AdvancedTactics ??= new AdvancedTacticsCalibrationConfiguration();
        Performance ??= new PerformanceConfiguration();
        LlmDecision ??= new LlmDecisionConfiguration();

        Performance.ApplyLowImpactDefaults();
        Replay.RecordIntervalSeconds = Math.Max(Replay.RecordIntervalSeconds, BattlefieldReplayConfiguration.LowImpactRecordIntervalSeconds);
        Replay.Enabled = false;
        Replay.IncludePlayerDetails = false;
        Replay.ShowDebugInfo = false;
        ScoreReader.ShowRawSourceDebug = false;
        LimitBreak.ShowDebugInfo = false;
        BattleHigh.ShowDebugInfo = false;
        BattleHigh.ShowAllVisibleStatusesInDebug = false;
        TacticalStatus.ShowDebugInfo = false;
        TacticalStatus.ShowAllVisibleStatusesInDebug = false;
        Announcement.ShowDebugInfo = false;
        AdvancedTactics.ShowSuppressedInsights = false;
        Radar.ScreenRadar = false;
        Radar.MapRadar = false;
        Radar.FieldMarkers = false;
        Radar.TargetMarkers = false;
        Radar.AutoMarkFocusTarget = false;
        Radar.OnlyDisplayDot = true;
        Radar.ShowNames = false;
        Radar.ShowJobIcons = false;
        Radar.ShowCastBars = false;
        Radar.ShowHpPercentOnMap = false;
        Radar.ShowMapLoadingRange = false;
        LimitBreak.ShowLimitBreakUI = false;
    }

    private bool NormalizeAndMigrate()
    {
        var migrated = false;
        EnsureSections();

        if (Version < 1)
        {
            Radar.ImportDailyRoutinesDefaultsIfPresent();
            migrated = true;
        }

        if (Version < 14 && Replay.RecordIntervalSeconds < BattlefieldReplayConfiguration.DefaultRecordIntervalSeconds)
        {
            Replay.RecordIntervalSeconds = BattlefieldReplayConfiguration.DefaultRecordIntervalSeconds;
            migrated = true;
        }
        if (Version < 15)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 16)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 17)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 18)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 19 && Performance.RestoreDecisionResponsiveDefaultsIfNeeded())
        {
            migrated = true;
        }
        if (Version < 20)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 21)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 22)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 23)
        {
            ApplyLowImpactDefaults();
            migrated = true;
        }
        if (Version < 24)
        {
            LlmDecision ??= new LlmDecisionConfiguration();
            migrated = true;
        }

        ScoreReader.Normalize();
        CommandOverlay.Normalize();
        BattleHigh.Normalize();
        TacticalStatus.Normalize();
        Replay.Normalize();
        AdvancedTactics.Normalize();
        Performance.Normalize();
        LlmDecision.Normalize();

        if (Version != CurrentVersion)
        {
            Version = CurrentVersion;
            migrated = true;
        }

        return migrated;
    }

    private void EnsureSections()
    {
        Radar ??= new RadarConfiguration();
        FloatingButton ??= new FloatingButtonConfiguration();
        SightDistance ??= new SightDistanceConfiguration();
        LimitBreak ??= new LimitBreakConfiguration();
        CommandOverlay ??= new CommandOverlayConfiguration();
        Announcement ??= new BattlefieldAnnouncementConfiguration();
        ScoreReader ??= new ScoreReaderConfiguration();
        BattleHigh ??= new BattleHighConfiguration();
        TacticalStatus ??= new TacticalStatusConfiguration();
        Replay ??= new BattlefieldReplayConfiguration();
        AdvancedTactics ??= new AdvancedTacticsCalibrationConfiguration();
        Performance ??= new PerformanceConfiguration();
        LlmDecision ??= new LlmDecisionConfiguration();
    }

    private static bool TryDeserializeImportedConfiguration(string json, out Configuration configuration, out string message)
    {
        configuration = new Configuration();
        message = string.Empty;

        using var document = JsonDocument.Parse(json);
        if (TryGetPropertyIgnoreCase(document.RootElement, "Configuration", out var importedElement))
        {
            configuration = importedElement.Deserialize<Configuration>(JsonOptions) ?? new Configuration();
            return true;
        }

        configuration = JsonSerializer.Deserialize<Configuration>(json, JsonOptions) ?? new Configuration();
        return true;
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }
}

[Serializable]
public sealed class ConfigurationExportDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string PluginInternalName { get; set; } = "ai02";
    public string PluginName { get; set; } = "前线战术指挥";
    public string PluginVersion { get; set; } = string.Empty;
    public int ConfigurationVersion { get; set; }
    public DateTimeOffset ExportedAtUtc { get; set; }
    public Configuration Configuration { get; set; } = new();
}

[Serializable]
public class LlmDecisionConfiguration
{
    private const int PreviousDefaultMinIntervalSeconds = 10;
    private const int PreviousDefaultSameSituationCooldownSeconds = 15;
    private const int PreviousDefaultFreshDecisionSeconds = 55;
    private const int RelaxedDefaultMinIntervalSeconds = 8;
    private const int RelaxedDefaultSameSituationCooldownSeconds = 14;
    private const int LegacyHighDefaultMinIntervalSeconds = 18;
    private const int LegacyHighDefaultSameSituationCooldownSeconds = 36;

    public bool Enabled { get; set; } = true;
    public string ProviderBaseUrl { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-v4-flash";
    public string ApiKeyEnvironmentVariable { get; set; } = "DEEPSEEK_API_KEY";
    public string ApiKey { get; set; } = string.Empty;
    public int RequestTimeoutMs { get; set; } = 4500;
    public int MinIntervalSeconds { get; set; } = 5;
    public int SameSituationCooldownSeconds { get; set; } = 8;
    public int FreshDecisionSeconds { get; set; } = 40;
    public int MaxContextTurns { get; set; } = 6;
    public bool IncludeDebugPayload { get; set; } = true;

    public void Normalize()
    {
        ProviderBaseUrl = string.IsNullOrWhiteSpace(ProviderBaseUrl)
            ? "https://api.deepseek.com"
            : ProviderBaseUrl.Trim().TrimEnd('/');
        Model = string.IsNullOrWhiteSpace(Model) ? "deepseek-v4-flash" : Model.Trim();
        ApiKeyEnvironmentVariable = string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
            ? "DEEPSEEK_API_KEY"
            : ApiKeyEnvironmentVariable.Trim();
        ApiKey = ApiKey?.Trim() ?? string.Empty;
        if (MinIntervalSeconds == PreviousDefaultMinIntervalSeconds
            || MinIntervalSeconds == RelaxedDefaultMinIntervalSeconds
            || MinIntervalSeconds == LegacyHighDefaultMinIntervalSeconds)
            MinIntervalSeconds = 5;
        if (SameSituationCooldownSeconds == PreviousDefaultSameSituationCooldownSeconds
            || SameSituationCooldownSeconds == RelaxedDefaultSameSituationCooldownSeconds
            || SameSituationCooldownSeconds == LegacyHighDefaultSameSituationCooldownSeconds)
            SameSituationCooldownSeconds = 8;
        if (FreshDecisionSeconds == PreviousDefaultFreshDecisionSeconds)
            FreshDecisionSeconds = 40;
        RequestTimeoutMs = Math.Clamp(RequestTimeoutMs, 1500, 15000);
        MinIntervalSeconds = Math.Clamp(MinIntervalSeconds, 3, 120);
        SameSituationCooldownSeconds = Math.Clamp(SameSituationCooldownSeconds, 5, 180);
        FreshDecisionSeconds = Math.Clamp(FreshDecisionSeconds, 10, 180);
        MaxContextTurns = Math.Clamp(MaxContextTurns, 0, 12);
    }
}

[Serializable]
public class PerformanceConfiguration
{
    public const int LowImpactWorldRefreshIntervalMs = 4000;
    public const int LowImpactCombatRefreshIntervalMs = 1200;
    public const int LowImpactScoreScanIntervalMs = 4000;
    public const int LowImpactStatusScanIntervalMs = 8000;
    private const int TemporaryLowImpactWorldRefreshIntervalMs = 2200;
    private const int TemporaryLowImpactScoreScanIntervalMs = 2500;
    private const int TemporaryLowImpactStatusScanIntervalMs = 4500;
    private const int PreviousLowImpactAreaMapSampleIntervalMs = 2400;
    public const int LowImpactAreaMapSampleIntervalMs = 8000;
    public const int LowImpactLimitBreakSampleIntervalMs = 2500;
    public const int LowImpactDecisionRefreshIntervalMs = 8000;
    private const int TemporaryLowImpactAreaMapSampleIntervalMs = 900;
    private const int TemporaryLowImpactLimitBreakSampleIntervalMs = 1200;
    private const int TemporaryLowImpactDecisionRefreshIntervalMs = 6000;

    public bool LowImpactMode { get; set; }
    public int WorldRefreshIntervalMs { get; set; } = 1910;
    public int CombatRefreshIntervalMs { get; set; } = 1155;
    public int ScoreScanIntervalMs { get; set; } = 1623;
    public int StatusScanIntervalMs { get; set; } = 3800;
    public int AreaMapSampleIntervalMs { get; set; } = 533;
    public int LimitBreakSampleIntervalMs { get; set; } = 1142;
    public int DecisionRefreshIntervalMs { get; set; } = 2748;
    public bool EnableDecisionQualityFeedback { get; set; }

    public int EffectiveWorldRefreshIntervalMs => ResolveEffective(WorldRefreshIntervalMs, 250, 5000, LowImpactWorldRefreshIntervalMs);

    public int EffectiveCombatRefreshIntervalMs => ResolveEffective(CombatRefreshIntervalMs, 500, 5000, LowImpactCombatRefreshIntervalMs);

    public int EffectiveScoreScanIntervalMs => ResolveEffective(ScoreScanIntervalMs, 250, 10000, LowImpactScoreScanIntervalMs);

    public int EffectiveStatusScanIntervalMs => ResolveEffective(StatusScanIntervalMs, 250, 10000, LowImpactStatusScanIntervalMs);

    public int EffectiveAreaMapSampleIntervalMs => ResolveEffective(AreaMapSampleIntervalMs, 100, 10000, LowImpactAreaMapSampleIntervalMs);

    public int EffectiveLimitBreakSampleIntervalMs => ResolveEffective(LimitBreakSampleIntervalMs, 100, 3000, LowImpactLimitBreakSampleIntervalMs);

    public int EffectiveDecisionRefreshIntervalMs => ResolveEffective(DecisionRefreshIntervalMs, 1000, 10000, LowImpactDecisionRefreshIntervalMs);

    public void ApplyLowImpactDefaults()
    {
        LowImpactMode = true;
        WorldRefreshIntervalMs = Math.Max(WorldRefreshIntervalMs, LowImpactWorldRefreshIntervalMs);
        CombatRefreshIntervalMs = Math.Max(CombatRefreshIntervalMs, LowImpactCombatRefreshIntervalMs);
        ScoreScanIntervalMs = Math.Max(ScoreScanIntervalMs, LowImpactScoreScanIntervalMs);
        StatusScanIntervalMs = Math.Max(StatusScanIntervalMs, LowImpactStatusScanIntervalMs);
        AreaMapSampleIntervalMs = Math.Max(AreaMapSampleIntervalMs, LowImpactAreaMapSampleIntervalMs);
        LimitBreakSampleIntervalMs = Math.Max(LimitBreakSampleIntervalMs, LowImpactLimitBreakSampleIntervalMs);
        DecisionRefreshIntervalMs = Math.Max(DecisionRefreshIntervalMs, LowImpactDecisionRefreshIntervalMs);
        EnableDecisionQualityFeedback = false;
        Normalize();
    }

    public bool RestoreDecisionResponsiveDefaultsIfNeeded()
    {
        var changed = false;
        if (WorldRefreshIntervalMs == TemporaryLowImpactWorldRefreshIntervalMs)
        {
            WorldRefreshIntervalMs = LowImpactWorldRefreshIntervalMs;
            changed = true;
        }

        if (ScoreScanIntervalMs == TemporaryLowImpactScoreScanIntervalMs)
        {
            ScoreScanIntervalMs = LowImpactScoreScanIntervalMs;
            changed = true;
        }

        if (StatusScanIntervalMs == TemporaryLowImpactStatusScanIntervalMs)
        {
            StatusScanIntervalMs = LowImpactStatusScanIntervalMs;
            changed = true;
        }

        if (AreaMapSampleIntervalMs == TemporaryLowImpactAreaMapSampleIntervalMs)
        {
            AreaMapSampleIntervalMs = LowImpactAreaMapSampleIntervalMs;
            changed = true;
        }

        if (LimitBreakSampleIntervalMs == TemporaryLowImpactLimitBreakSampleIntervalMs)
        {
            LimitBreakSampleIntervalMs = LowImpactLimitBreakSampleIntervalMs;
            changed = true;
        }

        if (DecisionRefreshIntervalMs == TemporaryLowImpactDecisionRefreshIntervalMs)
        {
            DecisionRefreshIntervalMs = LowImpactDecisionRefreshIntervalMs;
            changed = true;
        }

        if (changed)
            Normalize();

        return changed;
    }

    public void Normalize()
    {
        WorldRefreshIntervalMs = Math.Clamp(WorldRefreshIntervalMs, 250, 5000);
        CombatRefreshIntervalMs = Math.Clamp(CombatRefreshIntervalMs, 500, 5000);
        ScoreScanIntervalMs = Math.Clamp(ScoreScanIntervalMs, 250, 10000);
        StatusScanIntervalMs = Math.Clamp(StatusScanIntervalMs, 250, 10000);
        AreaMapSampleIntervalMs = Math.Clamp(AreaMapSampleIntervalMs, 100, 10000);
        LimitBreakSampleIntervalMs = Math.Clamp(LimitBreakSampleIntervalMs, 100, 3000);
        DecisionRefreshIntervalMs = Math.Clamp(DecisionRefreshIntervalMs, 1000, 10000);
        if (LowImpactMode && AreaMapSampleIntervalMs == PreviousLowImpactAreaMapSampleIntervalMs)
            AreaMapSampleIntervalMs = LowImpactAreaMapSampleIntervalMs;
        if (!LowImpactMode)
            return;

        WorldRefreshIntervalMs = Math.Max(WorldRefreshIntervalMs, LowImpactWorldRefreshIntervalMs);
        CombatRefreshIntervalMs = Math.Max(CombatRefreshIntervalMs, LowImpactCombatRefreshIntervalMs);
        ScoreScanIntervalMs = Math.Max(ScoreScanIntervalMs, LowImpactScoreScanIntervalMs);
        StatusScanIntervalMs = Math.Max(StatusScanIntervalMs, LowImpactStatusScanIntervalMs);
        AreaMapSampleIntervalMs = Math.Max(AreaMapSampleIntervalMs, LowImpactAreaMapSampleIntervalMs);
        LimitBreakSampleIntervalMs = Math.Max(LimitBreakSampleIntervalMs, LowImpactLimitBreakSampleIntervalMs);
        DecisionRefreshIntervalMs = Math.Max(DecisionRefreshIntervalMs, LowImpactDecisionRefreshIntervalMs);
    }

    private int ResolveEffective(int value, int min, int max, int lowImpactMin)
    {
        var clamped = Math.Clamp(value, min, max);
        return LowImpactMode ? Math.Max(clamped, lowImpactMin) : clamped;
    }
}

[Serializable]
public class CommandOverlayConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool OnlyInFrontline { get; set; }
    public bool ShowPrimaryWhenIdle { get; set; }
    public bool ShowReason { get; set; }
    public bool ShowStroke { get; set; } = true;
    public bool ShowBackground { get; set; }
    public float X { get; set; } = 410.097f;
    public float Y { get; set; } = 283.107f;
    public float Width { get; set; } = 1396.311f;
    public float Height { get; set; } = 366.214f;
    public float FontScale { get; set; } = 3.002f;
    public int PublishedHoldSeconds { get; set; } = 5;
    public float TextColorR { get; set; } = 1f;
    public float TextColorG { get; set; } = 0.86f;
    public float TextColorB { get; set; } = 0.22f;
    public float StrokeColorR { get; set; }
    public float StrokeColorG { get; set; }
    public float StrokeColorB { get; set; }
    public float BackgroundAlpha { get; set; } = 0.615f;

    public void Normalize()
    {
        X = Math.Clamp(X, 0f, 7680f);
        Y = Math.Clamp(Y, 0f, 4320f);
        Width = Math.Clamp(Width, 260f, 2400f);
        Height = Math.Clamp(Height, 80f, 900f);
        FontScale = Math.Clamp(FontScale, 0.8f, 5f);
        PublishedHoldSeconds = Math.Clamp(PublishedHoldSeconds, 1, 20);
        TextColorR = Math.Clamp(TextColorR, 0f, 1f);
        TextColorG = Math.Clamp(TextColorG, 0f, 1f);
        TextColorB = Math.Clamp(TextColorB, 0f, 1f);
        StrokeColorR = Math.Clamp(StrokeColorR, 0f, 1f);
        StrokeColorG = Math.Clamp(StrokeColorG, 0f, 1f);
        StrokeColorB = Math.Clamp(StrokeColorB, 0f, 1f);
        BackgroundAlpha = Math.Clamp(BackgroundAlpha, 0f, 0.85f);
    }
}

[Serializable]
public class AdvancedTacticsCalibrationConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool ShowSuppressedInsights { get; set; }
    public int MinAlertConfidencePercent { get; set; } = 50;
    public float RetreatMinDistanceDelta { get; set; } = 4f;
    public float RetreatMinSpeed { get; set; } = 0.9f;
    public int RetreatMovingAwayRatioPercent { get; set; } = 55;
    public int RetreatMinMovingSamples { get; set; } = 3;
    public float RetreatMinSeverity { get; set; } = 42f;
    public float FakeRetreatMinSeverity { get; set; } = 58f;
    public int FakeRetreatMinThreatSignals { get; set; } = 1;
    public float FakeRetreatSideClusterDistance { get; set; } = 125f;
    public int FakeRetreatSideClusterMinCount { get; set; } = 3;
    public float HighGroundMinHeightDelta { get; set; } = 4.5f;
    public int HighGroundMinPressure { get; set; } = 3;
    public float HighGroundMinSeverity { get; set; } = 55f;
    public float ThirdPartyMaxDistance { get; set; } = 175f;
    public float ThirdPartyMinAngle { get; set; } = 92f;
    public float ThirdPartyMinSeverity { get; set; } = 55f;
    public float SquadSearchRadius { get; set; } = 30f;
    public float SquadMinScore { get; set; } = 58f;
    public int SquadMinDirectionSimilarityPercent { get; set; } = 68;
    public float SquadMinFormationStability { get; set; } = 48f;
    public int ChokeMinPressure { get; set; } = 3;
    public float ChokeMinSeverity { get; set; } = 55f;
    public int CohesionMinSampleCount { get; set; } = 6;
    public float CohesionMinAlertSeverity { get; set; } = 35f;
    public float CohesionLowThreshold { get; set; } = 45f;
    public float CohesionMediumThreshold { get; set; } = 65f;
    public float FollowDirectDistance { get; set; } = 28f;
    public float FollowNearDistance { get; set; } = 42f;
    public float FollowMountedDistance { get; set; } = 58f;
    public int DirectionDotThresholdPercent { get; set; } = 25;
    public float FakeRetreatLikelyRisk { get; set; } = 62f;
    public float HighGroundLikelyRisk { get; set; } = 58f;
    public float ThirdPartyLikelyRisk { get; set; } = 60f;
    public float SquadLikelyRisk { get; set; } = 58f;
    public float ChokeLikelyRisk { get; set; } = 58f;

    public void Normalize()
    {
        MinAlertConfidencePercent = Math.Clamp(MinAlertConfidencePercent, 0, 100);
        RetreatMinDistanceDelta = Math.Clamp(RetreatMinDistanceDelta, 0f, 20f);
        RetreatMinSpeed = Math.Clamp(RetreatMinSpeed, 0f, 5f);
        RetreatMovingAwayRatioPercent = Math.Clamp(RetreatMovingAwayRatioPercent, 0, 100);
        RetreatMinMovingSamples = Math.Clamp(RetreatMinMovingSamples, 1, 24);
        RetreatMinSeverity = Math.Clamp(RetreatMinSeverity, 0f, 100f);
        FakeRetreatMinSeverity = Math.Clamp(FakeRetreatMinSeverity, 0f, 100f);
        FakeRetreatMinThreatSignals = Math.Clamp(FakeRetreatMinThreatSignals, 0, 12);
        FakeRetreatSideClusterDistance = Math.Clamp(FakeRetreatSideClusterDistance, 40f, 260f);
        FakeRetreatSideClusterMinCount = Math.Clamp(FakeRetreatSideClusterMinCount, 1, 16);
        HighGroundMinHeightDelta = Math.Clamp(HighGroundMinHeightDelta, 0f, 12f);
        HighGroundMinPressure = Math.Clamp(HighGroundMinPressure, 1, 24);
        HighGroundMinSeverity = Math.Clamp(HighGroundMinSeverity, 0f, 100f);
        ThirdPartyMaxDistance = Math.Clamp(ThirdPartyMaxDistance, 60f, 320f);
        ThirdPartyMinAngle = Math.Clamp(ThirdPartyMinAngle, 45f, 180f);
        ThirdPartyMinSeverity = Math.Clamp(ThirdPartyMinSeverity, 0f, 100f);
        SquadSearchRadius = Math.Clamp(SquadSearchRadius, 12f, 60f);
        SquadMinScore = Math.Clamp(SquadMinScore, 0f, 100f);
        SquadMinDirectionSimilarityPercent = Math.Clamp(SquadMinDirectionSimilarityPercent, 0, 100);
        SquadMinFormationStability = Math.Clamp(SquadMinFormationStability, 0f, 100f);
        ChokeMinPressure = Math.Clamp(ChokeMinPressure, 1, 24);
        ChokeMinSeverity = Math.Clamp(ChokeMinSeverity, 0f, 100f);
        CohesionMinSampleCount = Math.Clamp(CohesionMinSampleCount, 1, 24);
        CohesionMinAlertSeverity = Math.Clamp(CohesionMinAlertSeverity, 0f, 100f);
        CohesionLowThreshold = Math.Clamp(CohesionLowThreshold, 0f, 100f);
        CohesionMediumThreshold = Math.Clamp(CohesionMediumThreshold, CohesionLowThreshold, 100f);
        FollowDirectDistance = Math.Clamp(FollowDirectDistance, 8f, 80f);
        FollowNearDistance = Math.Clamp(FollowNearDistance, FollowDirectDistance, 100f);
        FollowMountedDistance = Math.Clamp(FollowMountedDistance, FollowNearDistance, 140f);
        DirectionDotThresholdPercent = Math.Clamp(DirectionDotThresholdPercent, -100, 100);
        FakeRetreatLikelyRisk = Math.Clamp(FakeRetreatLikelyRisk, 0f, 100f);
        HighGroundLikelyRisk = Math.Clamp(HighGroundLikelyRisk, 0f, 100f);
        ThirdPartyLikelyRisk = Math.Clamp(ThirdPartyLikelyRisk, 0f, 100f);
        SquadLikelyRisk = Math.Clamp(SquadLikelyRisk, 0f, 100f);
        ChokeLikelyRisk = Math.Clamp(ChokeLikelyRisk, 0f, 100f);
    }
}

[Serializable]
public class BattlefieldReplayConfiguration
{
    public const int DefaultRecordIntervalSeconds = 10;
    public const int LowImpactRecordIntervalSeconds = 30;

    public bool Enabled { get; set; }
    public int RecordIntervalSeconds { get; set; } = LowImpactRecordIntervalSeconds;
    public int MaxSessionFiles { get; set; } = 30;
    public int MaxSessionAgeDays { get; set; } = 10;
    public bool IncludePlayerDetails { get; set; }
    public bool ShowDebugInfo { get; set; }
    public string DirectoryName { get; set; } = "ReplayLogs";

    public void Normalize()
    {
        RecordIntervalSeconds = Math.Clamp(RecordIntervalSeconds, 1, 30);
        MaxSessionFiles = Math.Clamp(MaxSessionFiles, 1, 300);
        MaxSessionAgeDays = Math.Clamp(MaxSessionAgeDays, 1, 365);
        DirectoryName = string.IsNullOrWhiteSpace(DirectoryName) ? "ReplayLogs" : DirectoryName.Trim();

        var invalidChars = Path.GetInvalidFileNameChars();
        DirectoryName = new string(DirectoryName.Where(ch => !invalidChars.Contains(ch)).ToArray());
        if (string.IsNullOrWhiteSpace(DirectoryName))
            DirectoryName = "ReplayLogs";
    }
}

[Serializable]
public class ScoreReaderConfiguration
{
    public bool ShowRawSourceDebug { get; set; }

    public void Normalize()
    {
    }
}

[Serializable]
public class BattleHighConfiguration
{
    public string CandidateStatusIds { get; set; } = "1263,1264,1265,1266,1267";
    public bool ShowDebugInfo { get; set; }
    public bool ShowAllVisibleStatusesInDebug { get; set; }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(CandidateStatusIds))
            CandidateStatusIds = "1263,1264,1265,1266,1267";
        else
            CandidateStatusIds = CandidateStatusIds.Trim();
    }
}

[Serializable]
public class TacticalStatusConfiguration
{
    public string GuardingStatusIds { get; set; } = string.Empty;
    public string CrowdControlledStatusIds { get; set; } = string.Empty;
    public string ControlImmuneStatusIds { get; set; } = string.Empty;
    public string InvulnerableStatusIds { get; set; } = string.Empty;
    public string SnowBlessingStatusIds { get; set; } = string.Empty;
    public bool ShowDebugInfo { get; set; }
    public bool ShowAllVisibleStatusesInDebug { get; set; }

    public void Normalize()
    {
        GuardingStatusIds = GuardingStatusIds?.Trim() ?? string.Empty;
        CrowdControlledStatusIds = CrowdControlledStatusIds?.Trim() ?? string.Empty;
        ControlImmuneStatusIds = ControlImmuneStatusIds?.Trim() ?? string.Empty;
        InvulnerableStatusIds = InvulnerableStatusIds?.Trim() ?? string.Empty;
        SnowBlessingStatusIds = SnowBlessingStatusIds?.Trim() ?? string.Empty;
    }
}

[Serializable]
public class RadarConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool ScreenRadar { get; set; }
    public bool MapRadar { get; set; } = true;
    public bool FieldMarkers { get; set; }
    public bool TargetMarkers { get; set; }
    public bool AutoMarkFocusTarget { get; set; }
    public bool OutsideFrontline { get; set; }
    public bool HideFriendlyCharacters { get; set; } = true;
    public bool OnlyDisplayDot { get; set; }
    public bool ShowNames { get; set; }
    public bool ShowJobIcons { get; set; }
    public bool ShowCastBars { get; set; }
    public bool ShowHpPercentOnMap { get; set; } = true;
    public bool ShowCountdownOnMap { get; set; } = true;
    public bool ShowControlPointScoreOnMap { get; set; } = true;
    public bool ShowMapLoadingRange { get; set; }
    public float ScreenDotRadius { get; set; } = 3f;
    public float MapDotRadius { get; set; } = 3.03f;
    public float JobIconScale { get; set; } = 0.936f;
    public int JobIconStyle { get; set; } = 2;

    public void ImportDailyRoutinesDefaultsIfPresent()
    {
        var dailyRoutinesConfig = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XIVLauncherCN",
            "pluginConfigs",
            "DailyRoutines",
            "FrontlinePlayerRadar.json");

        if (!File.Exists(dailyRoutinesConfig))
            return;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(dailyRoutinesConfig));
            var root = document.RootElement;

            if (root.TryGetProperty("DotRadius", out var dotRadius) && dotRadius.TryGetSingle(out var radius))
            {
                ScreenDotRadius = Math.Clamp(radius, 2f, 24f);
                MapDotRadius = Math.Clamp(radius, 1f, 12f);
            }

            if (root.TryGetProperty("ValidOutsideFrontline", out var validOutsideFrontline)
                && validOutsideFrontline.ValueKind is JsonValueKind.True or JsonValueKind.False)
                OutsideFrontline = validOutsideFrontline.GetBoolean();

            if (root.TryGetProperty("OnlyDisplayDot", out var onlyDisplayDot)
                && onlyDisplayDot.ValueKind is JsonValueKind.True or JsonValueKind.False)
                OnlyDisplayDot = onlyDisplayDot.GetBoolean();

            if (root.TryGetProperty("HideFriendlyCharacters", out var hideFriendlyCharacters)
                && hideFriendlyCharacters.ValueKind is JsonValueKind.True or JsonValueKind.False)
                HideFriendlyCharacters = hideFriendlyCharacters.GetBoolean();
        }
        catch
        {
            // DailyRoutines config is optional; keep local defaults if it cannot be read.
        }
    }
}

[Serializable]
public class FloatingButtonConfiguration
{
    public bool Enabled { get; set; } = true;
    public float X { get; set; } = 213f;
    public float Y { get; set; } = 57f;
}

[Serializable]
public class LimitBreakConfiguration
{
    public bool ShowLimitBreakUI { get; set; }
    public bool ShowDebugInfo { get; set; }
    public float OffsetX { get; set; } = -15.535f;
    public float OffsetY { get; set; } = -17.767f;
    public float WindowScale { get; set; } = 1.354f;
    public bool ShowStroke { get; set; } = true;
    public float TextColorR { get; set; } = 0f;
    public float TextColorG { get; set; } = 0.5f;
    public float TextColorB { get; set; } = 1f;
    public float StrokeColorR { get; set; } = 1f;
    public float StrokeColorG { get; set; } = 1f;
    public float StrokeColorB { get; set; } = 0f;

    public void Normalize()
    {
        OffsetX = Math.Clamp(OffsetX, -100f, 500f);
        OffsetY = Math.Clamp(OffsetY, -100f, 100f);
        WindowScale = Math.Clamp(WindowScale, 0.5f, 3f);
        TextColorR = Math.Clamp(TextColorR, 0f, 1f);
        TextColorG = Math.Clamp(TextColorG, 0f, 1f);
        TextColorB = Math.Clamp(TextColorB, 0f, 1f);
        StrokeColorR = Math.Clamp(StrokeColorR, 0f, 1f);
        StrokeColorG = Math.Clamp(StrokeColorG, 0f, 1f);
        StrokeColorB = Math.Clamp(StrokeColorB, 0f, 1f);
    }
}

[Serializable]
public class BattlefieldAnnouncementConfiguration
{
    public bool ShowDebugInfo { get; set; }
}

[Serializable]
public class SightDistanceConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool IgnoreCollision { get; set; } = true;
    public float MaxDistance { get; set; } = 80f;
    public float MinDistance { get; set; } = 0f;
    public float MaxRotation { get; set; } = 1.569f;
    public float MinRotation { get; set; } = -1.569f;
    public float MaxFoV { get; set; } = 0.78f;
    public float MinFoV { get; set; } = 0.69f;
    public float FoV { get; set; } = 0.78f;

    public void Normalize()
    {
        MaxDistance = Math.Clamp(MaxDistance, 1f, 80f);
        MinDistance = Math.Clamp(MinDistance, 0f, MaxDistance);
        MaxRotation = Math.Clamp(MaxRotation, MinRotation, 1.569f);
        MinRotation = Math.Clamp(MinRotation, -1.569f, MaxRotation);
        MaxFoV = Math.Clamp(MaxFoV, MinFoV, 3f);
        MinFoV = Math.Clamp(MinFoV, 0.01f, MaxFoV);
        FoV = Math.Clamp(FoV, MinFoV, MaxFoV);
    }

    public void ResetToRecommendedDefaults()
    {
        MaxDistance = 80f;
        MinDistance = 0f;
        MaxRotation = 1.569f;
        MinRotation = -1.569f;
        MaxFoV = 0.78f;
        MinFoV = 0.69f;
        FoV = 0.78f;
        IgnoreCollision = true;
    }
}
