using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Promaker.Controls.ExpressionEditor.Converters;
using Promaker.Controls.ExpressionEditor.Models;
using Promaker.Controls.ExpressionEditor.Providers;

namespace Promaker.Controls.ExpressionEditor.ViewModels;

/// <summary>
/// 식 편집기 VM — 단일 Root + Provider + 미리보기 갱신.
/// 외부에서 Root 를 주입하고, 편집 후 Root 그대로 또는 Clone 으로 export.
/// </summary>
public partial class ExpressionEditorViewModel : ObservableObject
{
    private ExprNode _root;

    public ExprNode Root
    {
        get => _root;
        set
        {
            if (_root == value) return;
            DetachListeners(_root);
            _root = value;
            AttachListeners(_root);
            OnPropertyChanged();
            RefreshPreviews();
            SelectedNode = _root;
        }
    }

    public IExpressionSymbolProvider SymbolProvider { get; }

    [ObservableProperty]
    private ExprNode? _selectedNode;

    [ObservableProperty]
    private string _stPreview = "";

    public ObservableCollection<ExprNode> RootCollection { get; } = new();

    public Array AvailableKinds { get; } = Enum.GetValues(typeof(ExprKind));

    public ExpressionEditorViewModel(ExprNode root, IExpressionSymbolProvider provider)
    {
        SymbolProvider = provider ?? throw new ArgumentNullException(nameof(provider));
        _root = root ?? new ExprNode(ExprKind.Var);
        RootCollection.Add(_root);
        AttachListeners(_root);
        SelectedNode = _root;
        RefreshPreviews();
    }

    private void AttachListeners(ExprNode? node)
    {
        if (node == null) return;
        node.PropertyChanged += Node_PropertyChanged;
        node.Children.CollectionChanged += Children_CollectionChanged;
        foreach (var c in node.Children) AttachListeners(c);
    }

    private void DetachListeners(ExprNode? node)
    {
        if (node == null) return;
        node.PropertyChanged -= Node_PropertyChanged;
        node.Children.CollectionChanged -= Children_CollectionChanged;
        foreach (var c in node.Children) DetachListeners(c);
    }

    private void Node_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshPreviews();

    private void Children_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (ExprNode n in e.NewItems) AttachListeners(n);
        if (e.OldItems != null)
            foreach (ExprNode n in e.OldItems) DetachListeners(n);
        RefreshPreviews();
    }

    public void RefreshPreviews()
    {
        StPreview = CoilConditionConverter.ToStPreview(_root);
        OnPropertyChanged(nameof(LdLayoutForPreview));
    }

    /// <summary>LD 미리보기용 — CoilLayout 결과. View 가 Canvas 로 렌더.</summary>
    public AAStoPLC.Ir.LdLayoutResult LdLayoutForPreview =>
        AAStoPLC.Ir.CoilLayout.run(CoilConditionConverter.ToCoilCondition(_root));

    // ── 노드 트리 조작 ──────────────────────────────────────────────────────────

    private (ExprNode? parent, int idx) FindParent(ExprNode? node, ExprNode? root = null)
    {
        root ??= _root;
        if (node == null || root == null || ReferenceEquals(node, root)) return (null, -1);
        for (int i = 0; i < root.Children.Count; i++)
        {
            if (ReferenceEquals(root.Children[i], node)) return (root, i);
            var inner = FindParent(node, root.Children[i]);
            if (inner.parent != null) return inner;
        }
        return (null, -1);
    }

    [RelayCommand]
    private void AddChild()
    {
        if (SelectedNode == null) return;
        if (SelectedNode.Kind == ExprKind.Not && SelectedNode.Children.Count >= 1) return;

        // Leaf 노드면 자동으로 AND 결합자로 wrap — 기존 leaf + 새 Var 자식을 묶음.
        if (!SelectedNode.Kind.HasChildren())
        {
            var clonedSelf = SelectedNode.Clone();
            // 현재 노드를 AND 로 변환하고 자기 자신의 복제본 + 새 Var 를 자식으로
            SelectedNode.Kind = ExprKind.And;
            SelectedNode.Symbol = "";
            SelectedNode.Children.Clear();
            SelectedNode.Children.Add(clonedSelf);
            var added = new ExprNode(ExprKind.Var);
            SelectedNode.Children.Add(added);
            SelectedNode = added;
            return;
        }

        var child = new ExprNode(ExprKind.Var);
        SelectedNode.Children.Add(child);
        SelectedNode = child;
    }

    [RelayCommand]
    private void AddSibling()
    {
        if (SelectedNode == null) return;
        var (parent, idx) = FindParent(SelectedNode);
        if (parent == null) return;
        var sib = new ExprNode(ExprKind.Var);
        parent.Children.Insert(idx + 1, sib);
        SelectedNode = sib;
    }

    [RelayCommand]
    private void Delete()
    {
        if (SelectedNode == null) return;
        if (ReferenceEquals(SelectedNode, _root))
        {
            // Root 삭제 → 빈 Var 노드로 치환
            DetachListeners(_root);
            _root.Kind = ExprKind.Var;
            _root.Symbol = "";
            _root.Children.Clear();
            AttachListeners(_root);
            RefreshPreviews();
            return;
        }
        var (parent, idx) = FindParent(SelectedNode);
        if (parent == null) return;
        parent.Children.RemoveAt(idx);
        SelectedNode = parent;
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (SelectedNode == null) return;
        var (parent, idx) = FindParent(SelectedNode);
        if (parent == null || idx <= 0) return;
        parent.Children.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (SelectedNode == null) return;
        var (parent, idx) = FindParent(SelectedNode);
        if (parent == null || idx < 0 || idx >= parent.Children.Count - 1) return;
        parent.Children.Move(idx, idx + 1);
    }

    /// <summary>선택 노드를 NOT(...) 으로 감싸기 — 기존 노드가 Not 의 유일한 자식이 됨.
    /// 예: NegVar W_X → Not(NegVar W_X), Var W_Y → Not(Var W_Y).</summary>
    [RelayCommand]
    private void WrapWithNot()
    {
        if (SelectedNode == null) return;
        WrapWith(ExprKind.Not);
    }

    /// <summary>선택 노드를 AND(...) 으로 감싸 새 형제 자리를 마련.</summary>
    [RelayCommand]
    private void WrapWithAnd() { if (SelectedNode != null) WrapWith(ExprKind.And); }

    /// <summary>선택 노드를 OR(...) 으로 감싸 새 형제 자리를 마련.</summary>
    [RelayCommand]
    private void WrapWithOr()  { if (SelectedNode != null) WrapWith(ExprKind.Or);  }

    private void WrapWith(ExprKind combinator)
    {
        if (SelectedNode == null) return;
        var clone = SelectedNode.Clone();

        if (ReferenceEquals(SelectedNode, _root))
        {
            // Root 인 경우 — 새 Root 를 combinator 로 만들어 children[0] = clone.
            DetachListeners(_root);
            _root = new ExprNode { SuppressKindMigration = true };
            _root.Kind = combinator;
            _root.Children.Add(clone);
            if (combinator != ExprKind.Not)
                _root.Children.Add(new ExprNode(ExprKind.Var));
            _root.SuppressKindMigration = false;
            RootCollection.Clear();
            RootCollection.Add(_root);
            AttachListeners(_root);
            SelectedNode = _root;
            RefreshPreviews();
            return;
        }

        var (parent, idx) = FindParent(SelectedNode);
        if (parent == null) return;
        var wrapped = new ExprNode { SuppressKindMigration = true };
        wrapped.Kind = combinator;
        wrapped.Children.Add(clone);
        if (combinator != ExprKind.Not)
            wrapped.Children.Add(new ExprNode(ExprKind.Var));
        wrapped.SuppressKindMigration = false;
        parent.Children[idx] = wrapped;
        SelectedNode = wrapped;
    }

    /// <summary>선택 노드 종류 변경 — Kind setter 의 OnKindChanged 가 자식 정합성 자동 보정.</summary>
    public void ChangeKind(ExprKind newKind)
    {
        if (SelectedNode == null) return;
        SelectedNode.Kind = newKind;
    }
}
