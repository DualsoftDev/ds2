namespace Ds2.Runtime.Sim.Engine

open System
open Ds2.Core
open Ds2.Runtime.Sim.Model
open Ds2.Runtime.Sim.Engine.Core

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

    // 설정
    abstract SpeedMultiplier: float with get, set
    abstract TimeIgnore: bool with get, set

    // 외부 신호
    abstract InjectIOValue: apiCallGuid: Guid * value: string -> unit

    // 토큰
    abstract SeedToken: sourceWorkGuid: Guid * value: TokenValue -> unit
    abstract DiscardToken: workGuid: Guid -> unit
    abstract GetWorkToken: workGuid: Guid -> TokenValue option

    // 이벤트
    [<CLIEvent>]
    abstract WorkStateChanged: IEvent<WorkStateChangedArgs>
    [<CLIEvent>]
    abstract CallStateChanged: IEvent<CallStateChangedArgs>
    [<CLIEvent>]
    abstract SimulationStatusChanged: IEvent<SimulationStatusChangedArgs>
    [<CLIEvent>]
    abstract TokenEvent: IEvent<TokenEventArgs>
