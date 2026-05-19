using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core.Store;
using Ds2.Editor;

namespace Promaker.Controls.ExpressionEditor.Providers;

/// <summary>
/// Call 조건 (AutoAux/ComAux) 편집용 심볼 제공자.
/// 후보 수집 결정은 F# <see cref="ApiCallCandidates"/> 위임 — 현 Call 의 ApiCalls
/// + 같은 Flow 의 다른 Call 의 ApiCalls (Group 라벨 포함, dedup).
/// 자유 텍스트 입력 비허용 — 모델 바운드 ApiCall 만 사용 (RefKey = ApiCall.Id).
/// </summary>
public sealed class ApiCallSymbolProvider : IExpressionSymbolProvider
{
    private readonly List<SymbolCandidate> _candidates;

    public ApiCallSymbolProvider(DsStore store, Guid callId)
    {
        _candidates = ApiCallCandidates.Collect(store, callId)
            .Select(c => new SymbolCandidate(c.Name, c.ApiCallId, c.GroupLabel))
            .ToList();
    }

    public IReadOnlyList<SymbolCandidate> GetCandidates() => _candidates;

    public bool IsValid(string symbol, Guid? refKey) => refKey.HasValue;

    public bool AllowsFreeText => false;
}
