namespace Ds2.Runtime.Engine.Core

open Ds2.Core

/// <summary>
/// 시뮬레이션 runtime 의 *공유 mode 분류* (passive / real-line control) + 연속 주입 (ContinuousInjection)
/// 가용성/지속 조건. 다른 Can* command 결정은 <c>SimulationCommandFacade</c> 의 typed Decision 으로 이관됨.
/// </summary>
module RuntimeCommandPolicy =

    let isPassiveMode (runtimeMode: RuntimeMode) =
        runtimeMode = RuntimeMode.VirtualPlant
        || runtimeMode = RuntimeMode.Monitoring

    let isRealLineControl (runtimeMode: RuntimeMode) (isRealPlcConnected: bool) =
        runtimeMode = RuntimeMode.Control && isRealPlcConnected

    let isContinuousInjectionAvailable
        (runtimeMode: RuntimeMode)
        (isRealPlcConnected: bool)
        =
        runtimeMode <> RuntimeMode.Monitoring
        && not (isRealLineControl runtimeMode isRealPlcConnected)

    let canContinueSourceCycle
        (isContinuousInjectionEnabled: bool)
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (newState: Status4)
        =
        isContinuousInjectionEnabled
        && isSimulating
        && not isSimPaused
        && not isHomingPhase
        && newState = Status4.Ready
