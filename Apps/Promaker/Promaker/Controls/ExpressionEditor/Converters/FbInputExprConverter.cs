using Ds2.Core;
using Promaker.Controls.ExpressionEditor.Models;

namespace Promaker.Controls.ExpressionEditor.Converters;

/// <summary>
/// ExprNode (UI) ↔ FbInputExpr (Ds2.Core) 양방향 변환.
/// Pre-FB 컨텍스트 — RefKey 무시 (Symbol 만 저장).
/// enum 정수값이 동일하므로 단순 매핑.
/// </summary>
public static class FbInputExprConverter
{
    public static ExprNode? FromCore(FbInputExpr? core)
    {
        if (core == null) return null;
        var node = new ExprNode { SuppressKindMigration = true };
        node.Kind   = (ExprKind)(int)core.Kind;
        node.Symbol = core.Symbol ?? "";
        if (core.Children != null)
            foreach (var c in core.Children)
            {
                var cn = FromCore(c);
                if (cn != null) node.Children.Add(cn);
            }
        node.SuppressKindMigration = false;
        return node;
    }

    public static FbInputExpr? ToCore(ExprNode? node)
    {
        if (node == null) return null;
        var core = new FbInputExpr
        {
            Kind   = (FbInputExprKind)(int)node.Kind,
            Symbol = node.Symbol ?? "",
        };
        foreach (var c in node.Children)
        {
            var cc = ToCore(c);
            if (cc != null) core.Children.Add(cc);
        }
        return core;
    }
}
