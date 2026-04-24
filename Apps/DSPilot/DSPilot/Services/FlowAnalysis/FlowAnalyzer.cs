using Ds2.Core;
using Ds2.Core.Store;

namespace DSPilot.Services.FlowAnalysis;

public sealed class FlowAnalysisResult
{
    public string FlowName { get; init; } = string.Empty;
    public Guid FlowId { get; init; }
    public Guid? RepresentativeWorkId { get; init; }
    public string? RepresentativeWorkName { get; init; }
    public Call? HeadCall { get; init; }
    public Call? TailCall { get; init; }
    public int HeadCount { get; init; }
    public int TailCount { get; init; }
    public string? MovingStartName { get; init; }
    public string? MovingEndName { get; init; }
}

internal sealed record CallDagNode(Call Call, int InDegree, int OutDegree);

/// <summary>
/// Flow 분석 (대표 Work 선택, DAG 구성, Head/Tail 탐지).
/// F# FlowAnalysis 모듈의 pure C# 포팅.
/// </summary>
public static class FlowAnalyzer
{
    public static FlowAnalysisResult AnalyzeFlow(Flow flow, DsStore store)
    {
        var works = Queries.worksOf(flow.Id, store).ToList();

        var allCalls = works
            .SelectMany(w => Queries.callsOf(w.Id, store))
            .ToList();

        var allArrows = works
            .SelectMany(w => Queries.arrowCallsOf(w.Id, store))
            .ToList();

        var repWork = SelectRepresentativeWork(works, store);
        if (repWork is null)
        {
            return new FlowAnalysisResult
            {
                FlowName = flow.Name,
                FlowId = flow.Id,
            };
        }

        if (allCalls.Count == 0)
        {
            return new FlowAnalysisResult
            {
                FlowName = flow.Name,
                FlowId = flow.Id,
                RepresentativeWorkId = repWork.Id,
                RepresentativeWorkName = repWork.Name,
            };
        }

        var dag = BuildCallDag(allCalls, allArrows);
        var flattenedEdges = FlattenGroupArrows(allArrows);
        DetectCycle(dag, flattenedEdges);

        var (headCall, headCount) = FindHeadCall(dag);
        var (tailCall, tailCount) = FindTailCall(dag);

        if (headCount > 1)
        {
            Console.WriteLine($"[WARNING] Flow '{flow.Name}': {headCount} heads found, using first '{headCall?.Name ?? "N/A"}'");
        }
        if (tailCount > 1)
        {
            Console.WriteLine($"[WARNING] Flow '{flow.Name}': {tailCount} tails found, using first '{tailCall?.Name ?? "N/A"}'");
        }

        return new FlowAnalysisResult
        {
            FlowName = flow.Name,
            FlowId = flow.Id,
            RepresentativeWorkId = repWork.Id,
            RepresentativeWorkName = repWork.Name,
            HeadCall = headCall,
            TailCall = tailCall,
            HeadCount = headCount,
            TailCount = tailCount,
            MovingStartName = headCall?.Name,
            MovingEndName = tailCall?.Name,
        };
    }

    private static Work? SelectRepresentativeWork(IReadOnlyCollection<Work> works, DsStore store)
    {
        if (works.Count == 0) return null;

        var withCounts = works
            .Select(w => (Work: w, Count: Queries.callsOf(w.Id, store).Count()))
            .ToList();

        var maxCount = withCounts.Max(x => x.Count);
        return withCounts
            .Where(x => x.Count == maxCount)
            .OrderBy(x => x.Work.Name)
            .Select(x => x.Work)
            .FirstOrDefault();
    }

    private static List<(Guid Source, Guid Target)> FlattenGroupArrows(IReadOnlyCollection<ArrowBetweenCalls> arrows)
    {
        var startArrows = arrows.Where(a => a.ArrowType == ArrowType.Start).ToList();
        var groupArrows = arrows.Where(a => a.ArrowType == ArrowType.Group).ToList();

        if (groupArrows.Count == 0)
        {
            return startArrows.Select(a => (a.SourceId, a.TargetId)).ToList();
        }

        var parent = new Dictionary<Guid, Guid>();

        Guid Find(Guid x)
        {
            if (!parent.ContainsKey(x))
            {
                parent[x] = x;
                return x;
            }
            if (parent[x] == x) return x;
            var root = Find(parent[x]);
            parent[x] = root;
            return root;
        }

        void Union(Guid x, Guid y)
        {
            var rootX = Find(x);
            var rootY = Find(y);
            if (rootX != rootY) parent[rootY] = rootX;
        }

        foreach (var a in groupArrows)
        {
            Union(a.SourceId, a.TargetId);
        }

        var groupsByRoot = parent.Keys
            .GroupBy(Find)
            .ToDictionary(g => g.Key, g => g.ToList());

        var nodeToGroup = new Dictionary<Guid, List<Guid>>();
        foreach (var kv in groupsByRoot)
        {
            foreach (var node in kv.Value)
            {
                nodeToGroup[node] = kv.Value;
            }
        }

        var edges = new HashSet<(Guid, Guid)>();
        foreach (var arrow in startArrows)
        {
            var sources = nodeToGroup.TryGetValue(arrow.SourceId, out var srcGroup) ? srcGroup : new List<Guid> { arrow.SourceId };
            var targets = nodeToGroup.TryGetValue(arrow.TargetId, out var tgtGroup) ? tgtGroup : new List<Guid> { arrow.TargetId };

            foreach (var s in sources)
            {
                foreach (var t in targets)
                {
                    edges.Add((s, t));
                }
            }
        }

        return edges.ToList();
    }

    private static List<CallDagNode> BuildCallDag(IReadOnlyCollection<Call> calls, IReadOnlyCollection<ArrowBetweenCalls> arrows)
    {
        var callIds = calls.Select(c => c.Id).ToHashSet();
        var flattenedEdges = FlattenGroupArrows(arrows);

        var inDegrees = flattenedEdges
            .Where(e => callIds.Contains(e.Target))
            .GroupBy(e => e.Target)
            .ToDictionary(g => g.Key, g => g.Count());

        var outDegrees = flattenedEdges
            .Where(e => callIds.Contains(e.Source))
            .GroupBy(e => e.Source)
            .ToDictionary(g => g.Key, g => g.Count());

        return calls
            .Select(call => new CallDagNode(
                call,
                inDegrees.TryGetValue(call.Id, out var inD) ? inD : 0,
                outDegrees.TryGetValue(call.Id, out var outD) ? outD : 0))
            .ToList();
    }

    private static void DetectCycle(IReadOnlyCollection<CallDagNode> dag, IReadOnlyCollection<(Guid Source, Guid Target)> flattenedEdges)
    {
        var callIdSet = dag.Select(n => n.Call.Id).ToHashSet();
        var inDegreeCounts = dag.ToDictionary(n => n.Call.Id, n => n.InDegree);

        var queue = new Queue<Guid>();
        foreach (var node in dag)
        {
            if (node.InDegree == 0) queue.Enqueue(node.Call.Id);
        }

        var visitedCount = 0;
        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            visitedCount++;

            foreach (var (sourceId, targetId) in flattenedEdges)
            {
                if (sourceId == currentId && callIdSet.Contains(targetId))
                {
                    inDegreeCounts[targetId] -= 1;
                    if (inDegreeCounts[targetId] == 0)
                    {
                        queue.Enqueue(targetId);
                    }
                }
            }
        }

        if (visitedCount != dag.Count)
        {
            throw new InvalidOperationException(
                $"Cycle detected in Call DAG. Visited {visitedCount} out of {dag.Count} nodes.");
        }
    }

    private static (Call? Call, int Count) FindHeadCall(IReadOnlyCollection<CallDagNode> dag)
    {
        var heads = dag
            .Where(n => n.InDegree == 0)
            .Select(n => n.Call)
            .OrderBy(c => c.Name)
            .ToList();

        return (heads.FirstOrDefault(), heads.Count);
    }

    private static (Call? Call, int Count) FindTailCall(IReadOnlyCollection<CallDagNode> dag)
    {
        var tails = dag
            .Where(n => n.OutDegree == 0)
            .Select(n => n.Call)
            .OrderBy(c => c.Name)
            .ToList();

        return (tails.FirstOrDefault(), tails.Count);
    }
}
