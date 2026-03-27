module Ds2.Store.Editor.Tests.EditorProjectionTests

open System
open Xunit
open Ds2.Core
open Ds2.Store
open Ds2.Editor
open Ds2.Store.Editor.Tests.TestHelpers

module TreeProjectionTests =

    [<Fact>]
    let ``EditorTreeProjection builds control roots as active systems directly`` () =
        let store = createStore ()
        let project = addProject store "Project"
        let activeSystem = addSystem store "ActiveSystem" project.Id true
        let passiveSystem = addSystem store "PassiveSystem" project.Id false
        let flow = addFlow store "Flow1" activeSystem.Id
        addWork store "Work1" flow.Id |> ignore
        addApiDef store "PassiveApi" passiveSystem.Id |> ignore

        let controlRoots, deviceRoots = EditorTreeProjection.buildTrees store
        let controlRoot = Assert.Single(controlRoots)
        let deviceRoot = Assert.Single(deviceRoots)

        Assert.Equal(activeSystem.Id, controlRoot.Id)
        Assert.Equal(EntityKind.System, controlRoot.EntityKind)
        Assert.True(controlRoot.ParentId.IsNone)
        Assert.Contains(controlRoot.Children, fun node -> node.Id = flow.Id)
        Assert.Contains(deviceRoot.Children, fun node -> node.Id = passiveSystem.Id)

module NavigationTests =

    [<Fact>]
    let ``EditorNavigation resolves work tab and parent flow tab`` () =
        let store = createStore ()
        let _, _, flow, work = setupBasicHierarchy store

        let workTab = EditorNavigation.tryOpenTabForEntity store EntityKind.Work work.Id
        let parentTab = EditorNavigation.tryOpenParentTab store EntityKind.Work work.Id

        Assert.True(workTab.IsSome)
        Assert.True(parentTab.IsSome)
        Assert.Equal(TabKind.Work, workTab.Value.Kind)
        Assert.Equal(work.Id, workTab.Value.RootId)
        Assert.Equal(TabKind.Flow, parentTab.Value.Kind)
        Assert.Equal(flow.Id, parentTab.Value.RootId)

    [<Fact>]
    let ``EditorNavigation returns titles and flow ids for tabs`` () =
        let store = createStore ()
        let _, system, flow, work = setupBasicHierarchy store

        let systemFlowIds = EditorNavigation.flowIdsForTab store TabKind.System system.Id
        let flowFlowIds = EditorNavigation.flowIdsForTab store TabKind.Flow flow.Id
        let workFlowIds = EditorNavigation.flowIdsForTab store TabKind.Work work.Id

        Assert.Equal<string>("TestFlow.TestWork", EditorNavigation.tabTitleOrNull store TabKind.Work work.Id)
        Assert.Equal<Guid list>([ flow.Id ], systemFlowIds)
        Assert.Equal<Guid list>([ flow.Id ], flowFlowIds)
        Assert.Equal<Guid list>([ flow.Id ], workFlowIds)

module CanvasProjectionTests =

    [<Fact>]
    let ``EditorCanvasProjection returns work nodes for flow tab`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        let work2 = addWork store "Work2" flow.Id

        let content = EditorCanvasProjection.canvasContentForTab store TabKind.Flow flow.Id
        let realNodes = content.Nodes |> List.filter (fun node -> not node.IsGhost)

        Assert.Equal(2, realNodes.Length)
        Assert.Contains(realNodes, fun node -> node.Id = work2.Id)

    [<Fact>]
    let ``EditorCanvasLayout computes auto layout for overlapping flow nodes`` () =
        let store = createStore ()
        let _, _, flow, _ = setupBasicHierarchy store
        addWork store "Work2" flow.Id |> ignore

        let requests = EditorCanvasLayout.computeAutoLayout store TabKind.Flow flow.Id

        Assert.Equal(2, requests.Length)

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
            IsReference = false
            ReferenceOfId = None
        }

    let private arrow sourceId targetId =
        {
            Id = Guid.NewGuid()
            SourceId = sourceId
            TargetId = targetId
            ArrowType = ArrowType.Start
        }

    [<Fact>]
    let ``EditorCanvasLayout keeps connected same-layer nodes closer together`` () =
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
        let positions = moves |> Seq.map (fun move -> move.Id, move.Position) |> Map.ofSeq

        let y1 = float positions.[b1.Id].Y
        let y2 = float positions.[b2.Id].Y
        let y3 = float positions.[b3.Id].Y

        let closeGap = abs (y1 - y2)
        let farGap = min (abs (y1 - y3)) (abs (y2 - y3))

        Assert.True(closeGap < farGap, $"Expected connected nodes to be closer, but closeGap={closeGap}, farGap={farGap}")

    [<Fact>]
    let ``EditorCanvasLayout places satellite and anchor at the same X with vertical separation`` () =
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
        let positions = moves |> Seq.map (fun move -> move.Id, move.Position) |> Map.ofSeq

        let satellitePos = positions.[satellite.Id]
        let anchorPos = positions.[anchor.Id]

        Assert.Equal(float anchorPos.X, float satellitePos.X, 5)
        Assert.True(abs (float satellitePos.Y - float anchorPos.Y) >= 90.0, $"Expected vertical satellite separation, but satelliteY={satellitePos.Y}, anchorY={anchorPos.Y}")

    [<Fact>]
    let ``EditorCanvasLayout keeps snake direction after wrapping to a new row`` () =
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
    let ``EditorCanvasLayout can break rows early to avoid stretching arrows needlessly`` () =
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
    let ``EditorCanvasLayout spreads cycle work nodes instead of stacking them on one vertical line`` () =
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
    let ``EditorCanvasLayout places disconnected work components outside the main flow area`` () =
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
    let ``specFromTypeIndex returns correct default spec`` () =
        let spec = PropertyPanelValueSpec.specFromTypeIndex 4
        match spec with
        | Int32Value (Single value) -> Assert.Equal(0, value)
        | _ -> Assert.Fail("Expected Int32Value")

module SelectionQueryTests =

    [<Fact>]
    let ``EditorSelectionQueries orders canvas keys by top left position`` () =
        let first = SelectionKey(Guid.NewGuid(), EntityKind.Work)
        let second = SelectionKey(Guid.NewGuid(), EntityKind.Work)

        let ordered =
            EditorSelectionQueries.orderCanvasSelectionKeys [
                CanvasSelectionCandidate(second, 300.0, 80.0, 100.0, 40.0, "Second")
                CanvasSelectionCandidate(first, 120.0, 40.0, 100.0, 40.0, "First")
            ]

        Assert.Equal<SelectionKey list>([ first; second ], ordered)
