using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Ds2.Core;
using Ds2.Core.Store;
using Ds2.Runtime.Engine;
using Ds2.Runtime.Engine.Core;

namespace Promaker.ViewModels;

/// <summary>
/// 연속 토큰 투입 controller. Source Work 가 Finish 되면 자동으로 다음 cycle 을 시작.
/// SimulationPanelState 의 partial 에서 분리. 사용자 토글(IsEnabled) 과 source guid set 보유.
/// 시뮬 상태(runtime mode / pause / homing 등) 은 Func 로 주입.
/// </summary>
public sealed partial class SimulationContinuousInjectionController : ObservableObject
{
    private readonly HashSet<Guid> _sources = [];

    private readonly Func<RuntimeMode>          _runtimeMode;
    private readonly Func<bool>                 _isRealPlcConnected;
    private readonly Func<bool>                 _isSimulating;
    private readonly Func<bool>                 _isSimPaused;
    private readonly Func<bool>                 _isHomingPhase;
    private readonly Func<ISimulationEngine?>   _engineProvider;
    private readonly Func<DsStore>              _storeProvider;
    private readonly Action<string, LogSeverity> _addSimLog;

    [ObservableProperty]
    private bool _isEnabled;

    public SimulationContinuousInjectionController(
        Func<RuntimeMode>           runtimeMode,
        Func<bool>                  isRealPlcConnected,
        Func<bool>                  isSimulating,
        Func<bool>                  isSimPaused,
        Func<bool>                  isHomingPhase,
        Func<ISimulationEngine?>    engineProvider,
        Func<DsStore>               storeProvider,
        Action<string, LogSeverity> addSimLog)
    {
        _runtimeMode          = runtimeMode;
        _isRealPlcConnected   = isRealPlcConnected;
        _isSimulating         = isSimulating;
        _isSimPaused          = isSimPaused;
        _isHomingPhase        = isHomingPhase;
        _engineProvider       = engineProvider;
        _storeProvider        = storeProvider;
        _addSimLog            = addSimLog;
    }

    /// <summary>연속투입 토글 사용 가능 여부. Monitoring 또는 Control+실 PLC 에서는 외부(PLC/원격 호스트) 가
    /// 토큰 투입 owner 이므로 시뮬 로컬 자동 투입이 의미 없고 충돌 위험 → 비활성.</summary>
    public bool IsAvailable =>
        RuntimeCommandPolicy.isContinuousInjectionAvailable(_runtimeMode(), _isRealPlcConnected());

    /// <summary>본체 RuntimeMode/PLC 토글 시 호출 — computed IsAvailable 의 PropertyChanged 발화.</summary>
    internal void RaiseIsAvailableChanged() => OnPropertyChanged(nameof(IsAvailable));

    partial void OnIsEnabledChanged(bool value)
    {
        if (!value)
            _sources.Clear();
    }

    public void Arm(Guid sourceGuid)
    {
        if (IsEnabled && sourceGuid != Guid.Empty)
            _sources.Add(sourceGuid);
    }

    public void Arm(IEnumerable<Guid> sourceGuids)
    {
        if (!IsEnabled)
            return;

        foreach (var sourceGuid in sourceGuids)
        {
            if (sourceGuid != Guid.Empty)
                _sources.Add(sourceGuid);
        }
    }

    public void Disarm(Guid sourceGuid) => _sources.Remove(sourceGuid);

    public void ClearCycle() => _sources.Clear();

    public void TryContinue(Guid workGuid, Status4 newState)
    {
        if (!RuntimeCommandPolicy.canContinueSourceCycle(
                IsEnabled,
                _isSimulating(),
                _isSimPaused(),
                _isHomingPhase(),
                newState)
            || _engineProvider() is not { } engine)
            return;

        var store = _storeProvider();
        var canonicalWorkGuid = Queries.resolveOriginalWorkId(workGuid, store);
        if (!_sources.Contains(canonicalWorkGuid))
            return;

        if (!SimIndexModule.isTokenSource(engine.Index, canonicalWorkGuid))
        {
            _sources.Remove(canonicalWorkGuid);
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
            _addSimLog($"연속 투입: {name.Value}", LogSeverity.Going);
    }
}
