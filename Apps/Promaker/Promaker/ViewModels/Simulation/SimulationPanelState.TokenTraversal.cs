using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Runtime.Model;
using Ds2.Runtime.Report;

namespace Promaker.ViewModels;

/// <summary>
/// 혼류(Mixed-flow) 환경에서 토큰별 traversal 시간을 추적.
/// TokenEvent (Seed/Shift/Complete/Discard/BlockedOnHoming) 를 구독하여
/// **분기(branch)** 를 포함한 토큰의 모든 활성 경로를 동시 추적한다.
///
/// 핵심 의미:
///   - 한 토큰이 N 개 successor 로 분기되면 같은 TokenItem 으로 N 개의 Shift 이벤트가 emit 됨.
///   - 이 클래스는 이를 N 개의 ActivePath 로 표현하여 각 branch 의 시간을 독립 추적.
///   - traversal 종료(완주) 시각 = max(완주 branch 들의 CompleteAt) — critical path 시맨틱.
///
/// 시뮬 종료 후 KpiAggregator.buildPerTokenKpis 에 입력으로 사용.
/// </summary>
public partial class SimulationPanelState
{
    private sealed class TraversalInProgress
    {
        public int TokenItem;
        public string OriginName = "";
        public string SpecLabel = "";
        public DateTime SeedAt;

        // 활성 branch 경로 — 한 토큰이 동시에 여러 Work 에 걸쳐 있을 수 있음.
        //   (workName, arrivalAt). 분기/병합 시 이 리스트가 늘었다 줄었다 한다.
        public List<(string WorkName, DateTime ArrivalAt)> ActivePaths = new();

        // 누적 (workName → 모든 branch 가 그 Work 에 머문 G+F 시간 합).
        public Dictionary<string, double> WorkTimes = new();

        // 완주(Complete) branch 들 중 가장 늦은 도달 시각 — 토큰 총 traversal 시간 산정용.
        public DateTime? MaxCompleteAt;

        // discard / blocked 으로 종료된 branch 가 하나라도 있는지 (관측용).
        public bool AnyDiscarded;
    }

    private readonly Dictionary<int, TraversalInProgress> _activeTraversals = new();
    private readonly List<KpiAggregator.TokenTraversal> _completedTraversals = new();

    private void OnTokenEventForTraversal(TokenEventArgs args)
    {
        if (args.Token == null) return;
        var item = args.Token.Item;
        var nowClock = args.Clock;
        var nowTs = _simStartTime + nowClock;

        switch (args.Kind)
        {
            case var k when k.IsSeed:
            {
                var origin = _simEngine?.GetTokenOrigin(args.Token);
                var (originName, _) = (origin != null && Microsoft.FSharp.Core.FSharpOption<Tuple<string, int>>.get_IsSome(origin))
                    ? (origin.Value.Item1 ?? "", origin.Value.Item2)
                    : ("", 0);
                var specLabel = ResolveSpecLabelByOrigin(originName);

                var t = new TraversalInProgress
                {
                    TokenItem = item,
                    OriginName = originName,
                    SpecLabel = specLabel,
                    SeedAt = nowTs,
                    ActivePaths = new List<(string, DateTime)> { (args.WorkName, nowTs) },
                };
                _activeTraversals[item] = t;
                break;
            }
            case var k when k.IsShift:
            {
                if (!_activeTraversals.TryGetValue(item, out var t)) return;

                // Shift(W → T') — branch 시 동일 tick 으로 N 회 fire 될 수 있음.
                //   첫 번째 Shift: ActivePaths 의 W 항목을 찾아 시간 누적 + 제거.
                //   두 번째 이후: W 가 이미 제거됐으므로 단순히 새 target 만 추가 → 자연스러운 분기.
                RemoveOnePathAt(t, args.WorkName, nowTs);

                var target = args.TargetWorkName != null && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(args.TargetWorkName)
                    ? args.TargetWorkName.Value
                    : args.WorkName;
                t.ActivePaths.Add((target, nowTs));
                break;
            }
            case var k when k.IsComplete:
            {
                if (!_activeTraversals.TryGetValue(item, out var t)) return;

                RemoveOnePathAt(t, args.WorkName, nowTs);
                t.MaxCompleteAt = t.MaxCompleteAt.HasValue && t.MaxCompleteAt.Value >= nowTs
                    ? t.MaxCompleteAt
                    : nowTs;

                // 모든 branch 가 종료되면 traversal finalize.
                if (t.ActivePaths.Count == 0)
                    FinalizeTraversal(item, t);
                break;
            }
            case var k when k.IsDiscard || k.IsBlockedOnHoming:
            {
                if (!_activeTraversals.TryGetValue(item, out var t)) return;

                RemoveOnePathAt(t, args.WorkName, nowTs);
                t.AnyDiscarded = true;

                if (t.ActivePaths.Count == 0)
                    FinalizeTraversal(item, t);
                break;
            }
        }
    }

    /// <summary>
    /// ActivePaths 에서 workName 과 일치하는 첫 항목을 찾아 G+F 시간을 누적하고 제거한다.
    /// 일치 항목이 없으면 무동작 (분기 시 두 번째 이후 Shift 가 이 케이스).
    /// </summary>
    private static void RemoveOnePathAt(TraversalInProgress t, string workName, DateTime nowTs)
    {
        var idx = -1;
        for (int i = 0; i < t.ActivePaths.Count; i++)
            if (string.Equals(t.ActivePaths[i].WorkName, workName, StringComparison.Ordinal)) { idx = i; break; }
        if (idx < 0) return;

        var (name, arrivalAt) = t.ActivePaths[idx];
        var dur = (nowTs - arrivalAt).TotalSeconds;
        if (dur > 0.0)
            t.WorkTimes[name] = t.WorkTimes.TryGetValue(name, out var prev) ? prev + dur : dur;

        t.ActivePaths.RemoveAt(idx);
    }

    private void FinalizeTraversal(int item, TraversalInProgress t)
    {
        _activeTraversals.Remove(item);
        _completedTraversals.Add(MakeTraversal(t, t.MaxCompleteAt));
    }

    private static KpiAggregator.TokenTraversal MakeTraversal(TraversalInProgress t, DateTime? completeAt)
    {
        var workTimes = t.WorkTimes
            .Select(kv => Tuple.Create(kv.Key, kv.Value))
            .ToList();
        return new KpiAggregator.TokenTraversal(
            tokenItem: t.TokenItem,
            originName: t.OriginName ?? "",
            specLabel: t.SpecLabel ?? "",
            seedAt: t.SeedAt,
            completeAt: completeAt.HasValue
                ? Microsoft.FSharp.Core.FSharpOption<DateTime>.Some(completeAt.Value)
                : Microsoft.FSharp.Core.FSharpOption<DateTime>.None,
            workTimes: Microsoft.FSharp.Collections.ListModule.OfSeq(workTimes));
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

    internal IReadOnlyList<KpiAggregator.TokenTraversal> CollectTraversalsSnapshot()
    {
        // 진행 중 토큰도 부분집계용으로 함께 반환 — 완주 branch 가 있으면 그 max 시각 사용,
        //   없으면 CompleteAt = None (= 미완주).
        var partial = _activeTraversals.Values
            .Select(t => MakeTraversal(t, t.MaxCompleteAt));
        return _completedTraversals.Concat(partial).ToList();
    }

    /// <summary>
    /// 시뮬 종료 시점 sweep — 활성 traversal 들을 finalize 하여 completed 로 옮긴다.
    /// 완주 branch 가 하나라도 있으면 그 max 시각을 CompleteAt 으로, 없으면 None (미완주) 으로 기록.
    /// 이 호출은 capture 직전이어야 KPI 집계가 모든 토큰을 본다.
    /// </summary>
    internal void FinalizePendingTraversals()
    {
        if (_activeTraversals.Count == 0) return;
        var pending = _activeTraversals.ToList();  // 스냅샷 (반복 중 수정 회피)
        foreach (var kv in pending)
            FinalizeTraversal(kv.Key, kv.Value);
    }

    internal void ResetTraversalTracking()
    {
        _activeTraversals.Clear();
        _completedTraversals.Clear();
    }
}
