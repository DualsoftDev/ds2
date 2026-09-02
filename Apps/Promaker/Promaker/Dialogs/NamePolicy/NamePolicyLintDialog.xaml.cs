using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Ds2.Core.Store;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 이름 정책 열기 린트 정리 다이얼로그 — 위반 이름 목록(현재 → 변환 미리보기)을 보여주고
/// 사용자가 체크한 항목만 [선택 항목 변환]으로 확정한다(DialogResult=true).
/// 실제 개명 실행은 호출자(MainViewModel)가 적용 직전 재계산(RecomputeSuggested)으로 수행 —
/// 다이얼로그가 떠 있는 동안 store 가 변했을 가능성(상위 개명 cascade)에 대비한다.
/// </summary>
public partial class NamePolicyLintDialog : Window
{
    /// <summary>표시/선택 행. 표시 이름은 스캔 시점 스냅샷 — 적용 시 재계산된다.</summary>
    public sealed class Row
    {
        public bool IsSelected { get; set; } = true;
        public string KindLabel { get; init; } = "";
        public string CurrentName { get; init; } = "";
        public string SuggestedName { get; init; } = "";
        public EntityKind Kind { get; init; }
        public Guid EntityId { get; init; }
    }

    public List<Row> Rows { get; }

    internal NamePolicyLintDialog(IReadOnlyList<NamePolicyIssue> issues)
    {
        InitializeComponent();

        Rows = issues.Select(i => new Row
        {
            KindLabel = i.KindLabel,
            CurrentName = i.CurrentName,
            SuggestedName = i.SuggestedName,
            Kind = i.Kind,
            EntityId = i.Id,
        }).ToList();

        HeaderText.Text = $"이름 정책 경고 {Rows.Count}건";
        IssueGrid.ItemsSource = Rows;
    }

    public List<Row> SelectedRows => Rows.Where(r => r.IsSelected).ToList();

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                this, "변환할 항목이 선택되지 않았습니다.", "이름 정책 정리",
                MessageBoxButton.OK, DialogHelpers.IconInfo);
            return;
        }
        DialogResult = true;
    }
}
