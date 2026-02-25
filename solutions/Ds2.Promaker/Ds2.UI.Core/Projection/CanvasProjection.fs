module Ds2.UI.Core.CanvasProjection

open System
open Ds2.Core

let private nodeFromPosition (id: Guid) (entityType: string) (name: string) (parentId: Guid) (pos: Xywh option) =
    match pos with
    | Some p ->
        { Id = id
          EntityType = entityType
          Name = name
          ParentId = parentId
          X = float p.X
          Y = float p.Y
          Width = float p.W
          Height = float p.H }
    | None ->
        { Id = id
          EntityType = entityType
          Name = name
          ParentId = parentId
          X = UiDefaults.DefaultNodeXf
          Y = UiDefaults.DefaultNodeYf
          Width = UiDefaults.DefaultNodeWidthf
          Height = UiDefaults.DefaultNodeHeightf }

let canvasContentForFlow (store: DsStore) (flowId: Guid) : CanvasContent =
    let nodes =
        DsQuery.worksOf flowId store
        |> List.collect (fun w ->
            let calls =
                DsQuery.callsOf w.Id store
                |> List.map (fun c -> nodeFromPosition c.Id EntityTypeNames.Call c.Name c.ParentId c.Position)

            nodeFromPosition w.Id EntityTypeNames.Work w.Name w.ParentId w.Position :: calls)

    let arrows = [
        yield! DsQuery.allArrowWorks store
               |> List.filter (fun a -> a.ParentId = flowId)
               |> List.map (fun a ->
                   { Id = a.Id
                     SourceId = a.SourceId
                     TargetId = a.TargetId
                     ArrowType = a.ArrowType })

        yield! DsQuery.allArrowCalls store
               |> List.filter (fun a -> a.ParentId = flowId)
               |> List.map (fun a ->
                   { Id = a.Id
                     SourceId = a.SourceId
                     TargetId = a.TargetId
                     ArrowType = a.ArrowType })
    ]

    { Nodes = nodes; Arrows = arrows }

let canvasContentForSystemWorks (store: DsStore) (systemId: Guid) : CanvasContent =
    let flowIds =
        DsQuery.flowsOf systemId store
        |> List.map (fun f -> f.Id)

    let flowSet = Set.ofList flowIds

    let nodes =
        flowIds
        |> List.collect (fun flowId ->
            DsQuery.worksOf flowId store
            |> List.map (fun w -> nodeFromPosition w.Id EntityTypeNames.Work w.Name w.ParentId w.Position))

    let arrows =
        DsQuery.allArrowWorks store
        |> List.filter (fun a -> flowSet.Contains a.ParentId)
        |> List.map (fun a ->
            { Id = a.Id
              SourceId = a.SourceId
              TargetId = a.TargetId
              ArrowType = a.ArrowType })

    { Nodes = nodes; Arrows = arrows }

let canvasContentForFlowWorks (store: DsStore) (flowId: Guid) : CanvasContent =
    let nodes =
        DsQuery.worksOf flowId store
        |> List.map (fun w -> nodeFromPosition w.Id EntityTypeNames.Work w.Name w.ParentId w.Position)

    let arrows =
        DsQuery.allArrowWorks store
        |> List.filter (fun a -> a.ParentId = flowId)
        |> List.map (fun a ->
            { Id = a.Id
              SourceId = a.SourceId
              TargetId = a.TargetId
              ArrowType = a.ArrowType })

    { Nodes = nodes; Arrows = arrows }

let canvasContentForWorkCalls (store: DsStore) (workId: Guid) : CanvasContent =
    match DsQuery.getWork workId store with
    | None -> { Nodes = []; Arrows = [] }
    | Some work ->
        let calls = DsQuery.callsOf workId store
        let callSet = calls |> List.map (fun c -> c.Id) |> Set.ofList

        let nodes =
            calls
            |> List.map (fun c -> nodeFromPosition c.Id EntityTypeNames.Call c.Name c.ParentId c.Position)

        let arrows =
            DsQuery.allArrowCalls store
            |> List.filter (fun a ->
                a.ParentId = work.ParentId
                && callSet.Contains a.SourceId
                && callSet.Contains a.TargetId)
            |> List.map (fun a ->
                { Id = a.Id
                  SourceId = a.SourceId
                  TargetId = a.TargetId
                  ArrowType = a.ArrowType })

        { Nodes = nodes; Arrows = arrows }

let canvasContentForTab (store: DsStore) (tabKind: TabKind) (rootId: Guid) : CanvasContent =
    match tabKind with
    | TabKind.System -> canvasContentForSystemWorks store rootId
    | TabKind.Flow -> canvasContentForFlowWorks store rootId
    | TabKind.Work -> canvasContentForWorkCalls store rootId
    | _ -> { Nodes = []; Arrows = [] }
