using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class CombatStartCastHook : IDisposable
{
    private const string StartCastSignature = "40 53 57 48 81 EC ?? ?? ?? ?? 48 8B FA 8B D1";

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private Hook<StartCastDelegate>? hook;

    private delegate void StartCastDelegate(uint sourceId, IntPtr ptr);

    public event Action<uint, IntPtr>? OnRaw;

    public CombatStartCastHook(
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        IPluginLog log)
    {
        this.sigScanner = sigScanner;
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;
        Initialize();
    }

    public void Dispose()
    {
        try
        {
            hook?.Disable();
            hook?.Dispose();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] 释放 StartCast Hook 失败");
        }
    }

    private void Initialize()
    {
        try
        {
            if (!sigScanner.TryScanText(StartCastSignature, out var address))
            {
                log.Warning("[CombatEvent] 未找到 StartCast Hook 签名");
                return;
            }

            hook = gameInteropProvider.HookFromAddress<StartCastDelegate>(address, Detour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] 初始化 StartCast Hook 失败");
        }
    }

    public void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
                hook?.Enable();
            else
                hook?.Disable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] 切换 StartCast Hook 失败");
        }
    }

    private void Detour(uint sourceId, IntPtr ptr)
    {
        try
        {
            OnRaw?.Invoke(sourceId, ptr);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] StartCast Detour 处理失败");
        }

        hook!.Original(sourceId, ptr);
    }
}
