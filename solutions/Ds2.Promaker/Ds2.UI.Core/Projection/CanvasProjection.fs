module Ds2.UI.Core.CanvasProjection

open System
open Ds2.Core

let private containsBoth (idSet: Set<Guid>) (sourceId: Guid) (targetId: Guid) =
    idSet.Contains sourceId && idSet.Contains targetId

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
    let works = DsQuery.worksOf flowId store
    let callsByWork =
        works
        |> List.map (fun work -> work, DsQuery.callsOf work.Id store)

    let nodes =
        callsByWork
        |> List.collect (fun (work, calls) ->
            let calls =
                calls
                |> List.map (fun c -> nodeFromPosition c.Id EntityTypeNames.Call c.Name c.ParentId c.Position)

            nodeFromPosition work.Id EntityTypeNames.Work work.Name work.ParentId work.Position :: calls)

    let workIds = works |> List.map (fun w -> w.Id) |> Set.ofList
    let callIds =
        callsByWork
        |> List.collect (fun (_, calls) -> calls)
        |> List.map (fun call -> call.Id)
        |> Set.ofList

    let arrows = [
        yield!
            DsQuery.arrowWorksOf flowId store
            |> List.filter (fun arrow -> containsBoth workIds arrow.SourceId arrow.TargetId)
            |> List.map (fun arrow ->
                { Id = arrow.Id
                  SourceId = arrow.SourceId
                  TargetId = arrow.TargetId
                  ArrowType = arrow.ArrowType })

        yield!
            DsQuery.arrowCallsOf flowId store
            |> List.filter (fun arrow -> containsBoth callIds arrow.SourceId arrow.TargetId)
            |> List.map (fun arrow ->
                { Id = arrow.Id
                  SourceId = arrow.SourceId
                  TargetId = arrow.TargetId
                  ArrowType = arrow.ArrowType })
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
    let works = DsQuery.worksOf flowId store
    let workIds = works |> List.map (fun w -> w.Id) |> Set.ofList

    let nodes =
        works
        |> List.map (fun w -> nodeFromPosition w.Id EntityTypeNames.Work w.Name w.ParentId w.Position)

    let arrows =
        DsQuery.arrowWorksOf flowId store
        |> List.filter (fun arrow -> containsBoth workIds arrow.SourceId arrow.TargetId)
        |> List.map (fun arrow ->
            { Id = arrow.Id
              SourceId = arrow.SourceId
              TargetId = arrow.TargetId
              ArrowType = arrow.ArrowType })

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
            DsQuery.arrowCallsOf work.ParentId store
            |> List.filter (fun a ->
                callSet.Contains a.SourceId
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
