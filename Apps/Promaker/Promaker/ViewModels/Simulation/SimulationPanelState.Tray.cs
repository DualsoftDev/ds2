using System;
using Ds2.Core;

namespace Promaker.ViewModels;

public partial class SimulationPanelState
{
    /// <summary>
    /// Monitoring + IsRealPlcConnected 인 상태에서 PLAY 가 성공하면 호출 — MainWindow 가
    /// 트레이로 전환하도록 신호. 호출자는 UI 스레드에서 실행한다고 가정 (StartSimulation 가 UI 스레드).
    /// </summary>
    public Action? RequestTrayHide { get; set; }

    /// <summary>STOP 또는 모드 전환 시 호출 — 트레이 활성 상태면 윈도우 복원 + 아이콘 제거.</summary>
    public Action? RequestTrayRestore { get; set; }

    /// <summary>StartSimulation 진입 시 사용자가 트레이 전환에 동의했는지. 단발성 — 시작 성공 후 reset.</summary>
    internal bool TrayTransitionPending { get; set; }

    /// <summary>
    /// Monitoring + IsRealPlcConnected 조합에서 PLAY 가 시작 흐름에 진입하기 전 호출되는 게이트.
    /// 동의 다이얼로그 표시 + 결과에 따라 false 반환 시 시작 중단.
    /// </summary>
    internal bool TryAcquireTrayConsent()
    {
        if (SelectedRuntimeMode != RuntimeMode.Monitoring) return true;
        if (!IsRealPlcConnected) return true;
        // "다시 묻지 않기" 영속화 시 다이얼로그 우회 — 항상 동의로 처리.
        if (!Dialogs.TrayConsentDialog.ShowAndAskConsent())
            return false;
        TrayTransitionPending = true;
        return true;
    }

    /// <summary>StartSimulation 의 정상 종료 직전 호출 — 보류된 트레이 전환 트리거.</summary>
    internal void FireTrayTransitionIfPending()
    {
        if (!TrayTransitionPending) return;
        TrayTransitionPending = false;
        try { RequestTrayHide?.Invoke(); }
        catch (Exception ex) { SimLog.Error("RequestTrayHide threw", ex); }
    }

    /// <summary>StopSimulation 또는 모드 전환 시 호출.</summary>
    internal void FireTrayRestore()
    {
        try { RequestTrayRestore?.Invoke(); }
        catch (Exception ex) { SimLog.Error("RequestTrayRestore threw", ex); }
    }
}
