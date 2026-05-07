namespace Promaker.Controls.ExpressionEditor.Models;

/// <summary>
/// 식 노드 종류 — Pre-FB 입력식과 Call 조건 양쪽 모두 표현.
/// AAStoPLC.Ir.CoilCondition / Ds2.Core.FbInputExprKind / CallCondition(IsOR/IsRising) 와
/// 양방향 변환 가능. UI 트리뷰에서 직접 다룬다.
/// </summary>
public enum ExprKind
{
    /// <summary>변수 (NO contact). Symbol = 변수명/식별자.</summary>
    Var      = 0,
    /// <summary>부정 변수 (NC contact). Symbol = 변수명.</summary>
    NegVar   = 1,
    /// <summary>AND 결합 (직렬). Children 사용.</summary>
    And      = 2,
    /// <summary>OR 결합 (병렬). Children 사용.</summary>
    Or       = 3,
    /// <summary>NOT (Children[0] 부정). Children 1개.</summary>
    Not      = 4,
    /// <summary>Rising edge (P contact). Symbol = 변수명.</summary>
    Rising   = 5,
    /// <summary>Falling edge (N contact). Symbol = 변수명.</summary>
    Falling  = 6,
    /// <summary>raw 부울 식 (백엔드가 그대로 emit). Symbol = 식 문자열.</summary>
    Raw      = 7,
}

public static class ExprKindEx
{
    public static bool HasSymbol(this ExprKind k) =>
        k is ExprKind.Var or ExprKind.NegVar or ExprKind.Rising or ExprKind.Falling or ExprKind.Raw;

    public static bool HasChildren(this ExprKind k) =>
        k is ExprKind.And or ExprKind.Or or ExprKind.Not;

    /// <summary>NOT 은 자식 1개만, And/Or 는 ≥1.</summary>
    public static int MaxChildren(this ExprKind k) =>
        k == ExprKind.Not ? 1 : int.MaxValue;

    public static string DisplayName(this ExprKind k) => k switch
    {
        ExprKind.Var      => "변수 (Var)",
        ExprKind.NegVar   => "부정 변수 (NegVar)",
        ExprKind.And      => "AND",
        ExprKind.Or       => "OR",
        ExprKind.Not      => "NOT",
        ExprKind.Rising   => "Rising (P)",
        ExprKind.Falling  => "Falling (N)",
        ExprKind.Raw      => "Raw 식",
        _                 => k.ToString(),
    };
}
