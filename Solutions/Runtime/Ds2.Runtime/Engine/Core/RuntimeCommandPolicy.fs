namespace Ds2.Runtime.Engine.Core

open Ds2.Core

module RuntimeCommandPolicy =

    let isPassiveMode (runtimeMode: RuntimeMode) =
        runtimeMode = RuntimeMode.VirtualPlant
        || runtimeMode = RuntimeMode.Monitoring

    let isRealLineControl (runtimeMode: RuntimeMode) (isRealPlcConnected: bool) =
        runtimeMode = RuntimeMode.Control && isRealPlcConnected

    let canStartSimulation (isSimulating: bool) (isSimPaused: bool) (isHomingPhase: bool) =
        (not isSimulating || isSimPaused) && not isHomingPhase

    let canPauseSimulation
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        (isRealPlcConnected: bool)
        =
        isSimulating
        && not isSimPaused
        && not isHomingPhase
        && not (isPassiveMode runtimeMode)
        && not (isRealLineControl runtimeMode isRealPlcConnected)

    let canStopSimulation (isSimulating: bool) =
        isSimulating

    let canResetSimulation (isSimulating: bool) =
        isSimulating

    let canStepSimulation
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        =
        (not isSimulating || isSimPaused)
        && not isHomingPhase
        && runtimeMode = RuntimeMode.Simulation

    let canUseManualSimulationControl
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        =
        isSimulating
        && not isSimPaused
        && not isHomingPhase
        && not (isPassiveMode runtimeMode)

    let canForceWork
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        (hasSelectedWork: bool)
        =
        canUseManualSimulationControl isSimulating isSimPaused isHomingPhase runtimeMode
        && hasSelectedWork

    let canSeedToken
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        (hasSelectedTokenSource: bool)
        =
        canUseManualSimulationControl isSimulating isSimPaused isHomingPhase runtimeMode
        && hasSelectedTokenSource

    let canBeginHoming
        (runtimeMode: RuntimeMode)
        (isRealPlcConnected: bool)
        (isSimulating: bool)
        (isHomingPhase: bool)
        (isHomingPressed: bool)
        =
        isRealLineControl runtimeMode isRealPlcConnected
        && not isSimulating
        && not isHomingPhase
        && not isHomingPressed

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
