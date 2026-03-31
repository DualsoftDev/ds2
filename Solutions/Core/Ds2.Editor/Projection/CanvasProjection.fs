module Ds2.Editor.EditorCanvasProjection

open System
open System.Runtime.CompilerServices
open Ds2.Core
open Ds2.Store
open Ds2.Store.DsQuery

let private nodeFromPosition (id: Guid) (entityKind: EntityKind) (name: string) (parentId: Guid) (pos: Xywh option) (conditionTypes: CallConditionType list) (isGhost: bool) (isReference: bool) (referenceOfId: Guid option) =
    let defaultPos = Xywh(UiDefaults.DefaultNodeX, UiDefaults.DefaultNodeY, UiDefaults.DefaultNodeWidth, UiDefaults.DefaultNodeHeight)
    let p = pos |> Option.defaultValue defaultPos
    { Id = id
      EntityKind = entityKind
      Name = name
      ParentId = parentId
      X = float p.X
      Y = float p.Y
      Width = float p.W
      Height = float p.H
      ConditionTypes = conditionTypes
      IsGhost = isGhost
      IsReference = isReference
      ReferenceOfId = referenceOfId }

let private toArrowInfo (a: DsArrow) : CanvasArrowInfo =
    { Id = a.Id; SourceId = a.SourceId; TargetId = a.TargetId; ArrowType = a.ArrowType }

let canvasContentForSystemWorks (store: DsStore) (systemId: Guid) : CanvasContent =
    let flowIds =
        Queries.flowsOf systemId store
        |> List.map (fun f -> f.Id)

    let nodes =
        flowIds
        |> List.collect (fun flowId ->
            Queries.worksOf flowId store
            |> List.map (fun w -> nodeFromPosition w.Id EntityKind.Work w.Name w.ParentId w.Position [] false w.ReferenceOf.IsSome w.ReferenceOf))

    let arrows =
        Queries.arrowWorksOf systemId store
        |> List.map toArrowInfo

    { Nodes = nodes; Arrows = arrows }

let canvasContentForFlowWorks (store: DsStore) (flowId: Guid) : CanvasContent =
    let works = Queries.worksOf flowId store
    let workIds = works |> List.map (fun w -> w.Id) |> Set.ofList

    let localNodes =
        works
        |> List.map (fun w -> nodeFromPosition w.Id EntityKind.Work w.Name w.ParentId w.Position [] false w.ReferenceOf.IsSome w.ReferenceOf)

    // 타 Flow의 Work와 연결된 화살표 + 고스트 Work 수집
    let allArrows, ghostNodes =
        match Queries.getFlow flowId store with
        | None -> [], []
        | Some flow ->
            let systemArrows = Queries.arrowWorksOf flow.ParentId store
            // 이 Flow의 Work가 한쪽이라도 포함된 화살표
            let relevantArrows =
                systemArrows
                |> List.filter (fun a -> workIds.Contains a.SourceId || workIds.Contains a.TargetId)

            // 외부 Work ID 수집
            let externalIds =
                relevantArrows
                |> List.collect (fun a -> [a.SourceId; a.TargetId])
                |> List.filter (fun id -> not (workIds.Contains id))
                |> List.distinct

            let ghosts =
                externalIds
                |> List.choose (fun id -> Queries.getWork id store)
                |> List.map (fun w -> nodeFromPosition w.Id EntityKind.Work w.Name w.ParentId w.Position [] true w.ReferenceOf.IsSome w.ReferenceOf)

            let arrows = relevantArrows |> List.map toArrowInfo
            arrows, ghosts

    { Nodes = localNodes @ ghostNodes; Arrows = allArrows }

let canvasContentForWorkCalls (store: DsStore) (workId: Guid) : CanvasContent =
    match Queries.getWork workId store with
    | None -> { Nodes = []; Arrows = [] }
    | Some _ ->
        let calls = Queries.callsOf workId store
        let callSet = calls |> List.map (fun c -> c.Id) |> Set.ofList

        let nodes =
            calls
            |> List.map (fun c -> nodeFromPosition c.Id EntityKind.Call c.Name c.ParentId c.Position (CallConditionQueries.conditionTypes c) false false None)

        let arrows =
            Queries.arrowCallsOf workId store
            |> List.filter (fun a -> callSet.Contains a.SourceId && callSet.Contains a.TargetId)
            |> List.map toArrowInfo

        { Nodes = nodes; Arrows = arrows }

[<CompiledName("CanvasContentForTab")>]
let canvasContentForTab (store: DsStore) (tabKind: TabKind) (rootId: Guid) : CanvasContent =
    match tabKind with
    | TabKind.System -> canvasContentForSystemWorks store rootId
    | TabKind.Flow -> canvasContentForFlowWorks store rootId
    | TabKind.Work -> canvasContentForWorkCalls store rootId
    | _ -> { Nodes = []; Arrows = [] }
