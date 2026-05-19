namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Core.Store

/// <summary>
/// Pre-FB 조건 편집기 (`SignalPatternSymbolProvider`) 용 store-traversal helpers.
/// AAStoPLC.TagWizard 의 preset DTO 자체는 외부 DLL 의존이라 본 모듈에 들고 오지 않음 —
/// 호출자(C#) 가 preset 을 enumerate 하며 각 entry 마다 본 모듈의 substitute 로 매크로 치환.
/// store 의 Call → ApiCall → ApiDef → System 체인 추적, Call → Work → Flow → System name 추출,
/// 매크로 치환 ($(F)/$(D)/$(A)/$(S)) 만 본 모듈 책임.
/// </summary>
module ConditionSymbolQueries =

    /// <summary>Call 이 호출하는 첫 ApiDef 의 부모 System 의 SystemType (비어있거나 미해석 시 "").</summary>
    [<CompiledName("ResolveCallSystemType")>]
    let resolveCallSystemType (store: DsStore) (call: Call) : string =
        let pickSystemType =
            call.ApiCalls
            |> Seq.tryPick (fun ac ->
                match ac.ApiDefId with
                | Some apiDefId ->
                    match Queries.getApiDef apiDefId store with
                    | Some def ->
                        match Queries.getSystem def.ParentId store with
                        | Some sys ->
                            match sys.SystemType with
                            | Some t when not (String.IsNullOrEmpty t) -> Some t
                            | _ -> None
                        | None -> None
                    | None -> None
                | None -> None)
        defaultArg pickSystemType ""

    /// <summary>Call 의 부모 Work / 그 부모 Flow 이름 — 매크로 치환 인자.</summary>
    [<CompiledName("ResolveFlowWorkName")>]
    let resolveFlowWorkName (store: DsStore) (call: Call) : struct (string * string) =
        match Queries.getWork call.ParentId store with
        | None -> struct ("", "")
        | Some work ->
            match Queries.getFlow work.ParentId store with
            | None -> struct ("", defaultArg (Option.ofObj work.Name) "")
            | Some flow ->
                let flowName = defaultArg (Option.ofObj flow.Name) ""
                let workName = defaultArg (Option.ofObj work.Name) ""
                struct (flowName, workName)

    /// <summary>Call → Work → Flow → Active System 이름 — 매크로 $(S) 치환용.</summary>
    [<CompiledName("ResolveActiveSystemName")>]
    let resolveActiveSystemName (store: DsStore) (call: Call) : string =
        match Queries.getWork call.ParentId store with
        | None -> ""
        | Some work ->
            match Queries.getFlow work.ParentId store with
            | None -> ""
            | Some flow ->
                match Queries.getSystem flow.ParentId store with
                | None -> ""
                | Some sys -> defaultArg (Option.ofObj sys.Name) ""

    /// <summary>$(F)/$(D)/$(A)/$(S) 매크로 치환. null 인자는 빈 문자열로 처리.</summary>
    [<CompiledName("SubstituteMacros")>]
    let substituteMacros (pattern: string) (flow: string) (device: string) (api: string) (system: string) : string =
        if String.IsNullOrEmpty pattern then ""
        else
            let safeFlow   = if isNull flow   then "" else flow
            let safeDevice = if isNull device then "" else device
            let safeApi    = if isNull api    then "" else api
            let safeSystem = if isNull system then "" else system
            pattern
                .Replace("$(F)", safeFlow)
                .Replace("$(D)", safeDevice)
                .Replace("$(A)", safeApi)
                .Replace("$(S)", safeSystem)
