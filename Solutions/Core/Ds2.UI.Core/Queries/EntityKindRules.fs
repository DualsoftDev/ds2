namespace Ds2.UI.Core

open System
open Ds2.Core

/// EntityKind 기반 비즈니스 규칙 — UI 프레임워크 중립
module EntityKindRules =

    /// Tree 컨텍스트 메뉴에서 특정 작업이 허용되는지 판정
    let isMenuOperationAllowed (kind: Nullable<EntityKind>) (op: string) (hasProject: bool) (isDeviceTree: bool) : bool =
        let kind = if kind.HasValue then Some kind.Value else None
        match op with
        | "AddSystem"     -> hasProject && (kind = None || kind = Some EntityKind.Project)
        | "AddFlow"       -> kind = Some EntityKind.System
        | "AddWork"       -> kind = Some EntityKind.Flow
        | "AddCall"       -> kind = Some EntityKind.Work
        | "Import"        -> isDeviceTree && (kind = None || kind = Some EntityKind.DeviceRoot)
        | "ImportCsv"     -> kind = None || kind = Some EntityKind.Project || kind = Some EntityKind.DeviceRoot
        | "ExportCsv"     -> hasProject && (kind = None || kind = Some EntityKind.Project || kind = Some EntityKind.DeviceRoot)
        | "ImportMermaid" -> not isDeviceTree && (kind = Some EntityKind.Flow || kind = Some EntityKind.Work)
        | "Copy"          -> kind = Some EntityKind.Flow || kind = Some EntityKind.Work || kind = Some EntityKind.Call
        | "Paste"         -> kind = Some EntityKind.System || kind = Some EntityKind.Flow || kind = Some EntityKind.Work
        | "FocusCanvas"   -> kind = Some EntityKind.Work || kind = Some EntityKind.Call
        | "Rename"        -> kind = Some EntityKind.Project || kind = Some EntityKind.System || kind = Some EntityKind.Flow
                             || kind = Some EntityKind.Work || kind = Some EntityKind.Call
        | "Delete"        -> kind = Some EntityKind.System || kind = Some EntityKind.Flow
                             || kind = Some EntityKind.Work || kind = Some EntityKind.Call
        | _ -> true

    /// Mermaid 임포트 가능한 EntityKind인지
    let canImportMermaid (kind: EntityKind) : bool =
        kind = EntityKind.System || kind = EntityKind.Flow || kind = EntityKind.Work

    /// Canvas에서 더블클릭으로 탭을 열 수 있는 EntityKind인지
    let canOpenAsTab (kind: EntityKind) : bool =
        kind = EntityKind.Work

    /// Canvas에서 드래그 이동 가능한 EntityKind인지
    let isDraggableKind (kind: EntityKind) : bool =
        kind = EntityKind.Work || kind = EntityKind.Call

    /// Work 간 화살표 모드인지 (Work=true → Reset/StartReset/ResetReset 사용 가능)
    let isWorkArrowMode (kind: EntityKind) : bool =
        kind = EntityKind.Work

    /// TabKind에서 화살표 모드 판정 (System/Flow 탭 = Work 간, Work 탭 = Call 간)
    let isWorkArrowModeForTab (tabKind: TabKind) : bool =
        tabKind <> TabKind.Work

    /// Work/Call에 따라 사용 가능한 ArrowType 목록
    let availableArrowTypes (isWorkMode: bool) : ArrowType list =
        if isWorkMode then
            [ ArrowType.Start; ArrowType.Reset; ArrowType.StartReset; ArrowType.ResetReset; ArrowType.Group ]
        else
            [ ArrowType.Start; ArrowType.Group ]

    /// Canvas 좌표를 격자에 정렬
    let snapToGrid (x: float) (y: float) (ctrlHeld: bool) : float * float =
        let gridX = 120.0
        let gridY = 40.0
        if ctrlHeld then
            (max 0.0 x, max 0.0 y)
        else
            let snappedX = System.Math.Round(x / gridX) * gridX |> max 0.0
            let snappedY = System.Math.Round(y / gridY) * gridY |> max 0.0
            (snappedX, snappedY)
