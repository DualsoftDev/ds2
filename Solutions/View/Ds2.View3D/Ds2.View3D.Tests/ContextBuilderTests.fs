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
let ``determinePrimaryFlow should return most frequent flow`` () =
    let flows = ["Flow1"; "Flow1"; "Flow2"]
    shouldEqual (Some "Flow1") (determinePrimaryFlow flows)

[<Fact>]
let ``determinePrimaryFlow should return None for empty list`` () =
    shouldEqual None (determinePrimaryFlow [])

[<Fact>]
let ``extractDevices should return error for non-existent project`` () =
    let (store, _, _, _, _, _) = createTestStore()
    let fakeProjectId = Guid.NewGuid()

    match extractDevices store fakeProjectId with
    | Ok _ -> failwith "Should have failed"
    | Error (ProjectNotFound id) -> shouldEqual fakeProjectId id
    | Error err -> failwithf "Wrong error type: %A" err

// TestHelpers: Robot/Conveyor = Active, Cylinder(Unit)/Unknown = Passive.
// 3D 배치 뷰는 실제 설비(Passive)만 표시하므로 extractDevices 는 Passive 만 반환.

[<Fact>]
let ``extractDevices should return only PassiveSystems`` () =
    let (store, projectId, _, _, cylinderId, unknownId) = createTestStore()

    match extractDevices store projectId with
    | Ok devices ->
        Assert.Equal(2, devices.Length)
        Assert.Contains(devices, fun d -> d.Id = cylinderId && d.ModelType = "Unit")
        Assert.Contains(devices, fun d -> d.Id = unknownId && d.ModelType = "Dummy")
        Assert.DoesNotContain(devices, fun d -> d.ModelType = "Robot")
        Assert.DoesNotContain(devices, fun d -> d.ModelType = "Conveyor")
    | Error err ->
        failwithf "extractDevices failed: %A" err

[<Fact>]
let ``extractDevices should calculate CallerCount for passive device ApiDefs`` () =
    let (store, projectId, _, _, _, _) = createTestStore()

    match extractDevices store projectId with
    | Ok devices ->
        let cylinder = devices |> List.find (fun d -> d.Name = "Cylinder_CY01")
        let extend = cylinder.ApiDefs |> List.find (fun a -> a.Name = "Extend")
        // Extend 는 call4(Flow2)에서 1회 호출됨
        Assert.Equal(1, extend.CallerCount)
    | Error err ->
        failwithf "extractDevices failed: %A" err
