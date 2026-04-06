using System;
using CommunityToolkit.Mvvm.Input;
using Ds2.Core;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Exporters;
using Ds2.Runtime.Sim.Report.Model;
using Microsoft.Win32;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReportCsv() => ExportReportAs(Ds2.Runtime.Sim.Report.Model.ExportFormat.Csv);

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReportXlsx() => ExportReportAs(Ds2.Runtime.Sim.Report.Model.ExportFormat.Excel);

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReportHtml() => ExportReportAs(Ds2.Runtime.Sim.Report.Model.ExportFormat.Html);

    private bool CanExportReport() => HasReportData;

    private void ExportReportAs(Ds2.Runtime.Sim.Report.Model.ExportFormat format)
    {
        var report = BuildReport();
        if (report.Entries.IsEmpty)
        {
            _setStatusText(SimText.ReportEmpty);
            return;
        }

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
                _setStatusText(SimText.ReportSaved(dlg.FileName));
            else if (result.IsError)
                _setStatusText(SimText.ReportSaveFailed(((ExportResult.Error)result).message));
        }
        catch (Exception ex)
        {
            SimLog.Error("Report export failed", ex);
            _setStatusText(SimText.ReportError(ex.Message));
        }
    }

    private void RecordStateChange(string nodeId, string nodeName, string nodeType, string systemId, Status4 state)
    {
        var stateString = SimText.StateCode(state);
        var timestamp = CurrentSimulationTimestamp();
        _stateChangeRecords.Add(
            new StateChangeRecord(nodeId, nodeName, nodeType, systemId, stateString, timestamp));
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
