namespace Ds2.Runtime.Engine.Core

open Ds2.Core

/// <summary>
/// 시뮬레이션 명령 (Start/Pause/Stop/Reset/Step/Homing/ForceWork/SeedToken) 의 *수락/거부* 결정 + 거부 사유.
/// Slice 4 (Simulation runtime facade) 의 typed result 첫 단계 — boolean Can* 결정에 *왜 거부됐는지*
/// 정보를 더해 C# UI 측이 사용자에게 reason 표시 또는 다른 분기 처리할 수 있게 한다.
///
/// 본 모듈은 **결정만** 한다 — engine action (engine.Start / engine.Pause 등) 호출은 C# 측 책임.
/// "F# decides, C# applies" Slice 4 plan 의 원칙.
/// </summary>
module SimulationCommandFacade =

    /// <summary>각 명령의 거부 사유. UI 측에서 사용자에게 보일 status 또는 disabled tooltip 으로 변환 가능.</summary>
    type Rejection =
        /// 이미 시뮬레이션 동작 중 (Start 시).
        | AlreadySimulating
        /// 시뮬레이션 정지 상태 (Pause/Stop/Reset/Force/Seed 시).
        | NotSimulating
        /// 시뮬 동작 중인데 일시정지 (Pause 시 — 이미 paused).
        | AlreadyPaused
        /// 자동 원위치 phase 진행 중 — 다른 명령 차단.
        | InHomingPhase
        /// VirtualPlant / Monitoring 모드 (Pause/Manual control 차단).
        | PassiveMode
        /// Control + 실 PLC 연결 (Pause 차단 — 안전 위험).
        | RealLineControlActive
        /// Step 은 Simulation 모드 전용.
        | NotInSimulationMode
        /// ForceWork 가 SelectedSimWork 없이 호출됨.
        | NoSelectedWork
        /// SeedToken 이 SelectedTokenSource 없이 호출됨.
        | NoSelectedTokenSource
        /// Homing 시작 조건 (Control + 실 PLC + 시뮬 미동작) 미충족.
        | HomingNotAllowed
        /// Homing 버튼이 이미 눌린 상태 (release 대기).
        | HomingAlreadyPressed

    /// <summary>명령 수락/거부 통합 typed decision.</summary>
    type Decision =
        | Accepted
        | Rejected of Rejection

    /// <summary>Decision → boolean 단축 (기존 boolean 호출자 호환). UI 가 reason 사용하지 않을 때.</summary>
    [<CompiledName("IsAccepted")>]
    let isAccepted (d: Decision) =
        match d with
        | Accepted -> true
        | Rejected _ -> false

    /// <summary>거부 사유의 사용자 표시 라벨 (Status 라인 / tooltip 용).</summary>
    [<CompiledName("RejectionLabel")>]
    let rejectionLabel (r: Rejection) : string =
        match r with
        | AlreadySimulating -> "이미 시뮬레이션 동작 중"
        | NotSimulating -> "시뮬레이션 정지 상태"
        | AlreadyPaused -> "이미 일시정지 상태"
        | InHomingPhase -> "자동 원위치 진행 중"
        | PassiveMode -> "VirtualPlant/Monitoring 모드에서는 사용 불가"
        | RealLineControlActive -> "실 PLC 연결 중에는 차단 (안전)"
        | NotInSimulationMode -> "Simulation 모드 전용"
        | NoSelectedWork -> "Work 가 선택되지 않음"
        | NoSelectedTokenSource -> "Token Source 가 선택되지 않음"
        | HomingNotAllowed -> "원위치는 Control + 실 PLC 연결 + 시뮬 미동작 시에만 가능"
        | HomingAlreadyPressed -> "원위치 버튼이 이미 눌린 상태"

    // ── Decision builders (boolean RuntimeCommandPolicy 위에 reason 첨가) ──

    let private isPassiveMode = RuntimeCommandPolicy.isPassiveMode
    let private isRealLineControl = RuntimeCommandPolicy.isRealLineControl

    [<CompiledName("DecideStart")>]
    let decideStart (isSimulating: bool) (isSimPaused: bool) (isHomingPhase: bool) : Decision =
        if isHomingPhase then Rejected InHomingPhase
        elif isSimulating && not isSimPaused then Rejected AlreadySimulating
        else Accepted

    [<CompiledName("DecidePause")>]
    let decidePause
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        (isRealPlcConnected: bool)
        : Decision =
        if not isSimulating then Rejected NotSimulating
        elif isSimPaused then Rejected AlreadyPaused
        elif isHomingPhase then Rejected InHomingPhase
        elif isPassiveMode runtimeMode then Rejected PassiveMode
        elif isRealLineControl runtimeMode isRealPlcConnected then Rejected RealLineControlActive
        else Accepted

    [<CompiledName("DecideStop")>]
    let decideStop (isSimulating: bool) : Decision =
        if isSimulating then Accepted else Rejected NotSimulating

    [<CompiledName("DecideReset")>]
    let decideReset (isSimulating: bool) : Decision =
        if isSimulating then Accepted else Rejected NotSimulating

    [<CompiledName("DecideStep")>]
    let decideStep
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        : Decision =
        if isHomingPhase then Rejected InHomingPhase
        elif runtimeMode <> RuntimeMode.Simulation then Rejected NotInSimulationMode
        elif isSimulating && not isSimPaused then Rejected AlreadySimulating
        else Accepted

    [<CompiledName("DecideForceWork")>]
    let decideForceWork
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        (hasSelectedWork: bool)
        : Decision =
        if not hasSelectedWork then Rejected NoSelectedWork
        else
            match decidePause isSimulating isSimPaused isHomingPhase runtimeMode false with
            // canUseManualSimulationControl 와 동일 조건 (Pause 와 같이 passive 차단 + simulating 필수).
            // Pause 와 달리 RealLineControl 도 허용 — Manual control 자체는 Control + real PLC 시에도 사용.
            | Rejected RealLineControlActive -> Accepted
            | other -> other

    [<CompiledName("DecideSeedToken")>]
    let decideSeedToken
        (isSimulating: bool)
        (isSimPaused: bool)
        (isHomingPhase: bool)
        (runtimeMode: RuntimeMode)
        (hasSelectedTokenSource: bool)
        : Decision =
        if not hasSelectedTokenSource then Rejected NoSelectedTokenSource
        else decideForceWork isSimulating isSimPaused isHomingPhase runtimeMode true

    [<CompiledName("DecideBeginHoming")>]
    let decideBeginHoming
        (runtimeMode: RuntimeMode)
        (isRealPlcConnected: bool)
        (isSimulating: bool)
        (isHomingPhase: bool)
        (isHomingPressed: bool)
        : Decision =
        if isHomingPressed then Rejected HomingAlreadyPressed
        elif isHomingPhase then Rejected InHomingPhase
        elif isSimulating then Rejected AlreadySimulating
        elif not (isRealLineControl runtimeMode isRealPlcConnected) then Rejected HomingNotAllowed
        else Accepted
