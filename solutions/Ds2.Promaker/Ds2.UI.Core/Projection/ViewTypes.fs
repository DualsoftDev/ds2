namespace Ds2.UI.Core

open System
open Ds2.Core

type TreeNodeInfo = {
    Id: Guid
    EntityType: string
    Name: string
    ParentId: Guid option
    Children: TreeNodeInfo list
}

type CanvasNodeInfo = {
    Id: Guid
    EntityType: string
    Name: string
    ParentId: Guid
    X: float
    Y: float
    Width: float
    Height: float
}

type CanvasArrowInfo = {
    Id: Guid
    SourceId: Guid
    TargetId: Guid
    ArrowType: ArrowType
}

type CanvasContent = {
    Nodes: CanvasNodeInfo list
    Arrows: CanvasArrowInfo list
}

[<RequireQualifiedAccess>]
type TabKind =
    | System = 0
    | Flow = 1
    | Work = 2

type TabOpenInfo = {
    Kind: TabKind
    RootId: Guid
    Title: string
}

type SelectionKey(id: Guid, entityType: string) =
    member _.Id = id
    member _.EntityType = entityType

    override _.Equals(obj) =
        match obj with
        | :? SelectionKey as other -> id = other.Id && entityType = other.EntityType
        | _ -> false

    override _.GetHashCode() =
        HashCode.Combine(id, entityType)

[<Sealed>]
type CanvasSelectionCandidate(key: SelectionKey, x: float, y: float, width: float, height: float, name: string) =
    member _.Key = key
    member _.X = x
    member _.Y = y
    member _.Width = width
    member _.Height = height
    member _.Name = name

[<Sealed>]
type NodeSelectionResult(orderedKeys: SelectionKey list, anchor: SelectionKey option) =
    member _.OrderedKeys = orderedKeys
    member _.Anchor = anchor

/// Device 트리에서 ApiDef 선택용 — C#의 CallCreateDialog에서 소비
[<Sealed>]
type ApiDefMatch(apiDefId: Guid, apiDefName: string, systemId: Guid, systemName: string) =
    member _.ApiDefId   = apiDefId
    member _.ApiDefName = apiDefName
    member _.SystemId   = systemId
    member _.SystemName = systemName
    /// 표시 이름: "{SystemName}.{ApiDefName}"
    member _.DisplayName = systemName + "." + apiDefName

/// System 프로퍼티 패널 — ApiDef 항목 (C# 소비용)
[<Sealed>]
type ApiDefPanelItem(id: Guid, name: string, isPush: bool, txWorkId: Guid option, rxWorkId: Guid option, duration: int, memo: string) =
    member _.Id       = id
    member _.Name     = name
    member _.IsPush   = isPush
    member _.TxWorkId = txWorkId
    member _.RxWorkId = rxWorkId
    member _.Duration = duration
    member _.Memo     = memo
    /// C# 편의: TxWorkId가 None이면 Guid.Empty 반환
    member _.TxWorkIdOrEmpty = txWorkId |> Option.defaultValue Guid.Empty
    /// C# 편의: RxWorkId가 None이면 Guid.Empty 반환
    member _.RxWorkIdOrEmpty = rxWorkId |> Option.defaultValue Guid.Empty

/// TX/RX Work 드롭다운 항목 (C# 소비용)
[<Sealed>]
type WorkDropdownItem(id: Guid, name: string) =
    member _.Id   = id
    member _.Name = name
    override _.ToString() = name
