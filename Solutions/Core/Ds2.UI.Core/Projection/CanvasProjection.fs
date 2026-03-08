module Ds2.UI.Core.CanvasProjection

open System
open Ds2.Core

let private defaultPos = Xywh(UiDefaults.DefaultNodeX, UiDefaults.DefaultNodeY, UiDefaults.DefaultNodeWidth, UiDefaults.DefaultNodeHeight)

let private nodeFromPosition (id: Guid) (entityKind: EntityKind) (name: string) (parentId: Guid) (pos: Xywh option) =
    let p = pos |> Option.defaultValue defaultPos
    { Id = id
      EntityKind = entityKind
      Name = name
      ParentId = parentId
      X = float p.X
      Y = float p.Y
      Width = float p.W
      Height = float p.H }

let private toArrowInfo (a: DsArrow) : CanvasArrowInfo =
    { Id = a.Id; SourceId = a.SourceId; TargetId = a.TargetId; ArrowType = a.ArrowType }

let canvasContentForSystemWorks (store: DsStore) (systemId: Guid) : CanvasContent =
    let flowIds =
        DsQuery.flowsOf systemId store
        |> List.map (fun f -> f.Id)

    let flowSet = Set.ofList flowIds

    let nodes =
        flowIds
        |> List.collect (fun flowId ->
            DsQuery.worksOf flowId store
            |> List.map (fun w -> nodeFromPosition w.Id EntityKind.Work w.Name w.ParentId w.Position))

    let arrows =
        DsQuery.allArrowWorks store
        |> List.filter (fun a -> flowSet.Contains a.ParentId)
        |> List.map toArrowInfo

    { Nodes = nodes; Arrows = arrows }

let canvasContentForFlowWorks (store: DsStore) (flowId: Guid) : CanvasContent =
    let works = DsQuery.worksOf flowId store
    let workIds = works |> List.map (fun w -> w.Id) |> Set.ofList

    let nodes =
        works
        |> List.map (fun w -> nodeFromPosition w.Id EntityKind.Work w.Name w.ParentId w.Position)

    let arrows =
        DsQuery.arrowWorksOf flowId store
        |> List.filter (fun a -> workIds.Contains a.SourceId && workIds.Contains a.TargetId)
        |> List.map toArrowInfo

    { Nodes = nodes; Arrows = arrows }

let canvasContentForWorkCalls (store: DsStore) (workId: Guid) : CanvasContent =
    match DsQuery.getWork workId store with
    | None -> { Nodes = []; Arrows = [] }
    | Some work ->
        let calls = DsQuery.callsOf workId store
        let callSet = calls |> List.map (fun c -> c.Id) |> Set.ofList

        let nodes =
            calls
            |> List.map (fun c -> nodeFromPosition c.Id EntityKind.Call c.Name c.ParentId c.Position)

        let arrows =
            DsQuery.arrowCallsOf work.ParentId store
            |> List.filter (fun a -> callSet.Contains a.SourceId && callSet.Contains a.TargetId)
            |> List.map toArrowInfo

        { Nodes = nodes; Arrows = arrows }

let canvasContentForTab (store: DsStore) (tabKind: TabKind) (rootId: Guid) : CanvasContent =
    match tabKind with
    | TabKind.System -> canvasContentForSystemWorks store rootId
    | TabKind.Flow -> canvasContentForFlowWorks store rootId
    | TabKind.Work -> canvasContentForWorkCalls store rootId
    | _ -> { Nodes = []; Arrows = [] }
