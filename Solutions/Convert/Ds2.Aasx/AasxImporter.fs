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
    | _ -> ArrowType.Unspecified

let private parseStatus4 (s: string) : Status4 =
    match Enum.TryParse<Status4>(s) with
    | true, v -> v
    | _ -> Status4.Ready

let private describeSmc (smc: SubmodelElementCollection) : string =
    let guidText = getProp smc Guid_ |> Option.defaultValue "<missing>"
    let nameText = getProp smc Name_ |> Option.defaultValue "<missing>"
    $"Guid={guidText}, Name={nameText}"

let private parseStrictList
    (ownerLabel: string)
    (itemLabel: string)
    (items: SubmodelElementCollection list)
    (parser: SubmodelElementCollection -> 'T option)
    : 'T list option =
    let rec loop acc rest =
        match rest with
        | [] -> Some(List.rev acc)
        | smc :: tail ->
            match parser smc with
            | Some value -> loop (value :: acc) tail
            | None ->
                log.Error($"AASX import failed: invalid {itemLabel} under {ownerLabel} ({describeSmc smc}).")
                None
    loop [] items

// ── 변환 계층 ──────────────────────────────────────────────────────────────

let private smcToArrowCall (smc: SubmodelElementCollection) (workId: Guid) : ArrowBetweenCalls option =
    try
        match getProp smc Source_ |> Option.map Guid.Parse, getProp smc Target_ |> Option.map Guid.Parse with
        | Some sourceId, Some targetId ->
            let id        = getProp smc Guid_ |> Option.map Guid.Parse |> Option.defaultValue (Guid.NewGuid())
            let arrowType = getProp smc Type_  |> Option.map parseArrowType |> Option.defaultValue ArrowType.Unspecified
            let arrow = ArrowBetweenCalls(workId, sourceId, targetId, arrowType)
            arrow.Id <- id
            Some arrow
        | _ -> log.Warn($"smcToArrowCall: Source 또는 Target 누락"); None
    with ex -> log.Warn($"smcToArrowCall 실패: {ex.Message}", ex); None

let private smcToArrowWork (smc: SubmodelElementCollection) (systemId: Guid) : ArrowBetweenWorks option =
    try
        match getProp smc Source_ |> Option.map Guid.Parse, getProp smc Target_ |> Option.map Guid.Parse with
        | Some sourceId, Some targetId ->
            let id        = getProp smc Guid_ |> Option.map Guid.Parse |> Option.defaultValue (Guid.NewGuid())
            let arrowType = getProp smc Type_  |> Option.map parseArrowType |> Option.defaultValue ArrowType.Unspecified
            let arrow = ArrowBetweenWorks(systemId, sourceId, targetId, arrowType)
            arrow.Id <- id
            Some arrow
        | _ -> log.Warn($"smcToArrowWork: Source 또는 Target 누락"); None
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

let private parseSystemsStrict
    (ownerLabel: string)
    (systemSmcs: SubmodelElementCollection list)
    : (DsSystem * Flow list * Work list * Call list * ArrowBetweenCalls list * ArrowBetweenWorks list * ApiDef list) list option =
    parseStrictList ownerLabel "System" systemSmcs smcToSystem

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

let private tryGetSmcChild (smc: SubmodelElementCollection) (idShort: string) : SubmodelElementCollection option =
    if smc.Value = null then None
    else
        smc.Value |> Seq.tryPick (function
            | :? SubmodelElementCollection as c when c.IdShort = idShort -> Some c
            | _ -> None)

/// Property 또는 MultiLanguageProperty에서 문자열 값 추출
let private getAnyStringValue (smc: SubmodelElementCollection) (idShort: string) : string =
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

let private smcToPhoneInfo (smc: SubmodelElementCollection) : PhoneInfo =
    let p = PhoneInfo()
    p.TelephoneNumber <- getAnyStringValue smc "TelephoneNumber"
    p.TypeOfTelephone <- getAnyStringValue smc "TypeOfTelephone"
    p

let private smcToFaxInfo (smc: SubmodelElementCollection) : FaxInfo =
    let f = FaxInfo()
    f.FaxNumber       <- getAnyStringValue smc "FaxNumber"
    f.TypeOfFaxNumber <- getAnyStringValue smc "TypeOfFaxNumber"
    f

let private smcToEmailInfo (smc: SubmodelElementCollection) : EmailInfo =
    let e = EmailInfo()
    e.EmailAddress       <- getAnyStringValue smc "EmailAddress"
    e.PublicKey           <- getAnyStringValue smc "PublicKey"
    e.TypeOfEmailAddress <- getAnyStringValue smc "TypeOfEmailAddress"
    e

let private smcToAddressInfo (smc: SubmodelElementCollection) : AddressInfo =
    let a = AddressInfo()
    a.Street       <- getAnyStringValue smc "Street"
    a.Zipcode      <- getAnyStringValue smc "Zipcode"
    a.CityTown     <- getAnyStringValue smc "CityTown"
    a.NationalCode <- getAnyStringValue smc "NationalCode"
    tryGetSmcChild smc "Phone" |> Option.iter (fun c -> a.Phone <- smcToPhoneInfo c)
    tryGetSmcChild smc "Fax"   |> Option.iter (fun c -> a.Fax   <- smcToFaxInfo c)
    tryGetSmcChild smc "Email" |> Option.iter (fun c -> a.Email <- smcToEmailInfo c)
    a

let private smcToMarkingInfo (smc: SubmodelElementCollection) : MarkingInfo =
    let m = MarkingInfo()
    m.MarkingName                         <- getAnyStringValue smc "MarkingName"
    m.DesignationOfCertificateOrApproval  <- getAnyStringValue smc "DesignationOfCertificateOrApproval"
    m.IssueDate                           <- getAnyStringValue smc "IssueDate"
    m.ExpiryDate                          <- getAnyStringValue smc "ExpiryDate"
    m.MarkingFile                         <- getAnyStringValue smc "MarkingFile"
    m.MarkingAdditionalText               <- getAnyStringValue smc "MarkingAdditionalText"
    m

let private submodelToNameplate (sm: ISubmodel) : Nameplate =
    let np = Nameplate()
    if sm.SubmodelElements = null then np
    else
        for elem in sm.SubmodelElements do
            match elem with
            | :? Property as p ->
                match p.IdShort with
                | "URIOfTheProduct"                     -> np.URIOfTheProduct <- (if p.Value = null then "" else p.Value)
                | "OrderCodeOfManufacturer"             -> np.OrderCodeOfManufacturer <- (if p.Value = null then "" else p.Value)
                | "ManufacturerProductType"             -> np.ManufacturerProductType <- (if p.Value = null then "" else p.Value)
                | "ProductArticleNumberOfManufacturer"  -> np.ProductArticleNumberOfManufacturer <- (if p.Value = null then "" else p.Value)
                | "SerialNumber"                        -> np.SerialNumber <- (if p.Value = null then "" else p.Value)
                | "YearOfConstruction"                  -> np.YearOfConstruction <- (if p.Value = null then "" else p.Value)
                | "DateOfManufacture"                   -> np.DateOfManufacture <- (if p.Value = null then "" else p.Value)
                | "HardwareVersion"                     -> np.HardwareVersion <- (if p.Value = null then "" else p.Value)
                | "FirmwareVersion"                     -> np.FirmwareVersion <- (if p.Value = null then "" else p.Value)
                | "SoftwareVersion"                     -> np.SoftwareVersion <- (if p.Value = null then "" else p.Value)
                | "CountryOfOrigin"                     -> np.CountryOfOrigin <- (if p.Value = null then "" else p.Value)
                | "UniqueFacilityIdentifier"            -> np.UniqueFacilityIdentifier <- (if p.Value = null then "" else p.Value)
                | "CompanyLogo"                         -> np.CompanyLogo <- (if p.Value = null then "" else p.Value)
                | _ -> ()
            | :? MultiLanguageProperty as mlp ->
                let v = if mlp.Value = null || mlp.Value.Count = 0 then "" else mlp.Value.[0].Text
                match mlp.IdShort with
                | "ManufacturerName"               -> np.ManufacturerName <- v
                | "ManufacturerProductDesignation" -> np.ManufacturerProductDesignation <- v
                | "ManufacturerProductRoot"        -> np.ManufacturerProductRoot <- v
                | "ManufacturerProductFamily"      -> np.ManufacturerProductFamily <- v
                | _ -> ()
            | :? SubmodelElementCollection as smc when smc.IdShort = "AddressInformation" ->
                np.AddressInformation <- smcToAddressInfo smc
            | :? SubmodelElementList as sml when sml.IdShort = "Markings" ->
                if sml.Value <> null then
                    for item in sml.Value do
                        match item with
                        | :? SubmodelElementCollection as msmc -> np.Markings.Add(smcToMarkingInfo msmc)
                        | _ -> ()
            | _ -> ()
        np

// ── HandoverDocumentation 역직렬화 (IDTA 02004-1-2) ─────────────────────────

let private smcToDocumentId (smc: SubmodelElementCollection) : DocumentId =
    let d = DocumentId()
    d.DocumentDomainId <- getAnyStringValue smc "DocumentDomainId"
    d.ValueId          <- getAnyStringValue smc "ValueId"
    let isPrimaryStr   = getAnyStringValue smc "IsPrimary"
    d.IsPrimary        <- isPrimaryStr.Equals("true", StringComparison.OrdinalIgnoreCase)
    d

let private smcToDocClassification (smc: SubmodelElementCollection) : DocumentClassification =
    let c = DocumentClassification()
    c.ClassId              <- getAnyStringValue smc "ClassId"
    c.ClassName            <- getAnyStringValue smc "ClassName"
    c.ClassificationSystem <- getAnyStringValue smc "ClassificationSystem"
    c

let private smcToDocVersion (smc: SubmodelElementCollection) : DocumentVersion =
    let dv = DocumentVersion()
    // Languages SML
    if smc.Value <> null then
        smc.Value |> Seq.iter (fun elem ->
            match elem with
            | :? SubmodelElementList as sml when sml.IdShort = "Languages" ->
                if sml.Value <> null then
                    for item in sml.Value do
                        match item with
                        | :? Property as p when p.Value <> null -> dv.Languages.Add(p.Value)
                        | _ -> ()
            | :? SubmodelElementList as sml when sml.IdShort = "DigitalFiles" ->
                if sml.Value <> null then
                    for item in sml.Value do
                        match item with
                        | :? Property as p when p.Value <> null -> dv.DigitalFiles.Add(p.Value)
                        | _ -> ()
            | _ -> ())
    dv.DocumentVersionId       <- getAnyStringValue smc "DocumentVersionId"
    dv.Title                   <- getAnyStringValue smc "Title"
    dv.SubTitle                <- getAnyStringValue smc "SubTitle"
    dv.Summary                 <- getAnyStringValue smc "Summary"
    dv.KeyWords                <- getAnyStringValue smc "KeyWords"
    dv.SetDate                 <- getAnyStringValue smc "SetDate"
    dv.StatusSetDate           <- getAnyStringValue smc "StatusSetDate"
    dv.StatusValue             <- getAnyStringValue smc "StatusValue"
    dv.OrganizationName        <- getAnyStringValue smc "OrganizationName"
    dv.OrganizationOfficialName <- getAnyStringValue smc "OrganizationOfficialName"
    dv.Role                    <- getAnyStringValue smc "Role"
    dv.PreviewFile             <- getAnyStringValue smc "PreviewFile"
    dv

let private smcToDocument (smc: SubmodelElementCollection) : Document =
    let doc = Document()
    if smc.Value <> null then
        for elem in smc.Value do
            match elem with
            | :? SubmodelElementList as sml ->
                match sml.IdShort with
                | "DocumentIds" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? SubmodelElementCollection as c -> doc.DocumentIds.Add(smcToDocumentId c)
                            | _ -> ()
                | "DocumentClassifications" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? SubmodelElementCollection as c -> doc.DocumentClassifications.Add(smcToDocClassification c)
                            | _ -> ()
                | "DocumentVersions" ->
                    if sml.Value <> null then
                        for item in sml.Value do
                            match item with
                            | :? SubmodelElementCollection as c -> doc.DocumentVersions.Add(smcToDocVersion c)
                            | _ -> ()
                | _ -> ()
            | _ -> ()
    doc

let private submodelToDocumentation (sm: ISubmodel) : HandoverDocumentation =
    let hd = HandoverDocumentation()
    if sm.SubmodelElements = null then hd
    else
        for elem in sm.SubmodelElements do
            match elem with
            | :? SubmodelElementList as sml when sml.IdShort = "Documents" ->
                if sml.Value <> null then
                    for item in sml.Value do
                        match item with
                        | :? SubmodelElementCollection as c -> hd.Documents.Add(smcToDocument c)
                        | _ -> ()
            | _ -> ()
        hd

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
                        submodelToProjectStore sm
                    else None)
            match result with
            | None ->
                log.Warn($"AASX 파싱 실패: '{SubmodelIdShort}' Submodel을 찾을 수 없습니다 ({path})")
                None
            | Some (project, store) ->
                // Nameplate Submodel 파싱
                env.Submodels
                |> Seq.tryFind (fun sm -> sm.IdShort = NameplateSubmodelIdShort)
                |> Option.iter (fun sm -> project.Nameplate <- submodelToNameplate sm)
                // Documentation Submodel 파싱
                env.Submodels
                |> Seq.tryFind (fun sm -> sm.IdShort = DocumentationSubmodelIdShort)
                |> Option.iter (fun sm -> project.HandoverDocumentation <- submodelToDocumentation sm)
                Some store)

let importIntoStore (store: DsStore) (path: string) : bool =
    match importFromAasxFile path with
    | Some imported ->
        store.ReplaceStore(imported)
        true
    | None -> false
