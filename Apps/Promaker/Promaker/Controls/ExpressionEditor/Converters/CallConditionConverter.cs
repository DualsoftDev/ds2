using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using AAStoPLC.LadderEditor.Expression;

namespace Promaker.Controls.ExpressionEditor.Converters;

/// <summary>
/// Ds2.Core.CallCondition (ApiCall-바운드 leaves + IsOR/IsRising 플래그) ↔ ExprNode.
///
/// 매핑 규칙:
///   • CallCondition(IsOR=false): ExprKind.And, children = leaves(Var with RefKey) ++ children(recursive)
///   • CallCondition(IsOR=true) : ExprKind.Or
///   • IsRising=true            : 각 leaf 변수를 ExprKind.Rising 으로 변환 (legacy 의미)
///
/// 역변환은 의미 손실 가능 — 트리 편집기에서 NegVar/Falling/Raw 같이 CallCondition 에 직접 매핑 안 되는
/// 노드를 만들면 Raw 식 또는 무시. 사용자에게 사전 안내 필요.
/// </summary>
public static class CallConditionConverter
{
    /// <summary>CallConditionPanelItem (Editor 의 view-type) → ExprNode 트리.</summary>
    public static ExprNode FromCallCondition(CallConditionPanelItem cc, DsStore store)
    {
        if (cc == null) return new ExprNode(ExprKind.Var);

        var leafNodes = new List<ExprNode>();
        if (cc.Items != null)
        {
            foreach (var item in cc.Items)
            {
                if (item == null) continue;
                var name = item.ApiDefDisplayName ?? "";
                leafNodes.Add(new ExprNode(ExprKind.Var, name, item.ApiCallId));
            }
        }

        var childNodes = new List<ExprNode>();
        if (cc.Children != null)
            foreach (var child in cc.Children)
                childNodes.Add(FromCallCondition(child, store));

        var combinatorKind = cc.IsOR ? ExprKind.Or : ExprKind.And;
        var allKids = leafNodes.Concat(childNodes).ToList();

        if (allKids.Count == 0) return new ExprNode(combinatorKind);
        if (allKids.Count == 1) return allKids[0];

        var node = new ExprNode { SuppressKindMigration = true };
        node.Kind = combinatorKind;
        foreach (var k in allKids) node.Children.Add(k);
        node.SuppressKindMigration = false;
        return node;
    }

    // 역변환 (ExprNode → CallCondition) 은 store API 기반 rebuild 가 필요 — 별도 마이그레이션 작업으로 분리.
    // CallCondition 은 internal 생성자를 가지며 ApiCall 객체 참조 무결성 유지가 까다로워, 본 변환에서는
    // 시각화/미리보기 단방향만 제공한다. 실제 편집은 기존 ConditionEditDialog 의 store API 경로 유지.
}
