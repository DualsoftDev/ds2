/// dsev2(seq) JSON 프로젝트 파일을 ds2 DsStore로 변환하는 모듈.
/// 임시 하위호환용 — 버전 업 시 Compat/ 폴더 통째로 삭제.
namespace Ds2.UI.Core.Compat

open System
open System.Collections.Generic
open System.Text.Json
open log4net
open Ds2.Core
open Ds2.UI.Core

module LegacyJsonImport =

    let private log = LogManager.GetLogger("Ds2.UI.Core.Compat")

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────

    let tryParseGuid (s: string) =
        match Guid.TryParse(s) with true, v -> Some v | _ -> None

    let private getStr (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
        | _ -> None

    let private getGuid (elem: JsonElement) (prop: string) =
        getStr elem prop |> Option.bind tryParseGuid

    let private getArray (elem: JsonElement) (prop: string) =
        match elem.TryGetProperty(prop) with
        | true, v when v.ValueKind = JsonValueKind.Array -> v.EnumerateArray() |> Seq.toList
        | _ -> []

    /// "RB1.START" → ("RB1", "START"),  "SomeCall" → ("SomeCall", "")
    let parseCallName (name: string) =
        match name.LastIndexOf('.') with
        | -1 -> name, ""
        | idx -> name.[.. idx - 1], name.[idx + 1 ..]

    // ── IOTags → IOTag + ValueSpec ────────────────────────────────────────────

    /// dsev2 $type 문자열 → Undefined ValueSpec
    let undefinedFromTypeName (typeName: string) : ValueSpec =
        match typeName with
        | "Boolean" -> BoolValue Undefined  | "SByte"  -> Int8Value Undefined
        | "Int16"   -> Int16Value Undefined | "Int32"  -> Int32Value Undefined
        | "Int64"   -> Int64Value Undefined | "Byte"   -> UInt8Value Undefined
        | "UInt16"  -> UInt16Value Undefined | "UInt32" -> UInt32Value Undefined
        | "UInt64"  -> UInt64Value Undefined | "Single" -> Float32Value Undefined
        | "Double"  -> Float64Value Undefined | "String" -> StringValue Undefined
        | _ -> UndefinedValue

    let tryParseLegacyTag (wrapper: JsonElement) : (IOTag * ValueSpec) option =
        try
            match wrapper.TryGetProperty("Tag") with
            | false, _ -> None
            | true, tag ->
                let name = match tag.TryGetProperty("Name") with true, v -> v.GetString() | _ -> ""
                let addr = match tag.TryGetProperty("Address") with true, v -> v.GetString() | _ -> ""
                let typeName = match tag.TryGetProperty("$type") with true, v -> v.GetString() | _ -> ""
                let spec = undefinedFromTypeName typeName
                Some (IOTag(name |> Option.ofObj |> Option.defaultValue "",
                            addr |> Option.ofObj |> Option.defaultValue "", ""), spec)
        with _ -> None

    let private parseIOTags (ioTagsElem: JsonElement) (apiCall: ApiCall) =
        match ioTagsElem.TryGetProperty("InTag") with
        | true, inTag ->
            tryParseLegacyTag inTag |> Option.iter (fun (tag, spec) ->
                apiCall.InTag <- Some tag
                apiCall.InputSpec <- spec)
        | _ -> ()
        match ioTagsElem.TryGetProperty("OutTag") with
        | true, outTag ->
            tryParseLegacyTag outTag |> Option.iter (fun (tag, spec) ->
                apiCall.OutTag <- Some tag
                apiCall.OutputSpec <- spec)
        | _ -> ()

    // ── JSON → 엔티티 변환 ────────────────────────────────────────────────────

    let private parseApiCall (elem: JsonElement) : ApiCall option =
        try
            let name = getStr elem "Name" |> Option.defaultValue ""
            let guid = getGuid elem "Guid" |> Option.defaultValue (Guid.NewGuid())
            let ac = ApiCall(name)
            ac.Id <- guid
            // Properties.ApiDef
            match elem.TryGetProperty("Properties") with
            | true, props ->
                getStr props "ApiDef" |> Option.bind tryParseGuid |> Option.iter (fun id -> ac.ApiDefId <- Some id)
            | _ -> ()
            // IOTags
            match elem.TryGetProperty("IOTags") with
            | true, ioTags -> parseIOTags ioTags ac
            | _ -> ()
            Some ac
        with ex -> log.Warn($"parseApiCall 실패: {ex.Message}", ex); None

    let private parseApiDef (elem: JsonElement) (systemId: Guid) : ApiDef option =
        try
            let name = getStr elem "Name" |> Option.defaultValue ""
            let guid = getGuid elem "Guid" |> Option.defaultValue (Guid.NewGuid())
            let apiDef = ApiDef(name, systemId)
            apiDef.Id <- guid
            Some apiDef
        with ex -> log.Warn($"parseApiDef 실패: {ex.Message}", ex); None

    let private parseArrow (elem: JsonElement) : (Guid * Guid * Guid * ArrowType) option =
        try
            let guid = getGuid elem "Guid" |> Option.defaultValue (Guid.NewGuid())
            match getGuid elem "Source", getGuid elem "Target" with
            | Some src, Some tgt ->
                let arrowType =
                    match getStr elem "Type" with
                    | Some "Start"      -> ArrowType.Start
                    | Some "Reset"      -> ArrowType.Reset
                    | Some "StartReset" -> ArrowType.StartReset
                    | Some "ResetReset" -> ArrowType.ResetReset
                    | _                 -> ArrowType.Unspecified
                Some (guid, src, tgt, arrowType)
            | _ -> None
        with _ -> None

    let private parseCall (elem: JsonElement) (workId: Guid) (apiCallMap: IDictionary<Guid, ApiCall>) : Call option =
        try
            let callName = getStr elem "Name" |> Option.defaultValue ""
            if String.IsNullOrEmpty callName then None
            else
                let devAlias, apiName = parseCallName callName
                let call = Call(devAlias, apiName, workId)
                getGuid elem "Guid" |> Option.iter (fun g -> call.Id <- g)
                // Properties.ApiCalls[].Guid → ApiCall 연결
                match elem.TryGetProperty("Properties") with
                | true, props ->
                    for acElem in getArray props "ApiCalls" do
                        getGuid acElem "Guid" |> Option.iter (fun acGuid ->
                            match apiCallMap.TryGetValue(acGuid) with
                            | true, ac -> call.ApiCalls.Add(ac)
                            | _ -> ())
                | _ -> ()
                Some call
        with ex -> log.Warn($"parseCall 실패: {ex.Message}", ex); None

    let private parseFlow (elem: JsonElement) (systemId: Guid) : Flow option =
        try
            let flow = Flow("", systemId)
            getGuid elem "Guid" |> Option.iter (fun g -> flow.Id <- g)
            getStr elem "Name" |> Option.iter (fun n -> flow.Name <- n)
            Some flow
        with ex -> log.Warn($"parseFlow 실패: {ex.Message}", ex); None

    let private parseSystem
        (store: DsStore) (elem: JsonElement) (projectId: Guid) (isActive: bool) =
        try
            let system = DsSystem("")
            getGuid elem "Guid" |> Option.iter (fun g -> system.Id <- g)
            getStr elem "Name" |> Option.iter (fun n -> system.Name <- n)
            store.DirectWrite(store.Systems, system)
            (if isActive then store.Projects.[projectId].ActiveSystemIds
             else store.Projects.[projectId].PassiveSystemIds).Add(system.Id) |> ignore

            // Flows
            for fElem in getArray elem "Flows" do
                parseFlow fElem system.Id |> Option.iter (fun f -> store.DirectWrite(store.Flows, f))

            // ApiDefs (passive systems)
            for adElem in getArray elem "ApiDefs" do
                parseApiDef adElem system.Id |> Option.iter (fun d -> store.DirectWrite(store.ApiDefs, d))

            // ApiCalls (active system) → map for Call 연결
            let apiCallMap = Dictionary<Guid, ApiCall>()
            for acElem in getArray elem "ApiCalls" do
                parseApiCall acElem |> Option.iter (fun ac ->
                    apiCallMap.[ac.Id] <- ac
                    if not (store.ApiCalls.ContainsKey(ac.Id)) then
                        store.DirectWrite(store.ApiCalls, ac))

            // Works (System-level)
            for wElem in getArray elem "Works" do
                let defaultFlowId =
                    getGuid wElem "FlowGuid"
                    |> Option.defaultWith (fun () ->
                        store.Flows.Values
                        |> Seq.tryFind (fun f -> f.ParentId = system.Id)
                        |> Option.map (fun f -> f.Id)
                        |> Option.defaultValue Guid.Empty)
                let work = Work("", defaultFlowId)
                getGuid wElem "Guid" |> Option.iter (fun g -> work.Id <- g)
                getStr wElem "Name" |> Option.iter (fun n -> work.Name <- n)
                store.DirectWrite(store.Works, work)

                // Calls inside Work
                for cElem in getArray wElem "Calls" do
                    parseCall cElem work.Id apiCallMap |> Option.iter (fun c ->
                        store.DirectWrite(store.Calls, c))

                // Arrows inside Work → ArrowBetweenCalls (parentId = workId)
                for aElem in getArray wElem "Arrows" do
                    parseArrow aElem |> Option.iter (fun (id, src, tgt, at) ->
                        let arrow = ArrowBetweenCalls(work.Id, src, tgt, at)
                        arrow.Id <- id
                        store.DirectWrite(store.ArrowCalls, arrow))

            // System-level Arrows → ArrowBetweenWorks (parentId = systemId)
            for aElem in getArray elem "Arrows" do
                parseArrow aElem |> Option.iter (fun (id, src, tgt, at) ->
                    let arrow = ArrowBetweenWorks(system.Id, src, tgt, at)
                    arrow.Id <- id
                    store.DirectWrite(store.ArrowWorks, arrow))
        with ex -> log.Warn($"parseSystem 실패: {ex.Message}", ex)

    // ── 판별 + 진입점 ────────────────────────────────────────────────────────

    /// 파일 내용이 dsev2 레거시 JSON 형식인지 판별.
    /// 최상위에 "RuntimeType":"Project" 가 있으면 레거시.
    let isLegacyJsonFormat (json: string) : bool =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            match getStr root "RuntimeType" with
            | Some "Project" -> true
            | _ -> false
        with _ -> false

    /// dsev2 JSON을 파싱하여 DsStore를 채운다.
    let importLegacyJson (store: DsStore) (json: string) : bool =
        try
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement

            let project = Project("")
            getGuid root "Guid" |> Option.iter (fun g -> project.Id <- g)
            getStr root "Name" |> Option.iter (fun n -> project.Name <- n)
            store.DirectWrite(store.Projects, project)

            for sElem in getArray root "ActiveSystems" do
                parseSystem store sElem project.Id true
            for sElem in getArray root "PassiveSystems" do
                parseSystem store sElem project.Id false

            log.Info($"Legacy JSON import: '{project.Name}'")
            true
        with ex ->
            log.Warn($"Legacy JSON import failed: {ex.Message}", ex)
            false
