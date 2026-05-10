using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace ai02;

public sealed class CombatActionEffectHook : IDisposable
{
    private const string ReceiveAbilitySignature = "E8 ?? ?? ?? ?? 48 8B 8D ?? ?? ?? ?? 48 33 CC E8 ?? ?? ?? ?? 48 81 C4 00 05 00 00";

    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private Hook<ReceiveAbilityDelegate>? hook;

    private delegate void ReceiveAbilityDelegate(
        uint sourceId,
        IntPtr sourceChara,
        IntPtr pos,
        IntPtr effectHeader,
        IntPtr effectArray,
        IntPtr effectTrail);

    public event Action<uint, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>? OnRaw;

    public CombatActionEffectHook(
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
            log.Warning(ex, "[CombatEvent] 释放 ActionEffect Hook 失败");
        }
    }

    private void Initialize()
    {
        try
        {
            if (!sigScanner.TryScanText(ReceiveAbilitySignature, out var address))
            {
                log.Warning("[CombatEvent] 未找到 ActionEffect Hook 签名");
                return;
            }

            hook = gameInteropProvider.HookFromAddress<ReceiveAbilityDelegate>(address, Detour);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] 初始化 ActionEffect Hook 失败");
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
            log.Warning(ex, "[CombatEvent] 切换 ActionEffect Hook 失败");
        }
    }

    private void Detour(
        uint sourceId,
        IntPtr sourceChara,
        IntPtr pos,
        IntPtr effectHeader,
        IntPtr effectArray,
        IntPtr effectTrail)
    {
        try
        {
            OnRaw?.Invoke(sourceId, sourceChara, pos, effectHeader, effectArray, effectTrail);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[CombatEvent] ActionEffect Detour 处理失败");
        }

        hook!.Original(sourceId, sourceChara, pos, effectHeader, effectArray, effectTrail);
    }
}
