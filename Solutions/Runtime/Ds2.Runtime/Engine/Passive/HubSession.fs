namespace Ds2.Runtime.Engine.Passive

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type RuntimeHubSession(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =

    /// ApiCallGuid → ApiCall lookup (store 의 ApiCalls 직접 dict 조회).
    /// HubSession 의 spec 기반 판정/echo 가 여기 lookup 결과를 사용.
    let tryGetApiCall (apiCallGuid: Guid) : ApiCall option =
        Queries.getApiCall apiCallGuid index.Store

    /// VP 측 output→active 판정 — output mapping 의 ApiCall.OutputSpec 으로 평가.
    let isOutputActive (mapping: SignalMapping) (value: string) : bool =
        tryGetApiCall mapping.ApiCallGuid
        |> Option.map (fun ac -> RuntimeSemantics.isActiveOutputValue ac value)
        |> Option.defaultValue (value = "true")

    /// InAddress 로 echo 할 *active* 값 — 해당 ApiCall.InputSpec 의 default string.
    /// Bool 이면 "true", Int8(Single 5) 면 "5".
    let activeInValueFor (mapping: SignalMapping) : string =
        tryGetApiCall mapping.ApiCallGuid
        |> Option.map RuntimeSemantics.activeInputValue
        |> Option.defaultValue "true"

    /// InAddress 로 echo 할 *reset* 값 — 해당 ApiCall.InputSpec 의 type 별 reset.
    let resetInValueFor (mapping: SignalMapping) : string =
        tryGetApiCall mapping.ApiCallGuid
        |> Option.map RuntimeSemantics.resetInputValue
        |> Option.defaultValue "false"

    /// 주소 기반 reset value — resetInAddr 의 첫 매핑의 ApiCall.InputSpec 기준.
    /// (mapping 객체 없이 주소만 알 때 — VP 의 cross-reset path.)
    let resetInValueForAddress (resetInAddr: string) : string =
        ioMap.GetByInAddress(resetInAddr)
        |> List.tryHead
        |> Option.map resetInValueFor
        |> Option.defaultValue "false"

    member _.HandleHubTag(address: string, value: string, source: string) =
        let effects = ResizeArray<RuntimeHubEffect>()

        match runtimeMode with
        | RuntimeMode.Control ->
            let inMappings = ioMap.GetByInAddress(address)
            if not (List.isEmpty inMappings) then
                RuntimeSessionEffects.addInjectIo effects address value
                RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Finish (sprintf "[Ctrl] In %s=%s (from %s)" address value source)
            else
                RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Warn (sprintf "[Ctrl] %s=%s [unmapped]" address value)

        | RuntimeMode.VirtualPlant ->
            // The wire carries an address/value, not the requesting Call ID. Match every
            // physical binding for this signal; declaration order must not select the command.
            let active, inactive =
                ioMap.GetByOutAddress(address)
                |> List.distinctBy (fun mapping -> mapping.ApiCallGuid)
                |> List.filter (fun mapping -> mapping.TxWorkGuid.IsSome)
                |> List.partition (fun mapping -> isOutputActive mapping value)

            let activeInputs = active |> List.map (fun mapping -> mapping.InAddress) |> Set.ofList
            let writes = HashSet<int * string * string>()
            let writeOnce delay input inputValue severity message =
                if not (String.IsNullOrEmpty input) && writes.Add((delay, input, inputValue)) then
                    RuntimeSessionEffects.addWriteTag effects delay input inputValue
                    RuntimeSessionEffects.addLog effects delay severity message

            for mapping in inactive do
                // A shared sensor address that is active in this event must not also be reset.
                if not (activeInputs.Contains mapping.InAddress) then
                    let resetIn = resetInValueFor mapping
                    writeOnce 0 mapping.InAddress resetIn RuntimeHubLogSeverity.Ready (sprintf "[VP] In OFF: %s=%s" mapping.InAddress resetIn)

            for txWorkGuid, mappings in active |> List.groupBy (fun mapping -> mapping.TxWorkGuid.Value) do
                // One device start per received signal, guarded by the engine's Ready state.
                RuntimeSessionEffects.addForceWorkStateIfReady effects 0 txWorkGuid Status4.Going

                match index.WorkResetPreds |> Map.tryFind txWorkGuid with
                | Some resetPreds ->
                    for predGuid in resetPreds |> Seq.distinct do
                        match ioMap.RxWorkToInAddresses |> Map.tryFind predGuid with
                        | Some resetInAddresses ->
                            for resetInAddr in resetInAddresses do
                                let resetValue = resetInValueForAddress resetInAddr
                                writeOnce 0 resetInAddr resetValue RuntimeHubLogSeverity.Homing (sprintf "[VP] Reset input: %s=%s" resetInAddr resetValue)
                        | None -> ()
                | None -> ()

                RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Going (sprintf "[VP] Out ON: %s -> Device Going" address)

                // Preserve the existing 500ms fallback for an unspecified/zero duration.
                let duration =
                    index.WorkDuration
                    |> Map.tryFind txWorkGuid
                    |> Option.map int
                    |> Option.filter (fun d -> d > 0)
                    |> Option.defaultValue 500

                for mapping in mappings do
                    // Finish remains owned by the engine. IN is delivered through hub replay.
                    let activeIn = activeInValueFor mapping
                    writeOnce duration mapping.InAddress activeIn RuntimeHubLogSeverity.Finish (sprintf "[VP] In ON: %s=%s (after %dms)" mapping.InAddress activeIn duration)

            let inMappings = ioMap.GetByInAddress(address)
            if not (List.isEmpty inMappings) then
                RuntimeSessionEffects.addInjectIo effects address value

            RuntimeSessionEffects.addPassiveObserve effects 0 address value

        | RuntimeMode.Monitoring ->
            // Monitoring 도 Control/VP 처럼 device work cycle 을 engine 내부 상태로 구동한다(=plan).
            // 단 실 장비가 actual IO 의 진실원이므로 PLC 재기록(WriteTag)은 안 한다(read-only).
            //   Out On → device Work Going (plan 시작; 상호리셋은 engine triggerImmediateResets 가 담당, isPassive device 해제)
            //   In active → device Work Finish (실제 actual 도달; Going 일 때만 atomic Force)
            // passive inference(addPassiveObserve)는 active Work/Call 추론을 계속 담당.
            ioMap.GetByOutAddress(address)
            |> List.filter (fun mapping -> isOutputActive mapping value)
            |> List.choose (fun mapping -> mapping.TxWorkGuid)
            |> List.distinct
            |> List.iter (fun txWorkGuid ->
                RuntimeSessionEffects.addForceWorkStateIfReady effects 0 txWorkGuid Status4.Going)

            let inMappings = ioMap.GetByInAddress(address)
            if not (List.isEmpty inMappings) then
                // engine IOValues 갱신(abnormal actual 대조의 진실원). device Finish 는 engine plan-duration owner — In 으로 force 안 함.
                RuntimeSessionEffects.addInjectIo effects address value

            RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Info (sprintf "[Mon] %s=%s (from %s)" address value source)
            RuntimeSessionEffects.addPassiveObserve effects 0 address value

        | RuntimeMode.Simulation ->
            ()
        | _ ->
            ()

        effects.ToArray()
