module Ds2.Aasx.AasxImporter

open System
open AasCore.Aas3_0
open Ds2.UI.Core   // DsStore, DsQuery 등을 위해 먼저 열기
open Ds2.Core      // Call, Work, Flow, Project 클래스가 EntityKind 케이스보다 우선시되도록 나중에 열기
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open log4net

// ── 내부 헬퍼 ──────────────────────────────────────────────────────────────

let private log = LogManager.GetLogger("Ds2.Aasx.AasxImporter")

let private getProp (smc: SubmodelElementCollection) (idShort: string) : string option =
    if smc.Value = null then None
    else
        smc.Value
        |> Seq.tryPick (function
            | :? Property as p when p.IdShort = idShort ->
                if p.Value = null then None else Some p.Value
            | _ -> None)

let private fromJsonProp<'T> (smc: SubmodelElementCollection) (idShort: string) : 'T option =
    getProp smc idShort
    |> Option.bind (fun json ->
        try Some (Ds2.Serialization.JsonConverter.deserialize<'T> json)
        with ex -> log.Warn($"JSON 역직렬화 실패: {idShort} — {ex.Message}", ex); None)

let private getChildSmlSmcs (smc: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection list =
    if smc.Value = null then []
    else
        smc.Value
        |> Seq.tryPick (function
            | :? SubmodelElementList as l when l.IdShort = idShort ->
                if l.Value = null then Some []
                else
                    Some (l.Value |> Seq.choose (function
                        | :? SubmodelElementCollection as c -> Some c
                        | _ -> None) |> Seq.toList)
            | _ -> None)
        |> Option.defaultValue []

let private parseArrowType (s: string) : ArrowType =
    match Enum.TryParse<ArrowType>(s) with
    | true, v -> v
    | _ -> ArrowType.None

let private parseStatus4 (s: string) : Status4 =
    match Enum.TryParse<Status4>(s) with
    | true, v -> v
    | _ -> Status4.Ready

// ── 변환 계층 ──────────────────────────────────────────────────────────────

let private smcToArrowCall (smc: SubmodelElementCollection) (flowId: Guid) : ArrowBetweenCalls option =
    try
        let id        = getProp smc Guid_   |> Option.map Guid.Parse |> Option.defaultValue (Guid.NewGuid())
        let sourceId  = getProp smc Source_ |> Option.map Guid.Parse |> Option.defaultValue Guid.Empty
        let targetId  = getProp smc Target_ |> Option.map Guid.Parse |> Option.defaultValue Guid.Empty
        let arrowType = getProp smc Type_   |> Option.map parseArrowType |> Option.defaultValue ArrowType.None
        let arrow = ArrowBetweenCalls(flowId, sourceId, targetId, arrowType)
        arrow.Id <- id
        Some arrow
    with ex -> log.Warn($"smcToArrowCall 실패: {ex.Message}", ex); None

let private smcToArrowWork (smc: SubmodelElementCollection) (flowId: Guid) : ArrowBetweenWorks option =
    try
        let id        = getProp smc Guid_   |> Option.map Guid.Parse |> Option.defaultValue (Guid.NewGuid())
        let sourceId  = getProp smc Source_ |> Option.map Guid.Parse |> Option.defaultValue Guid.Empty
        let targetId  = getProp smc Target_ |> Option.map Guid.Parse |> Option.defaultValue Guid.Empty
        let arrowType = getProp smc Type_   |> Option.map parseArrowType |> Option.defaultValue ArrowType.None
        let arrow = ArrowBetweenWorks(flowId, sourceId, targetId, arrowType)
        arrow.Id <- id
        Some arrow
    with ex -> log.Warn($"smcToArrowWork 실패: {ex.Message}", ex); None

let private smcToCall (smc: SubmodelElementCollection) (workId: Guid) : Call option =
    try
        let devAlias = getProp smc DevicesAlias_ |> Option.defaultValue ""
        let apiName  = getProp smc ApiName_       |> Option.defaultValue ""
        let call = Call(devAlias, apiName, workId)
        getProp smc Guid_ |> Option.iter (fun g -> call.Id <- Guid.Parse g)
        fromJsonProp<CallProperties> smc Properties_     |> Option.iter (fun p -> call.Properties <- p)
        fromJsonProp<Xywh option>    smc Position_       |> Option.flatten |> Option.iter (fun pos -> call.Position <- Some pos)
        getProp smc Status_          |> Option.iter (fun s -> call.Status4 <- parseStatus4 s)
        fromJsonProp<ResizeArray<ApiCall>>       smc ApiCalls_       |> Option.iter (fun acs -> call.ApiCalls <- acs)
        fromJsonProp<ResizeArray<CallCondition>> smc CallConditions_ |> Option.iter (fun ccs -> call.CallConditions <- ccs)
        Some call
    with ex -> log.Warn($"smcToCall 실패: {ex.Message}", ex); None

let private smcToWork
    (smc: SubmodelElementCollection)
    : (Work * Call list * ArrowBetweenCalls list) option =
    try
        // FlowGuid로 parentId 설정
        let flowId = getProp smc FlowGuid_ |> Option.map Guid.Parse |> Option.defaultValue Guid.Empty
        let work = Work("", flowId)
        getProp smc Guid_   |> Option.iter (fun g -> work.Id <- Guid.Parse g)
        getProp smc Name_   |> Option.iter (fun n -> work.Name <- n)
        fromJsonProp<WorkProperties> smc Properties_  |> Option.iter (fun p -> work.Properties <- p)
        fromJsonProp<Xywh option>    smc Position_    |> Option.flatten |> Option.iter (fun pos -> work.Position <- Some pos)
        getProp smc Status_ |> Option.iter (fun s -> work.Status4 <- parseStatus4 s)

        let calls      = getChildSmlSmcs smc Calls_  |> List.choose (fun c -> smcToCall c work.Id)
        let arrowCalls = getChildSmlSmcs smc Arrows_ |> List.choose (fun a -> smcToArrowCall a work.ParentId)
        Some (work, calls, arrowCalls)
    with ex -> log.Warn($"smcToWork 실패: {ex.Message}", ex); None

let private smcToFlow
    (smc: SubmodelElementCollection)
    (systemId: Guid)
    : Flow option =
    try
        let flow = Flow("", systemId)
        getProp smc Guid_ |> Option.iter (fun g -> flow.Id <- Guid.Parse g)
        getProp smc Name_ |> Option.iter (fun n -> flow.Name <- n)
        fromJsonProp<FlowProperties> smc Properties_ |> Option.iter (fun p -> flow.Properties <- p)
        Some flow
    with ex -> log.Warn($"smcToFlow 실패: {ex.Message}", ex); None

let private smcToApiDef (smc: SubmodelElementCollection) (systemId: Guid) : ApiDef option =
    try
        let apiDef = ApiDef("", systemId)
        getProp smc Guid_ |> Option.iter (fun g -> apiDef.Id <- Guid.Parse g)
        getProp smc Name_ |> Option.iter (fun n -> apiDef.Name <- n)
        fromJsonProp<ApiDefProperties> smc Properties_ |> Option.iter (fun p -> apiDef.Properties <- p)
        Some apiDef
    with ex -> log.Warn($"smcToApiDef 실패: {ex.Message}", ex); None

let private smcToSystem (smc: SubmodelElementCollection)
    : (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) option =
    try
        // Name 또는 Guid 없는 SMC는 IRI 참조용 빈 항목 — 파싱 생략
        let name = getProp smc Name_
        let guid = getProp smc Guid_
        if name.IsNone && guid.IsNone then None
        else
        let system = DsSystem("")
        guid |> Option.iter (fun g -> system.Id <- Guid.Parse g)
        name |> Option.iter (fun n -> system.Name <- n)
        fromJsonProp<SystemProperties> smc Properties_ |> Option.iter (fun p -> system.Properties <- p)
        let iri = getProp smc IRI_ |> Option.bind (fun s -> if String.IsNullOrEmpty s then None else Some s)
        system.IRI <- iri

        let flows = getChildSmlSmcs smc Flows_ |> List.choose (fun f -> smcToFlow f system.Id)

        // Works は System レベルで平坦化 (各 Work に FlowGuid あり)
        let workResults = getChildSmlSmcs smc Works_ |> List.choose smcToWork
        let works      = workResults |> List.map     (fun (w, _, _)   -> w)
        let calls      = workResults |> List.collect (fun (_, cs, _)  -> cs)
        let arrowCalls = workResults |> List.collect (fun (_, _, acs) -> acs)

        // ArrowBetweenWorks は System レベルに平坦化 — sourceWork の ParentId (flowId) を推論
        let arrowWorks =
            getChildSmlSmcs smc Arrows_
            |> List.choose (fun a ->
                let sourceId = getProp a Source_ |> Option.map Guid.Parse |> Option.defaultValue Guid.Empty
                let flowId   = works |> List.tryFind (fun w -> w.Id = sourceId)
                               |> Option.map (fun w -> w.ParentId)
                               |> Option.defaultValue Guid.Empty
                smcToArrowWork a flowId)

        let apiDefs = getChildSmlSmcs smc ApiDefs_ |> List.choose (fun a -> smcToApiDef a system.Id)
        Some (system, flows, works, calls, arrowCalls, arrowWorks, apiDefs)
    with ex -> log.Warn($"smcToSystem 실패: {ex.Message}", ex); None

let private populateStore
    (store: DsStore)
    (project: Project)
    (isActive: bool)
    (systemResults: (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) list) =
    systemResults
    |> List.iter (fun (system, flows, works, calls, arrowCalls, arrowWorks, apiDefs) ->
        store.DirectWrite(store.Systems, system)
        if isActive then 
            project.ActiveSystemIds.Add(system.Id) |> ignore
        else
            project.PassiveSystemIds.Add(system.Id) |> ignore

        flows |> List.iter (fun f -> store.DirectWrite(store.Flows, f))
        works |> List.iter (fun w -> store.DirectWrite(store.Works, w))
        calls |> List.iter (fun call ->
            store.DirectWrite(store.Calls, call)
            call.ApiCalls |> Seq.iter (fun apiCall -> store.DirectWrite(store.ApiCalls, apiCall))
            call.CallConditions
            |> Seq.iter (fun cond ->
                cond.Conditions
                |> Seq.filter (fun apiCall -> not (store.ApiCalls.ContainsKey(apiCall.Id)))
                |> Seq.iter (fun apiCall -> store.DirectWrite(store.ApiCalls, apiCall))))
        arrowCalls |> List.iter (fun a -> store.DirectWrite(store.ArrowCalls, a))
        arrowWorks |> List.iter (fun a -> store.DirectWrite(store.ArrowWorks, a))
        apiDefs    |> List.iter (fun d -> store.DirectWrite(store.ApiDefs, d)))

let private submodelToProjectStore (sm: ISubmodel) : (Project * DsStore) option =
    try
        if sm = null || sm.SubmodelElements = null || sm.SubmodelElements.Count = 0 then 
            None
        else
            sm.SubmodelElements
            |> Seq.tryPick (function
                | :? SubmodelElementCollection as pSmc ->
                    let project = Project("")
                    getProp pSmc Guid_ |> Option.iter (fun g -> project.Id <- Guid.Parse g)
                    getProp pSmc Name_ |> Option.iter (fun n -> project.Name <- n)
                    fromJsonProp<ProjectProperties> pSmc Properties_ |> Option.iter (fun p -> project.Properties <- p)

                    let activeSystems  = getChildSmlSmcs pSmc ActiveSystems_    |> List.choose smcToSystem
                    // DeviceReferences_ 우선, 없으면 구버전 PassiveSystems_ 폴백
                    let passiveSystems =
                        let fromNew = getChildSmlSmcs pSmc DeviceReferences_ |> List.choose smcToSystem
                        if fromNew.IsEmpty then
                            getChildSmlSmcs pSmc PassiveSystems_ |> List.choose smcToSystem
                        else fromNew

                    let store = DsStore()
                    store.DirectWrite(store.Projects, project)
                    populateStore store project true  activeSystems
                    populateStore store project false passiveSystems
                    Some (project, store)
                | _ -> None)
    with ex -> log.Warn($"submodelToProjectStore 실패: {ex.Message}", ex); None

// ── 진입점 ─────────────────────────────────────────────────────────────────

/// AASX 파일에서 DsStore를 읽어 반환합니다 (Project는 store.Projects에 포함됩니다).
let internal importFromAasxFile (path: string) : DsStore option =
    readEnvironment path
    |> Option.bind (fun env ->
        if env.Submodels = null then
            log.Warn($"AASX 파싱 실패: Submodels null ({path})")
            None
        else
            let result =
                env.Submodels
                |> Seq.tryPick (fun sm ->
                    if sm.IdShort = SubmodelIdShort then
                        submodelToProjectStore sm |> Option.map snd
                    else None)
            if result.IsNone then
                log.Warn($"AASX 파싱 실패: '{SubmodelIdShort}' Submodel을 찾을 수 없습니다 ({path})")
            result)

let importIntoStore (store: DsStore) (path: string) : bool =
    match importFromAasxFile path with
    | Some imported ->
        store.ReplaceStore(imported)
        true
    | None -> false
