namespace Ds2.Editor

open System
open Ds2.Core
open Ds2.Core.Store

type UndoRecord = {
    Undo: unit -> unit
    Redo: unit -> unit
    Description: string
}

type UndoTransaction = {
    Label: string
    Records: UndoRecord list
    AffectedEntityIds: Guid list
}

type EditorEvent =
    | ProjectAdded of Project
    | ProjectRemoved of Guid
    | SystemAdded of DsSystem
    | SystemRemoved of Guid
    | FlowAdded of Flow
    | FlowRemoved of Guid
    | WorkAdded of Work
    | WorkRemoved of Guid
    | CallAdded of Call
    | CallRemoved of Guid
    | ApiDefAdded of ApiDef
    | ApiDefRemoved of Guid
    | ArrowWorkAdded of ArrowBetweenWorks
    | ArrowWorkRemoved of Guid
    | ArrowCallAdded of ArrowBetweenCalls
    | ArrowCallRemoved of Guid
    | ConnectionsChanged
    | EntityRenamed of id: Guid * newName: string * treeName: string
    | ProjectPropsChanged of id: Guid
    | SystemPropsChanged of id: Guid
    | WorkPropsChanged of id: Guid
    | CallPropsChanged of id: Guid
    | ApiDefPropsChanged of id: Guid
    | HwComponentAdded of entityKind: EntityKind * id: Guid * name: string
    | HwComponentRemoved of entityKind: EntityKind * id: Guid
    | HistoryChanged of undoLabels: string list * redoLabels: string list
    | StoreRefreshed
    /// 노드 위치만 변경된 가벼운 이벤트. 트리/패널/캔버스 visual tree를 재구축하지 않고
    /// 이동된 노드의 위치 + 인접 화살표 path만 갱신하기 위한 hint.
    | EntitiesMoved of ids: Guid list
