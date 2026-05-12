using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Game.Command;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.Gui;
using System;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using System.Numerics;

namespace ai02;

public class Plugin : IDalamudPlugin
{
    public const string DisplayName = "前线战术指挥";
    public const string Tagline = "纷争前线的战场态势感知、战术决策与战斗指挥界面";

    public string Name => DisplayName;

    private IDalamudPluginInterface PluginInterface { get; init; }
    private ICommandManager CommandManager { get; init; }
    public IClientState ClientState { get; init; }
    public IObjectTable ObjectTable { get; init; }
    public IFramework Framework { get; init; }
    public IGameGui GameGui { get; init; }
    public IPartyList PartyList { get; init; }
    public IPluginLog Log { get; init; }
    public IChatGui ChatGui { get; init; }
    public ICondition Condition { get; init; }
    public IDutyState DutyState { get; init; }
    public Configuration Configuration { get; init; }
    public WindowSystem WindowSystem { get; init; } = new("ai02");

    public MainWindow MainWindow { get; init; }
    public FrontlineScoreReader FrontlineScoreReader { get; init; }
    public AreaMapProjectionService AreaMapProjectionService { get; init; }
    public FrontlineRadar FrontlineRadar { get; init; }
    public FloatingToggle FloatingToggle { get; init; }
    public LimitBreakService LimitBreakService { get; init; }
    public FrontlineAnnouncementReader FrontlineAnnouncementReader { get; init; }
    public FrontlineChatEventReader FrontlineChatEventReader { get; init; }
    public CombatEventService CombatEventService { get; init; }
    public FrontlineKeySkillEventReader FrontlineKeySkillEventReader { get; init; }
    public WorldStateService WorldStateService { get; init; }
    public MapAnnotationService MapAnnotationService { get; init; }
    public MapTacticalGraphService MapTacticalGraphService { get; init; }
    public MapTacticalAnalysisService MapTacticalAnalysisService { get; init; }
    public TacticalDecisionEngineService TacticalDecisionEngineService { get; init; }
    public LlmStrategicDecisionService LlmStrategicDecisionService { get; init; }
    public StrategicArbitrationService StrategicArbitrationService { get; init; }
    public AiTeacherLearningService AiTeacherLearningService { get; init; }
    public BattlefieldReplayRecorder BattlefieldReplayRecorder { get; init; }
    public CommandOverlayService CommandOverlayService { get; init; }

    public StatusEffectTracker StatusEffectTracker { get; init; }
    public ISigScanner SigScanner { get; init; }
    public ITextureProvider TextureProvider { get; init; }
    public IDataManager DataManager { get; init; }
    private bool disposed;
    private bool? combatCaptureEnabled;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        IGameGui gameGui,
        IPartyList partyList,
        IPluginLog log,
        IChatGui chatGui,
        ICondition condition,
        IDutyState dutyState,
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        ITextureProvider textureProvider,
        IDataManager dataManager)
    {
        PluginInterface = pluginInterface;
        CommandManager = commandManager;
        ClientState = clientState;
        ObjectTable = objectTable;
        Framework = framework;
        GameGui = gameGui;
        PartyList = partyList;
        Log = log;
        ChatGui = chatGui;
        Condition = condition;
        DutyState = dutyState;
        SigScanner = sigScanner;
        TextureProvider = textureProvider;
        DataManager = dataManager;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);
        LimitBreakService = new LimitBreakService(Configuration, clientState, gameGui, log);
        FrontlineAnnouncementReader = new FrontlineAnnouncementReader(gameGui, log);
        FrontlineChatEventReader = new FrontlineChatEventReader(chatGui, clientState, log);
        CombatEventService = new CombatEventService(sigScanner, gameInteropProvider, objectTable, dataManager, log);
        FrontlineKeySkillEventReader = new FrontlineKeySkillEventReader(chatGui, clientState, objectTable, CombatEventService, log);
        FrontlineScoreReader = new FrontlineScoreReader(Configuration, framework, log, clientState, gameGui, dataManager, dutyState);
        AreaMapProjectionService = new AreaMapProjectionService(Configuration, gameGui, clientState, objectTable, dataManager, log);
        MapAnnotationService = new MapAnnotationService();
        var pluginAssemblyDirectory = PluginInterface.AssemblyLocation.Directory?.FullName ?? AppContext.BaseDirectory;
        MapTacticalGraphService = new MapTacticalGraphService(dataManager, log, pluginAssemblyDirectory);
        MapTacticalAnalysisService = new MapTacticalAnalysisService(MapAnnotationService, MapTacticalGraphService);
        TacticalDecisionEngineService = new TacticalDecisionEngineService();
        LlmStrategicDecisionService = new LlmStrategicDecisionService(Configuration, log);
        AiTeacherLearningService = new AiTeacherLearningService(
            () => Configuration.Performance?.EnableDecisionQualityFeedback == true,
            () => BattlefieldReplayStoragePath.ResolveDirectory(Configuration.Replay.DirectoryName));
        StrategicArbitrationService = new StrategicArbitrationService();
        BattlefieldReplayRecorder = new BattlefieldReplayRecorder(Configuration, log);
        CommandOverlayService = new CommandOverlayService(Configuration, PluginInterface.UiBuilder);
        StatusEffectTracker = new StatusEffectTracker(Configuration, objectTable, clientState, framework, log);
        WorldStateService = new WorldStateService(Configuration, clientState, objectTable, framework, condition, log, FrontlineScoreReader, AreaMapProjectionService, LimitBreakService, FrontlineAnnouncementReader, FrontlineChatEventReader, FrontlineKeySkillEventReader, StatusEffectTracker, MapTacticalAnalysisService, TacticalDecisionEngineService, LlmStrategicDecisionService, StrategicArbitrationService, AiTeacherLearningService, BattlefieldReplayRecorder);
        FrontlineRadar = new FrontlineRadar(this, gameGui, textureProvider, dataManager, log, AreaMapProjectionService);
        FloatingToggle = new FloatingToggle(this);
        MainWindow = new MainWindow(this);
        WindowSystem.AddWindow(MainWindow);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;
        PluginInterface.UiBuilder.OpenMainUi += DrawMainUI;

        CommandManager.AddHandler("/man", new CommandInfo(OnCommand)
        {
            HelpMessage = "打开前线战术指挥主窗口"
        });
        CommandManager.AddHandler("/manradar", new CommandInfo(OnRadarCommand)
        {
            HelpMessage = "开关前线雷达显示"
        });
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        PluginInterface.UiBuilder.Draw -= DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;
        PluginInterface.UiBuilder.OpenMainUi -= DrawMainUI;
        CommandManager.RemoveHandler("/man");
        CommandManager.RemoveHandler("/manradar");
        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        WorldStateService.Dispose();
        AiTeacherLearningService.Dispose();
        FrontlineScoreReader.Dispose();
        FrontlineRadar.Dispose();
        LimitBreakService.Dispose();
        FrontlineAnnouncementReader.Dispose();
        FrontlineChatEventReader.Dispose();
        FrontlineKeySkillEventReader.Dispose();
        CombatEventService.Dispose();
        BattlefieldReplayRecorder.Dispose();
        LlmStrategicDecisionService.Dispose();
        CommandOverlayService.Dispose();
        AreaMapProjectionService.Dispose();
        StatusEffectTracker.Dispose();
    }

    private void DrawUI()
    {
        var snapshot = WorldStateService.GetSnapshot();
        if (combatCaptureEnabled != snapshot.IsInFrontline)
        {
            CombatEventService.SetCaptureEnabled(snapshot.IsInFrontline);
            combatCaptureEnabled = snapshot.IsInFrontline;
        }

        FloatingToggle.Draw();
        WindowSystem.Draw();
        if (ShouldDrawRadarUi(snapshot))
            DrawRadarUi();
        if (Configuration.LimitBreak.ShowLimitBreakUI)
            LimitBreakService.DrawOverlay(snapshot.IsInFrontline);
        if (Configuration.CommandOverlay.Enabled)
            CommandOverlayService.DrawOverlay(snapshot);
    }

    private bool ShouldDrawRadarUi(BattlefieldSnapshot snapshot)
    {
        var radar = Configuration.Radar;
        if (!radar.Enabled)
            return false;
        if (!snapshot.IsInFrontline && !radar.OutsideFrontline)
            return false;

        return radar.ScreenRadar || radar.MapRadar || radar.FieldMarkers;
    }

    private void DrawRadarUi()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.Pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(1f, 1f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        var flags = ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoScrollbar
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoFocusOnAppearing
            | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoInputs
            | ImGuiWindowFlags.NoBackground;

        if (ImGui.Begin("##ai02RadarDrawContext", flags))
        {
            FrontlineRadar.DrawWorld();
            FrontlineRadar.DrawPostUi();
        }

        ImGui.End();
    }

    public void DrawConfigUI()
    {
        MainWindow.IsOpen = true;
    }

    public void DrawMainUI()
    {
        MainWindow.IsOpen = true;
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.IsOpen = true;
    }

    private void OnRadarCommand(string command, string args)
    {
        Configuration.Radar.Enabled = !Configuration.Radar.Enabled;
        Configuration.Save();
        ChatGui.Print($"前线雷达已{(Configuration.Radar.Enabled ? "启用" : "关闭")}");
    }
}
