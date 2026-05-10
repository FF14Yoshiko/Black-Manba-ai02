using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Statuses;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace ai02;

public sealed class WorldStateService : IDisposable
{
    private const int DefaultRefreshIntervalMs = 750;
    private const ulong InvalidGameObjectId = 0xE0000000;
    private const int MatchDurationSeconds = 20 * 60;
    private const float PlayerClusterRadius = 35f;
    private const long TacticalHistoryExpiryMs = 45000;
    private const long RecentEventWindowMs = 20000;
    private const long MovementLookbackMs = 3000;
    private const long EnemyMainGroupMemoryTtlMs = 20000;
    private const long EnemyMainGroupPredictiveWindowMs = 6000;
    private const float EnemyMainGroupTeleportSpeedThreshold = 24f;
    private const float EnemyMainGroupRotationDistanceThreshold = 90f;
    private const long RespawnWaveJoinWindowMs = 6000;
    private const long RespawnWaveMaxAgeMs = 30000;
    private const float LowHpThresholdPercent = 35f;
    private const float MovementMinDistance = 3f;
    private const float MapVisionClusterRadius = 55f;
    private const float EnemyMeanShiftBandwidth = 65f;
    private const float EnemyClusterMergeDistance = 32f;
    private const int EnemyKMeansIterations = 8;
    private const int MaxEnemyClustersPerAlliance = 4;
    private const float EnemySplitMinDistance = 95f;
    private const int EnemySplitMinSecondaryCount = 3;
    private const long ObjectiveHistoryExpiryMs = 90000;
    private const long ObjectiveRecentDamageWindowMs = 6000;
    private const float ObjectiveActorMatchDistance = 55f;
    private const int FocusedObjectiveTargetThreshold = 3;
    private const int FocusedObjectiveCastThreshold = 1;
    private const long ScoreTrendWindowMs = 30000;
    private const long LimitBreakRecentEngagementWindowMs = 18000;
    private const int DefaultLimitBreakChargeSeconds = 90;
    private const long KeySkillUseHistoryExpiryMs = 90000;
    private const long KeySkillRecentUseWindowMs = 12000;
    private const long PlayerPositionSampleWindowMs = 12000;
    private const long PlayerPositionSampleMinIntervalMs = 500;
    private const float PlayerPositionSampleMinDistance = 0.35f;
    private const long SlowRefreshLogCooldownMs = 15000;
    private const double SlowRefreshWarningMs = 18d;
    private const long DecisionRefreshDeferMs = 150;
    private const long DecisionRefreshMaxDeferMs = 900;
    private const double SlowDecisionRefreshWarningMs = 14d;
    private const long StatusTextRefreshIntervalMs = 12000;
    private const long DerivedClusterRefreshIntervalMs = 2500;
    private const long MovementTrackRefreshIntervalMs = 3500;
    private const float ClusterSignatureQuantization = 4f;
    private const int MaxObjectScanEntriesPerFrame = 6;

    private static readonly HashSet<uint> CountdownIconIds = new()
    {
        60628, 60629, 60630, 60624, 60625, 60626, 60620, 60621, 60622,
        60989, 60990, 63987, 63979, 63986, 60992, 63985, 60991
    };

    private static readonly HashSet<uint> HealthIconIds = new() { 60902, 60904, 60999, 60998 };

    private static readonly HashSet<uint> ControlPointIconIds = new()
    {
        60585, 60586, 60587, 60588, 60589, 60590, 60591, 60592, 60593, 60594, 60595, 60596
    };

    private static readonly Dictionary<uint, int> SealRockControlPointScores = new()
    {
        [60585] = 80, [60586] = 80, [60587] = 80, [60588] = 80,
        [60589] = 120, [60590] = 120, [60591] = 120, [60592] = 120,
        [60593] = 160, [60594] = 160, [60595] = 160, [60596] = 160
    };

    private static readonly Dictionary<uint, int> AzimSteppeControlPointScores = new()
    {
        [60585] = 50, [60586] = 50, [60587] = 50, [60588] = 50,
        [60589] = 100, [60590] = 100, [60591] = 100, [60592] = 100,
        [60593] = 200, [60594] = 200, [60595] = 200, [60596] = 200
    };

    private static readonly Dictionary<uint, JobInfo> JobInfoByClassJobId = new()
    {
        [19] = new JobInfo("骑士", "坦克"),
        [20] = new JobInfo("武僧", "近战"),
        [21] = new JobInfo("战士", "坦克"),
        [22] = new JobInfo("龙骑士", "近战"),
        [23] = new JobInfo("吟游诗人", "远敏"),
        [24] = new JobInfo("白魔法师", "治疗"),
        [25] = new JobInfo("黑魔法师", "法系"),
        [26] = new JobInfo("秘术师", "未知"),
        [27] = new JobInfo("召唤师", "法系"),
        [28] = new JobInfo("学者", "治疗"),
        [29] = new JobInfo("双剑师", "未知"),
        [30] = new JobInfo("忍者", "近战"),
        [31] = new JobInfo("机工士", "远敏"),
        [32] = new JobInfo("暗黑骑士", "坦克"),
        [33] = new JobInfo("占星术士", "治疗"),
        [34] = new JobInfo("武士", "近战"),
        [35] = new JobInfo("赤魔法师", "法系"),
        [36] = new JobInfo("青魔法师", "法系"),
        [37] = new JobInfo("绝枪战士", "坦克"),
        [38] = new JobInfo("舞者", "远敏"),
        [39] = new JobInfo("钐镰客", "近战"),
        [40] = new JobInfo("贤者", "治疗"),
        [41] = new JobInfo("蝰蛇剑士", "近战"),
        [42] = new JobInfo("绘灵法师", "法系"),
    };

    private static readonly Dictionary<uint, LimitBreakThreatProfile> LimitBreakThreatProfileByClassJobId = new()
    {
        [19] = new("保护/单点控制", 14f, "骑士极限技更偏保护和压制"),
        [20] = new("单点破防", 20f, "武僧极限技对关键目标威胁高"),
        [21] = new("群体破防开团", 27f, "战士极限技可制造群体破防窗口"),
        [22] = new("范围爆发", 22f, "龙骑士极限技对密集人群威胁高"),
        [23] = new("远程团队增益", 16f, "吟游诗人极限技更偏团队推进"),
        [24] = new("治疗/控制反打", 21f, "白魔法师极限技可抬血并反打"),
        [25] = new("远程范围压制", 20f, "黑魔法师极限技对站桩人群威胁高"),
        [27] = new("召唤范围爆发", 22f, "召唤师极限技可打大范围压制"),
        [28] = new("团队续航", 16f, "学者极限技更偏团队续航"),
        [30] = new("斩杀连锁", 28f, "忍者极限技对残血和高战意目标威胁极高"),
        [31] = new("单点处决", 24f, "机工士极限技对残血/防御目标威胁高"),
        [32] = new("群体拉扯开团", 30f, "暗黑骑士极限技可配合队友形成团灭窗口"),
        [33] = new("团队增益/续航", 17f, "占星术士极限技偏团队节奏"),
        [34] = new("反打斩杀", 27f, "武士极限技在混战和反打中威胁高"),
        [35] = new("混合爆发", 19f, "赤魔法师极限技偏中距离爆发"),
        [37] = new("范围压制", 19f, "绝枪战士极限技可压低密集目标"),
        [38] = new("群体控制/破防", 27f, "舞者极限技可配合队伍打开群控窗口"),
        [39] = new("群控突进", 26f, "钐镰客极限技可强开并制造恐慌窗口"),
        [40] = new("区域压制/续航", 18f, "贤者极限技偏区域防守和反打"),
        [41] = new("近战爆发", 22f, "蝰蛇剑士极限技偏贴身爆发"),
        [42] = new("远程范围爆发", 21f, "绘灵法师极限技偏范围压制"),
    };

    private static readonly Dictionary<uint, KeySkillProfile[]> KeySkillProfilesByClassJobId = new()
    {
        [19] = new[] { new KeySkillProfile("守护/保护", BattlefieldKeySkillKind.Defensive, 25, 14f, "骑士可保护关键目标并拖住点位", false, false, false, false) },
        [20] = new[] { new KeySkillProfile("陨石冲击/防御拆除", BattlefieldKeySkillKind.GuardBreak, 60, 21f, "武僧可单体拆防并打断关键防守", false, true, true, false) },
        [21] = new[] { new KeySkillProfile("原初的怒号/群体破防", BattlefieldKeySkillKind.GuardBreak, 30, 28f, "战士可制造群体破防和推进窗口", true, true, false, true) },
        [22] = new[] { new KeySkillProfile("跳入范围爆发", BattlefieldKeySkillKind.Burst, 20, 20f, "龙骑士对密集人群有突入爆发压制", false, false, false, true) },
        [23] = new[] { new KeySkillProfile("沉默/远程打断", BattlefieldKeySkillKind.CrowdControl, 20, 16f, "吟游诗人可远程打断关键读条和撤退", true, false, false, false) },
        [24] = new[] { new KeySkillProfile("自然的奇迹", BattlefieldKeySkillKind.CrowdControl, 25, 23f, "白魔法师可点控关键目标并接集火", true, false, false, false) },
        [25] = new[] { new KeySkillProfile("睡眠/范围压制", BattlefieldKeySkillKind.CrowdControl, 25, 20f, "黑魔法师可对密集区域打控制和压制", true, false, false, true) },
        [27] = new[] { new KeySkillProfile("召唤范围爆发", BattlefieldKeySkillKind.AreaPressure, 20, 19f, "召唤师能对目标区形成范围爆发压力", false, false, false, true) },
        [28] = new[] { new KeySkillProfile("团队展开/群体护盾", BattlefieldKeySkillKind.Support, 30, 15f, "学者可提高队伍续航并支撑反打", false, false, false, true) },
        [30] = new[] { new KeySkillProfile("残血斩杀", BattlefieldKeySkillKind.Execute, 15, 27f, "忍者对低血目标和高战意目标斩杀威胁高", false, false, true, false) },
        [31] = new[] { new KeySkillProfile("钻头", BattlefieldKeySkillKind.DefensePierce, 20, 25f, "机工士钻头可无视防御减伤打关键残血", false, true, true, false) },
        [32] = new[] { new KeySkillProfile("暗黑拉人", BattlefieldKeySkillKind.Engage, 30, 30f, "暗黑骑士可把人群拉入队友爆发点", true, false, false, true) },
        [33] = new[] { new KeySkillProfile("重力/群体减速", BattlefieldKeySkillKind.CrowdControl, 25, 17f, "占星术士可限制转点和撤退节奏", true, false, false, true) },
        [34] = new[] { new KeySkillProfile("反打斩杀窗口", BattlefieldKeySkillKind.Execute, 25, 25f, "武士在被集火或混战中容易形成反打处决", false, false, true, false) },
        [35] = new[] { new KeySkillProfile("沉默/连击爆发", BattlefieldKeySkillKind.CrowdControl, 20, 18f, "赤魔法师可用控制衔接中距离爆发", true, false, false, false) },
        [37] = new[] { new KeySkillProfile("连续剑爆发", BattlefieldKeySkillKind.Burst, 20, 19f, "绝枪战士贴身爆发会压低前排血线", false, false, false, false) },
        [38] = new[] { new KeySkillProfile("行列步/群控破防", BattlefieldKeySkillKind.GuardBreak, 30, 27f, "舞者可用群体控制和拆防打开团战", true, true, false, true) },
        [39] = new[] { new KeySkillProfile("暗夜游魂/恐慌", BattlefieldKeySkillKind.CrowdControl, 30, 26f, "钐镰客可制造群体恐慌和拆防窗口", true, true, false, true) },
        [40] = new[] { new KeySkillProfile("区域压制/治疗切换", BattlefieldKeySkillKind.Support, 25, 16f, "贤者可用区域能力支撑推进或反打", false, false, false, true) },
        [41] = new[] { new KeySkillProfile("贴身爆发", BattlefieldKeySkillKind.Burst, 20, 21f, "蝰蛇剑士贴身爆发对落单目标威胁高", false, false, true, false) },
        [42] = new[] { new KeySkillProfile("远程范围压制", BattlefieldKeySkillKind.AreaPressure, 25, 20f, "绘灵法师可在目标区形成远程范围压制", false, false, false, true) },
    };

    private static readonly Regex CountdownRegex = new(@"(?<!\d)(\d{1,2}):([0-5]\d)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex PercentRegex = new(@"(\d{1,3})%", RegexOptions.Compiled);
    private static readonly Regex LocationIdRegex = new(@"(?<!\d)(0?[1-9]|1[0-5])(?!\d)", RegexOptions.Compiled);

    private static readonly Dictionary<uint, int> DefaultBattleHighLevelByStatusId = new()
    {
        [1263] = 1,
        [1264] = 2,
        [1265] = 3,
        [1266] = 4,
        [1267] = 5
    };

    private static readonly Regex BattleHighDigitRegex = new(@"(?<!\d)([1-5])(?!\d)", RegexOptions.Compiled);
    private static readonly Regex BattleHighAsciiRomanRegex = new(@"\b(V|IV|III|II|I)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] BattleHighKeywords =
    {
        "Battle High",
        "Battle Fever",
        "战意",
        "戰意",
        "斗志昂扬",
        "楝ュ織鏄傛彋",
        "斗志高昂",
        "楝ュ織楂樻槀",
        "狂热",
        "狂熱"
    };

    private static readonly Dictionary<uint, (string Name, string Description)> StatusTextCache = new();

    private static readonly string[] GuardingStatusKeywords =
    {
        "防御",
        "Guard"
    };

    private static readonly string[] CrowdControlStatusKeywords =
    {
        "鐪╂檿",
        "鏄忚糠",
        "睡眠",
        "姝㈡",
        "鍔犻噸",
        "沉默",
        "冻结",
        "深度冻结",
        "变形",
        "鎭愭厡",
        "魅惑",
        "Stun",
        "Sleep",
        "Bind",
        "Heavy",
        "Silence",
        "Deep Freeze",
        "Miracle of Nature",
        "Hysteria"
    };

    private static readonly string[] ControlImmuneStatusKeywords =
    {
        "净化",
        "韧性",
        "抗性",
        "免疫控制",
        "Purify",
        "Resilience",
        "control effects"
    };

    private static readonly string[] InvulnerableStatusKeywords =
    {
        "无敌",
        "绁炲湥棰嗗煙",
        "行尸走肉",
        "瓒呯伀娴佹槦",
        "无法受到伤害",
        "免疫伤害",
        "Invulnerable",
        "Hallowed Ground",
        "Living Dead",
        "damage nullified"
    };

    private static readonly string[] SnowBlessingStatusKeywords =
    {
        "雪精祝福",
        "雪精",
        "Snow",
        "Blessing"
    };

    private readonly Configuration configuration;
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IPluginLog log;
    private readonly FrontlineScoreReader scoreReader;
    private readonly AreaMapProjectionService areaMapProjectionService;
    private readonly LimitBreakService limitBreakService;
    private readonly FrontlineAnnouncementReader announcementReader;
    private readonly FrontlineChatEventReader chatEventReader;
    private readonly FrontlineKeySkillEventReader keySkillEventReader;
    private readonly StatusEffectTracker statusEffectTracker;
    private readonly MapTacticalAnalysisService mapTacticalAnalysisService;
    private readonly TacticalDecisionEngineService tacticalDecisionEngineService;
    private readonly LlmStrategicDecisionService llmStrategicDecisionService;
    private readonly BattlefieldReplayRecorder battlefieldReplayRecorder;

    private BattlefieldSnapshot latestSnapshot = new();
    private long lastRefreshTicks;
    private long adaptiveWorldRefreshBackoffUntilTicks;
    private long lastStrategicDecisionRefreshTicks;
    private long lastCombatDecisionRefreshTicks;
    private readonly Dictionary<ulong, PlayerHistoryEntry> playerHistory = new();
    private readonly Dictionary<string, ObjectiveHistoryEntry> objectiveHistory = new();
    private readonly Queue<EnemyClusterHistoryEntry> enemyClusterHistory = new();
    private readonly Queue<ScoreHistoryEntry> scoreHistory = new();
    private readonly Queue<KeySkillUseHistoryEntry> keySkillUseHistory = new();
    private readonly Dictionary<string, long> lastKeySkillUseByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<(ulong GameObjectId, string SkillName), long> lastKeySkillUseTicksByPlayerSkill = new();
    private ContextDerivedAnalysisSnapshot cachedContextDerivedAnalysis = new();
    private Task<ContextDerivedAnalysisResult>? contextDerivedAnalysisTask;
    private long lastContextDerivedAnalysisRequestedTicks = -1;
    private ClusterDerivedAnalysisSnapshot cachedClusterDerivedAnalysis = new();
    private Task<ClusterDerivedAnalysisResult>? clusterDerivedAnalysisTask;
    private long lastClusterDerivedAnalysisRequestedTicks = -1;
    private TeamDerivedAnalysisSnapshot cachedTeamDerivedAnalysis = new();
    private BattlefieldLimitBreakThreatSituationSnapshot cachedLimitBreakThreats = new();
    private BattlefieldKeySkillThreatSituationSnapshot cachedKeySkillThreats = new();
    private Task<TeamDerivedAnalysisResult>? teamDerivedAnalysisTask;
    private long lastTeamDerivedAnalysisRequestedTicks = -1;
    private Task<ThreatAnalysisResult>? threatAnalysisTask;
    private long lastThreatAnalysisRequestedTicks = -1;
    private string lastEnemyMovementSource = string.Empty;
    private string? cachedBattleHighStatusIdsRaw;
    private IReadOnlyDictionary<uint, int>? cachedBattleHighStatusIdMap;
    private string? cachedGuardingStatusIdsRaw;
    private string? cachedCrowdControlledStatusIdsRaw;
    private string? cachedControlImmuneStatusIdsRaw;
    private string? cachedInvulnerableStatusIdsRaw;
    private string? cachedSnowBlessingStatusIdsRaw;
    private TacticalStatusIdSets? cachedTacticalStatusIdSets;
    private long lastSlowRefreshLogTicks;
    private long lastSlowDecisionRefreshLogTicks;
    private long lastStatusTextRefreshTicks;
    private string cachedStatusText = string.Empty;
    private long lastMapVisionRefreshTicks;
    private uint cachedMapVisionTerritoryType;
    private uint cachedMapVisionMapId;
    private BattlefieldMapVisionPointSnapshot[] cachedMapVisionPoints = Array.Empty<BattlefieldMapVisionPointSnapshot>();
    private int cachedPlayerClusterSignature = int.MinValue;
    private BattlefieldPlayerClusterSnapshot[] cachedPlayerClusters = Array.Empty<BattlefieldPlayerClusterSnapshot>();
    private long lastPlayerClusterBuildTicks;
    private int cachedMapVisionClusterSignature = int.MinValue;
    private BattlefieldMapVisionClusterSnapshot[] cachedMapVisionClusters = Array.Empty<BattlefieldMapVisionClusterSnapshot>();
    private long lastMapVisionClusterBuildTicks;
    private int cachedEnemyClusterSignature = int.MinValue;
    private BattlefieldEnemyClusterSnapshot[] cachedEnemyClusters = Array.Empty<BattlefieldEnemyClusterSnapshot>();
    private long lastEnemyClusterBuildTicks;
    private BattlefieldPlayerTrackSnapshot[] cachedPlayerTracks = Array.Empty<BattlefieldPlayerTrackSnapshot>();
    private long lastPlayerTrackBuildTicks;
    private BattlefieldGroupTrackSnapshot[] cachedEnemyMainGroupTrack = Array.Empty<BattlefieldGroupTrackSnapshot>();
    private long lastEnemyMainGroupTrackBuildTicks;
    private EnemyMainGroupObservationEntry? lastEnemyMainGroupObservation;
    private ObjectTableSnapshot cachedObjectTableState = new(Array.Empty<BattlefieldPlayerSnapshot>(), Array.Empty<BattlefieldObjectiveActorSnapshot>(), null);
    private FrontlineMapType cachedObjectTableMapType;
    private uint cachedObjectTableTerritoryType;
    private uint cachedObjectTableMapId;
    private bool cachedObjectTableIsInFrontline;
    private long lastObjectScanCompletedTicks;
    private IncrementalObjectScanSession? activeObjectScan;
    private PendingWorldRefreshSession? activeWorldRefreshSession;
    private PendingDecisionRefresh? pendingDecisionRefresh;
    private PendingDecisionRefresh? activeDecisionRefresh;
    private Task<DecisionLayerResult>? decisionRefreshTask;
    private bool disposed;

    public WorldStateService(
        Configuration configuration,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        ICondition condition,
        IPluginLog log,
        FrontlineScoreReader scoreReader,
        AreaMapProjectionService areaMapProjectionService,
        LimitBreakService limitBreakService,
        FrontlineAnnouncementReader announcementReader,
        FrontlineChatEventReader chatEventReader,
        FrontlineKeySkillEventReader keySkillEventReader,
        StatusEffectTracker statusEffectTracker,
        MapTacticalAnalysisService mapTacticalAnalysisService,
        TacticalDecisionEngineService tacticalDecisionEngineService,
        LlmStrategicDecisionService llmStrategicDecisionService,
        BattlefieldReplayRecorder battlefieldReplayRecorder)
    {
        this.configuration = configuration;
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.framework = framework;
        this.condition = condition;
        this.log = log;
        this.scoreReader = scoreReader;
        this.areaMapProjectionService = areaMapProjectionService;
        this.limitBreakService = limitBreakService;
        this.announcementReader = announcementReader;
        this.chatEventReader = chatEventReader;
        this.keySkillEventReader = keySkillEventReader;
        this.statusEffectTracker = statusEffectTracker;
        this.mapTacticalAnalysisService = mapTacticalAnalysisService;
        this.tacticalDecisionEngineService = tacticalDecisionEngineService;
        this.llmStrategicDecisionService = llmStrategicDecisionService;
        this.battlefieldReplayRecorder = battlefieldReplayRecorder;

        framework.Update += OnFrameworkUpdate;
        Refresh();
        lastRefreshTicks = Environment.TickCount64;
        if (latestSnapshot.Decision.IsAvailable)
        {
            lastStrategicDecisionRefreshTicks = lastRefreshTicks;
            lastCombatDecisionRefreshTicks = lastRefreshTicks;
        }
    }

    public BattlefieldSnapshot GetSnapshot() => latestSnapshot;

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        framework.Update -= OnFrameworkUpdate;
        playerHistory.Clear();
        objectiveHistory.Clear();
        enemyClusterHistory.Clear();
        scoreHistory.Clear();
        keySkillUseHistory.Clear();
        lastKeySkillUseByKey.Clear();
        lastKeySkillUseTicksByPlayerSkill.Clear();
        cachedContextDerivedAnalysis = new ContextDerivedAnalysisSnapshot();
        contextDerivedAnalysisTask = null;
        lastContextDerivedAnalysisRequestedTicks = -1;
        cachedClusterDerivedAnalysis = new ClusterDerivedAnalysisSnapshot();
        clusterDerivedAnalysisTask = null;
        lastClusterDerivedAnalysisRequestedTicks = -1;
        cachedTeamDerivedAnalysis = new TeamDerivedAnalysisSnapshot();
        cachedLimitBreakThreats = new BattlefieldLimitBreakThreatSituationSnapshot();
        cachedKeySkillThreats = new BattlefieldKeySkillThreatSituationSnapshot();
        teamDerivedAnalysisTask = null;
        lastTeamDerivedAnalysisRequestedTicks = -1;
        threatAnalysisTask = null;
        lastThreatAnalysisRequestedTicks = -1;
        ClearDerivedClusterCache();
        cachedStatusText = string.Empty;
        lastEnemyMovementSource = string.Empty;
        lastEnemyMainGroupObservation = null;
        ClearIncrementalObjectScanCache();
        latestSnapshot = new BattlefieldSnapshot { StatusText = "战场状态采集已停止" };
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed)
            return;

        var now = Environment.TickCount64;
        var activeRefresh = activeWorldRefreshSession;
        if (activeRefresh == null)
            UpdateIncrementalObjectScan(now);

        var intervalMs = configuration.Performance?.EffectiveWorldRefreshIntervalMs ?? DefaultRefreshIntervalMs;
        var worldRefreshDue = now - lastRefreshTicks >= intervalMs
            && now >= adaptiveWorldRefreshBackoffUntilTicks;
        ApplyCompletedContextDerivedAnalysis();
        ApplyCompletedClusterDerivedAnalysis();
        ApplyCompletedTeamDerivedAnalysis();
        ApplyCompletedThreatAnalysis();
        ApplyCompletedDecisionRefresh(now);
        var pendingDecision = pendingDecisionRefresh;
        if (pendingDecision != null
            && decisionRefreshTask == null
            && now - pendingDecision.QueuedAtTicks >= DecisionRefreshDeferMs
            && (!worldRefreshDue || now - pendingDecision.QueuedAtTicks >= DecisionRefreshMaxDeferMs))
        {
            StartPendingDecisionRefresh(pendingDecision);
            return;
        }

        if (activeRefresh != null)
        {
            AdvanceRefreshSession(activeRefresh, now);
            return;
        }

        TryQueueCombatDecisionRefresh(now, worldRefreshDue);

        if (!worldRefreshDue)
            return;

        lastRefreshTicks = now;
        StartRefreshSession(now);
        if (activeWorldRefreshSession != null)
            AdvanceRefreshSession(activeWorldRefreshSession, now);
    }

    private void StartRefreshSession(long now)
    {
        var scoreSnapshot = scoreReader.GetSnapshot();
        var knowledge = FrontlineKnowledgeBase.GetSnapshot(clientState.TerritoryType, clientState.MapId);
        var mapType = knowledge.CurrentMap?.MapType ?? FrontlineMapType.Unknown;
        activeWorldRefreshSession = new PendingWorldRefreshSession
        {
            StartedAtTicks = now,
            StartedTimestamp = Stopwatch.GetTimestamp(),
            TerritoryType = clientState.TerritoryType,
            MapId = clientState.MapId,
            IsAreaTransitioning = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
            ScoreSnapshot = scoreSnapshot,
            Knowledge = knowledge,
            MapType = mapType,
            IsInFrontline = scoreSnapshot.IsInFrontline || knowledge.CurrentMap != null,
            Stage = WorldRefreshStage.CollectScene
        };
    }

    private void AdvanceRefreshSession(PendingWorldRefreshSession session, long now)
    {
        if (!IsRefreshSessionCurrent(session))
        {
            activeWorldRefreshSession = null;
            return;
        }

        try
        {
            switch (session.Stage)
            {
                case WorldRefreshStage.CollectScene:
                    AdvanceRefreshSceneStage(session, now);
                    break;
                case WorldRefreshStage.CollectSignals:
                    AdvanceRefreshSignalStage(session, now);
                    break;
                case WorldRefreshStage.Assemble:
                    AdvanceRefreshAssembleStage(session, now);
                    break;
                case WorldRefreshStage.Finalize:
                    FinalizeRefreshSession(session, now);
                    break;
            }
        }
        catch (Exception ex)
        {
            activeWorldRefreshSession = null;
            log.Debug(ex, "[WorldState] 战场态势刷新失败。");
            cachedStatusText = $"战场态势刷新失败：{ex.Message}";
            lastStatusTextRefreshTicks = Environment.TickCount64;
            latestSnapshot = new BattlefieldSnapshot
            {
                TerritoryType = clientState.TerritoryType,
                MapId = clientState.MapId,
                StatusText = cachedStatusText
            };
        }
    }

    private bool IsRefreshSessionCurrent(PendingWorldRefreshSession session)
        => session.TerritoryType == clientState.TerritoryType
            && session.MapId == clientState.MapId;

    private void AdvanceRefreshSceneStage(PendingWorldRefreshSession session, long now)
    {
        var topologyChanged = latestSnapshot.TerritoryType != session.TerritoryType
            || latestSnapshot.MapId != session.MapId
            || latestSnapshot.IsInFrontline != session.IsInFrontline;
        if (topologyChanged)
        {
            ClearMapVisionCache();
            ClearDerivedClusterCache();
            ClearIncrementalObjectScanCache();
            cachedContextDerivedAnalysis = new ContextDerivedAnalysisSnapshot();
            contextDerivedAnalysisTask = null;
            lastContextDerivedAnalysisRequestedTicks = -1;
            cachedClusterDerivedAnalysis = new ClusterDerivedAnalysisSnapshot();
            clusterDerivedAnalysisTask = null;
            lastClusterDerivedAnalysisRequestedTicks = -1;
            cachedTeamDerivedAnalysis = new TeamDerivedAnalysisSnapshot();
            teamDerivedAnalysisTask = null;
            lastTeamDerivedAnalysisRequestedTicks = -1;
            cachedLimitBreakThreats = new BattlefieldLimitBreakThreatSituationSnapshot();
            cachedKeySkillThreats = new BattlefieldKeySkillThreatSituationSnapshot();
            threatAnalysisTask = null;
            lastThreatAnalysisRequestedTicks = -1;
            adaptiveWorldRefreshBackoffUntilTicks = 0;
        }

        if (!session.IsInFrontline && !configuration.Radar.OutsideFrontline)
        {
            lastEnemyMainGroupObservation = null;
            pendingDecisionRefresh = null;
            decisionRefreshTask = null;
            activeDecisionRefresh = null;
            ClearMapVisionCache();
            ClearDerivedClusterCache();
            ClearIncrementalObjectScanCache();
            cachedContextDerivedAnalysis = new ContextDerivedAnalysisSnapshot();
            contextDerivedAnalysisTask = null;
            lastContextDerivedAnalysisRequestedTicks = -1;
            cachedClusterDerivedAnalysis = new ClusterDerivedAnalysisSnapshot();
            clusterDerivedAnalysisTask = null;
            lastClusterDerivedAnalysisRequestedTicks = -1;
            cachedTeamDerivedAnalysis = new TeamDerivedAnalysisSnapshot();
            teamDerivedAnalysisTask = null;
            lastTeamDerivedAnalysisRequestedTicks = -1;
            cachedLimitBreakThreats = new BattlefieldLimitBreakThreatSituationSnapshot();
            cachedKeySkillThreats = new BattlefieldKeySkillThreatSituationSnapshot();
            threatAnalysisTask = null;
            lastThreatAnalysisRequestedTicks = -1;
            cachedStatusText = "非纷争前线区域，低负载待机中。";
            lastStatusTextRefreshTicks = now;
            var standbySnapshot = new BattlefieldSnapshot
            {
                UpdatedAtTicks = now,
                TerritoryType = session.TerritoryType,
                MapId = session.MapId,
                IsInFrontline = false,
                IsAreaTransitioning = session.IsAreaTransitioning,
                MatchTimeRemaining = session.ScoreSnapshot.MatchTimeRemaining,
                ScoreSituation = BuildScoreSituation(session.ScoreSnapshot, session.Knowledge.CurrentMap, session.MapType, null, now),
                Knowledge = session.Knowledge,
                StatusText = cachedStatusText
            };
            latestSnapshot = BuildSnapshotWithLlmDecision(standbySnapshot, llmStrategicDecisionService.GetSnapshot(standbySnapshot));
            battlefieldReplayRecorder.Record(latestSnapshot);
            activeWorldRefreshSession = null;
            LogSlowRefresh(now, session.StartedTimestamp);
            return;
        }

        if (!HasReusableObjectTableSnapshot(session.MapType, session.IsInFrontline))
        {
            UpdateIncrementalObjectScan(now);
            if (!HasReusableObjectTableSnapshot(session.MapType, session.IsInFrontline))
                return;
        }

        session.ObjectTableState = cachedObjectTableState;
        session.LocalPlayer = session.ObjectTableState.LocalPlayer;
        session.Players = session.ObjectTableState.Players;
        session.ObjectiveActors = session.ObjectTableState.ObjectiveActors;
        session.MapEvents = CollectMapEvents();
        session.FieldMarkers = CollectFieldMarkers();
        session.TargetMarkers = CollectTargetMarkers(session.Players);
        QueueContextDerivedAnalysis(
            now,
            session.IsInFrontline,
            session.MapType,
            session.Knowledge.CurrentMap,
            session.MapEvents,
            session.FieldMarkers,
            session.TargetMarkers,
            session.ObjectiveActors,
            session.Players,
            session.LocalPlayer);
        session.Objectives = cachedContextDerivedAnalysis.Objectives;
        session.MapObjectives = cachedContextDerivedAnalysis.MapObjectives;
        session.PlayerFrameEvents = cachedContextDerivedAnalysis.PlayerFrameEvents;
        session.Stage = WorldRefreshStage.CollectSignals;
    }

    private void AdvanceRefreshSignalStage(PendingWorldRefreshSession session, long now)
    {
        session.MapVisionPoints = CollectMapVisionPoints(session.IsInFrontline, now);
        QueueClusterDerivedAnalysis(now, session.IsInFrontline, session.Players, session.MapVisionPoints, session.LocalPlayer);
        session.PlayerClusters = cachedClusterDerivedAnalysis.PlayerClusters;
        session.MapVisionClusters = cachedClusterDerivedAnalysis.MapVisionClusters;
        session.LocalBattalion = session.LocalPlayer.HasValue ? NormalizeBattalion(session.LocalPlayer.Value.Battalion) : null;
        session.ScoreSituation = BuildScoreSituation(session.ScoreSnapshot, session.Knowledge.CurrentMap, session.MapType, session.LocalBattalion, now);
        session.AnnouncementSituation = announcementReader.GetSnapshot(session.IsInFrontline, session.MapType);
        session.ChatEventSituation = chatEventReader.GetSnapshot(session.IsInFrontline, session.LocalBattalion);
        session.KeySkillLogEvents = keySkillEventReader.GetSnapshot(session.IsInFrontline, session.Knowledge);
        session.TimeSituation = BuildTimeSituation(session.ScoreSnapshot, session.Knowledge.CurrentMap, session.MapObjectives, session.AnnouncementSituation);
        session.LimitBreak = limitBreakService.GetSnapshot(session.IsInFrontline);
        session.Stage = WorldRefreshStage.Assemble;
    }

    private void AdvanceRefreshAssembleStage(PendingWorldRefreshSession session, long now)
    {
        UpdatePlayerHistory(session.Players, session.Knowledge, now);
        PrunePlayerHistory(now);
        ImportKeySkillLogEvents(session.Players, session.KeySkillLogEvents, now);
        PruneKeySkillUseHistory(now);
        QueueTeamDerivedAnalysis(
            now,
            session.IsInFrontline,
            session.Players,
            session.PlayerClusters,
            session.MapVisionPoints,
            session.MapVisionClusters,
            session.LocalPlayer);
        QueueThreatAnalysis(
            now,
            session.IsInFrontline,
            session.Players,
            session.ScoreSituation,
            session.Knowledge,
            session.TimeSituation,
            session.LimitBreak,
            session.KeySkillLogEvents);
        session.NormalizedAlliances = BuildAllianceData(session.ScoreSituation, session.ScoreSnapshot.Alliances);
        session.TeamSituation = BuildTeamSituation(
            session.Players,
            session.PlayerClusters,
            session.MapVisionPoints,
            session.MapVisionClusters,
            session.ScoreSituation,
            cachedLimitBreakThreats,
            cachedKeySkillThreats,
            session.LocalPlayer,
            now);
        session.CanReuseDecisionLayer = latestSnapshot.TerritoryType == session.TerritoryType
            && latestSnapshot.MapId == session.MapId
            && latestSnapshot.IsInFrontline == session.IsInFrontline;
        var decisionIntervalMs = configuration.Performance?.EffectiveDecisionRefreshIntervalMs ?? 4500;
        var hasPendingStrategicDecisionLayer = HasPendingStrategicDecisionRefresh(session.TerritoryType, session.MapId, session.IsInFrontline);
        session.ShouldQueueDecisionLayer = !hasPendingStrategicDecisionLayer
            && (!session.CanReuseDecisionLayer
            || !latestSnapshot.Decision.IsAvailable
            || now - lastStrategicDecisionRefreshTicks >= decisionIntervalMs);
        session.PlayerTracks = session.ShouldQueueDecisionLayer
            ? BuildPlayerTracks(now)
            : session.CanReuseDecisionLayer ? latestSnapshot.PlayerTracks : Array.Empty<BattlefieldPlayerTrackSnapshot>();
        session.EnemyMainGroupTrack = session.ShouldQueueDecisionLayer
            ? BuildEnemyMainGroupTrack(now)
            : session.CanReuseDecisionLayer ? latestSnapshot.EnemyMainGroupTrack : Array.Empty<BattlefieldGroupTrackSnapshot>();
        session.MapTactics = session.CanReuseDecisionLayer ? latestSnapshot.MapTactics : new BattlefieldMapTacticsSnapshot();
        session.Decision = session.CanReuseDecisionLayer ? latestSnapshot.Decision : new BattlefieldDecisionSnapshot();
        session.TeamSituation.AdvancedTactics = session.CanReuseDecisionLayer
            ? latestSnapshot.TeamSituation.AdvancedTactics
            : new BattlefieldAdvancedTacticalSituationSnapshot();
        session.Stage = WorldRefreshStage.Finalize;
    }

    private void FinalizeRefreshSession(PendingWorldRefreshSession session, long now)
    {
        if (string.IsNullOrWhiteSpace(cachedStatusText) || now - lastStatusTextRefreshTicks >= StatusTextRefreshIntervalMs)
        {
            cachedStatusText = BuildStatusText(
                session.NormalizedAlliances,
                session.Players,
                session.MapEvents,
                session.FieldMarkers,
                session.MapVisionPoints,
                session.MapVisionClusters,
                session.TargetMarkers,
                session.Objectives,
                session.MapObjectives,
                session.PlayerClusters,
                session.PlayerTracks,
                session.EnemyMainGroupTrack,
                session.TeamSituation,
                session.ChatEventSituation,
                session.KeySkillLogEvents,
                session.PlayerFrameEvents,
                session.MapTactics,
                session.Decision);
            lastStatusTextRefreshTicks = now;
        }

        latestSnapshot = new BattlefieldSnapshot
        {
            UpdatedAtTicks = now,
            TerritoryType = session.TerritoryType,
            MapId = session.MapId,
            IsInFrontline = session.IsInFrontline,
            IsAreaTransitioning = session.IsAreaTransitioning,
            MatchTimeRemaining = session.ScoreSnapshot.MatchTimeRemaining,
            Alliances = session.NormalizedAlliances,
            LocalPlayer = session.LocalPlayer,
            Players = session.Players,
            MapEvents = session.MapEvents,
            FieldMarkers = session.FieldMarkers,
            MapVisionPoints = session.MapVisionPoints,
            MapVisionClusters = session.MapVisionClusters,
            TargetMarkers = session.TargetMarkers,
            Objectives = session.Objectives,
            MapObjectives = session.MapObjectives,
            PlayerClusters = session.PlayerClusters,
            PlayerTracks = session.PlayerTracks,
            EnemyMainGroupTrack = session.EnemyMainGroupTrack,
            TeamSituation = session.TeamSituation,
            ScoreSituation = session.ScoreSituation,
            TimeSituation = session.TimeSituation,
            LimitBreak = session.LimitBreak,
            AnnouncementSituation = session.AnnouncementSituation,
            ChatEventSituation = session.ChatEventSituation,
            PlayerFrameEvents = session.PlayerFrameEvents,
            MapTactics = session.MapTactics,
            Decision = session.Decision,
            Knowledge = session.Knowledge,
            FriendlyPlayerCount = session.Players.Count(player => player.Relation == BattlefieldPlayerRelation.Friendly),
            EnemyPlayerCount = session.Players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy),
            DeadPlayerCount = session.Players.Count(player => player.IsDead),
            CastingPlayerCount = session.Players.Count(player => player.IsCasting),
            ScoreDebugInfo = session.ScoreSnapshot.DebugInfo,
            StatusText = cachedStatusText
        };
        latestSnapshot = BuildSnapshotWithLlmDecision(latestSnapshot, llmStrategicDecisionService.GetSnapshot(latestSnapshot));

        if (session.ShouldQueueDecisionLayer)
        {
            QueueDecisionRefresh(
                now,
                latestSnapshot,
                session.KeySkillLogEvents,
                session.Knowledge.CurrentMap?.Name ?? session.ScoreSituation.MapName);
        }

        battlefieldReplayRecorder.Record(latestSnapshot);
        activeWorldRefreshSession = null;
        LogSlowRefresh(now, session.StartedTimestamp);
    }

    private void Refresh()
    {
        try
        {
            var now = Environment.TickCount64;
            var scoreSnapshot = scoreReader.GetSnapshot();
            var knowledge = FrontlineKnowledgeBase.GetSnapshot(clientState.TerritoryType, clientState.MapId);
            var mapType = knowledge.CurrentMap?.MapType ?? FrontlineMapType.Unknown;
            var isInFrontline = scoreSnapshot.IsInFrontline || knowledge.CurrentMap != null;
            var topologyChanged = latestSnapshot.TerritoryType != clientState.TerritoryType
                || latestSnapshot.MapId != clientState.MapId
                || latestSnapshot.IsInFrontline != isInFrontline;
            if (topologyChanged)
            {
                ClearMapVisionCache();
                ClearDerivedClusterCache();
                ClearIncrementalObjectScanCache();
                cachedContextDerivedAnalysis = new ContextDerivedAnalysisSnapshot();
                contextDerivedAnalysisTask = null;
                lastContextDerivedAnalysisRequestedTicks = -1;
                cachedClusterDerivedAnalysis = new ClusterDerivedAnalysisSnapshot();
                clusterDerivedAnalysisTask = null;
                lastClusterDerivedAnalysisRequestedTicks = -1;
                cachedTeamDerivedAnalysis = new TeamDerivedAnalysisSnapshot();
                teamDerivedAnalysisTask = null;
                lastTeamDerivedAnalysisRequestedTicks = -1;
                cachedLimitBreakThreats = new BattlefieldLimitBreakThreatSituationSnapshot();
                cachedKeySkillThreats = new BattlefieldKeySkillThreatSituationSnapshot();
                threatAnalysisTask = null;
                lastThreatAnalysisRequestedTicks = -1;
                adaptiveWorldRefreshBackoffUntilTicks = 0;
            }

            if (!isInFrontline && !configuration.Radar.OutsideFrontline)
            {
                lastEnemyMainGroupObservation = null;
                pendingDecisionRefresh = null;
                decisionRefreshTask = null;
                activeDecisionRefresh = null;
                ClearMapVisionCache();
                ClearDerivedClusterCache();
                ClearIncrementalObjectScanCache();
                cachedContextDerivedAnalysis = new ContextDerivedAnalysisSnapshot();
                contextDerivedAnalysisTask = null;
                lastContextDerivedAnalysisRequestedTicks = -1;
                cachedClusterDerivedAnalysis = new ClusterDerivedAnalysisSnapshot();
                clusterDerivedAnalysisTask = null;
                lastClusterDerivedAnalysisRequestedTicks = -1;
                cachedTeamDerivedAnalysis = new TeamDerivedAnalysisSnapshot();
                teamDerivedAnalysisTask = null;
                lastTeamDerivedAnalysisRequestedTicks = -1;
                cachedLimitBreakThreats = new BattlefieldLimitBreakThreatSituationSnapshot();
                cachedKeySkillThreats = new BattlefieldKeySkillThreatSituationSnapshot();
                threatAnalysisTask = null;
                lastThreatAnalysisRequestedTicks = -1;
                cachedStatusText = "非纷争前线区域，低负载待机中。";
                lastStatusTextRefreshTicks = now;
                var standbySnapshot = new BattlefieldSnapshot
                {
                    UpdatedAtTicks = now,
                    TerritoryType = clientState.TerritoryType,
                    MapId = clientState.MapId,
                    IsInFrontline = false,
                    IsAreaTransitioning = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
                    MatchTimeRemaining = scoreSnapshot.MatchTimeRemaining,
                    ScoreSituation = BuildScoreSituation(scoreSnapshot, knowledge.CurrentMap, mapType, null, now),
                    Knowledge = knowledge,
                    StatusText = cachedStatusText
                };
                latestSnapshot = BuildSnapshotWithLlmDecision(standbySnapshot, llmStrategicDecisionService.GetSnapshot(standbySnapshot));
                battlefieldReplayRecorder.Record(latestSnapshot);
                return;
            }

            var battleHighLevelByStatusId = BuildBattleHighStatusIdMap();
            var tacticalStatusIdSets = BuildTacticalStatusIdSets();
            var objectTableState = HasReusableObjectTableSnapshot(mapType, isInFrontline)
                ? cachedObjectTableState
                : CollectPlayersAndObjectiveActors(isInFrontline, mapType, battleHighLevelByStatusId, tacticalStatusIdSets);
            if (!HasReusableObjectTableSnapshot(mapType, isInFrontline))
                CacheObjectTableSnapshot(objectTableState, mapType, isInFrontline, now);

            var localPlayer = objectTableState.LocalPlayer;
            var players = objectTableState.Players;
            var mapEvents = CollectMapEvents();
            var fieldMarkers = CollectFieldMarkers();
            var targetMarkers = CollectTargetMarkers(players);
            var objectiveActors = objectTableState.ObjectiveActors;
            QueueContextDerivedAnalysis(
                now,
                isInFrontline,
                mapType,
                knowledge.CurrentMap,
                mapEvents,
                fieldMarkers,
                targetMarkers,
                objectiveActors,
                players,
                localPlayer);
            var objectives = cachedContextDerivedAnalysis.Objectives;
            var mapObjectives = cachedContextDerivedAnalysis.MapObjectives;
            var mapVisionPoints = CollectMapVisionPoints(isInFrontline, now);
            QueueClusterDerivedAnalysis(now, isInFrontline, players, mapVisionPoints, localPlayer);
            var playerClusters = cachedClusterDerivedAnalysis.PlayerClusters;
            var mapVisionClusters = cachedClusterDerivedAnalysis.MapVisionClusters;
            var localBattalion = localPlayer.HasValue ? NormalizeBattalion(localPlayer.Value.Battalion) : null;
            var scoreSituation = BuildScoreSituation(scoreSnapshot, knowledge.CurrentMap, mapType, localBattalion, now);
            var announcementSituation = announcementReader.GetSnapshot(isInFrontline, mapType);
            var chatEventSituation = chatEventReader.GetSnapshot(isInFrontline, localBattalion);
            var keySkillLogEvents = keySkillEventReader.GetSnapshot(isInFrontline, knowledge);
            var playerFrameEvents = cachedContextDerivedAnalysis.PlayerFrameEvents;
            var timeSituation = BuildTimeSituation(scoreSnapshot, knowledge.CurrentMap, mapObjectives, announcementSituation);
            var limitBreak = limitBreakService.GetSnapshot(isInFrontline);
            UpdatePlayerHistory(players, knowledge, now);
            PrunePlayerHistory(now);
            ImportKeySkillLogEvents(players, keySkillLogEvents, now);
            PruneKeySkillUseHistory(now);
            QueueTeamDerivedAnalysis(
                now,
                isInFrontline,
                players,
                playerClusters,
                mapVisionPoints,
                mapVisionClusters,
                localPlayer);
            QueueThreatAnalysis(
                now,
                isInFrontline,
                players,
                scoreSituation,
                knowledge,
                timeSituation,
                limitBreak,
                keySkillLogEvents);
            var normalizedAlliances = BuildAllianceData(scoreSituation, scoreSnapshot.Alliances);
            var teamSituation = BuildTeamSituation(
                players,
                playerClusters,
                mapVisionPoints,
                mapVisionClusters,
                scoreSituation,
                cachedLimitBreakThreats,
                cachedKeySkillThreats,
                localPlayer,
                now);
            var canReuseDecisionLayer = latestSnapshot.TerritoryType == clientState.TerritoryType
                && latestSnapshot.MapId == clientState.MapId
                && latestSnapshot.IsInFrontline == isInFrontline;
            var decisionIntervalMs = configuration.Performance?.EffectiveDecisionRefreshIntervalMs ?? 4500;
            var hasPendingStrategicDecisionLayer = HasPendingStrategicDecisionRefresh(clientState.TerritoryType, clientState.MapId, isInFrontline);
            var shouldQueueDecisionLayer = !hasPendingStrategicDecisionLayer
                && (!canReuseDecisionLayer
                || !latestSnapshot.Decision.IsAvailable
                || now - lastStrategicDecisionRefreshTicks >= decisionIntervalMs);
            var playerTracks = shouldQueueDecisionLayer
                ? BuildPlayerTracks(now)
                : canReuseDecisionLayer ? latestSnapshot.PlayerTracks : Array.Empty<BattlefieldPlayerTrackSnapshot>();
            var enemyMainGroupTrack = shouldQueueDecisionLayer
                ? BuildEnemyMainGroupTrack(now)
                : canReuseDecisionLayer ? latestSnapshot.EnemyMainGroupTrack : Array.Empty<BattlefieldGroupTrackSnapshot>();
            var mapTactics = canReuseDecisionLayer ? latestSnapshot.MapTactics : new BattlefieldMapTacticsSnapshot();
            var decision = canReuseDecisionLayer ? latestSnapshot.Decision : new BattlefieldDecisionSnapshot();
            teamSituation.AdvancedTactics = canReuseDecisionLayer ? latestSnapshot.TeamSituation.AdvancedTactics : new BattlefieldAdvancedTacticalSituationSnapshot();
            if (string.IsNullOrWhiteSpace(cachedStatusText) || now - lastStatusTextRefreshTicks >= StatusTextRefreshIntervalMs)
            {
                cachedStatusText = BuildStatusText(normalizedAlliances, players, mapEvents, fieldMarkers, mapVisionPoints, mapVisionClusters, targetMarkers, objectives, mapObjectives, playerClusters, playerTracks, enemyMainGroupTrack, teamSituation, chatEventSituation, keySkillLogEvents, playerFrameEvents, mapTactics, decision);
                lastStatusTextRefreshTicks = now;
            }

            latestSnapshot = new BattlefieldSnapshot
            {
                UpdatedAtTicks = now,
                TerritoryType = clientState.TerritoryType,
                MapId = clientState.MapId,
                IsInFrontline = isInFrontline,
                IsAreaTransitioning = condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51],
                MatchTimeRemaining = scoreSnapshot.MatchTimeRemaining,
                Alliances = normalizedAlliances,
                LocalPlayer = localPlayer,
                Players = players,
                MapEvents = mapEvents,
                FieldMarkers = fieldMarkers,
                MapVisionPoints = mapVisionPoints,
                MapVisionClusters = mapVisionClusters,
                TargetMarkers = targetMarkers,
                Objectives = objectives,
                MapObjectives = mapObjectives,
                PlayerClusters = playerClusters,
                PlayerTracks = playerTracks,
                EnemyMainGroupTrack = enemyMainGroupTrack,
                TeamSituation = teamSituation,
                ScoreSituation = scoreSituation,
                TimeSituation = timeSituation,
                LimitBreak = limitBreak,
                AnnouncementSituation = announcementSituation,
                ChatEventSituation = chatEventSituation,
                PlayerFrameEvents = playerFrameEvents,
                MapTactics = mapTactics,
                Decision = decision,
                Knowledge = knowledge,
                FriendlyPlayerCount = players.Count(player => player.Relation == BattlefieldPlayerRelation.Friendly),
                EnemyPlayerCount = players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy),
                DeadPlayerCount = players.Count(player => player.IsDead),
                CastingPlayerCount = players.Count(player => player.IsCasting),
                ScoreDebugInfo = scoreSnapshot.DebugInfo,
                StatusText = cachedStatusText
            };
            latestSnapshot = BuildSnapshotWithLlmDecision(latestSnapshot, llmStrategicDecisionService.GetSnapshot(latestSnapshot));

            if (shouldQueueDecisionLayer)
                QueueDecisionRefresh(
                    now,
                    latestSnapshot,
                    keySkillLogEvents,
                    knowledge.CurrentMap?.Name ?? scoreSituation.MapName);

            battlefieldReplayRecorder.Record(latestSnapshot);
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[WorldState] 战场状态采集失败");
            cachedStatusText = $"战场状态采集失败：{ex.Message}";
            lastStatusTextRefreshTicks = Environment.TickCount64;
            latestSnapshot = new BattlefieldSnapshot
            {
                TerritoryType = clientState.TerritoryType,
                MapId = clientState.MapId,
                StatusText = cachedStatusText
            };
        }
    }

    private void LogSlowRefresh(long now, long startedTimestamp)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        if (elapsedMs < SlowRefreshWarningMs || now - lastSlowRefreshLogTicks < SlowRefreshLogCooldownMs)
            return;

        lastSlowRefreshLogTicks = now;
        var backoffMs = ResolveAdaptiveWorldRefreshBackoffMs(elapsedMs, latestSnapshot.Players.Length);
        if (backoffMs > 0)
            adaptiveWorldRefreshBackoffUntilTicks = Math.Max(adaptiveWorldRefreshBackoffUntilTicks, now + backoffMs);
        log.Debug(
            "[WorldState] Slow refresh: {ElapsedMs:F1}ms, players={PlayerCount}, enemies={EnemyCount}, mapVision={MapVisionCount}, decision={DecisionAvailable}, tactics={TacticsAvailable}",
            elapsedMs,
            latestSnapshot.Players.Length,
            latestSnapshot.EnemyPlayerCount,
            latestSnapshot.MapVisionPoints.Length,
            latestSnapshot.Decision.IsAvailable,
            latestSnapshot.MapTactics.IsAvailable);
    }

    private static long ResolveAdaptiveWorldRefreshBackoffMs(double elapsedMs, int playerCount)
    {
        var backoffMs = elapsedMs switch
        {
            >= 350d => 7000L,
            >= 220d => 5000L,
            >= 120d => 3200L,
            >= 80d => 1800L,
            _ => 0L,
        };

        if (playerCount >= 48)
            backoffMs = Math.Max(backoffMs, 4500L);
        else if (playerCount >= 24)
            backoffMs = Math.Max(backoffMs, 2500L);

        return backoffMs;
    }

    private void UpdateIncrementalObjectScan(long now)
    {
        var knowledge = FrontlineKnowledgeBase.GetSnapshot(clientState.TerritoryType, clientState.MapId);
        var mapType = knowledge.CurrentMap?.MapType ?? FrontlineMapType.Unknown;
        var isInFrontline = latestSnapshot.IsInFrontline || knowledge.CurrentMap != null;
        if (!isInFrontline)
        {
            ClearIncrementalObjectScanCache();
            return;
        }

        if (activeObjectScan != null)
        {
            if (!IsIncrementalObjectScanCurrent(activeObjectScan, mapType, isInFrontline))
            {
                activeObjectScan = null;
            }
            else
            {
                ProcessIncrementalObjectScanBatch(activeObjectScan, now);
                if (activeObjectScan != null)
                    return;
            }
        }

        var refreshIntervalMs = ResolveIncrementalObjectScanIntervalMs();
        if (HasReusableObjectTableSnapshot(mapType, isInFrontline)
            && now - lastObjectScanCompletedTicks < refreshIntervalMs)
            return;

        var battleHighLevelByStatusId = BuildBattleHighStatusIdMap();
        var tacticalStatusIdSets = BuildTacticalStatusIdSets();
        activeObjectScan = BeginIncrementalObjectScanSession(
            isInFrontline,
            mapType,
            battleHighLevelByStatusId,
            tacticalStatusIdSets);
        ProcessIncrementalObjectScanBatch(activeObjectScan, now);
    }

    private long ResolveIncrementalObjectScanIntervalMs()
    {
        var combatRefreshMs = ResolveCombatDecisionRefreshIntervalMs();
        return Math.Clamp(combatRefreshMs, 500L, 5000L);
    }

    private bool HasReusableObjectTableSnapshot(FrontlineMapType mapType, bool isInFrontline)
        => (cachedObjectTableState.LocalPlayer.HasValue
                || cachedObjectTableState.Players.Length > 0
                || cachedObjectTableState.ObjectiveActors.Length > 0)
            && cachedObjectTableTerritoryType == clientState.TerritoryType
            && cachedObjectTableMapId == clientState.MapId
            && cachedObjectTableIsInFrontline == isInFrontline
            && cachedObjectTableMapType == mapType;

    private bool IsIncrementalObjectScanCurrent(
        IncrementalObjectScanSession session,
        FrontlineMapType mapType,
        bool isInFrontline)
        => session.TerritoryType == clientState.TerritoryType
            && session.MapId == clientState.MapId
            && session.IsInFrontline == isInFrontline
            && session.MapType == mapType;

    private IncrementalObjectScanSession BeginIncrementalObjectScanSession(
        bool isInFrontline,
        FrontlineMapType mapType,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        TacticalStatusIdSets tacticalStatusIdSets)
    {
        var localPlayer = objectTable.LocalPlayer;
        var localGameObjectId = localPlayer?.GameObjectId ?? 0;
        var localBattalion = TryGetBattalion(localPlayer, out var battalion) ? battalion : (byte)255;
        var localPosition = localPlayer?.Position ?? Vector3.Zero;
        var hasLocalPosition = localPlayer != null && localPlayer.IsValid();

        return new IncrementalObjectScanSession(
            clientState.TerritoryType,
            clientState.MapId,
            isInFrontline,
            mapType,
            BuildIncrementalObjectScanEntries(mapType),
            localGameObjectId,
            localBattalion,
            localPosition,
            hasLocalPosition,
            battleHighLevelByStatusId,
            tacticalStatusIdSets,
            new List<BattlefieldPlayerSnapshot>(96),
            mapType == FrontlineMapType.Unknown ? null : new List<BattlefieldObjectiveActorSnapshot>(16));
    }

    private void ProcessIncrementalObjectScanBatch(IncrementalObjectScanSession session, long now)
    {
        var batchStart = session.NextIndex;
        var batchEnd = Math.Min(session.ObjectEntries.Length, batchStart + MaxObjectScanEntriesPerFrame);
        if (batchStart >= batchEnd)
            return;
        session.NextIndex = batchEnd;

        for (var i = batchStart; i < batchEnd; i++)
        {
            if (!TryGetTrackedObjectByTableEntry(session.ObjectEntries[i], out var obj) || obj == null)
                continue;

            if (obj is IPlayerCharacter player && player.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
            {
                if (!TryCreatePlayerSnapshot(
                        player,
                        session.LocalGameObjectId,
                        session.LocalBattalion,
                        session.LocalPosition,
                        session.IsInFrontline,
                        session.BattleHighLevelByStatusId,
                        session.TacticalStatusIdSets,
                        out var snapshot))
                    continue;

                session.Players.Add(snapshot);
                if (snapshot.Relation == BattlefieldPlayerRelation.LocalPlayer)
                    session.LocalPlayer = snapshot;
                continue;
            }

            if (session.ObjectiveActors != null
                && TryCreateObjectiveActorSnapshot(
                    session.MapType,
                    obj,
                    session.LocalPosition,
                    session.HasLocalPosition,
                    out var objectiveActor))
            {
                session.ObjectiveActors.Add(objectiveActor);
            }
        }

        if (session.NextIndex < session.ObjectEntries.Length)
            return;

        CacheObjectTableSnapshot(
            new ObjectTableSnapshot(
                session.Players.ToArray(),
                session.ObjectiveActors == null
                    ? Array.Empty<BattlefieldObjectiveActorSnapshot>()
                    : session.ObjectiveActors
                        .OrderBy(actor => actor.Category)
                        .ThenBy(actor => actor.DistanceToLocal)
                        .ToArray(),
                session.LocalPlayer),
            session.MapType,
            session.IsInFrontline,
            now);
        activeObjectScan = null;
    }

    private void CacheObjectTableSnapshot(
        ObjectTableSnapshot snapshot,
        FrontlineMapType mapType,
        bool isInFrontline,
        long now)
    {
        cachedObjectTableState = snapshot;
        cachedObjectTableMapType = mapType;
        cachedObjectTableTerritoryType = clientState.TerritoryType;
        cachedObjectTableMapId = clientState.MapId;
        cachedObjectTableIsInFrontline = isInFrontline;
        lastObjectScanCompletedTicks = now;
    }

    private void ClearIncrementalObjectScanCache()
    {
        activeObjectScan = null;
        cachedObjectTableState = new ObjectTableSnapshot(Array.Empty<BattlefieldPlayerSnapshot>(), Array.Empty<BattlefieldObjectiveActorSnapshot>(), null);
        cachedObjectTableMapType = FrontlineMapType.Unknown;
        cachedObjectTableTerritoryType = 0;
        cachedObjectTableMapId = 0;
        cachedObjectTableIsInFrontline = false;
        lastObjectScanCompletedTicks = 0;
    }

    private TrackedObjectTableEntry[] BuildIncrementalObjectScanEntries(FrontlineMapType mapType)
    {
        var includeObjectiveActors = mapType != FrontlineMapType.Unknown;
        var objectEntries = new List<TrackedObjectTableEntry>(128);
        var seen = new HashSet<ulong>();

        for (var index = 0; index < objectTable.Length; index++)
        {
            var obj = objectTable[index];
            if (!ShouldTrackObjectForWorldStateScan(obj, includeObjectiveActors))
                continue;

            if (seen.Add(obj!.GameObjectId))
                objectEntries.Add(new TrackedObjectTableEntry(index, obj.GameObjectId));
        }

        return objectEntries.ToArray();
    }

    private bool TryGetTrackedObjectByTableEntry(TrackedObjectTableEntry entry, out IGameObject? obj)
    {
        obj = null;
        if (entry.ObjectTableIndex < 0 || entry.ObjectTableIndex >= objectTable.Length)
            return false;

        obj = objectTable[entry.ObjectTableIndex];
        return obj != null
            && obj.IsValid()
            && obj.GameObjectId == entry.GameObjectId;
    }

    private static bool ShouldTrackObjectForWorldStateScan(IGameObject? obj, bool includeObjectiveActors)
    {
        if (obj == null || !obj.IsValid() || !IsValidGameObjectId(obj.GameObjectId))
            return false;

        if (obj is IPlayerCharacter player)
            return player.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc;

        return includeObjectiveActors && obj is ICharacter;
    }

    private static T[] SnapshotArray<T>(IReadOnlyList<T> items)
        => items as T[] ?? items.ToArray();

    private long ResolveCombatDecisionRefreshIntervalMs()
        => configuration.Performance?.EffectiveCombatRefreshIntervalMs ?? 1200;

    private RespawnHistoryEntrySample[] CaptureRespawnHistorySamples()
        => playerHistory.Values
            .Select(entry => new RespawnHistoryEntrySample(
                entry.GameObjectId,
                entry.Relation,
                entry.LastDeathTicks,
                entry.LastReviveTicks,
                entry.DeathStartedTicks))
            .ToArray();

    private bool HasPendingStrategicDecisionRefresh(uint territoryType, uint mapId, bool isInFrontline)
        => pendingDecisionRefresh is { Kind: DecisionRefreshKind.Strategic } pending
            && pending.TerritoryType == territoryType
            && pending.MapId == mapId
            && pending.IsInFrontline == isInFrontline;

    private void TryQueueCombatDecisionRefresh(long now, bool worldRefreshDue)
    {
        if (worldRefreshDue)
            return;

        var snapshot = latestSnapshot;
        if (!snapshot.IsInFrontline
            || snapshot.UpdatedAtTicks <= 0
            || snapshot.TerritoryType != clientState.TerritoryType
            || snapshot.MapId != clientState.MapId)
        {
            return;
        }

        if (decisionRefreshTask != null && activeDecisionRefresh?.Kind == DecisionRefreshKind.Strategic)
            return;

        var combatIntervalMs = ResolveCombatDecisionRefreshIntervalMs();
        if (lastCombatDecisionRefreshTicks > 0 && now - lastCombatDecisionRefreshTicks < combatIntervalMs)
            return;

        if (HasPendingStrategicDecisionRefresh(snapshot.TerritoryType, snapshot.MapId, snapshot.IsInFrontline))
            return;

        var pending = pendingDecisionRefresh;
        if (pending is { Kind: DecisionRefreshKind.Combat }
            && pending.BaseUpdatedAtTicks == snapshot.UpdatedAtTicks
            && now - pending.QueuedAtTicks < Math.Max(300L, combatIntervalMs / 2))
        {
            return;
        }

        var mapType = snapshot.Knowledge.CurrentMap?.MapType ?? FrontlineMapType.Unknown;
        if (!HasReusableObjectTableSnapshot(mapType, snapshot.IsInFrontline))
            return;

        var objectTableState = cachedObjectTableState;
        if (objectTableState.Players.Length == 0 && !objectTableState.LocalPlayer.HasValue)
            return;

        var players = objectTableState.Players;
        var fieldMarkers = CollectFieldMarkers();
        var targetMarkers = CollectTargetMarkers(players);
        var keySkillLogEvents = keySkillEventReader.GetSnapshot(snapshot.IsInFrontline, snapshot.Knowledge);

        UpdatePlayerHistory(players, snapshot.Knowledge, now);
        PrunePlayerHistory(now);
        ImportKeySkillLogEvents(players, keySkillLogEvents, now);
        PruneKeySkillUseHistory(now);
        QueueThreatAnalysis(
            now,
            snapshot.IsInFrontline,
            players,
            snapshot.ScoreSituation,
            snapshot.Knowledge,
            snapshot.TimeSituation,
            snapshot.LimitBreak,
            keySkillLogEvents);

        QueueDecisionRefresh(new PendingDecisionRefresh
        {
            Kind = DecisionRefreshKind.Combat,
            QueuedAtTicks = now,
            BaseUpdatedAtTicks = snapshot.UpdatedAtTicks,
            TerritoryType = snapshot.TerritoryType,
            MapId = snapshot.MapId,
            IsInFrontline = snapshot.IsInFrontline,
            MapName = snapshot.Knowledge.CurrentMap?.Name ?? snapshot.ScoreSituation.MapName,
            LocalPlayer = objectTableState.LocalPlayer,
            Players = players,
            FieldMarkers = fieldMarkers,
            TargetMarkers = targetMarkers,
            MapEvents = snapshot.MapEvents,
            MapVisionPoints = snapshot.MapVisionPoints,
            MapVisionClusters = snapshot.MapVisionClusters,
            Objectives = snapshot.Objectives,
            MapObjectives = snapshot.MapObjectives,
            PlayerClusters = snapshot.PlayerClusters,
            PlayerTracks = snapshot.PlayerTracks,
            EnemyMainGroupTrack = snapshot.EnemyMainGroupTrack,
            TeamSituation = snapshot.TeamSituation,
            ScoreSituation = snapshot.ScoreSituation,
            TimeSituation = snapshot.TimeSituation,
            AnnouncementSituation = snapshot.AnnouncementSituation,
            ChatEventSituation = snapshot.ChatEventSituation,
            PlayerFrameEvents = snapshot.PlayerFrameEvents,
            KeySkillLogEvents = keySkillLogEvents,
            Knowledge = snapshot.Knowledge,
            MapTactics = snapshot.MapTactics,
            LimitBreakThreats = cachedLimitBreakThreats,
            KeySkillThreats = cachedKeySkillThreats,
            RespawnHistory = CaptureRespawnHistorySamples()
        });
    }

    private void QueueContextDerivedAnalysis(
        long now,
        bool isInFrontline,
        FrontlineMapType mapType,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        IReadOnlyList<BattlefieldMapEventSnapshot> mapEvents,
        IReadOnlyList<BattlefieldFieldMarkerSnapshot> fieldMarkers,
        IReadOnlyList<BattlefieldTargetMarkerSnapshot> targetMarkers,
        IReadOnlyList<BattlefieldObjectiveActorSnapshot> objectiveActors,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        if (!isInFrontline)
            return;
        if (contextDerivedAnalysisTask is { IsCompleted: false })
            return;
        if (lastContextDerivedAnalysisRequestedTicks >= 0 && now - lastContextDerivedAnalysisRequestedTicks < ResolveContextDerivedAnalysisIntervalMs())
            return;

        lastContextDerivedAnalysisRequestedTicks = now;
        var request = new ContextDerivedAnalysisRequest(
            clientState.TerritoryType,
            clientState.MapId,
            isInFrontline,
            mapType,
            mapKnowledge,
            SnapshotArray(mapEvents),
            SnapshotArray(fieldMarkers),
            SnapshotArray(targetMarkers),
            SnapshotArray(objectiveActors),
            SnapshotArray(players),
            localPlayer,
            now,
            CaptureObjectiveHistorySnapshots(mapType, mapEvents, objectiveActors, now));
        contextDerivedAnalysisTask = Task.Run(() => BuildContextDerivedAnalysisResult(request));
    }

    private long ResolveContextDerivedAnalysisIntervalMs()
    {
        var worldRefreshMs = configuration.Performance?.EffectiveWorldRefreshIntervalMs ?? DefaultRefreshIntervalMs;
        return Math.Clamp(Math.Max(worldRefreshMs, 1000L), 1000L, 5000L);
    }

    private void ApplyCompletedContextDerivedAnalysis()
    {
        var task = contextDerivedAnalysisTask;
        if (task == null || !task.IsCompleted)
            return;

        contextDerivedAnalysisTask = null;
        ContextDerivedAnalysisResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[WorldState] 后台上下文推导刷新失败");
            return;
        }

        if (result.TerritoryType != clientState.TerritoryType
            || result.MapId != clientState.MapId
            || result.IsInFrontline != latestSnapshot.IsInFrontline)
        {
            return;
        }

        cachedContextDerivedAnalysis = result.Snapshot;
    }

    private ContextDerivedAnalysisResult BuildContextDerivedAnalysisResult(ContextDerivedAnalysisRequest request)
    {
        var focusIndex = BuildObjectiveFocusIndex(request.Players);
        return new ContextDerivedAnalysisResult(
            request.TerritoryType,
            request.MapId,
            request.IsInFrontline,
            new ContextDerivedAnalysisSnapshot
            {
                Objectives = BuildObjectives(request.MapEvents, request.FieldMarkers, request.TargetMarkers, request.Players),
                MapObjectives = BuildMapObjectivesDeferred(
                    request.MapType,
                    request.MapKnowledge,
                    request.MapEvents,
                    request.ObjectiveActors,
                    request.LocalPlayer,
                    request.ObservedAtTicks,
                    request.ObjectiveHistoryById,
                    focusIndex),
                PlayerFrameEvents = BuildPlayerFrameEventSituationFast(request.Players, request.ObservedAtTicks)
            });
    }

    private void QueueThreatAnalysis(
        long now,
        bool isInFrontline,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldScoreSituationSnapshot scoreSituation,
        FrontlineKnowledgeSnapshot knowledge,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldLimitBreakSnapshot localLimitBreak,
        BattlefieldKeySkillLogEventSituationSnapshot keySkillLogEvents)
    {
        if (!isInFrontline)
            return;
        if (threatAnalysisTask is { IsCompleted: false })
            return;
        if (lastThreatAnalysisRequestedTicks >= 0 && now - lastThreatAnalysisRequestedTicks < ResolveThreatAnalysisIntervalMs())
            return;

        lastThreatAnalysisRequestedTicks = now;
        var request = new ThreatAnalysisRequest(
            clientState.TerritoryType,
            clientState.MapId,
            isInFrontline,
            SnapshotArray(players),
            scoreSituation,
            knowledge,
            timeSituation,
            localLimitBreak,
            keySkillLogEvents.IsAvailable,
            keySkillLogEvents.SourceText,
            now,
            playerHistory.Values
                .Where(entry => entry.LastEngagementTicks > 0)
                .ToDictionary(entry => entry.GameObjectId, entry => entry.LastEngagementTicks),
            new Dictionary<(ulong GameObjectId, string SkillName), long>(lastKeySkillUseTicksByPlayerSkill),
            keySkillUseHistory.ToArray());
        threatAnalysisTask = Task.Run(() => BuildThreatAnalysisResult(request));
    }

    private void QueueTeamDerivedAnalysis(
        long now,
        bool isInFrontline,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldPlayerClusterSnapshot> playerClusters,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        if (!isInFrontline)
            return;
        if (teamDerivedAnalysisTask is { IsCompleted: false })
            return;
        if (lastTeamDerivedAnalysisRequestedTicks >= 0 && now - lastTeamDerivedAnalysisRequestedTicks < ResolveTeamDerivedAnalysisIntervalMs())
            return;

        lastTeamDerivedAnalysisRequestedTicks = now;
        var request = new TeamDerivedAnalysisRequest(
            clientState.TerritoryType,
            clientState.MapId,
            isInFrontline,
            SnapshotArray(players),
            SnapshotArray(playerClusters),
            SnapshotArray(mapVisionPoints),
            SnapshotArray(mapVisionClusters),
            localPlayer,
            now,
            CaptureRespawnHistorySamples());
        teamDerivedAnalysisTask = Task.Run(() => BuildTeamDerivedAnalysisResult(request));
    }

    private void QueueClusterDerivedAnalysis(
        long now,
        bool isInFrontline,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        if (!isInFrontline)
            return;
        if (clusterDerivedAnalysisTask is { IsCompleted: false })
            return;
        if (lastClusterDerivedAnalysisRequestedTicks >= 0 && now - lastClusterDerivedAnalysisRequestedTicks < ResolveClusterDerivedAnalysisIntervalMs())
            return;

        lastClusterDerivedAnalysisRequestedTicks = now;
        var request = new ClusterDerivedAnalysisRequest(
            clientState.TerritoryType,
            clientState.MapId,
            isInFrontline,
            SnapshotArray(players),
            SnapshotArray(mapVisionPoints),
            localPlayer);
        clusterDerivedAnalysisTask = Task.Run(() => BuildClusterDerivedAnalysisResult(request));
    }

    private long ResolveClusterDerivedAnalysisIntervalMs()
    {
        var worldRefreshMs = configuration.Performance?.EffectiveWorldRefreshIntervalMs ?? DefaultRefreshIntervalMs;
        return Math.Clamp(Math.Max(worldRefreshMs, 1500L), 1500L, 6000L);
    }

    private void ApplyCompletedClusterDerivedAnalysis()
    {
        var task = clusterDerivedAnalysisTask;
        if (task == null || !task.IsCompleted)
            return;

        clusterDerivedAnalysisTask = null;
        ClusterDerivedAnalysisResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[WorldState] 后台簇分析刷新失败");
            return;
        }

        if (result.TerritoryType != clientState.TerritoryType
            || result.MapId != clientState.MapId
            || result.IsInFrontline != latestSnapshot.IsInFrontline)
        {
            return;
        }

        cachedClusterDerivedAnalysis = result.Snapshot;
    }

    private static ClusterDerivedAnalysisResult BuildClusterDerivedAnalysisResult(ClusterDerivedAnalysisRequest request)
        => new(
            request.TerritoryType,
            request.MapId,
            request.IsInFrontline,
            new ClusterDerivedAnalysisSnapshot
            {
                PlayerClusters = BuildPlayerClusters(request.Players, request.LocalPlayer),
                MapVisionClusters = BuildMapVisionClusters(request.MapVisionPoints, request.LocalPlayer)
            });

    private long ResolveTeamDerivedAnalysisIntervalMs()
    {
        var worldRefreshMs = configuration.Performance?.EffectiveWorldRefreshIntervalMs ?? DefaultRefreshIntervalMs;
        return Math.Clamp(Math.Max(worldRefreshMs, 1500L), 1500L, 8000L);
    }

    private void ApplyCompletedTeamDerivedAnalysis()
    {
        var task = teamDerivedAnalysisTask;
        if (task == null || !task.IsCompleted)
            return;

        teamDerivedAnalysisTask = null;
        TeamDerivedAnalysisResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[WorldState] 后台团队态势派生失败");
            return;
        }

        if (result.TerritoryType != clientState.TerritoryType
            || result.MapId != clientState.MapId
            || result.IsInFrontline != latestSnapshot.IsInFrontline)
        {
            return;
        }

        cachedTeamDerivedAnalysis = result.Snapshot;
    }

    private TeamDerivedAnalysisResult BuildTeamDerivedAnalysisResult(TeamDerivedAnalysisRequest request)
    {
        var enemyClusters = BuildEnemyTacticalClusters(request.Players, request.MapVisionPoints, request.LocalPlayer);
        var enemySplit = IsEnemySplit(enemyClusters);
        return new TeamDerivedAnalysisResult(
            request.TerritoryType,
            request.MapId,
            request.IsInFrontline,
            new TeamDerivedAnalysisSnapshot
            {
                Friendly = BuildTeamSummary(request.Players, request.PlayerClusters, BattlefieldTacticalSide.Friendly),
                Enemy = BuildTeamSummary(request.Players, request.PlayerClusters, BattlefieldTacticalSide.Enemy),
                Unknown = BuildTeamSummary(request.Players, request.PlayerClusters, BattlefieldTacticalSide.Unknown),
                Alliances = BuildAllianceSituations(request.Players, request.PlayerClusters, request.MapVisionPoints, request.MapVisionClusters),
                EnemyClusters = enemyClusters,
                IsEnemySplit = enemySplit,
                EnemySplitSummaryText = BuildEnemySplitSummary(enemyClusters, enemySplit),
                EnemyFocusTargets = BuildFocusTargets(request.Players, IsEnemySource, IsFriendlyTarget),
                FriendlyFocusTargets = BuildFocusTargets(request.Players, IsFriendlySource, IsEnemyTarget),
                RespawnRhythm = BuildRespawnRhythm(request.Players, request.RespawnHistory, request.ObservedAtTicks)
            });
    }

    private long ResolveThreatAnalysisIntervalMs()
    {
        var combatRefreshMs = ResolveCombatDecisionRefreshIntervalMs();
        return Math.Clamp(Math.Max(combatRefreshMs, 900L), 900L, 5000L);
    }

    private void ApplyCompletedThreatAnalysis()
    {
        var task = threatAnalysisTask;
        if (task == null || !task.IsCompleted)
            return;

        threatAnalysisTask = null;
        ThreatAnalysisResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[WorldState] 后台威胁分析刷新失败");
            return;
        }

        if (result.TerritoryType != clientState.TerritoryType
            || result.MapId != clientState.MapId
            || result.IsInFrontline != latestSnapshot.IsInFrontline)
        {
            return;
        }

        cachedLimitBreakThreats = result.LimitBreakThreats;
        cachedKeySkillThreats = result.KeySkillThreats;
    }

    private ThreatAnalysisResult BuildThreatAnalysisResult(ThreatAnalysisRequest request)
        => new(
            request.TerritoryType,
            request.MapId,
            request.IsInFrontline,
            BuildLimitBreakThreatSituation(
                request.Players,
                request.ScoreSituation,
                request.Knowledge,
                request.TimeSituation,
                request.LocalLimitBreak,
                request.ObservedAtTicks,
                request.LastEngagementTicksByPlayerId),
            BuildKeySkillThreatSituation(
                request.Players,
                request.Knowledge,
                request.KeySkillLogEventsAvailable,
                request.KeySkillLogEventSourceText,
                request.ObservedAtTicks,
                request.LastKeySkillUseTicksByPlayerSkill,
                request.KeySkillUseHistory));

    private void QueueDecisionRefresh(
        long now,
        BattlefieldSnapshot baseSnapshot,
        BattlefieldKeySkillLogEventSituationSnapshot keySkillLogEvents,
        string mapName)
    {
        QueueDecisionRefresh(new PendingDecisionRefresh
        {
            Kind = DecisionRefreshKind.Strategic,
            QueuedAtTicks = now,
            BaseUpdatedAtTicks = baseSnapshot.UpdatedAtTicks,
            TerritoryType = baseSnapshot.TerritoryType,
            MapId = baseSnapshot.MapId,
            IsInFrontline = baseSnapshot.IsInFrontline,
            MapName = mapName,
            LocalPlayer = baseSnapshot.LocalPlayer,
            Players = baseSnapshot.Players,
            FieldMarkers = baseSnapshot.FieldMarkers,
            TargetMarkers = baseSnapshot.TargetMarkers,
            MapEvents = baseSnapshot.MapEvents,
            MapVisionPoints = baseSnapshot.MapVisionPoints,
            MapVisionClusters = baseSnapshot.MapVisionClusters,
            Objectives = baseSnapshot.Objectives,
            MapObjectives = baseSnapshot.MapObjectives,
            PlayerClusters = baseSnapshot.PlayerClusters,
            PlayerTracks = baseSnapshot.PlayerTracks,
            EnemyMainGroupTrack = baseSnapshot.EnemyMainGroupTrack,
            TeamSituation = baseSnapshot.TeamSituation,
            ScoreSituation = baseSnapshot.ScoreSituation,
            TimeSituation = baseSnapshot.TimeSituation,
            AnnouncementSituation = baseSnapshot.AnnouncementSituation,
            ChatEventSituation = baseSnapshot.ChatEventSituation,
            PlayerFrameEvents = baseSnapshot.PlayerFrameEvents,
            KeySkillLogEvents = keySkillLogEvents,
            Knowledge = baseSnapshot.Knowledge,
            MapTactics = baseSnapshot.MapTactics,
            LimitBreakThreats = baseSnapshot.TeamSituation.LimitBreakThreats,
            KeySkillThreats = baseSnapshot.TeamSituation.KeySkillThreats,
            RespawnHistory = CaptureRespawnHistorySamples()
        });
    }

    private void QueueDecisionRefresh(PendingDecisionRefresh pending)
    {
        if (pending.Kind == DecisionRefreshKind.Combat
            && pendingDecisionRefresh is { Kind: DecisionRefreshKind.Strategic } strategicPending
            && strategicPending.TerritoryType == pending.TerritoryType
            && strategicPending.MapId == pending.MapId
            && strategicPending.IsInFrontline == pending.IsInFrontline)
        {
            return;
        }

        pendingDecisionRefresh = pending;
    }

    private void StartPendingDecisionRefresh(PendingDecisionRefresh pending)
    {
        if (decisionRefreshTask != null)
            return;

        pendingDecisionRefresh = null;
        activeDecisionRefresh = pending;
        decisionRefreshTask = Task.Run(() => BuildDecisionLayerResult(pending));
    }

    private DecisionLayerResult BuildDecisionLayerResult(PendingDecisionRefresh pending)
    {
        var startedTimestamp = Stopwatch.GetTimestamp();
        var completedAt = Environment.TickCount64;
        try
        {
            BattlefieldMapTacticsSnapshot mapTactics;
            DecisionRefreshStateSnapshot stateSnapshot;
            BattlefieldDecisionSnapshot decision;

            if (pending.Kind == DecisionRefreshKind.Combat)
            {
                var mapVisionClusters = pending.MapVisionClusters.Length > 0
                    ? pending.MapVisionClusters
                    : BuildMapVisionClusters(pending.MapVisionPoints, pending.LocalPlayer);
                var playerClusters = BuildPlayerClusters(pending.Players, pending.LocalPlayer);
                var teamDerived = BuildTeamDerivedAnalysisResult(new TeamDerivedAnalysisRequest(
                    pending.TerritoryType,
                    pending.MapId,
                    pending.IsInFrontline,
                    pending.Players,
                    playerClusters,
                    pending.MapVisionPoints,
                    mapVisionClusters,
                    pending.LocalPlayer,
                    completedAt,
                    pending.RespawnHistory)).Snapshot;
                var playerFrameEvents = BuildPlayerFrameEventSituationFast(pending.Players, completedAt);
                var teamSituation = BuildTeamSituationFromDerived(
                    pending.ScoreSituation,
                    pending.LimitBreakThreats,
                    pending.KeySkillThreats,
                    teamDerived,
                    pending.TeamSituation.EnemyMainGroupMovement);
                teamSituation.AdvancedTactics = BuildAdvancedTacticalSituation(
                    pending.Players,
                    mapVisionClusters,
                    teamSituation,
                    pending.MapTactics,
                    completedAt);
                mapTactics = pending.MapTactics;
                stateSnapshot = new DecisionRefreshStateSnapshot
                {
                    PlayerClusters = playerClusters,
                    PlayerFrameEvents = playerFrameEvents,
                    TeamSituation = teamSituation
                };
                decision = tacticalDecisionEngineService.Analyze(
                    pending.LocalPlayer,
                    pending.MapObjectives,
                    pending.FieldMarkers,
                    pending.TargetMarkers,
                    teamSituation,
                    pending.ScoreSituation,
                    pending.TimeSituation,
                    pending.AnnouncementSituation,
                    mapTactics,
                    pending.ChatEventSituation,
                    playerFrameEvents,
                    pending.Knowledge,
                    Array.Empty<BattlefieldCommandEffectivenessSnapshot>());
            }
            else
            {
                mapTactics = mapTacticalAnalysisService.Analyze(
                    pending.TerritoryType,
                    pending.MapId,
                    pending.MapName,
                    pending.LocalPlayer,
                    pending.Players,
                    pending.PlayerTracks,
                    pending.MapVisionClusters,
                    pending.MapObjectives,
                    pending.TeamSituation,
                    pending.ChatEventSituation,
                    completedAt);
                pending.TeamSituation.AdvancedTactics = BuildAdvancedTacticalSituation(
                    pending.Players,
                    pending.MapVisionClusters,
                    pending.TeamSituation,
                    mapTactics,
                    completedAt);
                stateSnapshot = new DecisionRefreshStateSnapshot
                {
                    PlayerClusters = pending.PlayerClusters,
                    PlayerFrameEvents = pending.PlayerFrameEvents,
                    TeamSituation = pending.TeamSituation
                };
                decision = tacticalDecisionEngineService.Analyze(
                    pending.LocalPlayer,
                    pending.MapObjectives,
                    pending.FieldMarkers,
                    pending.TargetMarkers,
                    pending.TeamSituation,
                    pending.ScoreSituation,
                    pending.TimeSituation,
                    pending.AnnouncementSituation,
                    mapTactics,
                    pending.ChatEventSituation,
                    pending.PlayerFrameEvents,
                    pending.Knowledge,
                    configuration.Performance?.EnableDecisionQualityFeedback == true
                        ? battlefieldReplayRecorder.GetCommandEffectivenessSnapshots()
                        : Array.Empty<BattlefieldCommandEffectivenessSnapshot>());
            }

            return new DecisionLayerResult(
                pending,
                mapTactics,
                decision,
                completedAt,
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                null,
                stateSnapshot);
        }
        catch (Exception ex)
        {
            return new DecisionLayerResult(
                pending,
                new BattlefieldMapTacticsSnapshot(),
                new BattlefieldDecisionSnapshot(),
                completedAt,
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds,
                ex,
                new DecisionRefreshStateSnapshot());
        }
    }

    private void ApplyCompletedDecisionRefresh(long now)
    {
        var task = decisionRefreshTask;
        if (task == null || !task.IsCompleted)
            return;

        decisionRefreshTask = null;
        activeDecisionRefresh = null;
        DecisionLayerResult result;
        try
        {
            result = task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log.Debug(ex, "[WorldState] 后台决策层刷新失败");
            return;
        }

        LogSlowDecisionRefresh(now, result.ElapsedMs);
        if (result.Exception != null)
        {
            log.Debug(result.Exception, "[WorldState] 后台决策层刷新失败");
            return;
        }

        if (!IsPendingDecisionStillCurrent(result.Pending))
            return;

        if (result.Pending.Kind == DecisionRefreshKind.Combat)
        {
            lastCombatDecisionRefreshTicks = now;
            latestSnapshot = BuildSnapshotWithCombatDecisionLayer(latestSnapshot, result.Pending, result.StateSnapshot, result.Decision);
            return;
        }

        lastStrategicDecisionRefreshTicks = now;
        lastCombatDecisionRefreshTicks = now;
        var snapshotWithDecision = BuildSnapshotWithDecisionLayer(now, latestSnapshot, result.Pending, result.StateSnapshot, result.MapTactics, result.Decision);
        latestSnapshot = BuildSnapshotWithLlmDecision(snapshotWithDecision, llmStrategicDecisionService.EvaluateAndMaybeRequest(snapshotWithDecision));
        battlefieldReplayRecorder.Record(latestSnapshot);
    }

    private bool IsPendingDecisionStillCurrent(PendingDecisionRefresh pending)
        => latestSnapshot.UpdatedAtTicks == pending.BaseUpdatedAtTicks
            && latestSnapshot.TerritoryType == pending.TerritoryType
            && latestSnapshot.MapId == pending.MapId
            && latestSnapshot.IsInFrontline == pending.IsInFrontline;

    private BattlefieldSnapshot BuildSnapshotWithDecisionLayer(
        long now,
        BattlefieldSnapshot snapshot,
        PendingDecisionRefresh pending,
        DecisionRefreshStateSnapshot stateSnapshot,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldDecisionSnapshot decision)
        => new()
        {
            UpdatedAtTicks = now,
            TerritoryType = snapshot.TerritoryType,
            MapId = snapshot.MapId,
            IsInFrontline = snapshot.IsInFrontline,
            IsAreaTransitioning = snapshot.IsAreaTransitioning,
            MatchTimeRemaining = snapshot.MatchTimeRemaining,
            Alliances = snapshot.Alliances,
            LocalPlayer = snapshot.LocalPlayer,
            Players = snapshot.Players,
            MapEvents = snapshot.MapEvents,
            FieldMarkers = snapshot.FieldMarkers,
            MapVisionPoints = snapshot.MapVisionPoints,
            MapVisionClusters = snapshot.MapVisionClusters,
            TargetMarkers = snapshot.TargetMarkers,
            Objectives = snapshot.Objectives,
            MapObjectives = snapshot.MapObjectives,
            PlayerClusters = stateSnapshot.PlayerClusters,
            PlayerTracks = pending.PlayerTracks,
            EnemyMainGroupTrack = pending.EnemyMainGroupTrack,
            TeamSituation = stateSnapshot.TeamSituation,
            ScoreSituation = snapshot.ScoreSituation,
            TimeSituation = snapshot.TimeSituation,
            LimitBreak = snapshot.LimitBreak,
            AnnouncementSituation = snapshot.AnnouncementSituation,
            ChatEventSituation = snapshot.ChatEventSituation,
            PlayerFrameEvents = stateSnapshot.PlayerFrameEvents,
            MapTactics = mapTactics,
            Decision = decision,
            LlmStrategicDecision = snapshot.LlmStrategicDecision,
            Knowledge = snapshot.Knowledge,
            FriendlyPlayerCount = snapshot.Players.Count(player => player.Relation == BattlefieldPlayerRelation.Friendly),
            EnemyPlayerCount = snapshot.Players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy),
            DeadPlayerCount = snapshot.Players.Count(player => player.IsDead),
            CastingPlayerCount = snapshot.Players.Count(player => player.IsCasting),
            ScoreDebugInfo = snapshot.ScoreDebugInfo,
            StatusText = cachedStatusText
        };

    private BattlefieldSnapshot BuildSnapshotWithCombatDecisionLayer(
        BattlefieldSnapshot snapshot,
        PendingDecisionRefresh pending,
        DecisionRefreshStateSnapshot stateSnapshot,
        BattlefieldDecisionSnapshot decision)
        => new()
        {
            UpdatedAtTicks = snapshot.UpdatedAtTicks,
            TerritoryType = snapshot.TerritoryType,
            MapId = snapshot.MapId,
            IsInFrontline = snapshot.IsInFrontline,
            IsAreaTransitioning = snapshot.IsAreaTransitioning,
            MatchTimeRemaining = snapshot.MatchTimeRemaining,
            Alliances = snapshot.Alliances,
            LocalPlayer = pending.LocalPlayer,
            Players = pending.Players,
            MapEvents = snapshot.MapEvents,
            FieldMarkers = pending.FieldMarkers,
            MapVisionPoints = snapshot.MapVisionPoints,
            MapVisionClusters = snapshot.MapVisionClusters,
            TargetMarkers = pending.TargetMarkers,
            Objectives = snapshot.Objectives,
            MapObjectives = snapshot.MapObjectives,
            PlayerClusters = stateSnapshot.PlayerClusters,
            PlayerTracks = snapshot.PlayerTracks,
            EnemyMainGroupTrack = snapshot.EnemyMainGroupTrack,
            TeamSituation = stateSnapshot.TeamSituation,
            ScoreSituation = snapshot.ScoreSituation,
            TimeSituation = snapshot.TimeSituation,
            LimitBreak = snapshot.LimitBreak,
            AnnouncementSituation = snapshot.AnnouncementSituation,
            ChatEventSituation = snapshot.ChatEventSituation,
            PlayerFrameEvents = stateSnapshot.PlayerFrameEvents,
            MapTactics = snapshot.MapTactics,
            Decision = decision,
            LlmStrategicDecision = snapshot.LlmStrategicDecision,
            Knowledge = snapshot.Knowledge,
            FriendlyPlayerCount = pending.Players.Count(player => player.Relation == BattlefieldPlayerRelation.Friendly),
            EnemyPlayerCount = pending.Players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy),
            DeadPlayerCount = pending.Players.Count(player => player.IsDead),
            CastingPlayerCount = pending.Players.Count(player => player.IsCasting),
            ScoreDebugInfo = snapshot.ScoreDebugInfo,
            StatusText = snapshot.StatusText
        };

    private static BattlefieldSnapshot BuildSnapshotWithLlmDecision(
        BattlefieldSnapshot snapshot,
        BattlefieldLlmStrategicDecisionSnapshot llmDecision)
        => new()
        {
            UpdatedAtTicks = snapshot.UpdatedAtTicks,
            TerritoryType = snapshot.TerritoryType,
            MapId = snapshot.MapId,
            IsInFrontline = snapshot.IsInFrontline,
            IsAreaTransitioning = snapshot.IsAreaTransitioning,
            MatchTimeRemaining = snapshot.MatchTimeRemaining,
            Alliances = snapshot.Alliances,
            LocalPlayer = snapshot.LocalPlayer,
            Players = snapshot.Players,
            MapEvents = snapshot.MapEvents,
            FieldMarkers = snapshot.FieldMarkers,
            MapVisionPoints = snapshot.MapVisionPoints,
            MapVisionClusters = snapshot.MapVisionClusters,
            TargetMarkers = snapshot.TargetMarkers,
            Objectives = snapshot.Objectives,
            MapObjectives = snapshot.MapObjectives,
            PlayerClusters = snapshot.PlayerClusters,
            PlayerTracks = snapshot.PlayerTracks,
            EnemyMainGroupTrack = snapshot.EnemyMainGroupTrack,
            TeamSituation = snapshot.TeamSituation,
            ScoreSituation = snapshot.ScoreSituation,
            TimeSituation = snapshot.TimeSituation,
            LimitBreak = snapshot.LimitBreak,
            AnnouncementSituation = snapshot.AnnouncementSituation,
            ChatEventSituation = snapshot.ChatEventSituation,
            PlayerFrameEvents = snapshot.PlayerFrameEvents,
            MapTactics = snapshot.MapTactics,
            Decision = snapshot.Decision,
            LlmStrategicDecision = llmDecision,
            Knowledge = snapshot.Knowledge,
            FriendlyPlayerCount = snapshot.FriendlyPlayerCount,
            EnemyPlayerCount = snapshot.EnemyPlayerCount,
            DeadPlayerCount = snapshot.DeadPlayerCount,
            CastingPlayerCount = snapshot.CastingPlayerCount,
            ScoreDebugInfo = snapshot.ScoreDebugInfo,
            StatusText = snapshot.StatusText
        };

    private void LogSlowDecisionRefresh(long now, double elapsedMs)
    {
        if (elapsedMs < SlowDecisionRefreshWarningMs || now - lastSlowDecisionRefreshLogTicks < SlowRefreshLogCooldownMs)
            return;

        lastSlowDecisionRefreshLogTicks = now;
        log.Debug(
            "[WorldState] Slow deferred decision refresh: {ElapsedMs:F1}ms, players={PlayerCount}, enemies={EnemyCount}, mapVision={MapVisionCount}, command={CommandAvailable}",
            elapsedMs,
            latestSnapshot.Players.Length,
            latestSnapshot.EnemyPlayerCount,
            latestSnapshot.MapVisionPoints.Length,
            latestSnapshot.Decision.CommandSituation.IsAvailable);
    }

    private unsafe BattlefieldPlayerSnapshot[] CollectPlayers(
        bool isFrontline,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        TacticalStatusIdSets tacticalStatusIdSets,
        out BattlefieldPlayerSnapshot? localPlayerSnapshot)
    {
        var players = new List<BattlefieldPlayerSnapshot>(96);
        localPlayerSnapshot = null;
        var localPlayer = objectTable.LocalPlayer;
        var localGameObjectId = localPlayer?.GameObjectId ?? 0;
        var localBattalion = TryGetBattalion(localPlayer, out var battalion) ? battalion : (byte)255;
        var localPosition = localPlayer?.Position ?? Vector3.Zero;

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player || player.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
                continue;

            if (!TryCreatePlayerSnapshot(player, localGameObjectId, localBattalion, localPosition, isFrontline, battleHighLevelByStatusId, tacticalStatusIdSets, out var snapshot))
                continue;

            players.Add(snapshot);
            if (snapshot.Relation == BattlefieldPlayerRelation.LocalPlayer)
                localPlayerSnapshot = snapshot;
        }

        return players.ToArray();
    }

    private unsafe ObjectTableSnapshot CollectPlayersAndObjectiveActors(
        bool isFrontline,
        FrontlineMapType mapType,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        TacticalStatusIdSets tacticalStatusIdSets)
    {
        var players = new List<BattlefieldPlayerSnapshot>(96);
        var objectiveActors = mapType == FrontlineMapType.Unknown ? null : new List<BattlefieldObjectiveActorSnapshot>(16);
        BattlefieldPlayerSnapshot? localPlayerSnapshot = null;
        var localPlayer = objectTable.LocalPlayer;
        var localGameObjectId = localPlayer?.GameObjectId ?? 0;
        var localBattalion = TryGetBattalion(localPlayer, out var battalion) ? battalion : (byte)255;
        var localPosition = localPlayer?.Position ?? Vector3.Zero;
        var hasLocalPosition = localPlayer != null && localPlayer.IsValid();

        foreach (var obj in objectTable)
        {
            if (!ShouldTrackObjectForWorldStateScan(obj, objectiveActors != null))
                continue;

            if (obj is IPlayerCharacter player && player.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Pc)
            {
                if (!TryCreatePlayerSnapshot(player, localGameObjectId, localBattalion, localPosition, isFrontline, battleHighLevelByStatusId, tacticalStatusIdSets, out var snapshot))
                    continue;

                players.Add(snapshot);
                if (snapshot.Relation == BattlefieldPlayerRelation.LocalPlayer)
                    localPlayerSnapshot = snapshot;
                continue;
            }

            if (objectiveActors != null
                && TryCreateObjectiveActorSnapshot(mapType, obj, localPosition, hasLocalPosition, out var objectiveActor))
            {
                objectiveActors.Add(objectiveActor);
            }
        }

        return new ObjectTableSnapshot(
            players.ToArray(),
            objectiveActors == null
                ? Array.Empty<BattlefieldObjectiveActorSnapshot>()
                : objectiveActors.OrderBy(actor => actor.Category).ThenBy(actor => actor.DistanceToLocal).ToArray(),
            localPlayerSnapshot);
    }

    private BattlefieldPlayerFrameEventSituationSnapshot BuildPlayerFrameEventSituation(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        long now)
    {
        var playerByObjectId = players
            .Where(player => player.GameObjectId != 0)
            .GroupBy(player => player.GameObjectId)
            .ToDictionary(group => group.Key, group => group.First());

        var statusEvents = statusEffectTracker.GetRecentStatusChangedEvents(now, 30000)
            .Select(evt => ToStatusFrameEvent(evt, playerByObjectId))
            .ToArray();
        var deathEvents = statusEffectTracker.GetRecentDeathStateChangedEvents(now, 30000)
            .Select(evt => ToDeathFrameEvent(evt, playerByObjectId))
            .ToArray();
        var targetEvents = statusEffectTracker.GetRecentTargetChangedEvents(now, 30000)
            .Select(evt => ToTargetFrameEvent(evt, playerByObjectId))
            .ToArray();

        var gainedStatuses = statusEvents.Where(evt => evt.ChangeKind == PlayerStatusChangeKind.Gained).ToArray();
        var friendlyDeaths = deathEvents.Count(evt => IsFriendlySide(evt.Relation) && evt.IsDead);
        var enemyDeaths = deathEvents.Count(evt => evt.Relation == BattlefieldPlayerRelation.Enemy && evt.IsDead);
        var friendlyRevives = deathEvents.Count(evt => IsFriendlySide(evt.Relation) && !evt.IsDead);
        var enemyRevives = deathEvents.Count(evt => evt.Relation == BattlefieldPlayerRelation.Enemy && !evt.IsDead);
        var friendlyControlled = gainedStatuses.Count(evt => IsFriendlySide(evt.Relation) && evt.TacticalTag == "被控");
        var enemyControlled = gainedStatuses.Count(evt => evt.Relation == BattlefieldPlayerRelation.Enemy && evt.TacticalTag == "被控");
        var friendlyDefensive = gainedStatuses.Count(evt => IsFriendlySide(evt.Relation) && IsDefensiveFrameStatusTag(evt.TacticalTag));
        var enemyDefensive = gainedStatuses.Count(evt => evt.Relation == BattlefieldPlayerRelation.Enemy && IsDefensiveFrameStatusTag(evt.TacticalTag));
        var enemyTargetingFriendly = targetEvents.Count(evt =>
            evt.Relation == BattlefieldPlayerRelation.Enemy
            && evt.CurrentTargetRelation is BattlefieldPlayerRelation.Friendly or BattlefieldPlayerRelation.LocalPlayer
            && evt.CurrentTargetObjectId != 0);
        var friendlyTargetingEnemy = targetEvents.Count(evt =>
            IsFriendlySide(evt.Relation)
            && evt.CurrentTargetRelation == BattlefieldPlayerRelation.Enemy
            && evt.CurrentTargetObjectId != 0);

        var summary = statusEvents.Length + deathEvents.Length + targetEvents.Length == 0
            ? "玩家帧事件：30秒没有状态、死亡或目标切换事件"
            : $"玩家帧事件：状态 {statusEvents.Length}，死亡/复活 {deathEvents.Length}，目标切换 {targetEvents.Length}；我方阵亡 {friendlyDeaths}，敌方阵亡 {enemyDeaths}，敌方转火我方 {enemyTargetingFriendly}";

        return new BattlefieldPlayerFrameEventSituationSnapshot
        {
            StatusEvents = statusEvents,
            DeathEvents = deathEvents,
            TargetEvents = targetEvents,
            FriendlyDeathsRecent = friendlyDeaths,
            EnemyDeathsRecent = enemyDeaths,
            FriendlyRevivesRecent = friendlyRevives,
            EnemyRevivesRecent = enemyRevives,
            FriendlyStatusGainedRecent = gainedStatuses.Count(evt => IsFriendlySide(evt.Relation)),
            EnemyStatusGainedRecent = gainedStatuses.Count(evt => evt.Relation == BattlefieldPlayerRelation.Enemy),
            FriendlyControlledRecent = friendlyControlled,
            EnemyControlledRecent = enemyControlled,
            FriendlyDefensiveRecent = friendlyDefensive,
            EnemyDefensiveRecent = enemyDefensive,
            EnemyTargetingFriendlyRecent = enemyTargetingFriendly,
            FriendlyTargetingEnemyRecent = friendlyTargetingEnemy,
            SourceText = "玩家状态/死亡/目标逐帧对比",
            SummaryText = summary
        };
    }

    private BattlefieldPlayerFrameEventSituationSnapshot BuildPlayerFrameEventSituationFast(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        long now)
    {
        var playerByObjectId = new Dictionary<ulong, BattlefieldPlayerSnapshot>(players.Count);
        foreach (var player in players)
        {
            if (player.GameObjectId == 0 || playerByObjectId.ContainsKey(player.GameObjectId))
                continue;

            playerByObjectId[player.GameObjectId] = player;
        }

        var recentStatusEvents = statusEffectTracker.GetRecentStatusChangedEvents(now, 30000);
        var recentDeathEvents = statusEffectTracker.GetRecentDeathStateChangedEvents(now, 30000);
        var recentTargetEvents = statusEffectTracker.GetRecentTargetChangedEvents(now, 30000);
        var statusEvents = new BattlefieldPlayerStatusFrameEventSnapshot[recentStatusEvents.Length];
        var deathEvents = new BattlefieldPlayerDeathFrameEventSnapshot[recentDeathEvents.Length];
        var targetEvents = new BattlefieldPlayerTargetFrameEventSnapshot[recentTargetEvents.Length];
        var friendlyDeaths = 0;
        var enemyDeaths = 0;
        var friendlyRevives = 0;
        var enemyRevives = 0;
        var friendlyStatusGained = 0;
        var enemyStatusGained = 0;
        var friendlyControlled = 0;
        var enemyControlled = 0;
        var friendlyDefensive = 0;
        var enemyDefensive = 0;
        var enemyTargetingFriendly = 0;
        var friendlyTargetingEnemy = 0;

        for (var i = 0; i < recentStatusEvents.Length; i++)
        {
            var evt = ToStatusFrameEvent(recentStatusEvents[i], playerByObjectId);
            statusEvents[i] = evt;
            if (evt.ChangeKind != PlayerStatusChangeKind.Gained)
                continue;

            if (IsFriendlySide(evt.Relation))
            {
                friendlyStatusGained++;
                if (evt.TacticalTag == "被控")
                    friendlyControlled++;
                if (IsDefensiveFrameStatusTag(evt.TacticalTag))
                    friendlyDefensive++;
            }
            else if (evt.Relation == BattlefieldPlayerRelation.Enemy)
            {
                enemyStatusGained++;
                if (evt.TacticalTag == "被控")
                    enemyControlled++;
                if (IsDefensiveFrameStatusTag(evt.TacticalTag))
                    enemyDefensive++;
            }
        }

        for (var i = 0; i < recentDeathEvents.Length; i++)
        {
            var evt = ToDeathFrameEvent(recentDeathEvents[i], playerByObjectId);
            deathEvents[i] = evt;
            if (IsFriendlySide(evt.Relation))
            {
                if (evt.IsDead)
                    friendlyDeaths++;
                else
                    friendlyRevives++;
            }
            else if (evt.Relation == BattlefieldPlayerRelation.Enemy)
            {
                if (evt.IsDead)
                    enemyDeaths++;
                else
                    enemyRevives++;
            }
        }

        for (var i = 0; i < recentTargetEvents.Length; i++)
        {
            var evt = ToTargetFrameEvent(recentTargetEvents[i], playerByObjectId);
            targetEvents[i] = evt;
            if (evt.Relation == BattlefieldPlayerRelation.Enemy
                && evt.CurrentTargetRelation is BattlefieldPlayerRelation.Friendly or BattlefieldPlayerRelation.LocalPlayer
                && evt.CurrentTargetObjectId != 0)
            {
                enemyTargetingFriendly++;
            }

            if (IsFriendlySide(evt.Relation)
                && evt.CurrentTargetRelation == BattlefieldPlayerRelation.Enemy
                && evt.CurrentTargetObjectId != 0)
            {
                friendlyTargetingEnemy++;
            }
        }

        var summary = statusEvents.Length + deathEvents.Length + targetEvents.Length == 0
            ? "玩家帧事件：30秒没有状态、死亡或目标切换事件"
            : $"玩家帧事件：状态 {statusEvents.Length}，死亡/复活 {deathEvents.Length}，目标切换 {targetEvents.Length}；我方阵亡 {friendlyDeaths}，敌方阵亡 {enemyDeaths}，敌方转火我方 {enemyTargetingFriendly}";

        return new BattlefieldPlayerFrameEventSituationSnapshot
        {
            StatusEvents = statusEvents,
            DeathEvents = deathEvents,
            TargetEvents = targetEvents,
            FriendlyDeathsRecent = friendlyDeaths,
            EnemyDeathsRecent = enemyDeaths,
            FriendlyRevivesRecent = friendlyRevives,
            EnemyRevivesRecent = enemyRevives,
            FriendlyStatusGainedRecent = friendlyStatusGained,
            EnemyStatusGainedRecent = enemyStatusGained,
            FriendlyControlledRecent = friendlyControlled,
            EnemyControlledRecent = enemyControlled,
            FriendlyDefensiveRecent = friendlyDefensive,
            EnemyDefensiveRecent = enemyDefensive,
            EnemyTargetingFriendlyRecent = enemyTargetingFriendly,
            FriendlyTargetingEnemyRecent = friendlyTargetingEnemy,
            SourceText = "玩家状态/死亡/目标逐帧对比",
            SummaryText = summary
        };
    }

    private static BattlefieldPlayerStatusFrameEventSnapshot ToStatusFrameEvent(
        PlayerStatusChangedEvent evt,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerByObjectId)
    {
        var relation = ResolveFrameEventRelation(evt.Player.GameObjectId, playerByObjectId);
        return new BattlefieldPlayerStatusFrameEventSnapshot(
            evt.ObservedAtTicks,
            evt.Kind,
            relation,
            evt.Player.GameObjectId,
            evt.Player.Name,
            ResolveFrameEventJobName(evt.Player.GameObjectId, evt.Player.JobName, playerByObjectId),
            evt.Status.StatusId,
            evt.Status.StatusName,
            evt.Status.Param,
            evt.Status.RemainingTime,
            evt.Status.SourceId,
            evt.Status.SourceName,
            ResolveFrameStatusTag(evt.Status),
            evt.SourceText);
    }

    private static BattlefieldPlayerDeathFrameEventSnapshot ToDeathFrameEvent(
        PlayerDeathStateChangedEvent evt,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerByObjectId)
    {
        var relation = ResolveFrameEventRelation(evt.Player.GameObjectId, playerByObjectId);
        return new BattlefieldPlayerDeathFrameEventSnapshot(
            evt.ObservedAtTicks,
            relation,
            evt.Player.GameObjectId,
            evt.Player.Name,
            ResolveFrameEventJobName(evt.Player.GameObjectId, evt.Player.JobName, playerByObjectId),
            evt.IsDead,
            evt.SourceText);
    }

    private static BattlefieldPlayerTargetFrameEventSnapshot ToTargetFrameEvent(
        PlayerTargetChangedEvent evt,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerByObjectId)
    {
        var relation = ResolveFrameEventRelation(evt.Player.GameObjectId, playerByObjectId);
        return new BattlefieldPlayerTargetFrameEventSnapshot(
            evt.ObservedAtTicks,
            evt.Kind,
            relation,
            evt.Player.GameObjectId,
            evt.Player.Name,
            ResolveFrameEventJobName(evt.Player.GameObjectId, evt.Player.JobName, playerByObjectId),
            evt.PreviousTargetObjectId,
            evt.PreviousTargetName,
            ResolveFrameEventRelation(evt.PreviousTargetObjectId, playerByObjectId),
            evt.CurrentTargetObjectId,
            evt.CurrentTargetName,
            ResolveFrameEventRelation(evt.CurrentTargetObjectId, playerByObjectId),
            evt.SourceText);
    }

    private static BattlefieldPlayerRelation ResolveFrameEventRelation(
        ulong gameObjectId,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerByObjectId)
        => gameObjectId != 0 && playerByObjectId.TryGetValue(gameObjectId, out var player)
            ? player.Relation
            : BattlefieldPlayerRelation.Unknown;

    private static string ResolveFrameEventJobName(
        ulong gameObjectId,
        string fallback,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerByObjectId)
    {
        if (gameObjectId != 0 && playerByObjectId.TryGetValue(gameObjectId, out var player))
        {
            var jobName = ResolveJobInfo(player.ClassJobId).Name;
            if (!string.IsNullOrWhiteSpace(jobName))
                return jobName;
        }

        return fallback;
    }

    private static string ResolveFrameStatusTag(ObservedStatusSnapshot status)
    {
        var text = $"{status.StatusName} {status.Param}";
        if (LooksLikeAny(status.StatusName, CrowdControlStatusKeywords))
            return "被控";
        if (LooksLikeAny(status.StatusName, GuardingStatusKeywords))
            return "防御";
        if (LooksLikeAny(text, InvulnerableStatusKeywords))
            return "无敌";
        if (LooksLikeAny(text, ControlImmuneStatusKeywords))
            return "抗控";
        if (LooksLikeAny(text, SnowBlessingStatusKeywords))
            return "雪精祝福";
        if (LooksLikeBattleHighStatus(text))
            return "战意";
        return string.Empty;
    }

    private static bool IsDefensiveFrameStatusTag(string tag)
        => tag is "防御" or "无敌" or "抗控" or "雪精祝福";

    private unsafe bool TryCreatePlayerSnapshot(
        IPlayerCharacter player,
        ulong localGameObjectId,
        byte localBattalion,
        Vector3 localPosition,
        bool isFrontline,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        TacticalStatusIdSets tacticalStatusIdSets,
        out BattlefieldPlayerSnapshot snapshot)
    {
        snapshot = default;
        if (player.Address == IntPtr.Zero || !player.IsValid())
            return false;

        var battleChara = (BattleChara*)player.Address;
        var isLocalPlayer = localGameObjectId != 0 && player.GameObjectId == localGameObjectId;
        var isCasting = false;
        var currentCastTime = 0f;
        var totalCastTime = 0f;
        var castTargetObjectId = 0UL;
        if (isLocalPlayer && player is IBattleChara caster)
        {
            isCasting = SafeReadValue(() => caster.IsCasting, false);
            currentCastTime = SafeReadValue(() => caster.CurrentCastTime, 0f);
            totalCastTime = SafeReadValue(() => caster.TotalCastTime, 0f);
            castTargetObjectId = SafeReadObjectId(() => caster.CastTargetObjectId);
        }

        var currentHp = player.CurrentHp;
        var maxHp = player.MaxHp;
        var hpPercent = maxHp > 0 ? currentHp * 100f / maxHp : 0f;
        var isMounted = ReadIsMounted(battleChara, isLocalPlayer);
        var isInCombat = ReadIsInCombat(battleChara, isLocalPlayer);
        var battleHighLevel = 0;
        var battleHighStatusId = 0u;
        var battleHighRemainingSeconds = 0f;
        var tacticalStatuses = Array.Empty<BattlefieldTacticalStatusSnapshot>();
        var isGuarding = false;
        var isCrowdControlled = false;
        var isControlImmune = false;
        var isControlVulnerable = false;
        var isInvulnerable = false;
        var isExecutable = false;
        var hasSnowBlessing = false;
        var preferredStatuses = GetPreferredStatusesForPlayer(player.GameObjectId);
        ReadBattleHighStatus(preferredStatuses, battleHighLevelByStatusId, out battleHighLevel, out battleHighStatusId, out battleHighRemainingSeconds);
        ReadTacticalStatuses(
            preferredStatuses,
            tacticalStatusIdSets,
            player.IsDead,
            hpPercent,
            out tacticalStatuses,
            out isGuarding,
            out isCrowdControlled,
            out isControlImmune,
            out isControlVulnerable,
            out isInvulnerable,
            out isExecutable,
            out hasSnowBlessing);

        var relation = ResolveRelation(
            player.GameObjectId,
            localGameObjectId,
            battleChara->Battalion,
            localBattalion,
            battleChara->IsPartyMember,
            battleChara->IsAllianceMember,
            battleChara->IsFriend,
            isFrontline);

        snapshot = new BattlefieldPlayerSnapshot(
            player.GameObjectId,
            battleChara->ContentId,
            player.Name.TextValue,
            player.Position,
            player.Rotation,
            localGameObjectId == 0 ? 0f : Vector3.Distance(localPosition, player.Position),
            player.ClassJob.RowId,
            battleChara->Battalion,
            player.IsDead,
            isMounted,
            isInCombat,
            battleChara->IsPartyMember,
            battleChara->IsAllianceMember,
            battleChara->IsFriend,
            isCasting,
            currentCastTime,
            totalCastTime,
            SafeReadObjectId(() => player.TargetObjectId),
            castTargetObjectId,
            currentHp,
            maxHp,
            hpPercent,
            battleHighLevel,
            battleHighLevel >= 5,
            battleHighStatusId,
            battleHighRemainingSeconds,
            tacticalStatuses,
            isGuarding,
            isCrowdControlled,
            isControlImmune,
            isControlVulnerable,
            isInvulnerable,
            isExecutable,
            hasSnowBlessing,
            relation);

        return true;
    }

    private ObservedStatusSnapshot[] GetPreferredStatusesForPlayer(ulong gameObjectId)
        => gameObjectId == 0
            ? Array.Empty<ObservedStatusSnapshot>()
            : statusEffectTracker.GetStatusesForPlayer(gameObjectId);

    private static ulong SafeReadObjectId(Func<ulong> getter)
    {
        try
        {
            var value = getter();
            return IsValidGameObjectId(value) ? value : 0UL;
        }
        catch
        {
            return 0UL;
        }
    }

    private static T SafeReadValue<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch
        {
            return fallback;
        }
    }

    private static void ReadBattleHighStatus(
        IReadOnlyList<ObservedStatusSnapshot> statuses,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        out int level,
        out uint statusId,
        out float remainingSeconds)
    {
        level = 0;
        statusId = 0;
        remainingSeconds = 0f;

        foreach (var status in statuses)
        {
            if (!TryResolveBattleHighStatus(status, battleHighLevelByStatusId, out var candidateLevel, out _, out _, out _))
                continue;

            if (candidateLevel < level)
                continue;

            level = Math.Clamp(candidateLevel, 0, 5);
            statusId = status.StatusId;
            remainingSeconds = Math.Max(0f, status.RemainingTime);
        }
    }

    private static void ReadBattleHighStatus(
        IBattleChara character,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        out int level,
        out uint statusId,
        out float remainingSeconds)
    {
        level = 0;
        statusId = 0;
        remainingSeconds = 0f;

        foreach (var status in character.StatusList)
        {
            if (!TryResolveBattleHighStatus(status, battleHighLevelByStatusId, out var candidateLevel, out _, out _, out _))
                continue;

            if (candidateLevel < level)
                continue;

            level = Math.Clamp(candidateLevel, 0, 5);
            statusId = status.StatusId;
            remainingSeconds = Math.Max(0f, status.RemainingTime);
        }
    }

    public string[] GetBattleHighDebugLines()
    {
        var battleHighLevelByStatusId = BuildBattleHighStatusIdMap();
        var includeAllStatuses = configuration.BattleHigh.ShowAllVisibleStatusesInDebug;
        var lines = new List<string>(48);

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player || !player.IsValid())
                continue;

            var relation = latestSnapshot.Players.FirstOrDefault(snapshot => snapshot.GameObjectId == player.GameObjectId).Relation;
            foreach (var status in GetPreferredStatusesForPlayer(player.GameObjectId))
            {
                if (status.StatusId == 0)
                    continue;

                var isBattleHigh = TryResolveBattleHighStatus(status, battleHighLevelByStatusId, out var level, out var source, out var name, out _);
                if (!isBattleHigh && !includeAllStatuses)
                    continue;

                if (!isBattleHigh)
                    ReadStatusText(status, out name, out _);

                var statusName = string.IsNullOrWhiteSpace(name) ? "无表格名" : name;
                var mark = isBattleHigh ? $" -> 战意{level} [{source}]" : string.Empty;
                lines.Add($"{DebugRelationText(relation)} {player.Name.TextValue}: 编号={status.StatusId} 参数={ReadStatusParam(status)} 剩余={Math.Max(0f, status.RemainingTime):0}秒 名称={statusName}{mark}");

                if (lines.Count >= 40)
                {
                    lines.Add("调试行数已达上限");
                    return lines.ToArray();
                }
            }
        }

        if (lines.Count == 0)
            lines.Add(includeAllStatuses ? "没有可见玩家状态" : "没有战意候选状态");

        return lines.ToArray();
    }

    public string[] GetTacticalStatusDebugLines()
    {
        var tacticalStatusIdSets = BuildTacticalStatusIdSets();
        var includeAllStatuses = configuration.TacticalStatus.ShowAllVisibleStatusesInDebug;
        var lines = new List<string>(64);

        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter player || !player.IsValid())
                continue;

            var relation = latestSnapshot.Players.FirstOrDefault(snapshot => snapshot.GameObjectId == player.GameObjectId).Relation;
            foreach (var status in GetPreferredStatusesForPlayer(player.GameObjectId))
            {
                if (status.StatusId == 0)
                    continue;

                var recognized = TryResolveTacticalStatus(status, tacticalStatusIdSets, out var tacticalStatus);
                if (!recognized && !includeAllStatuses)
                    continue;

                if (recognized)
                {
                    lines.Add($"{DebugRelationText(relation)} {player.Name.TextValue}: {tacticalStatus.Label} 编号={tacticalStatus.StatusId} 参数={tacticalStatus.Param} 剩余={tacticalStatus.RemainingSeconds:0}秒 名称={tacticalStatus.Name} [{tacticalStatus.SourceText}]");
                }
                else
                {
                    ReadStatusText(status, out var name, out _);
                    var statusName = string.IsNullOrWhiteSpace(name) ? "无表格名" : name;
                    lines.Add($"{DebugRelationText(relation)} {player.Name.TextValue}: 编号={status.StatusId} 参数={ReadStatusParam(status)} 剩余={Math.Max(0f, status.RemainingTime):0}秒 名称={statusName}");
                }

                if (lines.Count >= 56)
                {
                    lines.Add("调试行数已达上限");
                    return lines.ToArray();
                }
            }
        }

        if (lines.Count == 0)
            lines.Add(includeAllStatuses ? "没有可见玩家状态" : "没有战术状态候选");

        return lines.ToArray();
    }

    private static void ReadTacticalStatuses(
        IReadOnlyList<ObservedStatusSnapshot> trackedStatuses,
        TacticalStatusIdSets tacticalStatusIdSets,
        bool isDead,
        float hpPercent,
        out BattlefieldTacticalStatusSnapshot[] tacticalStatuses,
        out bool isGuarding,
        out bool isCrowdControlled,
        out bool isControlImmune,
        out bool isControlVulnerable,
        out bool isInvulnerable,
        out bool isExecutable,
        out bool hasSnowBlessing)
    {
        var statuses = new List<BattlefieldTacticalStatusSnapshot>(8);

        foreach (var status in trackedStatuses)
        {
            if (status.StatusId == 0)
                continue;

            if (TryResolveTacticalStatus(status, tacticalStatusIdSets, out var tacticalStatus))
                AddTacticalStatus(statuses, tacticalStatus);
        }

        isGuarding = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.Guarding);
        isCrowdControlled = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.CrowdControlled);
        isControlImmune = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.ControlImmune);
        isInvulnerable = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.Invulnerable);
        hasSnowBlessing = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.SnowBlessing);

        isControlVulnerable = !isDead && !isGuarding && !isCrowdControlled && !isControlImmune && !isInvulnerable;
        if (isControlVulnerable)
            AddTacticalStatus(statuses, CreateDerivedTacticalStatus(BattlefieldTacticalStatusKind.ControlVulnerable, "可控", "无防御、无抗控、无无敌"));

        isExecutable = !isDead && hpPercent > 0f && hpPercent <= 50f && !isGuarding && !isInvulnerable;

        tacticalStatuses = statuses
            .OrderBy(status => TacticalStatusSortKey(status.Kind))
            .ThenBy(status => status.RemainingSeconds <= 0f ? float.MaxValue : status.RemainingSeconds)
            .ToArray();
    }

    private static void ReadTacticalStatuses(
        IBattleChara character,
        TacticalStatusIdSets tacticalStatusIdSets,
        bool isDead,
        float hpPercent,
        out BattlefieldTacticalStatusSnapshot[] tacticalStatuses,
        out bool isGuarding,
        out bool isCrowdControlled,
        out bool isControlImmune,
        out bool isControlVulnerable,
        out bool isInvulnerable,
        out bool isExecutable,
        out bool hasSnowBlessing)
    {
        var statuses = new List<BattlefieldTacticalStatusSnapshot>(8);

        foreach (var status in character.StatusList)
        {
            if (status.StatusId == 0)
                continue;

            if (TryResolveTacticalStatus(status, tacticalStatusIdSets, out var tacticalStatus))
                AddTacticalStatus(statuses, tacticalStatus);
        }

        isGuarding = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.Guarding);
        isCrowdControlled = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.CrowdControlled);
        isControlImmune = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.ControlImmune);
        isInvulnerable = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.Invulnerable);
        hasSnowBlessing = statuses.Any(status => status.Kind == BattlefieldTacticalStatusKind.SnowBlessing);

        isControlVulnerable = !isDead && !isGuarding && !isCrowdControlled && !isControlImmune && !isInvulnerable;
        if (isControlVulnerable)
            AddTacticalStatus(statuses, CreateDerivedTacticalStatus(BattlefieldTacticalStatusKind.ControlVulnerable, "可控", "无防御/无抗性/无无敌"));

        isExecutable = !isDead && hpPercent > 0f && hpPercent <= 50f && !isGuarding && !isInvulnerable;

        tacticalStatuses = statuses
            .OrderBy(status => TacticalStatusSortKey(status.Kind))
            .ThenBy(status => status.RemainingSeconds <= 0f ? float.MaxValue : status.RemainingSeconds)
            .ToArray();
    }

    private static bool TryResolveTacticalStatus(
        ObservedStatusSnapshot status,
        TacticalStatusIdSets tacticalStatusIdSets,
        out BattlefieldTacticalStatusSnapshot tacticalStatus)
    {
        tacticalStatus = default;
        ReadStatusText(status, out var name, out var description);
        var combinedText = $"{name} {description}";
        var param = ReadStatusParam(status);
        var remaining = Math.Max(0f, status.RemainingTime);

        if (tacticalStatusIdSets.Guarding.Contains(status.StatusId) || LooksLikeAny(name, GuardingStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.Guarding, "防御中", name, param, remaining, tacticalStatusIdSets.Guarding.Contains(status.StatusId) ? "status-id" : "status-name");
            return true;
        }

        if (tacticalStatusIdSets.SnowBlessing.Contains(status.StatusId) || LooksLikeAny(combinedText, SnowBlessingStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.SnowBlessing, "雪精祝福", name, param, remaining, tacticalStatusIdSets.SnowBlessing.Contains(status.StatusId) ? "status-id" : "status-text");
            return true;
        }

        if (tacticalStatusIdSets.Invulnerable.Contains(status.StatusId) || LooksLikeAny(combinedText, InvulnerableStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.Invulnerable, "无敌", name, param, remaining, tacticalStatusIdSets.Invulnerable.Contains(status.StatusId) ? "status-id" : "status-text");
            return true;
        }

        if (tacticalStatusIdSets.ControlImmune.Contains(status.StatusId) || LooksLikeAny(combinedText, ControlImmuneStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.ControlImmune, "抗控", name, param, remaining, tacticalStatusIdSets.ControlImmune.Contains(status.StatusId) ? "status-id" : "status-text");
            return true;
        }

        if (tacticalStatusIdSets.CrowdControlled.Contains(status.StatusId) || LooksLikeAny(name, CrowdControlStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.CrowdControlled, "被控", name, param, remaining, tacticalStatusIdSets.CrowdControlled.Contains(status.StatusId) ? "status-id" : "status-name");
            return true;
        }

        return false;
    }

    private static bool TryResolveTacticalStatus(
        IStatus status,
        TacticalStatusIdSets tacticalStatusIdSets,
        out BattlefieldTacticalStatusSnapshot tacticalStatus)
    {
        tacticalStatus = default;
        ReadStatusText(status, out var name, out var description);
        var combinedText = $"{name} {description}";
        var param = ReadStatusParam(status);
        var remaining = Math.Max(0f, status.RemainingTime);

        if (tacticalStatusIdSets.Guarding.Contains(status.StatusId) || LooksLikeAny(name, GuardingStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.Guarding, "防御中", name, param, remaining, tacticalStatusIdSets.Guarding.Contains(status.StatusId) ? "status-id" : "status-name");
            return true;
        }

        if (tacticalStatusIdSets.SnowBlessing.Contains(status.StatusId) || LooksLikeAny(combinedText, SnowBlessingStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.SnowBlessing, "雪精祝福", name, param, remaining, tacticalStatusIdSets.SnowBlessing.Contains(status.StatusId) ? "status-id" : "status-text");
            return true;
        }

        if (tacticalStatusIdSets.Invulnerable.Contains(status.StatusId) || LooksLikeAny(combinedText, InvulnerableStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.Invulnerable, "无敌", name, param, remaining, tacticalStatusIdSets.Invulnerable.Contains(status.StatusId) ? "status-id" : "status-text");
            return true;
        }

        if (tacticalStatusIdSets.ControlImmune.Contains(status.StatusId) || LooksLikeAny(combinedText, ControlImmuneStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.ControlImmune, "抗控", name, param, remaining, tacticalStatusIdSets.ControlImmune.Contains(status.StatusId) ? "status-id" : "status-text");
            return true;
        }

        if (tacticalStatusIdSets.CrowdControlled.Contains(status.StatusId) || LooksLikeAny(name, CrowdControlStatusKeywords))
        {
            tacticalStatus = CreateStatusBackedTacticalStatus(status.StatusId, BattlefieldTacticalStatusKind.CrowdControlled, "被控", name, param, remaining, tacticalStatusIdSets.CrowdControlled.Contains(status.StatusId) ? "status-id" : "status-name");
            return true;
        }

        return false;
    }

    private TacticalStatusIdSets BuildTacticalStatusIdSets()
    {
        var config = configuration.TacticalStatus;
        var guarding = config.GuardingStatusIds ?? string.Empty;
        var crowdControlled = config.CrowdControlledStatusIds ?? string.Empty;
        var controlImmune = config.ControlImmuneStatusIds ?? string.Empty;
        var invulnerable = config.InvulnerableStatusIds ?? string.Empty;
        var snowBlessing = config.SnowBlessingStatusIds ?? string.Empty;

        if (cachedTacticalStatusIdSets != null
            && string.Equals(cachedGuardingStatusIdsRaw, guarding, StringComparison.Ordinal)
            && string.Equals(cachedCrowdControlledStatusIdsRaw, crowdControlled, StringComparison.Ordinal)
            && string.Equals(cachedControlImmuneStatusIdsRaw, controlImmune, StringComparison.Ordinal)
            && string.Equals(cachedInvulnerableStatusIdsRaw, invulnerable, StringComparison.Ordinal)
            && string.Equals(cachedSnowBlessingStatusIdsRaw, snowBlessing, StringComparison.Ordinal))
        {
            return cachedTacticalStatusIdSets;
        }

        cachedGuardingStatusIdsRaw = guarding;
        cachedCrowdControlledStatusIdsRaw = crowdControlled;
        cachedControlImmuneStatusIdsRaw = controlImmune;
        cachedInvulnerableStatusIdsRaw = invulnerable;
        cachedSnowBlessingStatusIdsRaw = snowBlessing;
        cachedTacticalStatusIdSets = new TacticalStatusIdSets(
            ParseStatusIdSet(config.GuardingStatusIds),
            ParseStatusIdSet(config.CrowdControlledStatusIds),
            ParseStatusIdSet(config.ControlImmuneStatusIds),
            ParseStatusIdSet(config.InvulnerableStatusIds),
            ParseStatusIdSet(config.SnowBlessingStatusIds));
        return cachedTacticalStatusIdSets;
    }

    private static BattlefieldTacticalStatusSnapshot CreateStatusBackedTacticalStatus(
        uint statusId,
        BattlefieldTacticalStatusKind kind,
        string label,
        string name,
        int param,
        float remainingSeconds,
        string sourceText)
        => new(statusId, kind, label, string.IsNullOrWhiteSpace(name) ? $"Status {statusId}" : name, param, remainingSeconds, sourceText);

    private static BattlefieldTacticalStatusSnapshot CreateDerivedTacticalStatus(
        BattlefieldTacticalStatusKind kind,
        string label,
        string sourceText)
        => new(0, kind, label, "derived", 0, 0f, sourceText);

    private static void AddTacticalStatus(
        List<BattlefieldTacticalStatusSnapshot> statuses,
        BattlefieldTacticalStatusSnapshot status)
    {
        if (statuses.Any(existing => existing.Kind == status.Kind && existing.StatusId == status.StatusId))
            return;

        statuses.Add(status);
    }

    private static int TacticalStatusSortKey(BattlefieldTacticalStatusKind kind)
        => kind switch
        {
            BattlefieldTacticalStatusKind.Invulnerable => 0,
            BattlefieldTacticalStatusKind.Guarding => 1,
            BattlefieldTacticalStatusKind.SnowBlessing => 2,
            BattlefieldTacticalStatusKind.CrowdControlled => 3,
            BattlefieldTacticalStatusKind.ExecuteVulnerable => 4,
            BattlefieldTacticalStatusKind.ControlImmune => 5,
            BattlefieldTacticalStatusKind.ControlVulnerable => 6,
            _ => 10,
        };

    private static bool LooksLikeAny(string text, IEnumerable<string> keywords)
        => !string.IsNullOrWhiteSpace(text)
            && keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyDictionary<uint, int> BuildBattleHighStatusIdMap()
    {
        var raw = configuration.BattleHigh.CandidateStatusIds ?? string.Empty;
        if (cachedBattleHighStatusIdMap != null
            && string.Equals(cachedBattleHighStatusIdsRaw, raw, StringComparison.Ordinal))
        {
            return cachedBattleHighStatusIdMap;
        }

        var parsed = ParseBattleHighStatusIdMap(raw);
        cachedBattleHighStatusIdsRaw = raw;
        cachedBattleHighStatusIdMap = parsed.Count > 0 ? parsed : DefaultBattleHighLevelByStatusId;
        return cachedBattleHighStatusIdMap;
    }

    private static Dictionary<uint, int> ParseBattleHighStatusIdMap(string? raw)
    {
        var result = new Dictionary<uint, int>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        var implicitLevel = 1;
        foreach (var rawToken in raw.Split(new[] { ',', ';', '|', ' ', '，', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
                continue;

            if (TryParseStatusIdRange(token, out var rangeStart, out var rangeEnd))
            {
                var start = Math.Min(rangeStart, rangeEnd);
                var end = Math.Max(rangeStart, rangeEnd);
                for (var id = start; id <= end && id - start < 16; id++)
                {
                    result[id] = Math.Clamp(implicitLevel, 1, 5);
                    implicitLevel = Math.Min(implicitLevel + 1, 5);
                }

                continue;
            }

            var level = implicitLevel;
            var idText = token;
            var separatorIndex = token.IndexOf(':');
            if (separatorIndex < 0)
                separatorIndex = token.IndexOf('=');

            if (separatorIndex > 0 && separatorIndex < token.Length - 1)
            {
                idText = token[..separatorIndex].Trim();
                if (int.TryParse(token[(separatorIndex + 1)..].Trim(), out var explicitLevel))
                    level = explicitLevel;
            }

            if (uint.TryParse(idText, out var statusId) && statusId > 0)
            {
                result[statusId] = Math.Clamp(level, 1, 5);
                implicitLevel = Math.Min(implicitLevel + 1, 5);
            }
        }

        return result;
    }

    private static HashSet<uint> ParseStatusIdSet(string? raw)
    {
        var result = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var rawToken in raw.Split(new[] { ',', ';', '|', ' ', '，', '；' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
                continue;

            if (TryParseStatusIdRange(token, out var rangeStart, out var rangeEnd))
            {
                var start = Math.Min(rangeStart, rangeEnd);
                var end = Math.Max(rangeStart, rangeEnd);
                for (var id = start; id <= end && id - start < 64; id++)
                    result.Add(id);

                continue;
            }

            if (uint.TryParse(token, out var statusId) && statusId > 0)
                result.Add(statusId);
        }

        return result;
    }

    private static bool TryParseStatusIdRange(string token, out uint start, out uint end)
    {
        start = 0;
        end = 0;

        var separatorIndex = token.IndexOf('~');
        if (separatorIndex < 0)
            separatorIndex = token.IndexOf('-');

        if (separatorIndex <= 0 || separatorIndex >= token.Length - 1)
            return false;

        return uint.TryParse(token[..separatorIndex].Trim(), out start)
            && uint.TryParse(token[(separatorIndex + 1)..].Trim(), out end)
            && start > 0
            && end > 0;
    }

    private static bool TryResolveBattleHighStatus(
        ObservedStatusSnapshot status,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        out int level,
        out string source,
        out string name,
        out string description)
    {
        ReadStatusText(status, out name, out description);
        level = 0;
        source = string.Empty;

        if (battleHighLevelByStatusId.TryGetValue(status.StatusId, out var configuredLevel))
        {
            level = Math.Clamp(configuredLevel, 1, 5);
            source = "status-id";
            return true;
        }

        var combinedText = $"{name} {description}";
        if (!LooksLikeBattleHighStatus(combinedText))
            return false;

        level = InferBattleHighLevel(name, ReadStatusParam(status));
        if (level <= 0)
            level = InferBattleHighLevel(description, 0);
        if (level <= 0)
            level = 1;

        source = "status-text";
        return true;
    }

    private static bool TryResolveBattleHighStatus(
        IStatus status,
        IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
        out int level,
        out string source,
        out string name,
        out string description)
    {
        level = 0;
        source = string.Empty;
        name = string.Empty;
        description = string.Empty;

        if (battleHighLevelByStatusId.TryGetValue(status.StatusId, out var configuredLevel))
        {
            level = Math.Clamp(configuredLevel, 1, 5);
            source = "status-id";
            return true;
        }

        ReadStatusText(status, out name, out description);
        var combinedText = $"{name} {description}";
        if (!LooksLikeBattleHighStatus(combinedText))
            return false;

        level = InferBattleHighLevel(name, ReadStatusParam(status));
        if (level <= 0)
            level = InferBattleHighLevel(description, 0);
        if (level <= 0)
            level = 1;

        source = "status-text";
        return true;
    }

    private static void ReadStatusText(ObservedStatusSnapshot status, out string name, out string description)
    {
        name = string.IsNullOrWhiteSpace(status.StatusName) ? $"Status {status.StatusId}" : status.StatusName;
        description = StatusTextCache.TryGetValue(status.StatusId, out var cached)
            ? cached.Description
            : string.Empty;
    }

    private static void ReadStatusText(IStatus status, out string name, out string description)
    {
        if (StatusTextCache.TryGetValue(status.StatusId, out var cached))
        {
            name = cached.Name;
            description = cached.Description;
            return;
        }

        name = string.Empty;
        description = string.Empty;

        try
        {
            var gameData = status.GameData;
            name = gameData.Value.Name.ToString();
            description = gameData.Value.Description.ToString();
        }
        catch
        {
            // Some transient or invalid status rows may not resolve through Lumina.
        }

        StatusTextCache[status.StatusId] = (name, description);
    }

    private static bool LooksLikeBattleHighStatus(string text)
        => BattleHighKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static int InferBattleHighLevel(string text, int statusParam)
    {
        if (string.IsNullOrWhiteSpace(text))
            return NormalizeBattleHighLevel(statusParam);

        if (text.Contains("Battle Fever", StringComparison.OrdinalIgnoreCase)
            || text.Contains("狂热", StringComparison.OrdinalIgnoreCase)
            || text.Contains("狂熱", StringComparison.OrdinalIgnoreCase))
            return 5;

        // Additional roman numeral variants are handled by the regex below.








        var romanMatch = BattleHighAsciiRomanRegex.Match(text);
        if (romanMatch.Success)
        {
            return romanMatch.Value.ToUpperInvariant() switch
            {
                "V" => 5,
                "IV" => 4,
                "III" => 3,
                "II" => 2,
                "I" => 1,
                _ => 0
            };
        }

        var digitMatch = BattleHighDigitRegex.Match(text);
        if (digitMatch.Success && int.TryParse(digitMatch.Groups[1].Value, out var digitLevel))
            return digitLevel;

        return NormalizeBattleHighLevel(statusParam);
    }

    private static int NormalizeBattleHighLevel(int level)
        => level is >= 1 and <= 5 ? level : 0;

    private static int ReadStatusParam(IStatus status)
    {
        try
        {
            return Convert.ToInt32(status.Param);
        }
        catch
        {
            return 0;
        }
    }

    private static int ReadStatusParam(ObservedStatusSnapshot status)
        => Convert.ToInt32(status.Param);

    private static string DebugRelationText(BattlefieldPlayerRelation relation)
        => relation switch
        {
            BattlefieldPlayerRelation.LocalPlayer => "self",
            BattlefieldPlayerRelation.Friendly => "ally",
            BattlefieldPlayerRelation.Enemy => "enemy",
            _ => "unknown",
        };

    private unsafe bool ReadIsMounted(BattleChara* battleChara, bool isLocalPlayer)
    {
        var localMounted = isLocalPlayer
            && (condition[ConditionFlag.Mounted] || condition[ConditionFlag.RidingPillion] || condition[ConditionFlag.Mounting]);

        try
        {
            return battleChara->IsMounted() || localMounted;
        }
        catch
        {
            return localMounted;
        }
    }

    private unsafe bool ReadIsInCombat(BattleChara* battleChara, bool isLocalPlayer)
    {
        var localInCombat = isLocalPlayer && condition[ConditionFlag.InCombat];
        try
        {
            return battleChara->InCombat || localInCombat;
        }
        catch
        {
            return localInCombat;
        }
    }

    private static BattlefieldPlayerRelation ResolveRelation(
        ulong gameObjectId,
        ulong localGameObjectId,
        byte battalion,
        byte localBattalion,
        bool isPartyMember,
        bool isAllianceMember,
        bool isFriend,
        bool isFrontline)
    {
        if (gameObjectId == localGameObjectId)
            return BattlefieldPlayerRelation.LocalPlayer;

        if (isPartyMember || isAllianceMember || isFriend)
            return BattlefieldPlayerRelation.Friendly;

        if (isFrontline && IsFrontlineBattalion(localBattalion) && IsFrontlineBattalion(battalion))
            return battalion == localBattalion ? BattlefieldPlayerRelation.Friendly : BattlefieldPlayerRelation.Enemy;

        return BattlefieldPlayerRelation.Unknown;
    }

    private static bool IsFrontlineBattalion(byte battalion)
        => battalion <= 2;

    private static unsafe bool TryGetBattalion(IPlayerCharacter? player, out byte battalion)
    {
        battalion = 255;
        if (player == null || player.Address == IntPtr.Zero || !player.IsValid())
            return false;

        battalion = ((BattleChara*)player.Address)->Battalion;
        return true;
    }

    private unsafe BattlefieldMapEventSnapshot[] CollectMapEvents()
    {
        var agentMap = AgentMap.Instance();
        if (agentMap == null)
            return Array.Empty<BattlefieldMapEventSnapshot>();

        var events = new List<BattlefieldMapEventSnapshot>(32);
        foreach (ref readonly var marker in agentMap->EventMarkers.AsSpan())
        {
            var kind = GetMapEventKind(marker.IconId);
            if (kind == BattlefieldMapEventKind.Unknown)
                continue;

            var tooltip = ReadTooltip(marker);
            events.Add(new BattlefieldMapEventSnapshot(
                marker.IconId,
                kind,
                marker.Position,
                tooltip,
                TryParseCountdownSeconds(tooltip, out var seconds) ? seconds : null,
                TryParseHpPercent(tooltip, out var hpPercent) ? hpPercent : null,
                TryGetControlPointScore(marker.IconId, out var score) ? score : null));
        }

        return events.ToArray();
    }

    private BattlefieldMapVisionPointSnapshot[] CollectMapVisionPoints(bool isFrontline, long now)
    {
        if (!isFrontline)
        {
            ClearMapVisionCache();
            return Array.Empty<BattlefieldMapVisionPointSnapshot>();
        }

        var intervalMs = configuration.Performance?.EffectiveAreaMapSampleIntervalMs ?? 500;
        if (cachedMapVisionTerritoryType == clientState.TerritoryType
            && cachedMapVisionMapId == clientState.MapId
            && now - lastMapVisionRefreshTicks < intervalMs)
            return cachedMapVisionPoints;

        if (!areaMapProjectionService.TryGetSnapshot(out var mapSnapshot) || mapSnapshot.MapVisionPoints.Length == 0)
        {
            cachedMapVisionTerritoryType = clientState.TerritoryType;
            cachedMapVisionMapId = clientState.MapId;
            cachedMapVisionPoints = Array.Empty<BattlefieldMapVisionPointSnapshot>();
            lastMapVisionRefreshTicks = now;
            return Array.Empty<BattlefieldMapVisionPointSnapshot>();
        }

        cachedMapVisionTerritoryType = clientState.TerritoryType;
        cachedMapVisionMapId = clientState.MapId;
        cachedMapVisionPoints = mapSnapshot.MapVisionPoints
            .Where(point => point.Relation is BattlefieldPlayerRelation.Friendly or BattlefieldPlayerRelation.Enemy)
            .ToArray();
        lastMapVisionRefreshTicks = now;
        return cachedMapVisionPoints;
    }

    private void ClearMapVisionCache()
    {
        cachedMapVisionTerritoryType = 0;
        cachedMapVisionMapId = 0;
        cachedMapVisionPoints = Array.Empty<BattlefieldMapVisionPointSnapshot>();
        lastMapVisionRefreshTicks = 0;
    }

    private void ClearDerivedClusterCache()
    {
        cachedPlayerClusterSignature = int.MinValue;
        cachedPlayerClusters = Array.Empty<BattlefieldPlayerClusterSnapshot>();
        lastPlayerClusterBuildTicks = 0;
        cachedMapVisionClusterSignature = int.MinValue;
        cachedMapVisionClusters = Array.Empty<BattlefieldMapVisionClusterSnapshot>();
        lastMapVisionClusterBuildTicks = 0;
        cachedEnemyClusterSignature = int.MinValue;
        cachedEnemyClusters = Array.Empty<BattlefieldEnemyClusterSnapshot>();
        lastEnemyClusterBuildTicks = 0;
        cachedPlayerTracks = Array.Empty<BattlefieldPlayerTrackSnapshot>();
        lastPlayerTrackBuildTicks = 0;
        cachedEnemyMainGroupTrack = Array.Empty<BattlefieldGroupTrackSnapshot>();
        lastEnemyMainGroupTrackBuildTicks = 0;
        enemyClusterHistory.Clear();
        lastEnemyMovementSource = string.Empty;
        lastEnemyMainGroupObservation = null;
    }

    private BattlefieldPlayerClusterSnapshot[] GetPlayerClusters(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldPlayerSnapshot? localPlayer,
        long now)
    {
        var signature = ComputePlayerClusterSignature(players, localPlayer);
        if (signature == cachedPlayerClusterSignature
            && now - lastPlayerClusterBuildTicks < DerivedClusterRefreshIntervalMs)
            return cachedPlayerClusters;

        if (cachedPlayerClusters.Length > 0 && now - lastPlayerClusterBuildTicks < DerivedClusterRefreshIntervalMs)
            return cachedPlayerClusters;

        cachedPlayerClusterSignature = signature;
        cachedPlayerClusters = BuildPlayerClusters(players, localPlayer);
        lastPlayerClusterBuildTicks = now;
        return cachedPlayerClusters;
    }

    private BattlefieldMapVisionClusterSnapshot[] GetMapVisionClusters(
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        BattlefieldPlayerSnapshot? localPlayer,
        long now)
    {
        var signature = ComputeMapVisionClusterSignature(mapVisionPoints, localPlayer);
        if (signature == cachedMapVisionClusterSignature
            && now - lastMapVisionClusterBuildTicks < DerivedClusterRefreshIntervalMs)
            return cachedMapVisionClusters;

        if (cachedMapVisionClusters.Length > 0 && now - lastMapVisionClusterBuildTicks < DerivedClusterRefreshIntervalMs)
            return cachedMapVisionClusters;

        cachedMapVisionClusterSignature = signature;
        cachedMapVisionClusters = BuildMapVisionClusters(mapVisionPoints, localPlayer);
        lastMapVisionClusterBuildTicks = now;
        return cachedMapVisionClusters;
    }

    private BattlefieldEnemyClusterSnapshot[] GetEnemyTacticalClusters(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        BattlefieldPlayerSnapshot? localPlayer,
        long now)
    {
        var signature = ComputeEnemyClusterSignature(players, mapVisionPoints, localPlayer);
        if (signature == cachedEnemyClusterSignature
            && now - lastEnemyClusterBuildTicks < DerivedClusterRefreshIntervalMs)
            return cachedEnemyClusters;

        if (cachedEnemyClusters.Length > 0 && now - lastEnemyClusterBuildTicks < DerivedClusterRefreshIntervalMs)
            return cachedEnemyClusters;

        cachedEnemyClusterSignature = signature;
        cachedEnemyClusters = BuildEnemyTacticalClusters(players, mapVisionPoints, localPlayer);
        lastEnemyClusterBuildTicks = now;
        return cachedEnemyClusters;
    }

    private static int ComputePlayerClusterSignature(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var hash = new HashCode();
        hash.Add(players.Count);
        if (localPlayer.HasValue)
        {
            hash.Add(localPlayer.Value.GameObjectId);
            hash.Add(QuantizeClusterCoordinate(localPlayer.Value.Position.X));
            hash.Add(QuantizeClusterCoordinate(localPlayer.Value.Position.Z));
        }

        foreach (var player in players)
        {
            if (player.Relation != BattlefieldPlayerRelation.Friendly
                && player.Relation != BattlefieldPlayerRelation.Enemy
                && player.Relation != BattlefieldPlayerRelation.Unknown)
                continue;
            if (player.Relation == BattlefieldPlayerRelation.LocalPlayer)
                continue;

            hash.Add(player.GameObjectId);
            hash.Add((int)player.Relation);
            hash.Add(player.Battalion);
            hash.Add(player.IsDead);
            hash.Add(player.IsCasting);
            hash.Add(player.IsGuarding);
            hash.Add(QuantizeClusterCoordinate(player.Position.X));
            hash.Add(QuantizeClusterCoordinate(player.Position.Z));
        }

        return hash.ToHashCode();
    }

    private static int ComputeMapVisionClusterSignature(
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var hash = new HashCode();
        hash.Add(mapVisionPoints.Count);
        if (localPlayer.HasValue)
        {
            hash.Add(localPlayer.Value.GameObjectId);
            hash.Add(QuantizeClusterCoordinate(localPlayer.Value.Position.X));
            hash.Add(QuantizeClusterCoordinate(localPlayer.Value.Position.Z));
        }

        foreach (var point in mapVisionPoints)
        {
            if (point.Relation != BattlefieldPlayerRelation.Friendly
                && point.Relation != BattlefieldPlayerRelation.Enemy)
                continue;
            if (point.IsDead)
                continue;

            hash.Add((int)point.Relation);
            hash.Add(point.Battalion);
            hash.Add(QuantizeClusterCoordinate(point.EstimatedWorldPosition.X));
            hash.Add(QuantizeClusterCoordinate(point.EstimatedWorldPosition.Z));
        }

        return hash.ToHashCode();
    }

    private static int ComputeEnemyClusterSignature(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var hash = new HashCode();
        hash.Add(players.Count);
        hash.Add(mapVisionPoints.Count);
        if (localPlayer.HasValue)
        {
            hash.Add(localPlayer.Value.GameObjectId);
            hash.Add(QuantizeClusterCoordinate(localPlayer.Value.Position.X));
            hash.Add(QuantizeClusterCoordinate(localPlayer.Value.Position.Z));
        }

        var hasMapSamples = false;
        foreach (var point in mapVisionPoints)
        {
            if (point.Relation != BattlefieldPlayerRelation.Enemy || point.IsDead)
                continue;

            hasMapSamples = true;
            hash.Add(point.Battalion);
            hash.Add(QuantizeClusterCoordinate(point.EstimatedWorldPosition.X));
            hash.Add(QuantizeClusterCoordinate(point.EstimatedWorldPosition.Z));
        }

        if (!hasMapSamples)
        {
            foreach (var player in players)
            {
                if (player.Relation != BattlefieldPlayerRelation.Enemy || player.IsDead)
                    continue;

                hash.Add(player.GameObjectId);
                hash.Add(player.Battalion);
                hash.Add(QuantizeClusterCoordinate(player.Position.X));
                hash.Add(QuantizeClusterCoordinate(player.Position.Z));
            }
        }

        return hash.ToHashCode();
    }

    private static int QuantizeClusterCoordinate(float value)
        => (int)MathF.Round(value / ClusterSignatureQuantization);

    private static BattlefieldMapEventKind GetMapEventKind(uint iconId)
    {
        if (CountdownIconIds.Contains(iconId))
            return BattlefieldMapEventKind.Countdown;
        if (HealthIconIds.Contains(iconId))
            return BattlefieldMapEventKind.Health;
        if (ControlPointIconIds.Contains(iconId))
            return BattlefieldMapEventKind.ControlPoint;

        return BattlefieldMapEventKind.Unknown;
    }

    private static unsafe string ReadTooltip(MapMarkerData marker)
    {
        if (marker.TooltipString == null)
            return string.Empty;

        try
        {
            return marker.TooltipString->ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool TryParseCountdownSeconds(string text, out int seconds)
    {
        seconds = 0;
        var match = CountdownRegex.Match(text);
        if (!match.Success)
            return false;

        seconds = int.Parse(match.Groups[1].Value) * 60 + int.Parse(match.Groups[2].Value);
        return true;
    }

    private static bool TryParseHpPercent(string text, out int hpPercent)
    {
        hpPercent = 0;
        var match = PercentRegex.Match(text);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out hpPercent))
            return false;

        hpPercent = Math.Clamp(hpPercent, 0, 100);
        return true;
    }

    private bool TryGetControlPointScore(uint iconId, out int score)
    {
        IReadOnlyDictionary<uint, int> scores;
        switch (clientState.TerritoryType)
        {
            case 431:
                scores = SealRockControlPointScores;
                break;
            case 888:
            case 1313:
            case 1273:
                scores = AzimSteppeControlPointScores;
                break;
            default:
                score = 0;
                return false;
        }

        return scores.TryGetValue(iconId, out score);
    }

    private unsafe BattlefieldFieldMarkerSnapshot[] CollectFieldMarkers()
    {
        var controller = MarkingController.Instance();
        if (controller == null)
            return Array.Empty<BattlefieldFieldMarkerSnapshot>();

        var markers = new List<BattlefieldFieldMarkerSnapshot>(8);
        var fields = controller->FieldMarkers;
        for (var i = 0; i < fields.Length; i++)
        {
            if (fields[i].Active)
                markers.Add(new BattlefieldFieldMarkerSnapshot((uint)i, fields[i].Position));
        }

        return markers.ToArray();
    }

    private static BattlefieldMapVisionClusterSnapshot[] BuildMapVisionClusters(
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var candidates = mapVisionPoints
            .Where(point => point.Relation is BattlefieldPlayerRelation.Friendly or BattlefieldPlayerRelation.Enemy)
            .Where(point => !point.IsDead)
            .ToArray();
        if (candidates.Length == 0)
            return Array.Empty<BattlefieldMapVisionClusterSnapshot>();

        var clusters = new List<BattlefieldMapVisionClusterSnapshot>(8);
        var visited = new bool[candidates.Length];
        var radiusSquared = MapVisionClusterRadius * MapVisionClusterRadius;

        for (var i = 0; i < candidates.Length; i++)
        {
            if (visited[i])
                continue;

            var relation = candidates[i].Relation;
            var battalion = candidates[i].Battalion;
            var memberIndexes = new List<int>(16);
            var queue = new Queue<int>();
            visited[i] = true;
            queue.Enqueue(i);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                memberIndexes.Add(current);

                for (var next = 0; next < candidates.Length; next++)
                {
                    if (visited[next]
                        || candidates[next].Relation != relation
                        || candidates[next].Battalion != battalion)
                        continue;

                    if (DistanceSquared2D(candidates[current].EstimatedWorldPosition, candidates[next].EstimatedWorldPosition) > radiusSquared)
                        continue;

                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            clusters.Add(CreateMapVisionCluster(candidates, memberIndexes, relation, battalion, localPlayer));
        }

        return clusters
            .OrderByDescending(cluster => cluster.PointCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .ToArray();
    }

    private static BattlefieldMapVisionClusterSnapshot CreateMapVisionCluster(
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> candidates,
        IReadOnlyList<int> memberIndexes,
        BattlefieldPlayerRelation relation,
        byte? battalion,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var center = Vector3.Zero;
        foreach (var index in memberIndexes)
            center += candidates[index].EstimatedWorldPosition;

        center /= Math.Max(1, memberIndexes.Count);
        var distanceToLocal = localPlayer.HasValue ? Vector3.Distance(localPlayer.Value.Position, center) : 0f;
        return new BattlefieldMapVisionClusterSnapshot(relation, battalion, center, memberIndexes.Count, distanceToLocal);
    }

    private unsafe BattlefieldTargetMarkerSnapshot[] CollectTargetMarkers(IReadOnlyList<BattlefieldPlayerSnapshot> players)
    {
        var controller = MarkingController.Instance();
        if (controller == null)
            return Array.Empty<BattlefieldTargetMarkerSnapshot>();

        var playerNames = new Dictionary<ulong, string>(players.Count);
        foreach (var player in players)
        {
            if (!playerNames.ContainsKey(player.GameObjectId))
                playerNames[player.GameObjectId] = player.Name;
        }
        var targetMarkers = new List<BattlefieldTargetMarkerSnapshot>(16);
        var markers = controller->Markers;
        for (var i = 0; i < markers.Length; i++)
        {
            var id = (ulong)markers[i];
            if (id == 0 || id == InvalidGameObjectId)
                continue;

            targetMarkers.Add(new BattlefieldTargetMarkerSnapshot(
                (uint)i,
                id,
                playerNames.TryGetValue(id, out var name) ? name : string.Empty));
        }

        return targetMarkers.ToArray();
    }

    private static BattlefieldObjectiveSnapshot[] BuildObjectives(
        IReadOnlyList<BattlefieldMapEventSnapshot> mapEvents,
        IReadOnlyList<BattlefieldFieldMarkerSnapshot> fieldMarkers,
        IReadOnlyList<BattlefieldTargetMarkerSnapshot> targetMarkers,
        IReadOnlyList<BattlefieldPlayerSnapshot> players)
    {
        var objectives = new List<BattlefieldObjectiveSnapshot>(
            mapEvents.Count + fieldMarkers.Count + targetMarkers.Count);

        foreach (var mapEvent in mapEvents)
        {
            objectives.Add(new BattlefieldObjectiveSnapshot(
                $"map:{mapEvent.IconId}:{mapEvent.Position.X:0.0}:{mapEvent.Position.Z:0.0}",
                mapEvent.Kind switch
                {
                    BattlefieldMapEventKind.Countdown => BattlefieldObjectiveKind.TimedSpawn,
                    BattlefieldMapEventKind.Health => BattlefieldObjectiveKind.HealthObjective,
                    BattlefieldMapEventKind.ControlPoint => BattlefieldObjectiveKind.ControlPoint,
                    _ => BattlefieldObjectiveKind.Unknown,
                },
                mapEvent.Position,
                mapEvent.IconId,
                string.IsNullOrWhiteSpace(mapEvent.Tooltip) ? MapEventKindName(mapEvent.Kind) : mapEvent.Tooltip,
                mapEvent.CountdownSeconds,
                mapEvent.HpPercent,
                mapEvent.ScoreValue));
        }

        foreach (var marker in fieldMarkers)
        {
            objectives.Add(new BattlefieldObjectiveSnapshot(
                $"field:{marker.Index}",
                BattlefieldObjectiveKind.FieldMarker,
                marker.Position,
                null,
                $"场地标记 {marker.Index + 1}",
                null,
                null,
                null));
        }

        var playerById = new Dictionary<ulong, BattlefieldPlayerSnapshot>(players.Count);
        foreach (var player in players)
        {
            if (!playerById.ContainsKey(player.GameObjectId))
                playerById[player.GameObjectId] = player;
        }
        foreach (var marker in targetMarkers)
        {
            if (!playerById.TryGetValue(marker.TargetGameObjectId, out var target))
                continue;

            objectives.Add(new BattlefieldObjectiveSnapshot(
                $"target:{marker.Index}:{marker.TargetGameObjectId}",
                BattlefieldObjectiveKind.TargetMarker,
                target.Position,
                null,
                string.IsNullOrWhiteSpace(marker.TargetName) ? target.Name : marker.TargetName,
                null,
                null,
                null));
        }

        return objectives
            .OrderBy(objective => objective.Kind)
            .ThenBy(objective => objective.Name)
            .ToArray();
    }

    private static string MapEventKindName(BattlefieldMapEventKind kind)
        => kind switch
        {
            BattlefieldMapEventKind.Countdown => "倒计时目标",
            BattlefieldMapEventKind.Health => "血量目标",
            BattlefieldMapEventKind.ControlPoint => "据点目标",
            _ => "地图目标",
        };

    private BattlefieldObjectiveActorSnapshot[] CollectObjectiveActors(FrontlineMapType mapType, BattlefieldPlayerSnapshot? localPlayer)
    {
        if (mapType == FrontlineMapType.Unknown)
            return Array.Empty<BattlefieldObjectiveActorSnapshot>();

        var localPosition = localPlayer?.Position ?? objectTable.LocalPlayer?.Position ?? Vector3.Zero;
        var hasLocalPosition = localPlayer.HasValue;
        var actors = new List<BattlefieldObjectiveActorSnapshot>(16);
        foreach (var obj in objectTable)
        {
            if (TryCreateObjectiveActorSnapshot(mapType, obj, localPosition, hasLocalPosition, out var actor))
                actors.Add(actor);
        }

        return actors
            .OrderBy(actor => actor.Category)
            .ThenBy(actor => actor.DistanceToLocal)
            .ToArray();
    }

    private bool TryCreateObjectiveActorSnapshot(
        FrontlineMapType mapType,
        IGameObject? obj,
        Vector3 localPosition,
        bool hasLocalPosition,
        out BattlefieldObjectiveActorSnapshot snapshot)
    {
        snapshot = default;
        if (mapType == FrontlineMapType.Unknown
            || obj == null
            || !obj.IsValid()
            || obj is IPlayerCharacter
            || obj is not ICharacter character)
        {
            return false;
        }

        var name = obj.Name.TextValue;
        var maxHp = character.MaxHp;
        var category = InferActorObjectiveCategory(mapType, name, maxHp);
        if (category == BattlefieldMapObjectiveCategory.Unknown)
            return false;

        var currentHp = character.CurrentHp;
        var hpPercent = maxHp > 0 ? (int)MathF.Round(currentHp * 100f / maxHp) : 0;
        snapshot = new BattlefieldObjectiveActorSnapshot(
            obj.GameObjectId,
            obj.BaseId,
            obj.BaseId,
            name,
            category,
            obj.Position,
            hasLocalPosition ? Vector3.Distance(localPosition, obj.Position) : 0f,
            currentHp,
            maxHp,
            Math.Clamp(hpPercent, 0, 100),
            obj.ObjectKind.ToString());
        return true;
    }

    private BattlefieldMapObjectiveSnapshot[] BuildMapObjectives(
        FrontlineMapType mapType,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        IReadOnlyList<BattlefieldMapEventSnapshot> mapEvents,
        IReadOnlyList<BattlefieldObjectiveActorSnapshot> objectiveActors,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldPlayerSnapshot? localPlayer,
        long now)
    {
        if (mapType == FrontlineMapType.Unknown && mapEvents.Count == 0 && objectiveActors.Count == 0)
            return Array.Empty<BattlefieldMapObjectiveSnapshot>();

        var objectives = new List<BattlefieldMapObjectiveSnapshot>(mapEvents.Count + objectiveActors.Count);
        var usedActorIds = new HashSet<ulong>();
        var localBattalion = localPlayer.HasValue ? NormalizeBattalion(localPlayer.Value.Battalion) : null;

        foreach (var mapEvent in mapEvents)
        {
            var category = InferMapEventObjectiveCategory(mapType, mapEvent);
            if (category == BattlefieldMapObjectiveCategory.Unknown)
                continue;

            var actor = FindNearestObjectiveActor(category, mapEvent.Position, objectiveActors, usedActorIds);
            if (actor.HasValue)
                usedActorIds.Add(actor.Value.GameObjectId);

            objectives.Add(CreateMapObjectiveFromEvent(
                mapType,
                mapKnowledge,
                mapEvent,
                actor,
                players,
                localBattalion,
                now));
        }

        foreach (var actor in objectiveActors)
        {
            if (usedActorIds.Contains(actor.GameObjectId))
                continue;

            objectives.Add(CreateMapObjectiveFromActor(mapType, mapKnowledge, actor, players, localBattalion, now));
        }

        PruneObjectiveHistory(now);
        return objectives
            .OrderByDescending(objective => objective.IsBeingFocused)
            .ThenByDescending(objective => objective.IsBeingAttacked)
            .ThenBy(objective => objective.Category)
            .ThenByDescending(objective => RankSortValue(objective.RankName))
            .ThenBy(objective => objective.RemainingSeconds ?? int.MaxValue)
            .ThenBy(objective => objective.Name)
            .ToArray();
    }

    private BattlefieldMapObjectiveSnapshot CreateMapObjectiveFromEvent(
        FrontlineMapType mapType,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        BattlefieldMapEventSnapshot mapEvent,
        BattlefieldObjectiveActorSnapshot? actor,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        byte? localBattalion,
        long now)
    {
        var category = InferMapEventObjectiveCategory(mapType, mapEvent);
        var locationId = TryParseLocationId(mapEvent.Tooltip, out var parsedLocationId) ? parsedLocationId : string.Empty;
        var scoreValue = mapEvent.ScoreValue ?? InferScoreValue(mapType, category, mapEvent.Tooltip, actor);
        var rankName = InferRankName(mapType, category, mapEvent.IconId, scoreValue, mapEvent.Tooltip, actor);
        var ownership = InferOwnership(mapEvent.IconId, mapEvent.Tooltip);
        var state = InferObjectiveState(category, mapEvent.Kind, mapEvent.Tooltip, ownership, actor?.HpPercent);
        var hpPercent = actor?.HpPercent ?? mapEvent.HpPercent;
        var id = BuildObjectiveId(mapType, category, mapEvent.IconId, mapEvent.Position, locationId);
        var history = TouchObjectiveHistory(id, hpPercent, actor?.CurrentHp, now);
        var focus = BuildObjectiveFocus(
            players,
            actor.HasValue ? new[] { actor.Value.GameObjectId } : Array.Empty<ulong>(),
            actor?.Position ?? mapEvent.Position,
            category);
        var remainingSeconds = ResolveObjectiveRemainingSeconds(
            mapKnowledge,
            category,
            state,
            rankName,
            mapEvent.CountdownSeconds,
            history.FirstSeenTicks,
            now,
            out var remainingSource);
        var wasRecentlyDamaged = history.LastDamageTicks > 0 && now - history.LastDamageTicks <= ObjectiveRecentDamageWindowMs;
        var isBeingAttacked = focus.AttackerCount > 0 || focus.CasterCount > 0 || wasRecentlyDamaged;

        return new BattlefieldMapObjectiveSnapshot(
            id,
            mapType,
            category,
            state,
            actor?.Position ?? mapEvent.Position,
            mapEvent.IconId,
            actor?.GameObjectId,
            BuildObjectiveName(mapType, category, rankName, locationId, mapEvent.Tooltip, actor?.Name),
            locationId,
            rankName,
            ownership,
            BuildOwnershipText(ownership, localBattalion),
            remainingSeconds,
            remainingSource,
            hpPercent,
            actor?.CurrentHp,
            actor?.MaxHp,
            scoreValue,
            focus.AttackerCount,
            focus.FriendlyAttackerCount,
            focus.EnemyAttackerCount,
            focus.CasterCount,
            focus.IsBeingFocused,
            isBeingAttacked,
            history.RecentHpLoss,
            history.RecentHpLossPerSecond,
            history.LastDamageTicks > 0 ? Math.Max(0, now - history.LastDamageTicks) : null,
            focus.Contributors,
            focus.ContributionSummaryText,
            focus.EnmitySourceText,
            focus.AggressorNames,
            BuildObjectiveSourceText(mapEvent, actor, wasRecentlyDamaged),
            CalculateObjectiveConfidence(mapType, mapEvent, actor, rankName, ownership, hpPercent, remainingSeconds));
    }

    private BattlefieldMapObjectiveSnapshot CreateMapObjectiveFromActor(
        FrontlineMapType mapType,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        BattlefieldObjectiveActorSnapshot actor,
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        byte? localBattalion,
        long now)
    {
        var rankName = InferRankName(mapType, actor.Category, null, InferScoreValue(mapType, actor.Category, actor.Name, actor), actor.Name, actor);
        var scoreValue = InferScoreValue(mapType, actor.Category, actor.Name, actor);
        var state = actor.HpPercent <= 0 ? BattlefieldMapObjectiveState.Destroyed : BattlefieldMapObjectiveState.Active;
        var id = $"actor:{mapType}:{actor.Category}:{actor.GameObjectId:X}";
        var history = TouchObjectiveHistory(id, actor.HpPercent, actor.CurrentHp, now);
        var focus = BuildObjectiveFocus(players, new[] { actor.GameObjectId }, actor.Position, actor.Category);
        var remainingSeconds = ResolveObjectiveRemainingSeconds(
            mapKnowledge,
            actor.Category,
            state,
            rankName,
            null,
            history.FirstSeenTicks,
            now,
            out var remainingSource);
        var wasRecentlyDamaged = history.LastDamageTicks > 0 && now - history.LastDamageTicks <= ObjectiveRecentDamageWindowMs;
        var isBeingAttacked = focus.AttackerCount > 0 || focus.CasterCount > 0 || wasRecentlyDamaged;

        return new BattlefieldMapObjectiveSnapshot(
            id,
            mapType,
            actor.Category,
            state,
            actor.Position,
            null,
            actor.GameObjectId,
            BuildObjectiveName(mapType, actor.Category, rankName, string.Empty, actor.Name, actor.Name),
            string.Empty,
            rankName,
            null,
            BuildOwnershipText(null, localBattalion),
            remainingSeconds,
            remainingSource,
            actor.HpPercent,
            actor.CurrentHp,
            actor.MaxHp,
            scoreValue,
            focus.AttackerCount,
            focus.FriendlyAttackerCount,
            focus.EnemyAttackerCount,
            focus.CasterCount,
            focus.IsBeingFocused,
            isBeingAttacked,
            history.RecentHpLoss,
            history.RecentHpLossPerSecond,
            history.LastDamageTicks > 0 ? Math.Max(0, now - history.LastDamageTicks) : null,
            focus.Contributors,
            focus.ContributionSummaryText,
            focus.EnmitySourceText,
            focus.AggressorNames,
            $"对象：{actor.ObjectKindText}",
            CalculateActorObjectiveConfidence(mapType, actor, rankName, scoreValue));
    }

    private static BattlefieldMapObjectiveSnapshot[] BuildMapObjectivesDeferred(
        FrontlineMapType mapType,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        IReadOnlyList<BattlefieldMapEventSnapshot> mapEvents,
        IReadOnlyList<BattlefieldObjectiveActorSnapshot> objectiveActors,
        BattlefieldPlayerSnapshot? localPlayer,
        long now,
        IReadOnlyDictionary<string, ObjectiveHistorySnapshot> objectiveHistoryById,
        ObjectiveFocusIndex focusIndex)
    {
        if (mapType == FrontlineMapType.Unknown && mapEvents.Count == 0 && objectiveActors.Count == 0)
            return Array.Empty<BattlefieldMapObjectiveSnapshot>();

        var objectives = new List<BattlefieldMapObjectiveSnapshot>(mapEvents.Count + objectiveActors.Count);
        var usedActorIds = new HashSet<ulong>();
        var localBattalion = localPlayer.HasValue ? NormalizeBattalion(localPlayer.Value.Battalion) : null;

        foreach (var mapEvent in mapEvents)
        {
            var category = InferMapEventObjectiveCategory(mapType, mapEvent);
            if (category == BattlefieldMapObjectiveCategory.Unknown)
                continue;

            var actor = FindNearestObjectiveActorFast(category, mapEvent.Position, objectiveActors, usedActorIds);
            if (actor.HasValue)
                usedActorIds.Add(actor.Value.GameObjectId);

            objectives.Add(CreateMapObjectiveFromEventDeferred(
                mapType,
                mapKnowledge,
                mapEvent,
                actor,
                localBattalion,
                now,
                objectiveHistoryById,
                focusIndex));
        }

        foreach (var actor in objectiveActors)
        {
            if (usedActorIds.Contains(actor.GameObjectId))
                continue;

            objectives.Add(CreateMapObjectiveFromActorDeferred(
                mapType,
                mapKnowledge,
                actor,
                localBattalion,
                now,
                objectiveHistoryById,
                focusIndex));
        }

        return objectives
            .OrderByDescending(objective => objective.IsBeingFocused)
            .ThenByDescending(objective => objective.IsBeingAttacked)
            .ThenBy(objective => objective.Category)
            .ThenByDescending(objective => RankSortValue(objective.RankName))
            .ThenBy(objective => objective.RemainingSeconds ?? int.MaxValue)
            .ThenBy(objective => objective.Name)
            .ToArray();
    }

    private static BattlefieldMapObjectiveSnapshot CreateMapObjectiveFromEventDeferred(
        FrontlineMapType mapType,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        BattlefieldMapEventSnapshot mapEvent,
        BattlefieldObjectiveActorSnapshot? actor,
        byte? localBattalion,
        long now,
        IReadOnlyDictionary<string, ObjectiveHistorySnapshot> objectiveHistoryById,
        ObjectiveFocusIndex focusIndex)
    {
        var category = InferMapEventObjectiveCategory(mapType, mapEvent);
        var locationId = TryParseLocationId(mapEvent.Tooltip, out var parsedLocationId) ? parsedLocationId : string.Empty;
        var scoreValue = mapEvent.ScoreValue ?? InferScoreValue(mapType, category, mapEvent.Tooltip, actor);
        var rankName = InferRankName(mapType, category, mapEvent.IconId, scoreValue, mapEvent.Tooltip, actor);
        var ownership = InferOwnership(mapEvent.IconId, mapEvent.Tooltip);
        var state = InferObjectiveState(category, mapEvent.Kind, mapEvent.Tooltip, ownership, actor?.HpPercent);
        var hpPercent = actor?.HpPercent ?? mapEvent.HpPercent;
        var id = BuildObjectiveId(mapType, category, mapEvent.IconId, mapEvent.Position, locationId);
        var history = MergeObjectiveHistorySnapshots(
            GetObjectiveHistorySnapshot(objectiveHistoryById, id, now),
            actor.HasValue
                ? GetObjectiveHistorySnapshot(objectiveHistoryById, BuildActorObjectiveHistoryId(mapType, actor.Value), now)
                : ObjectiveHistorySnapshot.Empty);
        var focus = actor.HasValue
            ? BuildObjectiveFocusFast(focusIndex, new[] { actor.Value.GameObjectId }, actor.Value.Position, category)
            : ObjectiveFocusWork.Empty;
        var remainingSeconds = ResolveObjectiveRemainingSeconds(
            mapKnowledge,
            category,
            state,
            rankName,
            mapEvent.CountdownSeconds,
            history.FirstSeenTicks,
            now,
            out var remainingSource);
        var wasRecentlyDamaged = history.LastDamageTicks > 0 && now - history.LastDamageTicks <= ObjectiveRecentDamageWindowMs;
        var isBeingAttacked = focus.AttackerCount > 0 || focus.CasterCount > 0 || wasRecentlyDamaged;

        return new BattlefieldMapObjectiveSnapshot(
            id,
            mapType,
            category,
            state,
            actor?.Position ?? mapEvent.Position,
            mapEvent.IconId,
            actor?.GameObjectId,
            BuildObjectiveName(mapType, category, rankName, locationId, mapEvent.Tooltip, actor?.Name),
            locationId,
            rankName,
            ownership,
            BuildOwnershipText(ownership, localBattalion),
            remainingSeconds,
            remainingSource,
            hpPercent,
            actor?.CurrentHp,
            actor?.MaxHp,
            scoreValue,
            focus.AttackerCount,
            focus.FriendlyAttackerCount,
            focus.EnemyAttackerCount,
            focus.CasterCount,
            focus.IsBeingFocused,
            isBeingAttacked,
            history.RecentHpLoss,
            history.RecentHpLossPerSecond,
            history.LastDamageTicks > 0 ? Math.Max(0, now - history.LastDamageTicks) : null,
            focus.Contributors,
            focus.ContributionSummaryText,
            focus.EnmitySourceText,
            focus.AggressorNames,
            BuildObjectiveSourceText(mapEvent, actor, wasRecentlyDamaged),
            CalculateObjectiveConfidence(mapType, mapEvent, actor, rankName, ownership, hpPercent, remainingSeconds));
    }

    private static BattlefieldMapObjectiveSnapshot CreateMapObjectiveFromActorDeferred(
        FrontlineMapType mapType,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        BattlefieldObjectiveActorSnapshot actor,
        byte? localBattalion,
        long now,
        IReadOnlyDictionary<string, ObjectiveHistorySnapshot> objectiveHistoryById,
        ObjectiveFocusIndex focusIndex)
    {
        var rankName = InferRankName(mapType, actor.Category, null, InferScoreValue(mapType, actor.Category, actor.Name, actor), actor.Name, actor);
        var scoreValue = InferScoreValue(mapType, actor.Category, actor.Name, actor);
        var state = actor.HpPercent <= 0 ? BattlefieldMapObjectiveState.Destroyed : BattlefieldMapObjectiveState.Active;
        var id = BuildActorObjectiveHistoryId(mapType, actor);
        var history = GetObjectiveHistorySnapshot(objectiveHistoryById, id, now);
        var focus = BuildObjectiveFocusFast(focusIndex, new[] { actor.GameObjectId }, actor.Position, actor.Category);
        var remainingSeconds = ResolveObjectiveRemainingSeconds(
            mapKnowledge,
            actor.Category,
            state,
            rankName,
            null,
            history.FirstSeenTicks,
            now,
            out var remainingSource);
        var wasRecentlyDamaged = history.LastDamageTicks > 0 && now - history.LastDamageTicks <= ObjectiveRecentDamageWindowMs;
        var isBeingAttacked = focus.AttackerCount > 0 || focus.CasterCount > 0 || wasRecentlyDamaged;

        return new BattlefieldMapObjectiveSnapshot(
            id,
            mapType,
            actor.Category,
            state,
            actor.Position,
            null,
            actor.GameObjectId,
            BuildObjectiveName(mapType, actor.Category, rankName, string.Empty, actor.Name, actor.Name),
            string.Empty,
            rankName,
            null,
            BuildOwnershipText(null, localBattalion),
            remainingSeconds,
            remainingSource,
            actor.HpPercent,
            actor.CurrentHp,
            actor.MaxHp,
            scoreValue,
            focus.AttackerCount,
            focus.FriendlyAttackerCount,
            focus.EnemyAttackerCount,
            focus.CasterCount,
            focus.IsBeingFocused,
            isBeingAttacked,
            history.RecentHpLoss,
            history.RecentHpLossPerSecond,
            history.LastDamageTicks > 0 ? Math.Max(0, now - history.LastDamageTicks) : null,
            focus.Contributors,
            focus.ContributionSummaryText,
            focus.EnmitySourceText,
            focus.AggressorNames,
            $"对象：{actor.ObjectKindText}",
            CalculateActorObjectiveConfidence(mapType, actor, rankName, scoreValue));
    }

    private static BattlefieldMapObjectiveCategory InferActorObjectiveCategory(FrontlineMapType mapType, string name, uint maxHp)
    {
        if (IsIceObjectiveName(name) || mapType == FrontlineMapType.FieldsOfHonor && IsLikelyIceHp(maxHp))
            return BattlefieldMapObjectiveCategory.Ice;

        if (mapType == FrontlineMapType.BorderlandRuinsSecure && ContainsAny(
                name,
                "截击", "无人机", "指挥系统", "截击系统", "intercept", "drone", "node", "commander"))
        {
            return BattlefieldMapObjectiveCategory.Monster;
        }

        if (ContainsAny(name, "亚拉戈石文书", "亚拉戈石文", "allagan tomelith"))
            return BattlefieldMapObjectiveCategory.Tomelith;

        if (ContainsAny(name, "无垢的大地", "ovoos", "ovo"))
            return BattlefieldMapObjectiveCategory.Ovoo;

        if (ContainsAny(name, "战略目标点", "strategic target", "tactical target"))
            return BattlefieldMapObjectiveCategory.StrategicPoint;

        return BattlefieldMapObjectiveCategory.Unknown;
    }

    private static BattlefieldMapObjectiveCategory InferMapEventObjectiveCategory(FrontlineMapType mapType, BattlefieldMapEventSnapshot mapEvent)
    {
        if (IsIceObjectiveName(mapEvent.Tooltip))
            return BattlefieldMapObjectiveCategory.Ice;

        if (ContainsAny(mapEvent.Tooltip, "亚拉戈石文书", "亚拉戈石文", "allagan tomelith"))
            return BattlefieldMapObjectiveCategory.Tomelith;

        if (ContainsAny(mapEvent.Tooltip, "无垢的大地", "ovoos", "ovo"))
            return BattlefieldMapObjectiveCategory.Ovoo;

        if (ContainsAny(mapEvent.Tooltip, "战略目标点", "strategic target", "tactical target"))
            return BattlefieldMapObjectiveCategory.StrategicPoint;

        return mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => mapEvent.Kind == BattlefieldMapEventKind.ControlPoint
                ? BattlefieldMapObjectiveCategory.Base
                : BattlefieldMapObjectiveCategory.Monster,
            FrontlineMapType.SealRock => BattlefieldMapObjectiveCategory.Tomelith,
            FrontlineMapType.FieldsOfHonor => BattlefieldMapObjectiveCategory.Ice,
            FrontlineMapType.OnsalHakair => BattlefieldMapObjectiveCategory.Ovoo,
            FrontlineMapType.Vochester => BattlefieldMapObjectiveCategory.StrategicPoint,
            _ => mapEvent.Kind switch
            {
                BattlefieldMapEventKind.Health => BattlefieldMapObjectiveCategory.Monster,
                BattlefieldMapEventKind.ControlPoint => BattlefieldMapObjectiveCategory.Base,
                _ => BattlefieldMapObjectiveCategory.Unknown,
            }
        };
    }

    private static BattlefieldMapObjectiveState InferObjectiveState(
        BattlefieldMapObjectiveCategory category,
        BattlefieldMapEventKind eventKind,
        string tooltip,
        NodeOwnership? ownership,
        int? hpPercent)
    {
        if (hpPercent.HasValue && hpPercent.Value <= 0)
            return BattlefieldMapObjectiveState.Destroyed;

        if (ContainsAny(tooltip, "争夺", "抢夺", "contested"))
            return BattlefieldMapObjectiveState.Contested;

        if (ContainsAny(tooltip, "失效", "停止", "待机", "inactive", "disabled"))
            return BattlefieldMapObjectiveState.Inactive;

        if (eventKind == BattlefieldMapEventKind.Countdown)
            return BattlefieldMapObjectiveState.Warning;

        if (eventKind == BattlefieldMapEventKind.Health)
            return BattlefieldMapObjectiveState.Active;

        if (category is BattlefieldMapObjectiveCategory.Base
            or BattlefieldMapObjectiveCategory.Tomelith
            or BattlefieldMapObjectiveCategory.Ovoo
            or BattlefieldMapObjectiveCategory.StrategicPoint)
        {
            if (ownership.HasValue && ownership.Value != NodeOwnership.Neutral)
                return BattlefieldMapObjectiveState.Controlled;

            return BattlefieldMapObjectiveState.Active;
        }

        return BattlefieldMapObjectiveState.Active;
    }

    private static int? InferScoreValue(
        FrontlineMapType mapType,
        BattlefieldMapObjectiveCategory category,
        string text,
        BattlefieldObjectiveActorSnapshot? actor)
    {
        var rankName = InferRankName(mapType, category, null, null, text, actor);
        if (category == BattlefieldMapObjectiveCategory.Ice)
        {
            if (string.Equals(rankName, "大", StringComparison.Ordinal))
                return 200;
            if (string.Equals(rankName, "小", StringComparison.Ordinal))
                return 50;
        }

        return (mapType, rankName) switch
        {
            (FrontlineMapType.SealRock, "S") => 160,
            (FrontlineMapType.SealRock, "A") => 120,
            (FrontlineMapType.SealRock, "B") => 80,
            (FrontlineMapType.OnsalHakair, "S") => 200,
            (FrontlineMapType.OnsalHakair, "A") => 100,
            (FrontlineMapType.OnsalHakair, "B") => 50,
            (FrontlineMapType.Vochester, "S") => 200,
            (FrontlineMapType.Vochester, "A") => 100,
            (FrontlineMapType.Vochester, "B") => 50,
            _ => null,
        };
    }

    private static string InferRankName(
        FrontlineMapType mapType,
        BattlefieldMapObjectiveCategory category,
        uint? iconId,
        int? scoreValue,
        string text,
        BattlefieldObjectiveActorSnapshot? actor)
    {
        if (category == BattlefieldMapObjectiveCategory.Base && mapType == FrontlineMapType.BorderlandRuinsSecure)
            return string.Empty;

        if (category == BattlefieldMapObjectiveCategory.Ice)
        {
            if (ContainsAny(text, "大冰", "大）", "大", "：大", ":大", "large") || actor.HasValue && actor.Value.MaxHp >= 1000000)
                return "大";
            if (ContainsAny(text, "小冰", "小）", "小", "：小", ":小", "small") || actor.HasValue && actor.Value.MaxHp is > 0 and < 1000000)
                return "小";
            return string.Empty;
        }

        if (ContainsRankToken(text, "S"))
            return "S";
        if (ContainsRankToken(text, "A"))
            return "A";
        if (ContainsRankToken(text, "B"))
            return "B";

        if (scoreValue.HasValue)
        {
            return (mapType, scoreValue.Value) switch
            {
                (FrontlineMapType.SealRock, 160) => "S",
                (FrontlineMapType.SealRock, 120) => "A",
                (FrontlineMapType.SealRock, 80) => "B",
                (FrontlineMapType.OnsalHakair, 200) => "S",
                (FrontlineMapType.OnsalHakair, 100) => "A",
                (FrontlineMapType.OnsalHakair, 50) => "B",
                (FrontlineMapType.Vochester, 200) => "S",
                (FrontlineMapType.Vochester, 100) => "A",
                (FrontlineMapType.Vochester, 50) => "B",
                _ => string.Empty,
            };
        }

        if (iconId.HasValue && TryInferRankFromControlPointIcon(iconId.Value, out var rankFromIcon))
            return rankFromIcon;

        return string.Empty;
    }

    private static NodeOwnership? InferOwnership(uint? iconId, string tooltip)
    {
        if (TryInferOwnershipFromText(tooltip, out var textOwnership))
            return textOwnership;

        if (iconId.HasValue && TryInferOwnershipFromControlPointIcon(iconId.Value, out var iconOwnership))
            return iconOwnership;

        return null;
    }

    private static bool TryInferOwnershipFromText(string text, out NodeOwnership ownership)
    {
        if (ContainsAny(text, "黑涡", "黑渦", "maelstrom"))
        {
            ownership = NodeOwnership.Maelstrom;
            return true;
        }

        if (ContainsAny(text, "双蛇", "雙蛇", "twin adder", "adders"))
        {
            ownership = NodeOwnership.TwinAdder;
            return true;
        }

        if (ContainsAny(text, "鎭掕緣", "鎭嗚紳", "immortal flames", "flames"))
        {
            ownership = NodeOwnership.ImmortalFlames;
            return true;
        }

        if (ContainsAny(text, "中立", "未占领", "未占领", "neutral", "unclaimed"))
        {
            ownership = NodeOwnership.Neutral;
            return true;
        }

        ownership = NodeOwnership.Neutral;
        return false;
    }

    private static bool TryInferOwnershipFromControlPointIcon(uint iconId, out NodeOwnership ownership)
    {
        ownership = NodeOwnership.Neutral;
        if (!ControlPointIconIds.Contains(iconId))
            return false;

        var variant = (iconId - 60585) % 4;
        ownership = variant switch
        {
            1 => NodeOwnership.Maelstrom,
            2 => NodeOwnership.TwinAdder,
            3 => NodeOwnership.ImmortalFlames,
            _ => NodeOwnership.Neutral,
        };
        return true;
    }

    private static bool TryInferRankFromControlPointIcon(uint iconId, out string rankName)
    {
        rankName = string.Empty;
        if (!ControlPointIconIds.Contains(iconId))
            return false;

        rankName = ((iconId - 60585) / 4) switch
        {
            0 => "B",
            1 => "A",
            2 => "S",
            _ => string.Empty,
        };
        return !string.IsNullOrEmpty(rankName);
    }

    private static BattlefieldObjectiveActorSnapshot? FindNearestObjectiveActor(
        BattlefieldMapObjectiveCategory category,
        Vector3 position,
        IReadOnlyList<BattlefieldObjectiveActorSnapshot> actors,
        IReadOnlySet<ulong> usedActorIds)
    {
        var match = actors
            .Where(actor => !usedActorIds.Contains(actor.GameObjectId) && IsActorCompatibleWithCategory(category, actor.Category))
            .Select(actor => new { Actor = actor, Distance = Distance2D(position, actor.Position) })
            .Where(item => item.Distance <= ObjectiveActorMatchDistance)
            .OrderBy(item => item.Distance)
            .FirstOrDefault();

        return match?.Actor;
    }

    private static bool IsActorCompatibleWithCategory(BattlefieldMapObjectiveCategory eventCategory, BattlefieldMapObjectiveCategory actorCategory)
        => eventCategory == actorCategory
            || eventCategory == BattlefieldMapObjectiveCategory.Unknown
            || actorCategory == BattlefieldMapObjectiveCategory.Unknown
            || eventCategory == BattlefieldMapObjectiveCategory.Monster && actorCategory == BattlefieldMapObjectiveCategory.Ice;

    private static BattlefieldObjectiveActorSnapshot? FindNearestObjectiveActorFast(
        BattlefieldMapObjectiveCategory category,
        Vector3 position,
        IReadOnlyList<BattlefieldObjectiveActorSnapshot> actors,
        IReadOnlySet<ulong> usedActorIds)
    {
        BattlefieldObjectiveActorSnapshot? bestMatch = null;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < actors.Count; i++)
        {
            var actor = actors[i];
            if (usedActorIds.Contains(actor.GameObjectId) || !IsActorCompatibleWithCategory(category, actor.Category))
                continue;

            var distance = Distance2D(position, actor.Position);
            if (distance > ObjectiveActorMatchDistance || distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestMatch = actor;
        }

        return bestMatch;
    }

    private IReadOnlyDictionary<string, ObjectiveHistorySnapshot> CaptureObjectiveHistorySnapshots(
        FrontlineMapType mapType,
        IReadOnlyList<BattlefieldMapEventSnapshot> mapEvents,
        IReadOnlyList<BattlefieldObjectiveActorSnapshot> objectiveActors,
        long now)
    {
        var snapshots = new Dictionary<string, ObjectiveHistorySnapshot>(mapEvents.Count + objectiveActors.Count, StringComparer.Ordinal);
        foreach (var mapEvent in mapEvents)
        {
            var category = InferMapEventObjectiveCategory(mapType, mapEvent);
            if (category == BattlefieldMapObjectiveCategory.Unknown)
                continue;

            var locationId = TryParseLocationId(mapEvent.Tooltip, out var parsedLocationId) ? parsedLocationId : string.Empty;
            var id = BuildObjectiveId(mapType, category, mapEvent.IconId, mapEvent.Position, locationId);
            snapshots[id] = ToObjectiveHistorySnapshot(TouchObjectiveHistory(id, mapEvent.HpPercent, null, now));
        }

        foreach (var actor in objectiveActors)
        {
            var id = BuildActorObjectiveHistoryId(mapType, actor);
            snapshots[id] = ToObjectiveHistorySnapshot(TouchObjectiveHistory(id, actor.HpPercent, actor.CurrentHp, now));
        }

        PruneObjectiveHistory(now);
        return snapshots;
    }

    private static string BuildActorObjectiveHistoryId(FrontlineMapType mapType, BattlefieldObjectiveActorSnapshot actor)
        => $"actor:{mapType}:{actor.Category}:{actor.GameObjectId:X}";

    private static ObjectiveHistorySnapshot ToObjectiveHistorySnapshot(ObjectiveHistoryEntry entry)
        => new(
            entry.FirstSeenTicks,
            entry.LastSeenTicks,
            entry.LastHpPercent,
            entry.LastDamageTicks,
            entry.RecentHpLoss,
            entry.RecentHpLossPerSecond);

    private static ObjectiveHistorySnapshot GetObjectiveHistorySnapshot(
        IReadOnlyDictionary<string, ObjectiveHistorySnapshot> objectiveHistoryById,
        string id,
        long now)
        => objectiveHistoryById.TryGetValue(id, out var history)
            ? history
            : new ObjectiveHistorySnapshot(now, now, null, 0, null, 0f);

    private static ObjectiveHistorySnapshot MergeObjectiveHistorySnapshots(
        ObjectiveHistorySnapshot baseHistory,
        ObjectiveHistorySnapshot overlayHistory)
    {
        if (overlayHistory == ObjectiveHistorySnapshot.Empty)
            return baseHistory;
        if (baseHistory == ObjectiveHistorySnapshot.Empty)
            return overlayHistory;

        return new ObjectiveHistorySnapshot(
            baseHistory.FirstSeenTicks > 0 && overlayHistory.FirstSeenTicks > 0
                ? Math.Min(baseHistory.FirstSeenTicks, overlayHistory.FirstSeenTicks)
                : Math.Max(baseHistory.FirstSeenTicks, overlayHistory.FirstSeenTicks),
            Math.Max(baseHistory.LastSeenTicks, overlayHistory.LastSeenTicks),
            overlayHistory.LastHpPercent ?? baseHistory.LastHpPercent,
            overlayHistory.LastDamageTicks > 0 ? overlayHistory.LastDamageTicks : baseHistory.LastDamageTicks,
            overlayHistory.RecentHpLoss ?? baseHistory.RecentHpLoss,
            overlayHistory.RecentHpLoss.HasValue ? overlayHistory.RecentHpLossPerSecond : baseHistory.RecentHpLossPerSecond);
    }

    private static ObjectiveFocusIndex BuildObjectiveFocusIndex(IReadOnlyList<BattlefieldPlayerSnapshot> players)
    {
        var targetingPlayersByObjectId = new Dictionary<ulong, List<BattlefieldPlayerSnapshot>>();
        var castingPlayersByObjectId = new Dictionary<ulong, List<BattlefieldPlayerSnapshot>>();

        foreach (var player in players)
        {
            if (player.IsDead)
                continue;

            if (IsValidGameObjectId(player.TargetObjectId))
            {
                if (!targetingPlayersByObjectId.TryGetValue(player.TargetObjectId, out var targeters))
                {
                    targeters = new List<BattlefieldPlayerSnapshot>(4);
                    targetingPlayersByObjectId[player.TargetObjectId] = targeters;
                }

                targeters.Add(player);
            }

            if (player.IsCasting && IsValidGameObjectId(player.CastTargetObjectId))
            {
                if (!castingPlayersByObjectId.TryGetValue(player.CastTargetObjectId, out var casters))
                {
                    casters = new List<BattlefieldPlayerSnapshot>(4);
                    castingPlayersByObjectId[player.CastTargetObjectId] = casters;
                }

                casters.Add(player);
            }
        }

        return new ObjectiveFocusIndex(targetingPlayersByObjectId, castingPlayersByObjectId);
    }

    private static ObjectiveFocusWork BuildObjectiveFocusFast(
        ObjectiveFocusIndex focusIndex,
        IReadOnlyCollection<ulong> actorGameObjectIds,
        Vector3 objectivePosition,
        BattlefieldMapObjectiveCategory category)
    {
        if (actorGameObjectIds.Count == 0)
            return ObjectiveFocusWork.Empty;

        var relevantPlayers = new Dictionary<ulong, (BattlefieldPlayerSnapshot Player, bool TargetsObjective, bool CastsAtObjective)>();
        foreach (var actorId in actorGameObjectIds)
        {
            if (focusIndex.TargetingPlayersByObjectId.TryGetValue(actorId, out var targeters))
            {
                foreach (var player in targeters)
                {
                    if (relevantPlayers.TryGetValue(player.GameObjectId, out var existing))
                        relevantPlayers[player.GameObjectId] = (existing.Player, true, existing.CastsAtObjective);
                    else
                        relevantPlayers[player.GameObjectId] = (player, true, false);
                }
            }

            if (focusIndex.CastingPlayersByObjectId.TryGetValue(actorId, out var casters))
            {
                foreach (var player in casters)
                {
                    if (relevantPlayers.TryGetValue(player.GameObjectId, out var existing))
                        relevantPlayers[player.GameObjectId] = (existing.Player, existing.TargetsObjective, true);
                    else
                        relevantPlayers[player.GameObjectId] = (player, false, true);
                }
            }
        }

        if (relevantPlayers.Count == 0)
            return ObjectiveFocusWork.Empty;

        var attackerIds = new HashSet<ulong>();
        var casterIds = new HashSet<ulong>();
        var sourceNames = new List<string>(8);
        var friendlyAttackers = 0;
        var enemyAttackers = 0;
        var contributors = new List<BattlefieldObjectiveContributionSnapshot>(Math.Min(16, relevantPlayers.Count));

        foreach (var candidate in relevantPlayers.Values)
        {
            var player = candidate.Player;
            var targetsObjective = candidate.TargetsObjective;
            var castsAtObjective = candidate.CastsAtObjective;
            var distance = Distance2D(player.Position, objectivePosition);
            var estimatedWeight = (targetsObjective ? 1f : 0f)
                + (castsAtObjective ? 1.5f : 0f)
                + (distance <= 35f ? 0.25f : 0f);
            var evidence = BuildObjectiveContributionEvidence(targetsObjective, castsAtObjective, category);
            if (targetsObjective)
                attackerIds.Add(player.GameObjectId);
            if (castsAtObjective)
                casterIds.Add(player.GameObjectId);

            if (IsFriendlySide(player.Relation))
                friendlyAttackers++;
            else if (player.Relation == BattlefieldPlayerRelation.Enemy)
                enemyAttackers++;

            if (sourceNames.Count < 8)
            {
                var label = FormatSourceLabel(player);
                if (!sourceNames.Contains(label, StringComparer.Ordinal))
                    sourceNames.Add(label);
            }

            contributors.Add(new BattlefieldObjectiveContributionSnapshot(
                player.GameObjectId,
                player.Name,
                player.Relation,
                NormalizeBattalion(player.Battalion),
                ResolveJobInfo(player.ClassJobId).Name,
                targetsObjective,
                castsAtObjective,
                distance,
                null,
                null,
                estimatedWeight,
                evidence));
        }

        var attackerCount = attackerIds.Count;
        var casterCount = casterIds.Count;
        var orderedContributors = contributors
            .OrderByDescending(contributor => contributor.EstimatedContributionWeight)
            .ThenBy(contributor => contributor.DistanceToObjective)
            .Take(12)
            .ToArray();
        return new ObjectiveFocusWork(
            attackerCount,
            friendlyAttackers,
            enemyAttackers,
            casterCount,
            attackerCount >= FocusedObjectiveTargetThreshold || casterCount >= FocusedObjectiveCastThreshold,
            orderedContributors,
            BuildObjectiveContributionSummary(orderedContributors, attackerCount, casterCount),
            "未读取到全场仇恨表；当前使用目标锁定/读条/距离估算",
            sourceNames.ToArray());
    }

    private ObjectiveHistoryEntry TouchObjectiveHistory(string id, int? hpPercent, uint? currentHp, long now)
    {
        if (!objectiveHistory.TryGetValue(id, out var entry))
        {
            entry = new ObjectiveHistoryEntry(now);
            objectiveHistory[id] = entry;
        }

        entry.LastSeenTicks = now;
        if (hpPercent.HasValue)
        {
            if (entry.LastHpPercent.HasValue && hpPercent.Value < entry.LastHpPercent.Value)
                entry.LastDamageTicks = now;

            entry.LastHpPercent = hpPercent.Value;
        }

        if (currentHp.HasValue)
        {
            if (entry.LastCurrentHp.HasValue && currentHp.Value < entry.LastCurrentHp.Value)
            {
                var elapsedSeconds = Math.Max(0.001f, (now - entry.LastHpSampleTicks) / 1000f);
                entry.RecentHpLoss = entry.LastCurrentHp.Value - currentHp.Value;
                entry.RecentHpLossPerSecond = entry.RecentHpLoss.Value / elapsedSeconds;
                entry.LastDamageTicks = now;
            }
            else if (entry.LastDamageTicks > 0 && now - entry.LastDamageTicks > ObjectiveRecentDamageWindowMs)
            {
                entry.RecentHpLoss = null;
                entry.RecentHpLossPerSecond = 0f;
            }

            entry.LastCurrentHp = currentHp.Value;
            entry.LastHpSampleTicks = now;
        }

        return entry;
    }

    private void PruneObjectiveHistory(long now)
    {
        List<string>? staleKeys = null;
        foreach (var pair in objectiveHistory)
        {
            if (now - pair.Value.LastSeenTicks <= ObjectiveHistoryExpiryMs)
                continue;

            staleKeys ??= new List<string>(4);
            staleKeys.Add(pair.Key);
        }

        if (staleKeys == null)
            return;

        foreach (var key in staleKeys)
            objectiveHistory.Remove(key);
    }

    private static ObjectiveFocusWork BuildObjectiveFocus(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyCollection<ulong> actorGameObjectIds,
        Vector3 objectivePosition,
        BattlefieldMapObjectiveCategory category)
    {
        if (actorGameObjectIds.Count == 0)
            return ObjectiveFocusWork.Empty;

        var actorIds = actorGameObjectIds.ToHashSet();
        var attackerIds = new HashSet<ulong>();
        var casterIds = new HashSet<ulong>();
        var sourceNames = new List<string>(8);
        var friendlyAttackers = 0;
        var enemyAttackers = 0;
        var contributors = new List<BattlefieldObjectiveContributionSnapshot>(16);

        foreach (var player in players)
        {
            if (player.IsDead)
                continue;

            var targetsObjective = IsValidGameObjectId(player.TargetObjectId) && actorIds.Contains(player.TargetObjectId);
            var castsAtObjective = player.IsCasting && IsValidGameObjectId(player.CastTargetObjectId) && actorIds.Contains(player.CastTargetObjectId);
            if (!targetsObjective && !castsAtObjective)
                continue;

            var distance = Distance2D(player.Position, objectivePosition);
            var estimatedWeight = (targetsObjective ? 1f : 0f)
                + (castsAtObjective ? 1.5f : 0f)
                + (distance <= 35f ? 0.25f : 0f);
            var evidence = BuildObjectiveContributionEvidence(targetsObjective, castsAtObjective, category);
            if (targetsObjective)
                attackerIds.Add(player.GameObjectId);
            if (castsAtObjective)
                casterIds.Add(player.GameObjectId);

            if (IsFriendlySide(player.Relation))
                friendlyAttackers++;
            else if (player.Relation == BattlefieldPlayerRelation.Enemy)
                enemyAttackers++;

            if (sourceNames.Count < 8)
            {
                var label = FormatSourceLabel(player);
                if (!sourceNames.Contains(label, StringComparer.Ordinal))
                    sourceNames.Add(label);
            }

            contributors.Add(new BattlefieldObjectiveContributionSnapshot(
                player.GameObjectId,
                player.Name,
                player.Relation,
                NormalizeBattalion(player.Battalion),
                ResolveJobInfo(player.ClassJobId).Name,
                targetsObjective,
                castsAtObjective,
                distance,
                null,
                null,
                estimatedWeight,
                evidence));
        }

        var attackerCount = attackerIds.Count;
        var casterCount = casterIds.Count;
        var orderedContributors = contributors
            .OrderByDescending(contributor => contributor.EstimatedContributionWeight)
            .ThenBy(contributor => contributor.DistanceToObjective)
            .Take(12)
            .ToArray();
        return new ObjectiveFocusWork(
            attackerCount,
            friendlyAttackers,
            enemyAttackers,
            casterCount,
            attackerCount >= FocusedObjectiveTargetThreshold || casterCount >= FocusedObjectiveCastThreshold,
            orderedContributors,
            BuildObjectiveContributionSummary(orderedContributors, attackerCount, casterCount),
            "未读取到全场仇恨表；当前使用目标锁定/读条/距离估算",
            sourceNames.ToArray());
    }

    private static string BuildObjectiveContributionEvidence(bool targetsObjective, bool castsAtObjective, BattlefieldMapObjectiveCategory category)
    {
        var parts = new List<string>(3);
        if (targetsObjective)
            parts.Add("目标锁定");
        if (castsAtObjective)
            parts.Add("读条指向");
        if (category == BattlefieldMapObjectiveCategory.Ice)
            parts.Add("冰仇恨估算");

        return parts.Count == 0 ? "估算" : string.Join("+", parts);
    }

    private static string BuildObjectiveContributionSummary(
        IReadOnlyList<BattlefieldObjectiveContributionSnapshot> contributors,
        int attackerCount,
        int casterCount)
    {
        if (contributors.Count == 0)
            return "暂无可见打目标玩家";

        var friendly = contributors.Count(contributor => IsFriendlySide(contributor.Relation));
        var enemy = contributors.Count(contributor => contributor.Relation == BattlefieldPlayerRelation.Enemy);
        var unknown = contributors.Count - friendly - enemy;
        var top = string.Join("，", contributors.Take(4).Select(contributor => $"{contributor.PlayerName}({contributor.JobName})"));
        var unknownText = unknown > 0 ? $" 未知:{unknown}" : string.Empty;
        return $"锁定:{attackerCount} 读条:{casterCount} 我方:{friendly} 敌方:{enemy}{unknownText}；主力 {top}";
    }

    private static int? ResolveObjectiveRemainingSeconds(
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        BattlefieldMapObjectiveCategory category,
        BattlefieldMapObjectiveState state,
        string rankName,
        int? countdownSeconds,
        long firstSeenTicks,
        long now,
        out string source)
    {
        if (countdownSeconds.HasValue)
        {
            source = "地图倒计时";
            return countdownSeconds.Value;
        }

        var duration = ResolveObjectiveDurationSeconds(mapKnowledge, category, state, rankName);
        if (!duration.HasValue || state is BattlefieldMapObjectiveState.Warning or BattlefieldMapObjectiveState.Inactive or BattlefieldMapObjectiveState.Destroyed)
        {
            source = "未知";
            return null;
        }

        var elapsedSeconds = (int)Math.Max(0, (now - firstSeenTicks) / 1000);
        if (elapsedSeconds > duration.Value + 5)
        {
            source = "已超出估算窗口";
            return null;
        }

        source = "瑙勫垯估算";
        return Math.Max(0, duration.Value - elapsedSeconds);
    }

    private static int? ResolveObjectiveDurationSeconds(
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        BattlefieldMapObjectiveCategory category,
        BattlefieldMapObjectiveState state,
        string rankName)
    {
        if (category == BattlefieldMapObjectiveCategory.Ice)
            return null;

        if (mapKnowledge != null && !string.IsNullOrWhiteSpace(rankName))
        {
            var rule = mapKnowledge.ObjectiveRankScores.FirstOrDefault(item => string.Equals(item.RankName, rankName, StringComparison.OrdinalIgnoreCase));
            if (rule.ActiveDurationSeconds > 0)
                return rule.ActiveDurationSeconds;
        }

        return category switch
        {
            BattlefieldMapObjectiveCategory.Tomelith => 120,
            BattlefieldMapObjectiveCategory.Ovoo => 30,
            BattlefieldMapObjectiveCategory.StrategicPoint => state == BattlefieldMapObjectiveState.Controlled ? 30 : null,
            _ => null,
        };
    }

    private static string BuildObjectiveId(
        FrontlineMapType mapType,
        BattlefieldMapObjectiveCategory category,
        uint iconId,
        Vector3 position,
        string locationId)
    {
        if (!string.IsNullOrWhiteSpace(locationId))
            return $"map:{mapType}:{category}:{locationId}:{iconId}";

        return $"map:{mapType}:{category}:{iconId}:{MathF.Round(position.X)}:{MathF.Round(position.Z)}";
    }

    private static string BuildObjectiveName(
        FrontlineMapType mapType,
        BattlefieldMapObjectiveCategory category,
        string rankName,
        string locationId,
        string tooltip,
        string? actorName)
    {
        var preferredName = ResolveObjectiveDisplayName(category, tooltip, actorName);
        var baseName = string.IsNullOrWhiteSpace(preferredName) ? category switch
        {
            BattlefieldMapObjectiveCategory.Base => "据点",
            BattlefieldMapObjectiveCategory.Tomelith => "亚拉戈石文",
            BattlefieldMapObjectiveCategory.Ice => "冰封的石文书",
            BattlefieldMapObjectiveCategory.Ovoo => "无垢的大地",
            BattlefieldMapObjectiveCategory.StrategicPoint => "战略目标点",
            BattlefieldMapObjectiveCategory.Monster => "机制目标",
            _ => "地图目标",
        } : preferredName;

        var rankPrefix = string.Empty;
        if (!string.IsNullOrWhiteSpace(rankName)
            && (string.IsNullOrWhiteSpace(preferredName) || !ContainsRankToken(preferredName, rankName)))
        {
            rankPrefix = category switch
            {
                BattlefieldMapObjectiveCategory.Ice => $"{rankName} ",
                BattlefieldMapObjectiveCategory.Tomelith or BattlefieldMapObjectiveCategory.Ovoo or BattlefieldMapObjectiveCategory.StrategicPoint => $"{rankName}级",
                _ => $"{rankName} 点"
            };
        }

        var locationSuffix = string.IsNullOrWhiteSpace(preferredName)
            && category == BattlefieldMapObjectiveCategory.StrategicPoint
            && !string.IsNullOrWhiteSpace(locationId)
                ? $" {locationId}"
                : string.Empty;
        var name = $"{rankPrefix}{baseName}{locationSuffix}".Trim();

        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(tooltip))
            name = tooltip.Length > 32 ? tooltip[..32] : tooltip;

        return string.IsNullOrWhiteSpace(name) ? $"{mapType} 目标" : name;
    }

    private static string ResolveObjectiveDisplayName(
        BattlefieldMapObjectiveCategory category,
        string tooltip,
        string? actorName)
    {
        foreach (var candidate in new[] { actorName, tooltip })
        {
            var cleaned = CleanObjectiveDisplayName(candidate);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;
            if (IsGenericObjectiveDisplayName(cleaned, category))
                continue;

            return cleaned;
        }

        if (category is BattlefieldMapObjectiveCategory.Ice or BattlefieldMapObjectiveCategory.Monster)
            return CleanObjectiveDisplayName(actorName);

        return string.Empty;
    }

    private static string CleanObjectiveDisplayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Regex.Replace(value.Trim(), @"\s+", " ");
    }

    private static bool IsGenericObjectiveDisplayName(string value, BattlefieldMapObjectiveCategory category)
    {
        var normalized = Regex.Replace(value, @"\s+", string.Empty)
            .Replace("亚拉戈石文书", "亚拉戈石文", StringComparison.Ordinal)
            .Replace("冰封的石文书", "冰封石文", StringComparison.Ordinal)
            .Replace("戰略目標點", "战略目标点", StringComparison.Ordinal)
            .Replace("亞拉戈石文", "亚拉戈石文", StringComparison.Ordinal)
            .Replace("無垢的大地", "无垢的大地", StringComparison.Ordinal)
            .Replace("據點", "据点", StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, @"^[SAB](级|點|点)?", string.Empty, RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"(0?[1-9]|1[0-5])号?$", string.Empty);

        return category switch
        {
            BattlefieldMapObjectiveCategory.Base => normalized == "据点",
            BattlefieldMapObjectiveCategory.Tomelith => normalized == "亚拉戈石文" || normalized == "allagantomelith",
            BattlefieldMapObjectiveCategory.Ice => normalized == "冰封石文" || normalized == "iceboundtomelith" || normalized == "ice",
            BattlefieldMapObjectiveCategory.Ovoo => normalized == "无垢的大地" || normalized == "ovoos" || normalized == "ovo",
            BattlefieldMapObjectiveCategory.StrategicPoint => normalized == "战略目标点" || normalized == "战略目标" || normalized == "目标点" || normalized == "strategictarget" || normalized == "tacticaltarget",
            BattlefieldMapObjectiveCategory.Monster => normalized == "机制目标",
            _ => normalized == "地图目标",
        };
    }

    private static string BuildObjectiveSourceText(BattlefieldMapEventSnapshot mapEvent, BattlefieldObjectiveActorSnapshot? actor, bool wasRecentlyDamaged)
    {
        var parts = new List<string> { $"地图事件/{MapEventKindName(mapEvent.Kind)}" };
        if (!string.IsNullOrWhiteSpace(mapEvent.Tooltip))
            parts.Add($"鎻愮ず:{mapEvent.Tooltip}");
        if (actor.HasValue)
            parts.Add($"瀵硅薄:{actor.Value.Name}");
        if (wasRecentlyDamaged)
            parts.Add("近期血量下降");
        return string.Join("；", parts);
    }

    private static float CalculateObjectiveConfidence(
        FrontlineMapType mapType,
        BattlefieldMapEventSnapshot mapEvent,
        BattlefieldObjectiveActorSnapshot? actor,
        string rankName,
        NodeOwnership? ownership,
        int? hpPercent,
        int? remainingSeconds)
    {
        var confidence = 0.35f;
        if (mapType != FrontlineMapType.Unknown)
            confidence += 0.12f;
        if (mapEvent.IconId != 0)
            confidence += 0.16f;
        if (!string.IsNullOrWhiteSpace(mapEvent.Tooltip))
            confidence += 0.10f;
        if (!string.IsNullOrWhiteSpace(rankName))
            confidence += 0.10f;
        if (ownership.HasValue)
            confidence += 0.08f;
        if (hpPercent.HasValue || remainingSeconds.HasValue)
            confidence += 0.08f;
        if (actor.HasValue)
            confidence += 0.16f;

        return Math.Clamp(confidence, 0f, 1f);
    }

    private static float CalculateActorObjectiveConfidence(FrontlineMapType mapType, BattlefieldObjectiveActorSnapshot actor, string rankName, int? scoreValue)
    {
        var confidence = 0.45f;
        if (mapType != FrontlineMapType.Unknown)
            confidence += 0.12f;
        if (!string.IsNullOrWhiteSpace(actor.Name))
            confidence += 0.16f;
        if (actor.MaxHp > 0)
            confidence += 0.12f;
        if (!string.IsNullOrWhiteSpace(rankName))
            confidence += 0.08f;
        if (scoreValue.HasValue)
            confidence += 0.07f;

        return Math.Clamp(confidence, 0f, 1f);
    }

    private static bool TryParseLocationId(string text, out string locationId)
    {
        locationId = string.Empty;
        if (!ContainsAny(text, "点", "目標", "目标", "石文", "无垢", "大地", "冰封", "据点", "战略", "node", "point"))
            return false;

        var match = LocationIdRegex.Match(text);
        if (!match.Success)
            return false;

        locationId = NormalizeLocationId(match.Groups[1].Value);
        return true;
    }

    private static string NormalizeLocationId(string value)
    {
        if (!int.TryParse(value, out var number))
            return value;

        return number is >= 0 and < 100 ? number.ToString("D2") : value;
    }

    private static string BuildOwnershipText(NodeOwnership? ownership, byte? localBattalion)
    {
        if (!ownership.HasValue)
            return "未知";

        if (ownership.Value == NodeOwnership.Neutral)
            return "中立";

        var battalion = OwnershipToBattalion(ownership.Value);
        var name = AllianceName(battalion);
        if (!localBattalion.HasValue || !battalion.HasValue)
            return name;

        return battalion.Value == localBattalion.Value ? $"{name}(我方)" : $"{name}(敌方)";
    }

    private static byte? OwnershipToBattalion(NodeOwnership ownership)
        => ownership switch
        {
            NodeOwnership.Maelstrom => 0,
            NodeOwnership.TwinAdder => 1,
            NodeOwnership.ImmortalFlames => 2,
            _ => null,
        };

    private static bool ContainsRankToken(string text, string rank)
        => ContainsAny(text, $"{rank}点", $"{rank} 点", $"{rank}级", $"{rank} 级", $"等级{rank}", $"等级 {rank}", $"rank {rank}");

    private static bool IsIceObjectiveName(string name)
        => ContainsAny(name, "冰封", "冰块", "大冰", "小冰", "icebound", "icebound tomelith", "ice");

    private static bool IsLikelyIceHp(uint maxHp)
        => maxHp is >= 250000 and <= 350000 or >= 2500000 and <= 3500000;

    private static bool ContainsAny(string text, params string[] needles)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int RankSortValue(string rankName)
        => rankName switch
        {
            "S" => 4,
            "A" => 3,
            "B" => 2,
            "大" => 3,
            "小" => 2,
            _ => 0,
        };

    private static BattlefieldPlayerClusterSnapshot[] BuildPlayerClusters(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var candidates = players
            .Where(player => player.Relation is BattlefieldPlayerRelation.Friendly or BattlefieldPlayerRelation.Enemy or BattlefieldPlayerRelation.Unknown)
            .Where(player => player.Relation != BattlefieldPlayerRelation.LocalPlayer)
            .ToArray();
        if (candidates.Length == 0)
            return Array.Empty<BattlefieldPlayerClusterSnapshot>();

        var clusters = new List<BattlefieldPlayerClusterSnapshot>(8);
        var visited = new bool[candidates.Length];
        var radiusSquared = PlayerClusterRadius * PlayerClusterRadius;

        for (var i = 0; i < candidates.Length; i++)
        {
            if (visited[i])
                continue;

            var relation = candidates[i].Relation;
            var battalion = NormalizeBattalion(candidates[i].Battalion);
            var memberIndexes = new List<int>(16);
            var queue = new Queue<int>();
            visited[i] = true;
            queue.Enqueue(i);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                memberIndexes.Add(current);

                for (var next = 0; next < candidates.Length; next++)
                {
                    if (visited[next]
                        || candidates[next].Relation != relation
                        || NormalizeBattalion(candidates[next].Battalion) != battalion)
                        continue;

                    if (DistanceSquared2D(candidates[current].Position, candidates[next].Position) > radiusSquared)
                        continue;

                    visited[next] = true;
                    queue.Enqueue(next);
                }
            }

            clusters.Add(CreateCluster(candidates, memberIndexes, relation, battalion, localPlayer));
        }

        return clusters
            .OrderByDescending(cluster => cluster.PlayerCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .ToArray();
    }

    private static BattlefieldPlayerClusterSnapshot CreateCluster(
        IReadOnlyList<BattlefieldPlayerSnapshot> candidates,
        IReadOnlyList<int> memberIndexes,
        BattlefieldPlayerRelation relation,
        byte? battalion,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var center = Vector3.Zero;
        var deadCount = 0;
        var castingCount = 0;
        foreach (var index in memberIndexes)
        {
            var player = candidates[index];
            center += player.Position;
            if (player.IsDead)
                deadCount++;
            if (player.IsCasting)
                castingCount++;
        }

        center /= Math.Max(1, memberIndexes.Count);
        var distanceToLocal = localPlayer.HasValue ? Vector3.Distance(localPlayer.Value.Position, center) : 0f;
        return new BattlefieldPlayerClusterSnapshot(relation, battalion, center, memberIndexes.Count, deadCount, castingCount, distanceToLocal);
    }

    private static float DistanceSquared2D(Vector3 a, Vector3 b)
    {
        var x = a.X - b.X;
        var z = a.Z - b.Z;
        return x * x + z * z;
    }

    private BattlefieldScoreSituationSnapshot BuildScoreSituation(
        FrontlineSnapshot scoreSnapshot,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        FrontlineMapType mapType,
        byte? localBattalion,
        long now)
    {
        var victoryScore = ResolveVictoryScore(mapType, mapKnowledge, scoreSnapshot.Alliances);
        var rawAlliances = NormalizeScoreAlliances(scoreSnapshot.Alliances);
        var current = new ScoreHistoryEntry(
            now,
            GetAllianceScore(rawAlliances, 1),
            GetAllianceScore(rawAlliances, 2),
            GetAllianceScore(rawAlliances, 3));
        var hasScoreData = scoreSnapshot.IsInFrontline && scoreSnapshot.HasScoreData && rawAlliances.Length >= 3;

        if (!scoreSnapshot.IsInFrontline)
        {
            scoreHistory.Clear();
        }
        else if (hasScoreData)
        {
            scoreHistory.Enqueue(current);
            while (scoreHistory.Count > 0 && now - scoreHistory.Peek().Ticks > ScoreTrendWindowMs)
                scoreHistory.Dequeue();
        }

        var baseline = scoreHistory.Count > 0 ? scoreHistory.Peek() : current;
        var elapsedSeconds = Math.Max(0f, (now - baseline.Ticks) / 1000f);
        var trendWindowSeconds = (int)MathF.Round(elapsedSeconds);
        var localAllianceId = BattalionToAllianceId(localBattalion);

        var ranked = rawAlliances
            .Select(alliance =>
            {
                var isLocalAlliance = localAllianceId.HasValue && alliance.AllianceId == localAllianceId.Value;
                var relation = ResolveAllianceRelation(alliance.AllianceId, localAllianceId);
                var delta = GetScoreDelta(current, baseline, alliance.AllianceId);
                var speed = elapsedSeconds >= 1f ? delta / elapsedSeconds : 0f;
                return new BattlefieldAllianceScoreSnapshot(
                    alliance.AllianceId,
                    AllianceIdToBattalion(alliance.AllianceId),
                    GetAllianceName(alliance),
                    relation,
                    isLocalAlliance,
                    alliance.Score,
                    victoryScore,
                    0,
                    string.Empty,
                    false,
                    trendWindowSeconds,
                    delta,
                    speed);
            })
            .OrderByDescending(alliance => alliance.Score)
            .ThenBy(alliance => alliance.AllianceId)
            .Select((alliance, index) => alliance with
            {
                RankIndex = index + 1,
                RankText = GetScoreRankText(index),
                IsLeading = hasScoreData && index == 0
            })
            .ToArray();

        var rankByAllianceId = ranked.ToDictionary(alliance => alliance.AllianceId);
        var alliances = rawAlliances
            .Select(alliance => rankByAllianceId[alliance.AllianceId])
            .ToArray();

        var friendlyAlliance = localAllianceId.HasValue
            ? alliances.Where(alliance => alliance.AllianceId == localAllianceId.Value).Select(alliance => (BattlefieldAllianceScoreSnapshot?)alliance).FirstOrDefault()
            : null;
        var enemyAlliances = localAllianceId.HasValue
            ? ranked.Where(alliance => alliance.AllianceId != localAllianceId.Value).ToArray()
            : Array.Empty<BattlefieldAllianceScoreSnapshot>();

        return new BattlefieldScoreSituationSnapshot
        {
            HasScoreData = hasScoreData,
            MapType = mapType,
            MapName = mapKnowledge?.Name ?? string.Empty,
            VictoryScore = victoryScore,
            Alliances = alliances,
            RankedAlliances = ranked,
            FriendlyAlliance = friendlyAlliance,
            EnemyAlliance1 = enemyAlliances.Length > 0 ? enemyAlliances[0] : null,
            EnemyAlliance2 = enemyAlliances.Length > 1 ? enemyAlliances[1] : null,
            SummaryText = BuildScoreSummaryText(friendlyAlliance, enemyAlliances, ranked, victoryScore, hasScoreData)
        };
    }

    private static AllianceData[] BuildAllianceData(BattlefieldScoreSituationSnapshot scoreSituation, IReadOnlyList<AllianceData> fallbackAlliances)
    {
        if (scoreSituation.Alliances.Length == 0)
            return fallbackAlliances.ToArray();

        var fallbackByAllianceId = fallbackAlliances
            .GroupBy(alliance => alliance.AllianceId)
            .ToDictionary(group => group.Key, group => group.First());

        return scoreSituation.Alliances
            .Select(alliance =>
            {
                fallbackByAllianceId.TryGetValue(alliance.AllianceId, out var fallback);
                return new AllianceData
                {
                    AllianceId = alliance.AllianceId,
                    Score = alliance.Score,
                    TargetScore = alliance.VictoryScore > 0 ? alliance.VictoryScore : fallback.TargetScore,
                    Name = alliance.Name,
                    IsPlayerAlliance = alliance.IsLocalAlliance,
                    IsLeading = alliance.IsLeading
                };
            })
            .ToArray();
    }

    private static AllianceData[] NormalizeScoreAlliances(IReadOnlyList<AllianceData> alliances)
    {
        var byAllianceId = alliances
            .Where(alliance => alliance.AllianceId is >= 1 and <= 3)
            .GroupBy(alliance => alliance.AllianceId)
            .ToDictionary(group => group.Key, group => group.First());

        return new[] { 1u, 2u, 3u }
            .Select(allianceId =>
            {
                if (byAllianceId.TryGetValue(allianceId, out var alliance))
                    return alliance;

                return new AllianceData
                {
                    AllianceId = allianceId,
                    Score = 0,
                    TargetScore = 0,
                    Name = AllianceName(AllianceIdToBattalion(allianceId)),
                    IsPlayerAlliance = false,
                    IsLeading = false
                };
            })
            .ToArray();
    }

    private static int ResolveVictoryScore(FrontlineMapType mapType, FrontlineMapKnowledgeSnapshot? mapKnowledge, IReadOnlyList<AllianceData> alliances)
    {
        var staticVictoryScore = mapType switch
        {
            FrontlineMapType.BorderlandRuinsSecure => 2400,
            FrontlineMapType.SealRock => 700,
            FrontlineMapType.FieldsOfHonor => 1600,
            FrontlineMapType.OnsalHakair => 1400,
            FrontlineMapType.Vochester => 1400,
            _ => 0
        };
        if (staticVictoryScore > 0)
            return staticVictoryScore;

        if (mapKnowledge?.VictoryScore > 0)
            return mapKnowledge.VictoryScore;

        return alliances
            .Select(alliance => alliance.TargetScore)
            .Where(score => score > 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static string BuildScoreSummaryText(
        BattlefieldAllianceScoreSnapshot? friendlyAlliance,
        IReadOnlyList<BattlefieldAllianceScoreSnapshot> enemyAlliances,
        IReadOnlyList<BattlefieldAllianceScoreSnapshot> rankedAlliances,
        int victoryScore,
        bool hasScoreData)
    {
        if (!hasScoreData)
            return "比分态势等待可靠比分来源首次稳定读数";

        var rankText = rankedAlliances.Count == 0
            ? "老一/老二/老三暂未形成"
            : string.Join("；", rankedAlliances.Select(alliance => $"{alliance.RankText} {alliance.Name} {FormatScoreWithVictory(alliance.Score, victoryScore)}"));

        if (!friendlyAlliance.HasValue)
            return $"尚未识别我方阵营，{rankText}";

        var friendly = friendlyAlliance.Value;
        var parts = new List<string>
        {
            $"我方 {friendly.Name} {FormatScoreWithVictory(friendly.Score, victoryScore)}（{friendly.RankText}，{FormatTrend(friendly)}）"
        };

        if (enemyAlliances.Count > 0)
            parts.Add($"敌方1 {FormatAllianceScore(enemyAlliances[0], victoryScore)}");
        if (enemyAlliances.Count > 1)
            parts.Add($"敌方2 {FormatAllianceScore(enemyAlliances[1], victoryScore)}");

        parts.Add(rankText);
        return string.Join("；", parts);
    }

    private static string FormatAllianceScore(BattlefieldAllianceScoreSnapshot alliance, int victoryScore)
        => $"{alliance.Name} {FormatScoreWithVictory(alliance.Score, victoryScore)}（{alliance.RankText}，{FormatTrend(alliance)}）";

    private static string FormatScoreWithVictory(int score, int victoryScore)
        => victoryScore > 0 ? $"{score}/{victoryScore}" : score.ToString();

    private static string FormatTrend(BattlefieldAllianceScoreSnapshot alliance)
        => $"近30秒 {FormatSignedNumber(alliance.ScoreDelta30s)}，{alliance.ScorePerSecond30s:0.0}/秒";

    private static string FormatSignedNumber(int value)
        => value > 0 ? $"+{value}" : value.ToString();

    private static int GetAllianceScore(IReadOnlyList<AllianceData> alliances, uint allianceId)
        => alliances.FirstOrDefault(alliance => alliance.AllianceId == allianceId).Score;

    private static int GetScoreDelta(ScoreHistoryEntry current, ScoreHistoryEntry baseline, uint allianceId)
        => GetScoreFromHistory(current, allianceId) - GetScoreFromHistory(baseline, allianceId);

    private static int GetScoreFromHistory(ScoreHistoryEntry entry, uint allianceId)
        => allianceId switch
        {
            1 => entry.MaelstromScore,
            2 => entry.TwinAdderScore,
            3 => entry.ImmortalFlamesScore,
            _ => 0
        };

    private static uint? BattalionToAllianceId(byte? battalion)
        => battalion.HasValue && IsFrontlineBattalion(battalion.Value) ? (uint)(battalion.Value + 1) : null;

    private static byte? AllianceIdToBattalion(uint allianceId)
        => allianceId is >= 1 and <= 3 ? (byte)(allianceId - 1) : null;

    private static string GetAllianceName(AllianceData alliance)
        => string.IsNullOrWhiteSpace(alliance.Name)
            ? AllianceName(AllianceIdToBattalion(alliance.AllianceId))
            : alliance.Name;

    private static BattlefieldPlayerRelation ResolveAllianceRelation(uint allianceId, uint? localAllianceId)
    {
        if (!localAllianceId.HasValue)
            return BattlefieldPlayerRelation.Unknown;

        return allianceId == localAllianceId.Value
            ? BattlefieldPlayerRelation.Friendly
            : BattlefieldPlayerRelation.Enemy;
    }

    private static string GetScoreRankText(int rankIndex)
        => rankIndex switch
        {
            0 => "老一",
            1 => "老二",
            2 => "老三",
            _ => string.Empty,
        };

    private static BattlefieldTimeSituationSnapshot BuildTimeSituation(
        FrontlineSnapshot scoreSnapshot,
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> mapObjectives,
        BattlefieldAnnouncementSituationSnapshot announcements)
    {
        var hasMatchTime = scoreSnapshot.IsInFrontline && scoreSnapshot.HasMatchTime && scoreSnapshot.MatchTimeRemaining is >= 0 and <= MatchDurationSeconds;
        var remainingSeconds = hasMatchTime ? scoreSnapshot.MatchTimeRemaining : 0;
        var elapsedSeconds = hasMatchTime ? Math.Clamp(MatchDurationSeconds - remainingSeconds, 0, MatchDurationSeconds) : 0;
        var (phaseName, phaseDetail) = ResolveGenericMatchPhase(elapsedSeconds, hasMatchTime);
        var mapPhase = ResolveMapRulePhase(mapKnowledge, elapsedSeconds, hasMatchTime);
        var resourceTimer = ResolveNextResourceTimer(mapKnowledge, mapObjectives, announcements, elapsedSeconds, hasMatchTime);

        return new BattlefieldTimeSituationSnapshot
        {
            HasMatchTime = hasMatchTime,
            MatchTimeRemainingSeconds = remainingSeconds,
            MatchElapsedSeconds = elapsedSeconds,
            MatchPhaseName = phaseName,
            MatchPhaseDetail = phaseDetail,
            MapRulePhaseName = mapPhase?.Name ?? string.Empty,
            MapRuleMaxActiveObjectives = mapPhase?.MaxActiveObjectives,
            MapRuleMinimumObjectiveRank = mapPhase?.MinimumObjectiveRank ?? string.Empty,
            NextResourceSeconds = resourceTimer.Seconds,
            NextResourceName = resourceTimer.Name,
            NextResourceSource = resourceTimer.Source,
            SummaryText = BuildTimeSummaryText(hasMatchTime, remainingSeconds, elapsedSeconds, phaseName, mapPhase, resourceTimer)
        };
    }

    private static (string Name, string Detail) ResolveGenericMatchPhase(int elapsedSeconds, bool hasMatchTime)
    {
        if (!hasMatchTime)
            return ("未知阶段", "等待对局倒计时读数");

        return elapsedSeconds switch
        {
            < 180 => ("开局", "已进行不足 3 分钟"),
            < 720 => ("中期", "已进行 3-12 分钟"),
            _ => ("终局", "已进行 12 分钟以上")
        };
    }

    private static FrontlineMapPhaseRuleSnapshot? ResolveMapRulePhase(
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        int elapsedSeconds,
        bool hasMatchTime)
    {
        if (!hasMatchTime || mapKnowledge == null)
            return null;

        return mapKnowledge.PhaseRules
            .Where(rule => elapsedSeconds >= rule.StartElapsedSeconds && (!rule.EndElapsedSeconds.HasValue || elapsedSeconds < rule.EndElapsedSeconds.Value))
            .Select(rule => (FrontlineMapPhaseRuleSnapshot?)rule)
            .FirstOrDefault();
    }

    private static ResourceTimerWork ResolveNextResourceTimer(
        FrontlineMapKnowledgeSnapshot? mapKnowledge,
        IReadOnlyList<BattlefieldMapObjectiveSnapshot> mapObjectives,
        BattlefieldAnnouncementSituationSnapshot announcements,
        int elapsedSeconds,
        bool hasMatchTime)
    {
        var objectiveTimer = mapObjectives
            .Where(objective => objective.RemainingSeconds.HasValue)
            .OrderBy(objective => ResourceStatePriority(objective.State))
            .ThenBy(objective => objective.RemainingSeconds!.Value)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(objectiveTimer.Id) && objectiveTimer.RemainingSeconds.HasValue)
        {
            var name = string.IsNullOrWhiteSpace(objectiveTimer.RankName)
                ? objectiveTimer.Name
                : $"{objectiveTimer.Name}({objectiveTimer.RankName})";
            return new ResourceTimerWork(
                objectiveTimer.RemainingSeconds.Value,
                name,
                $"{MapObjectiveStateText(objectiveTimer.State)} / {objectiveTimer.RemainingSource}");
        }

        var announcementTimer = announcements.RecentAnnouncements
            .Where(announcement => announcement.RemainingSeconds.HasValue)
            .Where(announcement => announcement.Kind is BattlefieldAnnouncementKind.ObjectiveWarning
                or BattlefieldAnnouncementKind.ObjectiveAvailable
                or BattlefieldAnnouncementKind.WeatherWarning)
            .OrderBy(announcement => announcement.RemainingSeconds!.Value)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(announcementTimer.Text) && announcementTimer.RemainingSeconds.HasValue)
        {
            return new ResourceTimerWork(
                announcementTimer.RemainingSeconds.Value,
                announcementTimer.SummaryText,
                $"战场通告 / {announcementTimer.Source}");
        }

        if (hasMatchTime && mapKnowledge != null)
        {
            var staticTimer = mapKnowledge.TimedSpawns
                .Where(spawn => spawn.FirstSpawnOffsetSeconds.HasValue && spawn.FirstSpawnOffsetSeconds.Value >= elapsedSeconds)
                .OrderBy(spawn => spawn.FirstSpawnOffsetSeconds!.Value)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(staticTimer.Id) && staticTimer.FirstSpawnOffsetSeconds.HasValue)
            {
                return new ResourceTimerWork(
                    staticTimer.FirstSpawnOffsetSeconds.Value - elapsedSeconds,
                    staticTimer.Name,
                    "地图规则首刷估算");
            }
        }

        return new ResourceTimerWork(null, string.Empty, "未读取到资源倒计时");
    }

    private static int ResourceStatePriority(BattlefieldMapObjectiveState state)
        => state switch
        {
            BattlefieldMapObjectiveState.Warning => 0,
            BattlefieldMapObjectiveState.Active => 1,
            BattlefieldMapObjectiveState.Contested => 2,
            BattlefieldMapObjectiveState.Controlled => 3,
            _ => 4
        };

    private static string MapObjectiveStateText(BattlefieldMapObjectiveState state)
        => state switch
        {
            BattlefieldMapObjectiveState.Inactive => "未激活",
            BattlefieldMapObjectiveState.Warning => "棰勫憡",
            BattlefieldMapObjectiveState.Active => "可处理",
            BattlefieldMapObjectiveState.Controlled => "已归属",
            BattlefieldMapObjectiveState.Contested => "争夺中",
            BattlefieldMapObjectiveState.Destroyed => "已破坏",
            _ => "未知",
        };

    private static string BuildTimeSummaryText(
        bool hasMatchTime,
        int remainingSeconds,
        int elapsedSeconds,
        string phaseName,
        FrontlineMapPhaseRuleSnapshot? mapPhase,
        ResourceTimerWork resourceTimer)
    {
        if (!hasMatchTime)
            return "时间态势等待对局开始锚点";

        var mapPhaseText = mapPhase.HasValue
            ? $"地图阶段 {mapPhase.Value.Name}"
            : "地图阶段未命中规则";
        var resourceText = resourceTimer.Seconds.HasValue
            ? $"下一资源 {resourceTimer.Name} {FormatDuration(resourceTimer.Seconds.Value)}（{resourceTimer.Source}）"
            : $"下一资源 {resourceTimer.Source}";

        return $"剩余 {FormatDuration(remainingSeconds)}，已进行 {FormatDuration(elapsedSeconds)}，{phaseName}，{mapPhaseText}，{resourceText}";
    }

    private static string FormatDuration(int seconds)
        => $"{Math.Max(0, seconds) / 60:D2}:{Math.Max(0, seconds) % 60:D2}";

    private BattlefieldTeamSituationSnapshot BuildTeamSituation(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldPlayerClusterSnapshot> playerClusters,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldLimitBreakThreatSituationSnapshot limitBreakThreats,
        BattlefieldKeySkillThreatSituationSnapshot keySkillThreats,
        BattlefieldPlayerSnapshot? localPlayer,
        long now)
    {
        var derived = cachedTeamDerivedAnalysis;
        var movement = BuildEnemyGroupMovement(derived.EnemyClusters, playerClusters, mapVisionClusters, now, derived.IsEnemySplit);
        return BuildTeamSituationFromDerived(scoreSituation, limitBreakThreats, keySkillThreats, derived, movement);
    }

    private static BattlefieldTeamSituationSnapshot BuildTeamSituationFromDerived(
        BattlefieldScoreSituationSnapshot scoreSituation,
        BattlefieldLimitBreakThreatSituationSnapshot limitBreakThreats,
        BattlefieldKeySkillThreatSituationSnapshot keySkillThreats,
        TeamDerivedAnalysisSnapshot derived,
        BattlefieldGroupMovementSnapshot movement)
    {
        var friendlyAlliance = FindAllianceSituation(derived.Alliances, scoreSituation.FriendlyAlliance);
        var enemyAlliance1 = FindAllianceSituation(derived.Alliances, scoreSituation.EnemyAlliance1);
        var enemyAlliance2 = FindAllianceSituation(derived.Alliances, scoreSituation.EnemyAlliance2);
        var friendlyPlayers = friendlyAlliance?.VisiblePlayers ?? Array.Empty<BattlefieldPlayerSnapshot>();
        var enemyAlliance1Players = enemyAlliance1?.VisiblePlayers ?? Array.Empty<BattlefieldPlayerSnapshot>();
        var enemyAlliance2Players = enemyAlliance2?.VisiblePlayers ?? Array.Empty<BattlefieldPlayerSnapshot>();

        return new BattlefieldTeamSituationSnapshot
        {
            Friendly = derived.Friendly,
            Enemy = derived.Enemy,
            Unknown = derived.Unknown,
            Alliances = derived.Alliances,
            FriendlyAlliance = friendlyAlliance,
            EnemyAlliance1 = enemyAlliance1,
            EnemyAlliance2 = enemyAlliance2,
            FriendlyPlayers = friendlyPlayers,
            EnemyAlliance1Players = enemyAlliance1Players,
            EnemyAlliance2Players = enemyAlliance2Players,
            EnemyClusters = derived.EnemyClusters,
            IsEnemySplit = derived.IsEnemySplit,
            EnemySplitSummaryText = derived.EnemySplitSummaryText,
            EnemyFocusTargets = derived.EnemyFocusTargets,
            FriendlyFocusTargets = derived.FriendlyFocusTargets,
            RespawnRhythm = derived.RespawnRhythm,
            EnemyMainGroupMovement = movement,
            LimitBreakThreats = limitBreakThreats,
            KeySkillThreats = keySkillThreats,
            SummaryText = BuildTacticalSummary(
                derived.Friendly,
                derived.Enemy,
                derived.EnemyFocusTargets,
                derived.FriendlyFocusTargets,
                derived.RespawnRhythm,
                movement,
                derived.EnemySplitSummaryText,
                limitBreakThreats,
                keySkillThreats)
        };
    }

    private static BattlefieldPlayerSnapshot[] BuildAlliancePlayers(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        byte? battalion)
        => battalion.HasValue
            ? players.Where(player => NormalizeBattalion(player.Battalion) == battalion).ToArray()
            : Array.Empty<BattlefieldPlayerSnapshot>();

    private static BattlefieldAllianceSituationSnapshot? FindAllianceSituation(
        IReadOnlyList<BattlefieldAllianceSituationSnapshot> situations,
        BattlefieldAllianceScoreSnapshot? scoreAlliance)
    {
        if (!scoreAlliance.HasValue)
            return null;

        var battalion = scoreAlliance.Value.Battalion;
        if (!battalion.HasValue)
            return null;

        return situations.FirstOrDefault(situation => situation.Battalion == battalion);
    }

    private static BattlefieldTeamSummarySnapshot BuildTeamSummary(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldPlayerClusterSnapshot> playerClusters,
        BattlefieldTacticalSide side)
    {
        var members = players.Where(player => IsSideMember(player.Relation, side)).ToArray();
        var mainCluster = playerClusters
            .Where(cluster => IsSideMember(cluster.Relation, side))
            .OrderByDescending(cluster => cluster.PlayerCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .Select(cluster => (BattlefieldPlayerClusterSnapshot?)cluster)
            .FirstOrDefault();

        return new BattlefieldTeamSummarySnapshot
        {
            Side = side,
            Name = side switch
            {
                BattlefieldTacticalSide.Friendly => "我方",
                BattlefieldTacticalSide.Enemy => "敌方",
                _ => "未知"
            },
            TotalCount = members.Length,
            AliveCount = members.Count(player => !player.IsDead),
            DeadCount = members.Count(player => player.IsDead),
            MountedCount = members.Count(player => player.IsMounted),
            InCombatCount = members.Count(player => player.IsInCombat),
            LowHpCount = members.Count(player => !player.IsDead && player.HpPercent > 0f && player.HpPercent <= LowHpThresholdPercent),
            CastingCount = members.Count(player => player.IsCasting),
            BattleHighCount = members.Count(player => player.BattleHighLevel > 0),
            BattleFeverCount = members.Count(player => player.IsBattleFever),
            MaxBattleHighLevel = members.Select(player => player.BattleHighLevel).DefaultIfEmpty(0).Max(),
            BattleHighTotalLevel = members.Sum(player => player.BattleHighLevel),
            GuardingCount = members.Count(player => player.IsGuarding),
            CrowdControlledCount = members.Count(player => player.IsCrowdControlled),
            ControlVulnerableCount = members.Count(player => player.IsControlVulnerable),
            InvulnerableCount = members.Count(player => player.IsInvulnerable),
            ExecutableCount = members.Count(player => player.IsExecutable),
            SnowBlessingCount = members.Count(player => player.HasSnowBlessing),
            NearCount = members.Count(player => player.DistanceToLocal <= 30f),
            MidCount = members.Count(player => player.DistanceToLocal > 30f && player.DistanceToLocal <= 60f),
            FarCount = members.Count(player => player.DistanceToLocal > 60f),
            RoleComposition = BuildRoleComposition(members),
            JobComposition = BuildJobComposition(members),
            MainCluster = mainCluster,
        };
    }

    private static BattlefieldAllianceSituationSnapshot[] BuildAllianceSituations(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldPlayerClusterSnapshot> playerClusters,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters)
    {
        var localPlayer = TryGetLocalPlayer(players);
        var localBattalion = localPlayer.HasValue ? NormalizeBattalion(localPlayer.Value.Battalion) : null;
        var battalions = new byte?[] { 0, 1, 2 };
        return battalions
            .Select(battalion => BuildAllianceSituation(players, playerClusters, mapVisionPoints, mapVisionClusters, battalion, localBattalion))
            .Where(summary => summary.VisiblePlayerCount > 0 || summary.MapVisionPointCount > 0 || summary.MainPlayerCluster.HasValue || summary.MainMapVisionCluster.HasValue)
            .OrderByDescending(summary => summary.IsLocalAlliance)
            .ThenBy(summary => summary.Battalion ?? byte.MaxValue)
            .ToArray();
    }

    private static BattlefieldAllianceSituationSnapshot BuildAllianceSituation(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldPlayerClusterSnapshot> playerClusters,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        byte? battalion,
        byte? localBattalion)
    {
        var members = players
            .Where(player => NormalizeBattalion(player.Battalion) == battalion)
            .ToArray();
        var allianceMapVisionPoints = mapVisionPoints
            .Where(point => point.Battalion == battalion)
            .ToArray();
        var isLocalAlliance = battalion.HasValue && localBattalion.HasValue && battalion.Value == localBattalion.Value;
        var relation = isLocalAlliance
            ? BattlefieldPlayerRelation.Friendly
            : members.FirstOrDefault(player => player.Relation != BattlefieldPlayerRelation.Unknown).Relation;
        if (relation == BattlefieldPlayerRelation.Unknown && battalion.HasValue && localBattalion.HasValue)
            relation = BattlefieldPlayerRelation.Enemy;

        var mainPlayerCluster = playerClusters
            .Where(cluster => cluster.Battalion == battalion)
            .OrderByDescending(cluster => cluster.PlayerCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .Select(cluster => (BattlefieldPlayerClusterSnapshot?)cluster)
            .FirstOrDefault();
        var mainMapVisionCluster = mapVisionClusters
            .Where(cluster => cluster.Battalion == battalion)
            .OrderByDescending(cluster => cluster.PointCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .Select(cluster => (BattlefieldMapVisionClusterSnapshot?)cluster)
            .FirstOrDefault();

        return new BattlefieldAllianceSituationSnapshot
        {
            Battalion = battalion,
            Name = AllianceName(battalion),
            Relation = relation,
            IsLocalAlliance = isLocalAlliance,
            VisiblePlayers = members,
            MapVisionPoints = allianceMapVisionPoints,
            VisiblePlayerCount = members.Length,
            MapVisionPointCount = allianceMapVisionPoints.Count(point => !point.IsDead),
            AliveCount = members.Count(player => !player.IsDead),
            DeadCount = members.Count(player => player.IsDead),
            MountedCount = members.Count(player => player.IsMounted),
            InCombatCount = members.Count(player => player.IsInCombat),
            LowHpCount = members.Count(player => !player.IsDead && player.HpPercent > 0f && player.HpPercent <= LowHpThresholdPercent),
            CastingCount = members.Count(player => player.IsCasting),
            BattleHighCount = members.Count(player => player.BattleHighLevel > 0),
            BattleFeverCount = members.Count(player => player.IsBattleFever),
            MaxBattleHighLevel = members.Select(player => player.BattleHighLevel).DefaultIfEmpty(0).Max(),
            BattleHighTotalLevel = members.Sum(player => player.BattleHighLevel),
            GuardingCount = members.Count(player => player.IsGuarding),
            CrowdControlledCount = members.Count(player => player.IsCrowdControlled),
            ControlVulnerableCount = members.Count(player => player.IsControlVulnerable),
            InvulnerableCount = members.Count(player => player.IsInvulnerable),
            ExecutableCount = members.Count(player => player.IsExecutable),
            SnowBlessingCount = members.Count(player => player.HasSnowBlessing),
            RoleComposition = BuildRoleComposition(members),
            JobComposition = BuildJobComposition(members),
            MainPlayerCluster = mainPlayerCluster,
            MainMapVisionCluster = mainMapVisionCluster,
        };
    }

    private BattlefieldLimitBreakThreatSituationSnapshot BuildLimitBreakThreatSituation(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldScoreSituationSnapshot scoreSituation,
        FrontlineKnowledgeSnapshot knowledge,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldLimitBreakSnapshot localLimitBreak,
        long now,
        IReadOnlyDictionary<ulong, long> lastEngagementTicksByPlayerId)
    {
        var playerById = players.ToDictionary(player => player.GameObjectId);
        var threats = players
            .Where(player => IsFriendlySide(player.Relation) || player.Relation == BattlefieldPlayerRelation.Enemy)
            .Select(player => BuildLimitBreakThreat(player, playerById, scoreSituation, knowledge, timeSituation, localLimitBreak, now, lastEngagementTicksByPlayerId))
            .Where(threat => threat.GameObjectId != 0)
            .ToArray();

        var friendly = threats
            .Where(threat => IsFriendlySide(threat.Relation))
            .OrderByDescending(threat => threat.ThreatScore)
            .ThenBy(threat => threat.EstimatedSecondsToReady)
            .ToArray();
        var enemy = threats
            .Where(threat => threat.Relation == BattlefieldPlayerRelation.Enemy)
            .OrderByDescending(threat => threat.ThreatScore)
            .ThenBy(threat => threat.EstimatedSecondsToReady)
            .ToArray();

        var friendlyReady = friendly.Count(threat => threat.IsLikelyReady);
        var enemyReady = enemy.Count(threat => threat.IsLikelyReady);
        var friendlyHigh = friendly.Count(IsHighLimitBreakThreat);
        var enemyHigh = enemy.Count(IsHighLimitBreakThreat);

        return new BattlefieldLimitBreakThreatSituationSnapshot
        {
            FriendlyThreats = friendly,
            EnemyThreats = enemy,
            TopFriendlyThreats = friendly.Take(6).ToArray(),
            TopEnemyThreats = enemy.Take(6).ToArray(),
            FriendlyLikelyReadyCount = friendlyReady,
            EnemyLikelyReadyCount = enemyReady,
            FriendlyHighThreatCount = friendlyHigh,
            EnemyHighThreatCount = enemyHigh,
            SummaryText = BuildLimitBreakThreatSummary(friendly, enemy, friendlyReady, enemyReady, friendlyHigh, enemyHigh, timeSituation)
        };
    }

    private BattlefieldKeySkillThreatSituationSnapshot BuildKeySkillThreatSituation(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        FrontlineKnowledgeSnapshot knowledge,
        bool keySkillLogEventsAvailable,
        string keySkillLogEventSourceText,
        long now,
        IReadOnlyDictionary<(ulong GameObjectId, string SkillName), long> lastKeySkillUseTicksByPlayerSkill,
        IReadOnlyList<KeySkillUseHistoryEntry> keySkillUseHistoryEntries)
    {
        var playerById = players.ToDictionary(player => player.GameObjectId);
        var combatants = players
            .Where(player => (IsFriendlySide(player.Relation) || player.Relation == BattlefieldPlayerRelation.Enemy) && !player.IsDead)
            .ToArray();
        var threats = new List<BattlefieldKeySkillThreatSnapshot>(combatants.Length * 2);
        foreach (var player in combatants)
        {
            var jobInfo = ResolveJobInfo(player.ClassJobId);
            var profiles = ResolveKeySkillProfiles(player.ClassJobId, jobInfo.Role, knowledge);
            var nearbyContext = BuildKeySkillNearbyContext(player, combatants, playerById);
            foreach (var profile in profiles)
            {
                var threat = BuildKeySkillThreat(
                    player,
                    jobInfo,
                    profile,
                    nearbyContext,
                    now,
                    lastKeySkillUseTicksByPlayerSkill);
                if (threat.GameObjectId != 0)
                    threats.Add(threat);
            }
        }

        var friendly = threats
            .Where(threat => IsFriendlySide(threat.Relation))
            .OrderByDescending(threat => threat.ThreatScore)
            .ThenBy(threat => threat.EstimatedCooldownRemainingSeconds)
            .ToArray();
        var enemy = threats
            .Where(threat => threat.Relation == BattlefieldPlayerRelation.Enemy)
            .OrderByDescending(threat => threat.ThreatScore)
            .ThenBy(threat => threat.EstimatedCooldownRemainingSeconds)
            .ToArray();

        var recentUses = keySkillUseHistoryEntries
            .OrderByDescending(entry => entry.ObservedAtTicks)
            .Take(24)
            .Select(entry => new BattlefieldKeySkillUseSnapshot(
                entry.ObservedAtTicks,
                Math.Max(0, now - entry.ObservedAtTicks),
                entry.GameObjectId,
                entry.Name,
                entry.Relation,
                entry.Battalion,
                entry.AllianceName,
                entry.ClassJobId,
                entry.JobName,
                entry.SkillName,
                entry.Kind,
                entry.TargetName,
                entry.SourceText,
                entry.EvidenceText))
            .ToArray();

        var friendlyReady = friendly.Count(threat => threat.IsEstimatedReady);
        var enemyReady = enemy.Count(threat => threat.IsEstimatedReady);
        var friendlyHigh = friendly.Count(IsHighKeySkillThreat);
        var enemyHigh = enemy.Count(IsHighKeySkillThreat);
        var enemyControlChain = enemy.Count(threat => threat.IsControlChainCandidate && IsHighKeySkillThreat(threat));
        var enemyDefenseBreak = enemy.Count(threat => threat.IsDefenseBreakWindow && IsHighKeySkillThreat(threat));
        var enemyExecute = enemy.Count(threat => threat.IsExecuteWindow && IsHighKeySkillThreat(threat));

        return new BattlefieldKeySkillThreatSituationSnapshot
        {
            FriendlyThreats = friendly,
            EnemyThreats = enemy,
            TopFriendlyThreats = friendly.Take(6).ToArray(),
            TopEnemyThreats = enemy.Take(6).ToArray(),
            RecentUses = recentUses,
            FriendlyLikelyReadyCount = friendlyReady,
            EnemyLikelyReadyCount = enemyReady,
            FriendlyHighThreatCount = friendlyHigh,
            EnemyHighThreatCount = enemyHigh,
            EnemyControlChainCount = enemyControlChain,
            EnemyDefenseBreakWindowCount = enemyDefenseBreak,
            EnemyExecuteWindowCount = enemyExecute,
            SourceText = keySkillLogEventsAvailable
                ? $"{keySkillLogEventSourceText}/灰机Wiki/冷却回推"
                : knowledge.KeySkillRules.Length > 0 ? "灰机Wiki 对战技能/可见状态使用记录估算" : "内置职业档案/可见状态使用记录估算",
            SummaryText = BuildKeySkillThreatSummary(friendly, enemy, friendlyReady, enemyReady, friendlyHigh, enemyHigh, enemyControlChain, enemyDefenseBreak, enemyExecute, recentUses)
        };
    }

    private void ImportKeySkillLogEvents(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldKeySkillLogEventSituationSnapshot keySkillLogEvents,
        long now)
    {
        if (keySkillLogEvents.RecentEvents.Length == 0)
            return;

        foreach (var item in keySkillLogEvents.RecentEvents)
        {
            if (item.ObservedAtTicks <= 0 || now - item.ObservedAtTicks > KeySkillUseHistoryExpiryMs)
                continue;

            if (TryResolveKeySkillEventPlayer(players, item, out var player))
            {
                AddKeySkillUseRecord(
                    player,
                    item.SkillName,
                    item.Kind,
                    item.TargetName,
                    item.SourceText,
                    item.EvidenceText,
                    item.ObservedAtTicks);
            }
            else
            {
                AddUnresolvedKeySkillUseRecord(item, now);
            }
        }
    }

    private static bool TryResolveKeySkillEventPlayer(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldKeySkillUseSnapshot item,
        out BattlefieldPlayerSnapshot player)
    {
        if (item.Name is "你" or "您" or "You" or "you")
        {
            foreach (var candidate in players)
            {
                if (candidate.Relation == BattlefieldPlayerRelation.LocalPlayer)
                {
                    player = candidate;
                    return true;
                }
            }
        }

        var normalizedName = NormalizeActorName(item.Name);
        foreach (var candidate in players)
        {
            if (item.ClassJobId > 0 && candidate.ClassJobId != item.ClassJobId)
                continue;

            if (string.Equals(NormalizeActorName(candidate.Name), normalizedName, StringComparison.OrdinalIgnoreCase))
            {
                player = candidate;
                return true;
            }
        }

        foreach (var candidate in players)
        {
            if (item.ClassJobId > 0 && candidate.ClassJobId != item.ClassJobId)
                continue;

            var candidateName = NormalizeActorName(candidate.Name);
            if (candidateName.Length > 0 && normalizedName.Length > 0
                && (candidateName.Contains(normalizedName, StringComparison.OrdinalIgnoreCase)
                    || normalizedName.Contains(candidateName, StringComparison.OrdinalIgnoreCase)))
            {
                player = candidate;
                return true;
            }
        }

        player = default;
        return false;
    }

    private static KeySkillNearbyContext BuildKeySkillNearbyContext(
        BattlefieldPlayerSnapshot player,
        IReadOnlyList<BattlefieldPlayerSnapshot> combatants,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerById)
    {
        var nearbyOpposingCount = 0;
        var vulnerableCount = 0;
        var controlledCount = 0;
        var executeCount = 0;
        var guardedOrInvulnerableCount = 0;

        foreach (var target in combatants)
        {
            if (!IsOpposingSide(player.Relation, target.Relation) || target.IsDead)
                continue;
            if (Distance2D(player.Position, target.Position) > 60f)
                continue;

            nearbyOpposingCount++;
            if (target.IsControlVulnerable)
                vulnerableCount++;
            if (target.IsCrowdControlled)
                controlledCount++;
            if (target.IsExecutable || target.HpPercent is > 0f and <= 30f)
                executeCount++;
            if (target.IsGuarding || target.IsInvulnerable)
                guardedOrInvulnerableCount++;
        }

        return new KeySkillNearbyContext(
            IsTargetingOpposingSide(player, playerById),
            nearbyOpposingCount,
            vulnerableCount,
            controlledCount,
            executeCount,
            guardedOrInvulnerableCount);
    }

    private BattlefieldKeySkillThreatSnapshot BuildKeySkillThreat(
        BattlefieldPlayerSnapshot player,
        JobInfo jobInfo,
        KeySkillProfile profile,
        KeySkillNearbyContext nearbyContext,
        long now,
        IReadOnlyDictionary<(ulong GameObjectId, string SkillName), long> lastKeySkillUseTicksByPlayerSkill)
    {
        if (player.GameObjectId == 0 || player.IsDead)
            return default;

        var lastUseAgeMs = FindLastKeySkillUseAge(player.GameObjectId, profile.SkillName, now, lastKeySkillUseTicksByPlayerSkill);
        var hasLastUse = lastUseAgeMs >= 0;
        var cooldownRemaining = hasLastUse
            ? MathF.Max(0f, profile.CooldownSeconds - lastUseAgeMs / 1000f)
            : 0f;
        var isReady = cooldownRemaining <= 0.1f;
        var wasRecentlyUsed = hasLastUse && lastUseAgeMs <= KeySkillRecentUseWindowMs;
        var targetingOpposingSide = nearbyContext.IsTargetingOpposingSide;
        var vulnerableCount = nearbyContext.VulnerableCount;
        var controlledCount = nearbyContext.ControlledCount;
        var executeCount = nearbyContext.ExecuteCount;
        var guardedOrInvulnerableCount = nearbyContext.GuardedOrInvulnerableCount;
        var targetGuardedOrInvulnerable = guardedOrInvulnerableCount > 0;
        var controlChain = profile.ControlChain && isReady && (targetingOpposingSide || vulnerableCount >= 2 || controlledCount > 0);
        var defenseBreak = profile.DefenseBreak && isReady && targetGuardedOrInvulnerable;
        var executeWindow = profile.ExecuteWindow && isReady && executeCount > 0;
        var threatScore = ScoreKeySkillThreat(
            player,
            profile,
            isReady,
            wasRecentlyUsed,
            targetingOpposingSide,
            controlChain,
            defenseBreak,
            executeWindow,
            vulnerableCount,
            guardedOrInvulnerableCount,
            nearbyContext.NearbyOpposingCount);
        var threatLevel = ResolveKeySkillThreatLevel(threatScore);
        var evidence = BuildKeySkillThreatEvidence(
            profile,
            player,
            hasLastUse,
            lastUseAgeMs,
            cooldownRemaining,
            isReady,
            wasRecentlyUsed,
            targetingOpposingSide,
            controlChain,
            defenseBreak,
            executeWindow,
            vulnerableCount,
            executeCount,
            guardedOrInvulnerableCount,
            nearbyContext.NearbyOpposingCount);

        return new BattlefieldKeySkillThreatSnapshot(
            player.GameObjectId,
            player.Name,
            player.Relation,
            NormalizeBattalion(player.Battalion),
            AllianceName(NormalizeBattalion(player.Battalion)),
            player.ClassJobId,
            jobInfo.Name,
            profile.SkillName,
            profile.Kind,
            profile.CooldownSeconds,
            cooldownRemaining,
            isReady,
            wasRecentlyUsed,
            lastUseAgeMs,
            player.IsCasting,
            targetingOpposingSide,
            controlChain,
            defenseBreak,
            executeWindow,
            targetGuardedOrInvulnerable,
            vulnerableCount,
            threatLevel,
            threatScore,
            hasLastUse ? "使用记录/冷却估算" : "职业档案/未见使用",
            evidence);
    }

    private static KeySkillProfile[] ResolveKeySkillProfiles(uint classJobId, string role, FrontlineKnowledgeSnapshot knowledge)
    {
        var knowledgeRules = knowledge.KeySkillRules
            .Where(rule => rule.ClassJobId == classJobId || RuleMatchesRole(rule, role))
            .Select(ToKeySkillProfile)
            .ToArray();
        if (knowledgeRules.Length > 0)
            return knowledgeRules;

        if (KeySkillProfilesByClassJobId.TryGetValue(classJobId, out var profiles))
            return profiles;

        return role switch
        {
            "坦克" => new[] { new KeySkillProfile("坦克开团/保护", BattlefieldKeySkillKind.Engage, 25, 16f, "坦克职业可开团或保护关键目标", true, false, false, false) },
            "治疗" => new[] { new KeySkillProfile("治疗反打/解压", BattlefieldKeySkillKind.Support, 30, 15f, "治疗职业可支撑反打和重整", false, false, false, true) },
            "近战" => new[] { new KeySkillProfile("近战贴身爆发", BattlefieldKeySkillKind.Burst, 20, 18f, "近战职业对落单和残血目标威胁较高", false, false, true, false) },
            "远敏" => new[] { new KeySkillProfile("远程打断/压制", BattlefieldKeySkillKind.CrowdControl, 20, 16f, "远敏职业可远程压制撤退和读条", true, false, false, false) },
            "法系" => new[] { new KeySkillProfile("法系范围压制", BattlefieldKeySkillKind.AreaPressure, 25, 18f, "法系职业对密集目标区威胁较高", true, false, false, true) },
            _ => new[] { new KeySkillProfile("未知关键技能", BattlefieldKeySkillKind.Unknown, 25, 12f, "职业关键技能档案未录入", false, false, false, false) },
        };
    }

    private static KeySkillProfile ResolvePrimaryKeySkillProfile(uint classJobId, string role, FrontlineKnowledgeSnapshot knowledge)
        => ResolveKeySkillProfiles(classJobId, role, knowledge)
            .OrderByDescending(profile => profile.BaseThreat)
            .First();

    private static KeySkillProfile ToKeySkillProfile(FrontlineKeySkillRuleSnapshot rule)
        => new(
            rule.SkillName,
            rule.Kind,
            rule.CooldownSeconds,
            rule.BaseThreat,
            $"{rule.TacticalNote} 来源：{rule.SourceName}",
            rule.ControlChain,
            rule.DefenseBreak,
            rule.ExecuteWindow,
            rule.AreaPressure);

    private static bool RuleMatchesRole(FrontlineKeySkillRuleSnapshot rule, string role)
    {
        if (rule.ClassJobId.HasValue)
            return false;

        return role switch
        {
            "坦克" => rule.JobName.Contains("防护", StringComparison.Ordinal) || rule.JobName.Contains("坦克", StringComparison.Ordinal),
            "近战" => rule.JobName.Contains("近战", StringComparison.Ordinal),
            "远敏" => rule.JobName.Contains("远程", StringComparison.Ordinal) || rule.JobName.Contains("远敏", StringComparison.Ordinal),
            "法系" => rule.JobName.Contains("魔法", StringComparison.Ordinal) || rule.JobName.Contains("法系", StringComparison.Ordinal),
            "治疗" => rule.JobName.Contains("治疗", StringComparison.Ordinal) || rule.JobName.Contains("鎭㈠", StringComparison.Ordinal),
            _ => false,
        };
    }

    private static long FindLastKeySkillUseAge(
        ulong gameObjectId,
        string skillName,
        long now,
        IReadOnlyDictionary<(ulong GameObjectId, string SkillName), long> lastKeySkillUseTicksByPlayerSkill)
        => lastKeySkillUseTicksByPlayerSkill.TryGetValue((gameObjectId, skillName), out var observedAtTicks)
            ? Math.Max(0, now - observedAtTicks)
            : -1;

    private static bool IsOpposingSide(BattlefieldPlayerRelation source, BattlefieldPlayerRelation target)
        => IsFriendlySide(source)
            ? target == BattlefieldPlayerRelation.Enemy
            : source == BattlefieldPlayerRelation.Enemy && IsFriendlySide(target);

    private static float ScoreKeySkillThreat(
        BattlefieldPlayerSnapshot player,
        KeySkillProfile profile,
        bool isReady,
        bool wasRecentlyUsed,
        bool targetingOpposingSide,
        bool controlChain,
        bool defenseBreak,
        bool executeWindow,
        int vulnerableCount,
        int guardedOrInvulnerableCount,
        int nearbyOpposingCount)
    {
        var score = profile.BaseThreat
            + (isReady ? 22f : -12f)
            + player.BattleHighLevel * 4f
            + (player.IsBattleFever ? 7f : 0f);

        if (player.IsInCombat)
            score += 5f;
        if (player.IsCasting)
            score += 7f;
        if (targetingOpposingSide)
            score += 8f;
        if (player.IsControlImmune)
            score += 4f;
        if (player.IsGuarding)
            score -= 8f;
        if (player.IsInvulnerable)
            score -= 6f;
        if (wasRecentlyUsed)
            score -= 14f;

        if (controlChain)
            score += 16f;
        if (defenseBreak)
            score += 14f;
        if (executeWindow)
            score += 16f;
        if (profile.AreaPressure && nearbyOpposingCount >= 4)
            score += Math.Min(18f, nearbyOpposingCount * 2.5f);

        score += Math.Min(12f, vulnerableCount * 3f);
        score += Math.Min(10f, guardedOrInvulnerableCount * 3f);

        if (player.DistanceToLocal is > 0f and <= 30f)
            score += player.Relation == BattlefieldPlayerRelation.Enemy ? 8f : 3f;
        else if (player.DistanceToLocal is > 30f and <= 60f)
            score += player.Relation == BattlefieldPlayerRelation.Enemy ? 4f : 2f;

        return Math.Clamp(score, 0f, 100f);
    }

    private static BattlefieldLimitBreakThreatLevel ResolveKeySkillThreatLevel(float score)
    {
        if (score >= 82f)
            return BattlefieldLimitBreakThreatLevel.Critical;
        if (score >= 62f)
            return BattlefieldLimitBreakThreatLevel.High;
        if (score >= 40f)
            return BattlefieldLimitBreakThreatLevel.Medium;
        return BattlefieldLimitBreakThreatLevel.Low;
    }

    private static bool IsHighKeySkillThreat(BattlefieldKeySkillThreatSnapshot threat)
        => threat.ThreatLevel is BattlefieldLimitBreakThreatLevel.High or BattlefieldLimitBreakThreatLevel.Critical;

    private static string BuildKeySkillThreatEvidence(
        KeySkillProfile profile,
        BattlefieldPlayerSnapshot player,
        bool hasLastUse,
        long lastUseAgeMs,
        float cooldownRemaining,
        bool isReady,
        bool wasRecentlyUsed,
        bool targetingOpposingSide,
        bool controlChain,
        bool defenseBreak,
        bool executeWindow,
        int vulnerableCount,
        int executeCount,
        int guardedOrInvulnerableCount,
        int nearbyOpposingCount)
    {
        var parts = new List<string> { profile.Note };

        if (hasLastUse)
            parts.Add(cooldownRemaining <= 0f
                ? $"上次可见使用 {FormatDuration((int)(lastUseAgeMs / 1000))} 前，估算已转好"
                : $"估算冷却剩余 {cooldownRemaining:0}秒");
        else
            parts.Add("未见近期使用，保守按可能可用");

        if (isReady)
            parts.Add("疑似可用");
        if (wasRecentlyUsed)
            parts.Add("刚出现使用状态记录");
        if (player.BattleHighLevel > 0)
            parts.Add(player.IsBattleFever ? "战意狂热" : $"战意 {player.BattleHighLevel}");
        if (targetingOpposingSide)
            parts.Add("正在锁定敌对目标");
        if (player.IsCasting)
            parts.Add("正在咏唱");
        if (player.IsGuarding)
            parts.Add("防御中，主动威胁下调");
        if (player.IsControlImmune)
            parts.Add("抗控/净化后，可继续推进");
        if (controlChain)
            parts.Add($"控制链窗口，可控目标 {vulnerableCount}");
        if (defenseBreak)
            parts.Add($"破防窗口，防御/无敌目标 {guardedOrInvulnerableCount}");
        if (executeWindow)
            parts.Add($"收割窗口，残血目标 {executeCount}");
        if (profile.AreaPressure && nearbyOpposingCount >= 4)
            parts.Add($"密集目标 {nearbyOpposingCount}");
        if (player.DistanceToLocal is > 0f and <= 60f)
            parts.Add($"距离 {player.DistanceToLocal:0}y");

        return string.Join("；", parts);
    }

    private static string BuildKeySkillThreatSummary(
        IReadOnlyList<BattlefieldKeySkillThreatSnapshot> friendly,
        IReadOnlyList<BattlefieldKeySkillThreatSnapshot> enemy,
        int friendlyReady,
        int enemyReady,
        int friendlyHigh,
        int enemyHigh,
        int enemyControlChain,
        int enemyDefenseBreak,
        int enemyExecute,
        IReadOnlyList<BattlefieldKeySkillUseSnapshot> recentUses)
    {
        if (friendly.Count == 0 && enemy.Count == 0)
            return "关键技能：暂无可见玩家样本";

        var parts = new List<string>
        {
            $"敌方疑似可用 {enemyReady} / 高危 {enemyHigh}",
            $"我方疑似可用 {friendlyReady} / 高威胁 {friendlyHigh}"
        };

        if (enemyControlChain > 0)
            parts.Add($"控制链 {enemyControlChain}");
        if (enemyDefenseBreak > 0)
            parts.Add($"破防 {enemyDefenseBreak}");
        if (enemyExecute > 0)
            parts.Add($"斩杀 {enemyExecute}");
        if (enemy.Count > 0)
        {
            var topEnemy = enemy[0];
            parts.Add($"敌方最高 {topEnemy.Name}({topEnemy.JobName}) {topEnemy.SkillName} {LimitBreakThreatLevelText(topEnemy.ThreatLevel)}");
        }
        if (recentUses.Count > 0)
            parts.Add($"近期记录 {recentUses.Count}");

        return $"关键技能：{string.Join("；", parts)}";
    }

    private BattlefieldLimitBreakThreatSnapshot BuildLimitBreakThreat(
        BattlefieldPlayerSnapshot player,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerById,
        BattlefieldScoreSituationSnapshot scoreSituation,
        FrontlineKnowledgeSnapshot knowledge,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldLimitBreakSnapshot localLimitBreak,
        long now,
        IReadOnlyDictionary<ulong, long> lastEngagementTicksByPlayerId)
    {
        if (player.GameObjectId == 0)
            return default;

        var jobInfo = ResolveJobInfo(player.ClassJobId);
        var jobAdjustment = knowledge.JobAdjustments.FirstOrDefault(job => job.ClassJobId == player.ClassJobId);
        var baseChargeSeconds = jobAdjustment.FrontlineLimitBreakChargeSeconds > 0
            ? jobAdjustment.FrontlineLimitBreakChargeSeconds
            : DefaultLimitBreakChargeSeconds;
        var rankSpeedModifierPercent = ResolveLimitBreakRankSpeedModifier(player, scoreSituation);
        var adjustedChargeSeconds = ApplyLimitBreakRankModifier(baseChargeSeconds, rankSpeedModifierPercent);
        var profile = ResolveLimitBreakThreatProfile(player.ClassJobId, jobInfo.Role);
        var hasActualLocalLimitBreak = player.Relation == BattlefieldPlayerRelation.LocalPlayer && localLimitBreak.IsAvailable;
        var estimatedPercent = EstimateLimitBreakPercent(player, adjustedChargeSeconds, timeSituation, localLimitBreak, hasActualLocalLimitBreak, now, out var chargeSourceText);
        var secondsToReady = estimatedPercent >= 100f
            ? 0f
            : Math.Max(0f, adjustedChargeSeconds * (100f - estimatedPercent) / 100f);
        var targetingOpposingSide = IsTargetingOpposingSide(player, playerById);
        var recentlyEngaged = IsRecentlyEngaged(player, now, lastEngagementTicksByPlayerId);
        var threatScore = ScoreLimitBreakThreat(player, profile, estimatedPercent, recentlyEngaged, targetingOpposingSide, timeSituation.HasMatchTime, hasActualLocalLimitBreak);
        var isLikelyReady = estimatedPercent >= 95f;
        var threatLevel = ResolveLimitBreakThreatLevel(player, threatScore, isLikelyReady, recentlyEngaged, targetingOpposingSide);
        var evidence = BuildLimitBreakThreatEvidence(
            profile,
            chargeSourceText,
            rankSpeedModifierPercent,
            adjustedChargeSeconds,
            player,
            recentlyEngaged,
            targetingOpposingSide,
            timeSituation.HasMatchTime,
            hasActualLocalLimitBreak);

        return new BattlefieldLimitBreakThreatSnapshot(
            player.GameObjectId,
            player.Name,
            player.Relation,
            NormalizeBattalion(player.Battalion),
            AllianceName(NormalizeBattalion(player.Battalion)),
            player.ClassJobId,
            jobInfo.Name,
            jobInfo.Role,
            player.BattleHighLevel,
            player.IsBattleFever,
            player.DistanceToLocal,
            baseChargeSeconds,
            adjustedChargeSeconds,
            rankSpeedModifierPercent,
            estimatedPercent,
            secondsToReady,
            isLikelyReady,
            threatLevel,
            threatScore,
            recentlyEngaged,
            player.IsCasting,
            targetingOpposingSide,
            profile.ThreatType,
            evidence);
    }

    private static float EstimateLimitBreakPercent(
        BattlefieldPlayerSnapshot player,
        int adjustedChargeSeconds,
        BattlefieldTimeSituationSnapshot timeSituation,
        BattlefieldLimitBreakSnapshot localLimitBreak,
        bool hasActualLocalLimitBreak,
        long now,
        out string sourceText)
    {
        if (player.IsDead)
        {
            sourceText = "姝讳骸";
            return 0f;
        }

        if (hasActualLocalLimitBreak)
        {
            sourceText = "本地真实极限技";
            return Math.Clamp(localLimitBreak.Percent, 0f, 100f);
        }

        if (timeSituation.HasMatchTime)
        {
            sourceText = "对局开始锚点";
            return Math.Clamp(timeSituation.MatchElapsedSeconds * 100f / Math.Max(1, adjustedChargeSeconds), 0f, 100f);
        }

        sourceText = "可见时间样本";
        return 0f;
    }

    private static bool IsRecentlyEngaged(
        BattlefieldPlayerSnapshot player,
        long now,
        IReadOnlyDictionary<ulong, long> lastEngagementTicksByPlayerId)
    {
        if (player.IsInCombat || player.IsCasting)
            return true;

        if (!lastEngagementTicksByPlayerId.TryGetValue(player.GameObjectId, out var lastEngagementTicks) || lastEngagementTicks <= 0)
            return false;

        return now - lastEngagementTicks <= LimitBreakRecentEngagementWindowMs;
    }

    private static bool IsTargetingOpposingSide(
        BattlefieldPlayerSnapshot player,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerById)
        => IsTargetingOpposingSide(player, player.TargetObjectId, playerById)
            || IsTargetingOpposingSide(player, player.CastTargetObjectId, playerById);

    private static bool IsTargetingOpposingSide(
        BattlefieldPlayerSnapshot source,
        ulong targetObjectId,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerById)
    {
        if (!IsValidGameObjectId(targetObjectId) || !playerById.TryGetValue(targetObjectId, out var target))
            return false;

        return IsFriendlySide(source.Relation)
            ? target.Relation == BattlefieldPlayerRelation.Enemy
            : IsFriendlySide(target.Relation);
    }

    private static int ResolveLimitBreakRankSpeedModifier(
        BattlefieldPlayerSnapshot player,
        BattlefieldScoreSituationSnapshot scoreSituation)
    {
        var battalion = NormalizeBattalion(player.Battalion);
        if (!battalion.HasValue || !scoreSituation.HasScoreData)
            return 0;

        var alliance = scoreSituation.Alliances.FirstOrDefault(item => item.Battalion == battalion);
        return alliance.RankIndex switch
        {
            1 => -25,
            2 => 0,
            3 => 25,
            _ => 0
        };
    }

    private static int ApplyLimitBreakRankModifier(int baseChargeSeconds, int speedModifierPercent)
    {
        var speedMultiplier = Math.Max(0.25f, 1f + speedModifierPercent / 100f);
        return Math.Max(1, (int)MathF.Ceiling(baseChargeSeconds / speedMultiplier));
    }

    private static float ScoreLimitBreakThreat(
        BattlefieldPlayerSnapshot player,
        LimitBreakThreatProfile profile,
        float estimatedPercent,
        bool recentlyEngaged,
        bool targetingOpposingSide,
        bool hasMatchTime,
        bool hasActualLocalLimitBreak)
    {
        if (player.IsDead)
            return 0f;

        var score = estimatedPercent * (hasActualLocalLimitBreak ? 0.62f : 0.48f)
            + profile.Weight
            + player.BattleHighLevel * 4.5f
            + (player.IsBattleFever ? 7f : 0f);

        if (estimatedPercent >= 95f)
            score += 10f;
        if (recentlyEngaged)
            score += 8f;
        if (player.IsCasting)
            score += 7f;
        if (targetingOpposingSide)
            score += 8f;
        if (player.DistanceToLocal is > 0f and <= 30f)
            score += player.Relation == BattlefieldPlayerRelation.Enemy ? 8f : 3f;
        else if (player.DistanceToLocal is > 30f and <= 60f)
            score += player.Relation == BattlefieldPlayerRelation.Enemy ? 4f : 2f;

        if (!hasMatchTime && !hasActualLocalLimitBreak)
            score -= 8f;

        return Math.Clamp(score, 0f, 100f);
    }

    private static BattlefieldLimitBreakThreatLevel ResolveLimitBreakThreatLevel(
        BattlefieldPlayerSnapshot player,
        float threatScore,
        bool isLikelyReady,
        bool recentlyEngaged,
        bool targetingOpposingSide)
    {
        if (player.IsDead)
            return BattlefieldLimitBreakThreatLevel.Low;

        if (threatScore >= 85f || (isLikelyReady && threatScore >= 75f && (recentlyEngaged || targetingOpposingSide)))
            return BattlefieldLimitBreakThreatLevel.Critical;
        if (threatScore >= 65f || isLikelyReady)
            return BattlefieldLimitBreakThreatLevel.High;
        if (threatScore >= 42f)
            return BattlefieldLimitBreakThreatLevel.Medium;
        return BattlefieldLimitBreakThreatLevel.Low;
    }

    private static bool IsHighLimitBreakThreat(BattlefieldLimitBreakThreatSnapshot threat)
        => threat.ThreatLevel is BattlefieldLimitBreakThreatLevel.High or BattlefieldLimitBreakThreatLevel.Critical;

    private static LimitBreakThreatProfile ResolveLimitBreakThreatProfile(uint classJobId, string role)
    {
        if (LimitBreakThreatProfileByClassJobId.TryGetValue(classJobId, out var profile))
            return profile;

        return role switch
        {
            "坦克" => new LimitBreakThreatProfile("开团/保护", 17f, "坦克极限技主要影响开团和阵地控制"),
            "治疗" => new LimitBreakThreatProfile("续航/反打", 17f, "治疗极限技主要影响续航和反打"),
            "近战" => new LimitBreakThreatProfile("近战爆发", 20f, "近战极限技对近距离目标威胁较高"),
            "远敏" => new LimitBreakThreatProfile("远程压制", 17f, "远敏极限技偏远程压制"),
            "法系" => new LimitBreakThreatProfile("范围压制", 18f, "法系极限技偏范围压制"),
            _ => new LimitBreakThreatProfile("未知极限技", 14f, "职业威胁档案未录入")
        };
    }

    private static string BuildLimitBreakThreatEvidence(
        LimitBreakThreatProfile profile,
        string chargeSourceText,
        int rankSpeedModifierPercent,
        int adjustedChargeSeconds,
        BattlefieldPlayerSnapshot player,
        bool recentlyEngaged,
        bool targetingOpposingSide,
        bool hasMatchTime,
        bool hasActualLocalLimitBreak)
    {
        var parts = new List<string>
        {
            profile.Note,
            $"{chargeSourceText}，修正后充能 {adjustedChargeSeconds}秒"
        };

        if (rankSpeedModifierPercent != 0)
            parts.Add($"排名修正 {FormatSignedNumber(rankSpeedModifierPercent)}% 速度");
        if (player.BattleHighLevel > 0)
            parts.Add(player.IsBattleFever ? "战意狂热" : $"战意 {player.BattleHighLevel}");
        if (recentlyEngaged)
            parts.Add("近期交战");
        if (player.IsCasting)
            parts.Add("正在咏唱");
        if (targetingOpposingSide)
            parts.Add("正在锁定敌对目标");
        if (player.DistanceToLocal is > 0f and <= 60f)
            parts.Add($"距离 {player.DistanceToLocal:0}y");
        if (!hasMatchTime && !hasActualLocalLimitBreak)
            parts.Add("缺少对局时间锚点，充能只保守估算");

        return string.Join("；", parts);
    }

    private static string BuildLimitBreakThreatSummary(
        IReadOnlyList<BattlefieldLimitBreakThreatSnapshot> friendly,
        IReadOnlyList<BattlefieldLimitBreakThreatSnapshot> enemy,
        int friendlyReady,
        int enemyReady,
        int friendlyHigh,
        int enemyHigh,
        BattlefieldTimeSituationSnapshot timeSituation)
    {
        if (friendly.Count == 0 && enemy.Count == 0)
            return "极限技威胁：暂无可见玩家样本";

        var parts = new List<string>
        {
            $"敌方疑似可用 {enemyReady} / 高危 {enemyHigh}",
            $"我方疑似可用 {friendlyReady} / 高威胁 {friendlyHigh}"
        };

        if (enemy.Count > 0)
        {
            var topEnemy = enemy[0];
            parts.Add($"敌方最高 {topEnemy.Name}({topEnemy.JobName}) {LimitBreakThreatLevelText(topEnemy.ThreatLevel)} {topEnemy.EstimatedPercent:0}%");
        }

        if (friendly.Count > 0)
        {
            var topFriendly = friendly[0];
            parts.Add($"我方可打 {topFriendly.Name}({topFriendly.JobName}) {topFriendly.EstimatedPercent:0}%");
        }

        if (!timeSituation.HasMatchTime)
            parts.Add("缺少时间锚点");

        return $"极限技威胁：{string.Join("，", parts)}";
    }

    private static string LimitBreakThreatLevelText(BattlefieldLimitBreakThreatLevel level)
        => level switch
        {
            BattlefieldLimitBreakThreatLevel.Critical => "极高",
            BattlefieldLimitBreakThreatLevel.High => "高",
            BattlefieldLimitBreakThreatLevel.Medium => "中",
            _ => "低",
        };

    private static BattlefieldEnemyClusterSnapshot[] BuildEnemyTacticalClusters(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var mapSamples = mapVisionPoints
            .Where(point => point.Relation == BattlefieldPlayerRelation.Enemy && !point.IsDead)
            .Select(point => new EnemyClusterSample(point.EstimatedWorldPosition, point.Battalion, "地图视野"))
            .ToArray();
        if (mapSamples.Length > 0)
            return BuildEnemyTacticalClustersFromSamples(mapSamples, localPlayer);

        var playerSamples = players
            .Where(player => player.Relation == BattlefieldPlayerRelation.Enemy && !player.IsDead)
            .Select(player => new EnemyClusterSample(player.Position, NormalizeBattalion(player.Battalion), "附近实体"))
            .ToArray();
        var samples = playerSamples;
        if (samples.Length == 0)
            return Array.Empty<BattlefieldEnemyClusterSnapshot>();

        return BuildEnemyTacticalClustersFromSamples(samples, localPlayer);
    }

    private static BattlefieldEnemyClusterSnapshot[] BuildEnemyTacticalClustersFromSamples(
        IReadOnlyList<EnemyClusterSample> samples,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var works = samples
            .GroupBy(sample => sample.Battalion)
            .SelectMany(group => ClusterEnemySamples(group.ToArray(), localPlayer))
            .OrderByDescending(cluster => cluster.Count)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .ToArray();
        if (works.Length == 0)
            return Array.Empty<BattlefieldEnemyClusterSnapshot>();

        var mainCenter = works[0].Center;
        return works
            .Select((cluster, index) => new BattlefieldEnemyClusterSnapshot
            {
                ClusterId = index + 1,
                Battalion = cluster.Battalion,
                AllianceName = AllianceName(cluster.Battalion),
                SourceText = cluster.SourceText,
                Center = cluster.Center,
                Count = cluster.Count,
                Radius = cluster.Radius,
                DistanceToLocal = cluster.DistanceToLocal,
                SeparationFromMain = index == 0 ? 0f : Distance2D(cluster.Center, mainCenter),
                IsMainCluster = index == 0,
            })
            .ToArray();
    }

    private static EnemyClusterWork[] ClusterEnemySamples(
        IReadOnlyList<EnemyClusterSample> samples,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        if (samples.Count == 0)
            return Array.Empty<EnemyClusterWork>();

        var centers = EstimateMeanShiftCenters(samples);
        var memberIndexesByCluster = RunKMeans(samples, centers);
        return memberIndexesByCluster
            .Where(indexes => indexes.Length > 0)
            .Select(indexes => CreateEnemyClusterWork(samples, indexes, localPlayer))
            .Where(cluster => cluster.Count > 0)
            .OrderByDescending(cluster => cluster.Count)
            .Take(MaxEnemyClustersPerAlliance)
            .ToArray();
    }

    private static Vector3[] EstimateMeanShiftCenters(IReadOnlyList<EnemyClusterSample> samples)
    {
        if (samples.Count == 1)
            return new[] { samples[0].Position };

        var bandwidthSquared = EnemyMeanShiftBandwidth * EnemyMeanShiftBandwidth;
        var centers = new List<Vector3>(samples.Count);
        foreach (var sample in samples)
        {
            var center = sample.Position;
            for (var iteration = 0; iteration < 10; iteration++)
            {
                var sum = Vector3.Zero;
                var count = 0;
                foreach (var candidate in samples)
                {
                    if (DistanceSquared2D(center, candidate.Position) > bandwidthSquared)
                        continue;

                    sum += candidate.Position;
                    count++;
                }

                if (count == 0)
                    break;

                var nextCenter = sum / count;
                if (Distance2D(center, nextCenter) < 1f)
                {
                    center = nextCenter;
                    break;
                }

                center = nextCenter;
            }

            MergeMeanShiftCenter(centers, center);
        }

        return centers
            .OrderByDescending(center => samples.Count(sample => DistanceSquared2D(center, sample.Position) <= bandwidthSquared))
            .Take(Math.Min(MaxEnemyClustersPerAlliance, samples.Count))
            .ToArray();
    }

    private static void MergeMeanShiftCenter(List<Vector3> centers, Vector3 center)
    {
        for (var i = 0; i < centers.Count; i++)
        {
            if (Distance2D(centers[i], center) > EnemyClusterMergeDistance)
                continue;

            centers[i] = (centers[i] + center) * 0.5f;
            return;
        }

        centers.Add(center);
    }

    private static int[][] RunKMeans(IReadOnlyList<EnemyClusterSample> samples, IReadOnlyList<Vector3> initialCenters)
    {
        var k = Math.Clamp(initialCenters.Count, 1, samples.Count);
        var centers = initialCenters.Take(k).ToArray();
        if (centers.Length == 0)
            centers = new[] { samples[0].Position };

        var assignments = Enumerable.Repeat(-1, samples.Count).ToArray();
        for (var iteration = 0; iteration < EnemyKMeansIterations; iteration++)
        {
            var changed = false;
            for (var i = 0; i < samples.Count; i++)
            {
                var nearest = FindNearestCenter(samples[i].Position, centers);
                if (assignments[i] == nearest)
                    continue;

                assignments[i] = nearest;
                changed = true;
            }

            var sums = new Vector3[centers.Length];
            var counts = new int[centers.Length];
            for (var i = 0; i < samples.Count; i++)
            {
                var clusterIndex = assignments[i];
                if (clusterIndex < 0)
                    continue;

                sums[clusterIndex] += samples[i].Position;
                counts[clusterIndex]++;
            }

            for (var i = 0; i < centers.Length; i++)
            {
                if (counts[i] > 0)
                    centers[i] = sums[i] / counts[i];
            }

            if (!changed)
                break;
        }

        return Enumerable.Range(0, centers.Length)
            .Select(clusterIndex => assignments
                .Select((assignedCluster, sampleIndex) => (assignedCluster, sampleIndex))
                .Where(item => item.assignedCluster == clusterIndex)
                .Select(item => item.sampleIndex)
                .ToArray())
            .Where(indexes => indexes.Length > 0)
            .ToArray();
    }

    private static int FindNearestCenter(Vector3 position, IReadOnlyList<Vector3> centers)
    {
        var nearest = 0;
        var nearestDistance = float.MaxValue;
        for (var i = 0; i < centers.Count; i++)
        {
            var distance = DistanceSquared2D(position, centers[i]);
            if (distance >= nearestDistance)
                continue;

            nearest = i;
            nearestDistance = distance;
        }

        return nearest;
    }

    private static EnemyClusterWork CreateEnemyClusterWork(
        IReadOnlyList<EnemyClusterSample> samples,
        IReadOnlyList<int> memberIndexes,
        BattlefieldPlayerSnapshot? localPlayer)
    {
        var center = Vector3.Zero;
        foreach (var index in memberIndexes)
            center += samples[index].Position;

        center /= Math.Max(1, memberIndexes.Count);
        var radius = memberIndexes
            .Select(index => Distance2D(center, samples[index].Position))
            .DefaultIfEmpty(0f)
            .Max();
        var sourceText = samples[memberIndexes[0]].SourceText;
        return new EnemyClusterWork(
            samples[memberIndexes[0]].Battalion,
            $"{sourceText}/均值漂移 kmeans",
            center,
            memberIndexes.Count,
            radius,
            localPlayer.HasValue ? Vector3.Distance(localPlayer.Value.Position, center) : 0f);
    }

    private static BattlefieldCompositionSnapshot[] BuildRoleComposition(IReadOnlyList<BattlefieldPlayerSnapshot> members)
    {
        return members
            .GroupBy(player => ResolveJobInfo(player.ClassJobId).Role)
            .Select(group => new BattlefieldCompositionSnapshot(group.Key, "瀹氫綅", group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .ToArray();
    }

    private static BattlefieldCompositionSnapshot[] BuildJobComposition(IReadOnlyList<BattlefieldPlayerSnapshot> members)
    {
        return members
            .GroupBy(player => ResolveJobInfo(player.ClassJobId).Name)
            .Select(group => new BattlefieldCompositionSnapshot(group.Key, "鑱屼笟", group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Name)
            .ToArray();
    }

    private BattlefieldFocusTargetSnapshot[] BuildFocusTargets(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        Func<BattlefieldPlayerSnapshot, bool> sourceFilter,
        Func<BattlefieldPlayerSnapshot, bool> targetFilter)
    {
        var playerById = players.ToDictionary(player => player.GameObjectId);
        var accumulators = new Dictionary<ulong, FocusAccumulator>();

        foreach (var source in players.Where(sourceFilter))
        {
            AddFocusTarget(source, source.TargetObjectId, false, playerById, targetFilter, accumulators);

            if (source.IsCasting)
                AddFocusTarget(source, source.CastTargetObjectId, true, playerById, targetFilter, accumulators);
        }

        return accumulators.Values
            .Select(accumulator => accumulator.ToSnapshot())
            .Where(snapshot => snapshot.AttackerCount > 0 || snapshot.CasterCount > 0)
            .OrderByDescending(snapshot => snapshot.ThreatScore)
            .ThenByDescending(snapshot => snapshot.AttackerCount)
            .ThenByDescending(snapshot => snapshot.CasterCount)
            .ToArray();
    }

    private static void AddFocusTarget(
        BattlefieldPlayerSnapshot source,
        ulong targetObjectId,
        bool isCaster,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerById,
        Func<BattlefieldPlayerSnapshot, bool> targetFilter,
        Dictionary<ulong, FocusAccumulator> accumulators)
    {
        if (!IsValidGameObjectId(targetObjectId))
            return;

        if (!playerById.TryGetValue(targetObjectId, out var target))
            return;

        if (source.GameObjectId == target.GameObjectId || !targetFilter(target))
            return;

        if (!accumulators.TryGetValue(target.GameObjectId, out var accumulator))
        {
            accumulator = new FocusAccumulator(target);
            accumulators[target.GameObjectId] = accumulator;
        }

        accumulator.Add(source, isCaster);
    }

    private BattlefieldRespawnRhythmSnapshot BuildRespawnRhythm(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        long now)
    {
        var friendlyDeadNow = players.Count(player => IsFriendlySide(player.Relation) && player.IsDead);
        var enemyDeadNow = players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy && player.IsDead);
        var friendlyRecentlyDied = playerHistory.Values.Count(entry => IsFriendlySide(entry.Relation) && entry.LastDeathTicks > 0 && now - entry.LastDeathTicks <= RecentEventWindowMs);
        var enemyRecentlyDied = playerHistory.Values.Count(entry => entry.Relation == BattlefieldPlayerRelation.Enemy && entry.LastDeathTicks > 0 && now - entry.LastDeathTicks <= RecentEventWindowMs);
        var friendlyRecentlyRevived = playerHistory.Values.Count(entry => IsFriendlySide(entry.Relation) && entry.LastReviveTicks > 0 && now - entry.LastReviveTicks <= RecentEventWindowMs);
        var enemyRecentlyRevived = playerHistory.Values.Count(entry => entry.Relation == BattlefieldPlayerRelation.Enemy && entry.LastReviveTicks > 0 && now - entry.LastReviveTicks <= RecentEventWindowMs);
        var friendlyLikelyReturningSoon = players.Count(player => IsFriendlySide(player.Relation) && player.IsDead && TryGetDeathAge(now, player.GameObjectId, out var age) && age >= 12000);
        var enemyLikelyReturningSoon = players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy && player.IsDead && TryGetDeathAge(now, player.GameObjectId, out var age) && age >= 12000);
        var friendlyDeathWave = BuildRespawnWave(playerHistory.Values
            .Where(entry => IsFriendlySide(entry.Relation))
            .Select(entry => entry.LastDeathTicks), now);
        var enemyDeathWave = BuildRespawnWave(playerHistory.Values
            .Where(entry => entry.Relation == BattlefieldPlayerRelation.Enemy)
            .Select(entry => entry.LastDeathTicks), now);
        var friendlyReviveWave = BuildRespawnWave(playerHistory.Values
            .Where(entry => IsFriendlySide(entry.Relation))
            .Select(entry => entry.LastReviveTicks), now);
        var enemyReviveWave = BuildRespawnWave(playerHistory.Values
            .Where(entry => entry.Relation == BattlefieldPlayerRelation.Enemy)
            .Select(entry => entry.LastReviveTicks), now);
        var confidence = CalculateRespawnConfidence(
            friendlyDeadNow + enemyDeadNow,
            friendlyRecentlyDied + enemyRecentlyDied,
            friendlyRecentlyRevived + enemyRecentlyRevived,
            Math.Max(friendlyDeathWave.Size, enemyDeathWave.Size),
            Math.Max(friendlyReviveWave.Size, enemyReviveWave.Size));

        return new BattlefieldRespawnRhythmSnapshot
        {
            FriendlyDeadNow = friendlyDeadNow,
            EnemyDeadNow = enemyDeadNow,
            FriendlyRecentlyDied = friendlyRecentlyDied,
            EnemyRecentlyDied = enemyRecentlyDied,
            FriendlyRecentlyRevived = friendlyRecentlyRevived,
            EnemyRecentlyRevived = enemyRecentlyRevived,
            FriendlyLikelyReturningSoon = friendlyLikelyReturningSoon,
            EnemyLikelyReturningSoon = enemyLikelyReturningSoon,
            FriendlyDeathWaveSize = friendlyDeathWave.Size,
            EnemyDeathWaveSize = enemyDeathWave.Size,
            FriendlyReviveWaveSize = friendlyReviveWave.Size,
            EnemyReviveWaveSize = enemyReviveWave.Size,
            FriendlyDeathWaveAgeMs = friendlyDeathWave.AgeMs,
            EnemyDeathWaveAgeMs = enemyDeathWave.AgeMs,
            FriendlyReviveWaveAgeMs = friendlyReviveWave.AgeMs,
            EnemyReviveWaveAgeMs = enemyReviveWave.AgeMs,
            Confidence = confidence,
            SummaryText = BuildRespawnSummary(
                friendlyDeadNow,
                enemyDeadNow,
                friendlyRecentlyDied,
                enemyRecentlyDied,
                friendlyRecentlyRevived,
                enemyRecentlyRevived,
                friendlyLikelyReturningSoon,
                enemyLikelyReturningSoon,
                friendlyDeathWave,
                enemyDeathWave,
                friendlyReviveWave,
                enemyReviveWave,
                confidence)
        };
    }

    private static BattlefieldRespawnRhythmSnapshot BuildRespawnRhythm(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<RespawnHistoryEntrySample> historyEntries,
        long now)
    {
        var historyById = historyEntries.ToDictionary(entry => entry.GameObjectId);
        var friendlyDeadNow = players.Count(player => IsFriendlySide(player.Relation) && player.IsDead);
        var enemyDeadNow = players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy && player.IsDead);
        var friendlyRecentlyDied = historyEntries.Count(entry => IsFriendlySide(entry.Relation) && entry.LastDeathTicks > 0 && now - entry.LastDeathTicks <= RecentEventWindowMs);
        var enemyRecentlyDied = historyEntries.Count(entry => entry.Relation == BattlefieldPlayerRelation.Enemy && entry.LastDeathTicks > 0 && now - entry.LastDeathTicks <= RecentEventWindowMs);
        var friendlyRecentlyRevived = historyEntries.Count(entry => IsFriendlySide(entry.Relation) && entry.LastReviveTicks > 0 && now - entry.LastReviveTicks <= RecentEventWindowMs);
        var enemyRecentlyRevived = historyEntries.Count(entry => entry.Relation == BattlefieldPlayerRelation.Enemy && entry.LastReviveTicks > 0 && now - entry.LastReviveTicks <= RecentEventWindowMs);
        var friendlyLikelyReturningSoon = players.Count(player =>
            IsFriendlySide(player.Relation)
            && player.IsDead
            && historyById.TryGetValue(player.GameObjectId, out var entry)
            && entry.DeathStartedTicks > 0
            && now - entry.DeathStartedTicks >= 12000);
        var enemyLikelyReturningSoon = players.Count(player =>
            player.Relation == BattlefieldPlayerRelation.Enemy
            && player.IsDead
            && historyById.TryGetValue(player.GameObjectId, out var entry)
            && entry.DeathStartedTicks > 0
            && now - entry.DeathStartedTicks >= 12000);
        var friendlyDeathWave = BuildRespawnWave(historyEntries.Where(entry => IsFriendlySide(entry.Relation)).Select(entry => entry.LastDeathTicks), now);
        var enemyDeathWave = BuildRespawnWave(historyEntries.Where(entry => entry.Relation == BattlefieldPlayerRelation.Enemy).Select(entry => entry.LastDeathTicks), now);
        var friendlyReviveWave = BuildRespawnWave(historyEntries.Where(entry => IsFriendlySide(entry.Relation)).Select(entry => entry.LastReviveTicks), now);
        var enemyReviveWave = BuildRespawnWave(historyEntries.Where(entry => entry.Relation == BattlefieldPlayerRelation.Enemy).Select(entry => entry.LastReviveTicks), now);
        var confidence = CalculateRespawnConfidence(
            friendlyDeadNow + enemyDeadNow,
            friendlyRecentlyDied + enemyRecentlyDied,
            friendlyRecentlyRevived + enemyRecentlyRevived,
            Math.Max(friendlyDeathWave.Size, enemyDeathWave.Size),
            Math.Max(friendlyReviveWave.Size, enemyReviveWave.Size));

        return new BattlefieldRespawnRhythmSnapshot
        {
            FriendlyDeadNow = friendlyDeadNow,
            EnemyDeadNow = enemyDeadNow,
            FriendlyRecentlyDied = friendlyRecentlyDied,
            EnemyRecentlyDied = enemyRecentlyDied,
            FriendlyRecentlyRevived = friendlyRecentlyRevived,
            EnemyRecentlyRevived = enemyRecentlyRevived,
            FriendlyLikelyReturningSoon = friendlyLikelyReturningSoon,
            EnemyLikelyReturningSoon = enemyLikelyReturningSoon,
            FriendlyDeathWaveSize = friendlyDeathWave.Size,
            EnemyDeathWaveSize = enemyDeathWave.Size,
            FriendlyReviveWaveSize = friendlyReviveWave.Size,
            EnemyReviveWaveSize = enemyReviveWave.Size,
            FriendlyDeathWaveAgeMs = friendlyDeathWave.AgeMs,
            EnemyDeathWaveAgeMs = enemyDeathWave.AgeMs,
            FriendlyReviveWaveAgeMs = friendlyReviveWave.AgeMs,
            EnemyReviveWaveAgeMs = enemyReviveWave.AgeMs,
            Confidence = confidence,
            SummaryText = BuildRespawnSummary(
                friendlyDeadNow,
                enemyDeadNow,
                friendlyRecentlyDied,
                enemyRecentlyDied,
                friendlyRecentlyRevived,
                enemyRecentlyRevived,
                friendlyLikelyReturningSoon,
                enemyLikelyReturningSoon,
                friendlyDeathWave,
                enemyDeathWave,
                friendlyReviveWave,
                enemyReviveWave,
                confidence)
        };
    }

    private BattlefieldGroupMovementSnapshot BuildEnemyGroupMovement(
        IReadOnlyList<BattlefieldEnemyClusterSnapshot> enemyClusters,
        IReadOnlyList<BattlefieldPlayerClusterSnapshot> playerClusters,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        long now,
        bool isEnemySplit)
    {
        var tacticalMainCluster = enemyClusters.FirstOrDefault(cluster => cluster.IsMainCluster);
        if (tacticalMainCluster is { Count: > 0 })
            return BuildEnemyGroupMovementFromCluster(
                tacticalMainCluster.Center,
                tacticalMainCluster.Count,
                tacticalMainCluster.SourceText,
                tacticalMainCluster.Battalion,
                tacticalMainCluster.AllianceName,
                enemyClusters.Count,
                isEnemySplit,
                0.86f,
                now);

        var mapVisionEnemyCluster = mapVisionClusters
            .Where(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy)
            .OrderByDescending(cluster => cluster.PointCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .FirstOrDefault();

        if (mapVisionEnemyCluster.PointCount > 0)
            return BuildEnemyGroupMovementFromCluster(
                mapVisionEnemyCluster.Center,
                mapVisionEnemyCluster.PointCount,
                "地图视野",
                mapVisionEnemyCluster.Battalion,
                AllianceName(mapVisionEnemyCluster.Battalion),
                mapVisionClusters.Count(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy),
                isEnemySplit,
                0.78f,
                now);

        var nearbyEnemyCluster = playerClusters
            .Where(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy)
            .OrderByDescending(cluster => cluster.PlayerCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .FirstOrDefault();

        if (nearbyEnemyCluster.PlayerCount <= 0)
            return BuildEnemyGroupMovementFromMemory(now, isEnemySplit);

        return BuildEnemyGroupMovementFromCluster(
            nearbyEnemyCluster.Center,
            nearbyEnemyCluster.PlayerCount,
            "附近实体",
            nearbyEnemyCluster.Battalion,
            AllianceName(nearbyEnemyCluster.Battalion),
            playerClusters.Count(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy),
            isEnemySplit,
            0.92f,
            now);
    }

    private BattlefieldGroupMovementSnapshot BuildEnemyGroupMovementFromCluster(
        Vector3 center,
        int playerCount,
        string sourceText,
        byte? battalion,
        string allianceName,
        int enemyClusterCount,
        bool isEnemySplit,
        float baseConfidence,
        long now)
    {
        var countUnit = sourceText.Contains("附近实体", StringComparison.Ordinal) ? "人" : "点";
        var movementSourceKey = $"{sourceText}:{battalion?.ToString() ?? "未知"}";
        if (!string.Equals(lastEnemyMovementSource, movementSourceKey, StringComparison.Ordinal))
        {
            enemyClusterHistory.Clear();
            cachedEnemyMainGroupTrack = Array.Empty<BattlefieldGroupTrackSnapshot>();
            lastEnemyMainGroupTrackBuildTicks = 0;
            lastEnemyMovementSource = movementSourceKey;
        }

        enemyClusterHistory.Enqueue(new EnemyClusterHistoryEntry(now, center, playerCount, sourceText));
        while (enemyClusterHistory.Count > 0 && now - enemyClusterHistory.Peek().Ticks > TacticalHistoryExpiryMs)
            enemyClusterHistory.Dequeue();

        var previousFrame = enemyClusterHistory
            .Where(frame => frame.Ticks < now)
            .OrderByDescending(frame => frame.Ticks)
            .FirstOrDefault();
        var hasMovementSample = previousFrame.Ticks > 0;
        var transition = AnalyzeEnemyMainGroupTransition(center, battalion, now);
        var confidence = CalculateEnemyGroupConfidence(baseConfidence, 0, playerCount, true, hasMovementSample);

        if (previousFrame.Ticks <= 0)
        {
            lastEnemyMainGroupObservation = new EnemyMainGroupObservationEntry(
                now,
                center,
                Vector3.Zero,
                playerCount,
                battalion,
                allianceName,
                sourceText,
                0f);

            return new BattlefieldGroupMovementSnapshot
            {
                HasMainGroup = true,
                SourceText = sourceText,
                Battalion = battalion,
                AllianceName = allianceName,
                CurrentCenter = center,
                PredictedNextCenter = center,
                PlayerCount = playerCount,
                Confidence = confidence,
                ObservationAgeMs = 0,
                IsEnemySplit = isEnemySplit,
                EnemyClusterCount = enemyClusterCount,
                SummaryText = $"敌方主团 {allianceName} {playerCount} {countUnit}（{sourceText}），中心 {FormatVector(center)}，正在积累连续移动样本",
            };
        }

        var delta = center - previousFrame.Center;
        delta.Y = 0f;
        var predictedNextCenter = center + delta;
        var distance = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        var elapsedSeconds = Math.Max(0.001f, (now - previousFrame.Ticks) / 1000f);
        var speed = distance / elapsedSeconds;
        var direction = DescribeDirection(delta);
        var directionText = string.IsNullOrWhiteSpace(direction) ? "静止" : direction;
        lastEnemyMainGroupObservation = new EnemyMainGroupObservationEntry(
            now,
            center,
            delta,
            playerCount,
            battalion,
            allianceName,
                sourceText,
                speed);
        var summaryText = distance < MovementMinDistance
            ? $"敌方主团 {allianceName} {playerCount} {countUnit}（{sourceText}），中心 {FormatVector(center)}，连续位移变化不大"
            : $"敌方主团 {allianceName} {playerCount} {countUnit}（{sourceText}），向 {directionText} 移动，位移 {FormatVector(delta)}，预测下一步 {FormatVector(predictedNextCenter)}，约 {speed:0.0} 米/秒";

        return new BattlefieldGroupMovementSnapshot
        {
            HasMainGroup = true,
            SourceText = sourceText,
            Battalion = battalion,
            AllianceName = allianceName,
            CurrentCenter = center,
            PreviousCenter = previousFrame.Center,
            Delta = delta,
            PredictedNextCenter = predictedNextCenter,
            PlayerCount = playerCount,
            SpeedPerSecond = speed,
            Confidence = confidence,
            ObservationAgeMs = 0,
            IsRotationLikely = transition.IsRotationLikely,
            IsTeleportLikely = transition.IsTeleportLikely,
            TransitionText = transition.Text,
            DirectionText = direction,
            IsEnemySplit = isEnemySplit,
            EnemyClusterCount = enemyClusterCount,
            SummaryText = summaryText,
        };
    }

    private BattlefieldGroupMovementSnapshot BuildEnemyGroupMovementFromMemory(long now, bool isEnemySplit)
    {
        if (!lastEnemyMainGroupObservation.HasValue)
        {
            return new BattlefieldGroupMovementSnapshot
            {
                SourceText = "no-sample",
                SummaryText = "敌方主团方向样本不足",
            };
        }

        var observation = lastEnemyMainGroupObservation.Value;
        var ageMs = Math.Max(0, now - observation.Ticks);
        if (ageMs > EnemyMainGroupMemoryTtlMs)
        {
            return new BattlefieldGroupMovementSnapshot
            {
                SourceText = "memory-expired",
                ObservationAgeMs = ageMs,
                SummaryText = "敌方主团记忆样本已过期",
            };
        }

        var predictedCenter = observation.Center;
        var hasMotion = Length2D(observation.Delta) >= MovementMinDistance && observation.SpeedPerSecond > 0.5f;
        if (hasMotion && ageMs <= EnemyMainGroupPredictiveWindowMs)
        {
            var directionVector = new Vector3(observation.Delta.X, 0f, observation.Delta.Z);
            if (directionVector != Vector3.Zero)
            {
                directionVector = Vector3.Normalize(directionVector);
                var predictedDistance = MathF.Min(32f, observation.SpeedPerSecond * (ageMs / 1000f) * 0.45f);
                predictedCenter += directionVector * predictedDistance;
            }
        }

        var confidence = CalculateEnemyGroupConfidence(0.62f, ageMs, observation.PlayerCount, false, hasMotion);
        return new BattlefieldGroupMovementSnapshot
        {
            HasMainGroup = true,
            SourceText = $"{observation.SourceText}/memory",
            Battalion = observation.Battalion,
            AllianceName = observation.AllianceName,
            CurrentCenter = observation.Center,
            PreviousCenter = observation.Center - observation.Delta,
            Delta = observation.Delta,
            PredictedNextCenter = predictedCenter,
            PlayerCount = observation.PlayerCount,
            SpeedPerSecond = observation.SpeedPerSecond,
            Confidence = confidence,
            ObservationAgeMs = ageMs,
            IsMemoryEstimate = true,
            DirectionText = hasMotion ? DescribeDirection(observation.Delta) : string.Empty,
            IsEnemySplit = isEnemySplit,
            EnemyClusterCount = 0,
            SummaryText = $"敌方主团暂时脱视野，保留 {ageMs / 1000f:0.0}s 前位置 {FormatVector(observation.Center)}，置信 {confidence * 100f:0}% ，预测 {FormatVector(predictedCenter)}",
        };
    }

    private (bool IsRotationLikely, bool IsTeleportLikely, string Text) AnalyzeEnemyMainGroupTransition(
        Vector3 center,
        byte? battalion,
        long now)
    {
        if (!lastEnemyMainGroupObservation.HasValue)
            return (false, false, string.Empty);

        var previous = lastEnemyMainGroupObservation.Value;
        var ageMs = Math.Max(0, now - previous.Ticks);
        if (ageMs <= 0 || ageMs > EnemyMainGroupMemoryTtlMs)
            return (false, false, string.Empty);

        if (battalion.HasValue && previous.Battalion.HasValue && battalion.Value != previous.Battalion.Value)
            return (false, false, string.Empty);

        var distance = Distance2D(previous.Center, center);
        if (distance < EnemyMainGroupRotationDistanceThreshold)
            return (false, false, string.Empty);

        var speed = distance / Math.Max(0.001f, ageMs / 1000f);
        if (speed >= EnemyMainGroupTeleportSpeedThreshold)
            return (false, true, $"疑似传送/跳线 {distance:0}y/{ageMs / 1000f:0.0}s");

        return (true, false, $"疑似整队转线 {distance:0}y/{ageMs / 1000f:0.0}s");
    }

    private static float CalculateEnemyGroupConfidence(
        float baseConfidence,
        long ageMs,
        int sampleCount,
        bool directObserved,
        bool hasMotionSample)
    {
        var confidence = baseConfidence;
        confidence += Math.Min(0.10f, Math.Max(0, sampleCount - 1) * 0.012f);
        if (hasMotionSample)
            confidence += 0.04f;
        if (!directObserved)
            confidence -= 0.06f;

        return DecayObservationConfidence(confidence, ageMs, 3500, EnemyMainGroupMemoryTtlMs, 0.18f);
    }

    private static float DecayObservationConfidence(
        float baseConfidence,
        long ageMs,
        long freshWindowMs,
        long maxAgeMs,
        float floor)
    {
        var clampedBase = Math.Clamp(baseConfidence, floor, 0.98f);
        if (ageMs <= freshWindowMs)
            return clampedBase;

        if (ageMs >= maxAgeMs)
            return floor;

        var t = (float)(ageMs - freshWindowMs) / Math.Max(1, maxAgeMs - freshWindowMs);
        return Math.Clamp(clampedBase - (clampedBase - floor) * t, floor, clampedBase);
    }

    private BattlefieldAdvancedTacticalSituationSnapshot BuildAdvancedTacticalSituation(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        long now)
    {
        var tuning = configuration.AdvancedTactics;
        tuning.Normalize();
        if (!tuning.Enabled)
        {
            return new BattlefieldAdvancedTacticalSituationSnapshot
            {
                IsAvailable = false,
                SourceText = "高级战术洞察已关闭",
                CalibrationText = BuildAdvancedTacticsCalibrationText(tuning),
                SummaryText = "高级战术洞察已关闭",
            };
        }

        var rawInsights = new List<BattlefieldAdvancedTacticalInsightSnapshot>();
        var localPlayer = TryGetLocalPlayer(players);
        var friendlyCenter = ResolveFriendlyTacticalCenter(localPlayer, teamSituation);
        var enemyCenter = ResolveEnemyTacticalCenter(teamSituation, mapVisionClusters);

        var cohesion = BuildFriendlyCohesionInsight(players, localPlayer, enemyCenter, now, tuning);
        if (cohesion.Insight.HasValue)
            rawInsights.Add(cohesion.Insight.Value);

        rawInsights.AddRange(BuildEnemyRetreatInsights(players, friendlyCenter, teamSituation, mapTactics, now, tuning));

        var highGround = BuildHighGroundDropPrepInsight(players, friendlyCenter, mapTactics, now, tuning);
        if (highGround.HasValue)
            rawInsights.Add(highGround.Value);

        var pincer = BuildThirdPartyPincerInsight(teamSituation, friendlyCenter, now, tuning);
        if (pincer.HasValue)
            rawInsights.Add(pincer.Value);

        var squad = BuildCoordinatedSquadInsight(players, now, tuning);
        if (squad.HasValue)
            rawInsights.Add(squad.Value);

        var choke = BuildChokeBlockedInsight(friendlyCenter, mapTactics, tuning);
        if (choke.HasValue)
            rawInsights.Add(choke.Value);

        var acceptedInsights = rawInsights
            .Where(insight => ShouldAcceptAdvancedTacticalInsight(insight, tuning))
            .ToArray();
        var suppressedInsights = rawInsights
            .Where(insight => !ShouldAcceptAdvancedTacticalInsight(insight, tuning))
            .OrderByDescending(insight => insight.Severity * Math.Clamp(insight.Confidence, 0.1f, 1f))
            .ThenByDescending(insight => insight.Severity)
            .Take(8)
            .ToArray();

        var ordered = acceptedInsights
            .OrderByDescending(insight => insight.Severity * Math.Clamp(insight.Confidence, 0.1f, 1f))
            .ThenByDescending(insight => insight.Severity)
            .Take(8)
            .ToArray();

        var ambushRisk = ordered
            .Where(insight => insight.Kind is BattlefieldAdvancedTacticalInsightKind.EnemyFakeRetreatAmbush or BattlefieldAdvancedTacticalInsightKind.EnemyRetreat)
            .Select(insight => insight.Kind == BattlefieldAdvancedTacticalInsightKind.EnemyRetreat ? insight.Severity * 0.62f : insight.Severity)
            .DefaultIfEmpty(0f)
            .Max();
        var highGroundRisk = ResolveInsightRisk(ordered, BattlefieldAdvancedTacticalInsightKind.HighGroundDropPrep);
        var pincerRisk = ResolveInsightRisk(ordered, BattlefieldAdvancedTacticalInsightKind.ThirdPartyPincer);
        var squadRisk = ResolveInsightRisk(ordered, BattlefieldAdvancedTacticalInsightKind.CoordinatedSquad);
        var chokeRisk = ResolveInsightRisk(ordered, BattlefieldAdvancedTacticalInsightKind.ChokeBlocked);
        var cohesionRisk = cohesion.SampleCount > 0 ? Math.Clamp(100f - cohesion.CohesionScore, 0f, 100f) : 35f;
        var summary = BuildAdvancedTacticalSummaryCalibrated(ordered, cohesion.FollowRate, cohesion.CohesionScore, rawInsights.Count, suppressedInsights.Length);
        var calibrationText = BuildAdvancedTacticsCalibrationText(tuning);

        return new BattlefieldAdvancedTacticalSituationSnapshot
        {
            IsAvailable = players.Count > 0 || mapTactics.IsAvailable,
            Insights = ordered,
            SuppressedInsights = suppressedInsights,
            TopInsight = ordered.Length > 0 ? ordered[0] : null,
            RawInsightCount = rawInsights.Count,
            SuppressedInsightCount = suppressedInsights.Length,
            FriendlyFollowRate = cohesion.FollowRate,
            FriendlyDirectionConsistency = cohesion.DirectionConsistency,
            FriendlyCohesionScore = cohesion.CohesionScore,
            FriendlyFollowerCount = cohesion.FollowerCount,
            FriendlySampleCount = cohesion.SampleCount,
            IsEnemyRetreating = ordered.Any(insight => insight.Kind == BattlefieldAdvancedTacticalInsightKind.EnemyRetreat),
            IsEnemyFakeRetreatAmbushLikely = ambushRisk >= tuning.FakeRetreatLikelyRisk && ordered.Any(insight => insight.Kind == BattlefieldAdvancedTacticalInsightKind.EnemyFakeRetreatAmbush),
            IsHighGroundDropPrepLikely = highGroundRisk >= tuning.HighGroundLikelyRisk,
            IsThirdPartyPincerLikely = pincerRisk >= tuning.ThirdPartyLikelyRisk,
            IsCoordinatedSquadLikely = squadRisk >= tuning.SquadLikelyRisk,
            IsChokeBlockedLikely = chokeRisk >= tuning.ChokeLikelyRisk,
            AmbushRisk = Math.Clamp(ambushRisk, 0f, 100f),
            CohesionRisk = cohesionRisk,
            HighGroundDropRisk = highGroundRisk,
            ThirdPartyPincerRisk = pincerRisk,
            CoordinatedSquadRisk = squadRisk,
            ChokeBlockRisk = chokeRisk,
            CalibrationText = calibrationText,
            SummaryText = summary,
        };
    }

    private (BattlefieldAdvancedTacticalInsightSnapshot? Insight, float FollowRate, float DirectionConsistency, float CohesionScore, int FollowerCount, int SampleCount) BuildFriendlyCohesionInsight(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        BattlefieldPlayerSnapshot? localPlayer,
        Vector3? enemyCenter,
        long now,
        AdvancedTacticsCalibrationConfiguration tuning)
    {
        if (!localPlayer.HasValue)
            return (null, 0f, 0f, 0f, 0, 0);

        var local = localPlayer.Value;
        var friendlies = players
            .Where(player => player.GameObjectId != local.GameObjectId && IsFriendlySide(player.Relation) && !player.IsDead)
            .ToArray();
        if (friendlies.Length == 0)
            return (null, 100f, 100f, 100f, 0, 0);

        var hasCommandDirection = TryGetPlayerMovement(local.GameObjectId, now, out var localDelta, out var localSpeed, out _, 4500)
            && localSpeed >= 0.75f
            && Length2D(localDelta) >= 0.8f;
        var commandDirection = hasCommandDirection
            ? localDelta
            : enemyCenter.HasValue
                ? enemyCenter.Value - local.Position
                : Vector3.Zero;
        commandDirection.Y = 0f;
        if (Length2D(commandDirection) < 2f)
            hasCommandDirection = false;

        var followers = 0;
        var directionSamples = 0;
        var directionScore = 0f;
        foreach (var player in friendlies)
        {
            var distance = Distance2D(player.Position, local.Position);
            var nearCommander = distance <= tuning.FollowNearDistance || (player.IsMounted && distance <= tuning.FollowMountedDistance);
            var directionOk = !hasCommandDirection;
            if (TryGetPlayerMovement(player.GameObjectId, now, out var playerDelta, out var playerSpeed, out _, 4500) && playerSpeed >= 0.65f)
            {
                if (hasCommandDirection)
                {
                    var dot = Dot2DNormalized(playerDelta, commandDirection);
                    directionScore += Math.Clamp((dot + 1f) * 50f, 0f, 100f);
                    directionSamples++;
                    directionOk = dot >= tuning.DirectionDotThresholdPercent / 100f;
                }
                else if (distance > tuning.FollowNearDistance)
                {
                    directionOk = Dot2DNormalized(playerDelta, local.Position - player.Position) >= tuning.DirectionDotThresholdPercent / 100f;
                }
            }

            if (distance <= tuning.FollowDirectDistance || (nearCommander && directionOk))
                followers++;
        }

        var followRate = friendlies.Length > 0 ? followers * 100f / friendlies.Length : 100f;
        var directionConsistency = directionSamples > 0 ? directionScore / directionSamples : (hasCommandDirection ? 55f : 100f);
        var cohesionScore = Math.Clamp(followRate * 0.72f + directionConsistency * 0.28f, 0f, 100f);
        var severity = Math.Clamp(100f - cohesionScore, 0f, 100f);
        var recommendation = cohesionScore < tuning.CohesionLowThreshold
            ? "先集合，不开团"
            : cohesionScore < tuning.CohesionMediumThreshold
                ? "放慢节奏，等队形"
                : "凝聚可用，可以执行主团动作";
        var evidence = $"跟随 {followers}/{friendlies.Length}，跟随率 {followRate:0}%；方向一致 {directionConsistency:0}%；凝聚 {cohesionScore:0}";

        var insight = new BattlefieldAdvancedTacticalInsightSnapshot(
            BattlefieldAdvancedTacticalInsightKind.FriendlyCohesion,
            "友方跟随/凝聚",
            severity,
            friendlies.Length >= tuning.CohesionMinSampleCount ? 0.86f : 0.42f,
            local.Position,
            friendlies.Length,
            NormalizeBattalion(local.Battalion),
            AllianceName(NormalizeBattalion(local.Battalion)),
            recommendation,
            evidence);
        return (insight, followRate, directionConsistency, cohesionScore, followers, friendlies.Length);
    }

    private IEnumerable<BattlefieldAdvancedTacticalInsightSnapshot> BuildEnemyRetreatInsights(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        Vector3? friendlyCenter,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldMapTacticsSnapshot mapTactics,
        long now,
        AdvancedTacticsCalibrationConfiguration tuning)
    {
        if (!friendlyCenter.HasValue || !teamSituation.EnemyMainGroupMovement.HasMainGroup)
            yield break;

        var movement = teamSituation.EnemyMainGroupMovement;
        if (movement.IsMemoryEstimate && (movement.ObservationAgeMs > 4500 || movement.Confidence < 0.45f))
            yield break;

        if (Length2D(movement.Delta) < 2.5f || movement.SpeedPerSecond < tuning.RetreatMinSpeed)
            yield break;

        var currentDistance = Distance2D(movement.CurrentCenter, friendlyCenter.Value);
        var previousDistance = Distance2D(movement.PreviousCenter, friendlyCenter.Value);
        var retreatDistanceDelta = currentDistance - previousDistance;
        var movingAwayDot = Dot2DNormalized(movement.Delta, movement.CurrentCenter - friendlyCenter.Value);
        var enemyMovers = players
            .Where(player => player.Relation == BattlefieldPlayerRelation.Enemy && !player.IsDead)
            .Select(player => TryGetPlayerMovement(player.GameObjectId, now, out var delta, out var speed, out _, 4500)
                ? (Player: player, Delta: delta, Speed: speed)
                : (Player: player, Delta: Vector3.Zero, Speed: 0f))
            .Where(item => item.Speed >= 0.75f)
            .ToArray();
        var movingAwayCount = enemyMovers.Count(item => Dot2DNormalized(item.Delta, item.Player.Position - friendlyCenter.Value) >= 0.25f);
        var groupRetreating = retreatDistanceDelta >= tuning.RetreatMinDistanceDelta && movingAwayDot >= tuning.DirectionDotThresholdPercent / 100f;
        var movingAwayRatio = Math.Clamp(tuning.RetreatMovingAwayRatioPercent / 100f, 0f, 1f);
        var collectiveRetreating = enemyMovers.Length >= tuning.RetreatMinMovingSamples
            && movingAwayCount >= Math.Max(tuning.RetreatMinMovingSamples, (int)MathF.Ceiling(enemyMovers.Length * movingAwayRatio));
        if (!groupRetreating && !collectiveRetreating)
            yield break;

        var movementConfidenceScale = Math.Clamp(movement.Confidence, 0.35f, 1f);
        var confidence = Math.Clamp(
            (0.48f + MathF.Min(0.24f, retreatDistanceDelta / 45f) + MathF.Min(0.18f, movingAwayCount / 16f)) * (0.72f + movementConfidenceScale * 0.28f),
            0.35f,
            0.92f);
        var retreatSeverity = Math.Clamp(42f + retreatDistanceDelta * 1.8f + movement.SpeedPerSecond * 2f, 35f, 74f);
        yield return new BattlefieldAdvancedTacticalInsightSnapshot(
            BattlefieldAdvancedTacticalInsightKind.EnemyRetreat,
            "敌方集体后撤",
            retreatSeverity,
            confidence,
            movement.CurrentCenter,
            Math.Max(movement.PlayerCount, movingAwayCount),
            movement.Battalion,
            movement.AllianceName,
            "不要深追，保持队形和视野",
            $"敌主团距离我方 +{retreatDistanceDelta:0.0}y，速度 {movement.SpeedPerSecond:0.0}y/s，远离样本 {movingAwayCount}/{Math.Max(1, enemyMovers.Length)}");

        var readyThreats = teamSituation.LimitBreakThreats.EnemyHighThreatCount
            + teamSituation.KeySkillThreats.EnemyHighThreatCount
            + teamSituation.KeySkillThreats.EnemyControlChainCount
            + teamSituation.Enemy.BattleFeverCount;
        var sideClusterNear = teamSituation.EnemyClusters
            .Skip(1)
            .Any(cluster => Distance2D(cluster.Center, friendlyCenter.Value) <= tuning.FakeRetreatSideClusterDistance && cluster.Count >= tuning.FakeRetreatSideClusterMinCount);
        var trapZoneRisk = mapTactics.IsAvailable
            ? mapTactics.TopZones
                .Where(zone => zone.Kind is MapAnnotationKind.Flank or MapAnnotationKind.Choke or MapAnnotationKind.Danger or MapAnnotationKind.Underpass || zone.IsMandatoryChoke)
                .Where(zone => Distance2D(zone.Position, movement.PredictedNextCenter) <= MathF.Max(zone.Radius, 28f) + 55f)
                .Select(zone => zone.TotalRisk)
                .DefaultIfEmpty(0f)
                .Max()
            : 0f;
        var ambushSeverity = Math.Clamp(
            retreatSeverity * 0.35f
            + readyThreats * 8f
            + (teamSituation.IsEnemySplit ? 18f : 0f)
            + (sideClusterNear ? 14f : 0f)
            + trapZoneRisk * 0.32f,
            0f,
            100f);
        if (ambushSeverity < tuning.FakeRetreatMinSeverity || readyThreats < tuning.FakeRetreatMinThreatSignals)
            yield break;

        yield return new BattlefieldAdvancedTacticalInsightSnapshot(
            BattlefieldAdvancedTacticalInsightKind.EnemyFakeRetreatAmbush,
            "疑似假撤退设伏",
            ambushSeverity,
            Math.Clamp(confidence + 0.05f + (trapZoneRisk >= 55f ? 0.08f : 0f), 0.42f, 0.94f),
            movement.PredictedNextCenter,
            movement.PlayerCount,
            movement.Battalion,
            movement.AllianceName,
            "停止深追，收缩等视野，先清侧翼",
            $"后撤同时高威胁 {readyThreats}，分兵 {teamSituation.IsEnemySplit}，侧翼簇 {sideClusterNear}，前方地形风险 {trapZoneRisk:0}");
    }

    private BattlefieldAdvancedTacticalInsightSnapshot? BuildHighGroundDropPrepInsight(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        Vector3? friendlyCenter,
        BattlefieldMapTacticsSnapshot mapTactics,
        long now,
        AdvancedTacticsCalibrationConfiguration tuning)
    {
        if (!friendlyCenter.HasValue)
            return null;

        var candidates = new List<HighGroundDropCandidate>();
        if (mapTactics.IsAvailable)
        {
            candidates.AddRange(mapTactics.TopZones
                .Where(zone => zone.Kind == MapAnnotationKind.HighGround || (zone.IsCliffOrHighPlatform && zone.HeightDeltaToLocal > tuning.HighGroundMinHeightDelta))
                .Select(zone =>
                {
                    var enemies = players
                        .Where(player => player.Relation == BattlefieldPlayerRelation.Enemy && !player.IsDead)
                        .Where(player => Distance2D(player.Position, zone.Position) <= MathF.Max(zone.Radius, 24f) + 16f)
                        .ToArray();
                    var facingOrMoving = enemies.Count(player => IsFacingOrMovingToward(player, friendlyCenter.Value, now));
                    var pressure = enemies.Length + zone.EnemyMapVisionNearby;
                    var severity = Math.Clamp(30f + pressure * 8f + facingOrMoving * 7f + zone.HighLimitBreakEnemies * 12f + zone.HighBattleHighEnemies * 8f + (zone.HeightDeltaToLocal > 5f ? 12f : 0f), 0f, 100f);
                    return new HighGroundDropCandidate(
                        string.IsNullOrWhiteSpace(zone.Label) ? "楂樺彴" : zone.Label,
                        zone.Position,
                        enemies.Length,
                        facingOrMoving,
                        pressure,
                        severity,
                        zone.HeightDeltaToLocal,
                        "地图标注");
                })
                .Where(item => item.Pressure >= tuning.HighGroundMinPressure && item.Severity >= tuning.HighGroundMinSeverity));
        }

        var minHeightDelta = MathF.Max(5f, tuning.HighGroundMinHeightDelta);
        var verticalEnemies = players
            .Where(player => player.Relation == BattlefieldPlayerRelation.Enemy && !player.IsDead)
            .Select(player => new
            {
                Player = player,
                HeightDelta = player.Position.Y - friendlyCenter.Value.Y,
                Distance = Distance2D(player.Position, friendlyCenter.Value)
            })
            .Where(item => item.HeightDelta >= minHeightDelta && item.Distance <= 95f)
            .ToArray();
        if (verticalEnemies.Length > 0)
        {
            var facingOrMoving = verticalEnemies.Count(item => IsFacingOrMovingToward(item.Player, friendlyCenter.Value, now));
            var highBattleHigh = verticalEnemies.Count(item => item.Player.BattleHighLevel >= 4 || item.Player.IsBattleFever);
            var maxHeightDelta = verticalEnemies.Max(item => item.HeightDelta);
            var center = new Vector3(
                verticalEnemies.Average(item => item.Player.Position.X),
                verticalEnemies.Average(item => item.Player.Position.Y),
                verticalEnemies.Average(item => item.Player.Position.Z));
            var pressure = verticalEnemies.Length;
            var severity = Math.Clamp(26f + pressure * 9f + facingOrMoving * 7f + highBattleHigh * 8f + Math.Min(18f, maxHeightDelta * 2f), 0f, 100f);
            if (pressure >= Math.Max(2, tuning.HighGroundMinPressure) && severity >= tuning.HighGroundMinSeverity)
            {
                candidates.Add(new HighGroundDropCandidate(
                    "Y轴高点",
                    center,
                    verticalEnemies.Length,
                    facingOrMoving,
                    pressure,
                    severity,
                    maxHeightDelta,
                    "Y坐标"));
            }
        }

        var best = candidates
            .OrderByDescending(item => item.Severity)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(best.Label))
            return null;

        var confidence = Math.Clamp(0.48f + best.Facing * 0.08f + (best.HeightDelta > 5f ? 0.14f : 0f) + (best.Source == "Y坐标" ? 0.06f : 0f), 0.45f, 0.9f);
        return new BattlefieldAdvancedTacticalInsightSnapshot(
            BattlefieldAdvancedTacticalInsightKind.HighGroundDropPrep,
            "敌方高台空降预警",
            best.Severity,
            confidence,
            best.Position,
            best.Pressure,
            null,
            "敌方",
            "散开，离开落点，不要在低地堆叠",
            $"{best.Label} 敌方压力 {best.Pressure}，面朝/移动低处 {best.Facing}/{Math.Max(1, best.Enemies)}，高度差 {best.HeightDelta:0.0}y，来源 {best.Source}");
    }

    private BattlefieldAdvancedTacticalInsightSnapshot? BuildThirdPartyPincerInsight(
        BattlefieldTeamSituationSnapshot teamSituation,
        Vector3? friendlyCenter,
        long now,
        AdvancedTacticsCalibrationConfiguration tuning)
    {
        if (!friendlyCenter.HasValue
            || !TryResolveAllianceCenter(teamSituation.EnemyAlliance1, out var first)
            || !TryResolveAllianceCenter(teamSituation.EnemyAlliance2, out var second))
            return null;

        var firstVector = first.Center - friendlyCenter.Value;
        var secondVector = second.Center - friendlyCenter.Value;
        var firstDistance = Length2D(firstVector);
        var secondDistance = Length2D(secondVector);
        var angle = AngleBetweenDegrees(firstVector, secondVector);
        if (firstDistance > tuning.ThirdPartyMaxDistance || secondDistance > tuning.ThirdPartyMaxDistance || angle < tuning.ThirdPartyMinAngle)
            return null;

        var firstApproach = ResolveAllianceApproachRatio(teamSituation.EnemyAlliance1?.VisiblePlayers ?? Array.Empty<BattlefieldPlayerSnapshot>(), friendlyCenter.Value, now);
        var secondApproach = ResolveAllianceApproachRatio(teamSituation.EnemyAlliance2?.VisiblePlayers ?? Array.Empty<BattlefieldPlayerSnapshot>(), friendlyCenter.Value, now);
        var closestBonus = firstDistance <= 95f || secondDistance <= 95f ? 14f : 0f;
        var severity = Math.Clamp(28f + (angle - 90f) * 0.35f + (tuning.ThirdPartyMaxDistance - MathF.Min(firstDistance, secondDistance)) * 0.18f + (firstApproach + secondApproach) * 18f + closestBonus, 0f, 100f);
        if (severity < tuning.ThirdPartyMinSeverity)
            return null;

        return new BattlefieldAdvancedTacticalInsightSnapshot(
            BattlefieldAdvancedTacticalInsightKind.ThirdPartyPincer,
            "第三方夹击风险",
            severity,
            Math.Clamp(0.50f + (angle - 90f) / 180f + (firstApproach + secondApproach) * 0.10f, 0.45f, 0.93f),
            friendlyCenter.Value,
            first.Count + second.Count,
            null,
            $"{first.Name}/{second.Name}",
            "立即收缩撤退，或反打最近一队",
            $"{first.Name} {firstDistance:0}y，{second.Name} {secondDistance:0}y，夹角 {angle:0} 度，接近率 {firstApproach:0.0}/{secondApproach:0.0}");
    }

    private BattlefieldAdvancedTacticalInsightSnapshot? BuildCoordinatedSquadInsight(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        long now,
        AdvancedTacticsCalibrationConfiguration tuning)
    {
        var enemies = players
            .Where(player => player.Relation == BattlefieldPlayerRelation.Enemy && !player.IsDead)
            .ToArray();
        var best = default(CoordinatedSquadWork);
        foreach (var allianceGroup in enemies.GroupBy(player => NormalizeBattalion(player.Battalion)))
        {
            var groupPlayers = allianceGroup.ToArray();
            foreach (var anchor in groupPlayers)
            {
                var squad = groupPlayers
                    .Where(player => Distance2D(player.Position, anchor.Position) <= tuning.SquadSearchRadius)
                    .OrderBy(player => Distance2D(player.Position, anchor.Position))
                    .Take(4)
                    .ToArray();
                if (squad.Length < 4)
                    continue;

                var moving = squad
                    .Select(player => TryGetPlayerMovement(player.GameObjectId, now, out var delta, out var speed, out _, 5500)
                        ? (Player: player, Delta: delta, Speed: speed)
                        : (Player: player, Delta: Vector3.Zero, Speed: 0f))
                    .Where(item => item.Speed >= 0.65f && Length2D(item.Delta) >= 0.8f)
                    .ToArray();
                if (moving.Length < 3)
                    continue;

                var directionSimilarity = AveragePairwiseDirectionSimilarity(moving.Select(item => item.Delta).ToArray());
                var formationStability = ResolveFormationStabilityScore(squad, now);
                var radius = squad.Max(player => Distance2D(player.Position, AveragePosition(squad)));
                var score = Math.Clamp(directionSimilarity * 45f + formationStability * 0.38f + Math.Max(0f, 24f - radius) * 0.7f, 0f, 100f);
                if (score <= best.Score || score < tuning.SquadMinScore || directionSimilarity < tuning.SquadMinDirectionSimilarityPercent / 100f || formationStability < tuning.SquadMinFormationStability)
                    continue;

                best = new CoordinatedSquadWork(score, directionSimilarity, formationStability, radius, squad, allianceGroup.Key);
            }
        }

        if (best.Players == null || best.Players.Length < 4)
            return null;

        var center = AveragePosition(best.Players);
        var names = string.Join("/", best.Players.Take(4).Select(player => player.Name));
        return new BattlefieldAdvancedTacticalInsightSnapshot(
            BattlefieldAdvancedTacticalInsightKind.CoordinatedSquad,
            "敌方固定4人协同",
            Math.Clamp(best.Score, 0f, 100f),
            Math.Clamp(0.44f + best.DirectionSimilarity * 0.26f + best.FormationStability * 0.003f, 0.45f, 0.88f),
            center,
            best.Players.Length,
            best.Battalion,
            AllianceName(best.Battalion),
            "按组排处理，别让小队切后排",
            $"队形 {names}，方向相似 {best.DirectionSimilarity:0.00}，队形稳定 {best.FormationStability:0}，半径 {best.Radius:0.0}y");
    }

    private BattlefieldAdvancedTacticalInsightSnapshot? BuildChokeBlockedInsight(
        Vector3? friendlyCenter,
        BattlefieldMapTacticsSnapshot mapTactics,
        AdvancedTacticsCalibrationConfiguration tuning)
    {
        if (!mapTactics.IsAvailable)
            return null;

        var blocked = mapTactics.TopZones
            .Where(zone => zone.Kind == MapAnnotationKind.Choke || zone.IsMandatoryChoke)
            .Select(zone =>
            {
                var pressure = zone.EnemyNearby + zone.EnemyMapVisionNearby;
                var friendlyDistance = friendlyCenter.HasValue ? Distance2D(friendlyCenter.Value, zone.Position) : 999f;
                var severity = Math.Clamp(24f + pressure * 9f + zone.TotalRisk * 0.48f + (zone.IsMandatoryChoke ? 16f : 0f) + (friendlyDistance <= 135f ? 10f : 0f), 0f, 100f);
                return (Zone: zone, Pressure: pressure, Distance: friendlyDistance, Severity: severity);
            })
            .Where(item => item.Pressure >= tuning.ChokeMinPressure && item.Severity >= tuning.ChokeMinSeverity)
            .OrderByDescending(item => item.Severity)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(blocked.Zone.Id))
            return null;

        return new BattlefieldAdvancedTacticalInsightSnapshot(
            BattlefieldAdvancedTacticalInsightKind.ChokeBlocked,
            "关键通道被封",
            blocked.Severity,
            Math.Clamp(0.50f + blocked.Pressure * 0.05f + (blocked.Zone.IsMandatoryChoke ? 0.12f : 0f), 0.45f, 0.92f),
            blocked.Zone.Position,
            blocked.Pressure,
            null,
            "敌方",
            "选择绕路，不要从窄口追击",
            $"{blocked.Zone.Label} 敌方压力 {blocked.Pressure}，宽 {blocked.Zone.EstimatedWidth:0.0}y，必经 {blocked.Zone.IsMandatoryChoke}，距我方 {blocked.Distance:0}y");
    }

    private static Vector3? ResolveFriendlyTacticalCenter(
        BattlefieldPlayerSnapshot? localPlayer,
        BattlefieldTeamSituationSnapshot teamSituation)
    {
        if (teamSituation.Friendly.MainCluster.HasValue)
            return teamSituation.Friendly.MainCluster.Value.Center;

        return localPlayer?.Position;
    }

    private static Vector3? ResolveEnemyTacticalCenter(
        BattlefieldTeamSituationSnapshot teamSituation,
        IReadOnlyList<BattlefieldMapVisionClusterSnapshot> mapVisionClusters)
    {
        if (teamSituation.EnemyMainGroupMovement.HasMainGroup)
            return teamSituation.EnemyMainGroupMovement.CurrentCenter;

        if (teamSituation.Enemy.MainCluster.HasValue)
            return teamSituation.Enemy.MainCluster.Value.Center;

        var mapCluster = mapVisionClusters
            .Where(cluster => cluster.Relation == BattlefieldPlayerRelation.Enemy)
            .OrderByDescending(cluster => cluster.PointCount)
            .ThenBy(cluster => cluster.DistanceToLocal)
            .FirstOrDefault();
        return mapCluster.PointCount > 0 ? mapCluster.Center : null;
    }

    private static float ResolveInsightRisk(
        IReadOnlyList<BattlefieldAdvancedTacticalInsightSnapshot> insights,
        BattlefieldAdvancedTacticalInsightKind kind)
        => insights
            .Where(insight => insight.Kind == kind)
            .Select(insight => insight.Severity * (0.65f + Math.Clamp(insight.Confidence, 0f, 1f) * 0.35f))
            .DefaultIfEmpty(0f)
            .Max();

    private static bool ShouldAcceptAdvancedTacticalInsight(
        BattlefieldAdvancedTacticalInsightSnapshot insight,
        AdvancedTacticsCalibrationConfiguration tuning)
    {
        var minSeverity = insight.Kind switch
        {
            BattlefieldAdvancedTacticalInsightKind.FriendlyCohesion => tuning.CohesionMinAlertSeverity,
            BattlefieldAdvancedTacticalInsightKind.EnemyRetreat => tuning.RetreatMinSeverity,
            BattlefieldAdvancedTacticalInsightKind.EnemyFakeRetreatAmbush => tuning.FakeRetreatMinSeverity,
            BattlefieldAdvancedTacticalInsightKind.HighGroundDropPrep => tuning.HighGroundMinSeverity,
            BattlefieldAdvancedTacticalInsightKind.ThirdPartyPincer => tuning.ThirdPartyMinSeverity,
            BattlefieldAdvancedTacticalInsightKind.CoordinatedSquad => tuning.SquadMinScore,
            BattlefieldAdvancedTacticalInsightKind.ChokeBlocked => tuning.ChokeMinSeverity,
            _ => 50f,
        };

        return insight.Severity >= minSeverity
            && insight.Confidence * 100f >= tuning.MinAlertConfidencePercent;
    }

    private static string BuildAdvancedTacticsCalibrationText(AdvancedTacticsCalibrationConfiguration tuning)
        => $"校准阈值：置信>={tuning.MinAlertConfidencePercent}%；假撤退>={tuning.FakeRetreatMinSeverity:0}；第三方>={tuning.ThirdPartyMinSeverity:0}/{tuning.ThirdPartyMinAngle:0}度；高台压力>={tuning.HighGroundMinPressure}/风险>={tuning.HighGroundMinSeverity:0}；组排分>={tuning.SquadMinScore:0}/方向>={tuning.SquadMinDirectionSimilarityPercent}%；封路压力>={tuning.ChokeMinPressure}/风险>={tuning.ChokeMinSeverity:0}；凝聚警报>={tuning.CohesionMinAlertSeverity:0}";

    private static string BuildAdvancedTacticalSummaryCalibrated(
        IReadOnlyList<BattlefieldAdvancedTacticalInsightSnapshot> insights,
        float followRate,
        float cohesionScore,
        int rawInsightCount,
        int suppressedInsightCount)
    {
        if (insights.Count == 0)
            return $"高级战术洞察：暂无高置信异常；跟随率 {followRate:0}%，凝聚 {cohesionScore:0}；候选 {rawInsightCount}/压制 {suppressedInsightCount}";

        var top = insights[0];
        return $"高级战术洞察：{top.Label} 风险 {top.Severity:0}/置信 {top.Confidence:P0}；跟随率 {followRate:0}%；凝聚 {cohesionScore:0}；候选 {rawInsightCount}/压制 {suppressedInsightCount}；建议 {top.Recommendation}";
    }

    private static string BuildAdvancedTacticalSummary(
        IReadOnlyList<BattlefieldAdvancedTacticalInsightSnapshot> insights,
        float followRate,
        float cohesionScore,
        int rawInsightCount,
        int suppressedInsightCount)
    {
        if (insights.Count == 0)
            return $"高级战术洞察：暂无高置信异常；跟随率 {followRate:0}%，凝聚 {cohesionScore:0}";

        var top = insights[0];
        return $"高级战术洞察：{top.Label} 风险 {top.Severity:0}/置信 {top.Confidence:P0}；跟随率 {followRate:0}%；凝聚 {cohesionScore:0}；建议 {top.Recommendation}";
    }

    private bool TryGetPlayerMovement(
        ulong gameObjectId,
        long now,
        out Vector3 delta,
        out float speed,
        out float directionRadians,
        long maxAgeMs)
    {
        delta = Vector3.Zero;
        speed = 0f;
        directionRadians = 0f;
        if (!playerHistory.TryGetValue(gameObjectId, out var entry) || entry.LastMovementTicks <= 0)
            return false;

        if (now - entry.LastMovementTicks > maxAgeMs)
            return false;

        delta = entry.MovementDelta;
        speed = entry.SpeedPerSecond;
        directionRadians = entry.MovementDirectionRadians;
        return Length2D(delta) >= 0.2f && speed > 0f;
    }

    private bool IsFacingOrMovingToward(BattlefieldPlayerSnapshot player, Vector3 targetPosition, long now)
    {
        var toTarget = targetPosition - player.Position;
        toTarget.Y = 0f;
        if (Length2D(toTarget) < 4f)
            return true;

        var facingVector = DirectionVectorFromRadians(player.RotationRadians);
        if (Dot2DNormalized(facingVector, toTarget) >= 0.45f)
            return true;

        return TryGetPlayerMovement(player.GameObjectId, now, out var delta, out var speed, out _, 4500)
            && speed >= 0.55f
            && Dot2DNormalized(delta, toTarget) >= 0.30f;
    }

    private static bool TryResolveAllianceCenter(
        BattlefieldAllianceSituationSnapshot? alliance,
        out AllianceCenterWork center)
    {
        center = default;
        if (alliance == null)
            return false;

        if (alliance.MainPlayerCluster.HasValue && alliance.MainPlayerCluster.Value.PlayerCount > 0)
        {
            var cluster = alliance.MainPlayerCluster.Value;
            center = new AllianceCenterWork(alliance.Name, alliance.Battalion, cluster.Center, cluster.PlayerCount);
            return true;
        }

        if (alliance.MainMapVisionCluster.HasValue && alliance.MainMapVisionCluster.Value.PointCount > 0)
        {
            var cluster = alliance.MainMapVisionCluster.Value;
            center = new AllianceCenterWork(alliance.Name, alliance.Battalion, cluster.Center, cluster.PointCount);
            return true;
        }

        return false;
    }

    private float ResolveAllianceApproachRatio(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        Vector3 friendlyCenter,
        long now)
    {
        var movers = players
            .Where(player => !player.IsDead)
            .Select(player => TryGetPlayerMovement(player.GameObjectId, now, out var delta, out var speed, out _, 4500)
                ? (Player: player, Delta: delta, Speed: speed)
                : (Player: player, Delta: Vector3.Zero, Speed: 0f))
            .Where(item => item.Speed >= 0.65f)
            .ToArray();
        if (movers.Length == 0)
            return 0f;

        var approaching = movers.Count(item => Dot2DNormalized(item.Delta, friendlyCenter - item.Player.Position) >= 0.25f);
        return approaching / (float)movers.Length;
    }

    private static float AveragePairwiseDirectionSimilarity(IReadOnlyList<Vector3> deltas)
    {
        if (deltas.Count < 2)
            return 0f;

        var total = 0f;
        var count = 0;
        for (var i = 0; i < deltas.Count; i++)
        {
            for (var j = i + 1; j < deltas.Count; j++)
            {
                var dot = Dot2DNormalized(deltas[i], deltas[j]);
                total += Math.Clamp((dot + 1f) * 0.5f, 0f, 1f);
                count++;
            }
        }

        return count > 0 ? total / count : 0f;
    }

    private float ResolveFormationStabilityScore(IReadOnlyList<BattlefieldPlayerSnapshot> players, long now)
    {
        var totalDelta = 0f;
        var pairs = 0;
        for (var i = 0; i < players.Count; i++)
        {
            if (!TryGetPastPosition(players[i].GameObjectId, now, out var firstPast))
                continue;

            for (var j = i + 1; j < players.Count; j++)
            {
                if (!TryGetPastPosition(players[j].GameObjectId, now, out var secondPast))
                    continue;

                var currentDistance = Distance2D(players[i].Position, players[j].Position);
                var pastDistance = Distance2D(firstPast, secondPast);
                totalDelta += MathF.Abs(currentDistance - pastDistance);
                pairs++;
            }
        }

        if (pairs == 0)
            return 0f;

        var averageDistanceDelta = totalDelta / pairs;
        return Math.Clamp(100f - averageDistanceDelta * 12f, 0f, 100f);
    }

    private bool TryGetPastPosition(ulong gameObjectId, long now, out Vector3 position)
    {
        position = Vector3.Zero;
        if (!playerHistory.TryGetValue(gameObjectId, out var entry))
            return false;

        foreach (var sample in entry.PositionSamples)
        {
            if (now - sample.Ticks < 2500)
                continue;

            position = sample.Position;
            return true;
        }

        return false;
    }

    private static Vector3 AveragePosition(IReadOnlyList<BattlefieldPlayerSnapshot> players)
    {
        if (players.Count == 0)
            return Vector3.Zero;

        var sum = Vector3.Zero;
        foreach (var player in players)
            sum += player.Position;

        return sum / players.Count;
    }

    private static float Dot2DNormalized(Vector3 first, Vector3 second)
    {
        first.Y = 0f;
        second.Y = 0f;
        var length = Length2D(first) * Length2D(second);
        if (length <= 0.001f)
            return 0f;

        return Math.Clamp((first.X * second.X + first.Z * second.Z) / length, -1f, 1f);
    }

    private static float AngleBetweenDegrees(Vector3 first, Vector3 second)
    {
        var dot = Dot2DNormalized(first, second);
        return MathF.Acos(Math.Clamp(dot, -1f, 1f)) * 180f / MathF.PI;
    }

    private static Vector3 DirectionVectorFromRadians(float radians)
        => new(MathF.Sin(radians), 0f, MathF.Cos(radians));

    private void UpdatePlayerHistory(
        IReadOnlyList<BattlefieldPlayerSnapshot> players,
        FrontlineKnowledgeSnapshot knowledge,
        long now)
    {
        var seen = new HashSet<ulong>(players.Count);
        var playerById = new Dictionary<ulong, BattlefieldPlayerSnapshot>(players.Count);
        foreach (var player in players)
        {
            if (!playerById.ContainsKey(player.GameObjectId))
                playerById[player.GameObjectId] = player;
        }

        foreach (var player in players)
        {
            seen.Add(player.GameObjectId);

            var isNew = false;
            if (!playerHistory.TryGetValue(player.GameObjectId, out var entry))
            {
                isNew = true;
                entry = new PlayerHistoryEntry(player.GameObjectId)
                {
                    FirstSeenTicks = now,
                    IsDead = player.IsDead,
                    DeathStartedTicks = player.IsDead ? now : 0,
                    IsGuarding = player.IsGuarding,
                    IsControlImmune = player.IsControlImmune,
                    IsCrowdControlled = player.IsCrowdControlled,
                    IsInvulnerable = player.IsInvulnerable,
                    IsExecutable = player.IsExecutable,
                    HasSnowBlessing = player.HasSnowBlessing,
                    IsCasting = player.IsCasting
                };
                playerHistory[player.GameObjectId] = entry;
            }
            else
            {
                if (!entry.IsDead && player.IsDead)
                {
                    entry.LastDeathTicks = now;
                    entry.DeathStartedTicks = now;
                }
                else if (entry.IsDead && !player.IsDead)
                {
                    entry.LastReviveTicks = now;
                    entry.DeathStartedTicks = 0;
                }
            }

            if (!isNew)
                RecordKeySkillStateTransitions(entry, player, playerById, knowledge, now);

            UpdatePlayerMovementHistory(entry, player, isNew, now);
            entry.Name = player.Name;
            entry.Relation = player.Relation;
            entry.ClassJobId = player.ClassJobId;
            entry.IsDead = player.IsDead;
            entry.CurrentHp = player.CurrentHp;
            entry.MaxHp = player.MaxHp;
            entry.HpPercent = player.HpPercent;
            entry.TargetObjectId = player.TargetObjectId;
            entry.CastTargetObjectId = player.CastTargetObjectId;
            entry.IsInCombat = player.IsInCombat;
            entry.IsCasting = player.IsCasting;
            entry.IsGuarding = player.IsGuarding;
            entry.IsControlImmune = player.IsControlImmune;
            entry.IsCrowdControlled = player.IsCrowdControlled;
            entry.IsInvulnerable = player.IsInvulnerable;
            entry.IsExecutable = player.IsExecutable;
            entry.HasSnowBlessing = player.HasSnowBlessing;
            if (player.IsInCombat || player.IsCasting || IsValidGameObjectId(player.TargetObjectId) || IsValidGameObjectId(player.CastTargetObjectId))
                entry.LastEngagementTicks = now;
            entry.LastSeenTicks = now;
        }

        List<ulong>? stalePlayerIds = null;
        foreach (var pair in playerHistory)
        {
            if (seen.Contains(pair.Key) || now - pair.Value.LastSeenTicks <= TacticalHistoryExpiryMs)
                continue;

            stalePlayerIds ??= new List<ulong>(4);
            stalePlayerIds.Add(pair.Key);
        }

        if (stalePlayerIds != null)
        {
            foreach (var key in stalePlayerIds)
                playerHistory.Remove(key);
        }

        PruneKeySkillUseHistory(now);
    }

    private static void UpdatePlayerMovementHistory(
        PlayerHistoryEntry entry,
        BattlefieldPlayerSnapshot player,
        bool isNew,
        long now)
    {
        if (isNew || entry.LastSeenTicks <= 0)
        {
            entry.PreviousPosition = player.Position;
            entry.PreviousSeenTicks = now;
            entry.Position = player.Position;
            entry.MovementDelta = Vector3.Zero;
            entry.SpeedPerSecond = 0f;
            entry.MovementDirectionRadians = player.RotationRadians;
            entry.FacingRadians = player.RotationRadians;
            entry.LastMovementTicks = now;
            AddPlayerPositionSample(entry, player.Position, now, force: true);
            return;
        }

        var delta = player.Position - entry.Position;
        delta.Y = 0f;
        var distance = Length2D(delta);
        var elapsedSeconds = Math.Max(0.001f, (now - entry.LastSeenTicks) / 1000f);
        if (distance >= PlayerPositionSampleMinDistance)
        {
            entry.PreviousPosition = entry.Position;
            entry.PreviousSeenTicks = entry.LastSeenTicks;
            entry.MovementDelta = delta;
            entry.SpeedPerSecond = distance / elapsedSeconds;
            entry.MovementDirectionRadians = DirectionRadians(delta);
            entry.LastMovementTicks = now;
        }
        else if (now - entry.LastMovementTicks > 2500)
        {
            entry.MovementDelta = Vector3.Zero;
            entry.SpeedPerSecond = 0f;
        }

        entry.Position = player.Position;
        entry.FacingRadians = player.RotationRadians;
        AddPlayerPositionSample(entry, player.Position, now, force: false);
    }

    private static void AddPlayerPositionSample(PlayerHistoryEntry entry, Vector3 position, long now, bool force)
    {
        var shouldAdd = force || !entry.HasLastPositionSample;
        if (!shouldAdd)
            shouldAdd = now - entry.LastPositionSampleTicks >= PlayerPositionSampleMinIntervalMs
                && Distance2D(entry.LastPositionSamplePosition, position) >= PlayerPositionSampleMinDistance;

        if (shouldAdd)
        {
            entry.PositionSamples.Enqueue(new PlayerPositionSample(now, position));
            entry.LastPositionSampleTicks = now;
            entry.LastPositionSamplePosition = position;
            entry.HasLastPositionSample = true;
        }

        while (entry.PositionSamples.Count > 0 && now - entry.PositionSamples.Peek().Ticks > PlayerPositionSampleWindowMs)
            entry.PositionSamples.Dequeue();

        if (entry.PositionSamples.Count == 0)
            entry.HasLastPositionSample = false;
    }

    private void RecordKeySkillStateTransitions(
        PlayerHistoryEntry entry,
        BattlefieldPlayerSnapshot player,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerById,
        FrontlineKnowledgeSnapshot knowledge,
        long now)
    {
        if (player.IsDead)
            return;

        if (!entry.IsGuarding && player.IsGuarding)
            AddKeySkillUseRecord(player, "防御", BattlefieldKeySkillKind.Defensive, string.Empty, "状态读取", "进入防御状态", now);

        if (!entry.IsControlImmune && player.IsControlImmune)
            AddKeySkillUseRecord(player, "净化抗控", BattlefieldKeySkillKind.Purify, string.Empty, "状态读取", "获得净化后抗控或控制免疫状态", now);

        if (!entry.IsCrowdControlled && player.IsCrowdControlled)
            AddKeySkillUseRecord(player, "控制命中", BattlefieldKeySkillKind.CrowdControl, player.Name, "状态读取", "目标进入被控状态，可作为控制链窗口", now);

        if (!entry.IsInvulnerable && player.IsInvulnerable)
            AddKeySkillUseRecord(player, "无敌/不可伤害", BattlefieldKeySkillKind.Invulnerability, string.Empty, "状态读取", "获得无敌或不可伤害状态", now);

        if (!entry.IsExecutable && player.IsExecutable)
            AddKeySkillUseRecord(player, "斩杀线", BattlefieldKeySkillKind.Execute, player.Name, "派生判断", "目标血量进入派生斩杀线", now);

        if (!entry.HasSnowBlessing && player.HasSnowBlessing)
            AddKeySkillUseRecord(player, "雪精祝福", BattlefieldKeySkillKind.Defensive, string.Empty, "状态读取", "获得沃刻其特雪精祝福护盾", now);

        if (!entry.IsCasting && player.IsCasting && player.TotalCastTime >= 0.5f)
        {
            var profile = ResolvePrimaryKeySkillProfile(player.ClassJobId, ResolveJobInfo(player.ClassJobId).Role, knowledge);
            var targetName = ResolveTargetName(player.CastTargetObjectId, playerById);
            AddKeySkillUseRecord(
                player,
                profile.SkillName,
                profile.Kind,
                targetName,
                "咏唱开始/职业推断",
                string.IsNullOrWhiteSpace(targetName) ? "可见咏唱开始" : $"可见咏唱开始，目标 {targetName}",
                now);
        }
    }

    private void AddKeySkillUseRecord(
        BattlefieldPlayerSnapshot player,
        string skillName,
        BattlefieldKeySkillKind kind,
        string targetName,
        string sourceText,
        string evidenceText,
        long now)
    {
        if (player.GameObjectId == 0 || string.IsNullOrWhiteSpace(skillName))
            return;

        var key = $"{player.GameObjectId}:{skillName}:{kind}:{targetName}";
        if (lastKeySkillUseByKey.TryGetValue(key, out var lastSeen) && now - lastSeen < 1500)
            return;

        lastKeySkillUseByKey[key] = now;
        lastKeySkillUseTicksByPlayerSkill[(player.GameObjectId, skillName)] = now;
        var jobInfo = ResolveJobInfo(player.ClassJobId);
        keySkillUseHistory.Enqueue(new KeySkillUseHistoryEntry(
            now,
            player.GameObjectId,
            player.Name,
            player.Relation == BattlefieldPlayerRelation.LocalPlayer ? BattlefieldPlayerRelation.Friendly : player.Relation,
            NormalizeBattalion(player.Battalion),
            AllianceName(NormalizeBattalion(player.Battalion)),
            player.ClassJobId,
            jobInfo.Name,
            skillName,
            kind,
            targetName,
            sourceText,
            evidenceText));
        PruneKeySkillUseHistory(now);
    }

    private void AddUnresolvedKeySkillUseRecord(BattlefieldKeySkillUseSnapshot item, long now)
    {
        if (item.ObservedAtTicks <= 0 || string.IsNullOrWhiteSpace(item.SkillName))
            return;

        var key = $"unresolved:{item.ObservedAtTicks}:{item.Name}:{item.SkillName}:{item.Kind}:{item.TargetName}";
        if (lastKeySkillUseByKey.ContainsKey(key))
            return;

        lastKeySkillUseByKey[key] = item.ObservedAtTicks;
        keySkillUseHistory.Enqueue(new KeySkillUseHistoryEntry(
            item.ObservedAtTicks,
            0,
            item.Name,
            BattlefieldPlayerRelation.Unknown,
            null,
            string.Empty,
            item.ClassJobId,
            item.JobName,
            item.SkillName,
            item.Kind,
            item.TargetName,
            item.SourceText,
            item.EvidenceText));
        PruneKeySkillUseHistory(now);
    }

    private void PruneKeySkillUseHistory(long now)
    {
        while (keySkillUseHistory.Count > 0 && now - keySkillUseHistory.Peek().ObservedAtTicks > KeySkillUseHistoryExpiryMs)
            keySkillUseHistory.Dequeue();

        List<string>? staleKeys = null;
        foreach (var pair in lastKeySkillUseByKey)
        {
            if (now - pair.Value <= KeySkillUseHistoryExpiryMs)
                continue;

            staleKeys ??= new List<string>(4);
            staleKeys.Add(pair.Key);
        }

        if (staleKeys != null)
        {
            foreach (var key in staleKeys)
                lastKeySkillUseByKey.Remove(key);
        }

        List<(ulong GameObjectId, string SkillName)>? stalePlayerSkillKeys = null;
        foreach (var pair in lastKeySkillUseTicksByPlayerSkill)
        {
            if (now - pair.Value <= KeySkillUseHistoryExpiryMs)
                continue;

            stalePlayerSkillKeys ??= new List<(ulong GameObjectId, string SkillName)>(4);
            stalePlayerSkillKeys.Add(pair.Key);
        }

        if (stalePlayerSkillKeys == null)
            return;

        foreach (var key in stalePlayerSkillKeys)
            lastKeySkillUseTicksByPlayerSkill.Remove(key);
    }

    private static string ResolveTargetName(
        ulong targetObjectId,
        IReadOnlyDictionary<ulong, BattlefieldPlayerSnapshot> playerById)
        => IsValidGameObjectId(targetObjectId) && playerById.TryGetValue(targetObjectId, out var target)
            ? target.Name
            : string.Empty;

    private void PrunePlayerHistory(long now)
    {
        foreach (var entry in playerHistory.Values)
        {
            if (!entry.IsDead && entry.DeathStartedTicks > 0)
                entry.DeathStartedTicks = 0;

            if (entry.LastDeathTicks > 0 && now - entry.LastDeathTicks > TacticalHistoryExpiryMs)
                entry.LastDeathTicks = 0;

            if (entry.LastReviveTicks > 0 && now - entry.LastReviveTicks > TacticalHistoryExpiryMs)
                entry.LastReviveTicks = 0;
        }
    }

    private BattlefieldPlayerTrackSnapshot[] BuildPlayerTracks(long now)
    {
        if (cachedPlayerTracks.Length > 0 && now - lastPlayerTrackBuildTicks < MovementTrackRefreshIntervalMs)
            return cachedPlayerTracks;

        if (playerHistory.Count == 0)
        {
            cachedPlayerTracks = Array.Empty<BattlefieldPlayerTrackSnapshot>();
            lastPlayerTrackBuildTicks = now;
            return cachedPlayerTracks;
        }

        var tracks = new BattlefieldPlayerTrackSnapshot[playerHistory.Count];
        var index = 0;
        foreach (var entry in playerHistory.Values)
        {
            tracks[index++] = new BattlefieldPlayerTrackSnapshot(
                entry.GameObjectId,
                entry.Name,
                entry.Relation,
                entry.ClassJobId,
                entry.Position,
                entry.PreviousPosition,
                entry.MovementDelta,
                entry.SpeedPerSecond,
                entry.MovementDirectionRadians,
                entry.FacingRadians,
                entry.IsDead,
                Math.Max(0, now - entry.LastSeenTicks),
                entry.LastMovementTicks > 0 ? Math.Max(0, now - entry.LastMovementTicks) : Math.Max(0, now - entry.LastSeenTicks),
                entry.DeathStartedTicks > 0 ? Math.Max(0, now - entry.DeathStartedTicks) : null);
        }

        Array.Sort(tracks, ComparePlayerTrackSnapshot);
        cachedPlayerTracks = tracks;
        lastPlayerTrackBuildTicks = now;
        return cachedPlayerTracks;
    }

    private BattlefieldGroupTrackSnapshot[] BuildEnemyMainGroupTrack(long now)
    {
        if (cachedEnemyMainGroupTrack.Length > 0 && now - lastEnemyMainGroupTrackBuildTicks < MovementTrackRefreshIntervalMs)
            return cachedEnemyMainGroupTrack;

        if (enemyClusterHistory.Count == 0)
        {
            cachedEnemyMainGroupTrack = Array.Empty<BattlefieldGroupTrackSnapshot>();
            lastEnemyMainGroupTrackBuildTicks = now;
            return cachedEnemyMainGroupTrack;
        }

        var tracks = new BattlefieldGroupTrackSnapshot[enemyClusterHistory.Count];
        var index = 0;
        foreach (var frame in enemyClusterHistory)
            tracks[index++] = new BattlefieldGroupTrackSnapshot(frame.Ticks, frame.Center, frame.PlayerCount, frame.SourceText);

        cachedEnemyMainGroupTrack = tracks;
        lastEnemyMainGroupTrackBuildTicks = now;
        return cachedEnemyMainGroupTrack;
    }

    private static int ComparePlayerTrackSnapshot(BattlefieldPlayerTrackSnapshot left, BattlefieldPlayerTrackSnapshot right)
    {
        var relationCompare = left.Relation.CompareTo(right.Relation);
        if (relationCompare != 0)
            return relationCompare;

        return string.Compare(left.Name, right.Name, StringComparison.Ordinal);
    }

    private bool TryGetDeathAge(long now, ulong gameObjectId, out long age)
    {
        age = 0;
        if (!playerHistory.TryGetValue(gameObjectId, out var entry) || entry.DeathStartedTicks <= 0)
            return false;

        age = now - entry.DeathStartedTicks;
        return age >= 0;
    }

    private static (int Size, long? AgeMs) BuildRespawnWave(IEnumerable<long> ticks, long now)
    {
        var ordered = ticks
            .Where(ticksValue => ticksValue > 0 && now - ticksValue <= RespawnWaveMaxAgeMs)
            .OrderByDescending(ticksValue => ticksValue)
            .ToArray();
        if (ordered.Length == 0)
            return (0, null);

        var newest = ordered[0];
        var previous = newest;
        var size = 1;
        for (var i = 1; i < ordered.Length; i++)
        {
            var current = ordered[i];
            if (previous - current > RespawnWaveJoinWindowMs)
                break;

            size++;
            previous = current;
        }

        return (size, Math.Max(0, now - newest));
    }

    private static float CalculateRespawnConfidence(
        int visibleDeadNow,
        int recentDeaths,
        int recentRevives,
        int deathWaveSize,
        int reviveWaveSize)
    {
        var confidence = 0.48f;
        confidence += Math.Min(0.18f, visibleDeadNow * 0.03f);
        confidence += Math.Min(0.18f, recentDeaths * 0.04f);
        confidence += Math.Min(0.14f, recentRevives * 0.05f);
        confidence += Math.Min(0.10f, Math.Max(deathWaveSize, reviveWaveSize) * 0.02f);
        return Math.Clamp(confidence, 0.18f, 0.92f);
    }

    private static string BuildRespawnSummary(
        int friendlyDeadNow,
        int enemyDeadNow,
        int friendlyRecentlyDied,
        int enemyRecentlyDied,
        int friendlyRecentlyRevived,
        int enemyRecentlyRevived,
        int friendlyLikelyReturningSoon,
        int enemyLikelyReturningSoon,
        (int Size, long? AgeMs) friendlyDeathWave,
        (int Size, long? AgeMs) enemyDeathWave,
        (int Size, long? AgeMs) friendlyReviveWave,
        (int Size, long? AgeMs) enemyReviveWave,
        float confidence)
    {
        static string DescribeWave((int Size, long? AgeMs) wave)
        {
            if (wave.Size <= 0)
                return "0";

            return wave.AgeMs.HasValue
                ? $"{wave.Size}({wave.AgeMs.Value / 1000f:0.0}s)"
                : wave.Size.ToString();
        }

        var parts = new List<string>();

        if (friendlyRecentlyDied > 0 || enemyRecentlyDied > 0)
            parts.Add($"30秒倒地 我方 {friendlyRecentlyDied} / 敌方 {enemyRecentlyDied}");

        if (friendlyRecentlyRevived > 0 || enemyRecentlyRevived > 0)
            parts.Add($"30秒复活 我方 {friendlyRecentlyRevived} / 敌方 {enemyRecentlyRevived}");

        if (friendlyDeathWave.Size > 0 || enemyDeathWave.Size > 0)
            parts.Add($"死亡波次 我方 {DescribeWave(friendlyDeathWave)} / 敌方 {DescribeWave(enemyDeathWave)}");

        if (friendlyReviveWave.Size > 0 || enemyReviveWave.Size > 0)
            parts.Add($"复活波次 我方 {DescribeWave(friendlyReviveWave)} / 敌方 {DescribeWave(enemyReviveWave)}");

        if (friendlyLikelyReturningSoon > 0 || enemyLikelyReturningSoon > 0)
            parts.Add($"回场窗口 我方 {friendlyLikelyReturningSoon} / 敌方 {enemyLikelyReturningSoon}");

        if (parts.Count == 0)
            return $"复活节奏平稳，当前可见死亡 我方 {friendlyDeadNow} / 敌方 {enemyDeadNow}；置信 {confidence:P0}";

        return $"{string.Join("，", parts)}；置信 {confidence:P0}";
    }

    private static bool IsEnemySplit(IReadOnlyList<BattlefieldEnemyClusterSnapshot> enemyClusters)
    {
        if (enemyClusters.Count < 2)
            return false;

        var main = enemyClusters[0];
        var secondaryMinCount = Math.Max(EnemySplitMinSecondaryCount, (int)MathF.Ceiling(main.Count * 0.25f));
        return enemyClusters
            .Skip(1)
            .Any(cluster => cluster.Count >= secondaryMinCount && cluster.SeparationFromMain >= EnemySplitMinDistance);
    }

    private static string BuildEnemySplitSummary(
        IReadOnlyList<BattlefieldEnemyClusterSnapshot> enemyClusters,
        bool isEnemySplit)
    {
        if (enemyClusters.Count == 0)
            return "敌方分兵状态样本不足";

        var main = enemyClusters[0];
        if (!isEnemySplit)
            return $"未见明显分兵，主团 {main.AllianceName}×{main.Count}，中心 {FormatVector(main.Center)}";

        var parts = enemyClusters
            .Take(3)
            .Select(cluster => $"{cluster.AllianceName}脳{cluster.Count}@{FormatVector(cluster.Center)}")
            .ToArray();
        return $"发现敌方分兵，{string.Join("，", parts)}";
    }

    private static string BuildTacticalSummary(
        BattlefieldTeamSummarySnapshot friendlySummary,
        BattlefieldTeamSummarySnapshot enemySummary,
        BattlefieldFocusTargetSnapshot[] enemyFocusTargets,
        BattlefieldFocusTargetSnapshot[] friendlyFocusTargets,
        BattlefieldRespawnRhythmSnapshot respawnRhythm,
        BattlefieldGroupMovementSnapshot movement,
        string splitSummary,
        BattlefieldLimitBreakThreatSituationSnapshot limitBreakThreats,
        BattlefieldKeySkillThreatSituationSnapshot keySkillThreats)
    {
        var parts = new List<string>
        {
            $"我方 {friendlySummary.TotalCount} 人 / 敌方 {enemySummary.TotalCount} 人"
        };

        if (enemyFocusTargets.Length > 0)
        {
            var first = enemyFocusTargets[0];
            parts.Add($"敌方集火 {first.TargetName} ({Math.Max(first.AttackerCount, first.CasterCount)}人)");
        }

        if (friendlyFocusTargets.Length > 0)
        {
            var first = friendlyFocusTargets[0];
            parts.Add($"我方集火 {first.TargetName} ({Math.Max(first.AttackerCount, first.CasterCount)}人)");
        }

        parts.Add(movement.SummaryText);
        parts.Add(splitSummary);
        parts.Add(limitBreakThreats.SummaryText);
        parts.Add(keySkillThreats.SummaryText);
        parts.Add(respawnRhythm.SummaryText);
        return string.Join("；", parts);
    }

    private static bool IsValidGameObjectId(ulong gameObjectId)
        => gameObjectId != 0 && gameObjectId != InvalidGameObjectId && gameObjectId != ulong.MaxValue;

    private static BattlefieldPlayerSnapshot? TryGetLocalPlayer(IReadOnlyList<BattlefieldPlayerSnapshot> players)
    {
        foreach (var player in players)
        {
            if (player.Relation == BattlefieldPlayerRelation.LocalPlayer)
                return player;
        }

        return null;
    }

    private static byte? NormalizeBattalion(byte battalion)
        => IsFrontlineBattalion(battalion) ? battalion : null;

    private static string AllianceName(byte? battalion)
        => battalion switch
        {
            0 => "黑涡团",
            1 => "双蛇党",
            2 => "恒辉队",
            _ => "未知阵营",
        };

    private static float Distance2D(Vector3 a, Vector3 b)
        => MathF.Sqrt(DistanceSquared2D(a, b));

    private static float Length2D(Vector3 vector)
        => MathF.Sqrt(vector.X * vector.X + vector.Z * vector.Z);

    private static float DirectionRadians(Vector3 delta)
        => MathF.Atan2(delta.X, delta.Z);

    private static string FormatVector(Vector3 vector)
        => $"({vector.X:0.0}, {vector.Z:0.0})";

    private static string NormalizeActorName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Trim();
        var worldSeparator = normalized.IndexOf('@');
        if (worldSeparator > 0)
            normalized = normalized[..worldSeparator];

        return new string(normalized.Where(character => !char.IsWhiteSpace(character)).ToArray());


    }

    private static bool IsFriendlySide(BattlefieldPlayerRelation relation)
        => relation is BattlefieldPlayerRelation.LocalPlayer or BattlefieldPlayerRelation.Friendly;

    private static bool IsEnemySource(BattlefieldPlayerSnapshot player)
        => player.Relation == BattlefieldPlayerRelation.Enemy;

    private static bool IsFriendlySource(BattlefieldPlayerSnapshot player)
        => IsFriendlySide(player.Relation);

    private static bool IsFriendlyTarget(BattlefieldPlayerSnapshot player)
        => IsFriendlySide(player.Relation);

    private static bool IsEnemyTarget(BattlefieldPlayerSnapshot player)
        => player.Relation == BattlefieldPlayerRelation.Enemy;

    private static bool IsSideMember(BattlefieldPlayerRelation relation, BattlefieldTacticalSide side)
        => side switch
        {
            BattlefieldTacticalSide.Friendly => IsFriendlySide(relation),
            BattlefieldTacticalSide.Enemy => relation == BattlefieldPlayerRelation.Enemy,
            _ => relation == BattlefieldPlayerRelation.Unknown,
        };

    private static JobInfo ResolveJobInfo(uint classJobId)
        => JobInfoByClassJobId.TryGetValue(classJobId, out var info)
            ? info
            : new JobInfo($"鑱屼笟{classJobId}", "未知");

    private static string DescribeDirection(Vector3 delta)
    {
        var x = delta.X;
        var z = delta.Z;
        if (MathF.Abs(x) < 1.5f && MathF.Abs(z) < 1.5f)
            return string.Empty;

        var east = x > 0f;
        var south = z > 0f;

        if (MathF.Abs(x) < 1.5f)
            return south ? "南" : "北";

        if (MathF.Abs(z) < 1.5f)
            return east ? "东" : "西";

        return south
            ? (east ? "东南" : "西南")
            : (east ? "东北" : "西北");
    }

    private static string BuildStatusText(
        IReadOnlyCollection<AllianceData> alliances,
        IReadOnlyCollection<BattlefieldPlayerSnapshot> players,
        IReadOnlyCollection<BattlefieldMapEventSnapshot> mapEvents,
        IReadOnlyCollection<BattlefieldFieldMarkerSnapshot> fieldMarkers,
        IReadOnlyCollection<BattlefieldMapVisionPointSnapshot> mapVisionPoints,
        IReadOnlyCollection<BattlefieldMapVisionClusterSnapshot> mapVisionClusters,
        IReadOnlyCollection<BattlefieldTargetMarkerSnapshot> targetMarkers,
        IReadOnlyCollection<BattlefieldObjectiveSnapshot> objectives,
        IReadOnlyCollection<BattlefieldMapObjectiveSnapshot> mapObjectives,
        IReadOnlyCollection<BattlefieldPlayerClusterSnapshot> playerClusters,
        IReadOnlyCollection<BattlefieldPlayerTrackSnapshot> playerTracks,
        IReadOnlyCollection<BattlefieldGroupTrackSnapshot> enemyMainGroupTrack,
        BattlefieldTeamSituationSnapshot teamSituation,
        BattlefieldChatEventSituationSnapshot chatEventSituation,
        BattlefieldKeySkillLogEventSituationSnapshot keySkillLogEvents,
        BattlefieldPlayerFrameEventSituationSnapshot playerFrameEvents,
        BattlefieldMapTacticsSnapshot mapTactics,
        BattlefieldDecisionSnapshot decision)
    {
        var enemyCount = players.Count(player => player.Relation == BattlefieldPlayerRelation.Enemy);
        var friendlyCount = players.Count(player => player.Relation == BattlefieldPlayerRelation.Friendly);
        return $"已采集：阵营 {alliances.Count}，玩家 {players.Count}，友方 {friendlyCount}，敌方 {enemyCount}，目标 {objectives.Count}，地图目标 {mapObjectives.Count}，附近人群 {playerClusters.Count}，轨迹 {playerTracks.Count}/{enemyMainGroupTrack.Count}，视野 {mapVisionPoints.Count}/{mapVisionClusters.Count}，地图事件 {mapEvents.Count}，场地标记 {fieldMarkers.Count}，目标标记 {targetMarkers.Count}，聊天事件 {chatEventSituation.RecentEvents.Length}，战斗日志技能 {keySkillLogEvents.RecentEventCount}，帧事件 {playerFrameEvents.StatusEvents.Length}/{playerFrameEvents.DeathEvents.Length}/{playerFrameEvents.TargetEvents.Length}，关键技能 {teamSituation.KeySkillThreats.EnemyThreats.Length}/{teamSituation.KeySkillThreats.RecentUses.Length}，地图战术区 {mapTactics.ZoneCount}，评分目标 {decision.ObjectivePriorities.Length}，{teamSituation.SummaryText}，{keySkillLogEvents.SummaryText}，{playerFrameEvents.SummaryText}，{chatEventSituation.SummaryText}，{mapTactics.SummaryText}，{decision.SummaryText}";
    }

    private sealed class FocusAccumulator
    {
        private readonly HashSet<ulong> attackerIds = new();
        private readonly HashSet<ulong> casterIds = new();
        private readonly List<string> sourceNames = new();
        private BattlefieldPlayerRelation sourceRelation = BattlefieldPlayerRelation.Unknown;

        public FocusAccumulator(BattlefieldPlayerSnapshot target)
        {
            Target = target;
        }

        public BattlefieldPlayerSnapshot Target { get; }

        public void Add(BattlefieldPlayerSnapshot source, bool isCaster)
        {
            if (sourceRelation == BattlefieldPlayerRelation.Unknown)
                sourceRelation = source.Relation == BattlefieldPlayerRelation.LocalPlayer ? BattlefieldPlayerRelation.Friendly : source.Relation;

            if (isCaster)
                casterIds.Add(source.GameObjectId);
            else
                attackerIds.Add(source.GameObjectId);

            if (sourceNames.Count >= 6)
                return;

            var label = FormatSourceLabel(source);
            if (!sourceNames.Contains(label, StringComparer.Ordinal))
                sourceNames.Add(label);
        }

        public BattlefieldFocusTargetSnapshot ToSnapshot()
        {
            var attackerCount = attackerIds.Count;
            var casterCount = casterIds.Count;
            var hpPercent = Target.HpPercent;
            var threatScore = attackerCount + casterCount * 1.5f + (hpPercent > 0f && hpPercent <= LowHpThresholdPercent ? 0.75f : 0f);

            return new BattlefieldFocusTargetSnapshot(
                Target.GameObjectId,
                Target.Name,
                Target.Relation,
                ResolveJobInfo(Target.ClassJobId).Name,
                Target.CurrentHp,
                Target.MaxHp,
                hpPercent,
                sourceRelation,
                attackerCount,
                casterCount,
                sourceNames.ToArray(),
                Target.Position,
                threatScore);
        }
    }

    private static string FormatSourceLabel(BattlefieldPlayerSnapshot source)
    {
        var job = ResolveJobInfo(source.ClassJobId);
        return $"{source.Name}({job.Name})";
    }

    private sealed class PlayerHistoryEntry
    {
        public PlayerHistoryEntry(ulong gameObjectId)
        {
            GameObjectId = gameObjectId;
        }

        public ulong GameObjectId { get; }
        public string Name { get; set; } = string.Empty;
        public BattlefieldPlayerRelation Relation { get; set; }
        public uint ClassJobId { get; set; }
        public Vector3 Position { get; set; }
        public Vector3 PreviousPosition { get; set; }
        public long PreviousSeenTicks { get; set; }
        public Vector3 MovementDelta { get; set; }
        public float SpeedPerSecond { get; set; }
        public float MovementDirectionRadians { get; set; }
        public float FacingRadians { get; set; }
        public bool IsDead { get; set; }
        public uint CurrentHp { get; set; }
        public uint MaxHp { get; set; }
        public float HpPercent { get; set; }
        public ulong TargetObjectId { get; set; }
        public ulong CastTargetObjectId { get; set; }
        public bool IsInCombat { get; set; }
        public bool IsCasting { get; set; }
        public bool IsGuarding { get; set; }
        public bool IsControlImmune { get; set; }
        public bool IsCrowdControlled { get; set; }
        public bool IsInvulnerable { get; set; }
        public bool IsExecutable { get; set; }
        public bool HasSnowBlessing { get; set; }
        public long FirstSeenTicks { get; set; }
        public long LastSeenTicks { get; set; }
        public long LastEngagementTicks { get; set; }
        public long LastDeathTicks { get; set; }
        public long LastReviveTicks { get; set; }
        public long DeathStartedTicks { get; set; }
        public long LastMovementTicks { get; set; }
        public long LastPositionSampleTicks { get; set; }
        public Vector3 LastPositionSamplePosition { get; set; }
        public bool HasLastPositionSample { get; set; }
        public Queue<PlayerPositionSample> PositionSamples { get; } = new();
    }

    private readonly record struct PlayerPositionSample(long Ticks, Vector3 Position);

    private sealed class TacticalStatusIdSets
    {
        public TacticalStatusIdSets(
            HashSet<uint> guarding,
            HashSet<uint> crowdControlled,
            HashSet<uint> controlImmune,
            HashSet<uint> invulnerable,
            HashSet<uint> snowBlessing)
        {
            Guarding = guarding;
            CrowdControlled = crowdControlled;
            ControlImmune = controlImmune;
            Invulnerable = invulnerable;
            SnowBlessing = snowBlessing;
        }

        public HashSet<uint> Guarding { get; }
        public HashSet<uint> CrowdControlled { get; }
        public HashSet<uint> ControlImmune { get; }
        public HashSet<uint> Invulnerable { get; }
        public HashSet<uint> SnowBlessing { get; }
    }

    private readonly record struct EnemyClusterSample(Vector3 Position, byte? Battalion, string SourceText);

    private readonly record struct EnemyClusterWork(
        byte? Battalion,
        string SourceText,
        Vector3 Center,
        int Count,
        float Radius,
        float DistanceToLocal);

    private readonly record struct EnemyClusterHistoryEntry(long Ticks, Vector3 Center, int PlayerCount, string SourceText);

    private readonly record struct EnemyMainGroupObservationEntry(
        long Ticks,
        Vector3 Center,
        Vector3 Delta,
        int PlayerCount,
        byte? Battalion,
        string AllianceName,
        string SourceText,
        float SpeedPerSecond);

    private enum WorldRefreshStage
    {
        CollectScene,
        CollectSignals,
        Assemble,
        Finalize
    }

    private sealed class PendingWorldRefreshSession
    {
        public long StartedAtTicks { get; init; }
        public long StartedTimestamp { get; init; }
        public uint TerritoryType { get; init; }
        public uint MapId { get; init; }
        public bool IsAreaTransitioning { get; init; }
        public FrontlineSnapshot ScoreSnapshot { get; init; } = new();
        public FrontlineKnowledgeSnapshot Knowledge { get; init; } = FrontlineKnowledgeBase.GetSnapshot(0, 0);
        public FrontlineMapType MapType { get; init; }
        public bool IsInFrontline { get; init; }
        public WorldRefreshStage Stage { get; set; }
        public ObjectTableSnapshot ObjectTableState { get; set; } = new(Array.Empty<BattlefieldPlayerSnapshot>(), Array.Empty<BattlefieldObjectiveActorSnapshot>(), null);
        public BattlefieldPlayerSnapshot? LocalPlayer { get; set; }
        public BattlefieldPlayerSnapshot[] Players { get; set; } = Array.Empty<BattlefieldPlayerSnapshot>();
        public BattlefieldMapEventSnapshot[] MapEvents { get; set; } = Array.Empty<BattlefieldMapEventSnapshot>();
        public BattlefieldFieldMarkerSnapshot[] FieldMarkers { get; set; } = Array.Empty<BattlefieldFieldMarkerSnapshot>();
        public BattlefieldTargetMarkerSnapshot[] TargetMarkers { get; set; } = Array.Empty<BattlefieldTargetMarkerSnapshot>();
        public BattlefieldObjectiveActorSnapshot[] ObjectiveActors { get; set; } = Array.Empty<BattlefieldObjectiveActorSnapshot>();
        public BattlefieldObjectiveSnapshot[] Objectives { get; set; } = Array.Empty<BattlefieldObjectiveSnapshot>();
        public BattlefieldMapObjectiveSnapshot[] MapObjectives { get; set; } = Array.Empty<BattlefieldMapObjectiveSnapshot>();
        public BattlefieldPlayerFrameEventSituationSnapshot PlayerFrameEvents { get; set; } = new();
        public BattlefieldMapVisionPointSnapshot[] MapVisionPoints { get; set; } = Array.Empty<BattlefieldMapVisionPointSnapshot>();
        public BattlefieldPlayerClusterSnapshot[] PlayerClusters { get; set; } = Array.Empty<BattlefieldPlayerClusterSnapshot>();
        public BattlefieldMapVisionClusterSnapshot[] MapVisionClusters { get; set; } = Array.Empty<BattlefieldMapVisionClusterSnapshot>();
        public byte? LocalBattalion { get; set; }
        public BattlefieldScoreSituationSnapshot ScoreSituation { get; set; } = new();
        public BattlefieldAnnouncementSituationSnapshot AnnouncementSituation { get; set; } = new();
        public BattlefieldChatEventSituationSnapshot ChatEventSituation { get; set; } = new();
        public BattlefieldKeySkillLogEventSituationSnapshot KeySkillLogEvents { get; set; } = new();
        public BattlefieldTimeSituationSnapshot TimeSituation { get; set; } = new();
        public BattlefieldLimitBreakSnapshot LimitBreak { get; set; } = new();
        public AllianceData[] NormalizedAlliances { get; set; } = Array.Empty<AllianceData>();
        public BattlefieldTeamSituationSnapshot TeamSituation { get; set; } = new();
        public bool CanReuseDecisionLayer { get; set; }
        public bool ShouldQueueDecisionLayer { get; set; }
        public BattlefieldPlayerTrackSnapshot[] PlayerTracks { get; set; } = Array.Empty<BattlefieldPlayerTrackSnapshot>();
        public BattlefieldGroupTrackSnapshot[] EnemyMainGroupTrack { get; set; } = Array.Empty<BattlefieldGroupTrackSnapshot>();
        public BattlefieldMapTacticsSnapshot MapTactics { get; set; } = new();
        public BattlefieldDecisionSnapshot Decision { get; set; } = new();
    }

    private enum DecisionRefreshKind
    {
        Strategic,
        Combat
    }

    private sealed class DecisionRefreshStateSnapshot
    {
        public BattlefieldPlayerClusterSnapshot[] PlayerClusters { get; init; } = Array.Empty<BattlefieldPlayerClusterSnapshot>();
        public BattlefieldPlayerFrameEventSituationSnapshot PlayerFrameEvents { get; init; } = new();
        public BattlefieldTeamSituationSnapshot TeamSituation { get; init; } = new();
    }

    private sealed class PendingDecisionRefresh
    {
        public DecisionRefreshKind Kind { get; init; } = DecisionRefreshKind.Strategic;
        public long QueuedAtTicks { get; init; }
        public long BaseUpdatedAtTicks { get; init; }
        public uint TerritoryType { get; init; }
        public uint MapId { get; init; }
        public bool IsInFrontline { get; init; }
        public string MapName { get; init; } = string.Empty;
        public BattlefieldPlayerSnapshot? LocalPlayer { get; init; }
        public BattlefieldPlayerSnapshot[] Players { get; init; } = Array.Empty<BattlefieldPlayerSnapshot>();
        public BattlefieldFieldMarkerSnapshot[] FieldMarkers { get; init; } = Array.Empty<BattlefieldFieldMarkerSnapshot>();
        public BattlefieldTargetMarkerSnapshot[] TargetMarkers { get; init; } = Array.Empty<BattlefieldTargetMarkerSnapshot>();
        public BattlefieldMapEventSnapshot[] MapEvents { get; init; } = Array.Empty<BattlefieldMapEventSnapshot>();
        public BattlefieldMapVisionPointSnapshot[] MapVisionPoints { get; init; } = Array.Empty<BattlefieldMapVisionPointSnapshot>();
        public BattlefieldMapVisionClusterSnapshot[] MapVisionClusters { get; init; } = Array.Empty<BattlefieldMapVisionClusterSnapshot>();
        public BattlefieldObjectiveSnapshot[] Objectives { get; init; } = Array.Empty<BattlefieldObjectiveSnapshot>();
        public BattlefieldMapObjectiveSnapshot[] MapObjectives { get; init; } = Array.Empty<BattlefieldMapObjectiveSnapshot>();
        public BattlefieldPlayerClusterSnapshot[] PlayerClusters { get; init; } = Array.Empty<BattlefieldPlayerClusterSnapshot>();
        public BattlefieldPlayerTrackSnapshot[] PlayerTracks { get; init; } = Array.Empty<BattlefieldPlayerTrackSnapshot>();
        public BattlefieldGroupTrackSnapshot[] EnemyMainGroupTrack { get; init; } = Array.Empty<BattlefieldGroupTrackSnapshot>();
        public BattlefieldTeamSituationSnapshot TeamSituation { get; init; } = new();
        public BattlefieldScoreSituationSnapshot ScoreSituation { get; init; } = new();
        public BattlefieldTimeSituationSnapshot TimeSituation { get; init; } = new();
        public BattlefieldAnnouncementSituationSnapshot AnnouncementSituation { get; init; } = new();
        public BattlefieldChatEventSituationSnapshot ChatEventSituation { get; init; } = new();
        public BattlefieldPlayerFrameEventSituationSnapshot PlayerFrameEvents { get; init; } = new();
        public BattlefieldKeySkillLogEventSituationSnapshot KeySkillLogEvents { get; init; } = new();
        public FrontlineKnowledgeSnapshot Knowledge { get; init; } = FrontlineKnowledgeBase.GetSnapshot(0, 0);
        public BattlefieldMapTacticsSnapshot MapTactics { get; init; } = new();
        public BattlefieldLimitBreakThreatSituationSnapshot LimitBreakThreats { get; init; } = new();
        public BattlefieldKeySkillThreatSituationSnapshot KeySkillThreats { get; init; } = new();
        public RespawnHistoryEntrySample[] RespawnHistory { get; init; } = Array.Empty<RespawnHistoryEntrySample>();
    }

    private sealed record DecisionLayerResult(
        PendingDecisionRefresh Pending,
        BattlefieldMapTacticsSnapshot MapTactics,
        BattlefieldDecisionSnapshot Decision,
        long CompletedAtTicks,
        double ElapsedMs,
        Exception? Exception,
        DecisionRefreshStateSnapshot StateSnapshot);

    private sealed record ContextDerivedAnalysisRequest(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        FrontlineMapType MapType,
        FrontlineMapKnowledgeSnapshot? MapKnowledge,
        BattlefieldMapEventSnapshot[] MapEvents,
        BattlefieldFieldMarkerSnapshot[] FieldMarkers,
        BattlefieldTargetMarkerSnapshot[] TargetMarkers,
        BattlefieldObjectiveActorSnapshot[] ObjectiveActors,
        BattlefieldPlayerSnapshot[] Players,
        BattlefieldPlayerSnapshot? LocalPlayer,
        long ObservedAtTicks,
        IReadOnlyDictionary<string, ObjectiveHistorySnapshot> ObjectiveHistoryById);

    private sealed record ContextDerivedAnalysisResult(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        ContextDerivedAnalysisSnapshot Snapshot);

    private sealed record ThreatAnalysisRequest(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        BattlefieldPlayerSnapshot[] Players,
        BattlefieldScoreSituationSnapshot ScoreSituation,
        FrontlineKnowledgeSnapshot Knowledge,
        BattlefieldTimeSituationSnapshot TimeSituation,
        BattlefieldLimitBreakSnapshot LocalLimitBreak,
        bool KeySkillLogEventsAvailable,
        string KeySkillLogEventSourceText,
        long ObservedAtTicks,
        IReadOnlyDictionary<ulong, long> LastEngagementTicksByPlayerId,
        IReadOnlyDictionary<(ulong GameObjectId, string SkillName), long> LastKeySkillUseTicksByPlayerSkill,
        IReadOnlyList<KeySkillUseHistoryEntry> KeySkillUseHistory);

    private sealed record ThreatAnalysisResult(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        BattlefieldLimitBreakThreatSituationSnapshot LimitBreakThreats,
        BattlefieldKeySkillThreatSituationSnapshot KeySkillThreats);

    private sealed record ClusterDerivedAnalysisRequest(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        BattlefieldPlayerSnapshot[] Players,
        BattlefieldMapVisionPointSnapshot[] MapVisionPoints,
        BattlefieldPlayerSnapshot? LocalPlayer);

    private sealed record ClusterDerivedAnalysisResult(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        ClusterDerivedAnalysisSnapshot Snapshot);

    private sealed record TeamDerivedAnalysisRequest(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        BattlefieldPlayerSnapshot[] Players,
        BattlefieldPlayerClusterSnapshot[] PlayerClusters,
        BattlefieldMapVisionPointSnapshot[] MapVisionPoints,
        BattlefieldMapVisionClusterSnapshot[] MapVisionClusters,
        BattlefieldPlayerSnapshot? LocalPlayer,
        long ObservedAtTicks,
        RespawnHistoryEntrySample[] RespawnHistory);

    private sealed record TeamDerivedAnalysisResult(
        uint TerritoryType,
        uint MapId,
        bool IsInFrontline,
        TeamDerivedAnalysisSnapshot Snapshot);

    private readonly record struct AllianceCenterWork(string Name, byte? Battalion, Vector3 Center, int Count);

    private readonly record struct CoordinatedSquadWork(
        float Score,
        float DirectionSimilarity,
        float FormationStability,
        float Radius,
        BattlefieldPlayerSnapshot[]? Players,
        byte? Battalion);

    private readonly record struct HighGroundDropCandidate(
        string Label,
        Vector3 Position,
        int Enemies,
        int Facing,
        int Pressure,
        float Severity,
        float HeightDelta,
        string Source);

    private readonly record struct LimitBreakThreatProfile(string ThreatType, float Weight, string Note);

    private readonly record struct KeySkillProfile(
        string SkillName,
        BattlefieldKeySkillKind Kind,
        int CooldownSeconds,
        float BaseThreat,
        string Note,
        bool ControlChain,
        bool DefenseBreak,
        bool ExecuteWindow,
        bool AreaPressure);

    private readonly record struct KeySkillNearbyContext(
        bool IsTargetingOpposingSide,
        int NearbyOpposingCount,
        int VulnerableCount,
        int ControlledCount,
        int ExecuteCount,
        int GuardedOrInvulnerableCount);

    private readonly record struct RespawnHistoryEntrySample(
        ulong GameObjectId,
        BattlefieldPlayerRelation Relation,
        long LastDeathTicks,
        long LastReviveTicks,
        long DeathStartedTicks);

    private readonly record struct KeySkillUseHistoryEntry(
        long ObservedAtTicks,
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

    private readonly record struct ScoreHistoryEntry(
        long Ticks,
        int MaelstromScore,
        int TwinAdderScore,
        int ImmortalFlamesScore);

    private readonly record struct ResourceTimerWork(
        int? Seconds,
        string Name,
        string Source);

    private readonly record struct ObjectTableSnapshot(
        BattlefieldPlayerSnapshot[] Players,
        BattlefieldObjectiveActorSnapshot[] ObjectiveActors,
        BattlefieldPlayerSnapshot? LocalPlayer);

    private sealed class ContextDerivedAnalysisSnapshot
    {
        public BattlefieldObjectiveSnapshot[] Objectives { get; init; } = Array.Empty<BattlefieldObjectiveSnapshot>();
        public BattlefieldMapObjectiveSnapshot[] MapObjectives { get; init; } = Array.Empty<BattlefieldMapObjectiveSnapshot>();
        public BattlefieldPlayerFrameEventSituationSnapshot PlayerFrameEvents { get; init; } = new();
    }

    private sealed class ClusterDerivedAnalysisSnapshot
    {
        public BattlefieldPlayerClusterSnapshot[] PlayerClusters { get; init; } = Array.Empty<BattlefieldPlayerClusterSnapshot>();
        public BattlefieldMapVisionClusterSnapshot[] MapVisionClusters { get; init; } = Array.Empty<BattlefieldMapVisionClusterSnapshot>();
    }

    private sealed class TeamDerivedAnalysisSnapshot
    {
        public BattlefieldTeamSummarySnapshot Friendly { get; init; } = new() { Side = BattlefieldTacticalSide.Friendly, Name = "我方" };
        public BattlefieldTeamSummarySnapshot Enemy { get; init; } = new() { Side = BattlefieldTacticalSide.Enemy, Name = "敌方" };
        public BattlefieldTeamSummarySnapshot Unknown { get; init; } = new() { Side = BattlefieldTacticalSide.Unknown, Name = "未知" };
        public BattlefieldAllianceSituationSnapshot[] Alliances { get; init; } = Array.Empty<BattlefieldAllianceSituationSnapshot>();
        public BattlefieldEnemyClusterSnapshot[] EnemyClusters { get; init; } = Array.Empty<BattlefieldEnemyClusterSnapshot>();
        public bool IsEnemySplit { get; init; }
        public string EnemySplitSummaryText { get; init; } = "敌方分兵状态样本不足";
        public BattlefieldFocusTargetSnapshot[] EnemyFocusTargets { get; init; } = Array.Empty<BattlefieldFocusTargetSnapshot>();
        public BattlefieldFocusTargetSnapshot[] FriendlyFocusTargets { get; init; } = Array.Empty<BattlefieldFocusTargetSnapshot>();
        public BattlefieldRespawnRhythmSnapshot RespawnRhythm { get; init; } = new();
    }

    private sealed class IncrementalObjectScanSession
    {
        public IncrementalObjectScanSession(
            uint territoryType,
            uint mapId,
            bool isInFrontline,
            FrontlineMapType mapType,
            TrackedObjectTableEntry[] objectEntries,
            ulong localGameObjectId,
            byte localBattalion,
            Vector3 localPosition,
            bool hasLocalPosition,
            IReadOnlyDictionary<uint, int> battleHighLevelByStatusId,
            TacticalStatusIdSets tacticalStatusIdSets,
            List<BattlefieldPlayerSnapshot> players,
            List<BattlefieldObjectiveActorSnapshot>? objectiveActors)
        {
            TerritoryType = territoryType;
            MapId = mapId;
            IsInFrontline = isInFrontline;
            MapType = mapType;
            ObjectEntries = objectEntries;
            LocalGameObjectId = localGameObjectId;
            LocalBattalion = localBattalion;
            LocalPosition = localPosition;
            HasLocalPosition = hasLocalPosition;
            BattleHighLevelByStatusId = battleHighLevelByStatusId;
            TacticalStatusIdSets = tacticalStatusIdSets;
            Players = players;
            ObjectiveActors = objectiveActors;
        }

        public uint TerritoryType { get; }
        public uint MapId { get; }
        public bool IsInFrontline { get; }
        public FrontlineMapType MapType { get; }
        public TrackedObjectTableEntry[] ObjectEntries { get; }
        public ulong LocalGameObjectId { get; }
        public byte LocalBattalion { get; }
        public Vector3 LocalPosition { get; }
        public bool HasLocalPosition { get; }
        public IReadOnlyDictionary<uint, int> BattleHighLevelByStatusId { get; }
        public TacticalStatusIdSets TacticalStatusIdSets { get; }
        public List<BattlefieldPlayerSnapshot> Players { get; }
        public List<BattlefieldObjectiveActorSnapshot>? ObjectiveActors { get; }
        public int NextIndex { get; set; }
        public BattlefieldPlayerSnapshot? LocalPlayer { get; set; }
    }

    private readonly record struct TrackedObjectTableEntry(int ObjectTableIndex, ulong GameObjectId);

    private readonly record struct BattlefieldObjectiveActorSnapshot(
        ulong GameObjectId,
        uint DataId,
        uint BaseId,
        string Name,
        BattlefieldMapObjectiveCategory Category,
        Vector3 Position,
        float DistanceToLocal,
        uint CurrentHp,
        uint MaxHp,
        int HpPercent,
        string ObjectKindText);

    private readonly record struct ObjectiveHistorySnapshot(
        long FirstSeenTicks,
        long LastSeenTicks,
        int? LastHpPercent,
        long LastDamageTicks,
        uint? RecentHpLoss,
        float RecentHpLossPerSecond)
    {
        public static ObjectiveHistorySnapshot Empty { get; } = new(0, 0, null, 0, null, 0f);
    }

    private sealed class ObjectiveFocusIndex
    {
        public ObjectiveFocusIndex(
            IReadOnlyDictionary<ulong, List<BattlefieldPlayerSnapshot>> targetingPlayersByObjectId,
            IReadOnlyDictionary<ulong, List<BattlefieldPlayerSnapshot>> castingPlayersByObjectId)
        {
            TargetingPlayersByObjectId = targetingPlayersByObjectId;
            CastingPlayersByObjectId = castingPlayersByObjectId;
        }

        public IReadOnlyDictionary<ulong, List<BattlefieldPlayerSnapshot>> TargetingPlayersByObjectId { get; }
        public IReadOnlyDictionary<ulong, List<BattlefieldPlayerSnapshot>> CastingPlayersByObjectId { get; }
    }

    private sealed class ObjectiveHistoryEntry
    {
        public ObjectiveHistoryEntry(long firstSeenTicks)
        {
            FirstSeenTicks = firstSeenTicks;
            LastSeenTicks = firstSeenTicks;
        }

        public long FirstSeenTicks { get; }
        public long LastSeenTicks { get; set; }
        public int? LastHpPercent { get; set; }
        public uint? LastCurrentHp { get; set; }
        public long LastHpSampleTicks { get; set; }
        public long LastDamageTicks { get; set; }
        public uint? RecentHpLoss { get; set; }
        public float RecentHpLossPerSecond { get; set; }
    }

    private readonly record struct ObjectiveFocusWork(
        int AttackerCount,
        int FriendlyAttackerCount,
        int EnemyAttackerCount,
        int CasterCount,
        bool IsBeingFocused,
        BattlefieldObjectiveContributionSnapshot[] Contributors,
        string ContributionSummaryText,
        string EnmitySourceText,
        string[] AggressorNames)
    {
        public static ObjectiveFocusWork Empty { get; } = new(
            0,
            0,
            0,
            0,
            false,
            Array.Empty<BattlefieldObjectiveContributionSnapshot>(),
            "暂无可见打目标玩家",
            "未读取到仇恨表",
            Array.Empty<string>());
    }

    private readonly record struct JobInfo(string Name, string Role);
}
