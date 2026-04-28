using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Ds2.Editor;
using Promaker.Dialogs;

namespace Promaker.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// I/O 조회 다이얼로그 열기 — 조회 전용 (편집은 TAG Wizard 에서만 수행).
    /// 매크로 슬롯 단위로 IO 신호를 재생성해 표시한다 (Step 3 와 동일한 결과).
    /// 따라서 ApiCall 수 가 아니라 매크로 정의 개수만큼 행이 표시된다.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenIoBatchDialog()
    {
        System.Collections.Generic.List<IoBatchRow> rows;
        try
        {
            using var tempDir = Promaker.Services.PresetToTempTemplateDir.Materialize(_store);
            var result = Plc.Xgi.IoListPipeline.generate(_store, tempDir.Path);
            rows = BuildIoBatchRows(result);
        }
        catch (System.Exception ex)
        {
            _dialogService.ShowWarning($"IO 신호 조회 중 오류: {ex.Message}");
            return;
        }

        if (rows.Count == 0)
        {
            _dialogService.ShowWarning("표시할 IO 신호가 없습니다. TAG Wizard 에서 시스템 타입과 매크로를 먼저 설정하세요.");
            return;
        }

        var dialog = new IoBatchSettingsDialog(_store, rows);
        _dialogService.ShowDialog(dialog);
    }

    /// <summary>
    /// IoListPipeline 결과를 IoBatchRow 리스트로 변환.
    /// 동일 (ApiCallId, Flow, Work, Call, Device=ApiDefName) 그룹 안에서 IW + QW 1쌍을 구성.
    /// 각 매크로 슬롯이 고유한 ApiDefName 을 가지므로 슬롯당 1행.
    /// </summary>
    private System.Collections.Generic.List<IoBatchRow> BuildIoBatchRows(Plc.Xgi.GenerationResult result)
    {
        var rows = new System.Collections.Generic.List<IoBatchRow>();

        // Api_None 신호: 신호 1개 = 행 1개 (API 컬럼 빈칸).
        foreach (var s in result.IoSignals.Where(IsApiNone))
        {
            bool isInput  = s.IoType.StartsWith("I", System.StringComparison.OrdinalIgnoreCase);
            bool isOutput = s.IoType.StartsWith("Q", System.StringComparison.OrdinalIgnoreCase);
            rows.Add(new IoBatchRow(
                callId:     System.Guid.Empty,
                apiCallId:  s.ApiCallId,
                flow:       s.FlowName,
                work:       s.WorkName,
                device:     s.DeviceAlias,
                api:        "",
                inAddress:  isInput  ? s.Address : "",
                inSymbol:   isInput  ? s.VarName : "",
                outAddress: isOutput ? s.Address : "",
                outSymbol:  isOutput ? s.VarName : ""));
        }

        // 그 외: ApiDefName 기준 IW + QW 페어링.
        var grouped = result.IoSignals
            .Where(s => !IsApiNone(s))
            .GroupBy(s => new { s.ApiCallId, s.FlowName, s.WorkName, s.CallName, s.DeviceName });

        foreach (var group in grouped)
        {
            var key = group.Key;
            var input  = group.FirstOrDefault(s => s.IoType.StartsWith("I", System.StringComparison.OrdinalIgnoreCase));
            var output = group.FirstOrDefault(s => s.IoType.StartsWith("Q", System.StringComparison.OrdinalIgnoreCase));

            rows.Add(new IoBatchRow(
                callId:     System.Guid.Empty,
                apiCallId:  key.ApiCallId,
                flow:       key.FlowName,
                work:       key.WorkName,
                device:     (input ?? output)?.DeviceAlias ?? "",
                api:        key.DeviceName,
                inAddress:  input?.Address  ?? "",
                inSymbol:   input?.VarName  ?? "",
                outAddress: output?.Address ?? "",
                outSymbol:  output?.VarName ?? ""));
        }
        return rows;
    }

    private static bool IsApiNone(Plc.Xgi.SignalRecord s) =>
        string.Equals(s.DeviceName, Promaker.Dialogs.TagWizardDialog.ApiNoneSentinel,
                      System.StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// I/O 생성 — 먼저 모드 선택 다이얼로그를 띄우고 기본/고급 Wizard 로 라우팅.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasProject))]
    private void OpenTagWizard()
    {
        if (!GuardSimulationSemanticEdit("IO 생성 (TAG Wizard)"))
            return;

        var picker = new TagWizardModeDialog();
        if (_dialogService.ShowDialog(picker) != true)
            return;

        switch (picker.SelectedMode)
        {
            case TagWizardModeDialog.WizardMode.Advanced:
                _dialogService.ShowDialog(new TagWizardDialog(_store));
                break;
            case TagWizardModeDialog.WizardMode.Basic:
            default:
                _dialogService.ShowDialog(new TagWizardBasicDialog(_store));
                break;
        }
    }
}
