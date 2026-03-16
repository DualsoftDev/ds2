namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.UI.Core

module internal AasxImportGraph =

    open AasxImportCore

    let smcToCall (smc: SubmodelElementCollection) (workId: Guid) : Call option =
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

    let smcToWork
        (smc: SubmodelElementCollection)
        : (Work * Call list * ArrowBetweenCalls list) option =
        try
            // FlowGuid로 parentId 설정
            let tryParseGuid (s: string) = match Guid.TryParse(s) with true, v -> Some v | _ -> None
            match getProp smc FlowGuid_ |> Option.bind tryParseGuid with
            | None ->
                let workGuid = getProp smc Guid_ |> Option.defaultValue "<missing>"
                let workName = getProp smc Name_ |> Option.defaultValue "<missing>"
                log.Error($"AASX import failed: Work FlowGuid missing or invalid (WorkGuid={workGuid}, WorkName={workName}).")
                None
            | Some flowId ->
                let work = Work("", flowId)
                getProp smc Guid_   |> Option.iter (fun g -> work.Id <- Guid.Parse g)
                getProp smc Name_   |> Option.iter (fun n -> work.Name <- n)
                fromJsonProp<WorkProperties> smc Properties_  |> Option.iter (fun p -> work.Properties <- p)
                fromJsonProp<Xywh option>    smc Position_    |> Option.flatten |> Option.iter (fun pos -> work.Position <- Some pos)
                getProp smc Status_ |> Option.iter (fun s -> work.Status4 <- parseStatus4 s)

                let calls      = getChildSmlSmcs smc Calls_  |> List.choose (fun c -> smcToCall c work.Id)
                let arrowCalls = getChildSmlSmcs smc Arrows_ |> List.choose (fun a -> smcToArrowCall a work.Id)
                Some (work, calls, arrowCalls)
        with ex -> log.Warn($"smcToWork 실패: {ex.Message}", ex); None

    let smcToFlow
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

    let smcToApiDef (smc: SubmodelElementCollection) (systemId: Guid) : ApiDef option =
        try
            let apiDef = ApiDef("", systemId)
            getProp smc Guid_ |> Option.iter (fun g -> apiDef.Id <- Guid.Parse g)
            getProp smc Name_ |> Option.iter (fun n -> apiDef.Name <- n)
            fromJsonProp<ApiDefProperties> smc Properties_ |> Option.iter (fun p -> apiDef.Properties <- p)
            Some apiDef
        with ex -> log.Warn($"smcToApiDef 실패: {ex.Message}", ex); None

    let smcToSystem (smc: SubmodelElementCollection)
        : (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) option =
        try
            let name = getProp smc Name_
            let guid = getProp smc Guid_
            if name.IsNone && guid.IsNone then
                log.Error($"AASX import failed: System entry missing Name and Guid ({describeSmc smc}).")
                None
            else
                let system = DsSystem("")
                guid |> Option.iter (fun g -> system.Id <- Guid.Parse g)
                name |> Option.iter (fun n -> system.Name <- n)
                fromJsonProp<SystemProperties> smc Properties_ |> Option.iter (fun p -> system.Properties <- p)
                let iri = getProp smc IRI_ |> Option.bind (fun s -> if String.IsNullOrEmpty s then None else Some s)
                system.IRI <- iri

                let systemLabel = $"System '{system.Name}' ({system.Id})"

                match parseStrictList systemLabel "Flow" (getChildSmlSmcs smc Flows_) (fun f -> smcToFlow f system.Id) with
                | None -> None
                | Some flows ->
                    match parseStrictList systemLabel "Work" (getChildSmlSmcs smc Works_) smcToWork with
                    | None -> None
                    | Some workResults ->
                        let works      = workResults |> List.map     (fun (w, _, _)   -> w)
                        let calls      = workResults |> List.collect (fun (_, cs, _)  -> cs)
                        let arrowCalls = workResults |> List.collect (fun (_, _, acs) -> acs)

                        match parseStrictList systemLabel "ArrowBetweenWorks" (getChildSmlSmcs smc Arrows_) (fun a -> smcToArrowWork a system.Id) with
                        | None -> None
                        | Some arrowWorks ->
                            match parseStrictList systemLabel "ApiDef" (getChildSmlSmcs smc ApiDefs_) (fun a -> smcToApiDef a system.Id) with
                            | None -> None
                            | Some apiDefs ->
                                let flowIds = flows |> List.map (fun f -> f.Id) |> Set.ofList
                                let workIds = works |> List.map (fun w -> w.Id) |> Set.ofList
                                let callIds = calls |> List.map (fun c -> c.Id) |> Set.ofList

                                match works |> List.tryFind (fun w -> not (Set.contains w.ParentId flowIds)) with
                                | Some invalidWork ->
                                    log.Error($"AASX import failed: Work '{invalidWork.Name}' ({invalidWork.Id}) references missing Flow ({invalidWork.ParentId}) in {systemLabel}.")
                                    None
                                | None ->
                                    match arrowCalls |> List.tryFind (fun a -> not (Set.contains a.ParentId workIds && Set.contains a.SourceId callIds && Set.contains a.TargetId callIds)) with
                                    | Some invalidArrowCall ->
                                        log.Error($"AASX import failed: ArrowBetweenCalls ({invalidArrowCall.Id}) has invalid references in {systemLabel}. ParentWork={invalidArrowCall.ParentId}, SourceCall={invalidArrowCall.SourceId}, TargetCall={invalidArrowCall.TargetId}.")
                                        None
                                    | None ->
                                        match arrowWorks |> List.tryFind (fun a -> a.ParentId <> system.Id || not (Set.contains a.SourceId workIds && Set.contains a.TargetId workIds)) with
                                        | Some invalidArrowWork ->
                                            log.Error($"AASX import failed: ArrowBetweenWorks ({invalidArrowWork.Id}) has invalid references in {systemLabel}. ParentSystem={invalidArrowWork.ParentId}, SourceWork={invalidArrowWork.SourceId}, TargetWork={invalidArrowWork.TargetId}.")
                                            None
                                        | None ->
                                            Some (system, flows, works, calls, arrowCalls, arrowWorks, apiDefs)
        with ex -> log.Warn($"smcToSystem 실패: {ex.Message}", ex); None

    let populateStore
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
            let rec registerConditionApiCalls (cond: CallCondition) =
                cond.Conditions
                |> Seq.filter (fun apiCall -> not (store.ApiCalls.ContainsKey(apiCall.Id)))
                |> Seq.iter (fun apiCall -> store.DirectWrite(store.ApiCalls, apiCall))
                cond.Children |> Seq.iter registerConditionApiCalls

            calls |> List.iter (fun call ->
                store.DirectWrite(store.Calls, call)
                call.ApiCalls |> Seq.iter (fun apiCall -> store.DirectWrite(store.ApiCalls, apiCall))
                call.CallConditions |> Seq.iter registerConditionApiCalls)
            arrowCalls |> List.iter (fun a -> store.DirectWrite(store.ArrowCalls, a))
            arrowWorks |> List.iter (fun a -> store.DirectWrite(store.ArrowWorks, a))
            apiDefs    |> List.iter (fun d -> store.DirectWrite(store.ApiDefs, d)))

    let parseSystemsStrict
        (ownerLabel: string)
        (systemSmcs: SubmodelElementCollection list)
        : (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) list option =
        parseStrictList ownerLabel "System" systemSmcs smcToSystem

    let submodelToProjectStore (sm: ISubmodel) : (Project * DsStore) option =
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

                        match parseSystemsStrict $"Project '{project.Name}' ActiveSystems" (getChildSmlSmcs pSmc ActiveSystems_) with
                        | None -> None
                        | Some activeSystems ->
                            let passiveSmcsFromDeviceRefs = getChildSmlSmcs pSmc DeviceReferences_
                            let passiveOwnerLabel =
                                if passiveSmcsFromDeviceRefs.IsEmpty then
                                    $"Project '{project.Name}' PassiveSystems"
                                else
                                    $"Project '{project.Name}' DeviceReferences"
                            let passiveSmcs =
                                if passiveSmcsFromDeviceRefs.IsEmpty then
                                    getChildSmlSmcs pSmc PassiveSystems_
                                else
                                    passiveSmcsFromDeviceRefs

                            match parseSystemsStrict passiveOwnerLabel passiveSmcs with
                            | None -> None
                            | Some passiveSystems ->
                                let store = DsStore()
                                store.DirectWrite(store.Projects, project)
                                populateStore store project true activeSystems
                                populateStore store project false passiveSystems
                                Some (project, store)
                    | _ -> None)
        with ex -> log.Warn($"submodelToProjectStore failed: {ex.Message}", ex); None

    // ── Nameplate 역직렬화 (IDTA 02006-3-0) ─────────────────────────────────────

    let tryGetSmcChild (smc: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection option =
        if smc.Value = null then None
        else
            smc.Value |> Seq.tryPick (function
                | :? SubmodelElementCollection as c when c.IdShort = idShort -> Some c
                | _ -> None)

    /// Property 또는 MultiLanguageProperty에서 문자열 값 추출
    let getAnyStringValue (smc: SubmodelElementCollection) (idShort: string) : string =
        if smc.Value = null then ""
        else
            smc.Value |> Seq.tryPick (fun elem ->
                if elem.IdShort <> idShort then None
                else
                    match elem with
                    | :? Property as p -> if p.Value = null then Some "" else Some p.Value
                    | :? MultiLanguageProperty as mlp ->
                        if mlp.Value = null || mlp.Value.Count = 0 then Some ""
                        else Some (mlp.Value.[0].Text)
                    | _ -> None)
            |> Option.defaultValue ""
