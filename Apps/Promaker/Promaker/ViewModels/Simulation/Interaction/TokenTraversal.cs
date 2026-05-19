using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Runtime.Model;
using Ds2.Runtime.Report;

namespace Promaker.ViewModels;

/// <summary>
/// 토큰별 traversal 시간 추적 — F# <c>TokenTraversalSession</c> 위임.
/// C# 측은 엔진 호출 (origin/specLabel 결정) 과 dispatcher wiring 만 담당.
/// </summary>
public partial class SimulationPanelState
{
    private readonly TokenTraversalSession.Session _traversalSession = new();

    private void OnTokenEventForTraversal(TokenEventArgs args)
    {
        if (args.Token == null) return;
        var item = args.Token.Item;
        var nowTs = _simStartTime + args.Clock;

        switch (args.Kind)
        {
            case var k when k.IsSeed:
            {
                var (originName, _) = ResolveTokenOrigin(args.Token);
                var specLabel = ResolveSpecLabelByOrigin(originName);
                _traversalSession.RecordSeed(item, originName, specLabel, nowTs, args.WorkName);
                break;
            }
            case var k when k.IsShift:
            {
                var target = args.TargetWorkName is not null
                             && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(args.TargetWorkName)
                    ? args.TargetWorkName.Value
                    : args.WorkName;
                _traversalSession.RecordShift(item, args.WorkName, target, nowTs);
                break;
            }
            case var k when k.IsComplete:
                _traversalSession.RecordComplete(item, args.WorkName, nowTs);
                break;
            case var k when k.IsDiscard || k.IsBlockedOnHoming:
                _traversalSession.RecordDiscardOrBlocked(item, args.WorkName, nowTs);
                break;
        }
    }

    private (string Name, int Seq) ResolveTokenOrigin(TokenValue token)
    {
        var origin = _simEngine?.GetTokenOrigin(token);
        if (origin != null && Microsoft.FSharp.Core.FSharpOption<Tuple<string, int>>.get_IsSome(origin))
            return (origin.Value.Item1 ?? "", origin.Value.Item2);
        return ("", 0);
    }

    private string ResolveSpecLabelByOrigin(string originName)
    {
        if (string.IsNullOrEmpty(originName)) return "";
        try
        {
            var projects = Ds2.Core.Store.Queries.allProjects(Store);
            if (projects.IsEmpty) return originName;
            var project = projects.Head;
            foreach (var spec in project.TokenSpecs)
            {
                if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(spec.WorkId))
                {
                    var wid = spec.WorkId.Value;
                    if (Store.Works.TryGetValue(wid, out var w) &&
                        string.Equals(w.Name, originName, StringComparison.Ordinal))
                    {
                        return string.IsNullOrEmpty(spec.Label) ? originName : spec.Label;
                    }
                }
            }
        }
        catch { /* best-effort */ }
        return originName;
    }

    internal IReadOnlyList<KpiAggregator.TokenTraversal> CollectTraversalsSnapshot() =>
        _traversalSession.Snapshot().ToList();

    /// <summary>
    /// 시뮬 종료 시점 sweep — 활성 traversal 들을 finalize 하여 completed 로 옮긴다.
    /// 이 호출은 capture 직전이어야 KPI 집계가 모든 토큰을 본다.
    /// </summary>
    internal void FinalizePendingTraversals() => _traversalSession.FinalizePending();

    internal void ResetTraversalTracking() => _traversalSession.Reset();
}
