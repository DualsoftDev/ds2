using System;
using System.Collections.Generic;

namespace Promaker.Controls.ExpressionEditor.Providers;

/// <summary>
/// 식 편집기 leaf 변수 후보 + 검증을 컨텍스트별로 제공.
/// • Pre-FB: SignalPattern 기반 (현 SystemType IW/QW/MW Pattern + 자유 텍스트)
/// • Call 조건: ApiCall 기반 (Call 의 가시 ApiCall 후보, RefKey = ApiCall.Id)
/// </summary>
public interface IExpressionSymbolProvider
{
    /// <summary>자동완성 후보 — 트리뷰의 Var/NegVar 노드 콤보박스.</summary>
    IReadOnlyList<SymbolCandidate> GetCandidates();

    /// <summary>심볼 검증 — true 면 정상, false 면 빨간 표시 / 경고.
    /// 자유 텍스트 허용 컨텍스트는 항상 true.</summary>
    bool IsValid(string symbol, Guid? refKey);

    /// <summary>자유 텍스트 입력 허용 여부 — false 면 콤보 제한 (모델 바운드 only).</summary>
    bool AllowsFreeText { get; }
}

/// <summary>심볼 후보 — 표시 이름 + (선택) 모델 엔티티 키.</summary>
public sealed record SymbolCandidate(string Display, Guid? RefKey = null, string? Group = null);
