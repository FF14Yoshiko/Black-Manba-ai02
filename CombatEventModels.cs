using System;
using System.Numerics;

namespace ai02;

public enum CombatEventKind
{
    Unknown,
    StartCast,
    ActionEffect,
}

public readonly record struct CombatActionEffectItem(
    byte Type,
    ushort Value,
    byte Param1,
    byte Param2,
    byte Param3,
    byte Param4,
    byte Param5);

public readonly record struct CombatActionTargetSnapshot(
    ulong TargetObjectId,
    uint TargetEntityId,
    string TargetName,
    CombatActionEffectItem[] Effects);

public readonly record struct CombatActionEvent(
    long ObservedAtTicks,
    CombatEventKind Kind,
    uint SourceEntityId,
    ulong SourceGameObjectId,
    string SourceName,
    uint SourceClassJobId,
    string SourceJobName,
    uint ActionId,
    string ActionName,
    uint GlobalSequence,
    float AnimationLockTime,
    ushort ActionAnimationId,
    byte Variation,
    byte EffectDisplayType,
    float CastTime,
    bool CanInterrupt,
    Vector3 Position,
    ulong PrimaryTargetObjectId,
    uint PrimaryTargetEntityId,
    string PrimaryTargetName,
    CombatActionTargetSnapshot[] Targets,
    string SourceText,
    string EvidenceText);
