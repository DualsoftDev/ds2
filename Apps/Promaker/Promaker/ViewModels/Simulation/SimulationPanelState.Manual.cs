using System;
using Ds2.Core;
using Ds2.Runtime.IO;
using Promaker.ViewModels.Manual;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    /// <summary>현재 활성화된 수동 컨트롤러 다이얼로그의 ViewModel — 다이얼로그 인스턴스가 만들어 SimulationPanelState 에 attach.
    /// 다이얼로그 닫힐 때 detach. 이 reference 를 통해 외부(toolbar 핸들러)에서 다이얼로그 활성 여부 추적.</summary>
    private ManualControlState? _manualControlState;

    /// <summary>수동 컨트롤러 세션 시작 — Hub + PLC 게이트웨이 띄우고 엔진은 Pause 로 두어
    /// 시퀀스가 자동으로 진행되지 않도록. 사용자는 다이얼로그에서 OUT 만 직접 토글.</summary>
    /// <returns>세션 시작 성공 시 true. 실패하면 사용자에게 이미 알림 됐고 false.</returns>
    public bool BeginManualControlSession()
    {
        if (IsManualControlActive)
        {
            AddSimLog("수동 컨트롤러 — 이미 활성 세션이 있습니다", LogSeverity.Warn);
            return false;
        }
        if (SelectedRuntimeMode != RuntimeMode.Control || !IsRealPlcConnected)
        {
            AddSimLog("수동 컨트롤러는 Control + 실 PLC 연결 모드 전용", LogSeverity.Warn);
            return false;
        }
        if (IsSimulating)
        {
            AddSimLog("수동 컨트롤러 진입 전 현재 시뮬을 정지합니다", LogSeverity.System);
            StopSimulation();
        }

        AddSimLog("수동 컨트롤러 시작 — Hub + PLC 게이트웨이 기동, 엔진 일시정지", LogSeverity.System);
        IsManualControlActive = true;

        StartSimulation();

        if (_simEngine is null)
        {
            // Hub/PLC 시작 실패 — 이미 로그 됨.
            IsManualControlActive = false;
            return false;
        }

        // 시뮬 진입에 성공했지만 — 수동 모드는 시퀀스를 자동 진행하지 않음.
        // homing 페이즈는 그냥 흘려보내고, 페이즈 완료 후 PauseSimulation 호출은 OnHomingPhaseCompleted 에서 처리.
        // (homing 자체가 없으면 즉시 pause.)
        if (!IsHomingPhase && CanPauseSimulation())
        {
            PauseSimulation();
        }

        return true;
    }

    /// <summary>다이얼로그가 종료될 때 호출 — 모든 OUT 을 false 로 cleanup 하면서 세션 정리.</summary>
    public void EndManualControlSession()
    {
        if (!IsManualControlActive) return;
        AddSimLog("수동 컨트롤러 종료 — 시뮬·Hub·PLC 정리 (모든 OUT off broadcast 포함)", LogSeverity.System);
        IsManualControlActive = false;
        if (IsSimulating)
            StopSimulation();
    }

    /// <summary>다이얼로그가 자기 ViewModel 을 attach/detach. 다이얼로그 인스턴스가 단일임을 보장.</summary>
    internal void AttachManualControlState(ManualControlState state) => _manualControlState = state;
    internal void DetachManualControlState(ManualControlState state)
    {
        if (ReferenceEquals(_manualControlState, state)) _manualControlState = null;
    }
    public ManualControlState? CurrentManualControlState => _manualControlState;

    /// <summary>현재 store 의 IO 매핑 + Call 정보로 ManualControlState 빌드.
    /// 외부(toolbar) 에서 store 직접 접근 못 하므로 SimulationPanelState 가 자기 내부 store 로 조립해서 반환.</summary>
    public ManualControlState BuildManualControlState()
    {
        var store = _storeProvider();
        var ioMap = SignalIOMapModule.build(store);
        return new ManualControlState(this, store, ioMap);
    }
}
