namespace Ds2.Mermaid

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Store

module internal MermaidTargetPlanning =

    open MermaidMapperCommon

    type PlannedCallNodes = {
        NodeToCallId: Dictionary<string, Guid>
        CallNameGroups: Dictionary<string, ResizeArray<Guid>>
        AllNodes: ResizeArray<MermaidNode * Call>
        CallsById: Dictionary<Guid, Call>
    }

    let createPlannedCallNodes () = {
        NodeToCallId = Dictionary<string, Guid>()
        CallNameGroups = Dictionary<string, ResizeArray<Guid>>()
        AllNodes = ResizeArray<MermaidNode * Call>()
        CallsById = Dictionary<Guid, Call>()
    }

    let private registerCallName (planned: PlannedCallNodes) callId callName =
        match planned.CallNameGroups.TryGetValue(callName) with
        | true, ids -> ids.Add(callId)
        | _ ->
            let ids = ResizeArray<Guid>()
            ids.Add(callId)
            planned.CallNameGroups.[callName] <- ids

    let private buildUniqueCallNameMap (planned: PlannedCallNodes) =
        let map = Dictionary<string, Guid>()
        for KeyValue(name, ids) in planned.CallNameGroups do
            if ids.Count = 1 then
                map.[name] <- ids.[0]
        map

    let registerCallNode
        (planned: PlannedCallNodes)
        (operations: ResizeArray<ImportPlanOperation>)
        parentWorkId
        (node: MermaidNode)
        =
        let devicesAlias, apiName = splitCallName node.Label
        let call = Call(devicesAlias, apiName, parentWorkId)
        operations.Add(AddCall call)
        planned.NodeToCallId.[node.Id] <- call.Id
        planned.CallsById.[call.Id] <- call
        registerCallName planned call.Id call.Name
        planned.AllNodes.Add(node, call)
        call, apiName

    let addInternalCallArrows
        (operations: ResizeArray<ImportPlanOperation>)
        parentId
        (planned: PlannedCallNodes)
        edges
        =
        for edge in edges do
            match planned.NodeToCallId.TryGetValue(edge.SourceId), planned.NodeToCallId.TryGetValue(edge.TargetId) with
            | (true, srcId), (true, tgtId) ->
                let arrow = ArrowBetweenCalls(parentId, srcId, tgtId, mapArrowType edge.Label)
                operations.Add(AddArrowCall arrow)
            | _ -> ()

    let restorePlannedConditions
        (operations: ResizeArray<ImportPlanOperation>)
        (planned: PlannedCallNodes)
        =
        let uniqueNameToCallId = buildUniqueCallNameMap planned
        for node, call in planned.AllNodes do
            restoreConditions
                (fun apiCall -> operations.Add(AddApiCall apiCall))
                call
                planned.NodeToCallId
                uniqueNameToCallId
                planned.CallsById
                node

    let flowNameOfFlow (store: DsStore) (flowId: Guid) =
        store.FlowsReadOnly.TryGetValue(flowId)
        |> function true, flow -> flow.Name | _ -> ""

    let flowNameOfWork (store: DsStore) (workId: Guid) =
        store.WorksReadOnly.TryGetValue(workId)
        |> function
            | true, work ->
                store.FlowsReadOnly.TryGetValue(work.ParentId)
                |> function true, flow -> flow.Name | _ -> ""
            | _ -> ""

    let linkCallsToDevicesIfNeeded
        (store: DsStore)
        (projectIdOpt: Guid option)
        flowName
        (createdCalls: (Call * string) seq)
        (operations: ResizeArray<ImportPlanOperation>)
        =
        let created = createdCalls |> Seq.toList
        match projectIdOpt, created with
        | Some projectId, _ :: _ ->
            ImportPlanDeviceOps.linkCallsToDevices store projectId flowName created operations
        | _ -> ()

    let linkCallsToDevicesByFlow
        (store: DsStore)
        (projectId: Guid)
        (createdCalls: seq<Call * string * string>)
        (operations: ResizeArray<ImportPlanOperation>)
        =
        createdCalls
        |> Seq.groupBy (fun (_, _, flowName) -> flowName)
        |> Seq.iter (fun (flowName, group) ->
            let calls = group |> Seq.map (fun (call, label, _) -> call, label) |> Seq.toList
            ImportPlanDeviceOps.linkCallsToDevices store projectId flowName calls operations)
