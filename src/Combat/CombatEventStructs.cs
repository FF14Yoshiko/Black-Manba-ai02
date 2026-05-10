using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace ai02;

[StructLayout(LayoutKind.Explicit)]
public unsafe struct CombatActorCast
{
    [FieldOffset(0)]
    public ushort ActionId;

    [FieldOffset(2)]
    public byte ActionKind;

    [FieldOffset(3)]
    public byte DisplayDelay;

    [FieldOffset(4)]
    public uint RealActionId;

    [FieldOffset(8)]
    public float CastTime;

    [FieldOffset(12)]
    public uint TargetId;

    [FieldOffset(16)]
    public ushort FacingRaw;

    [FieldOffset(18)]
    public byte CanInterrupt;

    [FieldOffset(24)]
    public PositionBuffer PositionData;

    public Vector3 Position
    {
        get
        {
            fixed (ushort* values = &PositionData.FixedElementField)
            {
                const float scale = 0.030518043041229247f;
                return new Vector3(
                    values[0] * scale - 1000f,
                    values[1] * scale - 1000f,
                    values[2] * scale - 1000f);
            }
        }
    }

    public float FacingRadians => (float)(FacingRaw * 9.587526218325454E-05 - Math.PI);

    [StructLayout(LayoutKind.Sequential, Size = 6)]
    public struct PositionBuffer
    {
        public ushort FixedElementField;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 8)]
public struct CombatEffectEntry
{
    [FieldOffset(0)]
    public byte Type;

    [FieldOffset(1)]
    public byte Param1;

    [FieldOffset(2)]
    public byte Param2;

    [FieldOffset(3)]
    public byte Param3;

    [FieldOffset(4)]
    public byte Param4;

    [FieldOffset(5)]
    public byte Param5;

    [FieldOffset(6)]
    public ushort Param0;
}

[StructLayout(LayoutKind.Sequential, Size = 2048)]
public unsafe struct CombatEffectsEntry
{
    public fixed ulong Entry[256];
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct CombatActionEffectHeader
{
    public uint AnimationTargetId;
    public uint Unknown0;
    public uint ActionId;
    public uint GlobalSequence;
    public float AnimationLockTime;
    public uint SomeTargetId;
    public ushort HiddenAnimation;
    public ushort Rotation;
    public ushort ActionAnimationId;
    public byte Variation;
    public byte EffectDisplayType;
    public byte Unknown20;
    public byte EffectCount;
    public ushort Padding21;
}

[StructLayout(LayoutKind.Sequential, Size = 256)]
public unsafe struct CombatTargetsEntry
{
    public fixed ulong Entry[32];
}
