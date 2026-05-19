using System;
using Ds2.Core;
using log4net;

namespace Promaker.ViewModels;

/// <summary>
/// Monitoring + 실 PLC 조합에서 PLAY 시점에 발생하는 트레이 전환 흐름 controller.
/// SimulationPanelState 의 partial 에서 분리. 외부 wire-up (MainWindow) 은 RequestTrayHide /
/// RequestTrayRestore / RequestDspilotOpen 콜백을 채우고, Runner 흐름이 동의 게이트(TryAcquireConsent) 와
/// 보류 전환(FireTransitionIfPending) / 복원(FireRestore) 을 호출.
/// </summary>
public sealed class SimulationTrayController
{
    private static readonly ILog Log = LogManager.GetLogger("Simulation");

    private readonly Func<RuntimeMode> _runtimeModeProvider;
    private readonly Func<bool>        _isRealPlcConnectedProvider;

    public SimulationTrayController(
        Func<RuntimeMode> runtimeModeProvider,
        Func<bool>        isRealPlcConnectedProvider)
    {
        _runtimeModeProvider = runtimeModeProvider;
        _isRealPlcConnectedProvider = isRealPlcConnectedProvider;
    }

    /// <summary>
    /// Monitoring + IsRealPlcConnected 인 상태에서 PLAY 가 성공하면 호출 — MainWindow 가
    /// 트레이로 전환하도록 신호. 호출자는 UI 스레드에서 실행한다고 가정 (StartSimulation 가 UI 스레드).
    /// </summary>
    public Action? RequestTrayHide { get; set; }

    /// <summary>STOP 또는 모드 전환 시 호출 — 트레이 활성 상태면 윈도우 복원 + 아이콘 제거.</summary>
    public Action? RequestTrayRestore { get; set; }

    /// <summary>Monitoring + IsRealPlcConnected PLAY 가 성공해 트레이 전환이 발생한 직후 호출 —
    /// DSPilot 웹 대시보드를 기본 브라우저로 띄운다. 트레이 아이콘의 "DSPilot 접속" 메뉴와 동일 동작.</summary>
    public Action? RequestDspilotOpen { get; set; }

    /// <summary>StartSimulation 진입 시 사용자가 트레이 전환에 동의했는지. 단발성 — 시작 성공 후 reset.</summary>
    internal bool TransitionPending { get; set; }

    /// <summary>
    /// Monitoring + IsRealPlcConnected 조합에서 PLAY 가 시작 흐름에 진입하기 전 호출되는 게이트.
    /// 동의 다이얼로그 표시 + 결과에 따라 false 반환 시 시작 중단.
    /// </summary>
    internal bool TryAcquireConsent()
    {
        if (_runtimeModeProvider() != RuntimeMode.Monitoring) return true;
        if (!_isRealPlcConnectedProvider()) return true;
        // "다시 묻지 않기" 영속화 시 다이얼로그 우회 — 항상 동의로 처리.
        if (!Dialogs.TrayConsentDialog.ShowAndAskConsent())
            return false;
        TransitionPending = true;
        return true;
    }

    /// <summary>StartSimulation 의 정상 종료 직전 호출 — 보류된 트레이 전환 트리거.
    /// 트레이 전환과 동시에 DSPilot 브라우저 접속도 발화 (Monitoring + 실 PLC PLAY 만 도달하는 경로).</summary>
    internal void FireTransitionIfPending()
    {
        if (!TransitionPending) return;
        TransitionPending = false;
        try { RequestTrayHide?.Invoke(); }
        catch (Exception ex) { Log.Error("RequestTrayHide threw", ex); }
        try { RequestDspilotOpen?.Invoke(); }
        catch (Exception ex) { Log.Error("RequestDspilotOpen threw", ex); }
    }

    /// <summary>StopSimulation 또는 모드 전환 시 호출.</summary>
    internal void FireRestore()
    {
        try { RequestTrayRestore?.Invoke(); }
        catch (Exception ex) { Log.Error("RequestTrayRestore threw", ex); }
    }
}
