using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core.Store;

namespace Promaker.Controls.ExpressionEditor.Providers;

/// <summary>
/// Call 조건 (AutoAux/ComAux) 편집용 심볼 제공자.
/// 후보: 현 Call 의 ApiCalls + 같은 Flow 의 다른 Call 의 ApiCalls (Group 으로 구분).
/// 자유 텍스트 입력 비허용 — 모델 바운드 ApiCall 만 사용 (RefKey = ApiCall.Id).
/// </summary>
public sealed class ApiCallSymbolProvider : IExpressionSymbolProvider
{
    private readonly List<SymbolCandidate> _candidates;

    public ApiCallSymbolProvider(DsStore store, Guid callId)
    {
        _candidates = BuildCandidates(store, callId);
    }

    public IReadOnlyList<SymbolCandidate> GetCandidates() => _candidates;

    public bool IsValid(string symbol, Guid? refKey) => refKey.HasValue;

    public bool AllowsFreeText => false;

    private static List<SymbolCandidate> BuildCandidates(DsStore store, Guid callId)
    {
        var result = new List<SymbolCandidate>();
        if (!store.Calls.TryGetValue(callId, out var call)) return result;

        foreach (var ac in call.ApiCalls)
            result.Add(new SymbolCandidate(ac.Name, ac.Id, "현재 Call"));

        if (store.Works.TryGetValue(call.ParentId, out var work)
            && store.Flows.TryGetValue(work.ParentId, out var flow))
        {
            var seen = new HashSet<Guid>(call.ApiCalls.Select(a => a.Id));
            foreach (var w in store.Works.Values.Where(w => w.ParentId == flow.Id))
                foreach (var c in store.Calls.Values.Where(c => c.ParentId == w.Id && c.Id != callId))
                    foreach (var ac in c.ApiCalls)
                        if (seen.Add(ac.Id))
                            result.Add(new SymbolCandidate($"{ac.Name}  ({c.Name})", ac.Id, "Flow 내"));
        }

        return result;
    }
}
