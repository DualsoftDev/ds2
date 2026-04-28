using System;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Report;
using Ds2.Runtime.Report.Exporters;
using Ds2.Runtime.Report.Model;
using Microsoft.Win32;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    /// <summary>통합 리포트 출력: 형식 선택 + 출력 후 열기 옵션.</summary>
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReport()
    {
        var report = BuildReport();
        if (report.Entries.IsEmpty)
        {
            _setStatusText(SimText.ReportEmpty);
            return;
        }

        var fmtDlg = new Promaker.Dialogs.ReportExportDialog();
        if (fmtDlg.ShowDialog() != true) return;

        var format = fmtDlg.SelectedFormat;
        var openAfter = fmtDlg.OpenAfter;

        var filter = ExportHelper.getFilter(format);
        var ext = ExportHelper.getExtension(format);

        var dlg = new SaveFileDialog
        {
            Title = SimText.ReportDialogTitle,
            Filter = filter,
            DefaultExt = ext,
            FileName = $"SimReport_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
        };

        if (dlg.ShowDialog() != true) return;

        try
        {
            var options = new ExportOptions
            {
                Format = format,
                FilePath = dlg.FileName,
                IncludeGanttChart = true,
                IncludeSummary = true,
                IncludeDetails = true,
                PixelsPerSecond = 10.0
            };
            var result = ReportService.export(report, options);

            if (result.IsSuccess)
            {
                _setStatusText(SimText.ReportSaved(dlg.FileName));
                if (openAfter && File.Exists(dlg.FileName))
                    OpenFileInDefaultApp(dlg.FileName);
            }
            else if (result.IsError)
            {
                _setStatusText(SimText.ReportSaveFailed(((ExportResult.Error)result).message));
            }
        }
        catch (Exception ex)
        {
            SimLog.Error("Report export failed", ex);
            _setStatusText(SimText.ReportError(ex.Message));
        }
    }

    private bool CanExportReport() => HasReportData;

    private static void OpenFileInDefaultApp(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SimLog.Warn($"파일 열기 실패: {ex.Message}");
        }
    }

    private void RecordStateChange(string nodeId, string nodeName, string nodeType, string systemId, Status4 state)
    {
        var stateString = SimText.StateCode(state);
        var timestamp = CurrentSimulationTimestamp();

        // Work 의 G/F 전환 시점에 해당 Work 가 들고 있는 토큰을 동봉 → 토큰별 KPI 집계 키로 사용
        var tokenItem = Microsoft.FSharp.Core.FSharpOption<int>.None;
        string originName = string.Empty;
        if (_simEngine != null && nodeType == "Work" && Guid.TryParse(nodeId, out var wid))
        {
            var token = _simEngine.GetWorkToken(wid);
            if (Microsoft.FSharp.Core.FSharpOption<TokenValue>.get_IsSome(token))
            {
                tokenItem = Microsoft.FSharp.Core.FSharpOption<int>.Some(token.Value.Item);
                var origin = _simEngine.GetTokenOrigin(token.Value);
                if (Microsoft.FSharp.Core.FSharpOption<System.Tuple<string, int>>.get_IsSome(origin))
                    originName = origin.Value.Item1 ?? string.Empty;
            }
        }

        _stateChangeRecords.Add(
            new StateChangeRecord(nodeId, nodeName, nodeType, systemId, stateString, timestamp,
                tokenItem, originName));
        HasReportData = _stateChangeRecords.Count > 0;
    }

    private DateTime CurrentSimulationTimestamp()
    {
        var clock = _simEngine?.State.Clock ?? TimeSpan.Zero;
        return _simStartTime + clock;
    }

    private SimulationReport BuildReport()
    {
        if (_stateChangeRecords.Count == 0) return ReportService.empty();

        var currentTime = CurrentSimulationTimestamp();
        var lastRecordTime = _stateChangeRecords[^1].Timestamp;
        var endTime = currentTime >= lastRecordTime ? currentTime : lastRecordTime;
        return ReportService.fromStateChanges(_simStartTime, endTime, _stateChangeRecords);
    }
}
