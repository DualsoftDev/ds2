namespace Ds2.Editor

open Ds2.Core.Store
open System
open System.Runtime.CompilerServices

module CanvasLayout =

    /// 비고스트 노드가 2개 이상인데 모두 같은 좌표에 몰려있으면 true
    let needsAutoLayout (content: CanvasContent) =
        let realNodes = content.Nodes |> List.filter (fun n -> not n.IsGhost)
        match realNodes with
        | [] | [_] -> false
        | _ ->
            let distinctPositions = realNodes |> List.map (fun n -> (n.X, n.Y)) |> List.distinct
            distinctPositions.Length <= 1

    let computeLayout content =
        CanvasLayoutPlacement.computeLayoutImpl content

module EditorCanvasLayout =

    [<CompiledName("NeedsAutoLayout")>]
    let needsAutoLayout content =
        CanvasLayout.needsAutoLayout content

    [<CompiledName("ComputeAutoLayout")>]
    let computeAutoLayout (store: DsStore) (kind: TabKind) (rootId: Guid) : MoveEntityRequest list =
        let content = EditorCanvasProjection.canvasContentForTab store kind rootId
        CanvasLayout.computeLayout content
