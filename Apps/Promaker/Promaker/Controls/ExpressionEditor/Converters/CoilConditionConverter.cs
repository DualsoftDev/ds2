using System.Linq;
using AAStoPLC.Ir;
using Microsoft.FSharp.Collections;
using Promaker.Controls.ExpressionEditor.Models;

namespace Promaker.Controls.ExpressionEditor.Converters;

/// <summary>
/// ExprNode (UI) → AAStoPLC.Ir.CoilCondition (LD/ST 미리보기용 단방향).
/// PLC 출력과 동일한 simplify/layout 엔진을 미리보기에서도 재사용 — 시각 진실원 통합.
/// </summary>
public static class CoilConditionConverter
{
    public static CoilCondition ToCoilCondition(ExprNode? node)
    {
        if (node == null) return CoilCondition.AlwaysTrue;
        // 심볼 없는 leaf 는 AlwaysTrue (식에서 제외) — AND/OR.simplify 가 자동 필터.
        if (node.Kind.HasSymbol() && string.IsNullOrWhiteSpace(node.Symbol))
            return CoilCondition.AlwaysTrue;
        return node.Kind switch
        {
            ExprKind.Var      => CoilCondition.NewVar(node.Symbol ?? ""),
            ExprKind.NegVar   => CoilCondition.NewNegVar(node.Symbol ?? ""),
            ExprKind.Rising   => CoilCondition.NewRising(node.Symbol ?? ""),
            ExprKind.Falling  => CoilCondition.NewFalling(node.Symbol ?? ""),
            ExprKind.Raw      => CoilCondition.NewRaw(node.Symbol ?? ""),
            ExprKind.And      => CoilCondition.NewAnd(ToFSharpList(node)),
            ExprKind.Or       => CoilCondition.NewOr(ToFSharpList(node)),
            ExprKind.Not      => node.Children.Count > 0
                                    ? CoilCondition.NewNot(ToCoilCondition(node.Children[0]))
                                    : CoilCondition.AlwaysTrue,
            _                 => CoilCondition.AlwaysTrue,
        };
    }

    /// <summary>심볼 빈 leaf 는 자식에서 제외 — AND/OR 표현 깔끔하게.</summary>
    private static FSharpList<CoilCondition> ToFSharpList(ExprNode node)
    {
        var arr = node.Children
            .Where(c => !(c.Kind.HasSymbol() && string.IsNullOrWhiteSpace(c.Symbol)))
            .Select(ToCoilCondition)
            .ToArray();
        return ListModule.OfArray(arr);
    }

    /// <summary>식 → ST 텍스트. CoilCondition.toSt 위임.</summary>
    public static string ToStPreview(ExprNode? node) =>
        AAStoPLC.Ir.CoilConditionModule.toSt(ToCoilCondition(node));

    /// <summary>역변환 — CoilCondition → ExprNode 트리 (LadderEditor 편집 결과 → 트리 동기화).</summary>
    public static ExprNode? ToExprNode(CoilCondition? cond)
    {
        if (cond is null) return null;
        return cond switch
        {
            CoilCondition.Var v      => Leaf(ExprKind.Var,     v.name),
            CoilCondition.NegVar v   => Leaf(ExprKind.NegVar,  v.name),
            CoilCondition.Rising v   => Leaf(ExprKind.Rising,  v.name),
            CoilCondition.Falling v  => Leaf(ExprKind.Falling, v.name),
            CoilCondition.Raw r      => Leaf(ExprKind.Raw,     r.expr),
            CoilCondition.And a      => Group(ExprKind.And, a.operands),
            CoilCondition.Or  o      => Group(ExprKind.Or,  o.operands),
            CoilCondition.Not n      => WrapNot(n.operand),
            _                        => Leaf(ExprKind.Var, ""),
        };
    }

    private static ExprNode Leaf(ExprKind k, string sym)
    {
        var n = new ExprNode(k);
        n.Symbol = sym;
        return n;
    }

    private static ExprNode Group(ExprKind k, FSharpList<CoilCondition> ops)
    {
        var n = new ExprNode(k);
        foreach (var op in ops)
        {
            var child = ToExprNode(op);
            if (child is not null) n.Children.Add(child);
        }
        return n;
    }

    private static ExprNode WrapNot(CoilCondition inner)
    {
        var n = new ExprNode(ExprKind.Not);
        var child = ToExprNode(inner);
        if (child is not null) n.Children.Add(child);
        return n;
    }
}
