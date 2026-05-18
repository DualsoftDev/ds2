using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using AAStoPLC.TagWizard;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.Dialogs;

public partial class TagInspectorDialog
{
    /// <summary>
    /// QueryResult 의 진단을 ViewModel 로 옮기고, 영향 행에 HasError 플래그를 매핑.
    /// 진단이 없으면 패널을 자동으로 닫는다.
    /// </summary>
    private void ApplyDiagnostics(IReadOnlyList<DiagnosticItem> diagnostics)
    {
        _diagnostics.Clear();

        // 행별 인덱스 — (CallId, ApiCallId) → IoBatchRow
        var rowIndex = new Dictionary<(Guid, Guid), IoBatchRow>();
        foreach (var r in _rows)
        {
            r.HasError = false;
            rowIndex[(r.CallId, r.ApiCallId)] = r;
        }

        foreach (var d in diagnostics)
        {
            var matched = new List<IoBatchRow>();
            foreach (var key in d.AffectedRows)
            {
                if (rowIndex.TryGetValue((key.CallId, key.ApiCallId), out var row))
                {
                    matched.Add(row);
                    if (d.Severity == DiagnosticSeverity.Error)
                        row.HasError = true;
                }
            }

            _diagnostics.Add(new DiagnosticItemViewModel(d, matched, _openFBTagMapEdit != null));
        }

        // 진단 없으면 패널 닫기. 있으면 헤더 텍스트만 갱신 (사용자가 직접 칩 클릭으로 열도록 둠).
        if (_diagnostics.Count == 0)
        {
            DiagnosticsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            UpdateDiagnosticsHeader();
        }
    }

    private void UpdateDiagnosticsHeader()
    {
        var parts = new List<string>();
        if (_errorCount > 0) parts.Add($"❌ 오류 {_errorCount}");
        if (_warningCount > 0) parts.Add($"⚠ 경고 {_warningCount}");
        DiagnosticsHeaderText.Text = parts.Count > 0
            ? $"진단 — {string.Join(" / ", parts)}"
            : "진단";
    }

    private void UpdateStatusChips()
    {
        TotalChip.Content = _view != null && _view.Cast<object>().Any()
            ? $"총 {_rows.Count}"
            : $"총 {_rows.Count}";

        if (_warningCount > 0)
        {
            WarningChip.Content = $"⚠ 경고 {_warningCount}";
            WarningChip.Visibility = Visibility.Visible;
        }
        else
        {
            WarningChip.Visibility = Visibility.Collapsed;
        }

        if (_errorCount > 0)
        {
            ErrorChip.Content = $"❌ 오류 {_errorCount}";
            ErrorChip.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorChip.Visibility = Visibility.Collapsed;
        }
    }

    private void DiagnosticsChip_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnostics.Count == 0) return;

        // 칩 클릭은 토글 — 이미 열려있으면 닫고, 닫혀있으면 연다.
        DiagnosticsPanel.Visibility = DiagnosticsPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void DiagnosticsClose_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    /// "FBTagMap 편집" 버튼 — 호출자가 주입한 액션으로 SystemType 을 넘기고,
    /// 그 액션(보통 TAG Wizard 모달)이 닫히면 IO 조회를 자동 새로고침해 결과를 즉시 반영.
    /// </summary>
    private void DiagnosticOpenFBTagMap_Click(object sender, RoutedEventArgs e)
    {
        if (_openFBTagMapEdit == null) return;
        if (sender is not Button { DataContext: DiagnosticItemViewModel vm }) return;

        _openFBTagMapEdit.Invoke(vm.SystemType);
        LoadFromStore();
    }

    private void DiagnosticGoToRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DiagnosticItemViewModel vm }) return;
        if (vm.MatchedRows.Count == 0) return;

        var first = vm.MatchedRows[0];

        // 필터 때문에 안 보일 수 있으니 필터를 임시로 비우는 대신, 일단 SelectedItem 만 세팅.
        // (사용자가 필터를 켠 상태에서 점프하길 원할 가능성도 있어 자동 해제는 보류.)
        IoGrid.SelectedItem = first;
        IoGrid.ScrollIntoView(first);
        IoGrid.Focus();
    }
}
