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

    let apiCallToSmc (apiCall: ApiCall) : ISubmodelElement =
        mkSmc "ApiCall" [
            mkProp Name_       apiCall.Name
            mkProp Guid_       (apiCall.Id.ToString())
            yield! (apiCall.ApiDefId |> Option.map (fun id -> mkProp ApiDefId_ (id.ToString())) |> Option.toList)
            mkJsonProp<IOTag option>  InTag_        apiCall.InTag
            mkJsonProp<IOTag option>  OutTag_       apiCall.OutTag
            mkJsonProp<ValueSpec>     InputSpec_    apiCall.InputSpec
            mkJsonProp<ValueSpec>     OutputSpec_   apiCall.OutputSpec
            yield! (apiCall.OriginFlowId |> Option.map (fun id -> mkProp OriginFlowId_ (id.ToString())) |> Option.toList)
        ]

    let callToSmc (call: Call) : ISubmodelElement =
        let apiCallSmcs = call.ApiCalls |> Seq.map apiCallToSmc |> List.ofSeq
        let smc = SubmodelElementCollection()
        // GUID를 IdShort로 사용: 접두사 "Call_" + GUID (하이픈 제거: "N" 형식)
        // 접두사를 붙여 영문자로 시작하도록 보장
        smc.IdShort <- sanitizeIdShort ("Call_" + call.Id.ToString("N"))
        smc.Value <- ResizeArray<ISubmodelElement>([
            mkProp     Name_         call.Name
            mkProp     Guid_         (call.Id.ToString())
            mkProp     DevicesAlias_ call.DevicesAlias
            mkProp     ApiName_      call.ApiName
            // Properties는 도메인별 서브모델(Simulation, Control 등)에만 저장
            mkJsonProp<Xywh option>                 Position_        call.Position
            mkProp     Status_       (string call.Status4)
            yield! mkSml ApiCalls_ apiCallSmcs |> Option.toList // Call 내부에 ApiCalls를 SubmodelElementList로 export
            mkJsonProp<ResizeArray<CallCondition>>  CallConditions_  call.CallConditions
        ])
        smc :> ISubmodelElement

    let arrowCallToSmc (arrow: ArrowBetweenCalls) : ISubmodelElement =
        mkSmc "Arrow" [
            mkProp Guid_    (arrow.Id.ToString())
            mkProp Source_  (arrow.SourceId.ToString())
            mkProp Target_  (arrow.TargetId.ToString())
            mkProp Type_    (string arrow.ArrowType)
        ]

    let workToSmc (store: DsStore) (work: Work) : ISubmodelElement =
        let rawCalls = Queries.callsOf work.Id store
        let calls    = rawCalls |> List.map callToSmc
        let callIds  = rawCalls |> List.map (fun c -> c.Id) |> Set.ofList
        let arrows   =
            Queries.arrowCallsOf work.Id store
            |> List.filter (fun a -> callIds.Contains a.SourceId && callIds.Contains a.TargetId)
            |> List.map arrowCallToSmc
        let smc = SubmodelElementCollection()
        // GUID를 IdShort로 사용: 접두사 "Work_" + GUID (하이픈 제거)
        smc.IdShort <- sanitizeIdShort ("Work_" + work.Id.ToString("N"))
        smc.Value <- ResizeArray<ISubmodelElement>([
            mkProp     Name_       work.Name
            mkProp     Guid_       (work.Id.ToString())
            mkProp     FlowGuid_   (work.ParentId.ToString())
            mkProp     FlowPrefix_ work.FlowPrefix
            mkProp     LocalName_  work.LocalName
            yield! (work.ReferenceOf |> Option.map (fun id -> mkProp ReferenceOf_ (id.ToString())) |> Option.toList)
            // Properties는 도메인별 서브모델(Simulation, Control 등)에만 저장
            mkJsonProp<Xywh option>    Position_   work.Position
            mkProp     Status_     (string work.Status4)
            mkProp     TokenRole_  (string (int work.TokenRole))
            yield! mkSml Calls_   calls |> Option.toList
            yield! mkSml Arrows_  arrows |> Option.toList
        ])
        smc :> ISubmodelElement

    let arrowWorkToSmc (arrow: ArrowBetweenWorks) : ISubmodelElement =
        mkSmc "Arrow" [
            mkProp Guid_    (arrow.Id.ToString())
            mkProp Source_  (arrow.SourceId.ToString())
            mkProp Target_  (arrow.TargetId.ToString())
            mkProp Type_    (string arrow.ArrowType)
        ]

    let flowToSmc (flow: Flow) : ISubmodelElement =
        mkSmc "Flow" [
            mkProp     Name_       flow.Name
            mkProp     Guid_       (flow.Id.ToString())
            // Properties는 도메인별 서브모델(Simulation, Control 등)에만 저장
        ]

    let apiDefToSmc (apiDef: ApiDef) : ISubmodelElement =
        mkSmc "ApiDef" [
            mkProp     Name_         apiDef.Name
            mkProp     Guid_         (apiDef.Id.ToString())
            mkProp     IsPush_       (string apiDef.IsPush)
            yield! (apiDef.TxGuid |> Option.map (fun id -> mkProp TxGuid_ (id.ToString())) |> Option.toList)
            yield! (apiDef.RxGuid |> Option.map (fun id -> mkProp RxGuid_ (id.ToString())) |> Option.toList)
        ]

    let systemToSmc (store: DsStore) (system: DsSystem) (isActive: bool) : ISubmodelElement =
        let allFlows  = Queries.flowsOf system.Id store
        let flows     = allFlows |> List.map flowToSmc
        let works     = allFlows |> List.collect (fun f -> Queries.worksOf f.Id store)
                                 |> List.map (workToSmc store)
        let arrows    = Queries.arrowWorksOf system.Id store
                        |> List.map arrowWorkToSmc
        let apiDefs   = Queries.apiDefsOf system.Id store |> List.map apiDefToSmc
        // ReferencedApiDefs는 ActiveSystem 전용 — DeviceSystem은 빈 목록
        let referencedApiDefs =
            if isActive then
                let allAcs = Queries.allApiCalls store
                allAcs
                |> List.choose (fun ac -> ac.ApiDefId |> Option.bind (fun id -> Queries.getApiDef id store))
                |> List.filter (fun ad -> ad.ParentId <> system.Id)
                |> List.distinctBy (fun ad -> ad.Id)
                |> List.map apiDefToSmc
            else []
        let iri       = system.IRI |> Option.defaultValue ""
        let smc = SubmodelElementCollection()
        // GUID를 IdShort로 사용: 접두사 "System_" + GUID (하이픈 제거)
        smc.IdShort <- sanitizeIdShort ("System_" + system.Id.ToString("N"))
        smc.Value <- ResizeArray<ISubmodelElement>([
            mkProp     Name_             system.Name
            mkProp     Guid_             (system.Id.ToString())
            mkProp     IRI_              iri
            // Properties는 도메인별 서브모델(Simulation, Control 등)에만 저장
            yield! mkSml ApiDefs_           apiDefs |> Option.toList
            yield! mkSml ReferencedApiDefs_ referencedApiDefs |> Option.toList
            yield! mkSml Flows_             flows |> Option.toList
            yield! mkSml Arrows_            arrows |> Option.toList
            yield! mkSml Works_             works |> Option.toList
        ])
        smc :> ISubmodelElement

    // ── Nameplate → AAS Submodel (IDTA 02006-3-0) ──────────────────────────────
