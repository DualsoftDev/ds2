module Ds2.UI.Core.ArrowPathCalculator

open System
open System.Collections.Generic
open Ds2.Core

/// Arrow path calculation result.
/// When Points has 4 items, C# renders it as cubic Bezier: [start; cp1; cp2; end].
type ArrowVisual = {
    Points: (float * float) list
}

let private edgeSepDistance = 8.0
let private obstacleMargin = 10.0
let private epsilon = 0.000001

type private DockFace =
    | Right
    | Left
    | Bottom
    | Top

type private RouteCategory =
    | HorizontalFlow
    | VerticalFlow
    | DiagonalBridge

type private RouteProfile = {
    StartPadding: float
    EndPadding: float
    HandleLength: float
    BaseShift: float
    CandidateShifts: float list
    CandidateScales: float list
}

let private rectOf (r: Xywh) =
    float r.X, float r.Y, float r.W, float r.H

let private centerOf (r: Xywh) =
    let x, y, w, h = rectOf r
    x + w / 2.0, y + h / 2.0

let private clamp minValue maxValue value =
    max minValue (min maxValue value)

let private faceVector face =
    match face with
    | Right -> 1.0, 0.0
    | Left -> -1.0, 0.0
    | Bottom -> 0.0, 1.0
    | Top -> 0.0, -1.0

let private faceOfBoundaryPoint (r: Xywh) (px, py) =
    let x, y, w, h = rectOf r
    [|
        Right, abs (px - (x + w))
        Left, abs (px - x)
        Bottom, abs (py - (y + h))
        Top, abs (py - y)
    |]
    |> Array.minBy snd
    |> fst

let private boundaryPointToward (r: Xywh) (towardX, towardY) =
    let x, _, w, h = rectOf r
    let cx, cy = centerOf r
    let dx = towardX - cx
    let dy = towardY - cy

    if abs dx <= epsilon && abs dy <= epsilon then
        Right, (x + w, cy)
    else
        let halfW = w / 2.0
        let halfH = h / 2.0
        let scaleX =
            if abs dx <= epsilon then Double.PositiveInfinity
            else halfW / abs dx
        let scaleY =
            if abs dy <= epsilon then Double.PositiveInfinity
            else halfH / abs dy
        let scale = min scaleX scaleY
        let px = cx + dx * scale
        let py = cy + dy * scale
        faceOfBoundaryPoint r (px, py), (px, py)

let private determineFaces (source: Xywh) (target: Xywh) =
    let targetCenter = centerOf target
    let sourceCenter = centerOf source
    let srcFace, _ = boundaryPointToward source targetCenter
    let tgtFace, _ = boundaryPointToward target sourceCenter
    srcFace, tgtFace

let private dockPointOnFace (r: Xywh) face (towardX, towardY) perpOffset =
    let x, y, w, h = rectOf r
    let _, (baseX, baseY) = boundaryPointToward r (towardX, towardY)
    match face with
    | Right -> x + w, clamp y (y + h) (baseY + perpOffset)
    | Left -> x, clamp y (y + h) (baseY + perpOffset)
    | Bottom -> clamp x (x + w) (baseX + perpOffset), y + h
    | Top -> clamp x (x + w) (baseX + perpOffset), y

let private classifyRouteCategory (source: Xywh) (target: Xywh) =
    let srcCx, srcCy = centerOf source
    let tgtCx, tgtCy = centerOf target
    let _, _, srcW, srcH = rectOf source
    let _, _, tgtW, tgtH = rectOf target
    let dx = abs (tgtCx - srcCx)
    let dy = abs (tgtCy - srcCy)
    let rowTolerance = (srcH + tgtH) / 2.0 * 0.75
    let colTolerance = (srcW + tgtW) / 2.0 * 0.75

    if dy <= rowTolerance && dx >= colTolerance then HorizontalFlow
    elif dx <= colTolerance && dy >= rowTolerance then VerticalFlow
    else DiagonalBridge

let private buildRouteProfile (source: Xywh) (target: Xywh) (srcOff: float) (tgtOff: float) =
    let srcCx, srcCy = centerOf source
    let tgtCx, tgtCy = centerOf target
    let dx = tgtCx - srcCx
    let dy = tgtCy - srcCy
    let dist = sqrt (dx * dx + dy * dy)
    let laneBias = ((srcOff + tgtOff) / 2.0) * 1.15 + ((srcOff - tgtOff) * 0.35)
    let closeRangeThreshold = 170.0
    let isCloseRange =
        dist <= closeRangeThreshold
        && abs laneBias < 0.5

    match classifyRouteCategory source target with
    | HorizontalFlow ->
        if isCloseRange then
            {
                StartPadding = 0.0
                EndPadding = 0.0
                HandleLength = clamp 10.0 20.0 (dist * 0.10)
                BaseShift = 0.0
                CandidateShifts = [ 0.0 ]
                CandidateScales = [ 1.0 ]
            }
        else
            let preferredLift = if dx >= 0.0 then -1.0 else 1.0
            let baseLift = preferredLift * clamp 24.0 72.0 (dist * 0.10)
            {
                StartPadding = 0.0
                EndPadding = 0.0
                HandleLength = clamp 32.0 120.0 (dist * 0.34)
                BaseShift = baseLift + laneBias
                CandidateShifts = [ 0.0; 18.0; -18.0; 36.0; -36.0; 54.0; -54.0 ]
                CandidateScales = [ 1.0; 1.2; 1.45; 1.8 ]
            }
    | VerticalFlow ->
        if isCloseRange then
            {
                StartPadding = 0.0
                EndPadding = 0.0
                HandleLength = clamp 10.0 20.0 (dist * 0.10)
                BaseShift = 0.0
                CandidateShifts = [ 0.0 ]
                CandidateScales = [ 1.0 ]
            }
        else
            let preferredLift = if dy >= 0.0 then 1.0 else -1.0
            let baseLift = preferredLift * clamp 24.0 72.0 (dist * 0.10)
            {
                StartPadding = 0.0
                EndPadding = 0.0
                HandleLength = clamp 32.0 120.0 (dist * 0.34)
                BaseShift = baseLift + laneBias
                CandidateShifts = [ 0.0; 18.0; -18.0; 36.0; -36.0; 54.0; -54.0 ]
                CandidateScales = [ 1.0; 1.2; 1.45; 1.8 ]
            }
    | DiagonalBridge ->
        let preferredLift =
            if abs dy >= abs dx then
                if dy >= 0.0 then 1.0 else -1.0
            else if dx >= 0.0 then -1.0 else 1.0
        let baseLift = preferredLift * clamp 10.0 34.0 (dist * 0.05)
        {
            StartPadding = 0.0
            EndPadding = 0.0
            HandleLength = clamp 26.0 90.0 (dist * 0.28)
            BaseShift = baseLift + laneBias * 0.85
            CandidateShifts = [ 0.0; 14.0; -14.0; 28.0; -28.0; 42.0; -42.0 ]
            CandidateScales = [ 1.0; 1.2; 1.45 ]
        }

let private sampleBezier (sx, sy) (c1x, c1y) (c2x, c2y) (ex, ey) t =
    let u = 1.0 - t
    u * u * u * sx + 3.0 * u * u * t * c1x + 3.0 * u * t * t * c2x + t * t * t * ex,
    u * u * u * sy + 3.0 * u * u * t * c1y + 3.0 * u * t * t * c2y + t * t * t * ey

let private pointInInflatedRect (px, py) (r: Xywh) =
    let x, y, w, h = rectOf r
    px >= x - obstacleMargin
    && px <= x + w + obstacleMargin
    && py >= y - obstacleMargin
    && py <= y + h + obstacleMargin

let private bezierHitsAnyObstacle p0 p1 p2 p3 (obstacles: Xywh list) =
    if obstacles.IsEmpty then
        false
    else
        let mutable hit = false
        for i in 1 .. 19 do
            if not hit then
                let t = float i / 20.0
                let pt = sampleBezier p0 p1 p2 p3 t
                for obs in obstacles do
                    if not hit && pointInInflatedRect pt obs then
                        hit <- true
        hit

let private computeBezierPoints srcFace tgtFace (srcPos: Xywh) (tgtPos: Xywh) srcOff tgtOff (obstacles: Xywh list) =
    let profile = buildRouteProfile srcPos tgtPos srcOff tgtOff
    let tgtCenter = centerOf tgtPos
    let srcCenter = centerOf srcPos
    let rawSx, rawSy = dockPointOnFace srcPos srcFace tgtCenter srcOff
    let rawTx, rawTy = dockPointOnFace tgtPos tgtFace srcCenter tgtOff
    let srcNx, srcNy = faceVector srcFace
    let tgtNx, tgtNy = faceVector tgtFace

    let sx = rawSx + srcNx * profile.StartPadding
    let sy = rawSy + srcNy * profile.StartPadding
    let tx = rawTx + tgtNx * profile.EndPadding
    let ty = rawTy + tgtNy * profile.EndPadding
    let dist = sqrt ((tx - sx) ** 2.0 + (ty - sy) ** 2.0)

    let lineLen = max dist 1.0
    let perpX = -(ty - sy) / lineLen
    let perpY = (tx - sx) / lineLen

    let makePoints scale extraShift =
        let handleLength = profile.HandleLength * scale
        let shift = profile.BaseShift + extraShift
        let cdx1 = srcNx * handleLength
        let cdy1 = srcNy * handleLength
        let cdx2 = tgtNx * handleLength
        let cdy2 = tgtNy * handleLength
        (sx, sy),
        (sx + cdx1 + perpX * shift, sy + cdy1 + perpY * shift),
        (tx + cdx2 + perpX * shift, ty + cdy2 + perpY * shift),
        (tx, ty)

    if obstacles.IsEmpty then
        let p0, p1, p2, p3 = makePoints 1.0 0.0
        [ p0; p1; p2; p3 ]
    else
        let candidates =
            [
                for scale in profile.CandidateScales do
                    for shift in profile.CandidateShifts do
                        scale, shift
            ]

        let rec tryNext = function
            | [] ->
                let p0, p1, p2, p3 = makePoints 1.0 0.0
                [ p0; p1; p2; p3 ]
            | (scale, shift) :: rest ->
                let p0, p1, p2, p3 = makePoints scale shift
                if bezierHitsAnyObstacle p0 p1 p2 p3 obstacles then tryNext rest
                else [ p0; p1; p2; p3 ]

        tryNext candidates

let chooseDockPoints (source: Xywh) (target: Xywh) =
    let tgtCenter = centerOf target
    let srcCenter = centerOf source
    let srcFace, _ = boundaryPointToward source tgtCenter
    let tgtFace, _ = boundaryPointToward target srcCenter
    dockPointOnFace source srcFace tgtCenter 0.0,
    dockPointOnFace target tgtFace srcCenter 0.0

let computePathWithObstacles (source: Xywh) (target: Xywh) (obstacles: Xywh list) : ArrowVisual =
    let srcFace, tgtFace = determineFaces source target
    { Points = computeBezierPoints srcFace tgtFace source target 0.0 0.0 obstacles }

let computePath (source: Xywh) (target: Xywh) : ArrowVisual =
    computePathWithObstacles source target []

let private tryResolveNodePosition
    (store: DsStore)
    (positions: Dictionary<Guid, Xywh>)
    (nodeId: Guid)
    : Xywh option =
    match positions.TryGetValue nodeId with
    | true, pos -> Some pos
    | _ ->
        match DsQuery.getWork nodeId store with
        | Some work -> Some(defaultArg work.Position (UiDefaults.createDefaultNodeBounds ()))
        | None ->
            match DsQuery.getCall nodeId store with
            | Some call -> Some(defaultArg call.Position (UiDefaults.createDefaultNodeBounds ()))
            | None -> None

let computeFlowArrowPaths (store: DsStore) (flowId: Guid) : Map<Guid, ArrowVisual> =
    let positions = Dictionary<Guid, Xywh>()
    for work in DsQuery.worksOf flowId store do
        positions.[work.Id] <- defaultArg work.Position (UiDefaults.createDefaultNodeBounds ())
        for call in DsQuery.callsOf work.Id store do
            positions.[call.Id] <- defaultArg call.Position (UiDefaults.createDefaultNodeBounds ())

    let arrows = ResizeArray<DsArrow>()
    match DsQuery.getFlow flowId store with
    | Some flow ->
        for a in DsQuery.arrowWorksOf flow.ParentId store do
            arrows.Add(a :> DsArrow)
    | None -> ()

    for work in DsQuery.worksOf flowId store do
        for a in DsQuery.arrowCallsOf work.Id store do
            arrows.Add(a :> DsArrow)

    let resolved = ResizeArray<struct (DsArrow * Xywh * Xywh * DockFace * DockFace)>()
    for arrow in arrows do
        match tryResolveNodePosition store positions arrow.SourceId, tryResolveNodePosition store positions arrow.TargetId with
        | Some srcPos, Some tgtPos ->
            let srcFace, tgtFace = determineFaces srcPos tgtPos
            resolved.Add(struct (arrow, srcPos, tgtPos, srcFace, tgtFace))
        | _ -> ()

    let faceTotal = Dictionary<struct (Guid * DockFace), int>()
    for struct (arrow, _, _, srcFace, tgtFace) in resolved do
        let sk = struct (arrow.SourceId, srcFace)
        faceTotal.[sk] <- (match faceTotal.TryGetValue sk with | true, c -> c | _ -> 0) + 1
        let tk = struct (arrow.TargetId, tgtFace)
        faceTotal.[tk] <- (match faceTotal.TryGetValue tk with | true, c -> c | _ -> 0) + 1

    let allPositions = positions |> Seq.map (fun kv -> kv.Key, kv.Value) |> Seq.toList
    let faceIdx = Dictionary<struct (Guid * DockFace), int>()
    let result = Dictionary<Guid, ArrowVisual>()

    for struct (arrow, srcPos, tgtPos, srcFace, tgtFace) in resolved do
        let sk = struct (arrow.SourceId, srcFace)
        let tk = struct (arrow.TargetId, tgtFace)

        let si = match faceIdx.TryGetValue sk with | true, i -> i | _ -> 0
        faceIdx.[sk] <- si + 1
        let ti = match faceIdx.TryGetValue tk with | true, i -> i | _ -> 0
        faceIdx.[tk] <- ti + 1

        let srcOff = (float si - float (faceTotal.[sk] - 1) / 2.0) * edgeSepDistance
        let tgtOff = (float ti - float (faceTotal.[tk] - 1) / 2.0) * edgeSepDistance

        let obstacles =
            allPositions
            |> List.choose (fun (id, pos) ->
                if id = arrow.SourceId || id = arrow.TargetId then None else Some pos)

        result.[arrow.Id] <- {
            Points = computeBezierPoints srcFace tgtFace srcPos tgtPos srcOff tgtOff obstacles
        }

    result |> Seq.map (fun kv -> kv.Key, kv.Value) |> Map.ofSeq
