using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    private readonly HashSet<Guid> _continuousSourceWorkGuids = [];

    [ObservableProperty]
    private bool _isContinuousInjectionEnabled;

    partial void OnIsContinuousInjectionEnabledChanged(bool value)
    {
        if (!value)
            _continuousSourceWorkGuids.Clear();
    }

    private void ArmContinuousSource(Guid sourceGuid)
    {
        if (IsContinuousInjectionEnabled && sourceGuid != Guid.Empty)
            _continuousSourceWorkGuids.Add(sourceGuid);
    }

    private void ArmContinuousSources(IEnumerable<Guid> sourceGuids)
    {
        if (!IsContinuousInjectionEnabled)
            return;

        foreach (var sourceGuid in sourceGuids)
        {
            if (sourceGuid != Guid.Empty)
                _continuousSourceWorkGuids.Add(sourceGuid);
        }
    }

    private void DisarmContinuousSource(Guid sourceGuid) =>
        _continuousSourceWorkGuids.Remove(sourceGuid);

    private void ClearContinuousSourceCycle() =>
        _continuousSourceWorkGuids.Clear();

    private void TryContinueSourceCycle(Guid workGuid, Status4 newState)
    {
        if (!IsContinuousInjectionEnabled
            || !IsSimulating
            || IsSimPaused
            || IsHomingPhase
            || newState != Status4.Ready
            || _simEngine is not { } engine)
            return;

        var canonicalWorkGuid = Queries.resolveOriginalWorkId(workGuid, Store);
        if (!_continuousSourceWorkGuids.Contains(canonicalWorkGuid))
            return;

        if (!SimIndexModule.isTokenSource(engine.Index, canonicalWorkGuid))
        {
            _continuousSourceWorkGuids.Remove(canonicalWorkGuid);
            return;
        }

        var workState = engine.GetWorkState(canonicalWorkGuid);
        if (workState is null || workState.Value != Status4.Ready)
            return;

        if (engine.GetWorkToken(canonicalWorkGuid) is not null)
            return;

        if (!WorkConditionChecker.canStartWorkPredOnly(engine.Index, engine.State, canonicalWorkGuid))
            return;

        engine.StartSourceWork(canonicalWorkGuid);

        if (engine.Index.WorkName.TryFind(canonicalWorkGuid) is { } name)
            AddSimLog($"연속 투입: {name.Value}", LogSeverity.Going);
    }
}
