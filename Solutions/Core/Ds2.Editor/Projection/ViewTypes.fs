namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Core.Store

type TreeNodeInfo = {
    Id: Guid
    EntityKind: EntityKind
    Name: string
    ParentId: Guid option
    Children: TreeNodeInfo list
}
with
    member this.ParentIdOrNull = this.ParentId |> Option.toNullable

type CanvasNodeInfo = {
    Id: Guid
    EntityKind: EntityKind
    Name: string
    ParentId: Guid
    X: float
    Y: float
    Width: float
    Height: float
    /// Call 노드의 조건 타입들 (Work 노드는 빈 리스트)
    ConditionTypes: CallConditionType list
    /// 타 Flow의 Work가 화살표로 연결되어 고스트로 표시되는 경우 true
    IsGhost: bool
    /// Reference Work인 경우 true (원본을 참조하는 복제 노드)
    IsReference: bool
    /// Reference Work의 원본 Work ID (IsReference=true일 때만 유효)
    ReferenceOfId: Guid option
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

[<AllowNullLiteral>]
type SelectionKey(id: Guid, entityKind: EntityKind) =
    member _.Id = id
    member _.EntityKind = entityKind

    interface IEquatable<SelectionKey> with
        member _.Equals(other: SelectionKey) =
            not (isNull (box other))
            && id = other.Id
            && entityKind = other.EntityKind

    override this.Equals(obj) =
        match obj with
        | :? SelectionKey as other -> (this :> IEquatable<SelectionKey>).Equals(other)
        | _ -> false

    override _.GetHashCode() =
        HashCode.Combine(id, int entityKind)

[<RequireQualifiedAccess>]
type CopyValidationResult =
    | Ok of SelectionKey list
    | MixedTypes
    | MixedParents
    | NothingToCopy

[<RequireQualifiedAccess>]
type PasteValidationResult =
    | Ok
    | SameWorkPaste
    | DuplicateCallInWork

[<RequireQualifiedAccess>]
type PasteResult =
    | Ok of Guid list
    | Blocked of PasteValidationResult

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
    member _.AnchorOrNull = defaultArg anchor null

/// System 프로퍼티 패널 — ApiDef 항목 (C# 소비용)
[<Sealed>]
type ApiDefPanelItem(id: Guid, name: string, actionType: ApiDefActionType, txWorkId: Guid option, rxWorkId: Guid option, description: string) =
    member _.Id             = id
    member _.Name           = name
    member _.ActionType     = actionType
    member _.TxWorkId       = txWorkId
    member _.RxWorkId       = rxWorkId
    member _.TxWorkIdOrNull = txWorkId |> Option.toNullable
    member _.RxWorkIdOrNull = rxWorkId |> Option.toNullable
    member _.Description    = description

[<Sealed>]
type ApiDefEditInfo(systemId: Guid, item: ApiDefPanelItem) =
    member _.SystemId = systemId
    member _.Item = item

/// TX/RX Work 드롭다운 항목 (C# 소비용)
[<Sealed>]
type WorkDropdownItem(id: Guid, name: string, ?isNone: bool) =
    member _.Id     = id
    member _.Name   = name
    member _.IsNone = defaultArg isNone false
    override _.ToString() = name

// =============================================================================
// PropertyPanel 전용 타입
// =============================================================================

[<Sealed>]
type DeviceApiDefOption(id: Guid, deviceName: string, apiDefName: string) =
    member _.Id = id
    member _.DeviceName = deviceName
    member _.ApiDefName = apiDefName
    member _.DisplayName = $"{deviceName}.{apiDefName}"

[<Sealed>]
type CallApiCallPanelItem
    (
        apiCallId: Guid,
        name: string,
        apiDefId: Guid option,
        apiDefDisplayName: string,
        outputTagName: string,
        outputAddress: string,
        inputTagName: string,
        inputAddress: string,
        valueSpecText: string,
        inputValueSpecText: string,
        outputSpecTypeIndex: int,
        inputSpecTypeIndex: int
    ) =
    member _.ApiCallId = apiCallId
    member _.Name = name
    member _.ApiDefId = apiDefId
    member _.ApiDefIdOrNull = apiDefId |> Option.toNullable
    member _.ApiDefDisplayName = apiDefDisplayName
    member _.OutputTagName = outputTagName
    member _.OutputAddress = outputAddress
    member _.InputTagName = inputTagName
    member _.InputAddress = inputAddress
    member _.ValueSpecText = valueSpecText
    member _.InputValueSpecText = inputValueSpecText
    member _.OutputSpecTypeIndex = outputSpecTypeIndex
    member _.InputSpecTypeIndex  = inputSpecTypeIndex

[<Sealed>]
type CallConditionApiCallItem
    (apiCallId: Guid, apiCallName: string, apiDefDisplayName: string,
     outputSpecText: string, outputSpecTypeIndex: int,
     inputSpecText: string, inputSpecTypeIndex: int) =
    member _.ApiCallId          = apiCallId
    member _.ApiCallName        = apiCallName
    member _.ApiDefDisplayName  = apiDefDisplayName
    member _.OutputSpecText     = outputSpecText
    member _.OutputSpecTypeIndex = outputSpecTypeIndex
    member _.InputSpecText      = inputSpecText
    member _.InputSpecTypeIndex = inputSpecTypeIndex

[<Sealed>]
type CallConditionPanelItem
    (conditionId: Guid, conditionType: CallConditionType,
     isOR: bool, isRising: bool, items: CallConditionApiCallItem list,
     children: CallConditionPanelItem list) =
    member _.ConditionId   = conditionId
    member _.ConditionType = conditionType
    member _.IsOR          = isOR
    member _.IsRising      = isRising
    member _.Items         = items
    member _.Children      = children
