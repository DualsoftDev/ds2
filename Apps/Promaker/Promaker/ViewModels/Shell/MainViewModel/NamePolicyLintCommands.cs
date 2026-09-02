using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core.Store;
using Ds2.Editor;
using Promaker.Services;

namespace Promaker.ViewModels;

/// <summary>
/// 이름 정책 열기 린트 — 하단 상태줄 우측의 상시 배지("⚠ 이름 정책 경고 N건")와 정리 다이얼로그.
/// 좌측 StatusText 는 일회성 로그라 다음 액션에 덮이므로, 경고는 별도 배지로 상시 노출한다
/// (해결되어야만 사라짐 = 침묵화 방지). 자동 변환은 하지 않는다 — 사용자 명시 실행만.
/// 재계산 시점: 프로젝트 열기 완료(CompleteOpen) · 개명 이벤트 · store 교체(undo/redo/임포트) · 정리 적용 후.
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    private int _namePolicyIssueCount;

    public void RefreshNamePolicyLint()
    {
        try
        {
            NamePolicyIssueCount = HasProject ? NamePolicyLint.Scan(_store).Count : 0;
        }
        catch
        {
            // 린트는 진단 보조 — 스캔 실패가 편집을 방해해선 안 된다.
            NamePolicyIssueCount = 0;
        }
    }

    [RelayCommand]
    private void OpenNamePolicyLint()
    {
        if (!HasProject) return;

        System.Collections.Generic.List<NamePolicyIssue> issues;
        try
        {
            issues = NamePolicyLint.Scan(_store);
        }
        catch (Exception ex)
        {
            StatusText = $"이름 정책 검사 실패: {ex.Message}";
            return;
        }

        if (issues.Count == 0)
        {
            NamePolicyIssueCount = 0;
            StatusText = "이름 정책 경고가 없습니다.";
            return;
        }

        var dialog = new Dialogs.NamePolicyLintDialog(issues);
        if (_dialogService.ShowDialog(dialog) != true)
        {
            RefreshNamePolicyLint();
            return;
        }

        if (!GuardSimulationSemanticEdit("이름 정책 일괄 변환"))
            return;

        // System → Flow → Work 순서 — Flow 개명의 Work.FlowPrefix cascade 가 하위 재계산에
        // 반영되도록. 각 항목은 적용 직전 재계산(이미 해결된 항목은 건너뜀 — 스냅샷 stale 방지).
        int applied = 0, skipped = 0;
        var ordered = dialog.SelectedRows.OrderBy(r => r.Kind switch
        {
            EntityKind.System => 0,
            EntityKind.Flow => 1,
            _ => 2,
        });
        foreach (var row in ordered)
        {
            var newName = NamePolicyLint.RecomputeSuggested(_store, row.Kind, row.EntityId);
            if (newName is null)
            {
                skipped++;
                continue;
            }
            if (TryEditorAction(() => _store.RenameEntitySmart(row.EntityId, row.Kind, newName)))
                applied++;
        }

        RefreshNamePolicyLint();
        StatusText = skipped > 0
            ? $"이름 정책 정리: {applied}건 변환됨 ({skipped}건은 이미 해결되어 건너뜀)"
            : $"이름 정책 정리: {applied}건 변환됨";
    }
}
