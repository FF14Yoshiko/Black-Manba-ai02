using System;
using System.Collections.Generic;
using System.Linq;

namespace ai02;

internal static class DecisionQualityFeedbackFusion
{
    private const float TeacherWeightMultiplier = 1.35f;

    public static BattlefieldCommandEffectivenessSnapshot[] Merge(
        IReadOnlyList<BattlefieldCommandEffectivenessSnapshot> replayFeedback,
        IReadOnlyList<BattlefieldCommandEffectivenessSnapshot> teacherFeedback)
    {
        if (replayFeedback.Count == 0)
            return teacherFeedback.ToArray();
        if (teacherFeedback.Count == 0)
            return replayFeedback.ToArray();

        var replayByKind = replayFeedback.ToDictionary(item => item.Kind);
        var teacherByKind = teacherFeedback.ToDictionary(item => item.Kind);
        var kinds = replayByKind.Keys
            .Concat(teacherByKind.Keys)
            .Distinct()
            .OrderBy(kind => kind)
            .ToArray();
        var merged = new List<BattlefieldCommandEffectivenessSnapshot>(kinds.Length);
        foreach (var kind in kinds)
        {
            var hasReplay = replayByKind.TryGetValue(kind, out var replay);
            var hasTeacher = teacherByKind.TryGetValue(kind, out var teacher);
            if (hasReplay && hasTeacher)
            {
                var replayWeight = Math.Max(1f, replay.SampleCount);
                var teacherWeight = Math.Max(1f, teacher.SampleCount) * TeacherWeightMultiplier;
                var totalWeight = replayWeight + teacherWeight;
                var average = (replay.AverageScore * replayWeight + teacher.AverageScore * teacherWeight) / totalWeight;
                var positiveRate = (replay.PositiveRate * replayWeight + teacher.PositiveRate * teacherWeight) / totalWeight;
                var modifier = Math.Clamp((replay.Modifier * replayWeight + teacher.Modifier * teacherWeight) / totalWeight, -8f, 8f);
                var sampleCount = replay.SampleCount + teacher.SampleCount;
                var summary = $"AI老师+回放: {teacher.Kind} 样本 {sampleCount}, 调整 {modifier:+0.0;-0.0;0}";
                merged.Add(new BattlefieldCommandEffectivenessSnapshot(
                    kind,
                    sampleCount,
                    average,
                    positiveRate,
                    modifier,
                    summary));
                continue;
            }

            merged.Add(hasTeacher ? teacher : replay);
        }

        return merged
            .OrderByDescending(item => item.SampleCount)
            .ThenBy(item => item.Kind)
            .ToArray();
    }
}
