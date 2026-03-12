using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Exporters;
using Ds2.Runtime.Sim.Report.Model;
using Ds2.Runtime.Sim.Model;
using log4net;
using Microsoft.Win32;

namespace Promaker.ViewModels;

/// <summary>시뮬레이션 리포트 내보내기</summary>
public partial class MainViewModel
{
    private void RecordStateChange(string nodeId, string nodeName, string nodeType, string systemId, Status4 state)
    {
        var stateStr = NodeMatching.nodeStateToString(state);
        _stateChangeRecords.Add(
            new StateChangeRecord(nodeId, nodeName, nodeType, systemId, stateStr, DateTime.Now));
    }

    public SimulationReport BuildReport()
    {
        if (_stateChangeRecords.Count == 0) return ReportService.empty();
        return ReportService.fromStateChanges(_simStartTime, DateTime.Now, _stateChangeRecords);
    }

    [RelayCommand]
    private void ExportReport()
    {
        var report = BuildReport();
        if (report.Entries.IsEmpty)
        {
            StatusText = "내보낼 시뮬레이션 데이터가 없습니다.";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "시뮬레이션 리포트 내보내기",
            Filter = ExportHelper.getAllFilters(),
            DefaultExt = ".html"
        };

        if (dlg.ShowDialog() != true) return;

        var result = ReportService.exportAuto(report, dlg.FileName);
        if (result.IsSuccess)
        {
            StatusText = $"리포트 저장: {dlg.FileName}";
        }
        else if (result.IsError)
        {
            var errResult = (ExportResult.Error)result;
            var msg = errResult.message;
            Log.Error($"리포트 내보내기 실패: {msg}");
            StatusText = $"리포트 오류: {msg}";
        }
    }
}
