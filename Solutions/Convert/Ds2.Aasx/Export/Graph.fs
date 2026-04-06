namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.Store
open Ds2.Store.DsQuery

module internal AasxExportGraph =

    open AasxExportCore

    // ────────────────────────────────────────────────────────────────────────────
    // 헬퍼 함수들
    // ────────────────────────────────────────────────────────────────────────────

    /// GUID 기반 idShort 생성
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
    let private mkArrowSmc (id: Guid) (sourceId: Guid) (targetId: Guid) (arrowType: ArrowType) =
        mkSmc "Arrow" [
            yield mkProp Guid_    (id.ToString())
            yield mkProp Source_  (sourceId.ToString())
            yield mkProp Target_  (targetId.ToString())
            yield mkProp Type_    (string arrowType)
        ]

    // ────────────────────────────────────────────────────────────────────────────
    // Entity → SMC 변환 함수들
    // ────────────────────────────────────────────────────────────────────────────

    let private apiCallToSmc (apiCall: ApiCall) =
        mkSmc "ApiCall" [
            yield mkProp Name_ apiCall.Name
            yield mkProp Guid_ (apiCall.Id.ToString())
            yield! apiCall.ApiDefId |> Option.map (fun id -> mkProp ApiDefId_ (id.ToString())) |> Option.toList
            yield mkJsonProp<IOTag option> InTag_ apiCall.InTag
            yield mkJsonProp<IOTag option> OutTag_ apiCall.OutTag
            yield mkJsonProp<ValueSpec> InputSpec_ apiCall.InputSpec
            yield mkJsonProp<ValueSpec> OutputSpec_ apiCall.OutputSpec
            yield! apiCall.OriginFlowId |> Option.map (fun id -> mkProp OriginFlowId_ (id.ToString())) |> Option.toList
        ]

    let private callToSmc (call: Call) (projectId: Guid) =
        let domainRefs = mkDomainRefsForCall projectId call.Id call
        let smc = SubmodelElementCollection()
        smc.IdShort <- mkEntityIdShort "Call" call.Id
        smc.Value <- ResizeArray<ISubmodelElement>([
            yield mkProp Name_ call.Name
            yield mkProp Guid_ (call.Id.ToString())
            yield mkProp DevicesAlias_ call.DevicesAlias
            yield mkProp ApiName_ call.ApiName
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
            yield mkJsonProp<Xywh option> Position_ call.Position
            yield mkProp Status_ (string call.Status4)
            yield! call.ApiCalls |> Seq.map apiCallToSmc |> List.ofSeq |> mkSml ApiCalls_ |> Option.toList
            yield mkJsonProp<ResizeArray<CallCondition>> CallConditions_ call.CallConditions
        ])
        smc :> ISubmodelElement

    let private arrowCallToSmc (arrow: ArrowBetweenCalls) =
        mkArrowSmc arrow.Id arrow.SourceId arrow.TargetId arrow.ArrowType

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
            yield mkProp Name_ work.Name
            yield mkProp Guid_ (work.Id.ToString())
            yield mkProp FlowGuid_ (work.ParentId.ToString())
            yield mkProp FlowPrefix_ work.FlowPrefix
            yield mkProp LocalName_ work.LocalName
            yield! work.ReferenceOf |> Option.map (fun id -> mkProp ReferenceOf_ (id.ToString())) |> Option.toList
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
            yield mkJsonProp<Xywh option> Position_ work.Position
            yield mkProp Status_ (string work.Status4)
            yield mkProp TokenRole_ (string (int work.TokenRole))
            yield! work.Duration |> Option.map (mkTimeSpanProp "Duration") |> Option.toList
            yield! mkSml Calls_ calls |> Option.toList
            yield! mkSml Arrows_ arrows |> Option.toList
        ])
        smc :> ISubmodelElement

    let private arrowWorkToSmc (arrow: ArrowBetweenWorks) =
        mkArrowSmc arrow.Id arrow.SourceId arrow.TargetId arrow.ArrowType

    let private flowToSmc (flow: Flow) (projectId: Guid) =
        let domainRefs = mkDomainRefsForFlow projectId flow.Id flow
        mkSmc "Flow" [
            yield mkProp Name_ flow.Name
            yield mkProp Guid_ (flow.Id.ToString())
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
        ]

    let private apiDefToSmc (apiDef: ApiDef) =
        mkSmc "ApiDef" [
            yield mkProp Name_ apiDef.Name
            yield mkProp Guid_ (apiDef.Id.ToString())
            yield mkProp IsPush_ (string apiDef.IsPush)
            yield! apiDef.TxGuid |> Option.map (fun id -> mkProp TxGuid_ (id.ToString())) |> Option.toList
            yield! apiDef.RxGuid |> Option.map (fun id -> mkProp RxGuid_ (id.ToString())) |> Option.toList
        ]

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
            yield mkProp Name_ system.Name
            yield mkProp Guid_ (system.Id.ToString())
            yield mkProp IRI_ (system.IRI |> Option.defaultValue "")
            yield! mkSmcOpt "DomainReferences" domainRefs |> Option.toList
            yield! mkSml ApiDefs_ apiDefs |> Option.toList
            yield! mkSml ReferencedApiDefs_ referencedApiDefs |> Option.toList
            yield! mkSml Flows_ flows |> Option.toList
            yield! mkSml Arrows_ arrows |> Option.toList
            yield! mkSml Works_ works |> Option.toList
        ])
        smc :> ISubmodelElement

    // ── Nameplate → AAS Submodel (IDTA 02006-3-0) ──────────────────────────────
