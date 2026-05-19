using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Model;
using Ds2.Runtime.Report;

namespace Promaker.ViewModels;

/// <summary>
/// 토큰별 traversal 시간 추적 collaborator. F# <c>TokenTraversalSession</c> 위임 + 토큰 origin/specLabel 결정만 담당.
/// SimulationPanelState 의 partial 에서 분리. store/engine/시작시각은 Func 로 주입.
/// </summary>
public sealed class SimulationTokenTraversalTracker
{
    private readonly TokenTraversalSession.Session _session = new();
    private readonly Func<DsStore>                  _storeProvider;
    private readonly Func<ISimulationEngine?>       _engineProvider;
    private readonly Func<DateTime>                 _simStartTimeProvider;

    public SimulationTokenTraversalTracker(
        Func<DsStore>             storeProvider,
        Func<ISimulationEngine?>  engineProvider,
        Func<DateTime>            simStartTimeProvider)
    {
        _storeProvider        = storeProvider;
        _engineProvider       = engineProvider;
        _simStartTimeProvider = simStartTimeProvider;
    }

    public void OnTokenEvent(TokenEventArgs args)
    {
        if (args.Token == null) return;
        var item = args.Token.Item;
        var nowTs = _simStartTimeProvider() + args.Clock;

        switch (args.Kind)
        {
            case var k when k.IsSeed:
            {
                var (originName, _) = ResolveOrigin(args.Token);
                var specLabel = ResolveSpecLabelByOrigin(originName);
                _session.RecordSeed(item, originName, specLabel, nowTs, args.WorkName);
                break;
            }
            case var k when k.IsShift:
            {
                var target = args.TargetWorkName is not null
                             && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(args.TargetWorkName)
                    ? args.TargetWorkName.Value
                    : args.WorkName;
                _session.RecordShift(item, args.WorkName, target, nowTs);
                break;
            }
            case var k when k.IsComplete:
                _session.RecordComplete(item, args.WorkName, nowTs);
                break;
            case var k when k.IsDiscard || k.IsBlockedOnHoming:
                _session.RecordDiscardOrBlocked(item, args.WorkName, nowTs);
                break;
        }
    }

    public IReadOnlyList<KpiAggregator.TokenTraversal> Snapshot() => _session.Snapshot().ToList();

    /// <summary>시뮬 종료 시점 sweep — 활성 traversal 들을 finalize 하여 completed 로 옮긴다.
    /// 이 호출은 capture 직전이어야 KPI 집계가 모든 토큰을 본다.</summary>
    public void FinalizePending() => _session.FinalizePending();

    public void Reset() => _session.Reset();

    private (string Name, int Seq) ResolveOrigin(TokenValue token)
    {
        var origin = _engineProvider()?.GetTokenOrigin(token);
        if (origin != null && Microsoft.FSharp.Core.FSharpOption<Tuple<string, int>>.get_IsSome(origin))
            return (origin.Value.Item1 ?? "", origin.Value.Item2);
        return ("", 0);
    }

    private string ResolveSpecLabelByOrigin(string originName)
    {
        if (string.IsNullOrEmpty(originName)) return "";
        try
        {
            var store = _storeProvider();
            var projects = Queries.allProjects(store);
            if (projects.IsEmpty) return originName;
            var project = projects.Head;
            foreach (var spec in project.TokenSpecs)
            {
                if (Microsoft.FSharp.Core.FSharpOption<Guid>.get_IsSome(spec.WorkId))
                {
                    var wid = spec.WorkId.Value;
                    if (store.Works.TryGetValue(wid, out var w) &&
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
}
