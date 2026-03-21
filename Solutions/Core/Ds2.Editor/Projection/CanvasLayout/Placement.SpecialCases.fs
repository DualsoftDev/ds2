namespace Ds2.Editor

open System
open System.Collections.Generic
open Ds2.Core

module internal CanvasLayoutPlacementSpecialCases =

    open CanvasLayoutLayeringAndOrdering

    let private rectsOverlap (left: Xywh) (right: Xywh) =
        let lx1, ly1 = left.X - collisionMargin, left.Y - collisionMargin
        let lx2, ly2 = left.X + left.W + collisionMargin, left.Y + left.H + collisionMargin
        let rx1, ry1 = right.X - collisionMargin, right.Y - collisionMargin
        let rx2, ry2 = right.X + right.W + collisionMargin, right.Y + right.H + collisionMargin
        not (lx2 <= rx1 || rx2 <= lx1 || ly2 <= ry1 || ry2 <= ly1)

    let buildDirectNeighbors (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) =
        let nodeIds = nodes |> List.map (fun n -> n.Id) |> Set.ofList
        let neighbors = Dictionary<Guid, HashSet<Guid>>()
        for node in nodes do
            neighbors.[node.Id] <- HashSet<Guid>()
        for arrow in arrows do
            if nodeIds.Contains arrow.SourceId && nodeIds.Contains arrow.TargetId then
                neighbors.[arrow.SourceId].Add(arrow.TargetId) |> ignore
                neighbors.[arrow.TargetId].Add(arrow.SourceId) |> ignore
        neighbors

    let findCycleComponents (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) =
        let nodeIds = nodes |> List.map (fun n -> n.Id) |> Set.ofList
        let successors = Dictionary<Guid, ResizeArray<Guid>>()
        for node in nodes do
            successors.[node.Id] <- ResizeArray<Guid>()
        for arrow in arrows do
            if nodeIds.Contains arrow.SourceId && nodeIds.Contains arrow.TargetId then
                successors.[arrow.SourceId].Add(arrow.TargetId)

        let indexById = Dictionary<Guid, int>()
        let lowLinkById = Dictionary<Guid, int>()
        let stack = Stack<Guid>()
        let onStack = HashSet<Guid>()
        let mutable nextIndex = 0
        let components = ResizeArray<Guid list>()

        let rec strongConnect nodeId =
            indexById.[nodeId] <- nextIndex
            lowLinkById.[nodeId] <- nextIndex
            nextIndex <- nextIndex + 1
            stack.Push(nodeId)
            onStack.Add(nodeId) |> ignore

            for succId in successors.[nodeId] do
                if not (indexById.ContainsKey succId) then
                    strongConnect succId
                    lowLinkById.[nodeId] <- min lowLinkById.[nodeId] lowLinkById.[succId]
                elif onStack.Contains succId then
                    lowLinkById.[nodeId] <- min lowLinkById.[nodeId] indexById.[succId]

            if lowLinkById.[nodeId] = indexById.[nodeId] then
                let mutable members = []
                let mutable keepGoing = true
                while keepGoing && stack.Count > 0 do
                    let poppedId = stack.Pop()
                    onStack.Remove(poppedId) |> ignore
                    members <- poppedId :: members
                    keepGoing <- poppedId <> nodeId

                if members.Length > 1 then
                    components.Add(members)

        for node in nodes do
            if not (indexById.ContainsKey node.Id) then
                strongConnect node.Id

        components |> Seq.toList

    let applySatellitePlacement
        (nodesById: Map<Guid, CanvasNodeInfo>)
        (layerMap: Map<Guid, int>)
        (neighbors: Dictionary<Guid, HashSet<Guid>>)
        (basePositions: Map<Guid, Xywh>) =

        let tryFindSatelliteAnchor nodeId =
            let nodeLayer = layerMap.[nodeId]
            let adjacent = neighbors.[nodeId] |> Seq.toList
            if adjacent.Length = 0 || adjacent.Length > satelliteMaxDegree then
                None
            else
                adjacent
                |> List.choose (fun neighborId ->
                    let layerDistance = abs (layerMap.[neighborId] - nodeLayer)
                    if layerDistance < satelliteLayerDistanceThreshold then
                        None
                    else
                        Some(layerDistance, neighborId))
                |> List.sortByDescending fst
                |> List.tryHead
                |> Option.map snd

        let candidateIds =
            nodesById
            |> Map.toList
            |> List.choose (fun (nodeId, _) ->
                tryFindSatelliteAnchor nodeId |> Option.map (fun anchorId -> nodeId, anchorId))
            |> List.sortByDescending (fun (nodeId, anchorId) -> abs (layerMap.[anchorId] - layerMap.[nodeId]))

        let mutable positions = basePositions

        for nodeId, anchorId in candidateIds do
            let node = nodesById.[nodeId]
            let anchorPos = positions.[anchorId]
            let currentPos = positions.[nodeId]
            let nodeHeight = int node.Height
            let nodeWidth = int node.Width
            let stepY = nodeHeight + satelliteSpacingY

            let candidateSlots =
                [
                    1, Xywh(anchorPos.X, anchorPos.Y - stepY, nodeWidth, nodeHeight)
                    1, Xywh(anchorPos.X, anchorPos.Y + stepY, nodeWidth, nodeHeight)
                    2, Xywh(anchorPos.X, anchorPos.Y - (stepY * 2), nodeWidth, nodeHeight)
                    2, Xywh(anchorPos.X, anchorPos.Y + (stepY * 2), nodeWidth, nodeHeight)
                ]

            let occupied =
                positions
                |> Map.toList
                |> List.choose (fun (id, pos) -> if id = nodeId then None else Some pos)

            let maxOccupiedY =
                occupied
                |> List.map (fun p -> p.Y + p.H)
                |> function [] -> canvasHeight | ys -> List.max ys
            let yUpperBound = max (canvasHeight - marginTop) (maxOccupiedY + marginTop)

            let validSlots =
                candidateSlots
                |> List.filter (fun (_, slot) ->
                    slot.Y >= marginTop &&
                    slot.Y + slot.H <= yUpperBound &&
                    occupied |> List.forall (fun other -> not (rectsOverlap slot other)))

            let chosenSlot =
                validSlots
                |> List.sortBy (fun (distanceRank, slot) ->
                    let yPenalty = abs (slot.Y - currentPos.Y)
                    let xPenalty = abs (slot.X - currentPos.X)
                    distanceRank, xPenalty + yPenalty)
                |> List.tryHead
                |> Option.map snd

            match chosenSlot with
            | Some slot -> positions <- positions.Add(nodeId, slot)
            | None -> ()

        positions

    let applyCyclePlacement
        (nodesById: Map<Guid, CanvasNodeInfo>)
        (arrows: CanvasArrowInfo list)
        (cycleComponents: Guid list list)
        (positions: Map<Guid, Xywh>) =

        let mutable nextPositions = positions

        let buildOrderedCycleGroup (cycleGroup: Guid list) =
            let groupSet = cycleGroup |> Set.ofList
            let successors = Dictionary<Guid, Guid list>()
            let predecessors = Dictionary<Guid, Guid list>()

            for nodeId in cycleGroup do
                successors.[nodeId] <- []
                predecessors.[nodeId] <- []

            for arrow in arrows do
                if groupSet.Contains arrow.SourceId && groupSet.Contains arrow.TargetId then
                    successors.[arrow.SourceId] <- successors.[arrow.SourceId] @ [ arrow.TargetId ]
                    predecessors.[arrow.TargetId] <- predecessors.[arrow.TargetId] @ [ arrow.SourceId ]

            let startId =
                cycleGroup
                |> List.minBy (fun nodeId ->
                    let pos = nextPositions.[nodeId]
                    pos.Y, pos.X)

            let rec walk currentId visited acc =
                let visited' = visited |> Set.add currentId
                let acc' = currentId :: acc

                let nextId =
                    successors.[currentId]
                    |> List.tryFind (fun succId -> not (visited'.Contains succId))
                    |> Option.orElseWith (fun () ->
                        predecessors.[currentId]
                        |> List.tryFind (fun predId -> not (visited'.Contains predId)))
                    |> Option.orElseWith (fun () ->
                        cycleGroup
                        |> List.filter (fun nodeId -> not (visited'.Contains nodeId))
                        |> List.sortBy (fun nodeId ->
                            let pos = nextPositions.[nodeId]
                            pos.Y, pos.X)
                        |> List.tryHead)

                match nextId with
                | Some unresolvedId -> walk unresolvedId visited' acc'
                | None -> List.rev acc'

            walk startId Set.empty []

        for cycleGroup in cycleComponents do
            let orderedIds = buildOrderedCycleGroup cycleGroup
            let memberPositions = orderedIds |> List.map (fun nodeId -> nextPositions.[nodeId])
            let count = orderedIds.Length

            if count > 1 then
                let centerX =
                    memberPositions
                    |> List.averageBy (fun pos -> float pos.X + float pos.W / 2.0)
                let centerY =
                    memberPositions
                    |> List.averageBy (fun pos -> float pos.Y + float pos.H / 2.0)
                let maxWidth = memberPositions |> List.maxBy (fun pos -> pos.W) |> fun pos -> pos.W
                let maxHeight = memberPositions |> List.maxBy (fun pos -> pos.H) |> fun pos -> pos.H
                let radiusX = float (max 120 (maxWidth + 36 + max 0 (count - 3) * 20))
                let radiusY = float (max 96 (maxHeight + 28 + max 0 (count - 3) * 18))
                let angleStep = (Math.PI * 2.0) / float count

                let arranged =
                    orderedIds
                    |> List.mapi (fun idx nodeId ->
                        let node = nodesById.[nodeId]
                        let angle = -Math.PI / 2.0 + angleStep * float idx
                        let cx = centerX + radiusX * Math.Cos(angle)
                        let cy = centerY + radiusY * Math.Sin(angle)
                        let x = int (Math.Round(cx - node.Width / 2.0))
                        let y = int (Math.Round(cy - node.Height / 2.0))
                        nodeId, Xywh(max marginLeft x, max marginTop y, int node.Width, int node.Height))

                for nodeId, pos in arranged do
                    nextPositions <- nextPositions.Add(nodeId, pos)

        nextPositions
