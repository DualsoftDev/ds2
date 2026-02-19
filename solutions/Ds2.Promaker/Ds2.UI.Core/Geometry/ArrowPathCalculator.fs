module Ds2.UI.Core.ArrowPathCalculator

open System
open Ds2.Core

/// 화살표 경로 + 화살표 헤드 계산 결과
type ArrowVisual = {
    Points: (float * float) list
}

// --- 유틸 함수 ---

let private edgeMidpoints (r: Xywh) =
    let x, y, w, h = float r.X, float r.Y, float r.W, float r.H
    [| x + w / 2.0, y             // Top
       x + w / 2.0, y + h         // Bottom
       x,           y + h / 2.0   // Left
       x + w,       y + h / 2.0 |] // Right

let private dist2 (x1, y1) (x2, y2) =
    (x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1)

let private compressPoints (points: (float * float) list) =
    let rec loop acc rest =
        match acc, rest with
        | _, [] -> List.rev acc
        | [], p :: tail -> loop [ p ] tail
        | prev :: _, p :: tail ->
            if prev = p then loop acc tail
            else loop (p :: acc) tail

    loop [] points

// --- 도킹 포인트 선택 ---

/// source/target 4변의 중점들에서 최단 거리의 점을 선택
let chooseDockPoints (source: Xywh) (target: Xywh) =
    let srcPts = edgeMidpoints source
    let tgtPts = edgeMidpoints target
    let mutable bestSrc = srcPts.[0]
    let mutable bestTgt = tgtPts.[0]
    let mutable bestDist = Double.MaxValue
    for s in srcPts do
        for t in tgtPts do
            let d = dist2 s t
            if d < bestDist then
                bestDist <- d
                bestSrc <- s
                bestTgt <- t
    bestSrc, bestTgt

// --- 경로 계산 ---


/// source->target 직교(polyline) 경로 + 화살표 헤드 계산
let computePath (source: Xywh) (target: Xywh) : ArrowVisual =
    let (sx, sy), (tx, ty) = chooseDockPoints source target

    let rawPoints =
        if abs (tx - sx) >= abs (ty - sy) then
            let mx = (sx + tx) / 2.0
            [ (sx, sy); (mx, sy); (mx, ty); (tx, ty) ]
        else
            let my = (sy + ty) / 2.0
            [ (sx, sy); (sx, my); (tx, my); (tx, ty) ]

    let points = compressPoints rawPoints
    { Points = points }

// --- Flow 단위 일괄 계산 ---

let private buildNodePositions (store: DsStore) (flowId: Guid) =
    let positions = Collections.Generic.Dictionary<Guid, Xywh>()
    for work in DsQuery.worksOf flowId store do
        match work.Position with
        | Some pos -> positions.[work.Id] <- pos
        | None -> positions.[work.Id] <- UiDefaults.createDefaultNodeBounds ()
        for call in DsQuery.callsOf work.Id store do
            match call.Position with
            | Some pos -> positions.[call.Id] <- pos
            | None -> positions.[call.Id] <- UiDefaults.createDefaultNodeBounds ()
    positions

/// Flow 내 모든 화살표 경로 일괄 계산
let computeFlowArrowPaths (store: DsStore) (flowId: Guid) : Map<Guid, ArrowVisual> =
    let positions = buildNodePositions store flowId
    let result = Collections.Generic.Dictionary<Guid, ArrowVisual>()
    for arrow in DsQuery.allArrowWorks store do
        if arrow.ParentId = flowId then
            match positions.TryGetValue(arrow.SourceId), positions.TryGetValue(arrow.TargetId) with
            | (true, srcPos), (true, tgtPos) -> result.[arrow.Id] <- computePath srcPos tgtPos
            | _ -> ()
    for arrow in DsQuery.allArrowCalls store do
        if arrow.ParentId = flowId then
            match positions.TryGetValue(arrow.SourceId), positions.TryGetValue(arrow.TargetId) with
            | (true, srcPos), (true, tgtPos) -> result.[arrow.Id] <- computePath srcPos tgtPos
            | _ -> ()
    result |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
