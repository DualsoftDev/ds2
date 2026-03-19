/// dsev2(seq) AASX의 그래프 구조를 ds2 도메인 엔티티로 변환하는 모듈.
/// 임시 하위호환용 — 버전 업 시 Compat/ 폴더 통째로 삭제.
namespace Ds2.Aasx.Compat

open System
open System.Collections.Generic
open AasCore.Aas3_0
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.UI.Core
open Ds2.UI.Core.Compat

module LegacyImportGraph =

    open Ds2.Aasx.AasxImportCore

    let private log = LogManager.GetLogger("Ds2.Aasx.Compat")

    // ── 공유 헬퍼 재사용 (Ds2.UI.Core.Compat.LegacyJsonImport) ──────────────

    let private tryParseGuid = LegacyJsonImport.tryParseGuid
    let parseCallName = LegacyJsonImport.parseCallName

    // ── seq Properties JSON 호환 ────────────────────────────────────────────

    type private LegacyCallProps = {
        Xywh: Xywh option
        ApiCalls: {| Guid: string |} array option
    }

    let private tryParseLegacyCallProps (smc: SubmodelElementCollection) : Xywh option * Guid list =
        match getProp smc Properties_ with
        | None -> None, []
        | Some json ->
            try
                let props = Ds2.Serialization.JsonConverter.deserialize<LegacyCallProps> json
                let guids =
                    props.ApiCalls
                    |> Option.map (Array.choose (fun ac -> tryParseGuid ac.Guid) >> Array.toList)
                    |> Option.defaultValue []
                props.Xywh, guids
            with _ -> None, []

    type private LegacyApiCallProps = { ApiDef: string option }

    let private tryParseLegacyApiDefId (smc: SubmodelElementCollection) : Guid option =
        getProp smc Properties_
        |> Option.bind (fun json ->
            try
                let props = Ds2.Serialization.JsonConverter.deserialize<LegacyApiCallProps> json
                props.ApiDef |> Option.bind tryParseGuid
            with _ -> None)

    // ── dsev2 IOTags JSON → ds2 IOTag + ValueSpec 변환 ──────────────────────

    open System.Text.Json

    /// dsev2 IOTags JSON에서 InTag/OutTag 하나를 파싱 (key로 프로퍼티 조회 후 공유 헬퍼 위임).
    let private tryParseLegacyTag (root: JsonElement) (key: string) : (IOTag * ValueSpec) option =
        try
            match root.TryGetProperty(key) with
            | false, _ -> None
            | true, wrapper -> LegacyJsonImport.tryParseLegacyTag wrapper
        with _ -> None

    /// dsev2 ApiCall SMC의 IOTags 프로퍼티에서 InTag/OutTag + InputSpec/OutputSpec 추출.
    let private parseLegacyIOTags (smc: SubmodelElementCollection) (apiCall: ApiCall) =
        getProp smc "IOTags"
        |> Option.iter (fun json ->
            try
                use doc = JsonDocument.Parse(json)
                let root = doc.RootElement
                tryParseLegacyTag root "InTag" |> Option.iter (fun (tag, spec) ->
                    apiCall.InTag <- Some tag
                    apiCall.InputSpec <- spec)
                tryParseLegacyTag root "OutTag" |> Option.iter (fun (tag, spec) ->
                    apiCall.OutTag <- Some tag
                    apiCall.OutputSpec <- spec)
            with _ -> ())

    // ── SMC → 도메인 엔티티 변환 (smcToFlow/smcToApiDef는 AasxImportGraph 재사용) ──

    open Ds2.Aasx.AasxImportGraph

    let private legacySmcToApiCall (smc: SubmodelElementCollection) : ApiCall option =
        try
            let name = getProp smc Name_ |> Option.defaultValue ""
            let guid = getProp smc Guid_ |> Option.bind tryParseGuid |> Option.defaultValue (Guid.NewGuid())
            let apiCall = ApiCall(name)
            apiCall.Id <- guid
            apiCall.ApiDefId <- tryParseLegacyApiDefId smc
            parseLegacyIOTags smc apiCall
            Some apiCall
        with ex -> log.Warn($"legacySmcToApiCall 실패: {ex.Message}", ex); None

    // ── Work 파싱 + Call 매칭 (중복 제거된 헬퍼) ────────────────────────────

    /// Work SMC → Work 엔티티. FlowGuid 없으면 fallbackFlowId 사용.
    let private parseWork (workSmc: SubmodelElementCollection) (fallbackFlowId: Guid) : Work =
        let flowId =
            getProp workSmc FlowGuid_
            |> Option.bind tryParseGuid
            |> Option.defaultValue fallbackFlowId
        let work = Work("", flowId)
        getProp workSmc Guid_ |> Option.iter (fun g -> work.Id <- Guid.Parse g)
        getProp workSmc Name_ |> Option.iter (fun n -> work.Name <- n)
        fromJsonProp<WorkProperties> workSmc Properties_ |> Option.iter (fun p -> work.Properties <- p)
        fromJsonProp<Xywh option> workSmc Position_ |> Option.flatten |> Option.iter (fun pos -> work.Position <- Some pos)
        getProp workSmc Status_ |> Option.iter (fun s -> work.Status4 <- parseStatus4 s)
        work

    /// seq Call.Name이 Work에 속하는지 판정: "WorkName.xxx" 또는 "WorkName_xxx.yyy"
    let private callBelongsToWork (callFullName: string) (workName: string) =
        callFullName.StartsWith(workName + ".", StringComparison.OrdinalIgnoreCase)
        || callFullName.StartsWith(workName + "_", StringComparison.OrdinalIgnoreCase)
        || callFullName = workName

    /// Call SMC → ds2 Call 엔티티. Name에서 DevicesAlias.ApiName 분리 + ApiCall 연결.
    let private parseCallSmc (callSmc: SubmodelElementCollection) (apiCallMap: IDictionary<Guid, ApiCall>) (workId: Guid) : Call option =
        let callFullName = getProp callSmc Name_ |> Option.defaultValue ""
        if String.IsNullOrEmpty callFullName then None
        else
            let devAlias, apiName = parseCallName callFullName
            let call = Call(devAlias, apiName, workId)
            getProp callSmc Guid_ |> Option.iter (fun g -> call.Id <- Guid.Parse g)
            let xywh, apiCallGuids = tryParseLegacyCallProps callSmc
            xywh |> Option.iter (fun pos -> call.Position <- Some pos)
            for acGuid in apiCallGuids do
                match apiCallMap.TryGetValue(acGuid) with
                | true, apiCall -> call.ApiCalls.Add(apiCall)
                | _ -> ()
            Some call

    /// Flow-level Call SMC 목록에서 Work에 매칭되는 Call을 찾아 변환 (Pattern A용)
    let private matchCallsToWork
        (flowCallSmcs: SubmodelElementCollection list)
        (apiCallMap: IDictionary<Guid, ApiCall>)
        (work: Work)
        : Call list =
        flowCallSmcs |> List.choose (fun callSmc ->
            let callFullName = getProp callSmc Name_ |> Option.defaultValue ""
            if not (callBelongsToWork callFullName work.Name) then None
            else parseCallSmc callSmc apiCallMap work.Id)

    // ── System 변환 ─────────────────────────────────────────────────────────

    let legacySmcToSystem (smc: SubmodelElementCollection)
        : (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) option =
        try
            let name = getProp smc Name_
            let guid = getProp smc Guid_
            if name.IsNone && guid.IsNone then
                log.Error("Legacy import: System missing Name and Guid"); None
            else
                let system = DsSystem("")
                guid |> Option.iter (fun g -> system.Id <- Guid.Parse g)
                name |> Option.iter (fun n -> system.Name <- n)
                fromJsonProp<SystemProperties> smc Properties_ |> Option.iter (fun p -> system.Properties <- p)
                system.IRI <- getProp smc IRI_ |> Option.bind (fun s -> if String.IsNullOrEmpty s then None else Some s)

                let apiDefs = getChildSmlSmcs smc ApiDefs_ |> List.choose (fun a -> smcToApiDef a system.Id)
                let apiCallMap = getChildSmlSmcs smc ApiCalls_ |> List.choose legacySmcToApiCall |> List.map (fun ac -> ac.Id, ac) |> dict

                // Flow 파싱 (공통)
                let flowPairs =
                    getChildSmlSmcs smc Flows_
                    |> List.choose (fun fSmc -> smcToFlow fSmc system.Id |> Option.map (fun f -> fSmc, f))
                let allFlows = flowPairs |> List.map snd

                let mutable allWorks = []
                let mutable allCalls = []
                let mutable allArrowCalls = []

                // Pattern 감지: System-level Works가 있으면 Pattern B (CommPre/S_COMPL)
                let systemLevelWorks = getChildSmlSmcs smc Works_
                if systemLevelWorks <> [] then
                    // Pattern B: Works at System level, Calls inside Work
                    let defaultFlowId = allFlows |> List.tryHead |> Option.map (fun f -> f.Id) |> Option.defaultValue Guid.Empty
                    for workSmc in systemLevelWorks do
                        let work = parseWork workSmc defaultFlowId
                        allWorks <- work :: allWorks
                        for callSmc in getChildSmlSmcs workSmc Calls_ do
                            parseCallSmc callSmc apiCallMap work.Id |> Option.iter (fun c -> allCalls <- c :: allCalls)
                        let workGuid = getProp workSmc Guid_ |> Option.bind tryParseGuid |> Option.defaultValue Guid.Empty
                        allArrowCalls <- (getChildSmlSmcs workSmc Arrows_ |> List.choose (fun a -> smcToArrowCall a workGuid)) @ allArrowCalls
                else
                    // Pattern A: Works inside Flow, Calls at Flow level
                    for (flowSmc, flow) in flowPairs do
                        let flowCallSmcs = getChildSmlSmcs flowSmc Calls_
                        for workSmc in getChildSmlSmcs flowSmc Works_ do
                            let work = parseWork workSmc flow.Id
                            allWorks <- work :: allWorks
                            allCalls <- matchCallsToWork flowCallSmcs apiCallMap work @ allCalls
                            let workGuid = getProp workSmc Guid_ |> Option.bind tryParseGuid |> Option.defaultValue Guid.Empty
                            allArrowCalls <- (getChildSmlSmcs workSmc Arrows_ |> List.choose (fun a -> smcToArrowCall a workGuid)) @ allArrowCalls

                let arrowWorks = getChildSmlSmcs smc Arrows_ |> List.choose (fun a -> smcToArrowWork a system.Id)
                Some (system, List.rev allFlows, List.rev allWorks, List.rev allCalls, List.rev allArrowCalls, arrowWorks, apiDefs)
        with ex -> log.Warn($"legacySmcToSystem 실패: {ex.Message}", ex); None

    // ── Store 채우기 ────────────────────────────────────────────────────────

    let private populateStore
        (store: DsStore) (project: Project) (isActive: bool)
        (results: (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) list) =
        for (system, flows, works, calls, arrowCalls, arrowWorks, apiDefs) in results do
            store.DirectWrite(store.Systems, system)
            (if isActive then project.ActiveSystemIds else project.PassiveSystemIds).Add(system.Id) |> ignore
            flows      |> List.iter (fun f -> store.DirectWrite(store.Flows, f))
            works      |> List.iter (fun w -> store.DirectWrite(store.Works, w))
            calls      |> List.iter (fun c ->
                store.DirectWrite(store.Calls, c)
                c.ApiCalls |> Seq.iter (fun ac ->
                    if not (store.ApiCalls.ContainsKey(ac.Id)) then store.DirectWrite(store.ApiCalls, ac)))
            arrowCalls |> List.iter (fun a -> store.DirectWrite(store.ArrowCalls, a))
            arrowWorks |> List.iter (fun a -> store.DirectWrite(store.ArrowWorks, a))
            apiDefs    |> List.iter (fun d -> store.DirectWrite(store.ApiDefs, d))

    // ── 진입점 ──────────────────────────────────────────────────────────────

    let legacySubmodelToProjectStore (sm: ISubmodel) : (Project * DsStore) option =
        try
            if sm = null || sm.SubmodelElements = null || sm.SubmodelElements.Count = 0 then None
            else
                sm.SubmodelElements |> Seq.tryPick (function
                    | :? SubmodelElementCollection as pSmc ->
                        let project = Project("")
                        getProp pSmc Guid_ |> Option.iter (fun g -> project.Id <- Guid.Parse g)
                        getProp pSmc Name_ |> Option.iter (fun n -> project.Name <- n)
                        fromJsonProp<ProjectProperties> pSmc Properties_ |> Option.iter (fun p -> project.Properties <- p)

                        let activeSystems  = getChildSmlSmcs pSmc ActiveSystems_ |> List.choose legacySmcToSystem
                        let passiveSmcs =
                            match getChildSmlSmcs pSmc DeviceReferences_ with
                            | [] -> getChildSmlSmcs pSmc PassiveSystems_
                            | refs -> refs
                        let passiveSystems = passiveSmcs |> List.choose legacySmcToSystem

                        let store = DsStore()
                        store.DirectWrite(store.Projects, project)
                        populateStore store project true activeSystems
                        populateStore store project false passiveSystems
                        log.Info($"Legacy AASX import: '{project.Name}' — {activeSystems.Length} active, {passiveSystems.Length} passive systems")
                        Some (project, store)
                    | _ -> None)
        with ex -> log.Warn($"legacySubmodelToProjectStore failed: {ex.Message}", ex); None
