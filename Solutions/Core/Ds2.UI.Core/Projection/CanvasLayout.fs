module Ds2.UI.Core.CanvasLayout

open System
open System.Collections.Generic
open Ds2.Core

let private marginLeft = 80
let private marginTop = 80
let private layerSpacingX = 200
let private nodeSpacingY = 60
let private connectedNodeSpacingY = 34
let private satelliteSpacingY = 52
let private canvasWidth = 3000
let private canvasHeight = 2000
let private rowGapY = 80
let private minLayerSpacingX = 80
let private satelliteLayerDistanceThreshold = 3
let private satelliteMaxDegree = 2
let private collisionMargin = 12
let private rowUnderfillPenalty = 6
let private rowBoundaryPenalty = 120

let private assignLayers (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) : Map<Guid, int> =
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

let private orderWithinLayers
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
    |> List.map (fun (l, ns) -> l, ns)

let private rectsOverlap (left: Xywh) (right: Xywh) =
    let lx1, ly1 = left.X - collisionMargin, left.Y - collisionMargin
    let lx2, ly2 = left.X + left.W + collisionMargin, left.Y + left.H + collisionMargin
    let rx1, ry1 = right.X - collisionMargin, right.Y - collisionMargin
    let rx2, ry2 = right.X + right.W + collisionMargin, right.Y + right.H + collisionMargin
    not (lx2 <= rx1 || rx2 <= lx1 || ly2 <= ry1 || ry2 <= ly1)

let private buildDirectNeighbors (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) =
    let nodeIds = nodes |> List.map (fun n -> n.Id) |> Set.ofList
    let neighbors = Dictionary<Guid, HashSet<Guid>>()
    for node in nodes do
        neighbors.[node.Id] <- HashSet<Guid>()
    for arrow in arrows do
        if nodeIds.Contains arrow.SourceId && nodeIds.Contains arrow.TargetId then
            neighbors.[arrow.SourceId].Add(arrow.TargetId) |> ignore
            neighbors.[arrow.TargetId].Add(arrow.SourceId) |> ignore
    neighbors

let private findCycleComponents (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) =
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

let private applySatellitePlacement
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

let private applyCyclePlacement
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

let private translatePositions (dx: int) (dy: int) (positions: Map<Guid, Xywh>) =
    positions
    |> Map.map (fun _ pos -> Xywh(pos.X + dx, pos.Y + dy, pos.W, pos.H))

let private getPositionBounds (positions: Map<Guid, Xywh>) =
    let values = positions |> Map.toList |> List.map snd
    let minX = values |> List.minBy (fun pos -> pos.X) |> fun pos -> pos.X
    let minY = values |> List.minBy (fun pos -> pos.Y) |> fun pos -> pos.Y
    let maxX = values |> List.maxBy (fun pos -> pos.X + pos.W) |> fun pos -> pos.X + pos.W
    let maxY = values |> List.maxBy (fun pos -> pos.Y + pos.H) |> fun pos -> pos.Y + pos.H
    minX, minY, maxX, maxY

let private layoutSingleComponent (content: CanvasContent) =
    let nodes = content.Nodes |> List.filter (fun n -> not n.IsGhost)
    if nodes.IsEmpty then
        Map.empty
    else
        let layerMap = assignLayers nodes content.Arrows
        let orderedLayers = orderWithinLayers layerMap nodes content.Arrows
        let nodeIds = nodes |> List.map (fun n -> n.Id) |> Set.ofList
        let nodesById = nodes |> List.map (fun n -> n.Id, n) |> Map.ofList

        let predecessors = Dictionary<Guid, Set<Guid>>()
        let successors = Dictionary<Guid, Set<Guid>>()
        for node in nodes do
            predecessors.[node.Id] <- Set.empty
            successors.[node.Id] <- Set.empty
        for arrow in content.Arrows do
            if nodeIds.Contains arrow.SourceId && nodeIds.Contains arrow.TargetId then
                predecessors.[arrow.TargetId] <- predecessors.[arrow.TargetId].Add(arrow.SourceId)
                successors.[arrow.SourceId] <- successors.[arrow.SourceId].Add(arrow.TargetId)

        let layerNeighborAffinity (left: CanvasNodeInfo) (right: CanvasNodeInfo) =
            let sharedPred = Set.intersect predecessors.[left.Id] predecessors.[right.Id] |> Set.count
            let sharedSucc = Set.intersect successors.[left.Id] successors.[right.Id] |> Set.count
            let direct =
                (if successors.[left.Id].Contains right.Id then 2 else 0) +
                (if successors.[right.Id].Contains left.Id then 2 else 0)
            sharedPred + sharedSucc + direct

        let maxNodeWidth =
            nodes |> List.map (fun n -> int n.Width) |> List.max

        let stepX = maxNodeWidth + layerSpacingX
        let maxLayersPerRow = max 1 ((canvasWidth - marginLeft) / stepX)
        let layerCount = orderedLayers.Length

        let layerNodeSets =
            orderedLayers
            |> List.map (fun (_, layerNodes) -> layerNodes |> List.map (fun n -> n.Id) |> Set.ofList)
            |> List.toArray

        let boundaryAffinities =
            [|
                for i in 0 .. layerCount - 2 do
                    let currentSet = layerNodeSets.[i]
                    let nextSet = layerNodeSets.[i + 1]
                    let directCount =
                        content.Arrows
                        |> List.sumBy (fun arrow ->
                            if (currentSet.Contains arrow.SourceId && nextSet.Contains arrow.TargetId) ||
                               (currentSet.Contains arrow.TargetId && nextSet.Contains arrow.SourceId) then
                                1
                            else
                                0)
                    yield directCount
            |]

        let chooseRowLengths () =
            let dp = Array.create (layerCount + 1) Int32.MaxValue
            let nextBreak = Array.create (layerCount + 1) layerCount
            dp.[layerCount] <- 0

            for startIdx in layerCount - 1 .. -1 .. 0 do
                let maxLen = min maxLayersPerRow (layerCount - startIdx)
                for len in 1 .. maxLen do
                    let rowEnd = startIdx + len - 1
                    let underfill = maxLayersPerRow - len
                    let underfillCost = underfill * underfill * rowUnderfillPenalty
                    let boundaryCost =
                        if rowEnd >= layerCount - 1 then 0
                        else boundaryAffinities.[rowEnd] * rowBoundaryPenalty
                    let candidate = underfillCost + boundaryCost + dp.[rowEnd + 1]
                    if candidate < dp.[startIdx] then
                        dp.[startIdx] <- candidate
                        nextBreak.[startIdx] <- rowEnd + 1

            let rec build startIdx acc =
                if startIdx >= layerCount then List.rev acc
                else
                    let stopIdx = nextBreak.[startIdx]
                    let len = stopIdx - startIdx
                    build stopIdx (len :: acc)

            build 0 []

        let rowLengths = chooseRowLengths ()
        let rowStarts =
            rowLengths
            |> List.scan (+) 0
            |> List.take rowLengths.Length
            |> List.toArray

        let findRowInfo seqIdx =
            rowStarts
            |> Array.findIndexBack (fun startIdx -> seqIdx >= startIdx)
            |> fun rowIdx -> rowIdx, seqIdx - rowStarts.[rowIdx], rowLengths.[rowIdx]

        let computeColumnLayout (columnNodes: CanvasNodeInfo list) =
            let nodeHeight =
                columnNodes
                |> List.tryHead
                |> Option.map (fun n -> int n.Height)
                |> Option.defaultValue 40

            let rec loop previous remaining currentY acc =
                match remaining with
                | [] -> List.rev acc
                | node :: rest ->
                    let nextY =
                        match previous with
                        | None -> currentY
                        | Some prev ->
                            let spacing =
                                if layerNeighborAffinity prev node > 0 then connectedNodeSpacingY
                                else nodeSpacingY
                            currentY + nodeHeight + spacing
                    loop (Some node) rest nextY ((node, nextY) :: acc)

            loop None columnNodes 0 []

        // 각 레이어의 상대 Y 위치 계산 + 컬럼 높이 수집
        let layerResults =
            orderedLayers
            |> List.mapi (fun seqIdx (_layerIdx, layerNodes) ->
                let rowIdx, colInRow, rowLength = findRowInfo seqIdx
                let isReversed = rowIdx % 2 = 1
                let col = if isReversed then (rowLength - 1 - colInRow) else colInRow
                let x = marginLeft + col * stepX

                let columnLayout = computeColumnLayout layerNodes
                let nodeHeight =
                    layerNodes |> List.tryHead |> Option.map (fun n -> int n.Height) |> Option.defaultValue 40
                let columnHeight =
                    match columnLayout |> List.tryLast with
                    | None -> 0
                    | Some (_, lastY) -> lastY + nodeHeight

                rowIdx, x, columnLayout, columnHeight)

        // 행별 최대 높이 계산
        let rowHeights =
            layerResults
            |> List.groupBy (fun (rowIdx, _, _, _) -> rowIdx)
            |> List.sortBy fst
            |> List.map (fun (_, items) ->
                items |> List.map (fun (_, _, _, h) -> h) |> List.max)

        // 행별 Y 오프셋 누적
        let rowOffsets =
            rowHeights
            |> List.scan (fun acc h -> acc + h + rowGapY) marginTop
            |> List.toArray

        let basePositions =
            layerResults
            |> List.collect (fun (rowIdx, x, columnLayout, _) ->
                let yOffset = rowOffsets.[rowIdx]
                columnLayout
                |> List.map (fun (node, relY) ->
                    node.Id, Xywh(x, yOffset + relY, int node.Width, int node.Height)))
            |> Map.ofList

        let neighbors = buildDirectNeighbors nodes content.Arrows
        let satellitePositions = applySatellitePlacement nodesById layerMap neighbors basePositions
        let cycleComponents = findCycleComponents nodes content.Arrows
        applyCyclePlacement nodesById content.Arrows cycleComponents satellitePositions

let private findConnectedComponents (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) =
    let neighbors = buildDirectNeighbors nodes arrows
    let mutable visited = Set.empty<Guid>
    let components = ResizeArray<Guid list>()

    for node in nodes do
        if not (visited.Contains node.Id) then
            let stack = Stack<Guid>()
            let mutable members = []
            stack.Push(node.Id)
            visited <- visited.Add(node.Id)

            while stack.Count > 0 do
                let currentId = stack.Pop()
                members <- currentId :: members
                for neighborId in neighbors.[currentId] do
                    if not (visited.Contains neighborId) then
                        visited <- visited.Add(neighborId)
                        stack.Push(neighborId)

            components.Add(List.rev members)

    components |> Seq.toList

let computeLayout (content: CanvasContent) : MoveEntityRequest list =
    let nodes = content.Nodes |> List.filter (fun n -> not n.IsGhost)
    if nodes.IsEmpty then
        []
    else
        let components = findConnectedComponents nodes content.Arrows

        let buildSubContent componentIds =
            let componentSet = componentIds |> Set.ofList
            {
                Nodes = nodes |> List.filter (fun node -> componentSet.Contains node.Id)
                Arrows =
                    content.Arrows
                    |> List.filter (fun arrow ->
                        componentSet.Contains arrow.SourceId &&
                        componentSet.Contains arrow.TargetId)
            }

        let sortedComponents =
            components
            |> List.sortByDescending List.length

        match sortedComponents with
        | [] -> []
        | mainComponent :: auxiliaryComponents ->
            let mainContent = buildSubContent mainComponent
            let mainPositions = layoutSingleComponent mainContent
            let _, _, _, mainMaxY = getPositionBounds mainPositions
            let componentGapX =
                let maxNodeWidth = nodes |> List.maxBy (fun node -> node.Width) |> fun node -> int node.Width
                (maxNodeWidth + layerSpacingX) / 2
            let componentGapY = rowGapY + 40

            let auxiliaryStartY = mainMaxY + componentGapY
            let mutable currentX = marginLeft
            let mutable currentY = auxiliaryStartY
            let mutable shelfHeight = 0

            let auxiliaryPositions =
                auxiliaryComponents
                |> List.map (fun componentIds ->
                    let componentPositions = layoutSingleComponent (buildSubContent componentIds)
                    let minX, minY, maxX, maxY = getPositionBounds componentPositions
                    let width = maxX - minX
                    let height = maxY - minY

                    if currentX > marginLeft && currentX + width > canvasWidth - marginLeft then
                        currentX <- marginLeft
                        currentY <- currentY + shelfHeight + componentGapY
                        shelfHeight <- 0

                    let translated =
                        translatePositions (currentX - minX) (currentY - minY) componentPositions

                    currentX <- currentX + width + componentGapX
                    shelfHeight <- max shelfHeight height
                    translated)
                |> List.fold
                    (fun (acc: Map<Guid, Xywh>) (positions: Map<Guid, Xywh>) ->
                        Map.fold (fun (state: Map<Guid, Xywh>) id pos -> state.Add(id, pos)) acc positions)
                    Map.empty<Guid, Xywh>

            let finalPositions =
                Map.fold (fun (state: Map<Guid, Xywh>) id pos -> state.Add(id, pos)) mainPositions auxiliaryPositions

            finalPositions
            |> Map.toList
            |> List.map (fun (id, position) -> MoveEntityRequest(id, position))
