using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Promaker.Dialogs;

public partial class TagWizardDialog
{
    /// <summary>조건식 컬럼 셀 클릭 → 공용 식 편집기 열기.
    /// Pre-FB 조건 편집 — 현 SystemType 의 IW/QW/MW Pattern 을 변수 후보로 제공.</summary>
    private void EditPreFbCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SignalPatternRow row) return;
        var sysType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(sysType))
        {
            DialogHelpers.ShowThemedMessageBox("SystemType 을 먼저 선택하세요.", "TAG Wizard",
                MessageBoxButton.OK, "⚠");
            return;
        }
        var provider = new Promaker.Controls.ExpressionEditor.Providers.SignalPatternSymbolProvider(_store, sysType);
        var initial = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(row.PreFbCondition);
        var dlg = new Promaker.Controls.ExpressionEditor.Views.ExpressionEditorWindow(initial, provider,
            $"Pre-FB 조건 편집 — {row.TargetFBPort}") { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            // Var 빈 노드만 있으면 조건 제거 (간소화)
            var node = dlg.Result;
            if (node.Kind == AAStoPLC.LadderEditor.Expression.ExprKind.Var
                && string.IsNullOrWhiteSpace(node.Symbol)
                && node.Children.Count == 0)
                row.PreFbCondition = null;
            else
                row.PreFbCondition = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(node);
            // 자동저장 — PreFbCondition setter 가 PropertyChanged 발화 → HookAutoSave 가 PersistCurrentPreset.
        }
    }

    /// <summary>AUX 포트 콤보의 입력/출력/전체 필터 라디오 변경 핸들러.</summary>
    /// <summary>AUX 포트 entry 의 조건식 편집 — 공용 식 편집기 열기.</summary>
    private void EditAuxPortCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not AuxPortRow row) return;
        var sysType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(sysType))
        {
            DialogHelpers.ShowThemedMessageBox("SystemType 을 먼저 선택하세요.", "TAG Wizard",
                MessageBoxButton.OK, "⚠");
            return;
        }
        var provider = new Promaker.Controls.ExpressionEditor.Providers.SignalPatternSymbolProvider(_store, sysType);
        var initial = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.FromCore(row.Condition);
        var dlg = new Promaker.Controls.ExpressionEditor.Views.ExpressionEditorWindow(initial, provider,
            $"AUX 조건식 — {row.ApiName} → {row.TargetFBPort}") { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            var node = dlg.Result;
            if (node.Kind == AAStoPLC.LadderEditor.Expression.ExprKind.Var
                && string.IsNullOrWhiteSpace(node.Symbol)
                && node.Children.Count == 0)
                row.Condition = null;
            else
                row.Condition = Promaker.Controls.ExpressionEditor.Converters.FbInputExprConverter.ToCore(node);
        }
    }

    /// <summary>AUX 그리드 선택 트리거 셀 — 클릭 시 행 선택 (Ctrl/Shift 지원).</summary>
    private void AuxRowSelector_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not AuxPortRow row) return;
        if (AuxPortGrid == null) return;
        bool ctrl  = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift)   != 0;
        if (ctrl)
        {
            if (AuxPortGrid.SelectedItems.Contains(row)) AuxPortGrid.SelectedItems.Remove(row);
            else AuxPortGrid.SelectedItems.Add(row);
        }
        else if (shift && AuxPortGrid.SelectedItem is AuxPortRow anchor)
        {
            int a = _auxPortRows.IndexOf(anchor), b = _auxPortRows.IndexOf(row);
            if (a >= 0 && b >= 0)
            {
                int lo = System.Math.Min(a, b), hi = System.Math.Max(a, b);
                AuxPortGrid.SelectedItems.Clear();
                for (int i = lo; i <= hi; i++) AuxPortGrid.SelectedItems.Add(_auxPortRows[i]);
            }
        }
        else
        {
            AuxPortGrid.SelectedItems.Clear();
            AuxPortGrid.SelectedItems.Add(row);
        }
        AuxPortGrid.Focus();
        e.Handled = true;
    }

    /// <summary>탭 변경 — AUX 포트 탭 활성화 시 심볼 후보를 IW/QW/MW 의 최신 패턴으로 재스냅샷.</summary>
    private void SignalSectionTab_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (e.Source != SignalSectionTabControl) return;
        if (SignalSectionTabControl.SelectedItem is not System.Windows.Controls.TabItem tab) return;
        var header = tab.Header as string ?? "";
        if (!header.Contains("AUX")) return;
        var sysType = _currentDeviceTemplateFile;
        if (string.IsNullOrWhiteSpace(sysType)) return;
        var fresh = BuildAuxApiOptions(sysType);
        foreach (var row in _auxPortRows)
        {
            row.ApiOptions = fresh;
            row.RaisePropertyChanged(nameof(AuxPortRow.ApiOptions));
        }
    }

    /// <summary>AUX 포트 그리드에 빈 행 추가 — 사용자가 한 API 에 여러 포트 매핑 가능.</summary>
    private void AddAuxPortRow_Click(object sender, RoutedEventArgs e)
    {
        var fb = GlobalFBTypeCombo?.SelectedItem as string ?? "";
        var sysType = _currentDeviceTemplateFile;
        _auxPortRows.Add(HookAutoSave(new AuxPortRow
        {
            ApiName = "",
            TargetFBType = fb,
            TargetFBPort = "",
            Kind = "DirectFB",
            AuxKind = "AutoAux",
            ApiOptions = BuildAuxApiOptions(sysType),
        }));
        PersistCurrentPreset();
    }

    /// <summary>선택 행 삭제.</summary>
    private void RemoveAuxPortRow_Click(object sender, RoutedEventArgs e)
    {
        if (AuxPortGrid == null) return;
        var selected = AuxPortGrid.SelectedItems.Cast<AuxPortRow>().ToList();
        if (selected.Count == 0) return;
        foreach (var row in selected) _auxPortRows.Remove(row);
        PersistCurrentPreset();
    }

    private void MoveAuxRowUp_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(AuxPortGrid, _auxPortRows, up: true);
    private void MoveAuxRowDown_Click(object sender, RoutedEventArgs e) =>
        MoveSelected(AuxPortGrid, _auxPortRows, up: false);
}
