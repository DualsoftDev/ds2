namespace Ds2.UI.Core

open System
open Ds2.Core

module UiDefaults =
    /// Fixed logical root ID for the device tree. Keep stable across sessions/files.
    let DeviceTreeRootId = Guid "11111111-1111-1111-1111-111111111111"

    [<Literal>]
    let DefaultNodeX = 0

    [<Literal>]
    let DefaultNodeY = 0

    [<Literal>]
    let DefaultNodeWidth = 120

    [<Literal>]
    let DefaultNodeHeight = 40

    [<Literal>]
    let DefaultNodeXf = 0.0

    [<Literal>]
    let DefaultNodeYf = 0.0

    [<Literal>]
    let DefaultNodeWidthf = 120.0

    [<Literal>]
    let DefaultNodeHeightf = 40.0

    let createDefaultNodeBounds () =
        Xywh(DefaultNodeX, DefaultNodeY, DefaultNodeWidth, DefaultNodeHeight)

    /// PassiveSystem의 ApiDef 카테고리 노드에 쓰이는 고정 ID (systemId에서 결정론적으로 파생).
    let apiDefCategoryId (systemId: Guid) =
        let bytes = systemId.ToByteArray()
        bytes.[0] <- bytes.[0] ^^^ 0xCAuy
        bytes.[15] <- bytes.[15] ^^^ 0xFEuy
        Guid(bytes)

[<RequireQualifiedAccess>]
module EntityTypeNames =
    [<Literal>]
    let Project = "Project"

    [<Literal>]
    let System = "System"

    [<Literal>]
    let Flow = "Flow"

    [<Literal>]
    let Work = "Work"

    [<Literal>]
    let Call = "Call"

    [<Literal>]
    let ApiDef = "ApiDef"

    [<Literal>]
    let Button = "Button"

    [<Literal>]
    let Lamp = "Lamp"

    [<Literal>]
    let Condition = "Condition"

    [<Literal>]
    let Action = "Action"

    [<Literal>]
    let ApiDefCategory = "ApiDefCategory"

    [<Literal>]
    let DeviceRoot = "DeviceRoot"

// =============================================================================
// EntityKind — 엔티티 타입 DU (컴파일 시점 완전성 보장)
// =============================================================================

[<Struct>]
type EntityKind =
    | Project | System | Flow | Work | Call
    | ApiDef | Button | Lamp | Condition | Action

module EntityKind =
    let tryOfString (s: string) : EntityKind voption =
        match s with
        | EntityTypeNames.Project   -> ValueSome Project
        | EntityTypeNames.System    -> ValueSome System
        | EntityTypeNames.Flow      -> ValueSome Flow
        | EntityTypeNames.Work      -> ValueSome Work
        | EntityTypeNames.Call      -> ValueSome Call
        | EntityTypeNames.ApiDef    -> ValueSome ApiDef
        | EntityTypeNames.Button    -> ValueSome Button
        | EntityTypeNames.Lamp      -> ValueSome Lamp
        | EntityTypeNames.Condition -> ValueSome Condition
        | EntityTypeNames.Action    -> ValueSome Action
        | _           -> ValueNone

    let toString (k: EntityKind) =
        match k with
        | Project -> EntityTypeNames.Project
        | System -> EntityTypeNames.System
        | Flow -> EntityTypeNames.Flow
        | Work -> EntityTypeNames.Work
        | Call -> EntityTypeNames.Call
        | ApiDef -> EntityTypeNames.ApiDef
        | Button -> EntityTypeNames.Button
        | Lamp -> EntityTypeNames.Lamp
        | Condition -> EntityTypeNames.Condition
        | Action -> EntityTypeNames.Action

module EntityNameAccess =
    let private tryGetEntity (store: DsStore) (entityType: string) (id: Guid) : DsEntity option =
        match EntityKind.tryOfString entityType with
        | ValueSome Project   -> DsQuery.getProject   id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome System    -> DsQuery.getSystem     id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome Flow      -> DsQuery.getFlow       id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome Work      -> DsQuery.getWork       id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome Call      -> DsQuery.getCall       id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome ApiDef    -> DsQuery.getApiDef     id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome Button    -> DsQuery.getButton     id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome Lamp      -> DsQuery.getLamp       id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome Condition -> DsQuery.getCondition  id store |> Option.map (fun e -> e :> DsEntity)
        | ValueSome Action    -> DsQuery.getAction     id store |> Option.map (fun e -> e :> DsEntity)
        | ValueNone           -> None

    let tryGetName (store: DsStore) (entityType: string) (id: Guid) : string option =
        tryGetEntity store entityType id |> Option.map (fun e -> e.Name)

    let setName (store: DsStore) (entityType: string) (id: Guid) (newName: string) =
        match tryGetEntity store entityType id with
        | Some e -> e.Name <- newName
        | None -> failwithf "Unknown entity type: %s" entityType
// =============================================================================
// EditorCommand — 모든 편집 명령을 표현하는 DU (STRUCTURE.md 4.1)
// =============================================================================

type EditorCommand =

    // --- Project ---
    | AddProject    of project: Project
    | RemoveProject of backup: Project

    // --- System ---
    | AddSystem    of system: DsSystem * projectId: Guid * isActive: bool
    | RemoveSystem of backup: DsSystem * projectId: Guid * isActive: bool

    // --- Flow ---
    | AddFlow    of flow: Flow
    | RemoveFlow of backup: Flow

    // --- Work ---
    | AddWork        of work: Work
    | RemoveWork     of backup: Work
    | MoveWork       of id: Guid * oldPos: Xywh option * newPos: Xywh option
    | RenameWork     of id: Guid * oldName: string * newName: string
    | UpdateWorkProps of id: Guid * oldProps: WorkProperties * newProps: WorkProperties

    // --- Call ---
    | AddCall        of call: Call
    | RemoveCall     of backup: Call
    | MoveCall       of id: Guid * oldPos: Xywh option * newPos: Xywh option
    | RenameCall     of id: Guid * oldName: string * newName: string
    | UpdateCallProps of id: Guid * oldProps: CallProperties * newProps: CallProperties

    // --- Arrow ---
    | AddArrowWork    of arrow: ArrowBetweenWorks
    | RemoveArrowWork of backup: ArrowBetweenWorks
    | AddArrowCall    of arrow: ArrowBetweenCalls
    | RemoveArrowCall of backup: ArrowBetweenCalls
    | ReconnectArrowWork of id: Guid * oldSourceId: Guid * oldTargetId: Guid * newSourceId: Guid * newTargetId: Guid
    | ReconnectArrowCall of id: Guid * oldSourceId: Guid * oldTargetId: Guid * newSourceId: Guid * newTargetId: Guid

    // --- ApiDef ---
    | AddApiDef    of apiDef: ApiDef
    | RemoveApiDef of backup: ApiDef
    | UpdateApiDefProps of id: Guid * oldProps: ApiDefProperties * newProps: ApiDefProperties

    // --- ApiCall ---
    | AddApiCallToCall      of callId: Guid * apiCall: ApiCall
    | RemoveApiCallFromCall of callId: Guid * apiCall: ApiCall
    /// 기존 ApiCall을 다른 Call에서 공유 참조 (store.ApiCalls 변경 없음)
    | AddSharedApiCallToCall      of callId: Guid * apiCallId: Guid
    | RemoveSharedApiCallFromCall of callId: Guid * apiCallId: Guid

    // --- HW Components ---
    | AddButton       of button: HwButton
    | RemoveButton    of backup: HwButton
    | AddLamp         of lamp: HwLamp
    | RemoveLamp      of backup: HwLamp
    | AddHwCondition  of condition: HwCondition
    | RemoveHwCondition of backup: HwCondition
    | AddHwAction     of action: HwAction
    | RemoveHwAction  of backup: HwAction

    // --- 범용 ---
    | RenameEntity of id: Guid * entityType: string * oldName: string * newName: string

    // --- 범용 (캐스케이드 삭제 용) ---
    | Composite of description: string * commands: EditorCommand list

// =============================================================================
// EditorEvent — UI에 발행하는 변경 이벤트 (STRUCTURE.md 4.2)
// =============================================================================

type EditorEvent =
    // --- 엔티티 생명주기 ---
    | ProjectAdded    of Project
    | ProjectRemoved  of Guid
    | SystemAdded     of DsSystem
    | SystemRemoved   of Guid
    | FlowAdded       of Flow
    | FlowRemoved     of Guid
    | WorkAdded       of Work
    | WorkRemoved     of Guid
    | CallAdded       of Call
    | CallRemoved     of Guid
    | ApiDefAdded     of ApiDef
    | ApiDefRemoved   of Guid
    | ArrowWorkAdded    of ArrowBetweenWorks
    | ArrowWorkRemoved  of Guid
    | ArrowCallAdded    of ArrowBetweenCalls
    | ArrowCallRemoved  of Guid

    // --- 프로퍼티 변경 ---
    | EntityRenamed      of id: Guid * newName: string
    | WorkMoved          of id: Guid * newPos: Xywh option
    | CallMoved          of id: Guid * newPos: Xywh option
    | WorkPropsChanged   of id: Guid
    | CallPropsChanged   of id: Guid
    | ApiDefPropsChanged of id: Guid

    // --- HW ---
    | HwComponentAdded   of entityType: string * id: Guid * name: string
    | HwComponentRemoved of entityType: string * id: Guid

    // --- Undo/Redo 상태 ---
    | UndoRedoChanged of canUndo: bool * canRedo: bool

    // --- 전체 갱신 (Composite undo/redo 용) ---
    | StoreRefreshed

[<Sealed>]
type MoveEntityRequest(entityType: string, id: Guid, newPos: Xywh option) =
    member _.EntityType = entityType
    member _.Id = id
    member _.NewPos = newPos

// =============================================================================
// CallCopyContext — Call 복사 시 ApiCall 공유/복제 정책
// =============================================================================

/// Call을 붙여넣을 위치에 따른 복사 컨텍스트.
/// - SameWork    : 동일 Work 내 복사 → ApiCall 공유 (AddSharedApiCallToCall)
/// - DifferentWork: 다른 Work(같은 Flow) 복사 → ApiCall 새 GUID
/// - DifferentFlow: 다른 Flow 복사 → ApiCall 새 GUID
///                  (향후: ApiDef/Device도 별도 생성 필요, 현재는 DifferentWork와 동일)
type CallCopyContext =
    | SameWork
    | DifferentWork
    | DifferentFlow
