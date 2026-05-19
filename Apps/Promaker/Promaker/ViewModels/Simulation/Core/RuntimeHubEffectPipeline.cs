using System.Collections.Generic;
using System.Linq;
using Ds2.Runtime.Engine.Passive;

namespace Promaker.ViewModels;

internal readonly record struct RuntimeHubEffectBatch(
    int DelayMs,
    bool AwaitWrites,
    bool RequiresExclusiveImmediateLane,
    IReadOnlyList<RuntimeHubEffect> Effects);

internal static class RuntimeHubEffectPipeline
{
    internal static IReadOnlyList<RuntimeHubEffectBatch> Build(IEnumerable<RuntimeHubEffect> effects)
    {
        var orderedEffects = effects
            .OrderBy(effect => effect.DelayMs)
            .ToArray();

        if (orderedEffects.Length == 0)
            return [];

        var batches = new List<RuntimeHubEffectBatch>();

        var immediateEffects = orderedEffects
            .Where(static effect => effect.DelayMs <= 0)
            .ToArray();
        if (immediateEffects.Length > 0)
            batches.Add(new RuntimeHubEffectBatch(0, false, true, immediateEffects));

        foreach (var delayedGroup in orderedEffects
                     .Where(static effect => effect.DelayMs > 0)
                     .GroupBy(static effect => effect.DelayMs))
        {
            batches.Add(new RuntimeHubEffectBatch(
                delayedGroup.Key,
                true,
                false,
                delayedGroup.ToArray()));
        }

        return batches;
    }
}
