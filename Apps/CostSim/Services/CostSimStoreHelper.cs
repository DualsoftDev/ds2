using System;
using System.Collections.Generic;
using System.Linq;
using Ds2.Core;
using Ds2.Editor;
using Ds2.Store;
using Microsoft.FSharp.Core;

namespace CostSim;

internal static class CostSimStoreHelper
{
    public static void UpdateWorkProperties(
        DsStore store,
        Guid workId,
        string operationCode,
        double durationSeconds,
        int workerCount,
        double laborCostPerHour,
        double equipmentCostPerHour,
        double overheadCostPerHour,
        double utilityCostPerHour,
        double yieldRate,
        double defectRate,
        string transactionLabel,
        bool emitHistory)
    {
        RunTransaction(store, transactionLabel, () =>
        {
            TrackMutate(store, store.Works, workId, work =>
            {
                var props = GetOrCreateProps(work);
                props.OperationCode = ToOption(operationCode);
                props.Duration = durationSeconds > 0.0 ? FSharpOption<TimeSpan>.Some(TimeSpan.FromSeconds(durationSeconds)) : null;
                props.WorkerCount = workerCount;
                props.LaborCostPerHour = laborCostPerHour;
                props.EquipmentCostPerHour = equipmentCostPerHour;
                props.OverheadCostPerHour = overheadCostPerHour;
                props.UtilityCostPerHour = utilityCostPerHour;
                props.YieldRate = yieldRate;
                props.DefectRate = defectRate;
            });
        });

        if (emitHistory)
            store.EmitRefreshAndHistory();
    }

    public static SimulationWorkProperties? GetExistingProps(Work work)
        => work.SimulationProperties is { } option ? option.Value : null;

    public static SimulationWorkProperties GetOrCreateProps(Work work)
    {
        if (work.SimulationProperties is { } option)
            return option.Value;

        var props = new SimulationWorkProperties();
        work.SimulationProperties = FSharpOption<SimulationWorkProperties>.Some(props);
        return props;
    }

    public static IEnumerable<Work> GetOrderedWorksInFlow(DsStore store, Guid flowId)
        => store.Works.Values
            .Where(work => work.ParentId == flowId)
            .OrderBy(GetSequenceSortKey)
            .ThenBy(work => work.LocalName, StringComparer.CurrentCultureIgnoreCase);

    public static int GetSequenceOrder(Work work)
        => Math.Max(0, GetExistingProps(work)?.SequenceOrder ?? 0);

    public static void SetSequenceOrder(Work work, int sequenceOrder)
    {
        GetOrCreateProps(work).SequenceOrder = sequenceOrder;
    }

    public static void ApplyCalculatedTotals(Work work)
    {
        var props = GetOrCreateProps(work);
        var durationSeconds = props.Duration is { } duration ? duration.Value.TotalSeconds : 0.0;
        var workerCount = Math.Max(1, props.WorkerCount);
        var laborCost = SimulationHelpers.calculateLaborCost(props.LaborCostPerHour, durationSeconds, workerCount);
        var equipmentCost = (props.EquipmentCostPerHour / 3600.0) * durationSeconds;
        var overheadCost = (props.OverheadCostPerHour / 3600.0) * durationSeconds;
        var utilityCost = SimulationHelpers.calculateUtilityCost(props.UtilityCostPerHour, durationSeconds);
        var totalCost = laborCost + equipmentCost + overheadCost + utilityCost;
        var effectiveYield = Math.Max(0.01, props.YieldRate * (1.0 - props.DefectRate));

        props.TotalMaterialCost = FSharpOption<double>.Some(0.0);
        props.TotalLaborCost = FSharpOption<double>.Some(laborCost);
        props.TotalEquipmentCost = FSharpOption<double>.Some(equipmentCost);
        props.TotalOverheadCost = FSharpOption<double>.Some(overheadCost + utilityCost);
        props.TotalCost = FSharpOption<double>.Some(totalCost);
        props.UnitCost = FSharpOption<double>.Some(totalCost / effectiveYield);
    }

    public static string ReadOption(FSharpOption<string>? option)
        => option?.Value ?? string.Empty;

    public static FSharpOption<string>? ToOption(string text)
        => string.IsNullOrWhiteSpace(text) ? null : FSharpOption<string>.Some(text.Trim());

    public static void RunTransaction(DsStore store, string label, Action action)
    {
        store.WithTransaction<object>(
            label,
            FuncConvert.FromFunc((Func<object>)(() =>
            {
                action();
                return new object();
            })));
    }

    public static void TrackMutate<TEntity>(
        DsStore store,
        Dictionary<Guid, TEntity> dict,
        Guid id,
        Action<TEntity> mutate)
        where TEntity : DsEntity
    {
        store.TrackMutate(dict, id, FuncConvert.FromAction(mutate));
    }

    private static int GetSequenceSortKey(Work work)
    {
        var sequence = GetSequenceOrder(work);
        return sequence <= 0 ? int.MaxValue : sequence;
    }
}
