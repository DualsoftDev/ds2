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

    /// <summary>연속투입 토글 사용 가능 여부. Monitoring 또는 Control+실 PLC 에서는 외부(PLC/원격 호스트) 가
    /// 토큰 투입 owner 이므로 시뮬 로컬 자동 투입이 의미 없고 충돌 위험 → 비활성.</summary>
    public bool IsContinuousInjectionAvailable =>
        SelectedRuntimeMode != RuntimeMode.Monitoring
        && !(SelectedRuntimeMode == RuntimeMode.Control && IsRealPlcConnected);

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
