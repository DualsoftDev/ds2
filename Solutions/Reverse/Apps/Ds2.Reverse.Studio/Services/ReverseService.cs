using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Reverse.Core;
using Ds2.Reverse.Studio.Models;
using Microsoft.FSharp.Collections;
using Microsoft.FSharp.Core;

namespace Ds2.Reverse.Studio.Services;

public static class ReverseService
{
    public static ReverseResult Run(
        GeneratedModel model,
        IReadOnlyList<CapturedEventRow> events,
        long cycleMs,
        bool autoTune = false)
    {
        // 1. ArrowCandidates 는 ground truth + (optional) noise
        var candidates = model.GroundTruth
            .Where(a => a.Kind != ArrowKindStd.StartReset)   // work-level 은 cross-flow
            .Select(a =>
            {
                string kind = a.Kind switch
                {
                    ArrowKindStd.Group => "group",
                    ArrowKindStd.Reset => "reset",
                    ArrowKindStd.StartReset => "trigger_reset",
                    ArrowKindStd.ResetReset => "mutex",
                    _ => "trigger"
                };
                return new ArrowCandidate(a.Src, a.Tgt, kind);
            })
            .ToList();

        var crossFlow = model.GroundTruth
            .Where(a => a.Kind == ArrowKindStd.StartReset)
            .Select(a => new ArrowCandidate(a.Src, a.Tgt, "trigger_reset"))
            .ToList();

        // 2. FlowCalls: 각 flow → calls list
        // model.Store 의 flows + calls
        var flowCalls = new Dictionary<string, IEnumerable<Tuple<string, string>>>();
        foreach (var fkv in model.Store.Flows)
        {
            var flowName = fkv.Value.Name;
            var calls = new List<Tuple<string, string>>();
            foreach (var ckv in model.Store.Calls)
            {
                var call = ckv.Value;
                // call.parentId → work → work.parentId == flowId
                if (model.Store.Works.TryGetValue(call.ParentId, out var work)
                    && work.ParentId == fkv.Key)
                {
                    calls.Add(Tuple.Create(call.Name, ""));
                }
            }
            if (calls.Count > 0)
                flowCalls[flowName] = calls;
        }

        // F# Map<string, FSharpList<Tuple<string,string>>>
        var fcMap = MapModule.OfSeq(
            flowCalls.Select(kv =>
                Tuple.Create(kv.Key, ListModule.OfSeq(kv.Value))));

        // 3. ReverseEngine.Input — mkInput 후 추가 필드
        var candList = ListModule.OfSeq(candidates);
        // ReverseEngine 은 (T, Name) 만 사용 — EndT 무시
        var evList = ListModule.OfSeq(events.Select(e =>
            new CapturedEvent(e.T, e.Name)));
        var cfg = CausationConfigModule.withCycleHint(cycleMs, CausationConfigModule.defaults);

        var baseInput = ReverseEngine.mkInput(
            "GeneratedModel", "Main", fcMap, candList, evList, cfg);
        var crossList = ListModule.OfSeq(crossFlow);
        // immutable record copy with CrossFlowCandidates
        var input = new ReverseEngine.Input(
            baseInput.ProjectName,
            baseInput.ActiveSystemName,
            baseInput.FlowCalls,
            baseInput.Candidates,
            baseInput.Events,
            baseInput.Config,
            baseInput.LogicRungs,
            baseInput.LogicMaxDepth,
            baseInput.LogicStrengthThreshold,
            crossList,
            baseInput.WorkAssignments,
            autoTune);

        // 4. Run
        var result = ReverseEngine.run(input);
        var detectedStore = result.Item1;
        var report = result.Item2;

        // Confidence lookup: (src, tgt) → (score, tier)
        var confByPair = new Dictionary<(string, string), (double score, string tier)>();
        foreach (var tup in report.EmittedConfidence)
        {
            var src = tup.Item1; var tgt = tup.Item2; var c = tup.Item3;
            string tierStr = c.Tier.IsHigh ? "High"
                          : c.Tier.IsMedium ? "Medium"
                          : c.Tier.IsLow ? "Low" : "Reject";
            confByPair[(src, tgt)] = (c.Score, tierStr);
        }

        // 5. Compare
        var truthSet = model.GroundTruth
            .Where(a => a.Kind != ArrowKindStd.StartReset)
            .Select(a => (a.Src, a.Tgt, a.Kind == ArrowKindStd.Group))
            .ToHashSet();

        var detectedArrows = detectedStore.ArrowCalls.Values
            .Select(a =>
            {
                var sName = detectedStore.Calls.TryGetValue(a.SourceId, out var s) ? s.Name : "?";
                var tName = detectedStore.Calls.TryGetValue(a.TargetId, out var t) ? t.Name : "?";
                bool isGroup = a.ArrowType == ArrowType.Group;
                return (Src: sName, Tgt: tName, IsGroup: isGroup);
            })
            .ToList();
        var detectedSet = detectedArrows.ToHashSet();

        int tp = 0, fp = 0, fn = 0;
        var diffs = new List<ArrowDiff>();

        foreach (var (src, tgt, isGrp) in detectedSet)
        {
            string typeStr = isGrp ? "Group" : "Start";
            confByPair.TryGetValue((src, tgt), out var c);
            double? conf = confByPair.ContainsKey((src, tgt)) ? c.score : (double?)null;
            string? tier = confByPair.ContainsKey((src, tgt)) ? c.tier : null;
            if (truthSet.Contains((src, tgt, isGrp)))
            { tp++; diffs.Add(new ArrowDiff(src, tgt, typeStr, "✓ TP", null, conf, tier)); }
            else
            { fp++; diffs.Add(new ArrowDiff(src, tgt, typeStr, "✗ FP",
                                            "in detected, not in truth", conf, tier)); }
        }
        foreach (var (src, tgt, isGrp) in truthSet)
        {
            if (!detectedSet.Contains((src, tgt, isGrp)))
            {
                string typeStr = isGrp ? "Group" : "Start";
                fn++;
                diffs.Add(new ArrowDiff(src, tgt, typeStr, "– FN", "missed"));
            }
        }

        double p = (tp + fp) == 0 ? 1.0 : (double)tp / (tp + fp);
        double r = (tp + fn) == 0 ? 1.0 : (double)tp / (tp + fn);
        double f1 = (p + r) == 0 ? 0.0 : 2 * p * r / (p + r);

        var anomalousList = report.AnomalousCycles
            .Select(t => (CycleIdx: t.Item1, Score: t.Item2))
            .ToList();

        var metrics = new DetectionMetrics(
            truthSet.Count, detectedSet.Count, tp, fp, fn, p, r, f1, diffs,
            NoiseLevel: report.NoiseLevel,
            AnomalousCyclesCount: report.AnomalousCycles.Count,
            AnomalousCycles: anomalousList);

        return new ReverseResult(detectedStore, metrics);
    }
}
