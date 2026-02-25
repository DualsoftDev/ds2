module Ds2.UI.Core.RemoveOps

open System
open Ds2.Core

let private requireEntity (entityType: string) (entityId: Guid) (entityOpt: 'T option) : 'T =
    entityOpt
    |> Option.defaultWith (fun () ->
        invalidOp $"'{entityType}' entity was not found while building remove command. id={entityId}")

let buildRemoveProjectCmd (store: DsStore) (projectId: Guid) : EditorCommand =
    let project = DsQuery.getProject projectId store |> requireEntity "Project" projectId
    let systems = DsQuery.projectSystemsOf project.Id store
    let flows = systems |> List.collect (fun s -> DsQuery.flowsOf s.Id store)
    let works = flows |> List.collect (fun f -> DsQuery.worksOf f.Id store)
    let calls = works |> List.collect (fun w -> DsQuery.callsOf w.Id store)
    let arrowWorks, arrowCalls = CascadeHelpers.collectDescendantArrows store works calls

    let backup = DeepCopyHelper.backupEntityAs project
    backup.ActiveSystemIds.Clear()
    backup.PassiveSystemIds.Clear()

    Composite("Delete Project", [
        yield! CascadeHelpers.coreRemoveCommands arrowCalls calls arrowWorks works
        yield! CascadeHelpers.flowRemoveCommands flows
        yield! CascadeHelpers.hwRemoveCommands store (systems |> List.map (fun s -> s.Id))
        yield! systems |> List.map (fun s ->
            let isActive = project.ActiveSystemIds.Contains(s.Id)
            RemoveSystem(DeepCopyHelper.backupEntityAs s, project.Id, isActive))
        RemoveProject backup
    ])

let buildRemoveSystemCmd (store: DsStore) (systemId: Guid) : EditorCommand =
    let system = DsQuery.getSystem systemId store |> requireEntity "System" systemId
    let project =
        DsQuery.allProjects store
        |> List.tryFind (fun p ->
            p.ActiveSystemIds.Contains(systemId) || p.PassiveSystemIds.Contains(systemId))
        |> Option.defaultWith (fun () ->
            invalidOp $"Cannot remove system {systemId}: owner project was not found.")
    let isActive = project.ActiveSystemIds.Contains(systemId)

    let flows = DsQuery.flowsOf systemId store
    let works = flows |> List.collect (fun f -> DsQuery.worksOf f.Id store)
    let calls = works |> List.collect (fun w -> DsQuery.callsOf w.Id store)
    let arrowWorks, arrowCalls = CascadeHelpers.collectDescendantArrows store works calls

    Composite("Delete System", [
        yield! CascadeHelpers.coreRemoveCommands arrowCalls calls arrowWorks works
        yield! CascadeHelpers.flowRemoveCommands flows
        yield! CascadeHelpers.hwRemoveCommands store [ systemId ]
        RemoveSystem(DeepCopyHelper.backupEntityAs system, project.Id, isActive)
    ])

let buildRemoveFlowCmd (store: DsStore) (flowId: Guid) : EditorCommand =
    let flow = DsQuery.getFlow flowId store |> requireEntity "Flow" flowId
    let works = DsQuery.worksOf flowId store
    let calls = works |> List.collect (fun w -> DsQuery.callsOf w.Id store)
    let arrowWorks, arrowCalls = CascadeHelpers.collectDescendantArrows store works calls

    if works.IsEmpty && arrowWorks.IsEmpty then
        RemoveFlow(DeepCopyHelper.backupEntityAs flow)
    else
        Composite("Delete Flow", [
            yield! CascadeHelpers.coreRemoveCommands arrowCalls calls arrowWorks works
            RemoveFlow(DeepCopyHelper.backupEntityAs flow)
        ])

let buildRemoveWorkCmd (store: DsStore) (workId: Guid) : EditorCommand =
    let work = DsQuery.getWork workId store |> requireEntity "Work" workId
    let calls = DsQuery.callsOf workId store
    let workIds = Set.singleton workId
    let callIds = calls |> List.map (fun c -> c.Id) |> Set.ofList
    let arrowWorks = CascadeHelpers.arrowWorksFor store workIds
    let arrowCalls = CascadeHelpers.arrowCallsFor store callIds

    if calls.IsEmpty && arrowWorks.IsEmpty then
        RemoveWork(DeepCopyHelper.backupEntityAs work)
    else
        Composite("Delete Work", [
            yield! CascadeHelpers.coreRemoveCommands arrowCalls calls arrowWorks []
            RemoveWork(DeepCopyHelper.backupEntityAs work)
        ])

let buildRemoveCallCmd (store: DsStore) (callId: Guid) : EditorCommand =
    let call = DsQuery.getCall callId store |> requireEntity "Call" callId
    let arrowCalls = CascadeHelpers.arrowCallsFor store (Set.singleton callId)

    if arrowCalls.IsEmpty then
        RemoveCall(DeepCopyHelper.backupEntityAs call)
    else
        Composite("Delete Call", [
            yield! arrowCalls |> List.map (fun a -> RemoveArrowCall(DeepCopyHelper.backupEntityAs a))
            RemoveCall(DeepCopyHelper.backupEntityAs call)
        ])

let buildMoveEntitiesCmds (store: DsStore) (requests: seq<MoveEntityRequest>) : EditorCommand list =
    requests
    |> Seq.distinctBy (fun r -> r.EntityType, r.Id)
    |> Seq.choose (fun r ->
        match EntityKind.tryOfString r.EntityType with
        | ValueSome Work ->
            match DsQuery.getWork r.Id store with
            | Some work when work.Position <> r.NewPos -> Some(MoveWork(r.Id, work.Position, r.NewPos))
            | _ -> None
        | ValueSome Call ->
            match DsQuery.getCall r.Id store with
            | Some call when call.Position <> r.NewPos -> Some(MoveCall(r.Id, call.Position, r.NewPos))
            | _ -> None
        | _ -> None)
    |> Seq.toList
