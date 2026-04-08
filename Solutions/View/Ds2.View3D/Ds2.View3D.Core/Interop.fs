namespace Ds2.View3D

open System
open Ds2.Core.Store
open Ds2.View3D

/// C#에서 호출 가능한 Scene 빌더
type SceneEngine(store: DsStore, layoutStore: ILayoutStore) =

    let mutable selectedDeviceId = None
    let mutable selectedApiDefName = None
    let lockObj = obj()

    /// Device Scene 빌드
    member _.BuildDeviceScene(sceneId: string, projectId: Guid) : SceneData =
        Log.info "SceneEngine.BuildDeviceScene: %s, %A" sceneId projectId

        match SceneBuilder.buildScene store projectId layoutStore with
        | Ok scene ->
            Log.info "Scene built successfully: %d devices, %d zones" scene.Devices.Length scene.FlowZones.Length
            scene
        | Error err ->
            Log.error "Failed to build scene: %A" err
            SceneData.empty

    /// Layout 저장
    member _.SaveLayout(projectId: Guid, positions: (Guid * float * float) list) : unit =
        Log.info "SceneEngine.SaveLayout: %A (%d positions)" projectId positions.Length

        let positionMap =
            positions
            |> List.map (fun (deviceId, x, z) -> (deviceId, Position.create x z))
            |> Map.ofList

        let layout = {
            ProjectId = projectId
            Positions = positionMap
            FlowZones = Map.empty
            Version = 1
        }

        match layoutStore.SaveLayout layout with
        | Ok () -> Log.info "Layout saved successfully"
        | Error ex -> Log.error "Failed to save layout: %s" ex.Message

    /// Selection 처리 (thread-safe)
    member _.Select(event: SelectionEvent) : unit =
        lock lockObj (fun () ->
            match event with
            | DeviceSelected deviceId ->
                selectedDeviceId <- Some deviceId
                selectedApiDefName <- None
                Log.debug "Device selected: %A" deviceId
            | ApiDefSelected (deviceId, apiDefName) ->
                selectedDeviceId <- Some deviceId
                selectedApiDefName <- Some apiDefName
                Log.debug "ApiDef selected: %A, %s" deviceId apiDefName
        )

    /// Selection 클리어 (thread-safe)
    member _.ClearSelection() : unit =
        lock lockObj (fun () ->
            selectedDeviceId <- None
            selectedApiDefName <- None
            Log.debug "Selection cleared"
        )

    /// 현재 선택된 Device ID (thread-safe)
    member _.SelectedDeviceId : Guid option =
        lock lockObj (fun () -> selectedDeviceId)

    /// 현재 선택된 ApiDef Name (thread-safe)
    member _.SelectedApiDefName : string option =
        lock lockObj (fun () -> selectedApiDefName)
