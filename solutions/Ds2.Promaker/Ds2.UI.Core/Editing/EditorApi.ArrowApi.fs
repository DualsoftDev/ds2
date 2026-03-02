namespace Ds2.UI.Core

open System
open Ds2.Core

// =============================================================================
// EditorArrowApi — 화살표 추가/삭제/재연결 진입점
// =============================================================================

type EditorArrowApi(store: DsStore, exec: ExecFn, batchExec: BatchExecFn) =

    member _.RemoveArrows(arrowIds: seq<Guid>) : int =
        let cmds = ArrowOps.buildRemoveArrowsCmds store arrowIds |> Seq.toList
        batchExec "Delete Arrows" (fun bExec -> cmds |> List.iter bExec)
        cmds.Length

    member _.ReconnectArrow(arrowId: Guid, replaceSource: bool, newEndpointId: Guid) : bool =
        match ArrowOps.tryResolveReconnectArrowCmd store arrowId replaceSource newEndpointId with
        | Some cmd -> exec cmd; true
        | None -> false

    member _.ConnectSelectionInOrder(orderedNodeIds: seq<Guid>, ?arrowType: ArrowType) : int =
        let connectArrowType = defaultArg arrowType ArrowType.Start
        let cmds = ArrowOps.buildConnectSelectionCmds store orderedNodeIds connectArrowType
        batchExec "Connect Selected Nodes In Order" (fun bExec -> cmds |> List.iter bExec)
        cmds.Length
