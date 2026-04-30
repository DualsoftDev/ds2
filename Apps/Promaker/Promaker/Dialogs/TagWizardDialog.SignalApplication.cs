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
    private void ApplyPatterns_Click(object sender, RoutedEventArgs e)
    {
        var confirm = DialogHelpers.ShowThemedMessageBox(
            $"프리뷰의 모든 패턴을 {_ioRows.Count}개 ApiCall 의 InTag/OutTag 에 적용합니다.\n\n" +
            "⚠ 현재 ApiCall 에 수동으로 설정된 이름과 주소가 모두 덮어써집니다.\n" +
            "   (I/O 일괄 편집에서 개별 수정한 항목 포함)\n\n" +
            "계속하시겠습니까?",
            "패턴 적용 확인",
            MessageBoxButton.YesNo,
            "⚠");
        if (confirm != MessageBoxResult.Yes) return;

        if (ApplySignals())
        {
            DialogHelpers.ShowThemedMessageBox(
                $"✓ {_successCount}개 ApiCall 에 패턴이 적용되었습니다.\n\n" +
                $"이후 I/O 일괄 편집에서 개별 수정한 값은 이 적용으로 손실될 수 있으며,\n" +
                $"필요 시 다시 '패턴 적용' 을 눌러야 합니다.",
                "패턴 적용 완료",
                MessageBoxButton.OK,
                "✓");
        }
    }

    private bool ApplySignals()
    {
        if (_ioRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox("적용할 IO 신호가 없습니다.",
                "TAG Wizard", MessageBoxButton.OK, "⚠");
            return false;
        }

        var validRows = _ioRows.Where(r => r.CallId != System.Guid.Empty && r.ApiCallId != System.Guid.Empty).ToList();
        var unmatchedCount = _unmatchedRows.Count;

        if (unmatchedCount > 0)
        {
            var ok = DialogHelpers.ShowThemedMessageBox(
                $"⚠ {unmatchedCount}개 항목이 DS2 모델과 매칭되지 않았습니다.\n\n" +
                $"'매칭 실패' 탭에서 상세 내역을 확인할 수 있습니다.\n\n" +
                $"✓ 매칭된 {validRows.Count}개 항목만 적용됩니다.\n\n" +
                $"계속하시겠습니까?",
                "TAG Wizard - 확인", MessageBoxButton.YesNo, "?");
            if (ok != MessageBoxResult.Yes) return false;
        }

        if (validRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "DS2 모델과 매칭되는 항목이 없습니다.\n\n" +
                "Flow, Device, Api 이름이 정확히 일치하는지 확인하세요.",
                "TAG Wizard", MessageBoxButton.OK, "⚠");
            return false;
        }

        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "적용 중...";

            var applyResult = IoTagApplier.Apply(_store, validRows);
            _successCount = applyResult.SuccessCount;

            var summary = new StringBuilder();
            summary.AppendLine($"✅ {_successCount}개 ApiCall에 IO 태그가 성공적으로 적용되었습니다.");
            summary.AppendLine($"📊 IO 신호: {_ioRows.Count}개");
            summary.AppendLine($"📊 Dummy 신호: {_dummyRows.Count}개");

            if (applyResult.AnyFailed)
            {
                summary.AppendLine();
                summary.AppendLine($"⚠️ {applyResult.FailedCount}개 항목 적용 실패:");
                foreach (var item in applyResult.FailedItems.Take(3))
                    summary.AppendLine($"  • {item}");
                if (applyResult.FailedCount > 3)
                    summary.AppendLine($"  ... 외 {applyResult.FailedCount - 3}개");
            }

            CompletionSummaryText.Text = summary.ToString();
            return _successCount > 0;
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
