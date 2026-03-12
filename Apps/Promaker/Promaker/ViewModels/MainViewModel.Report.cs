using System;
using Ds2.Core;
using Ds2.Runtime.Sim.Report;
using Ds2.Runtime.Sim.Report.Model;
using Ds2.Runtime.Sim.Model;

namespace Promaker.ViewModels;

/// <summary>????? ??? ??? ?? partial???.</summary>
public partial class MainViewModel
{
    private void RecordStateChange(string nodeId, string nodeName, string nodeType, string systemId, Status4 state)
    {
        var stateString = SimText.StateCode(state);
        _stateChangeRecords.Add(
            new StateChangeRecord(nodeId, nodeName, nodeType, systemId, stateString, DateTime.Now));
    }

    private SimulationReport BuildReport()
    {
        if (_stateChangeRecords.Count == 0) return ReportService.empty();
        return ReportService.fromStateChanges(_simStartTime, DateTime.Now, _stateChangeRecords);
    }
}
