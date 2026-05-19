namespace Ds2.Editor

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

/// <summary>
/// TagWizard Basic 모드 — 매크로 + Flow 선두주소만으로 IO 행 생성.
/// 호출자(C#)가 ApiCall 의 context 해석(외부 AAStoPLC F# 모듈) 결과를
/// <see cref="BasicMacroIo.ApiCallContext"/> 시퀀스로 미리 전달.
/// 본 모듈은 nth 추적 + SignalCounts 필터 + 매크로 expansion + 주소/심볼 계산 + dedup 캐시 담당.
/// </summary>
module BasicMacroIo =

    /// Flow 단위 선두주소(워드 단위 정수).
    type FlowBase = {
        FlowName: string
        IwBase: int
        QwBase: int
        MwBase: int
    }

    type Input = {
        IwMacro: string
        QwMacro: string
        MwMacro: string
        FlowBases: FlowBase list
    }

    /// 호출자가 ApiCall 각각에 대해 미리 해석한 context.
    type ApiCallContext = {
        ParentCallId: Guid
        ApiCallId: Guid
        ApiCallName: string
        FlowName: string
        DeviceAlias: string
        ApiName: string
    }

    /// generation 결과 1행 (callId/apiCallId 단위).
    type GeneratedRow = {
        CallId: Guid
        ApiCallId: Guid
        Flow: string
        Work: string
        Device: string
        Api: string
        InAddress: string
        InSymbol: string
        OutAddress: string
        OutSymbol: string
    }

    /// 매크로 토큰 치환: $(F)/$(D)/$(A).
    let expand (macro: string) (flow: string) (device: string) (api: string) =
        let m = if isNull macro then "" else macro
        m.Replace("$(F)", if isNull flow then "" else flow)
         .Replace("$(D)", if isNull device then "" else device)
         .Replace("$(A)", if isNull api then "" else api)

    /// ApiCall.Name = "{devAlias}.{apiName}" 형식에서 devAlias 추출. '.' 없으면 None.
    let private extractDeviceFromApiCallName (name: string) =
        if String.IsNullOrEmpty(name) then None
        else
            let idx = name.IndexOf('.')
            if idx > 0 then Some(name.Substring(0, idx)) else None

    /// Call.Properties → ControlCallProperties.SignalCounts[ApiName] 조회 헬퍼.
    /// 호출자가 <see cref="generate"/> 의 signalCountLookup 인자로 그대로 넘겨 쓰면 됨.
    let signalCountFromStore (store: DsStore) (callId: Guid) (apiName: string) =
        if String.IsNullOrEmpty(apiName) then None
        else
            match store.Calls.TryGetValue(callId) with
            | true, call ->
                call.Properties
                |> Seq.tryPick (fun p ->
                    match p with
                    | CallSubmodelProperty.ControlCall cc ->
                        match cc.SignalCounts.TryGetValue(apiName) with
                        | true, n -> Some n
                        | _ -> None
                    | _ -> None)
            | _ -> None

    /// 2-segment 포맷 %{prefix}W{word}.{bit} — Pipeline Address.formatAddr 와 일관.
    let private formatAddr (prefix: string) (baseWord: int) (offset: int) =
        sprintf "%%%sW%d.%d" prefix (baseWord + offset / 16) (offset % 16)

    /// context 시퀀스를 받아 GeneratedRow 시퀀스 생성.
    /// nth 추적/SignalCounts 필터/QW 캐시는 모두 본 함수 내 상태.
    /// <paramref name="signalCountLookup"/>: (callId, apiName) → max count.
    /// nth 가 max 초과 시 행 emit skip. 일반적으로 <see cref="signalCountFromStore"/> 사용.
    let generate
            (signalCountLookup: Guid -> string -> int option)
            (input: Input)
            (contexts: ApiCallContext seq)
            : GeneratedRow list =
        let flowMap =
            input.FlowBases
            |> List.map (fun b -> b.FlowName, b)
            |> List.fold
                (fun (m: Dictionary<string, FlowBase>) (k, v) -> m.[k] <- v; m)
                (Dictionary<string, FlowBase>(StringComparer.OrdinalIgnoreCase))

        let counter = Dictionary<string, struct (int * int * int)>(StringComparer.OrdinalIgnoreCase)
        let nthByCallApi = Dictionary<struct (Guid * string), int>()
        let qwCacheByCallApi = Dictionary<struct (Guid * string), struct (string * string)>()
        let rows = ResizeArray<GeneratedRow>()

        for ctx in contexts do
            if not (String.IsNullOrWhiteSpace ctx.FlowName) then
                let apiName = if isNull ctx.ApiName then "" else ctx.ApiName
                let key = struct (ctx.ParentCallId, apiName)
                let prev =
                    match nthByCallApi.TryGetValue(key) with
                    | true, v -> v
                    | _ -> 0
                let nth = prev + 1
                nthByCallApi.[key] <- nth

                let exceeds =
                    match signalCountLookup ctx.ParentCallId apiName with
                    | Some max -> nth > max
                    | None -> false

                if not exceeds then
                    // ApiCall 별 실제 device alias — Name 에서 추출, 실패 시 parent Call 의 DevicesAlias.
                    let perApiDevice =
                        extractDeviceFromApiCallName ctx.ApiCallName
                        |> Option.defaultValue (if isNull ctx.DeviceAlias then "" else ctx.DeviceAlias)

                    // IW 심볼: 매크로 + per-ApiCall device.
                    let inSym =
                        if String.IsNullOrEmpty input.IwMacro then ""
                        else expand input.IwMacro ctx.FlowName perApiDevice apiName

                    // QW 심볼: ApiCall 복제 모드는 같은 솔레노이드 → parent Call 의 DevicesAlias 사용.
                    let qwSymBase =
                        if String.IsNullOrEmpty input.QwMacro then ""
                        else
                            let pd = if isNull ctx.DeviceAlias then "" else ctx.DeviceAlias
                            expand input.QwMacro ctx.FlowName pd apiName

                    let mutable inAddr = ""
                    let mutable outAddr = ""
                    let mutable outSym = ""

                    let struct (iw0, qw0, mw0) =
                        match counter.TryGetValue(ctx.FlowName) with
                        | true, v -> v
                        | _ -> struct (0, 0, 0)
                    let mutable iw = iw0
                    let mutable qw = qw0
                    let mw = mw0

                    match flowMap.TryGetValue(ctx.FlowName) with
                    | true, fb ->
                        if not (String.IsNullOrEmpty inSym) then
                            inAddr <- formatAddr "I" fb.IwBase iw
                            iw <- iw + 1
                        if not (String.IsNullOrEmpty qwSymBase) then
                            match qwCacheByCallApi.TryGetValue(key) with
                            | true, struct (cachedAddr, cachedSym) ->
                                // 같은 (Call, ApiName) 의 후속 ApiCall — QW 주소/심볼 재사용, 카운터 증가 안 함.
                                outAddr <- cachedAddr
                                outSym <- cachedSym
                            | _ ->
                                outAddr <- formatAddr "Q" fb.QwBase qw
                                outSym <- qwSymBase
                                qw <- qw + 1
                                qwCacheByCallApi.[key] <- struct (outAddr, outSym)
                    | _ -> ()

                    counter.[ctx.FlowName] <- struct (iw, qw, mw)

                    rows.Add({
                        CallId = ctx.ParentCallId
                        ApiCallId = ctx.ApiCallId
                        Flow = ctx.FlowName
                        Work = ""
                        Device = perApiDevice
                        Api = apiName
                        InAddress = inAddr
                        InSymbol = inSym
                        OutAddress = outAddr
                        OutSymbol = outSym
                    })

        rows |> List.ofSeq
