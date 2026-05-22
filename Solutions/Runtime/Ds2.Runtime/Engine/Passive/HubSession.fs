namespace Ds2.Runtime.Engine.Passive

open System
open Ds2.Core
open Ds2.Core.Store
open Ds2.Runtime.Engine.Core
open Ds2.Runtime.IO

type RuntimeHubSession(index: SimIndex, ioMap: SignalIOMap, runtimeMode: RuntimeMode) =

    /// ApiCallGuid → ApiCall lookup (store 의 ApiCalls 직접 dict 조회).
    /// HubSession 의 spec 기반 판정/echo 가 여기 lookup 결과를 사용.
    let tryGetApiCall (apiCallGuid: Guid) : ApiCall option =
        Queries.getApiCall apiCallGuid index.Store

    /// inMappings 중 *어떤* mapping 의 ApiCall.InputSpec 으로 평가했을 때 active 인 것이 있는지.
    /// 같은 InAddress 에 여러 ApiCall (예: 공유 신호) 매핑 가능 — 하나라도 통과하면 trigger.
    let anyInputActive (mappings: SignalMapping list) (value: string) : bool =
        mappings
        |> List.exists (fun m ->
            tryGetApiCall m.ApiCallGuid
            |> Option.map (fun ac -> RuntimeSemantics.isActiveInputValue ac value)
            |> Option.defaultValue (value = "true"))   // ApiCall lookup 실패 시 legacy 동작 fallback

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
                // spec 기반 active 판정 — 이전엔 `value = "true"` 하드코딩. Int8/Float 등 InputSpec 대응.
                if anyInputActive inMappings value then
                    for mapping in inMappings do
                        match mapping.RxWorkGuid with
                        // currentState=Going 일 때만 atomic Force. 외부 PLC 의 stale IN=true 가
                        // Reset 흐름 도중 Homing→Finish 잘못 전이시키는 race 차단.
                        | Some rxWorkGuid -> RuntimeSessionEffects.addForceWorkStateIfGoing effects 0 rxWorkGuid Status4.Finish
                        | None -> ()

                RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Finish (sprintf "[Ctrl] In %s=%s (from %s)" address value source)
            else
                RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Warn (sprintf "[Ctrl] %s=%s [unmapped]" address value)

        | RuntimeMode.VirtualPlant ->
            match ioMap.GetByOutAddress(address) |> List.tryHead with
            | Some mapping ->
                match mapping.TxWorkGuid with
                | Some txWorkGuid ->
                    // spec 기반 active 판정 — 이전엔 `value = "true"` 하드코딩.
                    // Promaker(Control) 가 OutputSpec=Int8(Single 5) 로 "5" 송출하면 여기서 통과해야 echo 발생.
                    if isOutputActive mapping value then
                        RuntimeSessionEffects.addForceWorkState effects 0 txWorkGuid Status4.Going

                        match index.WorkResetPreds |> Map.tryFind txWorkGuid with
                        | Some resetPreds ->
                            for predGuid in resetPreds |> Seq.distinct do
                                match ioMap.RxWorkToInAddresses |> Map.tryFind predGuid with
                                | Some resetInAddresses ->
                                    for resetInAddr in resetInAddresses do
                                        // 주소별 InputSpec 의 reset value (Bool="false" / Int="0" / String="").
                                        let resetVal = resetInValueForAddress resetInAddr
                                        RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Homing (sprintf "[VP] Reset input: %s=%s" resetInAddr resetVal)
                                        RuntimeSessionEffects.addWriteTag effects 0 resetInAddr resetVal
                                | None -> ()
                        | None -> ()

                        RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Going (sprintf "[VP] Out ON: %s -> Device Going" address)

                        if not (String.IsNullOrEmpty(mapping.InAddress)) then
                            let duration =
                                index.WorkDuration
                                |> Map.tryFind txWorkGuid
                                |> Option.map int
                                |> Option.defaultValue 500

                            // InAddress echo 값 — InputSpec 의 active 대표값 (Bool="true" / Int8(Single 5)="5").
                            let activeIn = activeInValueFor mapping
                            RuntimeSessionEffects.addForceWorkState effects duration txWorkGuid Status4.Finish
                            RuntimeSessionEffects.addWriteTag effects duration mapping.InAddress activeIn
                            RuntimeSessionEffects.addLog effects duration RuntimeHubLogSeverity.Finish (sprintf "[VP] In ON: %s=%s (after %dms)" mapping.InAddress activeIn duration)
                    else if not (String.IsNullOrEmpty(mapping.InAddress)) then
                        // Output 이 inactive 로 떨어졌을 때 — InAddress 도 reset value 로.
                        let resetIn = resetInValueFor mapping
                        RuntimeSessionEffects.addWriteTag effects 0 mapping.InAddress resetIn
                        RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Ready (sprintf "[VP] In OFF: %s=%s" mapping.InAddress resetIn)
                | None -> ()
            | None -> ()

            RuntimeSessionEffects.addPassiveObserve effects 0 address value

        | RuntimeMode.Monitoring ->
            RuntimeSessionEffects.addLog effects 0 RuntimeHubLogSeverity.Info (sprintf "[Mon] %s=%s (from %s)" address value source)
            RuntimeSessionEffects.addPassiveObserve effects 0 address value

        | RuntimeMode.Simulation ->
            ()
        | _ ->
            ()

        effects.ToArray()
