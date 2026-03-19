namespace Ds2.Aasx

open System
open AasCore.Aas3_0
open Ds2.Core
open Ds2.Aasx.AasxSemantics
open Ds2.Aasx.AasxConceptDescriptions
open Ds2.Aasx.AasxFileIO
open Ds2.UI.Core

module internal AasxExportGraph =

    open AasxExportCore

    let callToSmc (call: Call) : ISubmodelElement =
        mkSmc "Call" [
            mkProp     Name_         call.Name
            mkProp     Guid_         (call.Id.ToString())
            mkProp     DevicesAlias_ call.DevicesAlias
            mkProp     ApiName_      call.ApiName
            mkJsonProp<CallProperties>              Properties_      call.Properties
            mkJsonProp<Xywh option>                 Position_        call.Position
            mkProp     Status_       (string call.Status4)
            mkJsonProp<ResizeArray<ApiCall>>        ApiCalls_        call.ApiCalls
            mkJsonProp<ResizeArray<CallCondition>>  CallConditions_  call.CallConditions
        ]

    let arrowCallToSmc (arrow: ArrowBetweenCalls) : ISubmodelElement =
        mkSmc "Arrow" [
            mkProp Guid_    (arrow.Id.ToString())
            mkProp Source_  (arrow.SourceId.ToString())
            mkProp Target_  (arrow.TargetId.ToString())
            mkProp Type_    (string arrow.ArrowType)
        ]

    let workToSmc (store: DsStore) (work: Work) : ISubmodelElement =
        let rawCalls = DsQuery.callsOf work.Id store
        let calls    = rawCalls |> List.map callToSmc
        let callIds  = rawCalls |> List.map (fun c -> c.Id) |> Set.ofList
        let arrows   =
            DsQuery.arrowCallsOf work.Id store
            |> List.filter (fun a -> callIds.Contains a.SourceId && callIds.Contains a.TargetId)
            |> List.map arrowCallToSmc
        mkSmc "Work" [
            mkProp     Name_       work.Name
            mkProp     Guid_       (work.Id.ToString())
            mkProp     FlowGuid_   (work.ParentId.ToString())
            mkJsonProp<WorkProperties> Properties_ work.Properties
            mkJsonProp<Xywh option>    Position_   work.Position
            mkProp     Status_     (string work.Status4)
            mkProp     TokenRole_  (string (int work.TokenRole))
            mkSml Calls_   calls
            mkSml Arrows_  arrows
        ]

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
            mkJsonProp<FlowProperties> Properties_ flow.Properties
        ]

    let apiDefToSmc (apiDef: ApiDef) : ISubmodelElement =
        mkSmc "ApiDef" [
            mkProp     Name_         apiDef.Name
            mkProp     Guid_         (apiDef.Id.ToString())
            mkJsonProp<ApiDefProperties> Properties_ apiDef.Properties
        ]

    let apiCallToSmc (apiCall: ApiCall) : ISubmodelElement =
        let apiDefProp = apiCall.ApiDefId |> Option.map (fun id -> $"{{\"ApiDef\":\"{id}\"}}") |> Option.defaultValue "{}"
        mkSmc "ApiCall" [
            mkProp Name_       apiCall.Name
            mkProp Guid_       (apiCall.Id.ToString())
            mkProp Properties_ apiDefProp
        ]

    let systemToSmc (store: DsStore) (system: DsSystem) (isActive: bool) : ISubmodelElement =
        let allFlows  = DsQuery.flowsOf system.Id store
        let flows     = allFlows |> List.map flowToSmc
        let works     = allFlows |> List.collect (fun f -> DsQuery.worksOf f.Id store)
                                 |> List.map (workToSmc store)
        let arrows    = DsQuery.arrowWorksOf system.Id store
                        |> List.map arrowWorkToSmc
        let apiDefs   = DsQuery.apiDefsOf system.Id store |> List.map apiDefToSmc
        // ApiCalls/ReferencedApiDefs는 ActiveSystem 전용 — DeviceSystem은 빈 목록
        let apiCalls, referencedApiDefs =
            if isActive then
                let allAcs = DsQuery.allApiCalls store
                let acs = allAcs |> List.map apiCallToSmc
                let refs =
                    allAcs
                    |> List.choose (fun ac -> ac.ApiDefId |> Option.bind (fun id -> DsQuery.getApiDef id store))
                    |> List.filter (fun ad -> ad.ParentId <> system.Id)
                    |> List.distinctBy (fun ad -> ad.Id)
                    |> List.map apiDefToSmc
                acs, refs
            else [], []
        let iri       = system.IRI |> Option.defaultValue ""
        mkSmc "System" [
            mkProp     Name_             system.Name
            mkProp     Guid_             (system.Id.ToString())
            mkProp     IRI_              iri
            mkJsonProp<SystemProperties> Properties_ system.Properties
            mkSml ApiDefs_           apiDefs
            mkSml ApiCalls_          apiCalls
            mkSml ReferencedApiDefs_ referencedApiDefs
            mkSml Flows_             flows
            mkSml Arrows_            arrows
            mkSml Works_             works
        ]

    // ── Nameplate → AAS Submodel (IDTA 02006-3-0) ──────────────────────────────
