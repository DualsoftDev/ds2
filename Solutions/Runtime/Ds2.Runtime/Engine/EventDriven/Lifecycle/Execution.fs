namespace Ds2.Runtime.Engine

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Engine.Core

type ApiCallExecutionContext = {
    RuntimeMode: RuntimeMode
    GetDeviceState: Guid -> Status4
    GetDeviceName: Guid -> string
    GetTxOutAddresses: Guid -> string list
    /// v10 §11 — Work GUID → 해당 Work 가 가리키는 (ApiDef, ApiCall) 쌍 목록.
    /// 각 쌍에 대해 RuntimeSemantics.emitOutput dispatch.
    GetApiCallsForWork: Guid -> (ApiDef * ApiCall) list
    WriteTag: string -> string -> unit
    /// v10 §11 — TimePolicy.Append / EdgePulse 의 시간 지연 적용용 Scheduler 위임.
    /// (delayMs, action) → delayMs 후 action 실행.
    ScheduleAfter: int * (unit -> unit) -> unit
    /// v10 §11 — 출력 주소를 delayMs 후 지정 값으로 쓰기.
    ScheduleOutputWrite: int * string * string -> unit
    /// v10 §7 — Device 단위 Latch Mutex. 새 ApiCall 진입 시 기존 Latched 출력 자동 reset.
    /// (deviceId, currentApiCallId) → 기존 active Latched ApiCall 의 OutTag 들을 "false" 로 송출하고 registry 갱신.
    ResetPriorLatchesOnDevice: Guid * Guid -> unit
    /// v10 §7 — 본 ApiCall 의 ActionType=Latched 인 경우 device registry 에 등록.
    RegisterLatch: Guid * Guid * IOTag -> unit
    ForceWorkState: Guid -> Status4 -> unit
}

type CallTransitionApplyContext = {
    RuntimeMode: RuntimeMode
    GetCallOutputResets: Guid -> CallOutputReset list
    WriteTag: string -> string -> unit
    ScheduleOutputWrite: int * string * string -> unit
    GetCallState: Guid -> Status4
    ApplyCallTransitionCore: Guid -> Status4 -> unit
}

and CallOutputReset =
    | ResetNow of address: string * value: string
    | ResetAfter of address: string * delayMs: int * value: string

type DurationCompleteContext = {
    /// plan-duration 대조는 Control/Monitoring 에서만. Simulation/VP 의 device work 는
    /// duration 으로 정상 Finish (실 IO 없이 plan=actual / VP echo) — IsDeviceWork 분기 안 탐.
    RuntimeMode: RuntimeMode
    IsPassiveMode: bool
    /// device work(passive system 의 work) 판정. plan-duration 만료 시 Finish 진행 대신 abnormal 대조.
    IsDeviceWork: Guid -> bool
    /// device work 의 plan-duration(Range.Max) 만료 콜백 — actual In 미도달이면 ActionOver (Composition 이 adapter 연결).
    OnDeviceDurationExpired: Guid -> unit
    RemoveDuration: Guid -> unit
    GetWorkState: Guid -> Status4
    MarkMinDurationMet: Guid -> unit
    GetWorkCallGuids: Guid -> Guid list
    GetCallState: Guid -> Status4
    ApplyWorkTransition: Guid -> Status4 -> unit
    /// v10 §10 — 이 Work 가 가진 ApiCall 들의 SensingType 중 *Append ms* 의 최대값.
    /// 0 = 추가 대기 없음. > 0 = ms 만큼 Finish 인정 연기.
    GetMaxSensingAppendMs: Guid -> int
    /// 이미 §10 추가 대기 적용했는지 flag (Work 단위). false → 1차 호출. true → 2차 호출 (확정 Finish).
    IsSensingAppendApplied: Guid -> bool
    /// v10 §10 추가 대기 적용 표시 + ms 후 DurationComplete 재 schedule.
    ApplySensingAppendDelay: Guid * int -> unit
    ScheduleConditionEvaluation: unit -> unit
}

module EventDrivenExecution =
    // **A2 (s6-r83, 15-reviewer Critical)** — 이전 박제는 매 executeApiCall 마다 MyDocuments\ds2_execapi_*.txt
    // 에 unconditional file append + 광범위 try-swallow → production binary 안 디버그 코드 잔존 + disk leak.
    // log4net SSOT 로 교체 (SimIndex / 기존 Runtime module 동일 패턴).
    let private log = log4net.LogManager.GetLogger("EventDrivenExecution")

    /// v10 §11.1 emitOutput effect 적용.
    /// - Control 모드: WriteTag 로 실 PLC OUT 송출 + 시간 정책 schedule.
    /// - Simulation 모드: SimState 의 IOValue 만 갱신 (시각화용) — 실 OUT 안 씀.
    /// - Monitoring/VirtualPlant: 외부 PLC 가 진실원 — no-op.
    let private applyOutputEffect (ctx: ApiCallExecutionContext) (apiDef: ApiDef) (apiCall: ApiCall) =
        match ctx.RuntimeMode with
        | RuntimeMode.Monitoring
        | RuntimeMode.VirtualPlant -> ()
        | _ ->
            let effect =
                try RuntimeSemantics.emitOutput apiDef apiCall |> Some
                with _ -> None

            let writeOut (addr: string) (v: string) =
                // Control: 실제 송출. Simulation: WriteTag 가 시뮬 OutputValues update 위임 (CompositionContext 가 wire).
                ctx.WriteTag addr v
            let scheduleWriteOut delayMs addr value =
                ctx.ScheduleOutputWrite(delayMs, addr, value)

            match effect with
            | None -> ()
            | Some (RuntimeSemantics.OutCoil tag) ->
                writeOut tag.Address (RuntimeSemantics.activeOutputValue apiCall)
            | Some (RuntimeSemantics.CoilAfterDelay (tag, _)) ->
                writeOut tag.Address (RuntimeSemantics.activeOutputValue apiCall)
            | Some (RuntimeSemantics.EdgePulse tag) ->
                writeOut tag.Address (RuntimeSemantics.activeOutputValue apiCall)
                scheduleWriteOut 1 tag.Address (RuntimeSemantics.resetOutputValue apiCall)
            | Some (RuntimeSemantics.EdgePulseHold (tag, ms)) ->
                writeOut tag.Address (RuntimeSemantics.activeOutputValue apiCall)
                scheduleWriteOut ms tag.Address (RuntimeSemantics.resetOutputValue apiCall)
            | Some (RuntimeSemantics.SetCoil tag) ->
                writeOut tag.Address (RuntimeSemantics.activeOutputValue apiCall)
            | Some RuntimeSemantics.NoOp ->
                ()
            | Some (RuntimeSemantics.NoOpAppend _) ->
                ()

    let executeApiCall (ctx: ApiCallExecutionContext) deviceWorkGuid =
        let curSt = ctx.GetDeviceState deviceWorkGuid
        let wname = ctx.GetDeviceName deviceWorkGuid

        log.Debug(sprintf "executeApiCall: mode=%A device=%s state=%A" ctx.RuntimeMode wname curSt)

        if curSt <> Status4.Finish then
            match ctx.RuntimeMode with
            | RuntimeMode.Simulation
            | RuntimeMode.Control ->
                // v10 §11 + §7 — 각 ApiCall 의 ActionType 따라 다른 effect 적용.
                // §7 Latch Mutex: 새 ApiCall 진입 시 같은 Device 의 기존 Latched 출력 자동 reset.
                for (apiDef, apiCall) in ctx.GetApiCallsForWork deviceWorkGuid do
                    let deviceId = apiDef.ParentId  // ApiDef.ParentId = DsSystem.Id (Device)
                    ctx.ResetPriorLatchesOnDevice (deviceId, apiCall.Id)
                    applyOutputEffect ctx apiDef apiCall
                    match apiDef.ActionType, apiCall.OutTag with
                    | ActionType.Real (Latched, _), Some tag ->
                        ctx.RegisterLatch (deviceId, apiCall.Id, tag)
                    | _ -> ()
                ctx.ForceWorkState deviceWorkGuid Status4.Going
            | RuntimeMode.Monitoring
            | RuntimeMode.VirtualPlant ->
                ()
            | _ ->
                ()

    let executeCallGoing index (ctx: ApiCallExecutionContext) callGuid =
        SimIndex.txWorkGuids index callGuid |> List.iter (executeApiCall ctx)

    let executeCallHoming index (ctx: ApiCallExecutionContext) (callGuid: Guid) (goingTargets: Set<Guid>) =
        for txGuid in SimIndex.txWorkGuids index callGuid do
            if goingTargets.Contains txGuid then
                executeApiCall ctx txGuid

    let private resetOutTagsForCall (ctx: CallTransitionApplyContext) callGuid =
        if ctx.RuntimeMode = RuntimeMode.Control || ctx.RuntimeMode = RuntimeMode.Simulation then
            for reset in ctx.GetCallOutputResets callGuid do
                match reset with
                | ResetNow (address, value) ->
                    ctx.WriteTag address value
                | ResetAfter (address, delayMs, value) ->
                    ctx.ScheduleOutputWrite(delayMs, address, value)

    let applyCallTransition (ctx: CallTransitionApplyContext) callGuid newState =
        let prevState = ctx.GetCallState callGuid
        ctx.ApplyCallTransitionCore callGuid newState
        let curState = ctx.GetCallState callGuid

        if prevState <> Status4.Finish && curState = Status4.Finish then
            resetOutTagsForCall ctx callGuid

    let handleDurationComplete (ctx: DurationCompleteContext) workGuid =
        ctx.RemoveDuration workGuid

        // device work 는 plan(duration) 으로 정상 Finish — 모든 모드, In 무관.
        //   (In/actual 은 abnormal adapter 가 plan 과 대조할 뿐 Finish 경로엔 개입 안 한다.)
        // 비-device work 는 passive 모드(VP/Monitoring)에서 passive inference 가 상태 owner 라 자동 Finish 안 함.
        let isDevice = ctx.IsDeviceWork workGuid
        if ctx.GetWorkState workGuid = Status4.Going && (isDevice || not ctx.IsPassiveMode) then
            // v10 §10 — SensingType.Virtual(Some Append ms) 의 추가 대기.
            // 1차 호출: append ms > 0 + 아직 미적용 → ms 후 재 schedule 하고 return.
            // 2차 호출 (또는 append=0): Finish 진행.
            let extraMs = ctx.GetMaxSensingAppendMs workGuid
            if extraMs > 0 && not (ctx.IsSensingAppendApplied workGuid) then
                ctx.ApplySensingAppendDelay (workGuid, extraMs)
            else
                ctx.MarkMinDurationMet workGuid
                ctx.ScheduleConditionEvaluation()

                let callGuids = ctx.GetWorkCallGuids workGuid

                if isDevice then
                    ctx.ApplyWorkTransition workGuid Status4.Finish
                elif callGuids.IsEmpty then
                    ctx.ApplyWorkTransition workGuid Status4.Finish
                elif callGuids |> List.forall (fun callGuid -> ctx.GetCallState callGuid = Status4.Finish) then
                    ctx.ApplyWorkTransition workGuid Status4.Finish
