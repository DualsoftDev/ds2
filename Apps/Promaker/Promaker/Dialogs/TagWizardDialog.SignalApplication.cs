using System.Linq;
using System.Text;
using System.Windows;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 신호 적용 + 오류 표시 — 적용 루프는 IoTagApplier 위임.
/// </summary>
public partial class TagWizardDialog
{
    /// <summary>
    /// "패턴 적용" 버튼 — 명시적 동의 후 ApiCall 에 일괄 덮어쓰기.
    /// </summary>
    private void ApplyPatterns_Click(object sender, RoutedEventArgs e) => ConfirmAndApplyPatterns();

    /// <summary>
    /// 단일 확인 → 적용 → 단일 통합 리포트.
    /// 다이얼로그는 사전 확인 1번 + 사후 통합 리포트 1번 (총 최대 2회).
    /// 통합 리포트 안에서 Call 구조 마이그레이션도 같이 분기 처리.
    /// </summary>
    private bool ConfirmAndApplyPatterns()
    {
        if (_ioRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("적용할 IO 신호가 없습니다.",
                "TAG Wizard", MessageBoxButton.OK, "⚠");
            return false;
        }

        var validRows = _ioRows
            .Where(r => r.CallId != System.Guid.Empty && r.ApiCallId != System.Guid.Empty)
            .ToList();
        if (validRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "DS2 모델과 매칭되는 항목이 없습니다.\n\n" +
                "Flow, Device, Api 이름이 정확히 일치하는지 확인하세요.",
                "TAG Wizard", MessageBoxButton.OK, "⚠");
            return false;
        }

        // 사전 확인 — unmatched/덮어쓰기 경고를 한 화면에 통합.
        var unmatchedCount = _unmatchedRows.Count;
        var preMsg = new StringBuilder();
        preMsg.AppendLine($"매칭된 {validRows.Count}개 ApiCall 의 InTag/OutTag 에 패턴을 적용합니다.");
        if (unmatchedCount > 0)
        {
            preMsg.AppendLine();
            preMsg.AppendLine($"⚠ {unmatchedCount}개 항목이 DS2 모델과 매칭되지 않아 제외됩니다 ('매칭 실패' 탭 참조).");
        }
        preMsg.AppendLine();
        preMsg.AppendLine("⚠ 현재 ApiCall 에 수동으로 설정된 이름과 주소가 모두 덮어써집니다.");
        preMsg.AppendLine("   (I/O 일괄 편집에서 개별 수정한 항목 포함)");
        preMsg.AppendLine();
        preMsg.Append("계속하시겠습니까?");

        var confirm = DialogHelpers.ShowThemedMessageBox(
            preMsg.ToString(), "패턴 적용 확인", MessageBoxButton.YesNo, "⚠");
        if (confirm != MessageBoxResult.Yes) return false;

        // 적용 실행.
        IoTagApplier.ApplyResult applyResult;
        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "적용 중...";
            applyResult = IoTagApplier.Apply(_store, validRows);
            _successCount = applyResult.SuccessCount;
        }
        catch (System.Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"IO 태그 적용 중 오류가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류", MessageBoxButton.OK, "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
        }

        if (_successCount <= 0) return false;

        // 사후 통합 리포트 — 신호 통계 + 적용 결과 + 검증 + Call 구조 위반 한꺼번에.
        ShowUnifiedApplyReport(applyResult, unmatchedCount);
        return true;
    }

    /// <summary>적용 후 통합 리포트 — 단일 다이얼로그. Call 구조 위반 시 마이그레이션 yes/no 분기 포함.</summary>
    private void ShowUnifiedApplyReport(IoTagApplier.ApplyResult applyResult, int unmatchedCount)
    {
        var summary = WizardSummaryBuilder.Build(
            _store, _successCount, _ioRows.Count, _dummyRows.Count, SignalTemplateWarnings);

        var msg = new StringBuilder();
        msg.AppendLine($"✓ {_successCount}개 ApiCall 에 패턴 적용 완료.");
        msg.AppendLine();
        msg.AppendLine("📊 신호 통계");
        msg.AppendLine(summary.SignalStats);

        if (applyResult.AnyFailed)
        {
            msg.AppendLine();
            msg.AppendLine($"⚠ 적용 실패 {applyResult.FailedCount}건:");
            foreach (var item in applyResult.FailedItems.Take(3))
                msg.AppendLine($"  • {item}");
            if (applyResult.FailedCount > 3)
                msg.AppendLine($"  … 외 {applyResult.FailedCount - 3}건");
        }
        if (unmatchedCount > 0)
        {
            msg.AppendLine();
            msg.AppendLine($"⚠ 매칭 실패 {unmatchedCount}건 — 적용 제외 ('매칭 실패' 탭 참조).");
        }

        msg.AppendLine();
        msg.AppendLine(summary.CompletionStatus);

        var hasViolations = summary.HasCallStructureViolations;
        if (hasViolations)
        {
            msg.AppendLine();
            msg.Append("Call 구조 마이그레이션을 지금 실행하시겠습니까?");
        }
        else
        {
            msg.AppendLine();
            msg.Append("필요 시 '패턴 적용' 을 다시 눌러 재적용할 수 있습니다.");
        }

        var btn = hasViolations ? MessageBoxButton.YesNo : MessageBoxButton.OK;
        var result = DialogHelpers.ShowThemedMessageBox(
            msg.ToString(), "TAG Wizard - 적용 결과", btn, "✓");

        if (hasViolations && result == MessageBoxResult.Yes)
        {
            var dlg = new MultiDeviceCallMigrationDialog(_store) { Owner = this };
            dlg.ShowDialog();
        }
    }

    /// <summary>오류 메시지 리스트를 다이얼로그 탭에 표시 (IoSignalPipeline 결과 기반).</summary>
    private void DisplayErrorsFromMessages(System.Collections.Generic.IReadOnlyList<string> messages)
    {
        _errorItems.Clear();
        foreach (var msg in messages.Distinct())
            _errorItems.Add(new ErrorDisplayItem { ErrorType = "오류", Message = msg });

        ErrorsTabItem.Visibility = _errorItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorCountText.Text = _errorItems.Count.ToString();
    }
}
