module Ds2.UI.Core.RemoveOps

open System
open Ds2.Core

let private requireEntity = CommandExecutor.requireEntity

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

/// 다중 선택 엔티티 일괄 삭제 command 목록 빌드.
/// selections: distinctBy snd 적용된 (entityType, id) 리스트.
/// 실행 순서: ArrowCall → cascadeCall → directCall → ArrowWork → Work → 기타
let buildRemoveEntitiesCmds (store: DsStore) (selections: (string * Guid) list) : EditorCommand list =
    let selIds = selections |> List.map snd |> Set.ofList

    // ── Work 배치 ────────────────────────────────────────────────────────
    let selectedWorks =
        selections |> List.choose (fun (et, id) ->
            match EntityKind.tryOfString et with
            | ValueSome EntityKind.Work -> DsQuery.getWork id store
            | _ -> None)

    let cascadeCalls    = selectedWorks |> List.collect (fun w -> DsQuery.callsOf w.Id store)
    let workIds         = selectedWorks |> List.map (fun w -> w.Id) |> Set.ofList
    let cascadeIds      = cascadeCalls  |> List.map (fun c -> c.Id) |> Set.ofList
    let batchArrowWorks = if workIds.IsEmpty then [] else CascadeHelpers.arrowWorksFor store workIds

    // ── Call 배치 (부모 Work가 선택 집합에 없는 Call만) ──────────────────
    let selectedCalls =
        selections |> List.choose (fun (et, id) ->
            match EntityKind.tryOfString et with
            | ValueSome EntityKind.Call ->
                match DsQuery.getCall id store with
                | Some call when not (selIds.Contains call.ParentId) -> Some call
                | _ -> None
            | _ -> None)

    let directCallIds   = selectedCalls |> List.map (fun c -> c.Id) |> Set.ofList
    let allCallIds      = Set.union cascadeIds directCallIds
    let batchArrowCalls = if allCallIds.IsEmpty then [] else CascadeHelpers.arrowCallsFor store allCallIds

    // ── 나머지 엔티티 (Flow/System/Project/HW) ───────────────────────────
    let otherCmds =
        selections |> List.choose (fun (et, id) ->
            match EntityKind.tryOfString et with
            | ValueSome EntityKind.Work | ValueSome EntityKind.Call -> None
            | ValueSome EntityKind.Flow      -> DsQuery.getFlow id store      |> Option.map (fun _ -> buildRemoveFlowCmd store id)
            | ValueSome EntityKind.System    -> DsQuery.getSystem id store    |> Option.map (fun _ -> buildRemoveSystemCmd store id)
            | ValueSome EntityKind.Project   -> DsQuery.getProject id store   |> Option.map (fun _ -> buildRemoveProjectCmd store id)
            | ValueSome EntityKind.ApiDef    -> DsQuery.getApiDef id store    |> Option.map (fun a -> RemoveApiDef(DeepCopyHelper.backupEntityAs a))
            | ValueSome EntityKind.Button    -> DsQuery.getButton id store    |> Option.map (fun b -> RemoveButton(DeepCopyHelper.backupEntityAs b))
            | ValueSome EntityKind.Lamp      -> DsQuery.getLamp id store      |> Option.map (fun l -> RemoveLamp(DeepCopyHelper.backupEntityAs l))
            | ValueSome EntityKind.Condition -> DsQuery.getCondition id store |> Option.map (fun c -> RemoveHwCondition(DeepCopyHelper.backupEntityAs c))
            | ValueSome EntityKind.Action    -> DsQuery.getAction id store    |> Option.map (fun a -> RemoveHwAction(DeepCopyHelper.backupEntityAs a))
            | ValueNone -> None)

    [
        yield! batchArrowCalls |> List.map (fun a -> RemoveArrowCall(DeepCopyHelper.backupEntityAs a))
        yield! cascadeCalls    |> List.map (fun c -> RemoveCall(DeepCopyHelper.backupEntityAs c))
        yield! selectedCalls   |> List.map (fun c -> RemoveCall(DeepCopyHelper.backupEntityAs c))
        yield! batchArrowWorks |> List.map (fun a -> RemoveArrowWork(DeepCopyHelper.backupEntityAs a))
        yield! selectedWorks   |> List.map (fun w -> RemoveWork(DeepCopyHelper.backupEntityAs w))
        yield! otherCmds
    ]

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
