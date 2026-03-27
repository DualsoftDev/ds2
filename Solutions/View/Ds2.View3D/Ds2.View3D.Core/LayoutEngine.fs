module Ds2.View3D.LayoutEngine

open System
open Ds2.View3D

/// Device를 FlowName별로 그룹화
let groupDevicesByFlow (devices: DeviceInfo list) : Map<string, DeviceInfo list> =
    devices
    |> List.groupBy (fun d -> d.FlowName)
    |> Map.ofList

/// Grid Layout으로 Device 배치
let applyGridLayout (devices: DeviceInfo list) (startX: float) (startZ: float) : DeviceInfo list =
    let spacing = 10.0
    let deviceCount = float devices.Length
    let cols = ceil (sqrt deviceCount) |> int

    devices
    |> List.mapi (fun i device ->
        let col = i % cols
        let row = i / cols
        let x = startX + float col * spacing
        let z = startZ + float row * spacing
        { device with Position = Some (Position.create x z) }
    )

/// HSL을 RGB로 변환
let private hslToRgb (h: int) (s: int) (l: int) : int * int * int =
    let h' = float h / 360.0
    let s' = float s / 100.0
    let l' = float l / 100.0

    let c = (1.0 - abs(2.0 * l' - 1.0)) * s'
    let x = c * (1.0 - abs((h' * 6.0) % 2.0 - 1.0))
    let m = l' - c / 2.0

    let (r', g', b') =
        match int (h' * 6.0) with
        | 0 -> (c, x, 0.0)
        | 1 -> (x, c, 0.0)
        | 2 -> (0.0, c, x)
        | 3 -> (0.0, x, c)
        | 4 -> (x, 0.0, c)
        | _ -> (c, 0.0, x)

    let r = int ((r' + m) * 255.0)
    let g = int ((g' + m) * 255.0)
    let b = int ((b' + m) * 255.0)

    (r, g, b)

/// Flow 이름으로부터 자동 색상 생성
let generateFlowColor (flowName: string) : string =
    if flowName = "Unassigned" then
        "#808080"
    else
        let hash = flowName.GetHashCode()
        let hue = abs hash % 360
        let saturation = 70
        let lightness = 60

        let (r, g, b) = hslToRgb hue saturation lightness
        sprintf "#%02X%02X%02X" r g b

/// Device 위치로부터 FlowZone 경계 계산
let calculateFlowZone (flowName: string) (devices: DeviceInfo list) (color: string) : FlowZone =
    let positions = devices |> List.choose (fun d -> d.Position)

    match positions with
    | [] ->
        { FlowName = flowName; CenterX = 0.0; CenterZ = 0.0; SizeX = 10.0; SizeZ = 10.0; Color = color }
    | _ ->
        let xs = positions |> List.map (fun p -> p.X)
        let zs = positions |> List.map (fun p -> p.Z)
        let minX, maxX = List.min xs, List.max xs
        let minZ, maxZ = List.min zs, List.max zs

        let margin = 5.0
        let sizeX = maxX - minX + margin * 2.0
        let sizeZ = maxZ - minZ + margin * 2.0
        let centerX = (minX + maxX) / 2.0
        let centerZ = (minZ + maxZ) / 2.0

        { FlowName = flowName; CenterX = centerX; CenterZ = centerZ; SizeX = sizeX; SizeZ = sizeZ; Color = color }

/// FlowZone을 수평으로 배치 (함수형 스타일)
let arrangeFlowZonesHorizontally (flowGroups: Map<string, DeviceInfo list>) : DeviceInfo list * FlowZone list =
    let zonePadding = 20.0
    let startX = -30.0

    let sortedGroups =
        flowGroups
        |> Map.toList
        |> List.sortBy (fun (flowName, _) ->
            if flowName = "Unassigned" then "~~~~~" else flowName
        )

    let (allDevices, flowZones, _) =
        sortedGroups
        |> List.fold (fun (accDevices, accZones, currentX) (flowName, devices) ->
            Log.info "Laying out FlowZone: %s (%d devices) at X=%.1f" flowName devices.Length currentX

            let layoutedDevices = applyGridLayout devices currentX 0.0
            let color = generateFlowColor flowName
            let zone = calculateFlowZone flowName layoutedDevices color
            let nextX = currentX + zone.SizeX + zonePadding

            (accDevices @ layoutedDevices, zone :: accZones, nextX)
        ) ([], [], startX)

    match flowZones with
    | [] -> Log.info "No FlowZones created"
    | zones ->
        let maxX = zones |> List.map (fun z -> z.CenterX + z.SizeX / 2.0) |> List.max
        Log.info "Layout range: X=%.1f to X=%.1f (width: %.1f)" startX maxX (maxX - startX)

    (allDevices, List.rev flowZones)

/// 기존 레이아웃 Position 적용
let applyExistingPositions (devices: DeviceInfo list) (positions: Map<Guid, Position>) : DeviceInfo list =
    devices
    |> List.map (fun d ->
        match Map.tryFind d.Id positions with
        | Some pos -> { d with Position = Some pos }
        | None -> d
    )
