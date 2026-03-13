module Ds2.UI.Core.CanvasLayout

open System
open System.Collections.Generic
open Ds2.Core

// =============================================================================
// 자동 재배치 — 위상 정렬 기반 좌→우 계층 레이아웃
// =============================================================================

let private marginLeft = 80
let private marginTop = 80
let private layerSpacingX = 200
let private nodeSpacingY = 60
let private canvasWidth = 3000
let private canvasHeight = 2000
let private minLayerSpacingX = 80

// ─── 위상 정렬 (Kahn's BFS) ─────────────────────────────────────────────────

/// 노드 → 레이어 인덱스 할당. Ghost 노드 제외.
let private assignLayers (nodes: CanvasNodeInfo list) (arrows: CanvasArrowInfo list) : Map<Guid, int> =
    let nodeIds = nodes |> List.map (fun n -> n.Id) |> Set.ofList
    // arrow 중 양쪽 모두 배치 대상인 것만 사용
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

    // 순환에 빠진 노드 처리: 남은 노드를 최대 레이어+1에 배치
    let maxLayer =
        if layer.Count = 0 then 0
        else layer.Values |> Seq.max
    for nid in nodeIds do
        if not (layer.ContainsKey nid) then
            layer.[nid] <- maxLayer + 1

    layer |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq

// ─── Barycenter 정렬 ────────────────────────────────────────────────────────

/// 레이어별 노드 리스트를 Barycenter 기반으로 정렬
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

    // 인접 노드의 Y 위치를 이용한 barycenter
    let predecessors = Dictionary<Guid, List<Guid>>()
    let successorsMap = Dictionary<Guid, List<Guid>>()
    for nid in nodeIds do
        predecessors.[nid] <- List<Guid>()
        successorsMap.[nid] <- List<Guid>()
    for a in arrows do
        if nodeIds.Contains a.SourceId && nodeIds.Contains a.TargetId then
            successorsMap.[a.SourceId].Add(a.TargetId)
            predecessors.[a.TargetId].Add(a.SourceId)

    // 노드 Y 위치를 추적 (정렬 과정에서 갱신)
    let posY = Dictionary<Guid, float>()
    for n in nodes do posY.[n.Id] <- n.Y

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

    // 2회 forward + backward 패스
    let mutable layers = grouped |> List.map snd |> List.toArray
    let layerIndices = grouped |> List.map fst |> List.toArray

    for _pass in 0..1 do
        // forward: 이전 레이어 기준 정렬
        for i in 1 .. layers.Length - 1 do
            layers.[i] <- sortByBarycenter layers.[i] (fun nid -> predecessors.[nid])
            // 위치 갱신
            layers.[i] |> List.iteri (fun idx n -> posY.[n.Id] <- float idx)

        // backward: 다음 레이어 기준 정렬
        for i in layers.Length - 2 .. -1 .. 0 do
            layers.[i] <- sortByBarycenter layers.[i] (fun nid -> successorsMap.[nid])
            layers.[i] |> List.iteri (fun idx n -> posY.[n.Id] <- float idx)

    Array.zip layerIndices layers
    |> Array.toList
    |> List.map (fun (l, ns) -> l, ns)

// ─── 좌표 할당 ──────────────────────────────────────────────────────────────

let computeLayout (content: CanvasContent) : MoveEntityRequest list =
    let nodes = content.Nodes |> List.filter (fun n -> not n.IsGhost)
    if nodes.IsEmpty then []
    else

    let layerMap = assignLayers nodes content.Arrows
    let orderedLayers = orderWithinLayers layerMap nodes content.Arrows

    let maxNodeWidth =
        nodes |> List.map (fun n -> int n.Width) |> List.max

    let layerCount = orderedLayers.Length

    // 너비 초과 시 layerSpacingX 압축
    let totalWidth = marginLeft + layerCount * (maxNodeWidth + layerSpacingX)
    let effectiveSpacingX =
        if totalWidth > canvasWidth && layerCount > 1 then
            let available = canvasWidth - marginLeft - layerCount * maxNodeWidth
            max (available / layerCount) minLayerSpacingX
        else
            layerSpacingX

    orderedLayers
    |> List.collect (fun (layerIdx, layerNodes) ->
        let x = marginLeft + layerIdx * (maxNodeWidth + effectiveSpacingX)

        // 높이 초과 시 서브컬럼 분할
        let maxNodesInColumn =
            let available = canvasHeight - marginTop * 2
            let nodeHeight = layerNodes |> List.tryHead |> Option.map (fun n -> int n.Height) |> Option.defaultValue 40
            max (available / (nodeHeight + nodeSpacingY)) 1

        layerNodes
        |> List.mapi (fun i n ->
            let subCol = i / maxNodesInColumn
            let idxInCol = i % maxNodesInColumn
            let nodeHeight = int n.Height
            let finalX = x + subCol * (maxNodeWidth + effectiveSpacingX / 2)
            let finalY = marginTop + idxInCol * (nodeHeight + nodeSpacingY)
            MoveEntityRequest(n.Id, Xywh(finalX, finalY, int n.Width, nodeHeight))))
