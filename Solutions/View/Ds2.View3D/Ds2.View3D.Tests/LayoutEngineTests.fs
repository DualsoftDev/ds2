module Ds2.View3D.Tests.LayoutEngineTests

open System
open Xunit
open Ds2.View3D
open Ds2.View3D.LayoutEngine

let createTestDevice name flowName =
    {
        Id = Guid.NewGuid()
        Name = name
        SystemType = None
        ModelType = "Dummy"
        FlowName = flowName
        ParticipatingFlows = [flowName]
        IsUsedInSimulation = true
        ApiDefs = []
        Position = None
    }

[<Fact>]
let ``groupDevicesByFlow should group devices by FlowName`` () =
    let devices = [
        createTestDevice "Device1" "Flow1"
        createTestDevice "Device2" "Flow1"
        createTestDevice "Device3" "Flow2"
    ]

    let result = groupDevicesByFlow devices

    Assert.Equal(2, result.Count)
    Assert.Equal(2, result.["Flow1"].Length)
    Assert.Equal(1, result.["Flow2"].Length)

[<Fact>]
let ``applyGridLayout should assign positions to devices`` () =
    let devices = [
        createTestDevice "Device1" "Flow1"
        createTestDevice "Device2" "Flow1"
        createTestDevice "Device3" "Flow1"
        createTestDevice "Device4" "Flow1"
    ]

    let result = applyGridLayout devices 0.0 0.0

    Assert.All(result, fun d -> Assert.True(d.Position.IsSome))

[<Fact>]
let ``applyGridLayout should arrange devices in grid pattern`` () =
    let devices = [
        createTestDevice "Device1" "Flow1"
        createTestDevice "Device2" "Flow1"
        createTestDevice "Device3" "Flow1"
        createTestDevice "Device4" "Flow1"
    ]

    let result = applyGridLayout devices 0.0 0.0

    let positions = result |> List.map (fun d -> d.Position.Value)
    Assert.Equal(4, positions.Length)

    Assert.All(positions, fun pos ->
        Assert.True(pos.X >= 0.0)
        Assert.True(pos.Z >= 0.0)
    )

[<Fact>]
let ``generateFlowColor should return gray for Unassigned`` () =
    let result = generateFlowColor "Unassigned"
    Assert.Equal("#808080", result)

[<Fact>]
let ``generateFlowColor should return hex color for named flow`` () =
    let result = generateFlowColor "Flow1"
    Assert.StartsWith("#", result)
    Assert.Equal(7, result.Length)

[<Fact>]
let ``calculateFlowZone should create zone encompassing all devices`` () =
    let devices = [
        { (createTestDevice "D1" "F1") with Position = Some (Position.create 0.0 0.0) }
        { (createTestDevice "D2" "F1") with Position = Some (Position.create 10.0 10.0) }
    ]

    let result = calculateFlowZone "Flow1" devices "#FF0000"

    Assert.Equal("Flow1", result.FlowName)
    Assert.True(result.SizeX > 10.0)
    Assert.True(result.SizeZ > 10.0)
    Assert.Equal("#FF0000", result.Color)

[<Fact>]
let ``calculateFlowZone should create default zone for devices without positions`` () =
    let devices = [
        createTestDevice "D1" "F1"
        createTestDevice "D2" "F1"
    ]

    let result = calculateFlowZone "Flow1" devices "#FF0000"

    Assert.Equal("Flow1", result.FlowName)
    Assert.Equal(10.0, result.SizeX)
    Assert.Equal(10.0, result.SizeZ)

[<Fact>]
let ``arrangeFlowZonesHorizontally should place zones side by side`` () =
    let devices = [
        createTestDevice "D1" "Flow1"
        createTestDevice "D2" "Flow1"
        createTestDevice "D3" "Flow2"
        createTestDevice "D4" "Flow2"
    ]

    let flowGroups = groupDevicesByFlow devices
    let (layoutedDevices, flowZones) = arrangeFlowZonesHorizontally flowGroups

    Assert.Equal(4, layoutedDevices.Length)
    Assert.Equal(2, flowZones.Length)

    Assert.All(layoutedDevices, fun d -> Assert.True(d.Position.IsSome))

    let sortedZones = flowZones |> List.sortBy (fun z -> z.CenterX)
    Assert.True(sortedZones.[0].CenterX < sortedZones.[1].CenterX)

[<Fact>]
let ``applyExistingPositions should restore saved positions`` () =
    let device1 = createTestDevice "D1" "F1"
    let device2 = createTestDevice "D2" "F1"

    let savedPositions =
        Map.ofList [
            (device1.Id, Position.create 100.0 200.0)
        ]

    let result = applyExistingPositions [device1; device2] savedPositions

    Assert.Equal(Some (Position.create 100.0 200.0), result.[0].Position)
    Assert.Equal(None, result.[1].Position)
