using System;

namespace CostSim;

public sealed class WorkCostRow
{
    public Guid WorkId { get; init; }
    public int SequenceOrder { get; init; }
    public string SystemName { get; init; } = "";
    public string FlowName { get; init; } = "";
    public string WorkName { get; init; } = "";
    public string OperationCode { get; init; } = "";
    public double DurationSeconds { get; init; }
    public int WorkerCount { get; init; }
    public double LaborCostPerHour { get; init; }
    public double EquipmentCostPerHour { get; init; }
    public double OverheadCostPerHour { get; init; }
    public double UtilityCostPerHour { get; init; }
    public double YieldRate { get; init; }
    public double DefectRate { get; init; }
    public double TotalCost { get; init; }
    public double UnitCost { get; init; }
    public bool IsSource { get; init; }
}
