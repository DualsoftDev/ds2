using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Runtime.Model;
using Ds2.Runtime.Report;

namespace Promaker.ViewModels;

/// <summary>
/// 혼류(Mixed-flow) 환경에서 토큰별 traversal 시간을 추적.
/// TokenEvent (Seed/Shift/Complete) 를 구독하여 토큰 인스턴스별 timeline 을 구성.
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
        // (workName, arrivalAt) — 가장 최근 arrival 만 보관 (재방문 시 누적)
        public string? CurrentWorkName;
        public DateTime CurrentWorkArrival;
        // 누적 (workName → 누적 G+F 시간 초)
        public Dictionary<string, double> WorkTimes = new();
    }

    private readonly Dictionary<int, TraversalInProgress> _activeTraversals = new();
    private readonly List<KpiAggregator.TokenTraversal> _completedTraversals = new();

    private void OnTokenEventForTraversal(TokenEventArgs args)
    {
        if (args.Token == null) return;
        var item = args.Token.Item;
        var nowClock = args.Clock;
        var nowTs = _simStartTime + nowClock;

        var origin = _simEngine?.GetTokenOrigin(args.Token);
        var (originName, _) = (origin != null && Microsoft.FSharp.Core.FSharpOption<Tuple<string, int>>.get_IsSome(origin))
            ? (origin.Value.Item1 ?? "", origin.Value.Item2)
            : ("", 0);

        // SpecLabel 매핑 시도 (Project.TokenSpecs 의 첫 매칭)
        var specLabel = ResolveSpecLabelByOrigin(originName);

        switch (args.Kind)
        {
            case var k when k.IsSeed:
            {
                var t = new TraversalInProgress
                {
                    TokenItem = item,
                    OriginName = originName,
                    SpecLabel = specLabel,
                    SeedAt = nowTs,
                    CurrentWorkName = args.WorkName,
                    CurrentWorkArrival = nowTs,
                };
                _activeTraversals[item] = t;
                break;
            }
            case var k when k.IsShift:
            {
                if (!_activeTraversals.TryGetValue(item, out var t)) return;
                // 이전 Work 체류 시간 누적
                if (t.CurrentWorkName != null)
                {
                    var dur = (nowTs - t.CurrentWorkArrival).TotalSeconds;
                    if (dur > 0.0)
                    {
                        var key = t.CurrentWorkName;
                        t.WorkTimes[key] = t.WorkTimes.TryGetValue(key, out var prev) ? prev + dur : dur;
                    }
                }
                // 다음 Work 진입
                t.CurrentWorkName = args.TargetWorkName != null && Microsoft.FSharp.Core.FSharpOption<string>.get_IsSome(args.TargetWorkName)
                    ? args.TargetWorkName.Value
                    : args.WorkName;
                t.CurrentWorkArrival = nowTs;
                break;
            }
            case var k when k.IsComplete:
            {
                if (!_activeTraversals.TryGetValue(item, out var t)) return;
                if (t.CurrentWorkName != null)
                {
                    var dur = (nowTs - t.CurrentWorkArrival).TotalSeconds;
                    if (dur > 0.0)
                    {
                        var key = t.CurrentWorkName;
                        t.WorkTimes[key] = t.WorkTimes.TryGetValue(key, out var prev) ? prev + dur : dur;
                    }
                }
                _activeTraversals.Remove(item);
                _completedTraversals.Add(MakeTraversal(t, nowTs));
                break;
            }
            case var k when k.IsDiscard || k.IsBlockedOnHoming:
            {
                // 진행 중 traversal 종료 (집계 제외 — CompleteAt = None 처리)
                if (_activeTraversals.TryGetValue(item, out var t))
                {
                    _activeTraversals.Remove(item);
                    _completedTraversals.Add(MakeTraversal(t, null));
                }
                break;
            }
        }
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
            // TokenSpec.WorkId 가 originName 의 Source Work 와 매칭되는 첫 spec
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
        // 진행 중 토큰도 부분집계용으로 함께 반환 (CompleteAt = None)
        var partial = _activeTraversals.Values
            .Select(t => MakeTraversal(t, null));
        return _completedTraversals.Concat(partial).ToList();
    }

    internal void ResetTraversalTracking()
    {
        _activeTraversals.Clear();
        _completedTraversals.Clear();
    }
}
