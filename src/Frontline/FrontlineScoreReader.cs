using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.STD;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace ai02;

public enum FrontlineScoreReaderState
{
    NotInFrontline,
    Scanning,
    Ready,
    Error
}

public sealed class FrontlineScoreReader : IDisposable
{
    private const int MaxReadableScore = 3000;
    private const int MinReadableScoreLimit = 100;
    private const int MaxMatchTimeSeconds = 20 * 60;
    private const int DefaultUpdateIntervalMs = 1000;
    private const long FrontlineDetectionCacheMs = 2500;
    private const long FrontlineDetectionGraceMs = 15000;

    private static readonly HashSet<uint> FrontlineTerritoryIds = new()
    {
        376, 431, 554, 888, 1273, 1313
    };

    private static readonly string[] FrontlineDetectionAddonNames =
    {
        "PvPFrontlineGauge",
        "PvPFrontlineScore",
        "PvPFrontlineResult",
        "PvPMatchScoreBoard",
        "_PvPScoreBoard"
    };

    private static readonly Regex FractionScoreRegex =
        new(@"(?<!\d)(\d{1,3}(?:[,，]\d{3})+|\d{1,5})\s*[/／]\s*(\d{1,3}(?:[,，]\d{3})+|\d{1,5})(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberRegex =
        new(@"(?<![\d:])(\d{1,3}(?:,\d{3})+|\d{1,5})(?![\d:])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeRegex =
        new(@"(?<!\d)(\d{1,2}):([0-5]\d)(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;
    private readonly IGameGui _gameGui;
    private readonly IDataManager _dataManager;
    private readonly IDutyState _dutyState;
    private readonly Configuration _configuration;

    private int _maelstromScore;
    private int _twinAdderScore;
    private int _immortalFlamesScore;
    private int _scoreLimit;
    private int _matchTimeSeconds;
    private int _pendingMaelstromScore = FrontlineScoreConfirmationPolicy.NoPendingScore;
    private int _pendingMaelstromScoreReads;
    private int _pendingTwinAdderScore = FrontlineScoreConfirmationPolicy.NoPendingScore;
    private int _pendingTwinAdderScoreReads;
    private int _pendingImmortalFlamesScore = FrontlineScoreConfirmationPolicy.NoPendingScore;
    private int _pendingImmortalFlamesScoreReads;
    private long _lastUpdateTicks;
    private int _matchTimeAnchorSeconds;
    private long _matchTimeAnchorTicks;
    private string _matchTimeSource = "未读取";
    private string _lastReadSource = "未读取";
    private string _lastFrontlineDetectionSource = "未检测";
    private bool _hasScoreRead;
    private bool _lastDutyStarted;
    private bool _hasMatchTimeAnchor;
    private bool _pendingMatchStartAnchor;
    private bool _disposed;
    private uint _lastFrontlineTerritoryType;
    private long _lastFrontlineSeenTicks = -1;
    private long _lastFrontlineDetectionSampleTicks = -FrontlineDetectionCacheMs;
    private uint _cachedFrontlineDetectionTerritoryType;
    private uint _cachedFrontlineDetectionMapId;
    private bool _cachedFrontlineDetectionDutyStarted;
    private bool _cachedFrontlineDetectionResult;

    private FrontlineScoreReaderState _state = FrontlineScoreReaderState.NotInFrontline;

    public FrontlineScoreReader(
        Configuration configuration,
        IFramework framework,
        IPluginLog log,
        IClientState clientState,
        IGameGui gameGui,
        IDataManager dataManager,
        IDutyState dutyState)
    {
        _configuration = configuration;
        _framework = framework;
        _log = log;
        _clientState = clientState;
        _gameGui = gameGui;
        _dataManager = dataManager;
        _dutyState = dutyState;
        _lastDutyStarted = _dutyState.IsDutyStarted;
        var intervalMs = _configuration.Performance?.EffectiveScoreScanIntervalMs ?? DefaultUpdateIntervalMs;
        _lastUpdateTicks = Environment.TickCount64 - intervalMs + Math.Min(350, intervalMs);

        _framework.Update += OnFrameworkUpdate;
        _log.Info("[FrontlineScoreReader] 已初始化，OCR 已移除，使用结构化比分来源");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _framework.Update -= OnFrameworkUpdate;
        ResetState();
        _log.Info("[FrontlineScoreReader] 已释放");
    }

    public bool IsInFrontline()
    {
        var now = Environment.TickCount64;
        var territory = _clientState.TerritoryType;
        var mapId = _clientState.MapId;
        var dutyStarted = _dutyState.IsDutyStarted;

        if (_lastFrontlineDetectionSampleTicks >= 0
            && now - _lastFrontlineDetectionSampleTicks < FrontlineDetectionCacheMs
            && territory == _cachedFrontlineDetectionTerritoryType
            && mapId == _cachedFrontlineDetectionMapId
            && dutyStarted == _cachedFrontlineDetectionDutyStarted)
        {
            return _cachedFrontlineDetectionResult;
        }

        var result = DetectFrontline(now, territory, mapId, dutyStarted);
        _lastFrontlineDetectionSampleTicks = now;
        _cachedFrontlineDetectionTerritoryType = territory;
        _cachedFrontlineDetectionMapId = mapId;
        _cachedFrontlineDetectionDutyStarted = dutyStarted;
        _cachedFrontlineDetectionResult = result;
        return result;
    }

    private bool DetectFrontline(long now, uint territory, uint mapId, bool dutyStarted)
    {
        if (territory == 0)
        {
            _lastFrontlineDetectionSource = "territory=0";
            return false;
        }

        if (TryMatchFrontlineTerritorySheet(territory, out var sheetSource))
        {
            MarkFrontlineSeen(territory, now, sheetSource);
            return true;
        }

        if (FrontlineTerritoryIds.Contains(territory))
        {
            MarkFrontlineSeen(territory, now, $"固定地图ID:{territory}");
            return true;
        }

        if (TryMatchFrontlineDirector(out var directorSource))
        {
            MarkFrontlineSeen(territory, now, directorSource);
            return true;
        }

        if (HasVisibleFrontlineAddon(out var addonName))
        {
            MarkFrontlineSeen(territory, now, $"前线UI:{addonName}");
            return true;
        }

        if (TryMatchFrontlineScoreSource(out var scoreSource))
        {
            MarkFrontlineSeen(territory, now, scoreSource);
            return true;
        }

        if (territory != 0
            && territory == _lastFrontlineTerritoryType
            && _lastFrontlineSeenTicks >= 0
            && now - _lastFrontlineSeenTicks <= FrontlineDetectionGraceMs)
        {
            _lastFrontlineDetectionSource = $"前线短时缓存:{territory}, age={now - _lastFrontlineSeenTicks}ms";
            return true;
        }

        _lastFrontlineDetectionSource = $"未命中 territory={territory}, map={mapId}, duty={dutyStarted}";
        return false;
    }

    private void MarkFrontlineSeen(uint territory, long now, string source)
    {
        _lastFrontlineTerritoryType = territory;
        _lastFrontlineSeenTicks = now;
        _lastFrontlineDetectionSource = source;
    }

    private unsafe bool TryMatchFrontlineScoreSource(out string source)
    {
        source = string.Empty;
        try
        {
            if (!TryReadStructuredScoreData(out var result) || !HasScorePayload(result))
                return false;

            source = $"比分来源:{result.Source}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "[FrontlineScoreReader] 比分来源前线判断失败");
            return false;
        }
    }

    public FrontlineScoreReaderState GetState() => _state;

    public (int Maelstrom, int TwinAdder, int ImmortalFlames) GetScores()
        => (_maelstromScore, _twinAdderScore, _immortalFlamesScore);

    public int GetMatchTimeSeconds()
    {
        RefreshMatchTime(Environment.TickCount64);
        return _matchTimeSeconds;
    }

    public unsafe IReadOnlyList<string> GetRawScoreSourceDebugLines()
    {
        var lines = new List<string>(32);

        try
        {
            AppendToDoListArrayDebugLines(lines);
        }
        catch (Exception ex)
        {
            lines.Add($"ToDoListArray 读取异常: {ex.Message}");
        }

        try
        {
            AppendDirectorTodoDebugLines(lines);
        }
        catch (Exception ex)
        {
            lines.Add($"DirectorTodo 读取异常: {ex.Message}");
        }

        if (lines.Count == 0)
            lines.Add("未读取到 ToDo/Director 原始数据。");

        return lines;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_disposed)
            return;

        var now = Environment.TickCount64;
        var intervalMs = _configuration.Performance?.EffectiveScoreScanIntervalMs ?? DefaultUpdateIntervalMs;
        if (now - _lastUpdateTicks < intervalMs)
            return;

        _lastUpdateTicks = now;
        RefreshDutyStartSignal();

        if (!IsInFrontline())
        {
            if (_state != FrontlineScoreReaderState.NotInFrontline)
            {
                ResetState(clearPendingMatchStartAnchor: !_dutyState.IsDutyStarted);
                _state = FrontlineScoreReaderState.NotInFrontline;
                _lastReadSource = "未在纷争前线";
            }

            return;
        }

        if (_state == FrontlineScoreReaderState.NotInFrontline)
            _state = FrontlineScoreReaderState.Scanning;

        if (!TryRefreshDirectorMatchTime(now))
            ApplyPendingMatchStartAnchor(now);
        RefreshMatchTime(now);

        ReadScoreData(now);
    }

    private void ResetState(bool clearPendingMatchStartAnchor = true)
    {
        _maelstromScore = 0;
        _twinAdderScore = 0;
        _immortalFlamesScore = 0;
        _scoreLimit = 0;
        _matchTimeSeconds = 0;
        _matchTimeAnchorSeconds = 0;
        _matchTimeAnchorTicks = 0;
        _matchTimeSource = "未读取";
        _hasMatchTimeAnchor = false;
        if (clearPendingMatchStartAnchor)
            _pendingMatchStartAnchor = false;
        _hasScoreRead = false;
        ClearPendingScores();
    }

    private void RefreshDutyStartSignal()
    {
        var dutyStarted = _dutyState.IsDutyStarted;
        if (!dutyStarted)
        {
            if (_lastDutyStarted || _hasMatchTimeAnchor || _hasScoreRead || HasAnyScore() || _matchTimeSeconds > 0)
            {
                ResetState();
                if (_state != FrontlineScoreReaderState.NotInFrontline)
                    _state = FrontlineScoreReaderState.Scanning;
                _lastReadSource = "等待下一局开局锚点";
            }

            _lastDutyStarted = false;
            _pendingMatchStartAnchor = false;
            return;
        }

        if (!_lastDutyStarted)
        {
            if (_hasMatchTimeAnchor || _hasScoreRead || HasAnyScore() || _matchTimeSeconds > 0)
                ResetState(clearPendingMatchStartAnchor: false);

            _pendingMatchStartAnchor = true;
        }

        _lastDutyStarted = true;
    }

    private void ApplyPendingMatchStartAnchor(long now)
    {
        if (!_pendingMatchStartAnchor || _hasMatchTimeAnchor)
            return;

        SetMatchTimeAnchor(MaxMatchTimeSeconds, now, "对局开始推算");
        _pendingMatchStartAnchor = false;
    }

    private unsafe bool TryRefreshDirectorMatchTime(long now)
    {
        if (!_dutyState.IsDutyStarted || !TryReadDirectorRemainingSeconds(out var remainingSeconds))
            return false;

        if (!_hasMatchTimeAnchor
            || Math.Abs(_matchTimeSeconds - remainingSeconds) > 2
            || !_matchTimeSource.Equals("ContentDirector剩余时间", StringComparison.Ordinal))
        {
            SetMatchTimeAnchor(remainingSeconds, now, "ContentDirector剩余时间");
        }

        _pendingMatchStartAnchor = false;
        return true;
    }

    private static unsafe bool TryReadDirectorRemainingSeconds(out int remainingSeconds)
    {
        remainingSeconds = 0;

        var framework = EventFramework.Instance();
        if (framework == null)
            return false;

        var director = framework->GetContentDirector();
        if (director == null)
            return false;

        var currentTimestamp = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var value = director->GetTimeRemaining(currentTimestamp);
        if (value is <= 0 or > MaxMatchTimeSeconds)
            return false;

        remainingSeconds = value;
        return true;
    }

    private bool TryMatchFrontlineTerritorySheet(uint territory, out string source)
    {
        source = string.Empty;
        try
        {
            var territorySheet = _dataManager.GetExcelSheet<TerritoryType>();
            if (!territorySheet.TryGetRow(territory, out var row))
                return false;

            if (row.TerritoryIntendedUse.RowId == 18)
            {
                source = $"TerritoryIntendedUse=18 territory={territory}";
                return true;
            }

            if (row.ContentFinderCondition.RowId != 0
                && IsFrontlineContentFinderCondition(row.ContentFinderCondition.Value, out var contentSource))
            {
                source = $"Territory.CFC:{contentSource} territory={territory}";
                return true;
            }

            if (row.IsPvpZone && row.BattalionMode > 0)
            {
                source = $"玩家对战战场区域 territory={territory}, battalionMode={row.BattalionMode}";
                return true;
            }
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "[FrontlineScoreReader] TerritoryType 前线判断失败");
        }

        return false;
    }

    private unsafe bool TryMatchFrontlineDirector(out string source)
    {
        source = string.Empty;
        try
        {
            var framework = EventFramework.Instance();
            if (framework == null)
                return false;

            var contentType = EventFramework.GetCurrentContentType();
            var contentId = EventFramework.GetCurrentContentId();
            var contentFinderConditionId = EventFramework.GetContentFinderCondition(contentType, contentId);
            if (contentFinderConditionId == 0)
                return false;

            var conditionSheet = _dataManager.GetExcelSheet<ContentFinderCondition>();
            if (!conditionSheet.TryGetRow(contentFinderConditionId, out var condition))
                return false;

            if (!IsFrontlineContentFinderCondition(condition, out var contentSource))
                return false;

            source = $"Director.CFC:{contentSource} id={contentFinderConditionId}";
            return true;
        }
        catch (Exception ex)
        {
            _log.Verbose(ex, "[FrontlineScoreReader] Director 前线判断失败");
            return false;
        }
    }

    private static bool IsFrontlineContentFinderCondition(ContentFinderCondition condition, out string source)
    {
        if (condition.DailyFrontlineChallenge)
        {
            source = $"每日纷争前线:{condition.Name}";
            return true;
        }

        if (condition.PvP && condition.QueueMaxPlayers >= 24)
        {
            source = $"大型玩家对战:{condition.Name}, max={condition.QueueMaxPlayers}";
            return true;
        }

        source = string.Empty;
        return false;
    }

    private unsafe bool HasVisibleFrontlineAddon(out string addonName)
    {
        foreach (var name in FrontlineDetectionAddonNames)
        {
            var addonPtr = _gameGui.GetAddonByName(name, 1);
            if (addonPtr == IntPtr.Zero)
                addonPtr = _gameGui.GetAddonByName(name, 0);

            if (addonPtr == IntPtr.Zero)
                continue;

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon == null || !addon->IsVisible)
                continue;

            addonName = name;
            return true;
        }

        addonName = string.Empty;
        return false;
    }

    private void RefreshMatchTime(long now)
    {
        if (!_hasMatchTimeAnchor)
            return;

        var elapsedSeconds = Math.Max(0, (int)((now - _matchTimeAnchorTicks) / 1000));
        _matchTimeSeconds = Math.Clamp(_matchTimeAnchorSeconds - elapsedSeconds, 0, MaxMatchTimeSeconds);
    }

    private void SetMatchTimeAnchor(int remainingSeconds, long now, string source)
    {
        remainingSeconds = Math.Clamp(remainingSeconds, 0, MaxMatchTimeSeconds);
        _matchTimeAnchorSeconds = remainingSeconds;
        _matchTimeAnchorTicks = now;
        _matchTimeSeconds = remainingSeconds;
        _matchTimeSource = source;
        _lastReadSource = source;
        _hasMatchTimeAnchor = true;
        _state = FrontlineScoreReaderState.Ready;
    }

    private void ReadScoreData(long now)
    {
        try
        {
            if (TryReadScoreData(out var result))
            {
                ApplyReadResult(result, now);
                return;
            }

            var hasAnyData = HasAnyScore() || _hasMatchTimeAnchor || _matchTimeSeconds > 0;
            _state = hasAnyData ? FrontlineScoreReaderState.Ready : FrontlineScoreReaderState.Scanning;
            if (!hasAnyData)
                _lastReadSource = "等待可靠比分来源/开局锚点";
            if (hasAnyData && _hasMatchTimeAnchor)
                _lastReadSource = _matchTimeSource;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "[FrontlineScoreReader] 读取失败");
            _state = FrontlineScoreReaderState.Error;
            _lastReadSource = "读取异常";
        }
    }

    private unsafe bool TryReadScoreData(out ScoreReadResult result)
    {
        result = default;
        return TryReadStructuredScoreData(out result);
    }

    private unsafe bool TryReadStructuredScoreData(out ScoreReadResult result)
    {
        if (TryReadTodoListArrayScoreData(out result))
            return true;

        if (TryReadDirectorTodoScoreData(out result))
            return true;

        result = default;
        return false;
    }

    private unsafe bool TryReadTodoListArrayScoreData(out ScoreReadResult result)
    {
        result = default;

        var numbers = ToDoListNumberArray.Instance();
        if (numbers == null)
            return false;

        var strings = ToDoListStringArray.Instance();
        var rows = new List<StructuredScoreRow>(10);
        var count = Math.Clamp(numbers->DutyObjectiveCount, 0, 10);
        var objectiveTypes = numbers->DutyObjectiveTypes;
        var objectiveValues = numbers->DutyObjectiveValue;
        var objectiveTexts = strings == null ? default : strings->DutyObjectives;
        var objectiveTimers = strings == null ? default : strings->DutyTimers;

        for (var i = 0; i < count; i++)
        {
            var text = strings == null ? string.Empty : CleanDebugText(objectiveTexts[i].ToString());
            var timer = strings == null ? string.Empty : CleanDebugText(objectiveTimers[i].ToString());
            rows.Add(new StructuredScoreRow(
                i,
                $"{text} {timer}".Trim(),
                objectiveValues[i],
                null,
                $"type={objectiveTypes[i]}"));
        }

        return TryBuildScoreReadResult(rows, "ToDoListArray", out result);
    }

    private unsafe bool TryReadDirectorTodoScoreData(out ScoreReadResult result)
    {
        result = default;

        var framework = EventFramework.Instance();
        if (framework == null)
            return false;

        var rows = new List<StructuredScoreRow>(16);
        var contentDirector = framework->GetContentDirector();
        if (contentDirector != null)
            AddDirectorTodoScoreRows(rows, "ContentDirector", contentDirector->GetDirectorTodos());

        var instanceDirector = framework->GetInstanceContentDirector();
        if (instanceDirector != null && (nint)instanceDirector != (nint)contentDirector)
            AddDirectorTodoScoreRows(rows, "InstanceContentDirector", instanceDirector->GetDirectorTodos());

        return TryBuildScoreReadResult(rows, "DirectorTodo", out result);
    }

    private static unsafe void AddDirectorTodoScoreRows(List<StructuredScoreRow> rows, string source, StdVector<DirectorTodo>* todos)
    {
        if (todos == null)
            return;

        var span = todos->AsSpan();
        var count = Math.Clamp(todos->Count, 0, 32);
        for (var i = 0; i < count && i < span.Length; i++)
        {
            ref var todo = ref span[i];
            if (!todo.Enabled)
                continue;

            rows.Add(new StructuredScoreRow(
                i,
                CleanDebugText(todo.Text.ToString()),
                todo.CurrentCount,
                IsValidScoreLimit(todo.NeededCount) ? todo.NeededCount : null,
                $"{source}/{todo.Type}"));
        }
    }

    private static bool TryBuildScoreReadResult(IReadOnlyList<StructuredScoreRow> rows, string source, out ScoreReadResult result)
    {
        var parserRows = rows
            .Select(row => new FrontlineStructuredScoreRow(row.Index, row.Text, row.Value, row.NeededCount, row.Source))
            .ToArray();
        if (FrontlineScoreTextParser.TryBuildStructuredScoreResult(parserRows, source, out var parsed))
        {
            result = new ScoreReadResult(
                parsed.Maelstrom,
                parsed.TwinAdder,
                parsed.ImmortalFlames,
                parsed.ScoreLimit,
                parsed.Source);
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryBuildStructuredScoreResult(IReadOnlyList<StructuredScoreRow> rows, string source, out ScoreReadResult result)
    {
        var scores = new Dictionary<AllianceKey, int>();
        var limits = new List<int>();
        var sources = new List<string>(3);

        foreach (var row in rows)
        {
            if (!TryIdentifyAlliance(row.Text, out var alliance))
                continue;

            if (!TryExtractStructuredRowScore(row, out var score, out var scoreLimit))
                continue;

            scores[alliance] = score;
            if (scoreLimit.HasValue)
                limits.Add(scoreLimit.Value);
            sources.Add($"{alliance}@{row.Index}:{row.Source}");
        }

        if (scores.TryGetValue(AllianceKey.Maelstrom, out var maelstrom)
            && scores.TryGetValue(AllianceKey.TwinAdder, out var twinAdder)
            && scores.TryGetValue(AllianceKey.ImmortalFlames, out var immortalFlames)
            && IsLikelyScoreTriple(maelstrom, twinAdder, immortalFlames, allowAllZero: true))
        {
            var scoreLimit = ResolveScoreLimit(limits);
            result = new ScoreReadResult(
                maelstrom,
                twinAdder,
                immortalFlames,
                scoreLimit > 0 ? scoreLimit : null,
                $"{source}/结构化阵营分数[{string.Join(",", sources.Take(3))}]");
            return true;
        }

        result = default;
        return false;
    }

    private static bool TryExtractStructuredRowScore(StructuredScoreRow row, out int score, out int? scoreLimit)
    {
        if (TryExtractFractionScore(row.Text, out var fractionScore, out var fractionLimit))
        {
            score = fractionScore;
            scoreLimit = fractionLimit;
            return true;
        }

        if (IsValidFrontlineScore(row.Value))
        {
            score = row.Value;
            scoreLimit = row.NeededCount;
            return true;
        }

        if (TryExtractStructuralScore(row.Text, out var structuralScore))
        {
            score = structuralScore;
            scoreLimit = row.NeededCount;
            return true;
        }

        score = 0;
        scoreLimit = null;
        return false;
    }


    private static bool HasScorePayload(ScoreReadResult result)
        => IsLikelyScoreTriple(result.Maelstrom, result.TwinAdder, result.ImmortalFlames, allowAllZero: true);

    private unsafe void AppendToDoListArrayDebugLines(List<string> lines)
    {
        var numbers = ToDoListNumberArray.Instance();
        var strings = ToDoListStringArray.Instance();

        if (numbers == null)
        {
            lines.Add("ToDoListNumberArray: null");
            return;
        }

        var title = strings == null ? string.Empty : CleanDebugText(strings->ActiveDutyTitle.ToString());
        var titleText = strings == null ? string.Empty : CleanDebugText(strings->DutyTitleText.ToString());
        var timer = strings == null ? string.Empty : CleanDebugText(strings->DutyTimer.ToString());
        lines.Add($"ToDoList: dutyCount={numbers->DutyObjectiveCount}, timer='{timer}', title='{FirstNonEmpty(title, titleText)}'");

        var count = Math.Clamp(numbers->DutyObjectiveCount, 0, 10);
        var objectiveTypes = numbers->DutyObjectiveTypes;
        var objectiveValues = numbers->DutyObjectiveValue;
        var objectiveTexts = strings == null ? default : strings->DutyObjectives;
        var objectiveTimers = strings == null ? default : strings->DutyTimers;

        for (var i = 0; i < count; i++)
        {
            var text = strings == null ? string.Empty : CleanDebugText(objectiveTexts[i].ToString());
            var objectiveTimer = strings == null ? string.Empty : CleanDebugText(objectiveTimers[i].ToString());
            lines.Add($"ToDo[{i}]: type={objectiveTypes[i]}, value={objectiveValues[i]}, timer='{objectiveTimer}', text='{text}'");
        }
    }

    private unsafe void AppendDirectorTodoDebugLines(List<string> lines)
    {
        var framework = EventFramework.Instance();
        if (framework == null)
        {
            lines.Add("EventFramework: null");
            return;
        }

        var contentDirector = framework->GetContentDirector();
        if (contentDirector != null)
            AppendDirectorTodoVectorDebugLines(lines, "ContentDirector", contentDirector->GetDirectorTodos());
        else
            lines.Add("ContentDirector: null");

        var instanceDirector = framework->GetInstanceContentDirector();
        if (instanceDirector != null && (nint)instanceDirector != (nint)contentDirector)
            AppendDirectorTodoVectorDebugLines(lines, "InstanceContentDirector", instanceDirector->GetDirectorTodos());
    }

    private static unsafe void AppendDirectorTodoVectorDebugLines(List<string> lines, string label, StdVector<DirectorTodo>* todos)
    {
        if (todos == null)
        {
            lines.Add($"{label}: todos=null");
            return;
        }

        var count = Math.Clamp(todos->Count, 0, 12);
        lines.Add($"{label}: todos={todos->Count}");
        var span = todos->AsSpan();
        for (var i = 0; i < count && i < span.Length; i++)
        {
            ref var todo = ref span[i];
            var text = CleanDebugText(todo.Text.ToString());
            lines.Add($"{label}[{i}]: enabled={todo.Enabled}, type={todo.Type}, complete={todo.Complete}, current={todo.CurrentCount}, needed={todo.NeededCount}, pct={todo.CurrentPercentage}/{todo.NeededPercentage}, icon={todo.IconId}, text='{text}'");
        }
    }

    private static string FirstNonEmpty(string first, string second)
        => !string.IsNullOrWhiteSpace(first) ? first : second;

    private static string CleanDebugText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var cleaned = text
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        return cleaned.Length <= 96 ? cleaned : cleaned[..96];
    }


    private void ApplyReadResult(ScoreReadResult result, long now)
    {
        var notes = new List<string>(2);
        if (HasScorePayload(result))
        {
            var heldScoreForConfirmation = false;
            heldScoreForConfirmation |= !FrontlineScoreConfirmationPolicy.TryApplyCandidate(
                ref _maelstromScore,
                ref _pendingMaelstromScore,
                ref _pendingMaelstromScoreReads,
                result.Maelstrom,
                true);
            heldScoreForConfirmation |= !FrontlineScoreConfirmationPolicy.TryApplyCandidate(
                ref _twinAdderScore,
                ref _pendingTwinAdderScore,
                ref _pendingTwinAdderScoreReads,
                result.TwinAdder,
                true);
            heldScoreForConfirmation |= !FrontlineScoreConfirmationPolicy.TryApplyCandidate(
                ref _immortalFlamesScore,
                ref _pendingImmortalFlamesScore,
                ref _pendingImmortalFlamesScoreReads,
                result.ImmortalFlames,
                true);

            if (heldScoreForConfirmation)
                notes.Add("异常分数等待二次确认");
            else
                _hasScoreRead = true;
        }

        if (result.ScoreLimit.HasValue)
            _scoreLimit = result.ScoreLimit.Value;

        _lastReadSource = notes.Count == 0 ? result.Source : $"{result.Source}; {string.Join("; ", notes)}";
        _state = FrontlineScoreReaderState.Ready;
    }

    private void ClearPendingScores()
    {
        FrontlineScoreConfirmationPolicy.ClearPendingScore(ref _pendingMaelstromScore, ref _pendingMaelstromScoreReads);
        FrontlineScoreConfirmationPolicy.ClearPendingScore(ref _pendingTwinAdderScore, ref _pendingTwinAdderScoreReads);
        FrontlineScoreConfirmationPolicy.ClearPendingScore(ref _pendingImmortalFlamesScore, ref _pendingImmortalFlamesScoreReads);
    }

    private bool HasAnyScore()
        => _maelstromScore != 0 || _twinAdderScore != 0 || _immortalFlamesScore != 0;

    private static bool TryExtractFractionScore(string text, out int score, out int scoreLimit)
    {
        score = 0;
        scoreLimit = 0;

        if (string.IsNullOrWhiteSpace(text) || TimeRegex.IsMatch(text))
            return false;

        var fractionMatch = FractionScoreRegex.Match(text);
        if (!fractionMatch.Success
            || !TryParseNumber(fractionMatch.Groups[1].Value, out var fractionScore)
            || !TryParseNumber(fractionMatch.Groups[2].Value, out var targetScore)
            || !IsValidScoreLimit(targetScore)
            || !IsValidFrontlineScore(fractionScore)
            || fractionScore > targetScore)
        {
            return false;
        }

        score = fractionScore;
        scoreLimit = targetScore;
        return true;
    }

    private static int ResolveScoreLimit(IEnumerable<int> scoreLimits)
    {
        var grouped = scoreLimits
            .Where(IsValidScoreLimit)
            .GroupBy(limit => limit)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Key)
            .FirstOrDefault();

        return grouped?.Key ?? 0;
    }

    private static bool TryExtractStructuralScore(string text, out int score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(text) || TimeRegex.IsMatch(text))
            return false;

        if (TryExtractFractionScore(text, out var fractionScore, out _))
        {
            score = fractionScore;
            return true;
        }

        var values = NumberRegex.Matches(text)
            .Select(match => match.Groups[1].Value)
            .Select(value => TryParseNumber(value, out var number) ? number : -1)
            .Where(IsValidFrontlineScore)
            .ToArray();

        if (values.Length == 0)
            return false;

        score = values[^1];
        return true;
    }


    private static bool IsLikelyScoreTriple(int maelstrom, int twinAdder, int immortalFlames, bool allowAllZero)
    {
        if (!IsValidFrontlineScore(maelstrom) || !IsValidFrontlineScore(twinAdder) || !IsValidFrontlineScore(immortalFlames))
            return false;

        if (allowAllZero && maelstrom == 0 && twinAdder == 0 && immortalFlames == 0)
            return true;

        var max = Math.Max(maelstrom, Math.Max(twinAdder, immortalFlames));
        if (max < 5)
            return false;

        if (maelstrom == 1 && twinAdder == 2 && immortalFlames == 3)
            return false;

        return true;
    }

    private static bool IsValidFrontlineScore(int value)
        => value is >= 0 and <= MaxReadableScore;

    private static bool IsValidScoreLimit(int value)
        => value is >= MinReadableScoreLimit and <= MaxReadableScore;

    private static bool TryParseNumber(string text, out int number)
        => int.TryParse(
            text.Replace(",", string.Empty).Replace("，", string.Empty),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out number);

    private static bool TryIdentifyAlliance(string text, out AllianceKey alliance)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            alliance = default;
            return false;
        }

        var normalized = NormalizeText(text);
        if (normalized.Contains("maelstrom", StringComparison.Ordinal)
            || normalized.Contains("黑涡", StringComparison.Ordinal)
            || normalized.Contains("黑渦", StringComparison.Ordinal)
            || normalized.Contains("黒渦", StringComparison.Ordinal))
        {
            alliance = AllianceKey.Maelstrom;
            return true;
        }

        if (normalized.Contains("twinadder", StringComparison.Ordinal)
            || normalized.Contains("adders", StringComparison.Ordinal)
            || normalized.Contains("双蛇", StringComparison.Ordinal)
            || normalized.Contains("雙蛇", StringComparison.Ordinal))
        {
            alliance = AllianceKey.TwinAdder;
            return true;
        }

        if (normalized.Contains("immortalflames", StringComparison.Ordinal)
            || normalized.Contains("flames", StringComparison.Ordinal)
            || normalized.Contains("恒辉", StringComparison.Ordinal)
            || normalized.Contains("恒輝", StringComparison.Ordinal)
            || normalized.Contains("不灭", StringComparison.Ordinal)
            || normalized.Contains("不滅", StringComparison.Ordinal))
        {
            alliance = AllianceKey.ImmortalFlames;
            return true;
        }

        alliance = default;
        return false;
    }

    private static bool ContainsAny(string text, IEnumerable<string> needles)
    {
        foreach (var needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeText(string text)
        => text.Replace(" ", string.Empty)
            .Replace("\u3000", string.Empty)
            .Replace("·", string.Empty)
            .Replace(".", string.Empty)
            .ToLowerInvariant();


    public FrontlineSnapshot GetSnapshot()
    {
        RefreshMatchTime(Environment.TickCount64);
        var highestScore = Math.Max(_maelstromScore, Math.Max(_twinAdderScore, _immortalFlamesScore));
        var hasLeader = highestScore > 0;

        return new FrontlineSnapshot
        {
            IsInFrontline = IsInFrontline(),
            HasScoreData = _hasScoreRead && _state == FrontlineScoreReaderState.Ready,
            HasMatchTime = _hasMatchTimeAnchor,
            ScoreReaderState = _state,
            MatchTimeRemaining = _matchTimeSeconds,
            Alliances = new[]
            {
                new AllianceData
                {
                    AllianceId = 1,
                    Score = _maelstromScore,
                    TargetScore = _scoreLimit,
                    Name = "黑涡团",
                    IsPlayerAlliance = false,
                    IsLeading = hasLeader && _maelstromScore == highestScore
                },
                new AllianceData
                {
                    AllianceId = 2,
                    Score = _twinAdderScore,
                    TargetScore = _scoreLimit,
                    Name = "双蛇党",
                    IsPlayerAlliance = false,
                    IsLeading = hasLeader && _twinAdderScore == highestScore
                },
                new AllianceData
                {
                    AllianceId = 3,
                    Score = _immortalFlamesScore,
                    TargetScore = _scoreLimit,
                    Name = "恒辉队",
                    IsPlayerAlliance = false,
                    IsLeading = hasLeader && _immortalFlamesScore == highestScore
                }
            },
            Nodes = Array.Empty<NodeData>(),
            LocalPlayer = new PlayerData { JobName = "未知", HpPercentage = 0f, IsDead = false, RespawnTimer = 0f, KillCount = 0 },
            PartyPositions = Array.Empty<string>(),
            LargeGroupPositions = Array.Empty<string>(),
            DebugInfo = $"State:{_state}, Scores:[{_maelstromScore},{_twinAdderScore},{_immortalFlamesScore}], Limit:{(_scoreLimit > 0 ? _scoreLimit.ToString(CultureInfo.InvariantCulture) : "Unknown")}, Time:{_matchTimeSeconds}s, Source:{_lastReadSource}, Frontline:{_lastFrontlineDetectionSource}, TimeAnchor:{_matchTimeSource}"
        };
    }

    private enum AllianceKey
    {
        Maelstrom,
        TwinAdder,
        ImmortalFlames
    }

    private readonly record struct ScoreReadResult(int Maelstrom, int TwinAdder, int ImmortalFlames, int? ScoreLimit, string Source);


    private readonly record struct StructuredScoreRow(int Index, string Text, int Value, int? NeededCount, string Source);
}
