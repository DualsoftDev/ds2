using System.Windows;
using AAStoPLC.LadderEditor.Expression;
using Promaker.Controls.ExpressionEditor.Providers;
using Promaker.Controls.ExpressionEditor.ViewModels;

namespace Promaker.Controls.ExpressionEditor.Views;

/// <summary>
/// 식 편집 모달 — 다양한 컨텍스트에서 재사용 (Pre-FB / Call 조건 등).
/// 사용:
///   var dlg = new ExpressionEditorWindow(rootClone, provider) { Owner = this };
///   if (dlg.ShowDialog() == true) save dlg.Result;
/// </summary>
public partial class ExpressionEditorWindow : Window
{
    private readonly ExpressionEditorViewModel _vm;

    /// <summary>편집 결과 — 확인 시 사용자가 편집한 노드 트리 (live).</summary>
    public ExprNode? Result { get; private set; }

    public ExpressionEditorWindow(ExprNode? initial, IExpressionSymbolProvider provider, string? title = null)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(title)) Title = title;
        // 외부 변경 격리 — 작업 사본으로 편집, 확인 시에만 외부에 반영.
        var working = initial?.Clone() ?? new ExprNode(ExprKind.Var);
        _vm = new ExpressionEditorViewModel(working, provider);
        EditorView.DataContext = _vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result = _vm.Root;
        DialogResult = true;
    }
}
