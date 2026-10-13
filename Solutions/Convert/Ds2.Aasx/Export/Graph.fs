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

    /// Export 1회 동안 재사용하는 스냅샷 묶음.
    ///
    /// <para>store 의 부모→자식 질의는 하나하나가 해당 딕셔너리 전수 스캔이라, Work 마다 Call 을
    /// 묻는 export 는 O(Work × Call) 로 자란다. 도메인 서브모델이 8종이라 그 값이 다시 8배가 된다.
    /// 진입 시 한 번만 그룹핑해 그 안에서 쓴다.</para>
    ///
    /// <para>인덱스는 만든 시점의 스냅샷이다. Export 는 엔티티 속성만 채우고(빈 도메인 속성 생성)
    /// 계층 자체는 바꾸지 않으므로 안전하다 — 노드를 추가·삭제하는 코드가 export 경로에 들어오면
    /// 그 시점에 인덱스를 다시 만들어야 한다. store 에 얹지 않는 이유는 StoreIndex 주석 참조.</para>
    type AasxExportContext =
        { Hierarchy : StoreHierarchyIndex
          /// 모든 ApiCall 이 가리키는 ApiDef 한 벌(중복 제거). System 마다 다시 모으면 O(System × ApiCall).
          /// System 별 필터(자기 소유 제외) 는 꺼내 쓰는 쪽에서 건다 — 전체 목록은 System 과 무관하고,
          /// distinctBy 는 Id 단위라 필터 순서를 바꿔도 남는 원소와 순서가 같다.
          ReferencedApiDefs : Lazy<ApiDef list> }

    let buildExportContext (store: DsStore) : AasxExportContext =
        { Hierarchy = buildHierarchyIndex store
          ReferencedApiDefs =
            lazy (Queries.allApiCalls store
                  |> List.choose (fun ac -> ac.ApiDefId |> Option.bind (fun id -> Queries.getApiDef id store))
                  |> List.distinctBy (fun ad -> ad.Id)) }

    // ── CD URI 매핑 ────────────────────────────────────────────────────────────
    // BaseUrl 은 AasxSemantics.CdBaseUrl 단일 진실 원천에서 가져온다.
    // semanticId 는 컬렉션 노드(ActiveSystems·Flows·Works…)에만 붙인다. 스칼라 속성(Guid·Name·Status…)에는
    // 붙이지 않는다 — 가져오기는 처음부터 idShort 로만 읽었고, 속성마다 ~300B 참조 블록이 붙으면 현장 모델
    // (속성 69만 개)에서 XML 의 절반 이상이 그 반복이었다(2026-10-13).
    let private cdBase = CdBaseUrl + "/sm/SequenceModel"

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
        mkSmc "Arrow" [
            yield mkProp "Guid"   (arrow.Id.ToString())
            yield mkProp "Source" (arrow.SourceId.ToString())
            yield mkProp "Target" (arrow.TargetId.ToString())
            yield mkProp "Type"   (string arrow.ArrowType)
        ]

    /// AasxField Attribute가 있는 속성들을 자동으로 Property로 변환 (스칼라 속성에는 semanticId 를 붙이지 않는다)
    let internal mkPropsFromAasxFields<'T> (entity: 'T) =
        let entityType = entity.GetType()  // 런타임 타입 사용 (상속 고려)

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
                    // 특수 타입 처리
                    let elemOpt =
                        if prop.PropertyType = typeof<Xywh option> then
                            Some (mkJsonProp<Xywh option> attr.FieldName (value :?> Xywh option))
                        elif prop.PropertyType = typeof<TimeSpan option> then
                            match value :?> TimeSpan option with
                            | Some ts -> Some (mkTimeSpanProp attr.FieldName ts)
                            | None -> None
                        elif prop.PropertyType = typeof<ResizeArray<Condition>> then
                            Some (mkJsonProp<ResizeArray<Condition>> attr.FieldName (value :?> ResizeArray<Condition>))
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
                        elif prop.PropertyType = typeof<SequenceLabel> then
                            let sl = value :?> SequenceLabel
                            Some (mkProp attr.FieldName (string sl))
                        elif prop.PropertyType = typeof<ArrowType> then
                            let at = value :?> ArrowType
                            Some (mkProp attr.FieldName (string at))
                        elif prop.PropertyType = typeof<ActionType> then
                            Some (mkJsonProp<ActionType> attr.FieldName (value :?> ActionType))
                        elif prop.PropertyType = typeof<SensingType> then
                            Some (mkJsonProp<SensingType> attr.FieldName (value :?> SensingType))
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
                    elemOpt
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

    let private workToSmc (ctx: AasxExportContext) (work: Work) (projectId: Guid) =
        let rawCalls = ctx.Hierarchy.Calls work.Id
        let calls = rawCalls |> List.map (fun c -> callToSmc c projectId)
        let callIds = rawCalls |> List.map (fun c -> c.Id) |> Set.ofList
        let arrows =
            ctx.Hierarchy.ArrowCalls work.Id
            |> List.filter (fun a -> callIds.Contains a.SourceId && callIds.Contains a.TargetId)
            |> List.map arrowCallToSmc
        let domainRefs = mkDomainRefsForWork projectId work.Id work

        let smc = SubmodelElementCollection()
        smc.IdShort <- mkEntityIdShort "Work" work.Id
        smc.Value <- ResizeArray<ISubmodelElement>([
            yield mkProp FlowGuid_ (work.ParentId.ToString())
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

    let systemToSmc (ctx: AasxExportContext) (system: DsSystem) (isActive: bool) (projectId: Guid) =
        let index = ctx.Hierarchy
        let allFlows = index.Flows system.Id
        let flows = allFlows |> List.map (fun f -> flowToSmc f projectId)
        let works = allFlows |> List.collect (fun f -> index.Works f.Id)
                             |> List.map (fun w -> workToSmc ctx w projectId)
        let arrows = index.ArrowWorks system.Id |> List.map arrowWorkToSmc
        let apiDefs = index.ApiDefs system.Id |> List.map apiDefToSmc
        let referencedApiDefs =
            if isActive then
                ctx.ReferencedApiDefs.Value
                |> List.filter (fun ad -> ad.ParentId <> system.Id)
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
