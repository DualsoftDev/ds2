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

module CanvasLayoutTests =

    let private node id name parentId x y =
        {
            Id = id
            EntityKind = EntityKind.Work
            Name = name
            ParentId = parentId
            X = x
            Y = y
            Width = 100.0
            Height = 40.0
            ConditionTypes = []
            IsGhost = false
        }

    let private arrow sourceId targetId =
        {
            Id = Guid.NewGuid()
            SourceId = sourceId
            TargetId = targetId
            ArrowType = ArrowType.Start
        }

    [<Fact>]
    let ``computeLayout keeps connected same-layer nodes closer together`` () =
        let rootId = Guid.NewGuid()
        let a = node (Guid.NewGuid()) "A" rootId 0.0 0.0
        let d = node (Guid.NewGuid()) "D" rootId 0.0 220.0
        let b1 = node (Guid.NewGuid()) "B1" rootId 100.0 0.0
        let b3 = node (Guid.NewGuid()) "B3" rootId 100.0 90.0
        let b2 = node (Guid.NewGuid()) "B2" rootId 100.0 220.0
        let c = node (Guid.NewGuid()) "C" rootId 200.0 0.0
        let e = node (Guid.NewGuid()) "E" rootId 200.0 220.0

        let content =
            {
                Nodes = [ a; d; b1; b3; b2; c; e ]
                Arrows =
                    [
                        arrow a.Id b1.Id
                        arrow b1.Id c.Id
                        arrow a.Id b2.Id
                        arrow b2.Id c.Id
                        arrow d.Id b3.Id
                        arrow b3.Id e.Id
                    ]
            }

        let moves = CanvasLayout.computeLayout content
        let positions =
            moves
            |> Seq.map (fun move -> move.Id, move.Position)
            |> Map.ofSeq

        let y1 = float positions.[b1.Id].Y
        let y2 = float positions.[b2.Id].Y
        let y3 = float positions.[b3.Id].Y

        let closeGap = abs (y1 - y2)
        let farGap = min (abs (y1 - y3)) (abs (y2 - y3))

        Assert.True(closeGap < farGap, $"Expected connected nodes to be closer, but closeGap={closeGap}, farGap={farGap}")

    [<Fact>]
    let ``computeLayout places satellite and anchor at the same X with vertical separation`` () =
        let rootId = Guid.NewGuid()
        let satellite = node (Guid.NewGuid()) "P160.RET" rootId 0.0 140.0
        let n1 = node (Guid.NewGuid()) "P162.ADV" rootId 120.0 0.0
        let n2 = node (Guid.NewGuid()) "P163.ADV" rootId 240.0 0.0
        let n3 = node (Guid.NewGuid()) "P164.ADV" rootId 360.0 0.0
        let anchor = node (Guid.NewGuid()) "P167.RET" rootId 480.0 0.0

        let content =
            {
                Nodes = [ satellite; n1; n2; n3; anchor ]
                Arrows =
                    [
                        arrow n1.Id n2.Id
                        arrow n2.Id n3.Id
                        arrow n3.Id anchor.Id
                        arrow satellite.Id anchor.Id
                    ]
            }

        let moves = CanvasLayout.computeLayout content
        let positions =
            moves
            |> Seq.map (fun move -> move.Id, move.Position)
            |> Map.ofSeq

        let satellitePos = positions.[satellite.Id]
        let anchorPos = positions.[anchor.Id]

        Assert.Equal(float anchorPos.X, float satellitePos.X, 5)
        Assert.True(abs (float satellitePos.Y - float anchorPos.Y) >= 90.0, $"Expected vertical satellite separation, but satelliteY={satellitePos.Y}, anchorY={anchorPos.Y}")

    [<Fact>]
    let ``computeLayout keeps snake direction after wrapping to a new row`` () =
        let rootId = Guid.NewGuid()
        let ids = [| for _ in 0..11 -> Guid.NewGuid() |]
        let nodes =
            ids
            |> Array.mapi (fun i id -> node id $"N{i}" rootId (float i * 120.0) 0.0)
            |> Array.toList
        let arrows =
            [| for i in 0..10 -> arrow ids.[i] ids.[i + 1] |]
            |> Array.toList

        let content = { Nodes = nodes; Arrows = arrows }
        let moves = CanvasLayout.computeLayout content
        let positions = moves |> Seq.map (fun move -> move.Id, move.Position) |> Map.ofSeq

        let x0 = float positions.[ids.[0]].X
        let x5 = float positions.[ids.[5]].X
        let x6 = float positions.[ids.[6]].X
        let x11 = float positions.[ids.[11]].X
        let y0 = float positions.[ids.[0]].Y
        let y6 = float positions.[ids.[6]].Y

        Assert.True(x0 < x5, $"Row 0 should go left-to-right: x0={x0}, x5={x5}")
        Assert.True(x6 > x11, $"Row 1 should go right-to-left: x6={x6}, x11={x11}")
        Assert.True(y6 > y0, $"Row 1 should be below row 0: y0={y0}, y6={y6}")

    [<Fact>]
    let ``computeLayout can break rows early to avoid stretching arrows needlessly`` () =
        let rootId = Guid.NewGuid()
        let ids = [| for _ in 0..9 -> Guid.NewGuid() |]
        let nodes =
            ids
            |> Array.mapi (fun i id -> node id $"N{i}" rootId (float i * 120.0) 0.0)
            |> Array.toList
        let arrows =
            [| for i in 0..8 -> arrow ids.[i] ids.[i + 1] |]
            |> Array.toList

        let content = { Nodes = nodes; Arrows = arrows }
        let moves = CanvasLayout.computeLayout content
        let positions = moves |> Seq.map (fun move -> move.Id, move.Position) |> Map.ofSeq

        let x0 = float positions.[ids.[0]].X
        let x4 = float positions.[ids.[4]].X
        let x5 = float positions.[ids.[5]].X
        let x9 = float positions.[ids.[9]].X

        Assert.True(x0 < x4, $"First row should still progress left-to-right: x0={x0}, x4={x4}")
        Assert.Equal(x4, x5, 5)
        Assert.Equal(x0, x9, 5)

    [<Fact>]
    let ``computeLayout spreads cycle work nodes instead of stacking them on one vertical line`` () =
        let rootId = Guid.NewGuid()
        let ids = [| for _ in 0..5 -> Guid.NewGuid() |]
        let nodes =
            ids
            |> Array.mapi (fun i id -> node id $"W{i + 1}" rootId 0.0 (float i * 80.0))
            |> Array.toList
        let arrows =
            [ for i in 0..4 -> arrow ids.[i] ids.[i + 1] ]
            @ [ arrow ids.[5] ids.[0] ]

        let content = { Nodes = nodes; Arrows = arrows }
        let moves = CanvasLayout.computeLayout content
        let positions = moves |> Seq.map (fun move -> move.Id, move.Position) |> Map.ofSeq

        let xs = ids |> Array.map (fun id -> positions.[id].X) |> Array.distinct
        let ys = ids |> Array.map (fun id -> positions.[id].Y) |> Array.distinct
        let minX = ids |> Array.minBy (fun id -> positions.[id].X) |> fun id -> positions.[id].X
        let maxX = ids |> Array.maxBy (fun id -> positions.[id].X) |> fun id -> positions.[id].X

        Assert.True(xs.Length > 1, $"Cycle nodes should not share one X: xs={String.Join(',', xs)}")
        Assert.True(ys.Length > 1, $"Cycle nodes should not collapse on one Y: ys={String.Join(',', ys)}")
        Assert.True(maxX - minX >= 120, $"Cycle nodes should spread horizontally: minX={minX}, maxX={maxX}")

    [<Fact>]
    let ``computeLayout places disconnected work components outside the main flow area`` () =
        let rootId = Guid.NewGuid()
        let a = node (Guid.NewGuid()) "A" rootId 0.0 0.0
        let b = node (Guid.NewGuid()) "B" rootId 120.0 0.0
        let c = node (Guid.NewGuid()) "C" rootId 240.0 0.0
        let d = node (Guid.NewGuid()) "D" rootId 360.0 0.0
        let isolated1 = node (Guid.NewGuid()) "ISO1" rootId 0.0 260.0
        let isolated2 = node (Guid.NewGuid()) "ISO2" rootId 120.0 260.0

        let content =
            {
                Nodes = [ a; b; c; d; isolated1; isolated2 ]
                Arrows =
                    [
                        arrow a.Id b.Id
                        arrow b.Id c.Id
                        arrow c.Id d.Id
                    ]
            }

        let moves = CanvasLayout.computeLayout content
        let positions = moves |> Seq.map (fun move -> move.Id, move.Position) |> Map.ofSeq

        let mainMaxY =
            [ a.Id; b.Id; c.Id; d.Id ]
            |> List.map (fun id -> positions.[id].Y + positions.[id].H)
            |> List.max

        let iso1 = positions.[isolated1.Id]
        let iso2 = positions.[isolated2.Id]

        Assert.True(iso1.Y > mainMaxY, $"Disconnected component should be below main flow: iso1Y={iso1.Y}, mainMaxY={mainMaxY}")
        Assert.True(iso2.Y > mainMaxY, $"Disconnected component should be below main flow: iso2Y={iso2.Y}, mainMaxY={mainMaxY}")


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
    let ``computePath keeps distant same-row nodes nearly straight without obstacles`` () =
        let source = Xywh(0, 0, 100, 60)
        let target = Xywh(280, 0, 100, 60)

        let visual = ArrowPathCalculator.computePath source target
        let points = visual.Points |> Seq.toList

        Assert.Equal(4, points.Length)

        let _, startY = points.[0]
        let _, cp1Y = points.[1]
        let _, cp2Y = points.[2]
        let _, endY = points.[3]

        Assert.Equal(startY, cp1Y, 5)
        Assert.Equal(endY, cp2Y, 5)

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
