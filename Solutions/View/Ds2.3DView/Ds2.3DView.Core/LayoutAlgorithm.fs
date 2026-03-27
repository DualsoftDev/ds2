namespace Ds2.ThreeDView

open System

// =============================================================================
// Layout Algorithm — 순수 함수형 좌표 계산
// =============================================================================

module LayoutAlgorithm =

    // ─────────────────────────────────────────────────────────────────────
    // Constants
    // ─────────────────────────────────────────────────────────────────────

    [<Literal>]
    let private DeviceSpacing = 12.0

    [<Literal>]
    let private FlowGap = 20.0

    [<Literal>]
    let private WorkSpacing = 10.0

    [<Literal>]
    let private FlowLineSpacingZ = 15.0

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// Square grid column count
    let private gridCols (count: int) =
        count |> float |> sqrt |> ceil |> int |> max 1

    /// Place items in grid layout with given spacing
    let private gridLayout (spacing: float) (offsetX: float) (offsetZ: float) (items: 'a list) (getId: 'a -> Guid) (kind: NodeKind) : LayoutPosition list =
        let cols = gridCols items.Length
        items
        |> List.mapi (fun i item ->
            let col = i % cols
            let row = i / cols
            {
                NodeId = getId item
                NodeKind = kind
                X = offsetX + float col * spacing
                Y = 0.0
                Z = offsetZ + float row * spacing
            })

    // ─────────────────────────────────────────────────────────────────────
    // Device Layout (스펙 10.2 — Flow별 그룹 그리드)
    // ─────────────────────────────────────────────────────────────────────

    /// Flow별로 그룹화한 후 각 그룹을 grid 배치, 그룹 간 FlowGap 간격
    let layoutDevices (devices: DeviceNode list) : LayoutPosition list =
        let groups =
            devices
            |> List.groupBy (fun d -> d.FlowName)
            |> List.sortBy fst

        let mutable offsetX = 0.0
        [
            for (_flowName, devs) in groups do
                let sorted = devs |> List.sortBy (fun d -> d.Name)
                let cols = gridCols sorted.Length
                let positions = gridLayout DeviceSpacing offsetX 0.0 sorted (fun d -> d.Id) NodeKind.DeviceNode
                yield! positions
                offsetX <- offsetX + float cols * DeviceSpacing + FlowGap
        ]

    // ─────────────────────────────────────────────────────────────────────
    // Work Layout (스펙 10.1 — Flow 수에 따라 분기)
    // ─────────────────────────────────────────────────────────────────────

    /// 1 flow: square grid, 2 flows: parallel lines, 3+: zone grid
    let layoutWorks (works: WorkNode list) : LayoutPosition list =
        let flowGroups =
            works
            |> List.groupBy (fun w -> w.FlowName)
            |> List.sortBy fst

        match flowGroups.Length with
        | 0 -> []

        // Single flow — square grid
        | 1 ->
            let (_, ws) = flowGroups.[0]
            gridLayout WorkSpacing 0.0 0.0 ws (fun w -> w.Id) NodeKind.WorkNode

        // Two flows — parallel horizontal lines
        | 2 ->
            [
                for fi, (_flowName, ws) in flowGroups |> List.indexed do
                    let sorted = ws |> List.sortBy (fun w -> w.Name)
                    let z = float fi * FlowLineSpacingZ
                    for wi, w in sorted |> List.indexed do
                        yield
                            {
                                NodeId = w.Id
                                NodeKind = NodeKind.WorkNode
                                X = float wi * WorkSpacing
                                Y = 0.0
                                Z = z
                            }
            ]

        // 3+ flows — zone grid (each flow gets a square grid, zones stacked in Z)
        | _ ->
            let mutable offsetZ = 0.0
            [
                for (_flowName, ws) in flowGroups do
                    let sorted = ws |> List.sortBy (fun w -> w.Name)
                    let cols = gridCols sorted.Length
                    let rows = (sorted.Length + cols - 1) / cols
                    let positions = gridLayout WorkSpacing 0.0 offsetZ sorted (fun w -> w.Id) NodeKind.WorkNode
                    yield! positions
                    offsetZ <- offsetZ + float rows * WorkSpacing + FlowGap
            ]

    // ─────────────────────────────────────────────────────────────────────
    // Flow Zone 계산
    // ─────────────────────────────────────────────────────────────────────

    /// 색상 팔레트
    let private zoneColors =
        [| "#4285F4"; "#EA4335"; "#FBBC05"; "#34A853"; "#FF6D01"; "#46BDC6"; "#7B1FA2"; "#C2185B" |]

    /// 배치된 위치에서 FlowZone 바운딩 박스 생성
    let computeFlowZones (devices: DeviceNode list) (positions: LayoutPosition list) : FlowZone list =
        let posMap =
            positions |> List.map (fun p -> p.NodeId, p) |> Map.ofList

        devices
        |> List.groupBy (fun d -> d.FlowName)
        |> List.sortBy fst
        |> List.mapi (fun i (flowName, devs) ->
            let devPositions =
                devs
                |> List.choose (fun d -> posMap |> Map.tryFind d.Id)

            match devPositions with
            | [] ->
                { FlowName = flowName; CenterX = 0.0; CenterZ = 0.0; SizeX = 10.0; SizeZ = 10.0
                  Color = zoneColors.[i % zoneColors.Length] }
            | ps ->
                let xs = ps |> List.map (fun p -> p.X)
                let zs = ps |> List.map (fun p -> p.Z)
                let minX = List.min xs - DeviceSpacing / 2.0
                let maxX = List.max xs + DeviceSpacing / 2.0
                let minZ = List.min zs - DeviceSpacing / 2.0
                let maxZ = List.max zs + DeviceSpacing / 2.0
                {
                    FlowName = flowName
                    CenterX = (minX + maxX) / 2.0
                    CenterZ = (minZ + maxZ) / 2.0
                    SizeX = maxX - minX
                    SizeZ = maxZ - minZ
                    Color = zoneColors.[i % zoneColors.Length]
                })
