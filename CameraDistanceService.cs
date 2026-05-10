using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace ai02;

public sealed unsafe class CameraDistanceService : IDisposable
{
    private const string SetActiveCameraSignature = "40 57 41 54 41 57 48 83 EC ?? 4C 63 FA";
    private const string CameraCurrentSightDistanceSignature = "40 53 48 83 EC ?? 48 8B 15 ?? ?? ?? ?? 48 8B D9 0F 29 74 24";
    private const string CameraCollisionSignature = "84 C0 0F 84 ?? ?? ?? ?? F3 0F 10 44 24 ?? 41 B7";

    private static readonly byte[] CameraCollisionPatchBytes =
    {
        0x90, 0x90, 0xE9, 0xA7, 0x01, 0x00, 0x00, 0x90,
    };

    private readonly Configuration configuration;
    private readonly ISigScanner sigScanner;
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;

    private Hook<SetActiveCameraDelegate>? setActiveCameraHook;
    private Hook<CameraCurrentSightDistanceDelegate>? cameraCurrentSightDistanceHook;
    private NativeMemoryPatch? cameraCollisionPatch;
    private string initializationFailure = string.Empty;
    private bool applied;
    private bool disposed;

    private unsafe delegate void SetActiveCameraDelegate(CameraManager* manager, int cameraIndex, void* a3);
    private delegate float CameraCurrentSightDistanceDelegate(nint a1, float minValue, float maxValue, float upperBound, float lowerBound, int mode, float currentValue, float targetValue);

    public CameraDistanceService(
        Configuration configuration,
        ISigScanner sigScanner,
        IGameInteropProvider gameInteropProvider,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.sigScanner = sigScanner;
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;

        Initialize();
        SyncFromConfiguration();
    }

    public bool IsReady => setActiveCameraHook != null && cameraCurrentSightDistanceHook != null;

    public string StatusText
        => IsReady
            ? "无限视野距离已启用；参数变更会实时应用。"
            : string.IsNullOrWhiteSpace(initializationFailure)
                ? "无限视野距离未启用：当前客户端签名未匹配，插件会保持正常加载。"
                : $"无限视野距离未启用：{initializationFailure}";

    public void SyncFromConfiguration()
    {
        if (disposed)
            return;

        configuration.SightDistance.Normalize();
        if (!configuration.SightDistance.Enabled || !IsReady)
        {
            DisableAndRestore();
            return;
        }

        try
        {
            setActiveCameraHook?.Enable();
            cameraCurrentSightDistanceHook?.Enable();

            if (configuration.SightDistance.IgnoreCollision)
                cameraCollisionPatch?.Enable();
            else
                cameraCollisionPatch?.Disable();

            UpdateActiveCamera(configuration.SightDistance);
            applied = true;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to apply sight distance customization.");
            DisableAndRestore();
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;
        DisableAndRestore();
        cameraCollisionPatch?.Dispose();
        cameraCollisionPatch = null;
        setActiveCameraHook?.Dispose();
        setActiveCameraHook = null;
        cameraCurrentSightDistanceHook?.Dispose();
        cameraCurrentSightDistanceHook = null;
    }

    private void Initialize()
    {
        try
        {
            if (TryScan(SetActiveCameraSignature, "SetActiveCamera", out var setActiveCameraAddress))
                setActiveCameraHook = gameInteropProvider.HookFromAddress<SetActiveCameraDelegate>(setActiveCameraAddress, SetActiveCameraDetour);

            if (TryScan(CameraCurrentSightDistanceSignature, "CameraCurrentSightDistance", out var sightDistanceAddress))
                cameraCurrentSightDistanceHook = gameInteropProvider.HookFromAddress<CameraCurrentSightDistanceDelegate>(sightDistanceAddress, CameraCurrentSightDistanceDetour);

            if (TryScan(CameraCollisionSignature, "CameraCollision", out var collisionAddress))
                cameraCollisionPatch = new NativeMemoryPatch(collisionAddress, CameraCollisionPatchBytes);
        }
        catch (Exception ex)
        {
            initializationFailure = "初始化 hook 失败，已自动降级。";
            log.Error(ex, "Failed to initialize sight distance customization.");
        }
    }

    private bool TryScan(string signature, string name, out IntPtr address)
    {
        try
        {
            if (sigScanner.TryScanText(signature, out address))
            {
                if (!IsSafeExecutableAddress(address, out var reason))
                {
                    initializationFailure = $"签名命中无效地址，已跳过 hook。";
                    log.Warning("Sight distance signature hit invalid address for {Name}: 0x{Address:X}, reason: {Reason}", name, address.ToInt64(), reason);
                    address = IntPtr.Zero;
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            initializationFailure = "扫描签名失败，已自动降级。";
            log.Warning(ex, "Failed to scan {Name} signature.", name);
        }

        address = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(initializationFailure))
            initializationFailure = "当前客户端签名未匹配。";
        log.Warning("Sight distance signature not found: {Name}", name);
        return false;
    }

    private static bool IsSafeExecutableAddress(IntPtr address, out string reason)
    {
        if (address == IntPtr.Zero)
        {
            reason = "空地址";
            return false;
        }

        if (!IsInsideMainModule(address, out reason))
            return false;

        if (VirtualQuery(address, out var memoryInfo, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
        {
            reason = $"VirtualQuery 失败: {Marshal.GetLastWin32Error()}";
            return false;
        }

        if (memoryInfo.State != MemoryCommit)
        {
            reason = $"内存未提交: 0x{memoryInfo.State:X}";
            return false;
        }

        if ((memoryInfo.Protect & PageGuard) != 0 || (memoryInfo.Protect & PageNoAccess) != 0)
        {
            reason = $"内存不可读: 0x{memoryInfo.Protect:X}";
            return false;
        }

        if (!IsExecutableProtection(memoryInfo.Protect))
        {
            reason = $"内存页不可执行: 0x{memoryInfo.Protect:X}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsInsideMainModule(IntPtr address, out string reason)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var module = process.MainModule;
            if (module == null)
            {
                reason = "无法获取主模块";
                return false;
            }

            var value = address.ToInt64();
            var start = module.BaseAddress.ToInt64();
            var end = start + module.ModuleMemorySize;
            if (value >= start && value < end)
            {
                reason = string.Empty;
                return true;
            }

            reason = $"不在主模块范围内: 0x{start:X}-0x{end:X}";
            return false;
        }
        catch (Exception ex)
        {
            reason = $"检查主模块失败: {ex.Message}";
            return false;
        }
    }

    private static bool IsExecutableProtection(uint protect)
        => (protect & PageExecuteRead) != 0
            || (protect & PageExecuteReadWrite) != 0
            || (protect & PageExecuteWriteCopy) != 0;

    private void DisableAndRestore()
    {
        try
        {
            cameraCollisionPatch?.Disable();
            setActiveCameraHook?.Disable();
            cameraCurrentSightDistanceHook?.Disable();

            if (applied)
                UpdateActiveCameraToOriginal();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to restore original sight distance settings.");
        }
        finally
        {
            applied = false;
        }
    }

    private void SetActiveCameraDetour(CameraManager* manager, int cameraIndex, void* a3)
    {
        setActiveCameraHook!.Original(manager, cameraIndex, a3);
        if (!configuration.SightDistance.Enabled)
            return;

        UpdateCamera(manager->GetActiveCamera(), configuration.SightDistance);
    }

    private float CameraCurrentSightDistanceDetour(nint a1, float minValue, float maxValue, float upperBound, float lowerBound, int mode, float currentValue, float targetValue)
    {
        var framework = Framework.Instance();
        var highTarget = Math.Min(upperBound - 0.001f, maxValue);
        var lowTarget = Math.Min(lowerBound - 0.001f, maxValue);
        var next = mode switch
        {
            1 => Math.Min(highTarget, Interpolate(lowTarget, 0.3f)),
            2 => Interpolate(highTarget, 0.3f),
            3 => highTarget,
            0 or 4 or 5 => Interpolate(highTarget, 0.07f),
            _ => currentValue,
        };

        return Math.Max(Math.Min(targetValue, next), configuration.SightDistance.MinDistance);

        float Interpolate(float target, float multiplier)
        {
            if (Math.Abs(target - currentValue) < 0.001f)
                return target;

            var frameScale = framework == null ? 1f : framework->FrameDeltaTime * 60f;
            var t = Math.Min(frameScale * multiplier, 1f);
            if (currentValue < target && target > targetValue)
                return Math.Min(currentValue + t * (target - currentValue), targetValue);

            return currentValue + t * (target - currentValue);
        }
    }

    private static void UpdateActiveCamera(SightDistanceConfiguration config)
    {
        var manager = CameraManager.Instance();
        if (manager == null)
            return;

        UpdateCamera(manager->GetActiveCamera(), config);
    }

    private static void UpdateActiveCameraToOriginal()
    {
        var manager = CameraManager.Instance();
        if (manager == null)
            return;

        UpdateCamera(manager->GetActiveCamera(), 20f, 1.5f, 0.785398f, -1.48353f, 0.78f, 0.69f, 0.78f);
    }

    private static void UpdateCamera(GameCamera* camera, SightDistanceConfiguration config)
        => UpdateCamera(camera, config.MaxDistance, config.MinDistance, config.MaxRotation, config.MinRotation, config.MaxFoV, config.MinFoV, config.FoV);

    private static void UpdateCamera(GameCamera* camera, float maxDistance, float minDistance, float maxRotation, float minRotation, float maxFoV, float minFoV, float fov)
    {
        if (camera == null)
            return;

        camera->MinDistance = minDistance;
        camera->MaxDistance = maxDistance;
        camera->DirVMin = minRotation;
        camera->DirVMax = maxRotation;
        camera->MinFoV = minFoV;
        camera->MaxFoV = maxFoV;
        camera->FoV = fov;
    }

    private sealed class NativeMemoryPatch : IDisposable
    {
        private const uint PageExecuteReadWrite = 0x40;

        private readonly IntPtr address;
        private readonly byte[] patchBytes;
        private readonly byte[] originalBytes;
        private bool enabled;

        public NativeMemoryPatch(IntPtr address, byte[] patchBytes)
        {
            this.address = address;
            this.patchBytes = patchBytes;
            originalBytes = new byte[patchBytes.Length];
            Marshal.Copy(address, originalBytes, 0, originalBytes.Length);
        }

        public void Enable()
        {
            if (enabled)
                return;

            Write(patchBytes);
            enabled = true;
        }

        public void Disable()
        {
            if (!enabled)
                return;

            Write(originalBytes);
            enabled = false;
        }

        public void Dispose()
        {
            Disable();
        }

        private void Write(byte[] bytes)
        {
            if (!VirtualProtect(address, (nuint)bytes.Length, PageExecuteReadWrite, out var oldProtect))
                throw new InvalidOperationException($"VirtualProtect failed: {Marshal.GetLastWin32Error()}");

            try
            {
                Marshal.Copy(bytes, 0, address, bytes.Length);
            }
            finally
            {
                VirtualProtect(address, (nuint)bytes.Length, oldProtect, out _);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);
    }

    private const uint MemoryCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageExecuteRead = 0x20;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQuery(
        IntPtr lpAddress,
        out MemoryBasicInformation lpBuffer,
        nuint dwLength);
}
