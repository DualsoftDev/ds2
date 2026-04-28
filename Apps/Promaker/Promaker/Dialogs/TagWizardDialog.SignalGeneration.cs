using System;
using System.Windows;
using Plc.Xgi;
using Promaker.Services;

namespace Promaker.Dialogs;

/// <summary>
/// 신호 생성 — 파이프라인 호출 + 결과를 IoSignalPipeline 으로 행 변환.
/// 행 변환/매칭/Dummy 변환 로직은 모두 Services/IoSignalPipeline 에 위치.
/// </summary>
public partial class TagWizardDialog
{
    private bool GenerateSignals()
    {
        try
        {
            NextButton.IsEnabled = false;
            NextButton.Content = "생성 중...";

            // AASX 내 Preset 데이터를 휘발성 임시 디렉토리에 emit 후 호출 → 즉시 삭제.
            using var tempDir = PresetToTempTemplateDir.Materialize(_store);
            var result = _generator.Generate(_store, tempDir.Path);

            if (!_generator.IsSuccess(result))
            {
                DisplayErrors(result);
                return true; // Step 3 로 이동하여 오류 탭 표시
            }

            _ioRows.Clear();
            _dummyRows.Clear();
            _unmatchedRows.Clear();

            foreach (var row in IoSignalPipeline.BuildIoBatchRows(result, _store))
                _ioRows.Add(row);

            foreach (var row in IoSignalPipeline.BuildDummyRows(result))
                _dummyRows.Add(row);

            foreach (var u in IoSignalPipeline.ClassifyUnmatched(_ioRows))
                _unmatchedRows.Add(u);

            UpdateUnmatchedTab();

            var unmatched = _unmatchedRows.Count;
            GenerationStatusText.Text = unmatched > 0
                ? $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개 생성 | ⚠ 매칭 실패 {unmatched}개"
                : $"✅ IO 신호 {_ioRows.Count}개, Dummy 신호 {_dummyRows.Count}개가 생성되었습니다. 모든 신호가 매칭되었습니다.";

            return true;
        }
        catch (Exception ex)
        {
            DialogHelpers.ShowThemedMessageBox(
                $"신호 생성 중 예외가 발생했습니다:\n\n{ex.Message}",
                "TAG Wizard - 오류",
                MessageBoxButton.OK,
                "✖");
            return false;
        }
        finally
        {
            NextButton.IsEnabled = true;
            NextButton.Content = "적용 ▶";
        }
    }

    private void UpdateUnmatchedTab()
    {
        if (_unmatchedRows.Count > 0)
        {
            UnmatchedTabItem.Visibility = Visibility.Visible;
            UnmatchedCountText.Text = _unmatchedRows.Count.ToString();
        }
        else
        {
            UnmatchedTabItem.Visibility = Visibility.Collapsed;
        }
    }
}
