namespace Ds2.Aasx

open System
open System.Reflection
open AasCore.Aas3_1
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Core.Store


module internal AasxExportGraph =

    open AasxExportCore

    // ── CD URI 매핑: (TypeName, FieldName) → CD URI ───────────────────────────
    let private cdBase = "https://dualsoft.com/cd/SequenceModel"

    let private cdUriMap : Map<string * string, string> =
        Map.ofList [
            // Project
            ("Project",  "Guid"),             $"{cdBase}/Project/Guid"
            ("Project",  "Name"),             $"{cdBase}/Project/Name"
            ("Project",  "Author"),           $"{cdBase}/Project/Author"
            ("Project",  "Version"),          $"{cdBase}/Project/Version"
            ("Project",  "DateTime"),         $"{cdBase}/Project/DateTime"
            ("Project",  "TokenSpecs"),       $"{cdBase}/Project/TokenSpecs"
            // DsSystem
            ("DsSystem", "Guid"),             $"{cdBase}/System/Guid"
            ("DsSystem", "Name"),             $"{cdBase}/System/Name"
            ("DsSystem", "IRI"),              $"{cdBase}/System/IRI"
            ("DsSystem", "SystemType"),       $"{cdBase}/System/SystemType"
            // Flow
            ("Flow",     "Guid"),             $"{cdBase}/Flow/Guid"
            ("Flow",     "Name"),             $"{cdBase}/Flow/Name"
            // Work
            ("Work",     "Guid"),             $"{cdBase}/Work/Guid"
            ("Work",     "Name"),             $"{cdBase}/Work/Name"
            ("Work",     "Status"),           $"{cdBase}/Work/Status"
            ("Work",     "Position"),         $"{cdBase}/Work/Position"
            ("Work",     "TokenRole"),        $"{cdBase}/Work/TokenRole"
            ("Work",     "Duration"),         $"{cdBase}/Work/Duration"
            ("Work",     "ReferenceOf"),      $"{cdBase}/Work/ReferenceOf"
            // Call
            ("Call",     "Guid"),             $"{cdBase}/Call/Guid"
            ("Call",     "Name"),             $"{cdBase}/Call/Name"
            ("Call",     "Status"),           $"{cdBase}/Call/Status"
            ("Call",     "Position"),         $"{cdBase}/Call/Position"
            ("Call",     "ReferenceOf"),      $"{cdBase}/Call/ReferenceOf"
            ("Call",     "CallConditions"),   $"{cdBase}/Call/CallConditions"
            // ApiCall
            ("ApiCall",  "Guid"),             $"{cdBase}/ApiCall/Guid"
            ("ApiCall",  "Name"),             $"{cdBase}/ApiCall/Name"
            ("ApiCall",  "InTag"),            $"{cdBase}/ApiCall/InTag"
            ("ApiCall",  "OutTag"),           $"{cdBase}/ApiCall/OutTag"
            ("ApiCall",  "ApiDefId"),         $"{cdBase}/ApiCall/ApiDefId"
            ("ApiCall",  "InputSpec"),        $"{cdBase}/ApiCall/InputSpec"
            ("ApiCall",  "OutputSpec"),       $"{cdBase}/ApiCall/OutputSpec"
            ("ApiCall",  "OriginFlowId"),     $"{cdBase}/ApiCall/OriginFlowId"
            // ApiDef
            ("ApiDef",   "Guid"),             $"{cdBase}/ApiDef/Guid"
            ("ApiDef",   "Name"),             $"{cdBase}/ApiDef/Name"
            ("ApiDef",   "ApiDefActionType"), $"{cdBase}/ApiDef/ApiDefActionType"
            ("ApiDef",   "TxGuid"),           $"{cdBase}/ApiDef/TxGuid"
            ("ApiDef",   "RxGuid"),           $"{cdBase}/ApiDef/RxGuid"
            // Arrow
            ("ArrowBetweenWorks", "Guid"),    $"{cdBase}/Arrow/Guid"
            ("ArrowBetweenWorks", "Source"),  $"{cdBase}/Arrow/Source"
            ("ArrowBetweenWorks", "Target"),  $"{cdBase}/Arrow/Target"
            ("ArrowBetweenWorks", "Type"),    $"{cdBase}/Arrow/Type"
            ("ArrowBetweenCalls", "Guid"),    $"{cdBase}/Arrow/Guid"
            ("ArrowBetweenCalls", "Source"),  $"{cdBase}/Arrow/Source"
            ("ArrowBetweenCalls", "Target"),  $"{cdBase}/Arrow/Target"
            ("ArrowBetweenCalls", "Type"),    $"{cdBase}/Arrow/Type"
        ]

    // 컬렉션 노드 CD URI
    let private collectionCdUri : Map<string, string> =
        Map.ofList [
            ActiveSystems_,     $"{cdBase}/ActiveSystems"
            Flows_,             $"{cdBase}/Flows"
            Works_,             $"{cdBase}/Works"
            Calls_,             $"{cdBase}/Calls"
            Arrows_,            $"{cdBase}/Arrows"
            ApiDefs_,           $"{cdBase}/ApiDefs"
            ApiCalls_,          $"{cdBase}/ApiCalls"
            ReferencedApiDefs_, $"{cdBase}/ReferencedApiDefs"
            DeviceReferences_,  $"{cdBase}/DeviceReferences"
        ]

    let internal mkSmlSem (idShort: string) (items: ISubmodelElement list) : ISubmodelElement option =
        mkSml idShort items
        |> Option.map (withSemId (Map.tryFind idShort collectionCdUri))

    let internal mkSmcSem (idShort: string) (elems: ISubmodelElement list) : ISubmodelElement =
        mkSmc idShort elems
        |> withSemId (Map.tryFind idShort collectionCdUri)

    let private mkEntityIdShort (prefix: string) (id: Guid) =
        sanitizeIdShort (sprintf "%s_%s" prefix (id.ToString("N")))

    /// 도메인 서브모델로의 역방향 Reference 생성 (AASd-128 준수)
    let private mkDomainReference (projectId: Guid) (submodelType: SubmodelType) (entityType: string) (entityId: Guid) : ISubmodelElement =
        let domainSubmodelId = mkSubmodelId projectId submodelType.Offset
        let targetIdShort = mkEntityIdShort entityType entityId
        let refElem = ReferenceElement()
        refElem.IdShort <- sanitizeIdShort submodelType.RefName
        refElem.Value <- Reference(
            ReferenceTypes.ModelReference,
            ResizeArray<IKey>([
                Key(KeyTypes.Submodel, domainSubmodelId) :> IKey
                Key(KeyTypes.SubmodelElementCollection, sprintf "%sProperties" entityType) :> IKey
                Key(KeyTypes.SubmodelElementCollection, targetIdShort) :> IKey
            ])) :> IReference
        refElem :> ISubmodelElement

    /// Properties 존재 여부 확인 (통합 버전 - PropertyConversion 사용)
    let private hasProperty (submodelType: SubmodelType) (entity: 'a) : bool =
        PropertyConversion.getEntityElements submodelType (entity :> obj) |> List.isEmpty |> not

    /// 도메인 참조 리스트 생성 (통합 버전)
    let private mkDomainRefs<'T> (projectId: Guid) (entityType: string) (entityId: Guid) (entity: 'T) =
        SubmodelType.AllDomains
        |> List.filter (fun sm -> hasProperty sm entity)
        |> List.map (fun sm -> mkDomainReference projectId sm entityType entityId)

    let private mkDomainRefsForSystem (projectId: Guid) (entityId: Guid) (sys: DsSystem) =
        mkDomainRefs projectId "System" entityId sys

    let private mkDomainRefsForFlow (projectId: Guid) (entityId: Guid) (flow: Flow) =
        mkDomainRefs projectId "Flow" entityId flow

    let private mkDomainRefsForWork (projectId: Guid) (entityId: Guid) (work: Work) =
        mkDomainRefs projectId "Work" entityId work

    let private mkDomainRefsForCall (projectId: Guid) (entityId: Guid) (call: Call) =
        mkDomainRefs projectId "Call" entityId call

    /// Arrow SMC 생성 (Call/Work 공통)
    let private mkArrowSmc (arrow: DsArrow) =
        let tn = arrow.GetType().Name
        let sem f = Map.tryFind (tn, f) cdUriMap
        mkSmc "Arrow" [
            yield mkProp "Guid"   (arrow.Id.ToString())       |> withSemId (sem "Guid")
            yield mkProp "Source" (arrow.SourceId.ToString()) |> withSemId (sem "Source")
            yield mkProp "Target" (arrow.TargetId.ToString()) |> withSemId (sem "Target")
            yield mkProp "Type"   (string arrow.ArrowType)    |> withSemId (sem "Type")
        ]

    /// AasxField Attribute가 있는 속성들을 자동으로 Property로 변환 (semanticId 포함)
    let internal mkPropsFromAasxFields<'T> (entity: 'T) =
        let entityType = entity.GetType()  // 런타임 타입 사용 (상속 고려)
        let typeName = entityType.Name

        // 현재 타입과 모든 베이스 타입의 속성을 가져오기
        let rec getAllProperties (t: Type) =
            seq {
                yield! t.GetProperties(BindingFlags.Public ||| BindingFlags.Instance ||| BindingFlags.DeclaredOnly)
                if t.BaseType <> null && t.BaseType <> typeof<obj> then
                    yield! getAllProperties t.BaseType
            }

        let props = getAllProperties entityType |> Seq.toArray

        props
        |> Seq.distinctBy (fun p -> p.Name)  // 중복 속성 제거 (abstract + default 패턴 대응)
        |> Seq.toArray
        |> Array.choose (fun prop ->
            match prop.GetCustomAttribute<AasxFieldAttribute>(true) |> box with  // inherit=true, 상속된 어트리뷰트 포함
                | null -> None
                | :? AasxFieldAttribute as attr when not attr.Skip ->
                    let value = prop.GetValue(entity)
                    let cdUri = Map.tryFind (typeName, attr.FieldName) cdUriMap
                    // 특수 타입 처리
                    let elemOpt =
                        if prop.PropertyType = typeof<Xywh option> then
                            Some (mkJsonProp<Xywh option> attr.FieldName (value :?> Xywh option))
                        elif prop.PropertyType = typeof<TimeSpan option> then
                            match value :?> TimeSpan option with
                            | Some ts -> Some (mkTimeSpanProp attr.FieldName ts)
                            | None -> None
                        elif prop.PropertyType = typeof<ResizeArray<CallCondition>> then
                            Some (mkJsonProp<ResizeArray<CallCondition>> attr.FieldName (value :?> ResizeArray<CallCondition>))
                        elif prop.PropertyType = typeof<ResizeArray<TokenSpec>> then
                            Some (mkJsonProp<ResizeArray<TokenSpec>> attr.FieldName (value :?> ResizeArray<TokenSpec>))
                        elif prop.PropertyType = typeof<IOTag option> then
                            Some (mkJsonProp<IOTag option> attr.FieldName (value :?> IOTag option))
                        elif prop.PropertyType = typeof<ValueSpec> then
                            Some (mkJsonProp<ValueSpec> attr.FieldName (value :?> ValueSpec))
                        elif prop.PropertyType = typeof<DateTimeOffset> then
                            let dt = value :?> DateTimeOffset
                            Some (mkProp attr.FieldName (dt.ToString("o")))
                        elif prop.PropertyType = typeof<TokenRole> then
                            let tr = value :?> TokenRole
                            Some (mkProp attr.FieldName (string (int tr)))
                        elif prop.PropertyType = typeof<Status4> then
                            let st = value :?> Status4
                            Some (mkProp attr.FieldName (string st))
                        elif prop.PropertyType = typeof<ArrowType> then
                            let at = value :?> ArrowType
                            Some (mkProp attr.FieldName (string at))
                        elif prop.PropertyType = typeof<ApiDefActionType> then
                            Some (mkJsonProp<ApiDefActionType> attr.FieldName (value :?> ApiDefActionType))
                        elif prop.PropertyType = typeof<bool> then
                            Some (mkProp attr.FieldName (string (value :?> bool)))
                        else
                            // 기본 문자열 변환
                            let strValue =
                                match value with
                                | null -> ""
                                | :? Guid as g -> g.ToString()
                                | :? string as s -> s
                                | :? (string option) as opt -> opt |> Option.defaultValue ""
                                | :? (Guid option) as opt -> opt |> Option.map (fun g -> g.ToString()) |> Option.defaultValue ""
                                | v -> v.ToString()
                            Some (mkProp attr.FieldName strValue)
                    elemOpt |> Option.map (withSemId cdUri)
                | _ -> None)
        |> Array.toList

    let private apiCallToSmc (apiCall: ApiCall) =
        mkSmc "ApiCall" (mkPropsFromAasxFields apiCall)

    let private callToSmc (call: Call) (projectId: Guid) =
        let domainRefs = mkDomainRefsForCall projectId call.Id call
        let smc = SubmodelElementCollection()
        smc.IdShort <- mkEntityIdShort "Call" call.Id
        smc.Value <- ResizeArray<ISubmodelElement>([
            yield! mkPropsFromAasxFields call
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
            yield! call.ApiCalls |> Seq.map apiCallToSmc |> List.ofSeq |> mkSmlSem ApiCalls_ |> Option.toList
        ])
        smc :> ISubmodelElement

    let private arrowCallToSmc (arrow: ArrowBetweenCalls) =
        mkArrowSmc arrow

    let private workToSmc (store: DsStore) (work: Work) (projectId: Guid) =
        let rawCalls = Queries.callsOf work.Id store
        let calls = rawCalls |> List.map (fun c -> callToSmc c projectId)
        let callIds = rawCalls |> List.map (fun c -> c.Id) |> Set.ofList
        let arrows =
            Queries.arrowCallsOf work.Id store
            |> List.filter (fun a -> callIds.Contains a.SourceId && callIds.Contains a.TargetId)
            |> List.map arrowCallToSmc
        let domainRefs = mkDomainRefsForWork projectId work.Id work

        let smc = SubmodelElementCollection()
        smc.IdShort <- mkEntityIdShort "Work" work.Id
        smc.Value <- ResizeArray<ISubmodelElement>([
            yield mkProp FlowGuid_ (work.ParentId.ToString()) |> withSemId (Some $"{cdBase}/Work/FlowGuid")
            yield! mkPropsFromAasxFields work
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
            yield! mkSmlSem Calls_ calls |> Option.toList
            yield! mkSmlSem Arrows_ arrows |> Option.toList
        ])
        smc :> ISubmodelElement

    let private arrowWorkToSmc (arrow: ArrowBetweenWorks) =
        mkArrowSmc arrow

    let private flowToSmc (flow: Flow) (projectId: Guid) =
        let domainRefs = mkDomainRefsForFlow projectId flow.Id flow
        mkSmc "Flow" [
            yield! mkPropsFromAasxFields flow
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
        ]

    let private apiDefToSmc (apiDef: ApiDef) =
        mkSmc "ApiDef" (mkPropsFromAasxFields apiDef)

    let systemToSmc (store: DsStore) (system: DsSystem) (isActive: bool) (projectId: Guid) =
        let allFlows = Queries.flowsOf system.Id store
        let flows = allFlows |> List.map (fun f -> flowToSmc f projectId)
        let works = allFlows |> List.collect (fun f -> Queries.worksOf f.Id store)
                             |> List.map (fun w -> workToSmc store w projectId)
        let arrows = Queries.arrowWorksOf system.Id store |> List.map arrowWorkToSmc
        let apiDefs = Queries.apiDefsOf system.Id store |> List.map apiDefToSmc
        let referencedApiDefs =
            if isActive then
                Queries.allApiCalls store
                |> List.choose (fun ac -> ac.ApiDefId |> Option.bind (fun id -> Queries.getApiDef id store))
                |> List.filter (fun ad -> ad.ParentId <> system.Id)
                |> List.distinctBy (fun ad -> ad.Id)
                |> List.map apiDefToSmc
            else []
        let domainRefs = mkDomainRefsForSystem projectId system.Id system

        let smc = SubmodelElementCollection()
        smc.IdShort <- mkEntityIdShort "System" system.Id
        smc.Value <- ResizeArray<ISubmodelElement>([
            yield! mkPropsFromAasxFields system
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
            yield! mkSmlSem ApiDefs_ apiDefs |> Option.toList
            yield! mkSmlSem ReferencedApiDefs_ referencedApiDefs |> Option.toList
            yield! mkSmlSem Flows_ flows |> Option.toList
            yield! mkSmlSem Arrows_ arrows |> Option.toList
            yield! mkSmlSem Works_ works |> Option.toList
        ])
        smc :> ISubmodelElement

    // ── Nameplate → AAS Submodel (IDTA 02006-3-0) ──────────────────────────────
