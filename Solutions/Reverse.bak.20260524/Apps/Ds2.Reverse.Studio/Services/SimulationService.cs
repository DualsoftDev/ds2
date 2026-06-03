using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Reverse.Studio.Models;

namespace Ds2.Reverse.Studio.Services;

/// <summary>
/// 생성된 모델의 ground truth arrows 따라 events 시뮬레이션.
/// Topological order 로 각 call 의 fire time 결정.
/// </summary>
public static class SimulationService
{
    public static List<CapturedEventRow> Simulate(
        GeneratedModel model, int nCycles, long cycleMs, int seed,
        int stageLagMs, int jitterMs)
    {
        var rng = new Random(seed);

        // 1. Call id → name
        var idToName = model.CallsByName.ToDictionary(t => t.id, t => t.name);
        var nameToId = model.CallsByName.ToDictionary(t => t.name, t => t.id);

        // 2. Arrow graph: src → [tgt]  + arrow type for group detection
        var adj = new Dictionary<Guid, List<(Guid tgt, bool isGroup)>>();

        // Helper: per-work calls (callId -> workId)
        var callToWork = new Dictionary<Guid, Guid>();
        foreach (var (_, callId) in model.CallsByName)
            if (model.Store.Calls.TryGetValue(callId, out var c))
                callToWork[callId] = c.ParentId;
        var callsByWork = new Dictionary<Guid, List<Guid>>();
        foreach (var (cid, wid) in callToWork)
        {
            if (!callsByWork.TryGetValue(wid, out var lst))
            { lst = new List<Guid>(); callsByWork[wid] = lst; }
            lst.Add(cid);
        }

        // intra-work call arrows + collect work-level (cross-work) arrows separately
        var workLevelArrows = new List<(Guid srcW, Guid tgtW)>();
        Guid LookupWork(string r)
        {
            // "{flowName}.{localName}" 형식 매칭
            var dot = r.IndexOf('.');
            if (dot <= 0) return Guid.Empty;
            var flowPrefix = r.Substring(0, dot);
            var localName = r.Substring(dot + 1);
            foreach (var kv in model.Store.Works)
                if (kv.Value.FlowPrefix == flowPrefix && kv.Value.LocalName == localName)
                    return kv.Key;
            return Guid.Empty;
        }
        void AddEdge(Guid s, Guid t, bool isGroup)
        {
            if (!adj.TryGetValue(s, out var lst))
            { lst = new List<(Guid, bool)>(); adj[s] = lst; }
            lst.Add((t, isGroup));
        }
        foreach (var (src, tgt, kind) in model.GroundTruth)
        {
            if (nameToId.TryGetValue(src, out var sId) && nameToId.TryGetValue(tgt, out var tId))
            {
                AddEdge(sId, tId, kind == ArrowKindStd.Group);
            }
            else
            {
                // Work-level reference (e.g., "F1.W2") — collect for later expansion
                var sW = LookupWork(src);
                var tW = LookupWork(tgt);
                if (sW != Guid.Empty && tW != Guid.Empty) workLevelArrows.Add((sW, tW));
            }
        }

        // 2b. Work-level arrows 를 call-level 로 확장:
        //   src work 의 exit call (no outgoing) → tgt work 의 entry call (no incoming)
        Guid EntryCallOf(Guid workId)
        {
            if (!callsByWork.TryGetValue(workId, out var calls)) return Guid.Empty;
            var hasIncoming = new HashSet<Guid>();
            foreach (var c in calls)
                if (adj.ContainsKey(c))
                    foreach (var (t, _) in adj[c])
                        if (callToWork.GetValueOrDefault(t) == workId) hasIncoming.Add(t);
            return calls.FirstOrDefault(c => !hasIncoming.Contains(c));
        }
        Guid ExitCallOf(Guid workId)
        {
            if (!callsByWork.TryGetValue(workId, out var calls)) return Guid.Empty;
            // 마지막 call: outgoing 이 없거나 자기 work 밖만 가리킴
            return calls.LastOrDefault(c =>
                !adj.TryGetValue(c, out var lst) ||
                lst.All(e => callToWork.GetValueOrDefault(e.tgt) != workId));
        }
        foreach (var (sW, tW) in workLevelArrows)
        {
            var exitCall = ExitCallOf(sW);
            var entryCall = EntryCallOf(tW);
            if (exitCall != Guid.Empty && entryCall != Guid.Empty)
                AddEdge(exitCall, entryCall, false);
        }

        // 3. Topological sort
        var indeg = new Dictionary<Guid, int>();
        foreach (var (_, id) in model.CallsByName) indeg[id] = 0;
        foreach (var kv in adj)
            foreach (var (t, _) in kv.Value)
                indeg[t] = indeg.GetValueOrDefault(t, 0) + 1;

        var topo = new List<Guid>();
        var queue = new Queue<Guid>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            topo.Add(n);
            if (adj.TryGetValue(n, out var succs))
            {
                foreach (var (t, _) in succs)
                {
                    indeg[t]--;
                    if (indeg[t] == 0) queue.Enqueue(t);
                }
            }
        }

        // Device duration helper
        long DurationFor(string callName)
        {
            var dot = callName.IndexOf('.');
            var dev = dot > 0 ? callName.Substring(0, dot) : callName;
            if (model.DeviceDurations.TryGetValue(dev, out var d)) return d;
            return 100L;   // default
        }

        // 4. 각 call 의 cycle-relative fire time + duration
        var events = new List<CapturedEventRow>();
        for (int c = 0; c < nCycles; c++)
        {
            long t0 = (long)c * cycleMs;
            var fireT = new Dictionary<Guid, long>();
            foreach (var nodeId in topo)
            {
                long t = 0L;
                long parentMaxEnd = -1;
                bool isGroupChild = false;
                foreach (var kv in adj)
                {
                    foreach (var (tgtId, isGroup) in kv.Value)
                    {
                        if (tgtId == nodeId)
                        {
                            if (fireT.TryGetValue(kv.Key, out var pStart))
                            {
                                long pEnd = pStart + DurationFor(idToName[kv.Key]);
                                if (isGroup)
                                {
                                    parentMaxEnd = Math.Max(parentMaxEnd, pStart);
                                    isGroupChild = true;
                                }
                                else
                                {
                                    parentMaxEnd = Math.Max(parentMaxEnd, pEnd);
                                }
                            }
                        }
                    }
                }
                int jit = rng.Next(-jitterMs, jitterMs + 1);
                if (parentMaxEnd < 0)
                {
                    t = jit;
                }
                else if (isGroupChild)
                {
                    t = parentMaxEnd + rng.Next(0, 10);
                }
                else
                {
                    t = parentMaxEnd + jit;
                }
                if (t < 0) t = 0;
                fireT[nodeId] = t;
                long duration = DurationFor(idToName[nodeId])
                    + rng.Next(-jitterMs / 2, jitterMs / 2 + 1);
                if (duration < 10) duration = 10;
                events.Add(new CapturedEventRow(t0 + t, t0 + t + duration, idToName[nodeId]));
            }
        }
        events.Sort((a, b) => a.T.CompareTo(b.T));
        return events;
    }
}
