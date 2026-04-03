namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Store

module internal AasxImportGraph =

    open AasxImportCore

    let inline addProperty add ctor propOpt =
        propOpt |> Option.iter (ctor >> add)

    let smcToApiCall (smc: SubmodelElementCollection) : ApiCall option =
        try
            let name = getProp smc Name_ |> Option.defaultValue ""
            let apiCall = ApiCall(name)
            getProp smc Guid_ |> Option.iter (fun g -> apiCall.Id <- Guid.Parse g)
            getProp smc ApiDefId_ |> Option.bind (fun s -> match Guid.TryParse(s) with true, g -> Some g | _ -> None)
                                  |> Option.iter (fun g -> apiCall.ApiDefId <- Some g)
            fromJsonProp<IOTag option> smc InTag_     |> Option.iter (fun t -> apiCall.InTag <- t)
            fromJsonProp<IOTag option> smc OutTag_    |> Option.iter (fun t -> apiCall.OutTag <- t)
            fromJsonProp<ValueSpec>    smc InputSpec_ |> Option.iter (fun s -> apiCall.InputSpec <- s)
            fromJsonProp<ValueSpec>    smc OutputSpec_|> Option.iter (fun s -> apiCall.OutputSpec <- s)
            getProp smc OriginFlowId_ |> Option.bind (fun s -> match Guid.TryParse(s) with true, g -> Some g | _ -> None)
                                      |> Option.iter (fun g -> apiCall.OriginFlowId <- Some g)
            Some apiCall
        with ex -> log.Warn($"smcToApiCall 실패: {ex.Message}", ex); None

    let smcToCall (smc: SubmodelElementCollection) (workId: Guid) : Call option =
        try
            let devAlias = getProp smc DevicesAlias_ |> Option.defaultValue ""
            let apiName  = getProp smc ApiName_       |> Option.defaultValue ""
            let call = Call(devAlias, apiName, workId)
            getProp smc Guid_ |> Option.iter (fun g -> call.Id <- Guid.Parse g)
            // 모든 도메인 속성 Import
            fromJsonProp<SimulationCallProperties> smc SimulationProperties_   |> addProperty call.Properties.Add SimulationCall
            fromJsonProp<ControlCallProperties> smc ControlProperties_         |> addProperty call.Properties.Add ControlCall
            fromJsonProp<MonitoringCallProperties> smc MonitoringProperties_   |> addProperty call.Properties.Add MonitoringCall
            fromJsonProp<LoggingCallProperties> smc LoggingProperties_         |> addProperty call.Properties.Add LoggingCall
            fromJsonProp<MaintenanceCallProperties> smc MaintenanceProperties_ |> addProperty call.Properties.Add MaintenanceCall
            fromJsonProp<CostAnalysisCallProperties> smc CostAnalysisProperties_ |> addProperty call.Properties.Add CostAnalysisCall
            fromJsonProp<Xywh option>    smc Position_       |> Option.flatten |> Option.iter (fun pos -> call.Position <- Some pos)
            getProp smc Status_          |> Option.iter (fun s -> call.Status4 <- parseStatus4 s)
            // ApiCalls를 SubmodelElementList에서 읽기 (새 형식)
            let apiCalls = getChildSmlSmcs smc ApiCalls_ |> List.choose smcToApiCall
            if not apiCalls.IsEmpty then
                call.ApiCalls <- ResizeArray(apiCalls)
            else
                // 하위 호환성: JSON blob에서 읽기 (구 형식)
                fromJsonProp<ResizeArray<ApiCall>> smc ApiCalls_ |> Option.iter (fun acs -> call.ApiCalls <- acs)
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
                let work = Work("", "", flowId)
                getProp smc Guid_   |> Option.iter (fun g -> work.Id <- Guid.Parse g)
                // 새 형식: FlowPrefix + LocalName 우선, 없으면 Name 폴백 (마이그레이션)
                match getProp smc FlowPrefix_, getProp smc LocalName_ with
                | Some fp, Some ln -> work.FlowPrefix <- fp; work.LocalName <- ln
                | _ -> getProp smc Name_ |> Option.iter (fun n -> work.Name <- n)
                getProp smc ReferenceOf_ |> Option.bind tryParseGuid |> Option.iter (fun g -> work.ReferenceOf <- Some g)
                // 모든 도메인 속성 Import
                fromJsonProp<SimulationWorkProperties> smc SimulationProperties_   |> addProperty work.Properties.Add SimulationWork
                fromJsonProp<ControlWorkProperties> smc ControlProperties_         |> addProperty work.Properties.Add ControlWork
                fromJsonProp<MonitoringWorkProperties> smc MonitoringProperties_   |> addProperty work.Properties.Add MonitoringWork
                fromJsonProp<LoggingWorkProperties> smc LoggingProperties_         |> addProperty work.Properties.Add LoggingWork
                fromJsonProp<MaintenanceWorkProperties> smc MaintenanceProperties_ |> addProperty work.Properties.Add MaintenanceWork
                fromJsonProp<CostAnalysisWorkProperties> smc CostAnalysisProperties_ |> addProperty work.Properties.Add CostAnalysisWork
                fromJsonProp<Xywh option>    smc Position_    |> Option.flatten |> Option.iter (fun pos -> work.Position <- Some pos)
                getProp smc Status_ |> Option.iter (fun s -> work.Status4 <- parseStatus4 s)
                getProp smc TokenRole_ |> Option.iter (fun s ->
                    match System.Int32.TryParse(s) with
                    | true, v -> work.TokenRole <- enum<TokenRole> v
                    | _ -> ())

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
            // 모든 도메인 속성 Import
            fromJsonProp<SimulationFlowProperties> smc SimulationProperties_   |> addProperty flow.Properties.Add SimulationFlow
            fromJsonProp<ControlFlowProperties> smc ControlProperties_         |> addProperty flow.Properties.Add ControlFlow
            fromJsonProp<MonitoringFlowProperties> smc MonitoringProperties_   |> addProperty flow.Properties.Add MonitoringFlow
            fromJsonProp<LoggingFlowProperties> smc LoggingProperties_         |> addProperty flow.Properties.Add LoggingFlow
            fromJsonProp<MaintenanceFlowProperties> smc MaintenanceProperties_ |> addProperty flow.Properties.Add MaintenanceFlow
            fromJsonProp<CostAnalysisFlowProperties> smc CostAnalysisProperties_ |> addProperty flow.Properties.Add CostAnalysisFlow
            Some flow
        with ex -> log.Warn($"smcToFlow 실패: {ex.Message}", ex); None

    let smcToApiDef (smc: SubmodelElementCollection) (systemId: Guid) : ApiDef option =
        try
            let apiDef = ApiDef("", systemId)
            getProp smc Guid_ |> Option.iter (fun g -> apiDef.Id <- Guid.Parse g)
            getProp smc Name_ |> Option.iter (fun n -> apiDef.Name <- n)
            getProp smc IsPush_ |> Option.iter (fun s ->
                match System.Boolean.TryParse(s) with
                | true, b -> apiDef.IsPush <- b
                | _ -> ())
            getProp smc TxGuid_ |> Option.bind (fun s -> match Guid.TryParse(s) with true, g -> Some g | _ -> None)
                                |> Option.iter (fun g -> apiDef.TxGuid <- Some g)
            getProp smc RxGuid_ |> Option.bind (fun s -> match Guid.TryParse(s) with true, g -> Some g | _ -> None)
                                |> Option.iter (fun g -> apiDef.RxGuid <- Some g)
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
                // 모든 도메인 속성 Import
                fromJsonProp<SimulationSystemProperties> smc SimulationProperties_   |> addProperty system.Properties.Add SimulationSystem
                fromJsonProp<ControlSystemProperties> smc ControlProperties_         |> addProperty system.Properties.Add ControlSystem
                fromJsonProp<MonitoringSystemProperties> smc MonitoringProperties_   |> addProperty system.Properties.Add MonitoringSystem
                fromJsonProp<LoggingSystemProperties> smc LoggingProperties_         |> addProperty system.Properties.Add LoggingSystem
                fromJsonProp<MaintenanceSystemProperties> smc MaintenanceProperties_ |> addProperty system.Properties.Add MaintenanceSystem
                fromJsonProp<CostAnalysisSystemProperties> smc CostAnalysisProperties_ |> addProperty system.Properties.Add CostAnalysisSystem
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
                // Call 내부 ApiCalls 등록
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

    /// DeviceReference SMC에서 DeviceRelativePath 확인 → 외부 참조 여부 판별
    let private hasDeviceRelativePath (smc: SubmodelElementCollection) : bool =
        getProp smc DeviceRelativePath_ |> Option.isSome

    /// 상대경로 검증 (.. 금지, 절대경로 금지)
    let private isValidRelativePath (path: string) : bool =
        not (System.String.IsNullOrWhiteSpace path)
        && not (System.IO.Path.IsPathRooted path)
        && not (path.Contains "..")

    /// 외부 Device AASX 파일에서 System 데이터를 로드
    let private loadExternalDevice (mainDir: string) (smc: SubmodelElementCollection)
        : (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) option =
        let relPath = getProp smc DeviceRelativePath_ |> Option.defaultValue ""
        let deviceName = getProp smc DeviceName_ |> Option.defaultValue "<unknown>"
        if not (isValidRelativePath relPath) then
            log.Warn($"Device '{deviceName}': 잘못된 상대경로 '{relPath}' — 스킵")
            None
        else
            let fullPath = System.IO.Path.Combine(mainDir, relPath)
            if not (System.IO.File.Exists fullPath) then
                log.Warn($"Device '{deviceName}': 파일 없음 '{fullPath}' — 스킵")
                None
            else
                match readEnvironment fullPath with
                | None ->
                    log.Warn($"Device '{deviceName}': AASX 읽기 실패 '{fullPath}' — 스킵")
                    None
                | Some env ->
                    if env.Submodels = null then None
                    else
                        env.Submodels
                        |> Seq.tryPick (fun sm ->
                            if sm.IdShort = SubmodelModelIdShort then
                                sm.SubmodelElements
                                |> Seq.tryPick (function
                                    | :? SubmodelElementCollection as pSmc ->
                                        // Device AASX에서 ActiveSystems에 저장된 System 추출
                                        let systemSmcs = getChildSmlSmcs pSmc ActiveSystems_
                                        match parseSystemsStrict $"Device '{deviceName}'" systemSmcs with
                                        | Some (head :: _) -> Some head
                                        | _ -> None
                                    | _ -> None)
                            else None)

    let submodelToProjectStore (sm: ISubmodel) (mainDir: string option) : (Project * DsStore) option =
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
                        fromJsonProp<ResizeArray<TokenSpec>> pSmc TokenSpecs_ |> Option.iter (fun ts ->
                            project.TokenSpecs.Clear()
                            for spec in ts do project.TokenSpecs.Add(spec))

                        // Project 메타데이터 로드
                        getProp pSmc Author_ |> Option.iter (fun v -> project.Author <- v)
                        getProp pSmc Version_ |> Option.iter (fun v -> project.Version <- v)
                        // IriPrefix는 앱 설정에서 관리되므로 import하지 않음
                        getProp pSmc DateTime_ |> Option.iter (fun dtStr ->
                            match DateTimeOffset.TryParse(dtStr) with
                            | true, dt -> project.DateTime <- dt
                            | _ -> ())

                        match parseSystemsStrict $"Project '{project.Name}' ActiveSystems" (getChildSmlSmcs pSmc ActiveSystems_) with
                        | None -> None
                        | Some activeSystems ->
                            let deviceRefSmcs = getChildSmlSmcs pSmc DeviceReferences_

                            // 외부 참조 모드: DeviceRelativePath가 있고 mainDir가 있으면 외부 로드
                            let hasExternalRefs =
                                mainDir.IsSome
                                && not deviceRefSmcs.IsEmpty
                                && deviceRefSmcs |> List.exists hasDeviceRelativePath

                            let passiveSystems =
                                if hasExternalRefs then
                                    let dir = mainDir.Value
                                    deviceRefSmcs
                                    |> List.choose (fun smc ->
                                        if hasDeviceRelativePath smc then
                                            loadExternalDevice dir smc
                                        else
                                            // DeviceRelativePath 없는 인라인 참조
                                            smcToSystem smc |> Option.map id)
                                    |> Some
                                else
                                    // 인라인 모드
                                    parseSystemsStrict $"Project '{project.Name}' DeviceReferences" deviceRefSmcs

                            match passiveSystems with
                            | None -> None
                            | Some ps ->
                                let store = DsStore()
                                store.DirectWrite(store.Projects, project)
                                populateStore store project true activeSystems
                                populateStore store project false ps
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
