module Ds2.View3D.SceneBuilder

open System
open Ds2.Core.Store
open Ds2.View3D
open Ds2.View3D.ResultExtensions

/// Scene 빌드 (Auto Layout 적용)
let buildScene (store: DsStore) (projectId: Guid) (layoutStore: ILayoutStore)
    : Result<SceneData, SceneError> =
    result {
        Log.info "Building scene for project: %A" projectId

        // 1. Device 추출
        let! devices = ContextBuilder.extractDevices store projectId
        Log.info "Extracted %d devices" devices.Length

        // 2. 기존 레이아웃 로드 시도
        let! existingLayout =
            match layoutStore.LoadLayout projectId with
            | Ok (Some layout) -> Ok (Some layout)
            | Ok None -> Ok None
            | Error ex -> Error (LayoutStoreError ex)

        // 3. Position 복원 또는 Auto Layout
        let devicesWithPosition =
            match existingLayout with
            | Some layout ->
                Log.info "Applying existing layout (%d positions)" layout.Positions.Count
                LayoutEngine.applyExistingPositions devices layout.Positions
            | None ->
                Log.info "No existing layout, running auto layout"
                let flowGroups = LayoutEngine.groupDevicesByFlow devices
                let (layoutedDevices, _) = LayoutEngine.arrangeFlowZonesHorizontally flowGroups
                layoutedDevices

        // 4. FlowZone 계산
        let flowGroups = LayoutEngine.groupDevicesByFlow devicesWithPosition
        Log.info "Created %d flow groups" flowGroups.Count

        let flowZones =
            flowGroups
            |> Map.toList
            |> List.map (fun (flowName, devs) ->
                let color = LayoutEngine.generateFlowColor flowName
                LayoutEngine.calculateFlowZone flowName devs color
            )

        Log.info "Calculated %d flow zones" flowZones.Length

        // 5. SceneData 반환
        return {
            Devices = devicesWithPosition
            FlowZones = flowZones
        }
    }

/// Layout 저장
let saveLayout (store: DsStore) (projectId: Guid) (layoutStore: ILayoutStore) : Result<unit, SceneError> =
    result {
        let! devices = ContextBuilder.extractDevices store projectId

        let positions =
            devices
            |> List.choose (fun d ->
                d.Position |> Option.map (fun pos -> (d.Id, pos))
            )
            |> Map.ofList

        let layout = {
            ProjectId = projectId
            Positions = positions
            FlowZones = Map.empty
            Version = 1
        }

        match layoutStore.SaveLayout layout with
        | Ok () -> return ()
        | Error ex -> return! Error (LayoutStoreError ex)
    }

/// Device Position 업데이트
let updateDevicePosition (devices: DeviceInfo list) (deviceId: Guid) (newPosition: Position) : DeviceInfo list =
    devices
    |> List.map (fun d ->
        if d.Id = deviceId then
            { d with Position = Some newPosition }
        else
            d
    )
