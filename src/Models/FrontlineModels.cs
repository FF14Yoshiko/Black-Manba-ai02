using System;
using System.Numerics;

namespace ai02;

public enum NodeOwnership
{
    Neutral = 0,
    Maelstrom = 1,
    TwinAdder = 2,
    ImmortalFlames = 3
}

public enum NodeRank
{
    Unknown,
    B,
    A,
    S
}

public enum FrontlineMapType
{
    Unknown,
    BorderlandRuinsSecure,
    WolvesDen,
    Garlemald,
    SealRock,
    FieldsOfHonor,
    OnsalHakair,
    Vochester
}

public struct AllianceData
{
    public uint AllianceId;
    public int Score;
    public int TargetScore;
    public string Name;
    public bool IsPlayerAlliance;
    public bool IsLeading;
}

public struct NodeData
{
    public int NodeId;
    public string Name;
    public NodeOwnership Ownership;
    public NodeRank Rank;
    public float CaptureProgress;
    public bool IsContested;
}

public struct PlayerData
{
    public string? JobName;
    public float HpPercentage;
    public bool IsDead;
    public float RespawnTimer;
    public uint KillCount;
}

public struct FrontlineSnapshot
{
    public bool IsInFrontline;
    public bool HasScoreData;
    public bool HasMatchTime;
    public FrontlineScoreReaderState ScoreReaderState;
    public int MatchTimeRemaining;
    public AllianceData[] Alliances;
    public NodeData[] Nodes;
    public PlayerData LocalPlayer;
    public string[] PartyPositions;
    public string[] LargeGroupPositions;
    public string DebugInfo;
}

public enum BattlefieldPlayerRelation
{
    Unknown,
    LocalPlayer,
    Friendly,
    Enemy
}

public enum BattlefieldMapEventKind
{
    Unknown,
    Countdown,
    Health,
    ControlPoint
}

public enum BattlefieldTacticalSide
{
    Unknown,
    Friendly,
    Enemy
}

public enum BattlefieldObjectiveKind
{
    Unknown,
    TimedSpawn,
    HealthObjective,
    ControlPoint,
    FieldMarker,
    TargetMarker
}

public enum BattlefieldAnnouncementKind
{
    Unknown,
    WeatherWarning,
    WeatherStarted,
    WeatherEnded,
    ObjectiveWarning,
    ObjectiveAvailable,
    ObjectiveControlled,
    ObjectiveReleased,
    ObjectiveOther
}

public enum BattlefieldChatEventKind
{
    Unknown,
    Kill,
    BattleHigh,
    ObjectiveCaptured,
    ObjectiveLost,
    ObjectiveContested,
    ObjectiveOther
}

public enum BattlefieldWeatherKind
{
    Unknown,
    Snow,
    Aurora
}

public enum BattlefieldMapObjectiveCategory
{
    Unknown,
    Base,
    Tomelith,
    Ice,
    Ovoo,
    StrategicPoint,
    Monster
}

public enum BattlefieldMapObjectiveState
{
    Unknown,
    Inactive,
    Warning,
    Active,
    Controlled,
    Contested,
    Destroyed
}

public enum BattlefieldTacticalStatusKind
{
    Guarding,
    ControlVulnerable,
    CrowdControlled,
    ControlImmune,
    Invulnerable,
    ExecuteVulnerable,
    SnowBlessing
}

public readonly record struct BattlefieldTacticalStatusSnapshot(
    uint StatusId,
    BattlefieldTacticalStatusKind Kind,
    string Label,
    string Name,
    int Param,
    float RemainingSeconds,
    string SourceText);

public readonly record struct BattlefieldPlayerSnapshot(
    ulong GameObjectId,
    ulong ContentId,
    string Name,
    Vector3 Position,
    float RotationRadians,
    float DistanceToLocal,
    uint ClassJobId,
    byte Battalion,
    bool IsDead,
    bool IsMounted,
    bool IsInCombat,
    bool IsPartyMember,
    bool IsAllianceMember,
    bool IsFriend,
    bool IsCasting,
    float CurrentCastTime,
    float TotalCastTime,
    ulong TargetObjectId,
    ulong CastTargetObjectId,
    uint CurrentHp,
    uint MaxHp,
    float HpPercent,
    int BattleHighLevel,
    bool IsBattleFever,
    uint BattleHighStatusId,
    float BattleHighRemainingSeconds,
    BattlefieldTacticalStatusSnapshot[] TacticalStatuses,
    bool IsGuarding,
    bool IsCrowdControlled,
    bool IsControlImmune,
    bool IsControlVulnerable,
    bool IsInvulnerable,
    bool IsExecutable,
    bool HasSnowBlessing,
    BattlefieldPlayerRelation Relation);

public readonly record struct BattlefieldMapEventSnapshot(
    uint IconId,
    BattlefieldMapEventKind Kind,
    Vector3 Position,
    string Tooltip,
    int? CountdownSeconds,
    int? HpPercent,
    int? ScoreValue);

public readonly record struct BattlefieldFieldMarkerSnapshot(
    uint Index,
    Vector3 Position);

public readonly record struct BattlefieldMapVisionPointSnapshot(
    uint IconId,
    BattlefieldPlayerRelation Relation,
    byte? Battalion,
    bool IsDead,
    Vector2 MapScreenPosition,
    Vector3 EstimatedWorldPosition);

public readonly record struct BattlefieldMapVisionClusterSnapshot(
    BattlefieldPlayerRelation Relation,
    byte? Battalion,
    Vector3 Center,
    int PointCount,
    float DistanceToLocal);

public readonly record struct BattlefieldTargetMarkerSnapshot(
    uint Index,
    ulong TargetGameObjectId,
    string TargetName);

public readonly record struct BattlefieldObjectiveSnapshot(
    string Id,
    BattlefieldObjectiveKind Kind,
    Vector3 Position,
    uint? IconId,
    string Name,
    int? CountdownSeconds,
    int? HpPercent,
    int? ScoreValue);

public readonly record struct BattlefieldObjectiveContributionSnapshot(
    ulong PlayerGameObjectId,
    string PlayerName,
    BattlefieldPlayerRelation Relation,
    byte? Battalion,
    string JobName,
    bool IsTargetingObjective,
    bool IsCastingAtObjective,
    float DistanceToObjective,
    int? EnmityPercent,
    int? EnmityLevel,
    float EstimatedContributionWeight,
    string EvidenceText);

public readonly record struct BattlefieldMapObjectiveSnapshot(
    string Id,
    FrontlineMapType MapType,
    BattlefieldMapObjectiveCategory Category,
    BattlefieldMapObjectiveState State,
    Vector3 Position,
    uint? IconId,
    ulong? GameObjectId,
    string Name,
    string LocationId,
    string RankName,
    NodeOwnership? Ownership,
    string OwnershipText,
    int? RemainingSeconds,
    string RemainingSource,
    int? HpPercent,
    uint? CurrentHp,
    uint? MaxHp,
    int? ScoreValue,
    int AttackerCount,
    int FriendlyAttackerCount,
    int EnemyAttackerCount,
    int CasterCount,
    bool IsBeingFocused,
    bool IsBeingAttacked,
    uint? RecentHpLoss,
    float RecentHpLossPerSecond,
    long? LastDamageAgeMs,
    BattlefieldObjectiveContributionSnapshot[] Contributors,
    string ContributionSummaryText,
    string EnmitySourceText,
    string[] AggressorNames,
    string SourceText,
    float Confidence);

public readonly record struct BattlefieldAnnouncementSnapshot(
    long ObservedAtTicks,
    long AgeMs,
    string Source,
    string Text,
    BattlefieldAnnouncementKind Kind,
    BattlefieldWeatherKind Weather,
    string WeatherName,
    string LocationId,
    string RankName,
    NodeOwnership? Ownership,
    int? CountdownSeconds,
    int? RemainingSeconds,
    string SummaryText);

public readonly record struct BattlefieldChatEventSnapshot(
    long ObservedAtTicks,
    long AgeMs,
    string Source,
    string Text,
    BattlefieldChatEventKind Kind,
    string ActorName,
    string TargetName,
    BattlefieldTacticalSide ActorSide,
    BattlefieldTacticalSide TargetSide,
    NodeOwnership? Ownership,
    string LocationId,
    string ObjectiveName,
    int? BattleHighLevel,
    int? BattleHighDelta,
    string SummaryText);

public readonly record struct BattlefieldPlayerClusterSnapshot(
    BattlefieldPlayerRelation Relation,
    byte? Battalion,
    Vector3 Center,
    int PlayerCount,
    int DeadCount,
    int CastingCount,
    float DistanceToLocal);

public readonly record struct BattlefieldCompositionSnapshot(
    string Name,
    string Category,
    int Count);

public sealed class BattlefieldTeamSummarySnapshot
{
    public BattlefieldTacticalSide Side { get; init; }
    public string Name { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public int AliveCount { get; init; }
    public int DeadCount { get; init; }
    public int MountedCount { get; init; }
    public int InCombatCount { get; init; }
    public int LowHpCount { get; init; }
    public int CastingCount { get; init; }
    public int BattleHighCount { get; init; }
    public int BattleFeverCount { get; init; }
    public int MaxBattleHighLevel { get; init; }
    public int BattleHighTotalLevel { get; init; }
    public int GuardingCount { get; init; }
    public int CrowdControlledCount { get; init; }
    public int ControlVulnerableCount { get; init; }
    public int InvulnerableCount { get; init; }
    public int ExecutableCount { get; init; }
    public int SnowBlessingCount { get; init; }
    public int NearCount { get; init; }
    public int MidCount { get; init; }
    public int FarCount { get; init; }
    public BattlefieldCompositionSnapshot[] RoleComposition { get; init; } = Array.Empty<BattlefieldCompositionSnapshot>();
    public BattlefieldCompositionSnapshot[] JobComposition { get; init; } = Array.Empty<BattlefieldCompositionSnapshot>();
    public BattlefieldPlayerClusterSnapshot? MainCluster { get; init; }
}

public sealed class BattlefieldAllianceSituationSnapshot
{
    public byte? Battalion { get; init; }
    public string Name { get; init; } = string.Empty;
    public BattlefieldPlayerRelation Relation { get; init; } = BattlefieldPlayerRelation.Unknown;
    public bool IsLocalAlliance { get; init; }
    public BattlefieldPlayerSnapshot[] VisiblePlayers { get; init; } = Array.Empty<BattlefieldPlayerSnapshot>();
    public BattlefieldMapVisionPointSnapshot[] MapVisionPoints { get; init; } = Array.Empty<BattlefieldMapVisionPointSnapshot>();
    public int VisiblePlayerCount { get; init; }
    public int MapVisionPointCount { get; init; }
    public int AliveCount { get; init; }
    public int DeadCount { get; init; }
    public int MountedCount { get; init; }
    public int InCombatCount { get; init; }
    public int LowHpCount { get; init; }
    public int CastingCount { get; init; }
    public int BattleHighCount { get; init; }
    public int BattleFeverCount { get; init; }
    public int MaxBattleHighLevel { get; init; }
    public int BattleHighTotalLevel { get; init; }
    public int GuardingCount { get; init; }
    public int CrowdControlledCount { get; init; }
    public int ControlVulnerableCount { get; init; }
    public int InvulnerableCount { get; init; }
    public int ExecutableCount { get; init; }
    public int SnowBlessingCount { get; init; }
    public BattlefieldCompositionSnapshot[] RoleComposition { get; init; } = Array.Empty<BattlefieldCompositionSnapshot>();
    public BattlefieldCompositionSnapshot[] JobComposition { get; init; } = Array.Empty<BattlefieldCompositionSnapshot>();
    public BattlefieldPlayerClusterSnapshot? MainPlayerCluster { get; init; }
    public BattlefieldMapVisionClusterSnapshot? MainMapVisionCluster { get; init; }
}

public readonly record struct BattlefieldFocusTargetSnapshot(
    ulong TargetGameObjectId,
    string TargetName,
    BattlefieldPlayerRelation TargetRelation,
    string TargetJobName,
    uint CurrentHp,
    uint MaxHp,
    float HpPercent,
    BattlefieldPlayerRelation SourceRelation,
    int AttackerCount,
    int CasterCount,
    string[] SourceNames,
    Vector3 Position,
    float ThreatScore);

public readonly record struct BattlefieldPlayerTrackSnapshot(
    ulong GameObjectId,
    string Name,
    BattlefieldPlayerRelation Relation,
    uint ClassJobId,
    Vector3 LastPosition,
    Vector3 PreviousPosition,
    Vector3 MovementDelta,
    float SpeedPerSecond,
    float MovementDirectionRadians,
    float FacingRadians,
    bool IsDead,
    long LastSeenAgeMs,
    long MovementAgeMs,
    long? DeathAgeMs);

public readonly record struct BattlefieldGroupTrackSnapshot(
    long Ticks,
    Vector3 Center,
    int PlayerCount,
    string SourceText);

public sealed class BattlefieldRespawnRhythmSnapshot
{
    public int FriendlyDeadNow { get; init; }
    public int EnemyDeadNow { get; init; }
    public int FriendlyRecentlyDied { get; init; }
    public int EnemyRecentlyDied { get; init; }
    public int FriendlyRecentlyRevived { get; init; }
    public int EnemyRecentlyRevived { get; init; }
    public int FriendlyLikelyReturningSoon { get; init; }
    public int EnemyLikelyReturningSoon { get; init; }
    public int FriendlyDeathWaveSize { get; init; }
    public int EnemyDeathWaveSize { get; init; }
    public int FriendlyReviveWaveSize { get; init; }
    public int EnemyReviveWaveSize { get; init; }
    public long? FriendlyDeathWaveAgeMs { get; init; }
    public long? EnemyDeathWaveAgeMs { get; init; }
    public long? FriendlyReviveWaveAgeMs { get; init; }
    public long? EnemyReviveWaveAgeMs { get; init; }
    public float Confidence { get; init; }
    public string SummaryText { get; init; } = "复活节奏正在累积样本";
}

public sealed class BattlefieldGroupMovementSnapshot
{
    public bool HasMainGroup { get; init; }
    public string SourceText { get; init; } = "附近实体";
    public byte? Battalion { get; init; }
    public string AllianceName { get; init; } = "敌方";
    public Vector3 CurrentCenter { get; init; }
    public Vector3 PreviousCenter { get; init; }
    public Vector3 Delta { get; init; }
    public Vector3 PredictedNextCenter { get; init; }
    public int PlayerCount { get; init; }
    public float SpeedPerSecond { get; init; }
    public float Confidence { get; init; }
    public long ObservationAgeMs { get; init; }
    public bool IsMemoryEstimate { get; init; }
    public bool IsRotationLikely { get; init; }
    public bool IsTeleportLikely { get; init; }
    public string TransitionText { get; init; } = string.Empty;
    public string DirectionText { get; init; } = "未知";
    public bool IsEnemySplit { get; init; }
    public int EnemyClusterCount { get; init; }
    public string SummaryText { get; init; } = "敌方主团方向正在累积样本";
}

public sealed class BattlefieldEnemyClusterSnapshot
{
    public int ClusterId { get; init; }
    public byte? Battalion { get; init; }
    public string AllianceName { get; init; } = "敌方";
    public string SourceText { get; init; } = "未知";
    public Vector3 Center { get; init; }
    public int Count { get; init; }
    public float Radius { get; init; }
    public float DistanceToLocal { get; init; }
    public float SeparationFromMain { get; init; }
    public bool IsMainCluster { get; init; }
}

public enum BattlefieldLimitBreakThreatLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum BattlefieldKeySkillKind
{
    Unknown,
    Engage,
    CrowdControl,
    GuardBreak,
    DefensePierce,
    Burst,
    Execute,
    Defensive,
    Purify,
    Invulnerability,
    AreaPressure,
    Support
}

public readonly record struct BattlefieldKeySkillUseSnapshot(
    long ObservedAtTicks,
    long AgeMs,
    ulong GameObjectId,
    string Name,
    BattlefieldPlayerRelation Relation,
    byte? Battalion,
    string AllianceName,
    uint ClassJobId,
    string JobName,
    string SkillName,
    BattlefieldKeySkillKind Kind,
    string TargetName,
    string SourceText,
    string EvidenceText);

public sealed class BattlefieldKeySkillLogEventSituationSnapshot
{
    public bool IsAvailable { get; init; }
    public BattlefieldKeySkillUseSnapshot[] RecentEvents { get; init; } = Array.Empty<BattlefieldKeySkillUseSnapshot>();
    public int RecentEventCount { get; init; }
    public string SourceText { get; init; } = "战斗日志技能事件未捕获";
    public string SummaryText { get; init; } = "关键技能战斗日志尚未捕获";
}

public readonly record struct BattlefieldKeySkillThreatSnapshot(
    ulong GameObjectId,
    string Name,
    BattlefieldPlayerRelation Relation,
    byte? Battalion,
    string AllianceName,
    uint ClassJobId,
    string JobName,
    string SkillName,
    BattlefieldKeySkillKind Kind,
    int CooldownSeconds,
    float EstimatedCooldownRemainingSeconds,
    bool IsEstimatedReady,
    bool WasRecentlyUsed,
    long LastObservedUseAgeMs,
    bool IsCasting,
    bool IsTargetingOpposingSide,
    bool IsControlChainCandidate,
    bool IsDefenseBreakWindow,
    bool IsExecuteWindow,
    bool TargetIsGuardingOrInvulnerable,
    int OpposingVulnerableCount,
    BattlefieldLimitBreakThreatLevel ThreatLevel,
    float ThreatScore,
    string SourceText,
    string EvidenceText);

public sealed class BattlefieldKeySkillThreatSituationSnapshot
{
    public BattlefieldKeySkillThreatSnapshot[] FriendlyThreats { get; init; } = Array.Empty<BattlefieldKeySkillThreatSnapshot>();
    public BattlefieldKeySkillThreatSnapshot[] EnemyThreats { get; init; } = Array.Empty<BattlefieldKeySkillThreatSnapshot>();
    public BattlefieldKeySkillThreatSnapshot[] TopEnemyThreats { get; init; } = Array.Empty<BattlefieldKeySkillThreatSnapshot>();
    public BattlefieldKeySkillThreatSnapshot[] TopFriendlyThreats { get; init; } = Array.Empty<BattlefieldKeySkillThreatSnapshot>();
    public BattlefieldKeySkillUseSnapshot[] RecentUses { get; init; } = Array.Empty<BattlefieldKeySkillUseSnapshot>();
    public int FriendlyLikelyReadyCount { get; init; }
    public int EnemyLikelyReadyCount { get; init; }
    public int FriendlyHighThreatCount { get; init; }
    public int EnemyHighThreatCount { get; init; }
    public int EnemyControlChainCount { get; init; }
    public int EnemyDefenseBreakWindowCount { get; init; }
    public int EnemyExecuteWindowCount { get; init; }
    public string SourceText { get; init; } = "可见状态/职业档案/使用记录估算";
    public string SummaryText { get; init; } = "关键技能威胁尚未形成";
}

public enum BattlefieldAdvancedTacticalInsightKind
{
    Unknown,
    EnemyRetreat,
    EnemyFakeRetreatAmbush,
    FriendlyCohesion,
    HighGroundDropPrep,
    ThirdPartyPincer,
    CoordinatedSquad,
    ChokeBlocked
}

public readonly record struct BattlefieldAdvancedTacticalInsightSnapshot(
    BattlefieldAdvancedTacticalInsightKind Kind,
    string Label,
    float Severity,
    float Confidence,
    Vector3 Position,
    int InvolvedCount,
    byte? Battalion,
    string AllianceName,
    string Recommendation,
    string EvidenceText);

public sealed class BattlefieldAdvancedTacticalSituationSnapshot
{
    public bool IsAvailable { get; init; }
    public BattlefieldAdvancedTacticalInsightSnapshot[] Insights { get; init; } = Array.Empty<BattlefieldAdvancedTacticalInsightSnapshot>();
    public BattlefieldAdvancedTacticalInsightSnapshot[] SuppressedInsights { get; init; } = Array.Empty<BattlefieldAdvancedTacticalInsightSnapshot>();
    public BattlefieldAdvancedTacticalInsightSnapshot? TopInsight { get; init; }
    public int RawInsightCount { get; init; }
    public int SuppressedInsightCount { get; init; }
    public float FriendlyFollowRate { get; init; }
    public float FriendlyDirectionConsistency { get; init; }
    public float FriendlyCohesionScore { get; init; }
    public int FriendlyFollowerCount { get; init; }
    public int FriendlySampleCount { get; init; }
    public bool IsEnemyRetreating { get; init; }
    public bool IsEnemyFakeRetreatAmbushLikely { get; init; }
    public bool IsHighGroundDropPrepLikely { get; init; }
    public bool IsThirdPartyPincerLikely { get; init; }
    public bool IsCoordinatedSquadLikely { get; init; }
    public bool IsChokeBlockedLikely { get; init; }
    public float AmbushRisk { get; init; }
    public float CohesionRisk { get; init; }
    public float HighGroundDropRisk { get; init; }
    public float ThirdPartyPincerRisk { get; init; }
    public float CoordinatedSquadRisk { get; init; }
    public float ChokeBlockRisk { get; init; }
    public string SourceText { get; init; } = "位置/面向/移动历史/地图标注";
    public string CalibrationText { get; init; } = string.Empty;
    public string SummaryText { get; init; } = "高级战术洞察尚未形成";
}

public readonly record struct BattlefieldLimitBreakThreatSnapshot(
    ulong GameObjectId,
    string Name,
    BattlefieldPlayerRelation Relation,
    byte? Battalion,
    string AllianceName,
    uint ClassJobId,
    string JobName,
    string Role,
    int BattleHighLevel,
    bool IsBattleFever,
    float DistanceToLocal,
    int BaseChargeSeconds,
    int AdjustedChargeSeconds,
    int RankSpeedModifierPercent,
    float EstimatedPercent,
    float EstimatedSecondsToReady,
    bool IsLikelyReady,
    BattlefieldLimitBreakThreatLevel ThreatLevel,
    float ThreatScore,
    bool IsEngagedRecently,
    bool IsCasting,
    bool IsTargetingOpposingSide,
    string ThreatType,
    string EvidenceText);

public sealed class BattlefieldLimitBreakThreatSituationSnapshot
{
    public BattlefieldLimitBreakThreatSnapshot[] FriendlyThreats { get; init; } = Array.Empty<BattlefieldLimitBreakThreatSnapshot>();
    public BattlefieldLimitBreakThreatSnapshot[] EnemyThreats { get; init; } = Array.Empty<BattlefieldLimitBreakThreatSnapshot>();
    public BattlefieldLimitBreakThreatSnapshot[] TopEnemyThreats { get; init; } = Array.Empty<BattlefieldLimitBreakThreatSnapshot>();
    public BattlefieldLimitBreakThreatSnapshot[] TopFriendlyThreats { get; init; } = Array.Empty<BattlefieldLimitBreakThreatSnapshot>();
    public int FriendlyLikelyReadyCount { get; init; }
    public int EnemyLikelyReadyCount { get; init; }
    public int FriendlyHighThreatCount { get; init; }
    public int EnemyHighThreatCount { get; init; }
    public string SourceText { get; init; } = "职业充能/排名/战意/可见行为估算";
    public string SummaryText { get; init; } = "极限技威胁估算尚未形成";
}

public sealed class BattlefieldTeamSituationSnapshot
{
    public BattlefieldTeamSummarySnapshot Friendly { get; init; } = new() { Side = BattlefieldTacticalSide.Friendly, Name = "我方" };
    public BattlefieldTeamSummarySnapshot Enemy { get; init; } = new() { Side = BattlefieldTacticalSide.Enemy, Name = "敌方" };
    public BattlefieldTeamSummarySnapshot Unknown { get; init; } = new() { Side = BattlefieldTacticalSide.Unknown, Name = "未知" };
    public BattlefieldAllianceSituationSnapshot[] Alliances { get; init; } = Array.Empty<BattlefieldAllianceSituationSnapshot>();
    public BattlefieldAllianceSituationSnapshot? FriendlyAlliance { get; init; }
    public BattlefieldAllianceSituationSnapshot? EnemyAlliance1 { get; init; }
    public BattlefieldAllianceSituationSnapshot? EnemyAlliance2 { get; init; }
    public BattlefieldPlayerSnapshot[] FriendlyPlayers { get; init; } = Array.Empty<BattlefieldPlayerSnapshot>();
    public BattlefieldPlayerSnapshot[] EnemyAlliance1Players { get; init; } = Array.Empty<BattlefieldPlayerSnapshot>();
    public BattlefieldPlayerSnapshot[] EnemyAlliance2Players { get; init; } = Array.Empty<BattlefieldPlayerSnapshot>();
    public BattlefieldEnemyClusterSnapshot[] EnemyClusters { get; init; } = Array.Empty<BattlefieldEnemyClusterSnapshot>();
    public bool IsEnemySplit { get; init; }
    public string EnemySplitSummaryText { get; init; } = "敌方分兵状态样本不足";
    public BattlefieldFocusTargetSnapshot[] EnemyFocusTargets { get; init; } = Array.Empty<BattlefieldFocusTargetSnapshot>();
    public BattlefieldFocusTargetSnapshot[] FriendlyFocusTargets { get; init; } = Array.Empty<BattlefieldFocusTargetSnapshot>();
    public BattlefieldRespawnRhythmSnapshot RespawnRhythm { get; init; } = new();
    public BattlefieldGroupMovementSnapshot EnemyMainGroupMovement { get; init; } = new();
    public BattlefieldLimitBreakThreatSituationSnapshot LimitBreakThreats { get; init; } = new();
    public BattlefieldKeySkillThreatSituationSnapshot KeySkillThreats { get; init; } = new();
    public BattlefieldAdvancedTacticalSituationSnapshot AdvancedTactics { get; set; } = new();
    public string SummaryText { get; init; } = "团队态势尚未形成";
}

public readonly record struct FrontlineKnowledgeRuleSnapshot(
    string Id,
    string Title,
    string Detail,
    string[] Tags);

public readonly record struct FrontlineDecisionHintSnapshot(
    string Id,
    string Trigger,
    string Recommendation,
    string Reason,
    string[] Tags);

public readonly record struct FrontlineCommanderMacroIntentSnapshot(
    string Id,
    string Name,
    string Intent,
    string[] Keywords,
    string TacticalMeaning,
    string SystemUse,
    string[] Tags);

public readonly record struct FrontlineBattleHighRewardSnapshot(
    string TargetBattleHighLevel,
    int KillGain,
    int AssistGain);

public readonly record struct FrontlineBattleHighTierSnapshot(
    int MinBattleHigh,
    int MaxBattleHigh,
    string Level,
    int DamageAndHealingBonusPercent);

public readonly record struct FrontlineLimitBreakRankRuleSnapshot(
    string RankName,
    int ChargeSpeedModifierPercent,
    string Note);

public readonly record struct FrontlineJobAdjustmentSnapshot(
    uint ClassJobId,
    string JobName,
    int MaxHp,
    int OutgoingDamageModifierPercent,
    int IncomingDamageModifierPercent,
    int BaseLimitBreakChargeSeconds,
    int FrontlineLimitBreakChargeModifierSeconds,
    int FrontlineLimitBreakChargeSeconds);

public readonly record struct FrontlineCrowdControlAdjustmentSnapshot(
    string EffectName,
    int DurationModifierPercent);

public readonly record struct FrontlineBattlefieldProfileSnapshot(
    int AllianceCount,
    int PartiesPerAlliance,
    int PlayersPerAlliance,
    int MaxPlayers,
    string Description);

public readonly record struct FrontlineDefenseInteractionSkillSnapshot(
    string JobName,
    uint? ClassJobId,
    string SkillName,
    string InteractionType,
    string TargetScope,
    string Detail);

public readonly record struct FrontlineKeySkillRuleSnapshot(
    string Id,
    uint? ClassJobId,
    string JobName,
    string SkillName,
    BattlefieldKeySkillKind Kind,
    int CooldownSeconds,
    float BaseThreat,
    bool ControlChain,
    bool DefenseBreak,
    bool ExecuteWindow,
    bool AreaPressure,
    string TacticalNote,
    string SourceName,
    string SourceUrl,
    string[] Tags);

public readonly record struct FrontlineBaseCaptureScoreSnapshot(
    int CapturedBaseCount,
    int ScorePerTick,
    int TickSeconds);

public readonly record struct FrontlineObjectiveRankScoreSnapshot(
    string RankName,
    int TotalScore,
    int ScorePerTick,
    int TickSeconds,
    int TickCount,
    int ActiveDurationSeconds);

public readonly record struct FrontlineScoreSourceSnapshot(
    string SourceName,
    int? OwnScoreDelta,
    int? EnemyScoreDelta,
    string Detail,
    string[] Tags);

public readonly record struct FrontlineMapObjectiveRuleSnapshot(
    string Id,
    string Name,
    string ObjectiveType,
    int? TacticalScore,
    int? BattleHighGain,
    int? DurationSeconds,
    string Detail,
    string[] Tags);

public readonly record struct FrontlineDestructibleObjectiveRuleSnapshot(
    string Id,
    string Name,
    string SizeCategory,
    int TotalCount,
    int MaxActiveCount,
    int MaxHp,
    int ScoreValue,
    int WarningSeconds,
    int? FirstWarningElapsedSeconds,
    int? FirstActivationElapsedSeconds,
    int? RespawnSeconds,
    int? NextWarningAfterDestroyedSeconds,
    string ScoreAllocationRule,
    string Detail,
    string[] Tags);

public readonly record struct FrontlineTimedSpawnRuleSnapshot(
    string Id,
    string Name,
    int? FirstSpawnOffsetSeconds,
    int WarningSeconds,
    int DurationSeconds,
    string Detail,
    string[] Tags);

public readonly record struct FrontlineTeleportRuleSnapshot(
    string Name,
    int ActivationLeadSeconds,
    int InvulnerabilitySecondsAfterTeleport,
    string Detail);

public readonly record struct FrontlineMapPhaseRuleSnapshot(
    string Name,
    int StartElapsedSeconds,
    int? EndElapsedSeconds,
    int? MaxActiveObjectives,
    string MinimumObjectiveRank,
    string Detail,
    string[] Tags);

public readonly record struct FrontlineMapLocationRuleSnapshot(
    string Name,
    string[] LocationIds,
    int? StartElapsedSeconds,
    int? EndElapsedSeconds,
    string MinimumObjectiveRank,
    string Detail,
    string[] Tags);

public readonly record struct FrontlineWeatherRuleSnapshot(
    string Name,
    int StartElapsedSeconds,
    int DurationSeconds,
    string Detail,
    string[] Tags);

public sealed class FrontlineMapKnowledgeSnapshot
{
    public FrontlineMapType MapType { get; init; }
    public uint[] TerritoryTypeIds { get; init; } = Array.Empty<uint>();
    public string Name { get; init; } = string.Empty;
    public string RuleSetName { get; init; } = string.Empty;
    public int VictoryScore { get; init; }
    public string PrimaryObjective { get; init; } = string.Empty;
    public string RankingRule { get; init; } = string.Empty;
    public FrontlineKnowledgeRuleSnapshot[] Rules { get; init; } = Array.Empty<FrontlineKnowledgeRuleSnapshot>();
    public FrontlineBaseCaptureScoreSnapshot[] BaseCaptureScores { get; init; } = Array.Empty<FrontlineBaseCaptureScoreSnapshot>();
    public FrontlineObjectiveRankScoreSnapshot[] ObjectiveRankScores { get; init; } = Array.Empty<FrontlineObjectiveRankScoreSnapshot>();
    public FrontlineScoreSourceSnapshot[] ScoreSources { get; init; } = Array.Empty<FrontlineScoreSourceSnapshot>();
    public FrontlineTimedSpawnRuleSnapshot[] TimedSpawns { get; init; } = Array.Empty<FrontlineTimedSpawnRuleSnapshot>();
    public FrontlineMapObjectiveRuleSnapshot[] ObjectiveRules { get; init; } = Array.Empty<FrontlineMapObjectiveRuleSnapshot>();
    public FrontlineDestructibleObjectiveRuleSnapshot[] DestructibleObjectiveRules { get; init; } = Array.Empty<FrontlineDestructibleObjectiveRuleSnapshot>();
    public FrontlineTeleportRuleSnapshot[] TeleportRules { get; init; } = Array.Empty<FrontlineTeleportRuleSnapshot>();
    public FrontlineMapPhaseRuleSnapshot[] PhaseRules { get; init; } = Array.Empty<FrontlineMapPhaseRuleSnapshot>();
    public FrontlineMapLocationRuleSnapshot[] LocationRules { get; init; } = Array.Empty<FrontlineMapLocationRuleSnapshot>();
    public FrontlineWeatherRuleSnapshot[] WeatherRules { get; init; } = Array.Empty<FrontlineWeatherRuleSnapshot>();
    public FrontlineDecisionHintSnapshot[] DecisionHints { get; init; } = Array.Empty<FrontlineDecisionHintSnapshot>();
    public string SummaryText { get; init; } = "地图知识尚未加载";
}

public sealed class FrontlineKnowledgeSnapshot
{
    public FrontlineKnowledgeRuleSnapshot[] GlobalRules { get; init; } = Array.Empty<FrontlineKnowledgeRuleSnapshot>();
    public FrontlineDecisionHintSnapshot[] DecisionHints { get; init; } = Array.Empty<FrontlineDecisionHintSnapshot>();
    public FrontlineCommanderMacroIntentSnapshot[] CommanderMacroIntents { get; init; } = Array.Empty<FrontlineCommanderMacroIntentSnapshot>();
    public FrontlineBattlefieldProfileSnapshot BattlefieldProfile { get; init; }
    public FrontlineBattleHighRewardSnapshot[] BattleHighRewards { get; init; } = Array.Empty<FrontlineBattleHighRewardSnapshot>();
    public FrontlineBattleHighTierSnapshot[] BattleHighTiers { get; init; } = Array.Empty<FrontlineBattleHighTierSnapshot>();
    public FrontlineLimitBreakRankRuleSnapshot[] LimitBreakRankRules { get; init; } = Array.Empty<FrontlineLimitBreakRankRuleSnapshot>();
    public FrontlineJobAdjustmentSnapshot[] JobAdjustments { get; init; } = Array.Empty<FrontlineJobAdjustmentSnapshot>();
    public FrontlineCrowdControlAdjustmentSnapshot[] CrowdControlAdjustments { get; init; } = Array.Empty<FrontlineCrowdControlAdjustmentSnapshot>();
    public FrontlineDefenseInteractionSkillSnapshot[] DefenseInteractionSkills { get; init; } = Array.Empty<FrontlineDefenseInteractionSkillSnapshot>();
    public FrontlineKeySkillRuleSnapshot[] KeySkillRules { get; init; } = Array.Empty<FrontlineKeySkillRuleSnapshot>();
    public FrontlineMapKnowledgeSnapshot? CurrentMap { get; init; }
    public FrontlineMapKnowledgeSnapshot[] KnownMaps { get; init; } = Array.Empty<FrontlineMapKnowledgeSnapshot>();
    public string SummaryText { get; init; } = "前线知识库尚未加载";
}

public readonly record struct BattlefieldAllianceScoreSnapshot(
    uint AllianceId,
    byte? Battalion,
    string Name,
    BattlefieldPlayerRelation Relation,
    bool IsLocalAlliance,
    int Score,
    int VictoryScore,
    int RankIndex,
    string RankText,
    bool IsLeading,
    int TrendWindowSeconds,
    int ScoreDelta30s,
    float ScorePerSecond30s);

public sealed class BattlefieldScoreSituationSnapshot
{
    public bool HasScoreData { get; init; }
    public FrontlineMapType MapType { get; init; }
    public string MapName { get; init; } = string.Empty;
    public int VictoryScore { get; init; }
    public BattlefieldAllianceScoreSnapshot[] Alliances { get; init; } = Array.Empty<BattlefieldAllianceScoreSnapshot>();
    public BattlefieldAllianceScoreSnapshot[] RankedAlliances { get; init; } = Array.Empty<BattlefieldAllianceScoreSnapshot>();
    public BattlefieldAllianceScoreSnapshot? FriendlyAlliance { get; init; }
    public BattlefieldAllianceScoreSnapshot? EnemyAlliance1 { get; init; }
    public BattlefieldAllianceScoreSnapshot? EnemyAlliance2 { get; init; }
    public string SummaryText { get; init; } = "比分态势尚未形成";
}

public sealed class BattlefieldTimeSituationSnapshot
{
    public bool HasMatchTime { get; init; }
    public int MatchTimeRemainingSeconds { get; init; }
    public int MatchElapsedSeconds { get; init; }
    public string MatchPhaseName { get; init; } = "未知阶段";
    public string MatchPhaseDetail { get; init; } = string.Empty;
    public string MapRulePhaseName { get; init; } = string.Empty;
    public int? MapRuleMaxActiveObjectives { get; init; }
    public string MapRuleMinimumObjectiveRank { get; init; } = string.Empty;
    public int? NextResourceSeconds { get; init; }
    public string NextResourceName { get; init; } = string.Empty;
    public string NextResourceSource { get; init; } = "未读取";
    public string SummaryText { get; init; } = "时间态势尚未形成";
}

public sealed class BattlefieldLimitBreakSnapshot
{
    public bool IsAvailable { get; init; }
    public bool IsInPvPRegion { get; init; }
    public ushort BarUnits { get; init; }
    public ushort CurrentUnits { get; init; }
    public float Percent { get; init; }
    public int Level { get; init; }
    public float LastPercent { get; init; }
    public float PercentPerTick { get; init; }
    public float EstimatedSecondsRemaining { get; init; }
    public float EstimatedTicksRemaining { get; init; }
    public string SourceText { get; init; } = "LimitBreakController";
    public string SummaryText { get; init; } = "极限槽尚未读取";
}

public sealed class BattlefieldAnnouncementSituationSnapshot
{
    public bool IsAvailable { get; init; }
    public BattlefieldAnnouncementSnapshot[] RecentAnnouncements { get; init; } = Array.Empty<BattlefieldAnnouncementSnapshot>();
    public BattlefieldAnnouncementSnapshot? LatestAnnouncement { get; init; }
    public BattlefieldAnnouncementSnapshot? LatestWeatherAnnouncement { get; init; }
    public BattlefieldAnnouncementSnapshot? LatestObjectiveAnnouncement { get; init; }
    public BattlefieldWeatherKind CurrentWeather { get; init; } = BattlefieldWeatherKind.Unknown;
    public string CurrentWeatherName { get; init; } = string.Empty;
    public string WeatherStateText { get; init; } = "天气通告尚未读取";
    public int? WeatherRemainingSeconds { get; init; }
    public string SourceText { get; init; } = "战场通告窗口";
    public string SummaryText { get; init; } = "战场通告尚未读取";
}

public sealed class BattlefieldChatEventSituationSnapshot
{
    public bool IsAvailable { get; init; }
    public BattlefieldChatEventSnapshot[] RecentEvents { get; init; } = Array.Empty<BattlefieldChatEventSnapshot>();
    public BattlefieldChatEventSnapshot? LatestKillEvent { get; init; }
    public BattlefieldChatEventSnapshot? LatestBattleHighEvent { get; init; }
    public BattlefieldChatEventSnapshot? LatestObjectiveEvent { get; init; }
    public int FriendlyKillsRecent { get; init; }
    public int FriendlyDeathsRecent { get; init; }
    public int EnemyKillsRecent { get; init; }
    public int EnemyDeathsRecent { get; init; }
    public int BattleHighEventsRecent { get; init; }
    public int ObjectiveEventsRecent { get; init; }
    public string SourceText { get; init; } = "IChatGui";
    public string SummaryText { get; init; } = "聊天事件尚未读取";
}

public readonly record struct BattlefieldMapTacticalZoneSnapshot(
    string Id,
    MapAnnotationKind Kind,
    string Label,
    string RouteId,
    Vector3 Position,
    float Radius,
    float EstimatedWidth,
    float HeightDeltaToLocal,
    bool IsCliffOrHighPlatform,
    bool IsMandatoryChoke,
    int FriendlyNearby,
    int EnemyNearby,
    int EnemyMapVisionNearby,
    int HighBattleHighEnemies,
    int HighLimitBreakEnemies,
    float EngagementWeightModifierPercent,
    float StaticRisk,
    float DynamicRisk,
    float TotalRisk,
    string Recommendation,
    string EvidenceText);

public readonly record struct BattlefieldMapTacticalRouteSnapshot(
    string RouteId,
    string KindSummary,
    int PointCount,
    float Distance,
    int MountedEtaSeconds,
    int OnFootEtaSeconds,
    float StaticRisk,
    float DynamicRisk,
    float TotalRisk,
    bool CrossesDangerZone,
    bool CrossesMandatoryChoke,
    string Recommendation,
    string EvidenceText);

public readonly record struct BattlefieldMapHeatPointSnapshot(
    Vector3 Position,
    float Radius,
    float Intensity,
    string SourceText);

public readonly record struct BattlefieldMapGroupPathSnapshot(
    BattlefieldTacticalSide Side,
    string SourceText,
    int PointCount,
    float Distance,
    int MountedEtaSeconds,
    int OnFootEtaSeconds,
    Vector3 LatestPosition,
    Vector3[] Points,
    string SummaryText);

public sealed class BattlefieldMapTacticsSnapshot
{
    public bool IsAvailable { get; init; }
    public uint TerritoryType { get; init; }
    public uint MapId { get; init; }
    public string MapName { get; init; } = string.Empty;
    public int AnnotationCount { get; init; }
    public int BuiltInGraphPointCount { get; init; }
    public int ManualAnnotationCount { get; init; }
    public string TacticalGraphSourceText { get; init; } = string.Empty;
    public string TacticalGraphCoverageText { get; init; } = string.Empty;
    public int ZoneCount { get; init; }
    public int StaticDangerCount { get; init; }
    public int DynamicDangerCount { get; init; }
    public int MandatoryChokeCount { get; init; }
    public BattlefieldMapTacticalZoneSnapshot[] TopZones { get; init; } = Array.Empty<BattlefieldMapTacticalZoneSnapshot>();
    public BattlefieldMapTacticalRouteSnapshot[] Routes { get; init; } = Array.Empty<BattlefieldMapTacticalRouteSnapshot>();
    public BattlefieldMapHeatPointSnapshot[] HeatPoints { get; init; } = Array.Empty<BattlefieldMapHeatPointSnapshot>();
    public BattlefieldMapGroupPathSnapshot FriendlyObservedPath { get; init; }
    public BattlefieldMapGroupPathSnapshot EnemyObservedPath { get; init; }
    public string CurrentRecommendation { get; init; } = string.Empty;
    public string SummaryText { get; init; } = "地图战术层尚未形成";
}

public readonly record struct BattlefieldObjectivePrioritySnapshot(
    string ObjectiveId,
    string Name,
    BattlefieldMapObjectiveCategory Category,
    BattlefieldMapObjectiveState State,
    Vector3 Position,
    NodeOwnership? Ownership,
    string OwnershipText,
    int? ScoreValue,
    int? RemainingSeconds,
    float DistanceToLocal,
    int MountedEtaSeconds,
    float RewardScore,
    float TimingScore,
    float DistanceScore,
    float PressureScore,
    float TeamAdvantageScore,
    float TerrainScore,
    float RiskScore,
    float HomeSideScore,
    float EnemyDoorstepPenalty,
    float CrossfirePenalty,
    float RouteBlockPenalty,
    float LongTravelPenalty,
    bool ShouldHoldInstead,
    float PriorityScore,
    string RecommendedAction,
    string EvidenceText);

public sealed class BattlefieldRiskAssessmentSnapshot
{
    public float OverallRisk { get; init; }
    public float CombatRisk { get; init; }
    public float FrameEventRisk { get; init; }
    public float MapRisk { get; init; }
    public float ObjectiveRisk { get; init; }
    public float LimitBreakRisk { get; init; }
    public float SkillThreatRisk { get; init; }
    public float BattleHighRisk { get; init; }
    public float RespawnRisk { get; init; }
    public float NumberDisadvantageRisk { get; init; }
    public float FlankRisk { get; init; }
    public float EnemyMainGroupDirectionRisk { get; init; }
    public float TerrainRisk { get; init; }
    public float RetreatRouteRisk { get; init; }
    public float EncirclementRisk { get; init; }
    public float AmbushRisk { get; init; }
    public float CohesionRisk { get; init; }
    public float HighGroundDropRisk { get; init; }
    public float ThirdPartyPincerRisk { get; init; }
    public float CoordinatedSquadRisk { get; init; }
    public float ChokeBlockRisk { get; init; }
    public float ScorePressure { get; init; }
    public string RiskLevel { get; init; } = "未知";
    public string SummaryText { get; init; } = "风险评估尚未形成";
}

public readonly record struct BattlefieldPlayerStatusFrameEventSnapshot(
    long ObservedAtTicks,
    PlayerStatusChangeKind ChangeKind,
    BattlefieldPlayerRelation Relation,
    ulong GameObjectId,
    string PlayerName,
    string JobName,
    uint StatusId,
    string StatusName,
    ushort Param,
    float RemainingTime,
    uint SourceId,
    string SourceName,
    string TacticalTag,
    string SourceText);

public readonly record struct BattlefieldPlayerDeathFrameEventSnapshot(
    long ObservedAtTicks,
    BattlefieldPlayerRelation Relation,
    ulong GameObjectId,
    string PlayerName,
    string JobName,
    bool IsDead,
    string SourceText);

public readonly record struct BattlefieldPlayerTargetFrameEventSnapshot(
    long ObservedAtTicks,
    PlayerTargetChangeKind Kind,
    BattlefieldPlayerRelation Relation,
    ulong GameObjectId,
    string PlayerName,
    string JobName,
    ulong PreviousTargetObjectId,
    string PreviousTargetName,
    BattlefieldPlayerRelation PreviousTargetRelation,
    ulong CurrentTargetObjectId,
    string CurrentTargetName,
    BattlefieldPlayerRelation CurrentTargetRelation,
    string SourceText);

public sealed class BattlefieldPlayerFrameEventSituationSnapshot
{
    public BattlefieldPlayerStatusFrameEventSnapshot[] StatusEvents { get; init; } = Array.Empty<BattlefieldPlayerStatusFrameEventSnapshot>();
    public BattlefieldPlayerDeathFrameEventSnapshot[] DeathEvents { get; init; } = Array.Empty<BattlefieldPlayerDeathFrameEventSnapshot>();
    public BattlefieldPlayerTargetFrameEventSnapshot[] TargetEvents { get; init; } = Array.Empty<BattlefieldPlayerTargetFrameEventSnapshot>();
    public int FriendlyDeathsRecent { get; init; }
    public int EnemyDeathsRecent { get; init; }
    public int FriendlyRevivesRecent { get; init; }
    public int EnemyRevivesRecent { get; init; }
    public int FriendlyStatusGainedRecent { get; init; }
    public int EnemyStatusGainedRecent { get; init; }
    public int FriendlyControlledRecent { get; init; }
    public int EnemyControlledRecent { get; init; }
    public int FriendlyDefensiveRecent { get; init; }
    public int EnemyDefensiveRecent { get; init; }
    public int EnemyTargetingFriendlyRecent { get; init; }
    public int FriendlyTargetingEnemyRecent { get; init; }
    public string SourceText { get; init; } = "玩家帧事件尚未采集";
    public string SummaryText { get; init; } = "玩家帧事件尚未形成";
}

public enum BattlefieldCommandKind
{
    Unknown,
    Regroup,
    Engage,
    Retreat,
    Disengage,
    Rotate,
    AttackObjective,
    ContestObjective,
    DefendObjective,
    AbandonObjective,
    Split,
    FocusTarget,
    ProtectTarget,
    Spread,
    Hold,
    Detour,
    PressureSide,
    Wait
}

public enum BattlefieldActionType
{
    Unknown,
    Rotate,
    DefendObjective,
    ContestObjective,
    AbandonObjective,
    AttackIce,
    TouchObjective,
    InterruptTouch,
    Engage,
    Retreat,
    ReturnToBase,
    Flank,
    WrapBehind,
    BacklinePressure,
    FocusTarget,
    ProtectHighBattleHigh,
    Regroup,
    Spread,
    Detour,
    Hold,
    Wait
}

public enum BattlefieldEnemyIntentKind
{
    Unknown,
    Pincer,
    Rotate,
    Engage,
    RetreatBait,
    ObjectiveRush,
    Hold,
    Flank
}

public enum BattlefieldLlmDecisionNeedKind
{
    None,
    ManualProbe,
    StrategicSampling,
    NearbyThirdPartyFight,
    FarObjectiveWithCloseEnemies,
    ScoreEndgameConflict,
    ObjectiveRace,
    UnstableThreeFaction,
    ScoreTargetAmbiguity
}

public enum BattlefieldTeamRoleInsightKind
{
    Unknown,
    ProtectHighBattleHigh,
    FrontlineOpenPath,
    BacklineUnderDive,
    ControlWindow,
    ProtectFocusTarget,
    BurstWindow
}

public readonly record struct BattlefieldCommandSnapshot(
    string Id,
    BattlefieldCommandKind Kind,
    string Scope,
    string CommandText,
    float Score,
    float Urgency,
    int CooldownSeconds,
    Vector3 Position,
    string TargetName,
    string ReasonText,
    string EvidenceText);

public readonly record struct BattlefieldActionCandidateSnapshot(
    string Id,
    string CommandId,
    BattlefieldActionType ActionType,
    BattlefieldCommandKind CommandKind,
    string Scope,
    string Text,
    float Priority,
    float Confidence,
    float Risk,
    float Urgency,
    Vector3 Destination,
    string DestinationName,
    string TargetId,
    string TargetName,
    string RouteId,
    string RouteText,
    int? CountdownSeconds,
    int EtaSeconds,
    int HoldSeconds,
    string PurposeText,
    string ReasonText,
    string EvidenceText,
    string FailureConditionText);

public sealed class BattlefieldCommandSituationSnapshot
{
    public bool IsAvailable { get; init; }
    public BattlefieldCommandSnapshot[] Commands { get; init; } = Array.Empty<BattlefieldCommandSnapshot>();
    public BattlefieldActionCandidateSnapshot[] ActionCandidates { get; init; } = Array.Empty<BattlefieldActionCandidateSnapshot>();
    public BattlefieldCommandSnapshot? PrimaryCommand { get; init; }
    public BattlefieldCommandSnapshot? EmergencyCommand { get; init; }
    public BattlefieldActionCandidateSnapshot? PrimaryAction { get; init; }
    public BattlefieldActionCandidateSnapshot? PublishedAction { get; init; }
    public bool IsActionHoldActive { get; init; }
    public int ActionHoldRemainingSeconds { get; init; }
    public string ActionHoldReason { get; init; } = string.Empty;
    public BattlefieldCommandPublishSnapshot Publish { get; init; } = new();
    public string SummaryText { get; init; } = "实时指挥规则评分尚未形成";
}

public sealed class BattlefieldCommandPublishSnapshot
{
    public bool ShouldAnnounce { get; init; }
    public bool IsSuppressed { get; init; }
    public bool InterruptedCooldown { get; init; }
    public BattlefieldCommandSnapshot? Command { get; init; }
    public string SpeakText { get; init; } = string.Empty;
    public string PriorityText { get; init; } = "低";
    public string StatusText { get; init; } = "暂无可发布指挥短句";
    public string SuppressionReason { get; init; } = string.Empty;
    public int GlobalCooldownRemainingSeconds { get; init; }
    public int CommandCooldownRemainingSeconds { get; init; }
    public int KindCooldownRemainingSeconds { get; init; }
    public long LastIssuedAgeMs { get; init; } = -1;
    public int Sequence { get; init; }
}

public readonly record struct BattlefieldCommandEffectivenessSnapshot(
    BattlefieldCommandKind Kind,
    int SampleCount,
    float AverageScore,
    float PositiveRate,
    float Modifier,
    string SummaryText);

public readonly record struct BattlefieldEnemyIntentPredictionSnapshot(
    BattlefieldEnemyIntentKind Kind,
    byte? Battalion,
    string AllianceName,
    float Confidence,
    float Urgency,
    Vector3 Position,
    string Recommendation,
    string EvidenceText);

public readonly record struct BattlefieldMapDecisionTemplateSnapshot(
    FrontlineMapType MapType,
    string Name,
    float RewardWeight,
    float TimingWeight,
    float DistanceWeight,
    float PressureWeight,
    float TeamAdvantageWeight,
    float TerrainWeight,
    float RiskPenaltyWeight,
    string SummaryText);

public readonly record struct BattlefieldInputReliabilityComponentSnapshot(
    string Id,
    string Label,
    float Reliability,
    float Weight,
    bool IsCritical,
    string EvidenceText);

public sealed class BattlefieldInputReliabilitySnapshot
{
    public bool IsAvailable { get; init; }
    public float OverallReliability { get; init; }
    public float ScoreReliability { get; init; }
    public float TimeReliability { get; init; }
    public float PlayerReliability { get; init; }
    public float ObjectiveReliability { get; init; }
    public float MapTacticsReliability { get; init; }
    public float CombatEventReliability { get; init; }
    public float AnnouncementReliability { get; init; }
    public float PublishReliabilityThreshold { get; init; }
    public float PublishActionConfidenceThreshold { get; init; }
    public float CriticalInputReliabilityThreshold { get; init; }
    public bool CanPublish { get; init; }
    public bool ShouldOnlyHint => IsAvailable && !CanPublish;
    public string GateText { get; init; } = string.Empty;
    public BattlefieldInputReliabilityComponentSnapshot[] Components { get; init; } = Array.Empty<BattlefieldInputReliabilityComponentSnapshot>();
    public string SummaryText { get; init; } = "输入可靠度尚未形成";
}

public readonly record struct BattlefieldTeamRoleInsightSnapshot(
    BattlefieldTeamRoleInsightKind Kind,
    string Label,
    float Severity,
    int PlayerCount,
    string TargetName,
    Vector3 Position,
    string Recommendation,
    string EvidenceText);

public sealed class BattlefieldDecisionQualitySnapshot
{
    public bool IsAvailable { get; init; }
    public BattlefieldCommandEffectivenessSnapshot[] CommandEffectiveness { get; init; } = Array.Empty<BattlefieldCommandEffectivenessSnapshot>();
    public BattlefieldEnemyIntentPredictionSnapshot[] EnemyIntentPredictions { get; init; } = Array.Empty<BattlefieldEnemyIntentPredictionSnapshot>();
    public BattlefieldTeamRoleInsightSnapshot[] TeamRoleInsights { get; init; } = Array.Empty<BattlefieldTeamRoleInsightSnapshot>();
    public BattlefieldMapDecisionTemplateSnapshot MapTemplate { get; init; }
    public BattlefieldInputReliabilitySnapshot InputReliability { get; init; } = new();
    public string CalibrationText { get; init; } = string.Empty;
    public string SummaryText { get; init; } = "决策质量层尚未形成";
}

public readonly record struct BattlefieldPriorityTargetSnapshot(
    string Lane,
    string TargetName,
    string ActionText,
    float Priority,
    float Urgency,
    Vector3 Position,
    string ReasonText,
    string EvidenceText);

public sealed class BattlefieldLlmStrategicDecisionSnapshot
{
    public bool IsEnabled { get; init; }
    public bool IsConfigured { get; init; }
    public bool IsAvailable { get; init; }
    public bool IsPending { get; init; }
    public bool ShouldRequest { get; init; }
    public bool IsFresh { get; init; }
    public BattlefieldLlmDecisionNeedKind NeedKind { get; init; } = BattlefieldLlmDecisionNeedKind.None;
    public string NeedText { get; init; } = "无需 AI 大决策";
    public string GateReason { get; init; } = string.Empty;
    public string SituationKey { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public string ShortReason { get; init; } = string.Empty;
    public string RecommendedAction { get; init; } = string.Empty;
    public string PriorityTarget { get; init; } = string.Empty;
    public float Confidence { get; init; }
    public float Risk { get; init; }
    public string DebugText { get; init; } = string.Empty;
    public string RawJson { get; init; } = string.Empty;
    public string ErrorText { get; init; } = string.Empty;
    public long RequestedAtTicks { get; init; } = -1;
    public long ReceivedAtTicks { get; init; } = -1;
    public int AgeSeconds { get; init; } = -1;
    public string StatusText { get; init; } = "AI 大决策尚未运行";
}

public readonly record struct BattlefieldLlmConversationTurnSnapshot(
    long Ticks,
    int MatchRemainingSeconds,
    string NeedText,
    string OperatorNote,
    string Decision,
    string ShortReason,
    float Confidence,
    string SituationKey);

public sealed class BattlefieldLlmDebugSnapshot
{
    public bool IsEnabled { get; init; }
    public bool IsConfigured { get; init; }
    public bool IsPending { get; init; }
    public bool HasRequest { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public string SessionId { get; init; } = string.Empty;
    public string CurrentNeedText { get; init; } = string.Empty;
    public string CurrentGateReason { get; init; } = string.Empty;
    public string LastRequestNeedText { get; init; } = string.Empty;
    public string LastRequestGateReason { get; init; } = string.Empty;
    public string LastRequestSituationKey { get; init; } = string.Empty;
    public string ManualInstruction { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = string.Empty;
    public string UserPrompt { get; init; } = string.Empty;
    public string RawResponse { get; init; } = string.Empty;
    public string ParsedJson { get; init; } = string.Empty;
    public string ErrorText { get; init; } = string.Empty;
    public long RequestedAtTicks { get; init; } = -1;
    public long ReceivedAtTicks { get; init; } = -1;
    public int AgeSeconds { get; init; } = -1;
    public BattlefieldLlmConversationTurnSnapshot[] ConversationTurns { get; init; } = Array.Empty<BattlefieldLlmConversationTurnSnapshot>();
}

public sealed class BattlefieldDecisionSnapshot
{
    public bool IsAvailable { get; init; }
    public BattlefieldObjectivePrioritySnapshot[] ObjectivePriorities { get; init; } = Array.Empty<BattlefieldObjectivePrioritySnapshot>();
    public BattlefieldObjectivePrioritySnapshot? PrimaryObjective { get; init; }
    public BattlefieldPriorityTargetSnapshot? ObjectivePriorityTarget { get; init; }
    public BattlefieldPriorityTargetSnapshot? FightPriorityTarget { get; init; }
    public BattlefieldRiskAssessmentSnapshot RiskAssessment { get; init; } = new();
    public BattlefieldCommandSituationSnapshot CommandSituation { get; init; } = new();
    public BattlefieldActionCandidateSnapshot[] ActionCandidates { get; init; } = Array.Empty<BattlefieldActionCandidateSnapshot>();
    public BattlefieldActionCandidateSnapshot? PrimaryAction { get; init; }
    public BattlefieldActionCandidateSnapshot? PublishedAction { get; init; }
    public BattlefieldDecisionQualitySnapshot DecisionQuality { get; init; } = new();
    public string RecommendedAction { get; init; } = "战场状态不足，继续寻找目标";
    public string SummaryText { get; init; } = "目标优先级评分尚未形成";
}

public sealed class BattlefieldSnapshot
{
    public long UpdatedAtTicks { get; init; }
    public uint TerritoryType { get; init; }
    public uint MapId { get; init; }
    public bool IsInFrontline { get; init; }
    public bool IsAreaTransitioning { get; init; }
    public int MatchTimeRemaining { get; init; }
    public AllianceData[] Alliances { get; init; } = Array.Empty<AllianceData>();
    public BattlefieldPlayerSnapshot? LocalPlayer { get; init; }
    public BattlefieldPlayerSnapshot[] Players { get; init; } = Array.Empty<BattlefieldPlayerSnapshot>();
    public BattlefieldMapEventSnapshot[] MapEvents { get; init; } = Array.Empty<BattlefieldMapEventSnapshot>();
    public BattlefieldFieldMarkerSnapshot[] FieldMarkers { get; init; } = Array.Empty<BattlefieldFieldMarkerSnapshot>();
    public BattlefieldMapVisionPointSnapshot[] MapVisionPoints { get; init; } = Array.Empty<BattlefieldMapVisionPointSnapshot>();
    public BattlefieldMapVisionClusterSnapshot[] MapVisionClusters { get; init; } = Array.Empty<BattlefieldMapVisionClusterSnapshot>();
    public BattlefieldTargetMarkerSnapshot[] TargetMarkers { get; init; } = Array.Empty<BattlefieldTargetMarkerSnapshot>();
    public BattlefieldObjectiveSnapshot[] Objectives { get; init; } = Array.Empty<BattlefieldObjectiveSnapshot>();
    public BattlefieldMapObjectiveSnapshot[] MapObjectives { get; init; } = Array.Empty<BattlefieldMapObjectiveSnapshot>();
    public BattlefieldPlayerClusterSnapshot[] PlayerClusters { get; init; } = Array.Empty<BattlefieldPlayerClusterSnapshot>();
    public BattlefieldPlayerTrackSnapshot[] PlayerTracks { get; init; } = Array.Empty<BattlefieldPlayerTrackSnapshot>();
    public BattlefieldGroupTrackSnapshot[] EnemyMainGroupTrack { get; init; } = Array.Empty<BattlefieldGroupTrackSnapshot>();
    public BattlefieldTeamSituationSnapshot TeamSituation { get; init; } = new();
    public BattlefieldScoreSituationSnapshot ScoreSituation { get; init; } = new();
    public BattlefieldTimeSituationSnapshot TimeSituation { get; init; } = new();
    public BattlefieldLimitBreakSnapshot LimitBreak { get; init; } = new();
    public BattlefieldAnnouncementSituationSnapshot AnnouncementSituation { get; init; } = new();
    public BattlefieldChatEventSituationSnapshot ChatEventSituation { get; init; } = new();
    public BattlefieldPlayerFrameEventSituationSnapshot PlayerFrameEvents { get; init; } = new();
    public BattlefieldMapTacticsSnapshot MapTactics { get; init; } = new();
    public BattlefieldDecisionSnapshot Decision { get; init; } = new();
    public BattlefieldLlmStrategicDecisionSnapshot LlmStrategicDecision { get; init; } = new();
    public FrontlineKnowledgeSnapshot Knowledge { get; init; } = new();
    public int FriendlyPlayerCount { get; init; }
    public int EnemyPlayerCount { get; init; }
    public int DeadPlayerCount { get; init; }
    public int CastingPlayerCount { get; init; }
    public string ScoreDebugInfo { get; init; } = string.Empty;
    public string StatusText { get; init; } = "战场状态尚未采集";
}
