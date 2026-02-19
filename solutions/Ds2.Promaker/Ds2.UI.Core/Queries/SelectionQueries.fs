module Ds2.UI.Core.SelectionQueries

open System
open System.Collections.Generic

let private distinctSelectionKeys (keys: seq<SelectionKey>) =
    let seen = HashSet<SelectionKey>()
    keys
    |> Seq.filter seen.Add
    |> Seq.toList

let orderCanvasSelectionKeys (nodes: seq<CanvasSelectionCandidate>) : SelectionKey list =
    nodes
    |> Seq.sortWith (fun left right ->
        let byY = compare left.Y right.Y
        if byY <> 0 then
            byY
        else
            let byX = compare left.X right.X
            if byX <> 0 then
                byX
            else
                let byName = StringComparer.Ordinal.Compare(left.Name, right.Name)
                if byName <> 0 then byName
                else compare left.Key.Id right.Key.Id)
    |> Seq.map (fun node -> node.Key)
    |> Seq.toList

let orderCanvasSelectionKeysForBox
    (startX: float)
    (startY: float)
    (endX: float)
    (endY: float)
    (nodes: seq<CanvasSelectionCandidate>)
    : SelectionKey list =

    let epsilon = 0.001
    let dxAbs = abs (endX - startX)
    let dyAbs = abs (endY - startY)

    nodes
    |> Seq.map (fun node ->
        let centerX = node.X + node.Width / 2.0
        let centerY = node.Y + node.Height / 2.0
        let tx = if dxAbs < epsilon then 0.0 else abs (centerX - startX) / dxAbs
        let ty = if dyAbs < epsilon then 0.0 else abs (centerY - startY) / dyAbs
        let includeOrder = max tx ty
        let distance2 = (centerX - startX) * (centerX - startX) + (centerY - startY) * (centerY - startY)
        node.Key, includeOrder, distance2, centerY, centerX, node.Name)
    |> Seq.sortWith (fun (leftKey, leftOrder, leftDistance, leftCenterY, leftCenterX, leftName)
                         (rightKey, rightOrder, rightDistance, rightCenterY, rightCenterX, rightName) ->
        let byOrder = compare leftOrder rightOrder
        if byOrder <> 0 then
            byOrder
        else
            let byDistance = compare leftDistance rightDistance
            if byDistance <> 0 then
                byDistance
            else
                let byCenterY = compare leftCenterY rightCenterY
                if byCenterY <> 0 then
                    byCenterY
                else
                    let byCenterX = compare leftCenterX rightCenterX
                    if byCenterX <> 0 then
                        byCenterX
                    else
                        let byName = StringComparer.Ordinal.Compare(leftName, rightName)
                        if byName <> 0 then byName
                        else compare leftKey.Id rightKey.Id)
    |> Seq.map (fun (key, _, _, _, _, _) -> key)
    |> Seq.toList

let private tryFindSelectionIndex (orderedKeys: SelectionKey array) (target: SelectionKey) =
    orderedKeys
    |> Array.tryFindIndex (fun key -> key = target)

let private tryApplyRangeSelection
    (anchor: SelectionKey option)
    (targetKey: SelectionKey)
    (additive: bool)
    (currentSelection: SelectionKey list)
    (orderedKeys: SelectionKey array)
    : SelectionKey list option =

    match anchor with
    | None -> None
    | Some anchorKey ->
        match tryFindSelectionIndex orderedKeys anchorKey, tryFindSelectionIndex orderedKeys targetKey with
        | Some startIndex, Some endIndex ->
            let fromIndex = min startIndex endIndex
            let toIndex = max startIndex endIndex
            let seed = if additive then currentSelection else []
            let result = ResizeArray<SelectionKey>(seed)
            let seen = HashSet<SelectionKey>(seed)

            for index = fromIndex to toIndex do
                let key = orderedKeys.[index]
                if seen.Add(key) then
                    result.Add(key)

            Some (result |> Seq.toList)
        | _ -> None

let applyNodeSelection
    (currentSelection: seq<SelectionKey>)
    (anchor: SelectionKey option)
    (target: SelectionKey option)
    (ctrlPressed: bool)
    (shiftPressed: bool)
    (orderedKeys: seq<SelectionKey>)
    : NodeSelectionResult =

    let current = distinctSelectionKeys currentSelection

    match target with
    | None ->
        if ctrlPressed || shiftPressed then
            NodeSelectionResult(current, anchor)
        else
            NodeSelectionResult([], None)
    | Some targetKey ->
        let ordered = orderedKeys |> Seq.toArray

        let rangeApplied =
            if shiftPressed then
                tryApplyRangeSelection anchor targetKey ctrlPressed current ordered
            else
                None

        match rangeApplied with
        | Some ranged ->
            NodeSelectionResult(ranged, Some targetKey)
        | None ->
            if ctrlPressed && not shiftPressed then
                let toggled =
                    if current |> List.exists (fun key -> key = targetKey) then
                        current |> List.filter (fun key -> key <> targetKey)
                    else
                        current @ [ targetKey ]

                NodeSelectionResult(toggled, Some targetKey)
            else
                NodeSelectionResult([ targetKey ], Some targetKey)
