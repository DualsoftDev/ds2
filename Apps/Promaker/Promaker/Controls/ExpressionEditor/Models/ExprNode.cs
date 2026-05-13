using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Promaker.Controls.ExpressionEditor.Models;

/// <summary>
/// 트리뷰 바인딩용 식 노드 — INotifyPropertyChanged 자동 (CommunityToolkit).
/// 외부 (FbInputExpr / CallCondition) 와 양방향 변환은 별도 Converter 가 담당.
///
/// • Symbol: Var/NegVar/Rising/Falling/Raw 일 때만 의미. And/Or/Not 은 빈 문자열.
/// • RefKey: 모델 엔티티에 바인딩될 때만 Some (예: ApiCall.Id). 자유 입력 변수는 null.
///   - Pre-FB 컨텍스트: 항상 null (문자열 변수명만 사용).
///   - Call 조건 컨텍스트: ApiCall 참조 시 Guid.
/// • Children: And/Or/Not 의 자식 노드들. Var 류는 빈 컬렉션.
/// </summary>
public partial class ExprNode : ObservableObject
{
    [ObservableProperty]
    private ExprKind _kind = ExprKind.Var;

    [ObservableProperty]
    private string _symbol = "";

    [ObservableProperty]
    private Guid? _refKey;

    public ObservableCollection<ExprNode> Children { get; } = new();

    /// <summary>역직렬화/Clone 시 OnKindChanged 자동 보정을 차단하기 위한 플래그.
    /// Converter 가 Kind 를 설정한 뒤 Children 을 별도 채우므로, 초기 로드 시 spurious Var 자식 추가 방지.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SuppressKindMigration { get; set; }

    public ExprNode() { }

    public ExprNode(ExprKind kind, string symbol = "", Guid? refKey = null)
    {
        Kind   = kind;
        Symbol = symbol ?? "";
        RefKey = refKey;
    }

    /// <summary>편의 생성자 — And/Or/Not 의 children 일괄 추가.</summary>
    public static ExprNode Combinator(ExprKind kind, params ExprNode[] children)
    {
        var n = new ExprNode { Kind = kind };
        foreach (var c in children) n.Children.Add(c);
        return n;
    }

    /// <summary>깊은 복사 — Undo / 다이얼로그 취소 시 원복.</summary>
    public ExprNode Clone()
    {
        var copy = new ExprNode { SuppressKindMigration = true };
        copy.Kind   = Kind;
        copy.Symbol = Symbol;
        copy.RefKey = RefKey;
        foreach (var c in Children) copy.Children.Add(c.Clone());
        copy.SuppressKindMigration = false;
        return copy;
    }

    /// <summary>Kind 변경 시 자식 정합성 자동 보정 — leaf↔combinator 전환에 따라 자식 추가/제거.
    /// CommunityToolkit 가 [ObservableProperty] 로 자동 생성한 setter 가 호출.
    /// SuppressKindMigration=true 이면 (역직렬화/Clone 등) 보정 스킵.</summary>
    partial void OnKindChanged(ExprKind oldValue, ExprKind newValue)
    {
        if (SuppressKindMigration) return;

        bool oldHasKids = oldValue.HasChildren();
        bool newHasKids = newValue.HasChildren();
        if (newHasKids && !oldHasKids)
        {
            // leaf → combinator: 기본 Var 자식 1개 추가, Symbol 비움
            Symbol = "";
            if (Children.Count == 0)
                Children.Add(new ExprNode(ExprKind.Var));
        }
        else if (!newHasKids && oldHasKids)
        {
            // combinator → leaf: 자식 제거, Symbol 보존
            Children.Clear();
        }
        else if (newValue == ExprKind.Not && Children.Count > 1)
        {
            // Not 은 자식 1개만
            while (Children.Count > 1) Children.RemoveAt(1);
        }

        // Not 은 항상 자식 1개 유지
        if (newValue == ExprKind.Not && Children.Count == 0)
            Children.Add(new ExprNode(ExprKind.Var));
    }
}
