module Ds2.View3D.Tests.SceneBuilderTests

open System
open Xunit
open Ds2.View3D
open Ds2.View3D.SceneBuilder
open Ds2.View3D.Persistence
open Ds2.View3D.Tests.TestHelpers

[<Fact>]
let ``buildScene should create valid scene with devices and zones`` () =
    let (store, projectId, _, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match buildScene store projectId layoutStore with
    | Ok scene ->
        Assert.True(scene.Devices.Length > 0)
        Assert.True(scene.FlowZones.Length > 0)
    | Error err ->
        failwithf "Scene build failed: %A" err

[<Fact>]
let ``buildScene should assign positions with auto layout`` () =
    let (store, projectId, _, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match buildScene store projectId layoutStore with
    | Ok scene ->
        Assert.All(scene.Devices, fun d ->
            Assert.True(d.Position.IsSome, sprintf "Device %s has no position" d.Name)
        )
    | Error err ->
        failwithf "Scene build failed: %A" err

[<Fact>]
let ``buildScene should restore positions from layout store`` () =
    let (store, projectId, _, _, cylinderId, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    let existingLayout = {
        ProjectId = projectId
        Positions = Map.ofList [(cylinderId, Position.create 999.0 888.0)]
        FlowZones = Map.empty
        Version = 1
    }
    (layoutStore :> ILayoutStore).SaveLayout(existingLayout) |> ignore

    match buildScene store projectId layoutStore with
    | Ok scene ->
        let cylinder = scene.Devices |> List.find (fun d -> d.Id = cylinderId)
        Assert.Equal(Some (Position.create 999.0 888.0), cylinder.Position)
    | Error err ->
        failwithf "Scene build failed: %A" err

[<Fact>]
let ``buildScene should create FlowZones for passive device flows`` () =
    // Cylinder 는 Flow2 에 참여, Unknown 은 Flow 참여 없음 → "Unassigned" Zone
    let (store, projectId, _, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match buildScene store projectId layoutStore with
    | Ok scene ->
        Assert.True(scene.FlowZones.Length >= 1, "Should have at least 1 flow zone")
        Assert.Contains(scene.FlowZones, fun z -> z.FlowName = "Flow2")
        Assert.Contains(scene.FlowZones, fun z -> z.FlowName = "Unassigned")
    | Error err ->
        failwithf "Scene build failed: %A" err

[<Fact>]
let ``buildScene should set FlowName on passive devices`` () =
    let (store, projectId, _, _, cylinderId, unknownId) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match buildScene store projectId layoutStore with
    | Ok scene ->
        let cylinder = scene.Devices |> List.find (fun d -> d.Id = cylinderId)
        Assert.Equal("Flow2", cylinder.FlowName)
        let unknown = scene.Devices |> List.find (fun d -> d.Id = unknownId)
        Assert.Equal("Unassigned", unknown.FlowName)
    | Error err ->
        failwithf "Scene build failed: %A" err

[<Fact>]
let ``buildScene should fail for non-existent project`` () =
    let (store, _, _, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()
    let fakeProjectId = Guid.NewGuid()

    match buildScene store fakeProjectId layoutStore with
    | Ok _ -> failwith "Should have failed"
    | Error (ProjectNotFound id) -> Assert.Equal(fakeProjectId, id)
    | Error err -> failwithf "Wrong error type: %A" err

[<Fact>]
let ``updateDevicePosition should update specific device position`` () =
    let device1 = {
        Id = Guid.NewGuid()
        Name = "D1"
        SystemType = None
        ModelType = "Dummy"
        FlowName = "Flow1"
        ParticipatingFlows = []
        IsUsedInSimulation = false
        ApiDefs = []
        Position = Some (Position.create 0.0 0.0)
    }
    let device2 = {
        Id = Guid.NewGuid()
        Name = "D2"
        SystemType = None
        ModelType = "Dummy"
        FlowName = "Flow1"
        ParticipatingFlows = []
        IsUsedInSimulation = false
        ApiDefs = []
        Position = Some (Position.create 10.0 10.0)
    }

    let newPosition = Position.create 100.0 200.0
    let result = updateDevicePosition [device1; device2] device1.Id newPosition

    Assert.Equal(Some newPosition, result.[0].Position)
    Assert.Equal(Some (Position.create 10.0 10.0), result.[1].Position)

[<Fact>]
let ``buildSceneAutoLayout should ignore stored layout`` () =
    let (store, projectId, _, _, cylinderId, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    // 저장된 layout 에 cylinder 를 특이 좌표로 고정
    let storedPosition = Position.create 999.0 888.0
    let stored = {
        ProjectId = projectId
        Positions = Map.ofList [(cylinderId, storedPosition)]
        FlowZones = Map.empty
        Version = 1
    }
    (layoutStore :> ILayoutStore).SaveLayout(stored) |> ignore

    match buildSceneAutoLayout store projectId layoutStore with
    | Ok scene ->
        let cylinder = scene.Devices |> List.find (fun d -> d.Id = cylinderId)
        // 저장된 (999, 888) 좌표가 아닌 자동 배치 결과여야 함
        Assert.NotEqual(Some storedPosition, cylinder.Position)
        Assert.True(cylinder.Position.IsSome, "Device should have auto-layout position")
    | Error err ->
        failwithf "buildSceneAutoLayout failed: %A" err

[<Fact>]
let ``buildSceneAutoLayout FlowZones should cover device positions`` () =
    let (store, projectId, _, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match buildSceneAutoLayout store projectId layoutStore with
    | Ok scene ->
        // 각 Flow Zone 이 자기 Flow 소속 device 의 bounding box 를 감싸야 함
        for zone in scene.FlowZones do
            let zoneDevs =
                scene.Devices
                |> List.filter (fun d -> d.FlowName = zone.FlowName)
                |> List.choose (fun d -> d.Position)
            if not zoneDevs.IsEmpty then
                let minX = zoneDevs |> List.map (fun p -> p.X) |> List.min
                let maxX = zoneDevs |> List.map (fun p -> p.X) |> List.max
                let minZ = zoneDevs |> List.map (fun p -> p.Z) |> List.min
                let maxZ = zoneDevs |> List.map (fun p -> p.Z) |> List.max
                let zoneMinX = zone.CenterX - zone.SizeX / 2.0
                let zoneMaxX = zone.CenterX + zone.SizeX / 2.0
                let zoneMinZ = zone.CenterZ - zone.SizeZ / 2.0
                let zoneMaxZ = zone.CenterZ + zone.SizeZ / 2.0
                Assert.True(minX >= zoneMinX, sprintf "minX %f < zoneMinX %f for %s" minX zoneMinX zone.FlowName)
                Assert.True(maxX <= zoneMaxX, sprintf "maxX %f > zoneMaxX %f for %s" maxX zoneMaxX zone.FlowName)
                Assert.True(minZ >= zoneMinZ, sprintf "minZ %f < zoneMinZ %f for %s" minZ zoneMinZ zone.FlowName)
                Assert.True(maxZ <= zoneMaxZ, sprintf "maxZ %f > zoneMaxZ %f for %s" maxZ zoneMaxZ zone.FlowName)
    | Error err ->
        failwithf "buildSceneAutoLayout failed: %A" err

[<Fact>]
let ``saveLayout should persist device positions`` () =
    let (store, projectId, _, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match saveLayout store projectId layoutStore with
    | Ok () ->
        match (layoutStore :> ILayoutStore).LoadLayout(projectId) with
        | Ok (Some layout) ->
            Assert.Equal(projectId, layout.ProjectId)
            Assert.Equal(1, layout.Version)
        | _ -> failwith "Layout not saved"
    | Error err ->
        failwithf "saveLayout failed: %A" err
