module Ds2.View3D.Tests.ContextBuilderTests

open System
open Xunit
open Ds2.View3D
open Ds2.View3D.ContextBuilder
open Ds2.View3D.Tests.TestHelpers

[<Fact>]
let ``inferModelType should return Robot for Robot`` () =
    shouldEqual "Robot" (inferModelType (Some "Robot"))

[<Fact>]
let ``inferModelType should return Unit for Unit`` () =
    shouldEqual "Unit" (inferModelType (Some "Unit"))

[<Fact>]
let ``inferModelType should return Conveyor for Conveyor`` () =
    shouldEqual "Conveyor" (inferModelType (Some "Conveyor"))

[<Fact>]
let ``inferModelType should return Dummy for unknown type`` () =
    shouldEqual "Dummy" (inferModelType (Some "UNKNOWN_DEVICE"))

[<Fact>]
let ``inferModelType should return Dummy when SystemType is None`` () =
    shouldEqual "Dummy" (inferModelType None)

[<Fact>]
let ``extractParticipatingFlows should return flows using the device`` () =
    let (store, projectId, _, _, _, _) = createTestStore()

    match extractDevices store projectId with
    | Ok devices ->
        let robot = devices |> List.find (fun d -> d.Name = "Robot_RB01")
        shouldNotBeEmpty robot.ParticipatingFlows
        shouldContain "Flow1" robot.ParticipatingFlows
        shouldContain "Flow2" robot.ParticipatingFlows
    | Error err ->
        failwithf "extractDevices failed: %A" err

[<Fact>]
let ``extractParticipatingFlows should return single flow for conveyor`` () =
    let (store, projectId, _, _, _, _) = createTestStore()

    match extractDevices store projectId with
    | Ok devices ->
        let conveyor = devices |> List.find (fun d -> d.Name = "Conveyor_CV01")
        shouldEqual ["Flow1"] conveyor.ParticipatingFlows
    | Error err ->
        failwithf "extractDevices failed: %A" err

[<Fact>]
let ``determinePrimaryFlow should return most frequent flow`` () =
    let flows = ["Flow1"; "Flow1"; "Flow2"]
    shouldEqual (Some "Flow1") (determinePrimaryFlow flows)

[<Fact>]
let ``determinePrimaryFlow should return None for empty list`` () =
    shouldEqual None (determinePrimaryFlow [])

[<Fact>]
let ``extractDevices should return all systems in project`` () =
    let (store, projectId, _, _, _, _) = createTestStore()

    match extractDevices store projectId with
    | Ok devices ->
        Assert.Equal(4, devices.Length)
        Assert.Contains(devices, fun d -> d.ModelType = "Robot")
        Assert.Contains(devices, fun d -> d.ModelType = "Conveyor")
        Assert.Contains(devices, fun d -> d.ModelType = "Unit")
        Assert.Contains(devices, fun d -> d.ModelType = "Dummy")
    | Error err ->
        failwithf "extractDevices failed: %A" err

[<Fact>]
let ``extractDevices should return error for non-existent project`` () =
    let (store, _, _, _, _, _) = createTestStore()
    let fakeProjectId = Guid.NewGuid()

    match extractDevices store fakeProjectId with
    | Ok _ -> failwith "Should have failed"
    | Error (ProjectNotFound id) -> shouldEqual fakeProjectId id
    | Error err -> failwithf "Wrong error type: %A" err

[<Fact>]
let ``extractDevices should calculate CallerCount for ApiDefs`` () =
    let (store, projectId, _, _, _, _) = createTestStore()

    match extractDevices store projectId with
    | Ok devices ->
        let robot = devices |> List.find (fun d -> d.Name = "Robot_RB01")
        let pick = robot.ApiDefs |> List.find (fun a -> a.Name = "Pick")
        // Pick은 call1(Flow1)과 call3(Flow2)에서 호출됨 → CallerCount = 2
        Assert.Equal(2, pick.CallerCount)

        let place = robot.ApiDefs |> List.find (fun a -> a.Name = "Place")
        // Place는 아무 Call에서도 호출되지 않음 → CallerCount = 0
        Assert.Equal(0, place.CallerCount)
    | Error err ->
        failwithf "extractDevices failed: %A" err
