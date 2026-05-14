using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AAStoPLC.Ir;
using AAStoPLC.LadderEditor.Expression;
using AAStoPLC.LadderEditor.Interaction;
using AAStoPLC.LadderEditor.Models;
using AAStoPLC.LadderEditor.Rendering;
using Promaker.Controls.ExpressionEditor.ViewModels;
using Promaker.Presentation;

namespace Promaker.Controls.ExpressionEditor.Views;

/// <summary>
/// 식 편집기 — 트리 입력 UI 제거 후 LadderEditor 단독.
/// VM 의 Root(ExprNode) 와 LadderEditor 의 단일 CoilRung 양방향 동기.
///   • VM PropertyChanged → LadderEditor 의 Condition 갱신
///   • LadderEditor RungEdited → ExprNode 트리 역변환 후 VM.Root 교체
/// </summary>
public partial class ExpressionEditorView : UserControl
{
    private readonly ObservableCollection<RungViewModel> _rungs = new();
    private readonly EditorContext _ldCtx = new() { GridCols = 14 };
    private CoilRungViewModel? _previewRung;
    private bool _suppressEditFeedback;

    public ExpressionEditorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        LdView.Context = _ldCtx;
        LdView.Rungs   = _rungs;
        LdView.Theme   = ThemeManager.CurrentTheme == AppTheme.Dark
            ? new DefaultDarkTheme() : new DefaultLightTheme();
        ThemeManager.ThemeChanged += SyncTheme;
        Unloaded += (_, _) => ThemeManager.ThemeChanged -= SyncTheme;
        if (LdView.Edit is { } edit) edit.RungEdited += OnLadderEdited;
        RefreshLd();
    }

    private void SyncTheme(AppTheme theme) =>
        LdView.Theme = theme == AppTheme.Dark ? new DefaultDarkTheme() : new DefaultLightTheme();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ExpressionEditorViewModel oldVm)
            oldVm.PropertyChanged -= Vm_PropertyChanged;
        if (e.NewValue is ExpressionEditorViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
            _ldCtx.SymbolProvider = new EditorSymbolProvider(newVm.SymbolProvider);
            RefreshLd();
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressEditFeedback) return;
        if (e.PropertyName is nameof(ExpressionEditorViewModel.LdLayoutForPreview)
            or nameof(ExpressionEditorViewModel.StPreview))
            RefreshLd();
    }

    private void RefreshLd()
    {
        if (DataContext is not ExpressionEditorViewModel vm) return;
        var cond = CoilConditionConverter.ToCoilCondition(vm.Root);
        // 빈 식 (AlwaysTrue) → 사용자가 클릭/드래그할 대상이 없음. 기본 빈 contact 1개 표시.
        if (cond is null || cond.IsAlwaysTrue) cond = CoilCondition.NewVar("");
        if (_previewRung is null)
        {
            _previewRung = new CoilRungViewModel(cond, "OUT");
            _rungs.Clear();
            _rungs.Add(_previewRung);
        }
        else
        {
            _previewRung.Condition = cond;
        }
    }

    private void OnLadderEdited(object? sender, RungEditedEventArgs args)
    {
        if (DataContext is not ExpressionEditorViewModel vm) return;
        if (_previewRung is null) return;
        var node = CoilConditionConverter.ToExprNode(_previewRung.Condition);
        if (node is null) return;
        _suppressEditFeedback = true;
        try { vm.Root = node; }
        finally { _suppressEditFeedback = false; }
    }

    /// <summary>ExpressionEditor 의 IExpressionSymbolProvider 를 LadderEditor 의 ISymbolProvider 로 어댑트.
    /// 항상 `_ON` / `_OFF` 기본 심볼 포함 (provider 비어도 popup 에 보이도록).</summary>
    private sealed class EditorSymbolProvider : AAStoPLC.LadderEditor.Models.ISymbolProvider
    {
        private static readonly string[] BuiltIn = { "_ON", "_OFF" };
        private readonly Promaker.Controls.ExpressionEditor.Providers.IExpressionSymbolProvider _inner;
        private readonly System.Collections.Generic.HashSet<string> _known;
        private readonly System.Collections.Generic.List<string> _candidates;

        public EditorSymbolProvider(Promaker.Controls.ExpressionEditor.Providers.IExpressionSymbolProvider inner)
        {
            _inner = inner;
            _candidates = BuiltIn
                .Concat(inner.GetCandidates().Select(c => c.Display))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            _known = new(_candidates, System.StringComparer.OrdinalIgnoreCase);
        }

        public System.Collections.Generic.IEnumerable<string> Search(string prefixOrSubstring) =>
            string.IsNullOrEmpty(prefixOrSubstring)
                ? _candidates
                : _candidates.Where(s => s.IndexOf(prefixOrSubstring, System.StringComparison.OrdinalIgnoreCase) >= 0);

        public bool IsKnown(string symbol) => _known.Contains(symbol);
    }
}
