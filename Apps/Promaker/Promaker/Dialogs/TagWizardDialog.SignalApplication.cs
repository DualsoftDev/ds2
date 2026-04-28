using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Editor;
using Plc.Xgi;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 신호 적용 + 오류 표시
/// </summary>
public partial class TagWizardDialog
{
    /// <summary>
    /// "패턴 적용" 버튼 클릭 핸들러 — 명시적 동의 후 ApiCall 에 일괄 덮어쓰기.
    /// IoBatch 에서 수동 설정한 이름이 있을 수 있으므로 반드시 경고 표시.
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

    /// <summary>
    /// 신호 적용 — 프리뷰의 각 IoRow 를 ApiCall.InTag/OutTag 에 덮어쓴다.
    /// 자동 호출 금지 — 반드시 ApplyPatterns_Click 또는 사용자 명시적 트리거로만 호출.
    /// </summary>
    private bool ApplySignals()
    {
        if (_ioRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "적용할 IO 신호가 없습니다.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        var validRows = _ioRows.Where(r => r.ApiCallId != Guid.Empty && r.CallId != Guid.Empty).ToList();
        var unmatchedCount = _unmatchedRows.Count;

        if (unmatchedCount > 0)
        {
            var result = DialogHelpers.ShowThemedMessageBox(
                $"⚠ {unmatchedCount}개 항목이 DS2 모델과 매칭되지 않았습니다.\n\n" +
                $"'매칭 실패' 탭에서 상세 내역을 확인할 수 있습니다.\n\n" +
                $"✓ 매칭된 {validRows.Count}개 항목만 적용됩니다.\n\n" +
                $"계속하시겠습니까?",
                "TAG Wizard - 확인",
                MessageBoxButton.YesNo,
                "?");

            if (result != MessageBoxResult.Yes)
                return false;
        }

        if (validRows.Count == 0)
        {
            DialogHelpers.ShowThemedMessageBox(
                "DS2 모델과 매칭되는 항목이 없습니다.\n\n" +
                "Flow, Device, Api 이름이 정확히 일치하는지 확인하세요.",
                "TAG Wizard",
                MessageBoxButton.OK,
                "⚠");
            return false;
        }

        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "적용 중...";

            _successCount = 0;
            var failedItems = new List<string>();

            foreach (var row in validRows)
            {
                try
                {
                    _store.UpdateApiCallIoTags(
                        row.CallId,
                        row.ApiCallId,
                        new IOTag(row.OutSymbol ?? "", row.OutAddress ?? "", ""),
                        new IOTag(row.InSymbol ?? "", row.InAddress ?? "", ""));

                    _successCount++;
                }
                catch (Exception ex)
                {
                    failedItems.Add($"{row.Flow}/{row.Device}/{row.Api}: {ex.Message}");
                }
            }

            // 완료 메시지 구성
            var summary = new StringBuilder();
            summary.AppendLine($"✅ {_successCount}개 ApiCall에 IO 태그가 성공적으로 적용되었습니다.");
            summary.AppendLine($"📊 IO 신호: {_ioRows.Count}개");
            summary.AppendLine($"📊 Dummy 신호: {_dummyRows.Count}개");

            if (failedItems.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine($"⚠️ {failedItems.Count}개 항목 적용 실패:");
                foreach (var item in failedItems.Take(3))
                {
                    summary.AppendLine($"  • {item}");
                }
                if (failedItems.Count > 3)
                {
                    summary.AppendLine($"  ... 외 {failedItems.Count - 3}개");
                }
            }

            CompletionSummaryText.Text = summary.ToString();

            return _successCount > 0;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"IO 태그 적용 중 오류가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// 오류를 다이얼로그 탭에 표시
    /// </summary>
    private void DisplayErrors(GenerationResult result)
    {
        _errorItems.Clear();

        // 오류를 그룹화하고 표시
        var errorGroups = result.Errors
            .GroupBy(e => e.ErrorType)
            .OrderBy(g => g.Key);

        foreach (var group in errorGroups)
        {
            var errorType = FormatErrorType(group.Key);
            var messages = string.Join("\n", group.Select(e => e.Message).Distinct());
            var count = group.Count();

            _errorItems.Add(new ErrorDisplayItem
            {
                ErrorType = $"{errorType} ({count}개)",
                Message = messages
            });
        }

        // 오류 탭 표시
        if (_errorItems.Count > 0)
        {
            ErrorsTabItem.Visibility = Visibility.Visible;
            ErrorCountText.Text = result.Errors.Count().ToString();
        }
        else
        {
            ErrorsTabItem.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 오류 타입을 사용자 친화적 문자열로 변환
    /// </summary>
    private string FormatErrorType(ErrorType errorType)
    {
        return errorType switch
        {
            ErrorType.TemplateNotFound => "템플릿 파일 없음",
            ErrorType.ApiDefNotInTemplate => "API가 템플릿에 정의되지 않음",
            _ => errorType.ToString()
        };
    }
}
