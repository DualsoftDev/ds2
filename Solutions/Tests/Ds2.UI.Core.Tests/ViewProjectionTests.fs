module Ds2.UI.Core.Tests.ViewProjectionTests

open System
open Xunit
open Ds2.Core
open Ds2.UI.Core
open Ds2.UI.Core.Tests.TestHelpers

module TreeProjectionTests =

    [<Fact>]
    let ``buildTrees returns active and passive trees`` () =
        let store = createStore ()
        let project = addProject store "P"
        let _ = addSystem store "Active" project.Id true
        let _ = addSystem store "Passive" project.Id false
        let activeTrees, passiveTrees = store.BuildTrees()
        Assert.NotEmpty(activeTrees)
        Assert.NotEmpty(passiveTrees)

    [<Fact>]
    let ``buildTrees includes nested children`` () =
        let store = createStore ()
        let project, system, _, _ = setupBasicHierarchy store
        let activeTrees, _ = store.BuildTrees()
        let projectNode = activeTrees |> List.find (fun n -> n.Id = project.Id)
        Assert.NotEmpty(projectNode.Children)
        let systemNode = projectNode.Children |> List.find (fun n -> n.Id = system.Id)
        Assert.NotEmpty(systemNode.Children)

module CanvasProjectionTests =

    [<Fact>]
    let ``CanvasContentForTab returns works for system tab`` () =
        let store = createStore ()
        let _, system, _, _ = setupBasicHierarchy store
        let content = store.CanvasContentForTab(TabKind.System, system.Id)
        Assert.NotEmpty(content.Nodes)

    [<Fact>]
    let ``CanvasContentForTab returns calls for flow tab`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store
        store.AddCallsWithDevice(Guid.Empty, work.Id, [ "Dev.Api" ], false)
        let content = store.CanvasContentForTab(TabKind.Flow, flow.Id)
        Assert.NotEmpty(content.Nodes)

module ArrowPathCalculatorTests =

    [<Fact>]
    let ``chooseDockPoints uses edge intersection instead of fixed face midpoint`` () =
        let source = Xywh(0, 0, 100, 100)
        let target = Xywh(200, 100, 100, 100)

        let (sx, sy), (tx, ty) = ArrowPathCalculator.chooseDockPoints source target

        Assert.Equal(100.0, sx, 5)
        Assert.Equal(75.0, sy, 5)
        Assert.Equal(200.0, tx, 5)
        Assert.Equal(125.0, ty, 5)

    [<Fact>]
    let ``chooseDockPoints can connect through matching rectangle corners`` () =
        let source = Xywh(0, 0, 100, 100)
        let target = Xywh(200, 200, 100, 100)

        let (sx, sy), (tx, ty) = ArrowPathCalculator.chooseDockPoints source target

        Assert.Equal(100.0, sx, 5)
        Assert.Equal(100.0, sy, 5)
        Assert.Equal(200.0, tx, 5)
        Assert.Equal(200.0, ty, 5)

    [<Fact>]
    let ``computePath starts and ends on the chosen dock points`` () =
        let source = Xywh(0, 0, 100, 60)
        let target = Xywh(280, 0, 100, 60)

        let (dockStartX, dockStartY), (dockEndX, dockEndY) =
            ArrowPathCalculator.chooseDockPoints source target

        let visual = ArrowPathCalculator.computePath source target
        let points = visual.Points |> Seq.toList

        Assert.Equal(4, points.Length)

        let startX, startY = points.[0]
        let endX, endY = points.[3]

        Assert.Equal(dockStartX, startX, 5)
        Assert.Equal(dockStartY, startY, 5)
        Assert.Equal(dockEndX, endX, 5)
        Assert.Equal(dockEndY, endY, 5)

    [<Fact>]
    let ``computePath keeps nearby same-row nodes almost straight`` () =
        let source = Xywh(0, 0, 100, 60)
        let target = Xywh(130, 0, 100, 60)

        let visual = ArrowPathCalculator.computePath source target
        let points = visual.Points |> Seq.toList

        Assert.Equal(4, points.Length)

        let _, startY = points.[0]
        let _, cp1Y = points.[1]
        let _, cp2Y = points.[2]
        let _, endY = points.[3]

        Assert.Equal(startY, cp1Y, 5)
        Assert.Equal(endY, cp2Y, 5)

    [<Fact>]
    let ``computePath keeps curve lift for distant same-row nodes`` () =
        let source = Xywh(0, 0, 100, 60)
        let target = Xywh(280, 0, 100, 60)

        let visual = ArrowPathCalculator.computePath source target
        let points = visual.Points |> Seq.toList

        Assert.Equal(4, points.Length)

        let _, startY = points.[0]
        let _, cp1Y = points.[1]
        let _, cp2Y = points.[2]
        let _, endY = points.[3]

        Assert.NotEqual(startY, cp1Y)
        Assert.NotEqual(endY, cp2Y)

module PropertyPanelValueSpecTests =

    [<Fact>]
    let ``format roundtrips through tryParseAs for int`` () =
        let spec = ValueSpec.singleInt32 42
        let text = PropertyPanelValueSpec.format spec
        let parsed = PropertyPanelValueSpec.tryParseAs spec text
        Assert.Equal(Some spec, parsed)

    [<Fact>]
    let ``format roundtrips through tryParseAs for float`` () =
        let spec = ValueSpec.singleFloat64 3.14
        let text = PropertyPanelValueSpec.format spec
        let parsed = PropertyPanelValueSpec.tryParseAs spec text
        Assert.Equal(Some spec, parsed)

    [<Fact>]
    let ``format keeps NaN token stable for float64`` () =
        let text = PropertyPanelValueSpec.format (ValueSpec.singleFloat64 Double.NaN)
        Assert.Equal("NaN", text)

        let parsed = PropertyPanelValueSpec.tryParseAs (ValueSpec.singleFloat64 0.0) text
        match parsed with
        | Some (Float64Value (Single value)) -> Assert.True(Double.IsNaN(value))
        | _ -> Assert.Fail("Expected Float64 NaN")

    [<Fact>]
    let ``format keeps Infinity token stable for float64`` () =
        let text = PropertyPanelValueSpec.format (ValueSpec.singleFloat64 Double.PositiveInfinity)
        Assert.Equal("Infinity", text)

        let parsed = PropertyPanelValueSpec.tryParseAs (ValueSpec.singleFloat64 0.0) text
        match parsed with
        | Some (Float64Value (Single value)) -> Assert.True(Double.IsPositiveInfinity(value))
        | _ -> Assert.Fail("Expected Float64 Infinity")

    [<Fact>]
    let ``dataTypeIndex maps to correct index`` () =
        Assert.Equal(0, PropertyPanelValueSpec.dataTypeIndex UndefinedValue)
        Assert.Equal(4, PropertyPanelValueSpec.dataTypeIndex (Int32Value (Single 0)))
        Assert.Equal(12, PropertyPanelValueSpec.dataTypeIndex (StringValue (Single "")))

    [<Fact>]
    let ``specFromTypeIndex returns correct default spec`` () =
        let spec = PropertyPanelValueSpec.specFromTypeIndex 4
        match spec with
        | Int32Value (Single v) -> Assert.Equal(0, v)
        | _ -> Assert.Fail("Expected Int32Value")
