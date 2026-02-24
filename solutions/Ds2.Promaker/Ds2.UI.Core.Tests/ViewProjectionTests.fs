module Ds2.UI.Core.Tests.ViewProjectionTests

open System
open Xunit
open Ds2.Core
open Ds2.UI.Core
open Ds2.UI.Core.Tests.TestHelpers

// =============================================================================
// 헬퍼
// =============================================================================

let private selectionKey id entityType =
    SelectionKey(id, entityType)

// =============================================================================
// buildTree
// =============================================================================

[<Fact>]
let ``buildTree returns empty list for empty store`` () =
    let store = DsStore.empty()
    let tree = TreeProjection.buildTree store
    Assert.Empty(tree)

[<Fact>]
let ``buildTree creates correct hierarchy`` () =
    let store, api, project, system, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)

    let tree = TreeProjection.buildTree store
    Assert.Single(tree) |> ignore  // 1 project

    let pNode = tree.Head
    Assert.Equal(project.Id, pNode.Id)
    Assert.Equal("Project", pNode.EntityType)
    Assert.Equal("P1", pNode.Name)
    Assert.True(pNode.ParentId.IsNone)

    let sNode = pNode.Children.Head
    Assert.Equal(system.Id, sNode.Id)
    Assert.Equal("System", sNode.EntityType)
    Assert.Equal(Some project.Id, sNode.ParentId)

    // System has Flow as first child
    let fNode = sNode.Children |> List.find (fun c -> c.EntityType = "Flow")
    Assert.Equal(flow.Id, fNode.Id)
    Assert.Equal(Some system.Id, fNode.ParentId)

    let wNode = fNode.Children.Head
    Assert.Equal(work.Id, wNode.Id)
    Assert.Equal("Work", wNode.EntityType)

    let cNode = wNode.Children.Head
    Assert.Equal(call.Id, cNode.Id)
    Assert.Equal("Call", cNode.EntityType)

[<Fact>]
let ``buildTree includes HW components under System`` () =
    let store, api, _project, system, _flow = setupProjectSystemFlow()
    api.AddButton("Btn1", system.Id) |> ignore
    api.AddLamp("Lamp1", system.Id) |> ignore

    let tree = TreeProjection.buildTree store
    let sNode = tree.Head.Children.Head
    let hwNodes = sNode.Children |> List.filter (fun c -> c.EntityType = "Button" || c.EntityType = "Lamp")
    Assert.Equal(2, hwNodes.Length)
    Assert.True(hwNodes |> List.exists (fun n -> n.Name = "Btn1" && n.EntityType = "Button"))
    Assert.True(hwNodes |> List.exists (fun n -> n.Name = "Lamp1" && n.EntityType = "Lamp"))

// =============================================================================
// canvasContentForFlow
// =============================================================================

[<Fact>]
let ``canvasContentForFlow returns works and calls`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)

    let content = CanvasProjection.canvasContentForFlow store flow.Id
    Assert.Equal(2, content.Nodes.Length)  // 1 work + 1 call

    let workNode = content.Nodes |> List.find (fun n -> n.EntityType = "Work")
    Assert.Equal(work.Id, workNode.Id)
    Assert.Equal("W1", workNode.Name)

    let callNode = content.Nodes |> List.find (fun n -> n.EntityType = "Call")
    Assert.Equal(call.Id, callNode.Id)

[<Fact>]
let ``canvasContentForFlow includes arrows`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let arrow = api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start)

    let content = CanvasProjection.canvasContentForFlow store flow.Id
    Assert.Single(content.Arrows) |> ignore
    Assert.Equal(arrow.Id, content.Arrows.Head.Id)
    Assert.Equal(w1.Id, content.Arrows.Head.SourceId)
    Assert.Equal(w2.Id, content.Arrows.Head.TargetId)

[<Fact>]
let ``canvasContentForFlow uses position when available`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    api.MoveWork(work.Id, Some(Xywh(100, 200, 150, 50)))

    let content = CanvasProjection.canvasContentForFlow store flow.Id
    let wNode = content.Nodes |> List.find (fun n -> n.Id = work.Id)
    Assert.Equal(100.0, wNode.X)
    Assert.Equal(200.0, wNode.Y)
    Assert.Equal(150.0, wNode.Width)
    Assert.Equal(50.0, wNode.Height)

[<Fact>]
let ``canvasContentForFlow returns empty for nonexistent flow`` () =
    let store = DsStore.empty()
    let content = CanvasProjection.canvasContentForFlow store (Guid.NewGuid())
    Assert.Empty(content.Nodes)
    Assert.Empty(content.Arrows)

[<Fact>]
let ``canvasContentForSystemWorks returns works from all flows only`` () =
    let store, api, _, system, flow1 = setupProjectSystemFlow()
    let flow2 = api.AddFlow("F2", system.Id)
    let w1 = api.AddWork("W1", flow1.Id)
    let w2 = api.AddWork("W2", flow2.Id)
    api.AddCall("Dev", "C1", w1.Id) |> ignore

    let content = CanvasProjection.canvasContentForSystemWorks store system.Id
    Assert.Equal(2, content.Nodes.Length)
    Assert.True(content.Nodes |> List.forall (fun n -> n.EntityType = "Work"))
    Assert.True(content.Nodes |> List.exists (fun n -> n.Id = w1.Id))
    Assert.True(content.Nodes |> List.exists (fun n -> n.Id = w2.Id))

[<Fact>]
let ``canvasContentForFlowWorks returns only works in selected flow`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let _flow2 = api.AddFlow("F2", system.Id)
    let w1 = api.AddWork("W1", flow.Id)
    api.AddCall("Dev", "C1", w1.Id) |> ignore

    let content = CanvasProjection.canvasContentForFlowWorks store flow.Id
    Assert.Single(content.Nodes) |> ignore
    Assert.Equal("Work", content.Nodes.Head.EntityType)
    Assert.Equal(w1.Id, content.Nodes.Head.Id)

[<Fact>]
let ``canvasContentForWorkCalls returns only calls in selected work`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let c1 = api.AddCall("Dev", "C1", w1.Id)
    let c2 = api.AddCall("Dev", "C2", w1.Id)
    let c3 = api.AddCall("Dev", "C3", w2.Id)
    let ownArrow = api.AddArrowBetweenCalls(flow.Id, c1.Id, c2.Id, ArrowType.Start)
    api.AddArrowBetweenCalls(flow.Id, c2.Id, c3.Id, ArrowType.Start) |> ignore

    let content = CanvasProjection.canvasContentForWorkCalls store w1.Id
    Assert.Equal(2, content.Nodes.Length)
    Assert.True(content.Nodes |> List.forall (fun n -> n.EntityType = "Call"))
    Assert.True(content.Nodes |> List.exists (fun n -> n.Id = c1.Id))
    Assert.True(content.Nodes |> List.exists (fun n -> n.Id = c2.Id))
    Assert.Single(content.Arrows) |> ignore
    Assert.Equal(ownArrow.Id, content.Arrows.Head.Id)

// =============================================================================
// flowsForSystem
// =============================================================================

[<Fact>]
let ``flowsForSystem returns flow list`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let flow2 = api.AddFlow("F2", system.Id)

    let flows = EntityHierarchyQueries.flowsForSystem store system.Id
    Assert.Equal(2, flows.Length)
    Assert.True(flows |> List.exists (fun (id, name) -> id = flow.Id && name = "F1"))
    Assert.True(flows |> List.exists (fun (id, name) -> id = flow2.Id && name = "F2"))

[<Fact>]
let ``buildTrees splits control tree and shows ApiDef category in device tree`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let control = api.AddSystem("Control", project.Id, true)
    let device  = api.AddSystem("Device",  project.Id, false)
    let flow = api.AddFlow("F1", control.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)
    let apiDef = api.AddApiDef("AD1", device.Id)
    let apiCall = ApiCall("AC1")
    apiCall.ApiDefId <- Some apiDef.Id
    api.AddApiCallToCall(call.Id, apiCall)

    let controlTree, deviceTree = TreeProjection.buildTrees store

    // control tree keeps project root and active systems only
    let controlProject = controlTree.Head
    Assert.True(controlProject.Children |> List.exists (fun n -> n.Id = control.Id))
    Assert.False(controlProject.Children |> List.exists (fun n -> n.Id = device.Id))

    // device tree: fixed root -> passive system -> ApiDef category -> ApiDef items
    let deviceRoot = deviceTree.Head
    Assert.Equal("DeviceRoot", deviceRoot.EntityType)
    Assert.Equal("Device system", deviceRoot.Name)
    Assert.True(deviceRoot.Children |> List.exists (fun n -> n.Id = device.Id))

    let deviceSystemNode = deviceRoot.Children |> List.find (fun n -> n.Id = device.Id)
    let apiDefCat = deviceSystemNode.Children |> List.find (fun n -> n.EntityType = "ApiDefCategory")
    Assert.Equal("ApiDefs", apiDefCat.Name)
    Assert.True(apiDefCat.Children |> List.exists (fun n -> n.Id = apiDef.Id && n.EntityType = "ApiDef"))

[<Fact>]
let ``buildTrees device system shows Flow and Works alongside ApiDefs category`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let device  = api.AddSystem("Dev", project.Id, false)
    let devFlow = api.AddFlow("Dev_Flow", device.Id)
    let w1      = api.AddWork("ADV", devFlow.Id)
    let w2      = api.AddWork("RET", devFlow.Id)
    let _ad1    = api.AddApiDef("ADV", device.Id)
    let _ad2    = api.AddApiDef("RET", device.Id)

    let _, deviceTree = TreeProjection.buildTrees store
    let deviceRoot = deviceTree.Head
    let deviceSystemNode = deviceRoot.Children |> List.find (fun n -> n.Id = device.Id)

    // Flow is visible under PassiveSystem
    let flowNode = deviceSystemNode.Children |> List.find (fun n -> n.EntityType = "Flow")
    Assert.Equal("Dev_Flow", flowNode.Name)
    Assert.True(flowNode.Children |> List.exists (fun n -> n.Id = w1.Id))
    Assert.True(flowNode.Children |> List.exists (fun n -> n.Id = w2.Id))

    // ApiDefs category is also present
    let apiDefCat = deviceSystemNode.Children |> List.find (fun n -> n.EntityType = "ApiDefCategory")
    Assert.Equal("ApiDefs", apiDefCat.Name)
    Assert.Equal(2, apiDefCat.Children.Length)

[<Fact>]
let ``buildTrees device system with no ApiDefs has no category node`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let _control = api.AddSystem("Control", project.Id, true)
    let device   = api.AddSystem("EmptyDevice", project.Id, false)

    let _, deviceTree = TreeProjection.buildTrees store

    let deviceRoot = deviceTree.Head
    let deviceSystemNode = deviceRoot.Children |> List.find (fun n -> n.Id = device.Id)
    Assert.Empty(deviceSystemNode.Children)

[<Fact>]
let ``findApiDefsByName returns all passive system ApiDefs when filter is empty`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let device = api.AddSystem("Dev", project.Id, false)
    let _ad1 = api.AddApiDef("ADV", device.Id)
    let _ad2 = api.AddApiDef("RET", device.Id)

    let results = EntityHierarchyQueries.findApiDefsByName store ""
    Assert.Equal(2, results.Length)

[<Fact>]
let ``findApiDefsByName filters by name`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let device = api.AddSystem("Dev", project.Id, false)
    let _ad1 = api.AddApiDef("ADV", device.Id)
    let _ad2 = api.AddApiDef("RET", device.Id)

    let results = EntityHierarchyQueries.findApiDefsByName store "AD"
    Assert.Equal(1, results.Length)
    Assert.Equal("ADV", results.[0].ApiDefName)
    Assert.Equal("Dev", results.[0].SystemName)
    Assert.Equal("Dev.ADV", results.[0].DisplayName)

// =============================================================================
// findProjectOfSystem
// =============================================================================

[<Fact>]
let ``findProjectOfSystem returns project ID`` () =
    let store, _, project, system, _ = setupProjectSystemFlow()
    let result = EntityHierarchyQueries.findProjectOfSystem store system.Id
    Assert.Equal(Some project.Id, result)

[<Fact>]
let ``findProjectOfSystem returns None for unknown system`` () =
    let store = DsStore.empty()
    let result = EntityHierarchyQueries.findProjectOfSystem store (Guid.NewGuid())
    Assert.True(result.IsNone)

// =============================================================================
// findHwParent
// =============================================================================

[<Fact>]
let ``findHwParent returns parent system ID`` () =
    let store, api, _, system, _ = setupProjectSystemFlow()
    let btn = api.AddButton("Btn1", system.Id)
    let result = EntityHierarchyQueries.findHwParent store btn.Id
    Assert.Equal(Some system.Id, result)

[<Fact>]
let ``findHwParent returns None for unknown ID`` () =
    let store = DsStore.empty()
    let result = EntityHierarchyQueries.findHwParent store (Guid.NewGuid())
    Assert.True(result.IsNone)

// =============================================================================
// isCallInFlow
// =============================================================================

[<Fact>]
let ``isCallInFlow returns true when call is in flow`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)
    Assert.True(EntityHierarchyQueries.isCallInFlow store call.Id flow.Id)

[<Fact>]
let ``isCallInFlow returns false for different flow`` () =
    let store, api, _, system, flow = setupProjectSystemFlow() // all used
    let flow2 = api.AddFlow("F2", system.Id)
    let work = api.AddWork("W1", flow.Id)
    let call = api.AddCall("Dev", "C1", work.Id)
    Assert.False(EntityHierarchyQueries.isCallInFlow store call.Id flow2.Id)

// =============================================================================
// resolveSystemForEntity
// =============================================================================

[<Fact>]
let ``resolveSystemForEntity resolves System type`` () =
    let store, _, _, system, flow = setupProjectSystemFlow()
    let result = EntityHierarchyQueries.resolveSystemForEntity store "System" system.Id
    Assert.True(result.IsSome)
    let (sysId, flowId) = result.Value
    Assert.Equal(system.Id, sysId)
    Assert.Equal(flow.Id, flowId)

[<Fact>]
let ``resolveSystemForEntity resolves Flow type`` () =
    let store, _, _, system, flow = setupProjectSystemFlow()
    let result = EntityHierarchyQueries.resolveSystemForEntity store "Flow" flow.Id
    Assert.True(result.IsSome)
    let (sysId, flowId) = result.Value
    Assert.Equal(system.Id, sysId)
    Assert.Equal(flow.Id, flowId)

[<Fact>]
let ``resolveSystemForEntity resolves Work type`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let result = EntityHierarchyQueries.resolveSystemForEntity store "Work" work.Id
    Assert.True(result.IsSome)
    let (sysId, flowId) = result.Value
    Assert.Equal(system.Id, sysId)
    Assert.Equal(flow.Id, flowId)

[<Fact>]
let ``resolveSystemForEntity returns None for System without flows`` () =
    let store, api = createApi()
    let project = api.AddProject("P1")
    let system = api.AddSystem("S1", project.Id, true)
    let result = EntityHierarchyQueries.resolveSystemForEntity store "System" system.Id
    Assert.True(result.IsNone)

[<Fact>]
let ``resolveSystemForEntity returns None for unknown type`` () =
    let store = DsStore.empty()
    let result = EntityHierarchyQueries.resolveSystemForEntity store "Project" (Guid.NewGuid())
    Assert.True(result.IsNone)

// =============================================================================
// EditorApi.RemoveEntity + RemoveArrow
// =============================================================================

[<Fact>]
let ``RemoveEntity removes work by entity type string`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    Assert.True(store.Works.ContainsKey(work.Id))

    api.RemoveEntity("Work", work.Id)
    Assert.False(store.Works.ContainsKey(work.Id))

[<Fact>]
let ``RemoveArrow removes arrow between works`` () =
    let store, api, _, _, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let arrow = api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start)
    Assert.True(store.ArrowWorks.ContainsKey(arrow.Id))

    api.RemoveArrow(arrow.Id)
    Assert.False(store.ArrowWorks.ContainsKey(arrow.Id))

// =============================================================================
// tab helpers
// =============================================================================

[<Fact>]
let ``tryOpenTabForEntity returns tab info`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    let sysTab = EntityHierarchyQueries.tryOpenTabForEntity store "System" system.Id
    Assert.True(sysTab.IsSome)
    Assert.Equal(TabKind.System, sysTab.Value.Kind)
    Assert.Equal(system.Id, sysTab.Value.RootId)
    Assert.Equal("S1", sysTab.Value.Title)

    let flowTab = EntityHierarchyQueries.tryOpenTabForEntity store "Flow" flow.Id
    Assert.True(flowTab.IsSome)
    Assert.Equal(TabKind.Flow, flowTab.Value.Kind)
    Assert.Equal(flow.Id, flowTab.Value.RootId)
    Assert.Equal("F1", flowTab.Value.Title)

    let workTab = EntityHierarchyQueries.tryOpenTabForEntity store "Work" work.Id
    Assert.True(workTab.IsSome)
    Assert.Equal(TabKind.Work, workTab.Value.Kind)
    Assert.Equal(work.Id, workTab.Value.RootId)
    Assert.Equal("W1", workTab.Value.Title)

[<Fact>]
let ``canvasContentForTab dispatches by tab kind`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let _call = api.AddCall("Dev", "C1", work.Id)

    let systemContent = CanvasProjection.canvasContentForTab store TabKind.System system.Id
    Assert.Single(systemContent.Nodes) |> ignore
    Assert.Equal("Work", systemContent.Nodes.Head.EntityType)

    let flowContent = CanvasProjection.canvasContentForTab store TabKind.Flow flow.Id
    Assert.Single(flowContent.Nodes) |> ignore
    Assert.Equal("Work", flowContent.Nodes.Head.EntityType)

    let workContent = CanvasProjection.canvasContentForTab store TabKind.Work work.Id
    Assert.Single(workContent.Nodes) |> ignore
    Assert.Equal("Call", workContent.Nodes.Head.EntityType)

[<Fact>]
let ``tabExists tabTitle and flowIdsForTab work for all tab kinds`` () =
    let store, api, _, system, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)

    Assert.True(EntityHierarchyQueries.tabExists store TabKind.System system.Id)
    Assert.True(EntityHierarchyQueries.tabExists store TabKind.Flow flow.Id)
    Assert.True(EntityHierarchyQueries.tabExists store TabKind.Work work.Id)
    Assert.False(EntityHierarchyQueries.tabExists store TabKind.Flow (Guid.NewGuid()))

    Assert.Equal(Some "S1", EntityHierarchyQueries.tabTitle store TabKind.System system.Id)
    Assert.Equal(Some "F1", EntityHierarchyQueries.tabTitle store TabKind.Flow flow.Id)
    Assert.Equal(Some "W1", EntityHierarchyQueries.tabTitle store TabKind.Work work.Id)
    Assert.True((EntityHierarchyQueries.tabTitle store TabKind.Flow (Guid.NewGuid())).IsNone)

    let systemFlowIds = EntityHierarchyQueries.flowIdsForTab store TabKind.System system.Id
    Assert.Single(systemFlowIds) |> ignore
    Assert.Equal(flow.Id, systemFlowIds.Head)

    let flowFlowIds = EntityHierarchyQueries.flowIdsForTab store TabKind.Flow flow.Id
    Assert.Single(flowFlowIds) |> ignore
    Assert.Equal(flow.Id, flowFlowIds.Head)

    let workFlowIds = EntityHierarchyQueries.flowIdsForTab store TabKind.Work work.Id
    Assert.Single(workFlowIds) |> ignore
    Assert.Equal(flow.Id, workFlowIds.Head)

[<Fact>]
let ``tryResolveAddFlowTarget resolves from selected project with single system`` () =
    let store, _api, project, _system, _flow = setupProjectSystemFlow()
    let result =
        AddTargetQueries.tryResolveAddFlowTarget
            store
            (Some "Project")
            (Some project.Id)
            None
            None
    Assert.True(result.IsSome)

[<Fact>]
let ``tryResolveAddFlowTarget resolves from active work tab`` () =
    let store, api, _project, system, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let result =
        AddTargetQueries.tryResolveAddFlowTarget
            store
            None
            None
            (Some TabKind.Work)
            (Some work.Id)
    Assert.Equal(Some system.Id, result)

[<Fact>]
let ``tryResolveAddSystemTarget resolves single project without selection`` () =
    let store, _api, project, _system, _flow = setupProjectSystemFlow()
    let result =
        AddTargetQueries.tryResolveAddSystemTarget
            store
            None
            None
            None
            None
    Assert.Equal(Some project.Id, result)

[<Fact>]
let ``tryResolveAddSystemTarget resolves from selected flow`` () =
    let store, _api, project, _system, flow = setupProjectSystemFlow()
    let result =
        AddTargetQueries.tryResolveAddSystemTarget
            store
            (Some "Flow")
            (Some flow.Id)
            None
            None
    Assert.Equal(Some project.Id, result)

[<Fact>]
let ``orderedArrowLinksForSelection returns sequential work links`` () =
    let store, api, _project, _system, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let w3 = api.AddWork("W3", flow.Id)

    let links =
        ConnectionQueries.orderedArrowLinksForSelection store [ w1.Id; w2.Id; w3.Id ]

    Assert.Equal(2, links.Length)
    let (t1, f1, s1, t1Target) = links.[0]
    Assert.Equal("Work", t1)
    Assert.Equal(flow.Id, f1)
    Assert.Equal(w1.Id, s1)
    Assert.Equal(w2.Id, t1Target)

[<Fact>]
let ``orderedArrowLinksForSelection skips already existing arrows`` () =
    let store, api, _project, _system, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)
    let w3 = api.AddWork("W3", flow.Id)
    api.AddArrowBetweenWorks(flow.Id, w1.Id, w2.Id, ArrowType.Start) |> ignore

    let links =
        ConnectionQueries.orderedArrowLinksForSelection store [ w1.Id; w2.Id; w3.Id ]

    Assert.Single(links) |> ignore
    let (_, _, src, tgt) = links.Head
    Assert.Equal(w2.Id, src)
    Assert.Equal(w3.Id, tgt)

[<Fact>]
let ``resolveFlowIdForConnect resolves work siblings`` () =
    let store, api, _project, _system, flow = setupProjectSystemFlow()
    let w1 = api.AddWork("W1", flow.Id)
    let w2 = api.AddWork("W2", flow.Id)

    let resolved =
        ConnectionQueries.resolveFlowIdForConnect
            store
            "Work"
            (Some w1.ParentId)
            "Work"
            (Some w2.ParentId)

    Assert.Equal(Some flow.Id, resolved)

[<Fact>]
let ``resolveFlowIdForConnect resolves call siblings by work parent`` () =
    let store, api, _project, _system, flow = setupProjectSystemFlow()
    let work = api.AddWork("W1", flow.Id)
    let call1 = api.AddCall("Dev", "C1", work.Id)
    let call2 = api.AddCall("Dev", "C2", work.Id)

    let resolved =
        ConnectionQueries.resolveFlowIdForConnect
            store
            "Call"
            (Some call1.ParentId)
            "Call"
            (Some call2.ParentId)

    Assert.Equal(Some flow.Id, resolved)

[<Fact>]
let ``applyNodeSelection applies shift range and updates anchor`` () =
    let k1 = selectionKey (Guid.NewGuid()) "Work"
    let k2 = selectionKey (Guid.NewGuid()) "Work"
    let k3 = selectionKey (Guid.NewGuid()) "Work"
    let ordered = [ k1; k2; k3 ]

    let result =
        SelectionQueries.applyNodeSelection
            [ k1 ]
            (Some k1)
            (Some k3)
            false
            true
            ordered

    Assert.Equal<Guid list>([ k1.Id; k2.Id; k3.Id ], result.OrderedKeys |> Seq.map (fun k -> k.Id) |> Seq.toList)
    Assert.True(result.Anchor.IsSome)
    Assert.Equal(k3.Id, result.Anchor.Value.Id)

[<Fact>]
let ``applyNodeSelection ctrl toggles node`` () =
    let k1 = selectionKey (Guid.NewGuid()) "Work"
    let k2 = selectionKey (Guid.NewGuid()) "Work"
    let ordered = [ k1; k2 ]

    let result =
        SelectionQueries.applyNodeSelection
            [ k1; k2 ]
            (Some k2)
            (Some k2)
            true
            false
            ordered

    Assert.Equal<Guid list>([ k1.Id ], result.OrderedKeys |> Seq.map (fun k -> k.Id) |> Seq.toList)
    Assert.True(result.Anchor.IsSome)
    Assert.Equal(k2.Id, result.Anchor.Value.Id)

[<Fact>]
let ``orderCanvasSelectionKeysForBox sorts by drag-progress from start point`` () =
    let k1 = selectionKey (Guid.NewGuid()) "Work"
    let k2 = selectionKey (Guid.NewGuid()) "Work"
    let k3 = selectionKey (Guid.NewGuid()) "Work"

    let n1 = CanvasSelectionCandidate(k1, 0.0, 0.0, 100.0, 40.0, "A")
    let n2 = CanvasSelectionCandidate(k2, 200.0, 0.0, 100.0, 40.0, "B")
    let n3 = CanvasSelectionCandidate(k3, 400.0, 0.0, 100.0, 40.0, "C")

    let ordered =
        SelectionQueries.orderCanvasSelectionKeysForBox
            0.0
            0.0
            500.0
            0.0
            [ n3; n1; n2 ]
        |> List.map (fun key -> key.Id)

    Assert.Equal<Guid list>([ k1.Id; k2.Id; k3.Id ], ordered)
