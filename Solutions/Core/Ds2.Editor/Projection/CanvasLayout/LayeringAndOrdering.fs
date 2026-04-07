namespace Ds2.Editor

open System
open System.Collections.Generic
open Ds2.Core
open Ds2.Core.Store

module internal CanvasLayoutLayeringAndOrdering =

    let marginLeft = 80
    let marginTop = 80
    let layerSpacingX = 200
    let nodeSpacingY = 60
    let connectedNodeSpacingY = 34
    let satelliteSpacingY = 52
    let canvasWidth = 3000
    let canvasHeight = 2000
    let rowGapY = 80
    let minLayerSpacingX = 80
    let satelliteLayerDistanceThreshold = 3
    let satelliteMaxDegree = 2
    let collisionMargin = 12
    let rowUnderfillPenalty = 6
    let rowBoundaryPenalty = 120

    let assignLayers (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) : Map<Guid, int> =
        let nodeIds = nodes |> List.map (fun n -> n.Id) |> Set.ofList
        let edges =
            arrows
            |> List.filter (fun a -> nodeIds.Contains a.SourceId && nodeIds.Contains a.TargetId)

        let successors = Dictionary<Guid, List<Guid>>()
        let inDegree = Dictionary<Guid, int>()
        for nid in nodeIds do
            successors.[nid] <- List<Guid>()
            inDegree.[nid] <- 0
        for e in edges do
            successors.[e.SourceId].Add(e.TargetId)
            inDegree.[e.TargetId] <- inDegree.[e.TargetId] + 1

        let layer = Dictionary<Guid, int>()
        let queue = Queue<Guid>()
        for nid in nodeIds do
            if inDegree.[nid] = 0 then
                queue.Enqueue nid
                layer.[nid] <- 0

        while queue.Count > 0 do
            let cur = queue.Dequeue()
            let curLayer = layer.[cur]
            for succ in successors.[cur] do
                let newLayer = curLayer + 1
                if layer.ContainsKey succ then
                    if newLayer > layer.[succ] then
                        layer.[succ] <- newLayer
                else
                    layer.[succ] <- newLayer
                inDegree.[succ] <- inDegree.[succ] - 1
                if inDegree.[succ] = 0 then
                    queue.Enqueue succ

        let maxLayer =
            if layer.Count = 0 then 0
            else layer.Values |> Seq.max
        for nid in nodeIds do
            if not (layer.ContainsKey nid) then
                layer.[nid] <- maxLayer + 1

        layer |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

    let orderWithinLayers
        (layerMap: Map<Guid, int>)
        (nodes: CanvasNodeInfo list)
        (arrows: CanvasArrowInfo list)
        : (int * CanvasNodeInfo list) list =

        let nodeIds = nodes |> List.map (fun n -> n.Id) |> Set.ofList

        let grouped =
            nodes
            |> List.groupBy (fun n -> layerMap.[n.Id])
            |> List.sortBy fst
            |> List.map (fun (l, ns) -> l, ns |> List.sortBy (fun n -> n.Y))

        let predecessors = Dictionary<Guid, List<Guid>>()
        let successorsMap = Dictionary<Guid, List<Guid>>()
        for nid in nodeIds do
            predecessors.[nid] <- List<Guid>()
            successorsMap.[nid] <- List<Guid>()
        for a in arrows do
            if nodeIds.Contains a.SourceId && nodeIds.Contains a.TargetId then
                successorsMap.[a.SourceId].Add(a.TargetId)
                predecessors.[a.TargetId].Add(a.SourceId)

        let posY = Dictionary<Guid, float>()
        for n in nodes do
            posY.[n.Id] <- n.Y

        let neighborSet (neighbors: List<Guid>) = neighbors |> Seq.toList |> Set.ofList

        let connectionAffinity (left: CanvasNodeInfo) (right: CanvasNodeInfo) =
            let leftPred = neighborSet predecessors.[left.Id]
            let rightPred = neighborSet predecessors.[right.Id]
            let leftSucc = neighborSet successorsMap.[left.Id]
            let rightSucc = neighborSet successorsMap.[right.Id]
            let sharedPred = Set.intersect leftPred rightPred |> Set.count
            let sharedSucc = Set.intersect leftSucc rightSucc |> Set.count
            let direct =
                (if leftSucc.Contains right.Id then 2 else 0) +
                (if rightSucc.Contains left.Id then 2 else 0)
            sharedPred + sharedSucc + direct

        let reorderByAffinity (layerNodes: CanvasNodeInfo list) =
            match layerNodes |> List.sortBy (fun n -> posY.[n.Id]) with
            | [] -> []
            | first :: tail ->
                let result = ResizeArray<CanvasNodeInfo>()
                let mutable current = first
                let mutable remaining = tail
                result.Add(current)

                while not remaining.IsEmpty do
                    let currentY = posY.[current.Id]
                    let next =
                        remaining
                        |> List.minBy (fun candidate ->
                            let affinity = connectionAffinity current candidate
                            let distancePenalty = abs (posY.[candidate.Id] - currentY)
                            distancePenalty - float affinity * 0.75)
                    result.Add(next)
                    remaining <- remaining |> List.filter (fun n -> n.Id <> next.Id)
                    current <- next

                result |> Seq.toList

        let sortByBarycenter (layerNodes: CanvasNodeInfo list) (getNeighbors: Guid -> List<Guid>) =
            layerNodes
            |> List.map (fun n ->
                let neighbors = getNeighbors n.Id
                let bc =
                    if neighbors.Count = 0 then posY.[n.Id]
                    else
                        neighbors
                        |> Seq.choose (fun nid -> if posY.ContainsKey nid then Some posY.[nid] else None)
                        |> Seq.toList
                        |> function
                            | [] -> posY.[n.Id]
                            | ys -> List.average ys
                bc, n)
            |> List.sortBy fst
            |> List.map snd
            |> reorderByAffinity

        let mutable layers = grouped |> List.map snd |> List.toArray
        let layerIndices = grouped |> List.map fst |> List.toArray

        for _pass in 0..1 do
            for i in 1 .. layers.Length - 1 do
                layers.[i] <- sortByBarycenter layers.[i] (fun nid -> predecessors.[nid])
                layers.[i] |> List.iteri (fun idx n -> posY.[n.Id] <- float idx)

            for i in layers.Length - 2 .. -1 .. 0 do
                layers.[i] <- sortByBarycenter layers.[i] (fun nid -> successorsMap.[nid])
                layers.[i] |> List.iteri (fun idx n -> posY.[n.Id] <- float idx)

        Array.zip layerIndices layers
        |> Array.toList
