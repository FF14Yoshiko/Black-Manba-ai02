using System;

namespace ai02;

public enum PlayerStatusChangeKind
{
    Gained,
    Lost,
}

public enum PlayerTargetChangeKind
{
    Target,
    CastTarget,
}

public readonly record struct ObservedPlayerSnapshot(
    ulong GameObjectId,
    uint EntityId,
    ulong ContentId,
    string Name,
    uint ClassJobId,
    string JobName,
    byte Battalion);

public readonly record struct ObservedStatusSnapshot(
    uint StatusId,
    string StatusName,
    ushort Param,
    float RemainingTime,
    uint SourceId,
    string SourceName);

public readonly record struct PlayerStatusChangedEvent(
    long ObservedAtTicks,
    PlayerStatusChangeKind Kind,
    ObservedPlayerSnapshot Player,
    ObservedStatusSnapshot Status,
    string SourceText);

public readonly record struct PlayerDeathStateChangedEvent(
    long ObservedAtTicks,
    ObservedPlayerSnapshot Player,
    bool IsDead,
    string SourceText);

public readonly record struct PlayerTargetChangedEvent(
    long ObservedAtTicks,
    PlayerTargetChangeKind Kind,
    ObservedPlayerSnapshot Player,
    ulong PreviousTargetObjectId,
    string PreviousTargetName,
    ulong CurrentTargetObjectId,
    string CurrentTargetName,
    string SourceText);
