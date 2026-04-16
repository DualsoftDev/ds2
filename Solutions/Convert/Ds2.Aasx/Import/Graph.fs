namespace Ds2.Aasx

open System
open System.Reflection
open AasCore.Aas3_1
open log4net
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store

module internal AasxImportGraph =

    open AasxImportCore

    let private setPropsFromAasxFields<'T> (smc: SubmodelElementCollection) (entity: 'T) =
        let entityType = entity.GetType()
        let rec getAllProperties (t: Type) =
            seq {
                yield! t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly)
                if t.BaseType <> null && t.BaseType <> typeof<obj> then
                    yield! getAllProperties t.BaseType
            }

        let props = getAllProperties entityType |> Seq.toArray
        props
        |> Seq.distinctBy (fun p -> p.Name)
        |> Seq.toArray
        |> Array.iter (fun prop ->
            match prop.GetCustomAttribute<AasxFieldAttribute>(true) |> box with
                | null -> ()
                | :? AasxFieldAttribute as attr when not attr.Skip ->
                    // 특수 타입 처리
                    if prop.PropertyType = typeof<Xywh option> then
                        fromJsonProp<Xywh option> smc attr.FieldName
                        |> Option.flatten
                        |> Option.iter (fun pos -> prop.SetValue(entity, Some pos))
                    elif prop.PropertyType = typeof<TimeSpan option> then
                        getProp smc attr.FieldName
                        |> Option.bind tryParseIsoDuration
                        |> Option.iter (fun d -> prop.SetValue(entity, Some d))
                    elif prop.PropertyType = typeof<ResizeArray<CallCondition>> then
                        fromJsonProp<ResizeArray<CallCondition>> smc attr.FieldName
                        |> Option.iter (fun ccs -> prop.SetValue(entity, ccs))
                    elif prop.PropertyType = typeof<ResizeArray<TokenSpec>> then
                        fromJsonProp<ResizeArray<TokenSpec>> smc attr.FieldName
                        |> Option.iter (fun ts -> prop.SetValue(entity, ts))
                    elif prop.PropertyType = typeof<IOTag option> then
                        fromJsonProp<IOTag option> smc attr.FieldName
                        |> Option.iter (fun t -> prop.SetValue(entity, t))
                    elif prop.PropertyType = typeof<ValueSpec> then
                        fromJsonProp<ValueSpec> smc attr.FieldName
                        |> Option.iter (fun s -> prop.SetValue(entity, s))
                    elif prop.PropertyType = typeof<DateTimeOffset> then
                        getProp smc attr.FieldName
                        |> Option.iter (fun dtStr ->
                            match DateTimeOffset.TryParse(dtStr) with
                            | true, dt -> prop.SetValue(entity, dt)
                            | _ -> ())
                    elif prop.PropertyType = typeof<TokenRole> then
                        getProp smc attr.FieldName
                        |> Option.iter (fun s ->
                            match System.Int32.TryParse(s) with
                            | true, v -> prop.SetValue(entity, enum<TokenRole> v)
                            | _ -> ())
                    elif prop.PropertyType = typeof<Status4> then
                        getProp smc attr.FieldName
                        |> Option.iter (fun s -> prop.SetValue(entity, parseStatus4 s))
                    elif prop.PropertyType = typeof<ArrowType> then
                        getProp smc attr.FieldName
                        |> Option.iter (fun s -> prop.SetValue(entity, parseArrowType s))
                    elif prop.PropertyType = typeof<ApiDefActionType> then
                        fromJsonProp<ApiDefActionType> smc attr.FieldName
                        |> Option.iter (fun at -> prop.SetValue(entity, at))
                    elif prop.PropertyType = typeof<bool> then
                        getProp smc attr.FieldName
                        |> Option.iter (fun s ->
                            match System.Boolean.TryParse(s) with
                            | true, b -> prop.SetValue(entity, b)
                            | _ -> ())
                    else
                        // 기본 문자열/GUID 처리
                        let value = getProp smc attr.FieldName
                        match value with
                        | None -> ()
                        | Some str ->
                            if prop.PropertyType = typeof<Guid> then
                                match Guid.TryParse(str) with
                                | true, g -> prop.SetValue(entity, g)
                                | _ -> ()
                            elif prop.PropertyType = typeof<string> then
                                prop.SetValue(entity, str)
                            elif prop.PropertyType = typeof<string option> then
                                let optValue = if String.IsNullOrEmpty str then None else Some str
                                prop.SetValue(entity, optValue)
                            elif prop.PropertyType = typeof<Guid option> then
                                match Guid.TryParse(str) with
                                | true, g -> prop.SetValue(entity, Some g)
                                | _ -> ()
                | _ -> ())

    let smcToApiCall (smc: SubmodelElementCollection) : ApiCall option =
        try
            let apiCall = ApiCall("")
            setPropsFromAasxFields smc apiCall
            Some apiCall
        with ex -> log.Warn($"smcToApiCall 실패: {ex.Message}", ex); None

    let smcToCall (smc: SubmodelElementCollection) (workId: Guid) : Call option =
        try
            let call = Call("", "", workId)
            setPropsFromAasxFields smc call
            let apiCalls = getChildSmlSmcs smc ApiCalls_ |> List.choose smcToApiCall
            if not apiCalls.IsEmpty then
                call.ApiCalls <- ResizeArray(apiCalls)
            else
                fromJsonProp<ResizeArray<ApiCall>> smc ApiCalls_ |> Option.iter (fun acs -> call.ApiCalls <- acs)
            Some call
        with ex -> log.Warn($"smcToCall 실패: {ex.Message}", ex); None

    let smcToWork
        (smc: SubmodelElementCollection)
        : (Work * Call list * ArrowBetweenCalls list) option =
        try
            let tryParseGuid (s: string) = match Guid.TryParse(s) with true, v -> Some v | _ -> None
            match getProp smc FlowGuid_ |> Option.bind tryParseGuid with
            | None ->
                let workGuid = getProp smc "Guid" |> Option.defaultValue "<missing>"
                let workName = getProp smc "Name" |> Option.defaultValue "<missing>"
                log.Error($"AASX import failed: Work FlowGuid missing or invalid (WorkGuid={workGuid}, WorkName={workName}).")
                None
            | Some flowId ->
                let work = Work("", "", flowId)
                setPropsFromAasxFields smc work
                let calls      = getChildSmlSmcs smc Calls_  |> List.choose (fun c -> smcToCall c work.Id)
                let arrowCalls = getChildSmlSmcs smc Arrows_ |> List.choose (fun a -> smcToArrowCall a work.Id)
                Some (work, calls, arrowCalls)
        with ex -> log.Warn($"smcToWork 실패: {ex.Message}", ex); None

    let smcToFlow (smc: SubmodelElementCollection) (systemId: Guid) : Flow option =
        try
            let flow = Flow("", systemId)
            setPropsFromAasxFields smc flow
            Some flow
        with ex -> log.Warn($"smcToFlow 실패: {ex.Message}", ex); None

    let smcToApiDef (smc: SubmodelElementCollection) (systemId: Guid) : ApiDef option =
        try
            let apiDef = ApiDef("", systemId)
            setPropsFromAasxFields smc apiDef
            Some apiDef
        with ex -> log.Warn($"smcToApiDef 실패: {ex.Message}", ex); None

    let smcToSystem (smc: SubmodelElementCollection)
        : (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) option =
        try
            let system = DsSystem("")
            setPropsFromAasxFields smc system
            if String.IsNullOrEmpty(system.Name) && system.Id = Guid.Empty then
                log.Error($"AASX import failed: System entry missing Name and Guid ({describeSmc smc}).")
                None
            else
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

    let private hasDeviceRelativePath (smc: SubmodelElementCollection) : bool =
        getProp smc DeviceRelativePath_ |> Option.isSome

    let private isValidRelativePath (path: string) : bool =
        not (System.String.IsNullOrWhiteSpace path)
        && not (System.IO.Path.IsPathRooted path)
        && not (path.Contains "..")

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
                        setPropsFromAasxFields pSmc project

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
                                        if hasDeviceRelativePath smc then loadExternalDevice dir smc
                                        else smcToSystem smc |> Option.map id)
                                    |> Some
                                else
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

    let tryGetSmcChild (smc: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection option =
        if smc.Value = null then None
        else
            smc.Value |> Seq.tryPick (function
                | :? SubmodelElementCollection as c when c.IdShort = idShort -> Some c
                | _ -> None)

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
