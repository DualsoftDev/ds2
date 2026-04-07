namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Runtime.Model
open Ds2.Runtime.Engine.Core

/// 시뮬레이션 엔진 인터페이스
/// Work/Call 상태 전이 (R->G->F->H) 시뮬레이션
type ISimulationEngine =
    inherit IDisposable

    // 상태 조회
    abstract State: SimState
    abstract Status: SimulationStatus
    abstract Index: SimIndex

    // 수명 주기
    abstract Start: unit -> unit
    abstract Pause: unit -> unit
    abstract Resume: unit -> unit
    abstract Stop: unit -> unit

    /// 리셋: F->H->R (H 상태 전이를 통해 내부 Call 정리 후 R로)
    abstract Reset: unit -> unit

    /// InitialFlag=true인 RxWork F 설정
    abstract ApplyInitialStates: unit -> unit

    // 상태 제어
    abstract ForceWorkState: workGuid: Guid * newState: Status4 -> unit
    abstract ForceCallState: callGuid: Guid * newState: Status4 -> unit
    abstract GetWorkState: workGuid: Guid -> Status4 option
    abstract GetCallState: callGuid: Guid -> Status4 option

    // Flow 단계 제어
    abstract SetAllFlowStates: tag: FlowTag -> unit
    abstract GetFlowState: flowGuid: Guid -> FlowTag

    /// 시작 가능한(Ready + 조건 충족) Work가 있는지
    abstract HasStartableWork: bool

    /// Pause에서도 duration이 자동 진행 중인 Going Work가 있는지
    /// (leaf Work 또는 모든 Call이 Finish인 Work — timer 취소 안 됨)
    abstract HasActiveDuration: bool

    /// Pause/STEP 모드에서 다음 의미 단위까지 진행합니다.
    /// true면 상태 또는 시뮬레이션 시간이 실제로 전진했습니다.
    abstract Step: unit -> bool

    /// 현재 선택된 Source/자동선택 정책까지 포함해 STEP 가능 여부를 계산합니다.
    abstract CanAdvanceStep: selectedSourceGuid: Guid * autoStartSources: bool -> bool

    /// Source priming까지 포함해 STEP을 수행합니다.
    abstract StepWithSourcePriming: selectedSourceGuid: Guid * autoStartSources: bool -> bool

    /// 현재 Store 기준으로 연결 topology만 다시 계산합니다.
    abstract ReloadConnections: unit -> unit

    /// 현재 Store 기준으로 Duration만 다시 계산합니다.
    /// Going 상태 Work는 기존 Duration 유지 (진행 중 타이머 보호).
    abstract ReloadDurations: unit -> unit

    // 설정
    abstract SpeedMultiplier: float with get, set
    abstract TimeIgnore: bool with get, set

    // 외부 신호
    abstract InjectIOValue: apiCallGuid: Guid * value: string -> unit

    // 토큰
    abstract NextToken: unit -> TokenValue
    abstract SeedToken: sourceWorkGuid: Guid * value: TokenValue -> unit
    abstract DiscardToken: workGuid: Guid -> unit
    abstract GetWorkToken: workGuid: Guid -> TokenValue option
    abstract GetTokenOrigin: token: TokenValue -> (string * int) option

    /// 자동 원위치 페이즈: ApplyInitialStates → Start → Homing 완료 대기 → 정상 시뮬레이션
    /// true면 Homing 대상이 있어서 페이즈 시작됨, false면 대상 없어서 즉시 정상 시작
    abstract StartWithHomingPhase: unit -> bool

    /// 현재 Homing 페이즈 진행 중인지
    abstract IsHomingPhase: bool

    // 이벤트
    [<CLIEvent>]
    abstract WorkStateChanged: IEvent<WorkStateChangedArgs>
    [<CLIEvent>]
    abstract CallStateChanged: IEvent<CallStateChangedArgs>
    [<CLIEvent>]
    abstract SimulationStatusChanged: IEvent<SimulationStatusChangedArgs>
    [<CLIEvent>]
    abstract TokenEvent: IEvent<TokenEventArgs>
    [<CLIEvent>]
    abstract CallTimeout: IEvent<CallTimeoutArgs>
    /// 자동 원위치 페이즈 완료 이벤트
    [<CLIEvent>]
    abstract HomingPhaseCompleted: IEvent<EventArgs>
