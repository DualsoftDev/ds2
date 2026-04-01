module Ds2.View3D.ContextBuilder

open System
open Ds2.Store
open Ds2.Store.DsQuery
open Ds2.View3D
open Ds2.View3D.ResultExtensions

/// SystemType이 DevicePresets에 등록된 이름과 일치하면 그대로 사용, 없으면 "Dummy"
let inferModelType (systemType: string option) : string =
    match systemType with
    | None -> "Dummy"
    | Some st ->
        if Set.contains st DevicePresets.KnownNames then st
        else "Dummy"

// =============================================================================
// Flow Extraction (성능 최적화: 전체 Call 1회 순회)
// =============================================================================

/// System → Flow 맵 빌드 (전체 Call을 한 번만 순회)
let private buildSystemToFlowsMap (store: DsStore) : Map<Guid, Set<string>> =
    store.Calls.Values
    |> Seq.collect (fun call ->
        call.ApiCalls
        |> Seq.choose (fun apiCall ->
            match apiCall.ApiDefId with
            | Some apiDefId ->
                match Queries.getApiDef apiDefId store with
                | Some apiDef ->
                    match Queries.getWork call.ParentId store with
                    | Some work ->
                        match Queries.getFlow work.ParentId store with
                        | Some flow -> Some (apiDef.ParentId, flow.Name)
                        | None -> None
                    | None -> None
                | _ -> None
            | None -> None
        )
    )
    |> Seq.groupBy fst
    |> Seq.map (fun (systemId, pairs) ->
        let flowNames = pairs |> Seq.map snd |> Set.ofSeq
        (systemId, flowNames)
    )
    |> Map.ofSeq

/// Device가 속한 Flow 목록 추출
let extractParticipatingFlows (systemToFlowsMap: Map<Guid, Set<string>>) (systemId: Guid) : string list =
    match Map.tryFind systemId systemToFlowsMap with
    | Some flowSet -> Set.toList flowSet
    | None -> []

/// Device의 주요 Flow 결정 (사용 빈도 기반)
let determinePrimaryFlow (participatingFlows: string list) : string option =
    match participatingFlows with
    | [] -> None
    | flows ->
        flows
        |> List.countBy id
        |> List.maxBy snd
        |> fst
        |> Some

// =============================================================================
// CallerCount Calculation
// =============================================================================

/// ApiDef별 호출 Call 수 계산 (전체 Call 1회 순회)
let private buildCallerCountMap (store: DsStore) : Map<Guid, int> =
    store.Calls.Values
    |> Seq.collect (fun call ->
        call.ApiCalls |> Seq.choose (fun ac -> ac.ApiDefId)
    )
    |> Seq.countBy id
    |> Map.ofSeq

// =============================================================================
// ApiDef Extraction
// =============================================================================

/// System의 ApiDef 목록 추출 (CallerCount 포함)
let extractApiDefs (store: DsStore) (callerCountMap: Map<Guid, int>) (systemId: Guid) : ApiDefInfo list =
    store.ApiDefs.Values
    |> Seq.filter (fun apiDef -> apiDef.ParentId = systemId)
    |> Seq.map (fun apiDef ->
        {
            Id = apiDef.Id
            Name = apiDef.Name
            CallerCount = Map.tryFind apiDef.Id callerCountMap |> Option.defaultValue 0
        }
    )
    |> Seq.toList

// =============================================================================
// Device Extraction
// =============================================================================

/// Project의 모든 Device 추출 (성능 최적화: 전체 맵 미리 빌드)
let extractDevices (store: DsStore) (projectId: Guid) : Result<DeviceInfo list, SceneError> =
    match Queries.getProject projectId store with
    | None ->
        Log.error "Project not found: %A" projectId
        Error (ProjectNotFound projectId)
    | Some project ->
        Log.info "Extracting devices for project: %s" project.Name

        let allSystemIds =
            Seq.append project.ActiveSystemIds project.PassiveSystemIds
            |> Seq.toList

        Log.info "Found %d systems in project" allSystemIds.Length

        // 성능 최적화: 전체 맵 미리 빌드 (전체 Call 1회 순회)
        let systemToFlowsMap = buildSystemToFlowsMap store
        let callerCountMap = buildCallerCountMap store
        Log.debug "Built system-to-flows map with %d entries" systemToFlowsMap.Count

        allSystemIds
        |> List.map (fun systemId ->
            match Queries.getSystem systemId store with
            | None ->
                Log.warn "System not found: %A" systemId
                Error (SystemNotFound systemId)
            | Some system ->
                let participatingFlows = extractParticipatingFlows systemToFlowsMap systemId
                let primaryFlow = determinePrimaryFlow participatingFlows
                let systemType = system.SimulationProperties |> Option.bind (fun p -> p.SystemType)
                let modelType = inferModelType systemType
                let apiDefs = extractApiDefs store callerCountMap systemId

                Log.debug "Device: %s, ModelType: %s, Flows: %A" system.Name modelType participatingFlows

                Ok {
                    Id = systemId
                    Name = system.Name
                    SystemType = systemType
                    ModelType  = modelType
                    FlowName = primaryFlow |> Option.defaultValue "Unassigned"
                    ParticipatingFlows = participatingFlows
                    IsUsedInSimulation = not participatingFlows.IsEmpty
                    ApiDefs = apiDefs
                    Position = None  // Layout 단계에서 설정
                }
        )
        |> sequenceResultA
