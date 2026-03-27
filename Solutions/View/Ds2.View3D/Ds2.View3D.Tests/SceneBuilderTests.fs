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
    let (store, projectId, robotId, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    // 기존 레이아웃 저장
    let existingLayout = {
        ProjectId = projectId
        Positions = Map.ofList [(robotId, Position.create 999.0 888.0)]
        FlowZones = Map.empty
        Version = 1
    }
    (layoutStore :> ILayoutStore).SaveLayout(existingLayout) |> ignore

    match buildScene store projectId layoutStore with
    | Ok scene ->
        let robot = scene.Devices |> List.find (fun d -> d.Id = robotId)
        Assert.Equal(Some (Position.create 999.0 888.0), robot.Position)
    | Error err ->
        failwithf "Scene build failed: %A" err

[<Fact>]
let ``buildScene should create FlowZones for each flow`` () =
    let (store, projectId, _, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match buildScene store projectId layoutStore with
    | Ok scene ->
        Assert.True(scene.FlowZones.Length >= 2, "Should have at least 2 flow zones")
        Assert.Contains(scene.FlowZones, fun z -> z.FlowName = "Flow1")
        Assert.Contains(scene.FlowZones, fun z -> z.FlowName = "Flow2")
    | Error err ->
        failwithf "Scene build failed: %A" err

[<Fact>]
let ``buildScene should set FlowName on devices`` () =
    let (store, projectId, robotId, _, _, _) = createTestStore()
    let layoutStore = InMemoryLayoutStore()

    match buildScene store projectId layoutStore with
    | Ok scene ->
        let robot = scene.Devices |> List.find (fun d -> d.Id = robotId)
        Assert.True(robot.FlowName = "Flow1" || robot.FlowName = "Flow2",
            sprintf "Robot FlowName should be Flow1 or Flow2, got: %s" robot.FlowName)
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
