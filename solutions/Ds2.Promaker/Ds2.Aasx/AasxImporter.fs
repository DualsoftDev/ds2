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

// ── 변환 계층 ──────────────────────────────────────────────────────────────

let private smcToArrowCall (smc: SubmodelElementCollection) (workId: Guid) : ArrowBetweenCalls option =
    try
        let id        = getProp smc Guid_     |> Option.map Guid.Parse   |> Option.defaultValue (Guid.NewGuid())
        let sourceId  = getProp smc SourceId_ |> Option.map Guid.Parse   |> Option.defaultValue Guid.Empty
        let targetId  = getProp smc TargetId_ |> Option.map Guid.Parse   |> Option.defaultValue Guid.Empty
        let arrowType = getProp smc ArrowType_ |> Option.map (fun s -> enum<ArrowType>(Int32.Parse s)) |> Option.defaultValue ArrowType.None
        let arrow = ArrowBetweenCalls(workId, sourceId, targetId, arrowType)
        arrow.Id <- id
        Some arrow
    with ex -> log.Warn($"smcToArrowCall 실패: {ex.Message}", ex); None

let private smcToArrowWork (smc: SubmodelElementCollection) (flowId: Guid) : ArrowBetweenWorks option =
    try
        let id        = getProp smc Guid_     |> Option.map Guid.Parse   |> Option.defaultValue (Guid.NewGuid())
        let sourceId  = getProp smc SourceId_ |> Option.map Guid.Parse   |> Option.defaultValue Guid.Empty
        let targetId  = getProp smc TargetId_ |> Option.map Guid.Parse   |> Option.defaultValue Guid.Empty
        let arrowType = getProp smc ArrowType_ |> Option.map (fun s -> enum<ArrowType>(Int32.Parse s)) |> Option.defaultValue ArrowType.None
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
        getProp smc Status_          |> Option.iter (fun s -> call.Status4 <- enum<Status4>(Int32.Parse s))
        fromJsonProp<ResizeArray<ApiCall>>       smc ApiCalls_       |> Option.iter (fun acs -> call.ApiCalls <- acs)
        fromJsonProp<ResizeArray<CallCondition>> smc CallConditions_ |> Option.iter (fun ccs -> call.CallConditions <- ccs)
        Some call
    with ex -> log.Warn($"smcToCall 실패: {ex.Message}", ex); None

let private smcToWork
    (smc: SubmodelElementCollection)
    (flowId: Guid)
    : (Work * Call list * ArrowBetweenCalls list) option =
    try
        let work = Work("", flowId)
        getProp smc Guid_   |> Option.iter (fun g -> work.Id <- Guid.Parse g)
        getProp smc Name_   |> Option.iter (fun n -> work.Name <- n)
        fromJsonProp<WorkProperties> smc Properties_  |> Option.iter (fun p -> work.Properties <- p)
        fromJsonProp<Xywh option>    smc Position_    |> Option.flatten |> Option.iter (fun pos -> work.Position <- Some pos)
        getProp smc Status_ |> Option.iter (fun s -> work.Status4 <- enum<Status4>(Int32.Parse s))

        let calls      = getChildSmlSmcs smc Calls_       |> List.choose (fun c -> smcToCall c work.Id)
        let arrowCalls = getChildSmlSmcs smc ArrowsBtCalls_ |> List.choose (fun a -> smcToArrowCall a work.Id)
        Some (work, calls, arrowCalls)
    with ex -> log.Warn($"smcToWork 실패: {ex.Message}", ex); None

let private smcToFlow
    (smc: SubmodelElementCollection)
    (systemId: Guid)
    : (Flow * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list) option =
    try
        let flow = Flow("", systemId)
        getProp smc Guid_ |> Option.iter (fun g -> flow.Id <- Guid.Parse g)
        getProp smc Name_ |> Option.iter (fun n -> flow.Name <- n)
        fromJsonProp<FlowProperties> smc Properties_ |> Option.iter (fun p -> flow.Properties <- p)

        let workResults = getChildSmlSmcs smc Works_ |> List.choose (fun w -> smcToWork w flow.Id)
        let works      = workResults |> List.map     (fun (w, _, _)   -> w)
        let calls      = workResults |> List.collect (fun (_, cs, _)  -> cs)
        let arrowCalls = workResults |> List.collect (fun (_, _, acs) -> acs)
        let arrowWorks = getChildSmlSmcs smc ArrowsBtWorks_ |> List.choose (fun a -> smcToArrowWork a flow.Id)
        Some (flow, works, calls, arrowCalls, arrowWorks)
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
    : (DsSystem * bool * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) option =
    try
        let system = DsSystem("")
        getProp smc Guid_ |> Option.iter (fun g -> system.Id <- Guid.Parse g)
        getProp smc Name_ |> Option.iter (fun n -> system.Name <- n)
        let isActive = getProp smc IsActive_ |> Option.map (fun s -> s.ToLowerInvariant() = "true") |> Option.defaultValue false
        fromJsonProp<SystemProperties> smc Properties_ |> Option.iter (fun p -> system.Properties <- p)

        let flowResults = getChildSmlSmcs smc Flows_ |> List.choose (fun f -> smcToFlow f system.Id)
        let flows      = flowResults |> List.map     (fun (f, _, _, _, _)    -> f)
        let works      = flowResults |> List.collect (fun (_, ws, _, _, _)   -> ws)
        let calls      = flowResults |> List.collect (fun (_, _, cs, _, _)   -> cs)
        let arrowCalls = flowResults |> List.collect (fun (_, _, _, acs, _)  -> acs)
        let arrowWorks = flowResults |> List.collect (fun (_, _, _, _, aws)  -> aws)
        let apiDefs    = getChildSmlSmcs smc ApiDefs_ |> List.choose (fun a -> smcToApiDef a system.Id)
        Some (system, isActive, flows, works, calls, arrowCalls, arrowWorks, apiDefs)
    with ex -> log.Warn($"smcToSystem 실패: {ex.Message}", ex); None

let private populateStore
    (store: DsStore)
    (project: Project)
    (systemResults: (DsSystem * bool * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) list) =
    for (system, isActive, flows, works, calls, arrowCalls, arrowWorks, apiDefs) in systemResults do
        StoreWrite.system store system
        if isActive then project.ActiveSystemIds.Add(system.Id)
        else             project.PassiveSystemIds.Add(system.Id)
        for flow    in flows      do StoreWrite.flow store flow
        for work    in works      do StoreWrite.work store work
        for call    in calls      do
            StoreWrite.call store call
            for apiCall in call.ApiCalls do
                StoreWrite.apiCall store apiCall
            for condition in call.CallConditions do
                for apiCall in condition.Conditions do
                    if not (store.ApiCalls.ContainsKey(apiCall.Id)) then
                        StoreWrite.apiCall store apiCall
        for arrow   in arrowCalls do StoreWrite.arrowCall store arrow
        for arrow   in arrowWorks do StoreWrite.arrowWork store arrow
        for apiDef  in apiDefs    do StoreWrite.apiDef store apiDef

let private submodelToProjectStore (sm: ISubmodel) : (Project * DsStore) option =
    try
        if sm = null || sm.SubmodelElements = null || sm.SubmodelElements.Count = 0 then None
        else
            sm.SubmodelElements
            |> Seq.tryPick (function
                | :? SubmodelElementCollection as pSmc ->
                    let project = Project("")
                    getProp pSmc Guid_ |> Option.iter (fun g -> project.Id <- Guid.Parse g)
                    getProp pSmc Name_ |> Option.iter (fun n -> project.Name <- n)
                    fromJsonProp<ProjectProperties> pSmc Properties_ |> Option.iter (fun p -> project.Properties <- p)

                    let activeSystems  = getChildSmlSmcs pSmc ActiveSystems_  |> List.choose smcToSystem
                    let passiveSystems = getChildSmlSmcs pSmc PassiveSystems_ |> List.choose smcToSystem

                    let store = DsStore()
                    StoreWrite.project store project
                    populateStore store project activeSystems
                    populateStore store project passiveSystems
                    Some (project, store)
                | _ -> None)
    with ex -> log.Warn($"submodelToProjectStore 실패: {ex.Message}", ex); None

// ── 진입점 ─────────────────────────────────────────────────────────────────

/// AASX 파일에서 DsStore를 읽어 반환합니다 (Project는 store.Projects에 포함됩니다).
let importFromAasxFile (path: string) : DsStore option =
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
